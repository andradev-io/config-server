using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Domain.Security;
using ConfigServer.Domain.Security.Identity;
using Microsoft.Extensions.Logging;

namespace ConfigServer.Application.Api.IdentityAdmin
{
    /// <summary>
    /// State shared by every <see cref="KeycloakIdentityAdmin"/> instance (singleton): the service-account access token and
    /// the internal id of the config client.
    /// </summary>
    public sealed class KeycloakAdminCache
    {
        internal sealed record CachedToken(string Value, DateTimeOffset RefreshAt);

        internal readonly SemaphoreSlim TokenGate = new SemaphoreSlim(1, 1);
        internal readonly SemaphoreSlim ClientGate = new SemaphoreSlim(1, 1);
        internal volatile CachedToken Token;
        internal volatile string ConfigClientUuid;
    }

    /// <summary>
    /// <see cref="IIdentityAdmin"/> over the Keycloak Admin REST API (typed <see cref="HttpClient"/>, no Keycloak SDK).
    /// Authenticates as the confidential client IdentityAdmin:Keycloak:AdminClientId with client_credentials; the token is
    /// cached until shortly before it expires and refreshed once on a 401. Grants are client roles of
    /// IdentityAdmin:Keycloak:ConfigClientId. The client secret and tokens are never logged or put in error messages.
    /// </summary>
    public sealed class KeycloakIdentityAdmin : IIdentityAdmin
    {
        public const string ProviderName = "keycloak";
        public const string GrantNote = "Grants take effect at the subject's next token refresh or sign-in.";

        /// <summary>Parent groups walked per group when collecting inherited grants.</summary>
        private const int MaxGroupDepth = 20;

        private const int MaxErrorDetailLength = 200;

        /// <summary>Marker on the clients this server creates; anything without it is never listed or changed.</summary>
        private const string ManagedByAttribute = "managed-by";
        private const string ManagedByValue = "config-server";
        private const string CreatedAtAttribute = "config-server.created-at";
        private const int MaxMachineClients = 200;
        private const int MaxSlugLength = 30;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _http;
        private readonly KeycloakAdminOptions _options;
        private readonly KeycloakAdminCache _cache;
        private readonly TimeProvider _time;
        private readonly ILogger _logger;

        public KeycloakIdentityAdmin(HttpClient http, KeycloakAdminOptions options, KeycloakAdminCache cache, TimeProvider time = null,
            ILogger<KeycloakIdentityAdmin> logger = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _time = time ?? TimeProvider.System;
            _logger = (ILogger)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        private string BaseUrl => _options.BaseUrl.TrimEnd('/');

        private string TokenUrl => $"{BaseUrl}/realms/{Esc(_options.Realm)}/protocol/openid-connect/token";

        public Task<IdentityAdminInfo> GetInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IdentityAdminInfo(ProviderName, true, _options.ConfigClientId, GrantNote, _options.TokenEndpoint ?? TokenUrl));

        public async Task<IReadOnlyList<IdentityUser>> SearchUsersAsync(string search, int first, int max, CancellationToken cancellationToken = default)
        {
            var url = AdminUrl($"users?briefRepresentation=true&first={Math.Max(0, first)}&max={Math.Max(1, max)}{SearchParam(search)}");
            const string operation = "search users";
            using var response = await SendAdminAsync(HttpMethod.Get, url, null, operation, cancellationToken);
            await EnsureSuccessAsync(response, operation, "query-users", cancellationToken: cancellationToken);
            var users = await ReadJsonAsync<List<UserRep>>(response, operation, cancellationToken) ?? new List<UserRep>();
            return users.Where(u => !string.IsNullOrEmpty(u?.Id))
                .Select(u => new IdentityUser(u.Id, u.Username ?? string.Empty, DisplayName(u), Blank(u.Email)))
                .ToList();
        }

        public async Task<IReadOnlyList<IdentityGroup>> SearchGroupsAsync(string search, int first, int max, CancellationToken cancellationToken = default)
        {
            var url = AdminUrl($"groups?briefRepresentation=true&first={Math.Max(0, first)}&max={Math.Max(1, max)}{SearchParam(search)}");
            const string operation = "search groups";
            using var response = await SendAdminAsync(HttpMethod.Get, url, null, operation, cancellationToken);
            await EnsureSuccessAsync(response, operation, "query-groups", cancellationToken: cancellationToken);
            var roots = await ReadJsonAsync<List<GroupRep>>(response, operation, cancellationToken) ?? new List<GroupRep>();

            // A search returns the matching groups inside their (non-matching) ancestors: flatten, keep the matches.
            var term = Blank(search);
            var result = new List<IdentityGroup>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Walk(GroupRep group, string parentPath, int depth)
            {
                if (group == null || string.IsNullOrEmpty(group.Id) || depth > MaxGroupDepth) return;
                var path = Blank(group.Path) ?? (parentPath ?? string.Empty) + "/" + group.Name;
                if ((term == null || (group.Name ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase)) && seen.Add(group.Id))
                    result.Add(new IdentityGroup(group.Id, group.Name ?? string.Empty, path));
                foreach (var child in group.SubGroups ?? new List<GroupRep>()) Walk(child, path, depth + 1);
            }
            foreach (var root in roots) Walk(root, null, 0);
            return result;
        }

        public async Task<IReadOnlyList<SubjectGrant>> GetGrantsAsync(SubjectType type, string subjectId, CancellationToken cancellationToken = default)
        {
            EnsureSubject(subjectId);
            var client = await ConfigClientUuidAsync(cancellationToken);
            var grants = (await DirectRolesAsync(type, subjectId, client, cancellationToken))
                .Select(r => SubjectGrant.FromRoleName(r.Name))
                .ToList();

            if (type == SubjectType.User)
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                foreach (var group in await UserGroupsAsync(subjectId, cancellationToken))
                {
                    // Members inherit the role mappings of the group and of its parent groups.
                    var current = group;
                    for (var depth = 0; current != null && depth < MaxGroupDepth && !string.IsNullOrEmpty(current.Id) && visited.Add(current.Id); depth++)
                    {
                        var roles = await DirectRolesAsync(SubjectType.Group, current.Id, client, cancellationToken, missingIsEmpty: true);
                        var via = current.Name;
                        grants.AddRange(roles.Select(r => SubjectGrant.FromRoleName(r.Name, inherited: true, via: via)));
                        current = string.IsNullOrEmpty(current.ParentId) ? null : await GroupAsync(current.ParentId, cancellationToken);
                    }
                }
            }
            return SubjectGrant.Collect(grants);
        }

        public async Task<SubjectGrant> GrantAsync(SubjectType type, string subjectId, ConfigGrant grant, CancellationToken cancellationToken = default)
        {
            EnsureSubject(subjectId);
            if (grant == null) throw new ArgumentNullException(nameof(grant));
            var client = await ConfigClientUuidAsync(cancellationToken);

            var existing = (await DirectRolesAsync(type, subjectId, client, cancellationToken)).FirstOrDefault(r => grant.MatchesRoleName(r.Name));
            if (existing != null) return SubjectGrant.From(grant, existing.Name);

            var role = await EnsureRoleAsync(client, grant, cancellationToken);
            var operation = $"assign the role '{role.Name}' to {type.ToName()} '{subjectId}'";
            using var response = await SendAdminAsync(HttpMethod.Post, MappingsUrl(type, subjectId, client),
                new[] { new RoleRep { Id = role.Id, Name = role.Name } }, operation, cancellationToken);
            await EnsureSuccessAsync(response, operation, "manage-users", () => SubjectNotFound(type, subjectId), cancellationToken);
            return SubjectGrant.From(grant, role.Name);
        }

        public async Task<bool> RevokeAsync(SubjectType type, string subjectId, ConfigGrant grant, CancellationToken cancellationToken = default)
        {
            EnsureSubject(subjectId);
            if (grant == null) throw new ArgumentNullException(nameof(grant));
            var client = await ConfigClientUuidAsync(cancellationToken);

            var mapped = (await DirectRolesAsync(type, subjectId, client, cancellationToken))
                .Where(r => grant.MatchesRoleName(r.Name))
                .Select(r => new RoleRep { Id = r.Id, Name = r.Name })
                .ToList();
            if (mapped.Count == 0) return false;

            var operation = $"remove the role '{grant.RoleName}' from {type.ToName()} '{subjectId}'";
            using var response = await SendAdminAsync(HttpMethod.Delete, MappingsUrl(type, subjectId, client), mapped, operation, cancellationToken);
            await EnsureSuccessAsync(response, operation, "manage-users", () => SubjectNotFound(type, subjectId), cancellationToken);
            return true;
        }

        // ---- machine credentials ("OIDC API keys") ----------------------------------------------------------------

        public async Task<IReadOnlyList<MachineClient>> ListMachineClientsAsync(CancellationToken cancellationToken = default)
        {
            const string operation = "list the machine credentials";
            var configClient = await ConfigClientUuidAsync(cancellationToken);
            var url = AdminUrl($"clients?clientId={Esc(MachineClients.ClientIdPrefix)}&search=true&first=0&max={MaxMachineClients}");
            using var response = await SendAdminAsync(HttpMethod.Get, url, null, operation, cancellationToken);
            await EnsureSuccessAsync(response, operation, "view-clients", cancellationToken: cancellationToken);
            var clients = await ReadJsonAsync<List<ClientRep>>(response, operation, cancellationToken) ?? new List<ClientRep>();

            var result = new List<MachineClient>();
            foreach (var client in clients.Where(IsManaged))
                result.Add(await ToMachineClientAsync(client, configClient, cancellationToken));
            return result
                .OrderByDescending(c => c.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<MachineClientSecret> CreateMachineClientAsync(string name, IReadOnlyList<ConfigGrant> grants, CancellationToken cancellationToken = default)
        {
            if (!MachineClients.IsValidName(name))
                throw new IdentityAdminException(IdentityAdminErrorKind.Invalid, $"'name' is required (at most {MachineClients.MaxNameLength} characters, no control characters).");
            var wanted = (grants ?? Array.Empty<ConfigGrant>()).Where(g => g != null).Distinct().ToList();
            if (wanted.Count > MachineClients.MaxGrants)
                throw new IdentityAdminException(IdentityAdminErrorKind.Invalid, $"At most {MachineClients.MaxGrants} grants per credential.");
            if (wanted.Any(g => g.Permission == AccessPermission.Admin))
                throw new IdentityAdminException(IdentityAdminErrorKind.Invalid, "Machine credentials cannot hold config:admin.");

            name = name.Trim();
            var configClient = await ConfigClientUuidAsync(cancellationToken);
            var createdAt = _time.GetUtcNow();

            string id = null;
            string clientId = null;
            for (var attempt = 0; attempt < 3 && id == null; attempt++)
            {
                clientId = NewClientId(name);
                id = await CreateClientAsync(clientId, name, createdAt, cancellationToken);
            }
            if (id == null)
                throw new IdentityAdminException(IdentityAdminErrorKind.Unavailable, "Keycloak refused every generated client id as already taken; try again.");

            try
            {
                var user = await ServiceAccountUserAsync(id, cancellationToken)
                           ?? throw new IdentityAdminException(IdentityAdminErrorKind.Unavailable, "Keycloak created the client without a service account user.");
                var roles = new List<RoleRep>();
                foreach (var grant in wanted) roles.Add(await EnsureRoleAsync(configClient, grant, cancellationToken));
                if (roles.Count > 0)
                {
                    var operation = $"assign the grants to the service account of '{clientId}'";
                    using var response = await SendAdminAsync(HttpMethod.Post, MappingsUrl(SubjectType.User, user.Id, configClient),
                        roles.Select(r => new RoleRep { Id = r.Id, Name = r.Name }).ToArray(), operation, cancellationToken);
                    await EnsureSuccessAsync(response, operation, "manage-users", cancellationToken: cancellationToken);
                }
                var secret = await ReadSecretAsync(id, cancellationToken);
                var client = new MachineClient(id, clientId, name,
                    SubjectGrant.Collect(wanted.Select(g => SubjectGrant.From(g))), true, createdAt);
                _logger.LogInformation("Created machine credential {ClientId} with {Grants} grant(s).", clientId, wanted.Count);
                return new MachineClientSecret(client, secret);
            }
            catch (Exception)
            {
                // Do not leave a half-configured credential behind (no secret was handed out yet).
                try
                {
                    await DeleteClientAsync(id, CancellationToken.None);
                }
                catch (Exception cleanup)
                {
                    _logger.LogWarning("Could not remove the incomplete machine credential {ClientId}: {Error}", clientId, cleanup.Message);
                }
                throw;
            }
        }

        public async Task<MachineClientSecret> RotateMachineClientSecretAsync(string id, CancellationToken cancellationToken = default)
        {
            var existing = await ManagedClientAsync(id, cancellationToken)
                           ?? throw MachineNotFound(id);
            var operation = $"rotate the secret of '{existing.ClientId}'";
            using var response = await SendAdminAsync(HttpMethod.Post, AdminUrl($"clients/{Esc(id)}/client-secret"), null, operation, cancellationToken);
            await EnsureSuccessAsync(response, operation, "manage-clients", () => MachineNotFound(id), cancellationToken);
            var secret = (await ReadJsonAsync<SecretRep>(response, operation, cancellationToken))?.Value;
            if (string.IsNullOrEmpty(secret))
                throw new IdentityAdminException(IdentityAdminErrorKind.Unavailable, $"Keycloak returned no secret while trying to {operation}.");

            var client = await ToMachineClientAsync(existing, await ConfigClientUuidAsync(cancellationToken), cancellationToken);
            _logger.LogInformation("Rotated the secret of machine credential {ClientId}.", existing.ClientId);
            return new MachineClientSecret(client, secret);
        }

        public async Task<bool> DeleteMachineClientAsync(string id, CancellationToken cancellationToken = default)
        {
            var existing = await ManagedClientAsync(id, cancellationToken);
            if (existing == null) return false;
            var deleted = await DeleteClientAsync(id, cancellationToken);
            if (deleted) _logger.LogInformation("Deleted machine credential {ClientId}.", existing.ClientId);
            return deleted;
        }

        /// <summary>The client when it exists and carries this server's marker; null for unknown and for foreign clients.</summary>
        private async Task<ClientRep> ManagedClientAsync(string id, CancellationToken cancellationToken)
        {
            EnsureSubject(id);
            var operation = $"read the machine credential '{id}'";
            using var response = await SendAdminAsync(HttpMethod.Get, AdminUrl($"clients/{Esc(id)}"), null, operation, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            await EnsureSuccessAsync(response, operation, "view-clients", cancellationToken: cancellationToken);
            var client = await ReadJsonAsync<ClientRep>(response, operation, cancellationToken);
            return IsManaged(client) ? client : null;
        }

        private async Task<string> CreateClientAsync(string clientId, string name, DateTimeOffset createdAt, CancellationToken cancellationToken)
        {
            var operation = $"create the machine credential '{clientId}'";
            var body = new ClientCreateRep
            {
                ClientId = clientId,
                Name = name,
                Description = "Config server machine credential (OIDC API key).",
                Attributes = new Dictionary<string, string>
                {
                    [ManagedByAttribute] = ManagedByValue,
                    [CreatedAtAttribute] = createdAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                }
            };
            using var response = await SendAdminAsync(HttpMethod.Post, AdminUrl("clients"), body, operation, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict) return null;
            await EnsureSuccessAsync(response, operation, "manage-clients", cancellationToken: cancellationToken);

            // The new client's internal id is the last segment of the Location header.
            var location = response.Headers.Location?.OriginalString.TrimEnd('/');
            var id = location == null ? null : Uri.UnescapeDataString(location.Substring(location.LastIndexOf('/') + 1));
            if (!string.IsNullOrEmpty(id)) return id;

            using var lookup = await SendAdminAsync(HttpMethod.Get, AdminUrl($"clients?clientId={Esc(clientId)}"), null, operation, cancellationToken);
            await EnsureSuccessAsync(lookup, operation, "view-clients", cancellationToken: cancellationToken);
            var found = (await ReadJsonAsync<List<ClientRep>>(lookup, operation, cancellationToken) ?? new List<ClientRep>())
                .FirstOrDefault(c => string.Equals(c?.ClientId, clientId, StringComparison.Ordinal));
            return string.IsNullOrEmpty(found?.Id)
                ? throw new IdentityAdminException(IdentityAdminErrorKind.Unavailable, $"Keycloak created '{clientId}' but does not return its id.")
                : found.Id;
        }

        private async Task<bool> DeleteClientAsync(string id, CancellationToken cancellationToken)
        {
            var operation = $"delete the machine credential '{id}'";
            using var response = await SendAdminAsync(HttpMethod.Delete, AdminUrl($"clients/{Esc(id)}"), null, operation, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            await EnsureSuccessAsync(response, operation, "manage-clients", cancellationToken: cancellationToken);
            return true;
        }

        private async Task<UserRep> ServiceAccountUserAsync(string id, CancellationToken cancellationToken)
        {
            var operation = $"read the service account of client '{id}'";
            using var response = await SendAdminAsync(HttpMethod.Get, AdminUrl($"clients/{Esc(id)}/service-account-user"), null, operation, cancellationToken);
            // 404 / 400: the client has no service account.
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.BadRequest) return null;
            await EnsureSuccessAsync(response, operation, "view-users", cancellationToken: cancellationToken);
            var user = await ReadJsonAsync<UserRep>(response, operation, cancellationToken);
            return string.IsNullOrEmpty(user?.Id) ? null : user;
        }

        private async Task<string> ReadSecretAsync(string id, CancellationToken cancellationToken)
        {
            var operation = $"read the secret of client '{id}'";
            using var response = await SendAdminAsync(HttpMethod.Get, AdminUrl($"clients/{Esc(id)}/client-secret"), null, operation, cancellationToken);
            await EnsureSuccessAsync(response, operation, "view-clients", cancellationToken: cancellationToken);
            var secret = (await ReadJsonAsync<SecretRep>(response, operation, cancellationToken))?.Value;
            if (!string.IsNullOrEmpty(secret)) return secret;

            // No secret generated yet: ask Keycloak for one.
            operation = $"generate the secret of client '{id}'";
            using var created = await SendAdminAsync(HttpMethod.Post, AdminUrl($"clients/{Esc(id)}/client-secret"), null, operation, cancellationToken);
            await EnsureSuccessAsync(created, operation, "manage-clients", cancellationToken: cancellationToken);
            secret = (await ReadJsonAsync<SecretRep>(created, operation, cancellationToken))?.Value;
            return string.IsNullOrEmpty(secret)
                ? throw new IdentityAdminException(IdentityAdminErrorKind.Unavailable, $"Keycloak returned no secret while trying to {operation}.")
                : secret;
        }

        private async Task<MachineClient> ToMachineClientAsync(ClientRep client, string configClient, CancellationToken cancellationToken)
        {
            var grants = new List<SubjectGrant>();
            var user = await ServiceAccountUserAsync(client.Id, cancellationToken);
            if (user != null)
                grants.AddRange((await DirectRolesAsync(SubjectType.User, user.Id, configClient, cancellationToken, missingIsEmpty: true))
                    .Select(r => SubjectGrant.FromRoleName(r.Name)));

            DateTimeOffset? createdAt = null;
            if (client.Attributes != null && client.Attributes.TryGetValue(CreatedAtAttribute, out var raw)
                && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                createdAt = parsed;

            return new MachineClient(client.Id, client.ClientId, Blank(client.Name) ?? client.ClientId, SubjectGrant.Collect(grants), client.Enabled ?? true, createdAt);
        }

        /// <summary>Only clients with our client id prefix <b>and</b> the marker attribute are ever listed or changed.</summary>
        private static bool IsManaged(ClientRep client)
            => client != null && !string.IsNullOrEmpty(client.Id)
               && client.ClientId != null && client.ClientId.StartsWith(MachineClients.ClientIdPrefix, StringComparison.Ordinal)
               && client.Attributes != null && client.Attributes.TryGetValue(ManagedByAttribute, out var value)
               && string.Equals(value, ManagedByValue, StringComparison.Ordinal);

        /// <summary>cfs-&lt;slug of the name&gt;-&lt;6 random chars&gt;.</summary>
        internal static string NewClientId(string name)
        {
            var slug = new System.Text.StringBuilder();
            foreach (var c in (name ?? string.Empty).ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) slug.Append(c);
                else if (slug.Length > 0 && slug[slug.Length - 1] != '-') slug.Append('-');
            }
            var text = slug.ToString().Trim('-');
            if (text.Length > MaxSlugLength) text = text.Substring(0, MaxSlugLength).Trim('-');
            if (text.Length == 0) text = "key";
            return $"{MachineClients.ClientIdPrefix}{text}-{System.Security.Cryptography.RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 6)}";
        }

        private static IdentityAdminException MachineNotFound(string id)
            => new IdentityAdminException(IdentityAdminErrorKind.NotFound, $"Machine credential '{id}' was not found.");

        // ---- Keycloak calls ----------------------------------------------------------------------------------------

        private async Task<List<RoleRep>> DirectRolesAsync(SubjectType type, string id, string client, CancellationToken cancellationToken, bool missingIsEmpty = false)
        {
            var operation = $"read the client role mappings of {type.ToName()} '{id}'";
            using var response = await SendAdminAsync(HttpMethod.Get, MappingsUrl(type, id, client), null, operation, cancellationToken);
            if (missingIsEmpty && response.StatusCode == HttpStatusCode.NotFound) return new List<RoleRep>();
            await EnsureSuccessAsync(response, operation, "view-users", () => SubjectNotFound(type, id), cancellationToken);
            return (await ReadJsonAsync<List<RoleRep>>(response, operation, cancellationToken) ?? new List<RoleRep>())
                .Where(r => !string.IsNullOrEmpty(r?.Name))
                .ToList();
        }

        private async Task<List<GroupRep>> UserGroupsAsync(string userId, CancellationToken cancellationToken)
        {
            var operation = $"read the groups of user '{userId}'";
            using var response = await SendAdminAsync(HttpMethod.Get, AdminUrl($"users/{Esc(userId)}/groups?briefRepresentation=true"), null, operation, cancellationToken);
            await EnsureSuccessAsync(response, operation, "view-users", () => SubjectNotFound(SubjectType.User, userId), cancellationToken);
            return await ReadJsonAsync<List<GroupRep>>(response, operation, cancellationToken) ?? new List<GroupRep>();
        }

        private async Task<GroupRep> GroupAsync(string groupId, CancellationToken cancellationToken)
        {
            var operation = $"read group '{groupId}'";
            using var response = await SendAdminAsync(HttpMethod.Get, AdminUrl($"groups/{Esc(groupId)}"), null, operation, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            await EnsureSuccessAsync(response, operation, "view-users", cancellationToken: cancellationToken);
            return await ReadJsonAsync<GroupRep>(response, operation, cancellationToken);
        }

        /// <summary>The client role for the grant, created when missing (a concurrent creation, 409, is fine).</summary>
        private async Task<RoleRep> EnsureRoleAsync(string client, ConfigGrant grant, CancellationToken cancellationToken)
        {
            var role = await FindRoleAsync(client, grant.RoleName, cancellationToken);
            if (role != null) return role;

            var operation = $"create the client role '{grant.RoleName}'";
            var body = new RoleRep
            {
                Name = grant.RoleName,
                Description = $"Config server grant: {grant.Permission.ToName()} on application '{grant.App}', environment '{grant.Environment}' (* = all)."
            };
            using (var created = await SendAdminAsync(HttpMethod.Post, AdminUrl($"clients/{Esc(client)}/roles"), body, operation, cancellationToken))
            {
                if (created.StatusCode != HttpStatusCode.Conflict)
                    await EnsureSuccessAsync(created, operation, "manage-clients", ConfigClientNotFound, cancellationToken);
            }
            _logger.LogInformation("Created Keycloak client role {Role} on client {ConfigClientId}.", grant.RoleName, _options.ConfigClientId);

            return await FindRoleAsync(client, grant.RoleName, cancellationToken)
                   ?? throw new IdentityAdminException(IdentityAdminErrorKind.Unavailable,
                       $"The client role '{grant.RoleName}' was created but Keycloak does not return it.");
        }

        private async Task<RoleRep> FindRoleAsync(string client, string roleName, CancellationToken cancellationToken)
        {
            var operation = $"read the client role '{roleName}'";
            // Role names contain ':' (and maybe '*'): escaped as one path segment.
            using var response = await SendAdminAsync(HttpMethod.Get, AdminUrl($"clients/{Esc(client)}/roles/{Esc(roleName)}"), null, operation, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            await EnsureSuccessAsync(response, operation, "view-clients", cancellationToken: cancellationToken);
            var role = await ReadJsonAsync<RoleRep>(response, operation, cancellationToken);
            return string.IsNullOrEmpty(role?.Id) ? null : role;
        }

        /// <summary>Internal id of the config client, resolved once by its clientId.</summary>
        private async Task<string> ConfigClientUuidAsync(CancellationToken cancellationToken)
        {
            var cached = _cache.ConfigClientUuid;
            if (cached != null) return cached;

            await _cache.ClientGate.WaitAsync(cancellationToken);
            try
            {
                if (_cache.ConfigClientUuid != null) return _cache.ConfigClientUuid;
                var operation = $"look up the client '{_options.ConfigClientId}'";
                using var response = await SendAdminAsync(HttpMethod.Get, AdminUrl($"clients?clientId={Esc(_options.ConfigClientId)}"), null, operation, cancellationToken);
                await EnsureSuccessAsync(response, operation, "view-clients", cancellationToken: cancellationToken);
                var clients = await ReadJsonAsync<List<ClientRep>>(response, operation, cancellationToken) ?? new List<ClientRep>();
                var match = clients.FirstOrDefault(c => string.Equals(c?.ClientId, _options.ConfigClientId, StringComparison.Ordinal));
                if (string.IsNullOrEmpty(match?.Id)) throw ConfigClientNotFound();
                _cache.ConfigClientUuid = match.Id;
                return match.Id;
            }
            finally
            {
                _cache.ClientGate.Release();
            }
        }

        // ---- HTTP plumbing -----------------------------------------------------------------------------------------

        /// <summary>Sends an admin API request with the cached token; on 401 refreshes the token once and retries.</summary>
        private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string url, object body, string operation, CancellationToken cancellationToken)
        {
            var token = await GetTokenAsync(null, cancellationToken);
            var response = await SendOnceAsync(method, url, body, token, operation, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

            response.Dispose();
            token = await GetTokenAsync(token, cancellationToken);
            return await SendOnceAsync(method, url, body, token, operation, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string url, object body, string token, string operation, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (body != null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            return await SendRawAsync(request, operation, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, string operation, CancellationToken cancellationToken)
        {
            try
            {
                return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Keycloak at {BaseUrl} timed out while trying to {Operation}.", BaseUrl, operation);
                throw new IdentityAdminException(IdentityAdminErrorKind.Unavailable,
                    $"Keycloak at {BaseUrl} did not answer in time while trying to {operation} (IdentityAdmin:Keycloak:TimeoutSeconds).", ex);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Keycloak at {BaseUrl} is unreachable while trying to {Operation}: {Error}", BaseUrl, operation, ex.Message);
                throw new IdentityAdminException(IdentityAdminErrorKind.Unavailable,
                    $"Keycloak is unreachable at {BaseUrl} while trying to {operation}: {ex.Message}", ex);
            }
        }

        /// <summary>The cached token unless it is about to expire or is <paramref name="rejected"/>; otherwise a new one.</summary>
        private async Task<string> GetTokenAsync(string rejected, CancellationToken cancellationToken)
        {
            var cached = _cache.Token;
            if (IsUsable(cached, rejected)) return cached.Value;

            await _cache.TokenGate.WaitAsync(cancellationToken);
            try
            {
                cached = _cache.Token;
                if (IsUsable(cached, rejected)) return cached.Value;
                _cache.Token = null;
                var fresh = await RequestTokenAsync(cancellationToken);
                _cache.Token = fresh;
                return fresh.Value;
            }
            finally
            {
                _cache.TokenGate.Release();
            }
        }

        private bool IsUsable(KeycloakAdminCache.CachedToken token, string rejected)
            => token != null && token.Value != rejected && _time.GetUtcNow() < token.RefreshAt;

        private async Task<KeycloakAdminCache.CachedToken> RequestTokenAsync(CancellationToken cancellationToken)
        {
            const string operation = "get an admin access token";
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.AdminClientId,
                    ["client_secret"] = _options.AdminClientSecret
                })
            };
            using var response = await SendRawAsync(request, operation, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorAsync(response, cancellationToken);
                var status = (int)response.StatusCode;
                _logger.LogWarning("Keycloak refused the admin token request for client {AdminClientId} ({Status}{Detail}).",
                    _options.AdminClientId, status, Suffix(detail));
                throw status switch
                {
                    400 or 401 => new IdentityAdminException(IdentityAdminErrorKind.Unauthorized,
                        $"Keycloak rejected the admin client credentials (IdentityAdmin:Keycloak:AdminClientId '{_options.AdminClientId}' / AdminClientSecret){Suffix(detail)}. " +
                        "Check the secret and that the client is confidential with 'Service accounts roles' enabled."),
                    404 => new IdentityAdminException(IdentityAdminErrorKind.Misconfigured,
                        $"Realm '{_options.Realm}' was not found at {BaseUrl} (IdentityAdmin:Keycloak:Realm / BaseUrl)."),
                    _ => new IdentityAdminException(IdentityAdminErrorKind.Unavailable,
                        $"The Keycloak token endpoint returned {status}{Suffix(detail)}.")
                };
            }

            var token = await ReadJsonAsync<TokenResponse>(response, operation, cancellationToken);
            if (string.IsNullOrEmpty(token?.AccessToken))
                throw new IdentityAdminException(IdentityAdminErrorKind.Unavailable, "The Keycloak token endpoint returned no access_token.");

            // Refresh shortly before expiry (30 s, or half of a short lifetime).
            var lifetime = TimeSpan.FromSeconds(Math.Max(1, token.ExpiresIn ?? 60));
            var margin = lifetime > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(30) : lifetime / 2;
            return new KeycloakAdminCache.CachedToken(token.AccessToken, _time.GetUtcNow() + lifetime - margin);
        }

        /// <summary>Maps a failed admin API response to an <see cref="IdentityAdminException"/> naming what to fix.</summary>
        private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, string requiredRole,
            Func<IdentityAdminException> notFound = null, CancellationToken cancellationToken = default)
        {
            if (response.IsSuccessStatusCode) return;

            var status = (int)response.StatusCode;
            if (status == 404)
            {
                // A 404 can also mean the cached config client id is stale (client recreated): resolve it again next time.
                _cache.ConfigClientUuid = null;
                if (notFound != null) throw notFound();
            }

            var detail = await ReadErrorAsync(response, cancellationToken);
            _logger.LogWarning("Keycloak admin API returned {Status} while trying to {Operation}{Detail}.", status, operation, Suffix(detail));
            throw status switch
            {
                401 => new IdentityAdminException(IdentityAdminErrorKind.Unauthorized,
                    $"Keycloak rejected the admin service account token while trying to {operation}. " +
                    $"Check that IdentityAdmin:Keycloak:AdminClientId '{_options.AdminClientId}' belongs to realm '{_options.Realm}'."),
                403 => new IdentityAdminException(IdentityAdminErrorKind.Denied,
                    $"The admin service account lacks permission to {operation}: grant the service account of client " +
                    $"'{_options.AdminClientId}' the realm-management role '{requiredRole}'."),
                _ => new IdentityAdminException(IdentityAdminErrorKind.Unavailable, $"Keycloak returned {status} while trying to {operation}{Suffix(detail)}.")
            };
        }

        private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
        {
            try
            {
                return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
            {
                throw new IdentityAdminException(IdentityAdminErrorKind.Unavailable, $"Keycloak returned an unexpected response while trying to {operation}.", ex);
            }
        }

        /// <summary>Keycloak's error text ("errorMessage", or "error" + "error_description"), shortened; null when absent.</summary>
        private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(text)) return null;
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                string Prop(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var message = Prop("errorMessage") ?? string.Join(": ", new[] { Prop("error"), Prop("error_description") }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (string.IsNullOrWhiteSpace(message)) return null;
                return message.Length > MaxErrorDetailLength ? message.Substring(0, MaxErrorDetailLength) + "..." : message;
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        // ---- helpers -----------------------------------------------------------------------------------------------

        private string AdminUrl(string path) => $"{BaseUrl}/admin/realms/{Esc(_options.Realm)}/{path}";

        private string MappingsUrl(SubjectType type, string id, string client)
            => AdminUrl($"{(type == SubjectType.Group ? "groups" : "users")}/{Esc(id)}/role-mappings/clients/{Esc(client)}");

        private static string Esc(string value) => Uri.EscapeDataString(value ?? string.Empty);

        private static string SearchParam(string search) => string.IsNullOrWhiteSpace(search) ? string.Empty : "&search=" + Esc(search.Trim());

        private static string Suffix(string detail) => string.IsNullOrEmpty(detail) ? string.Empty : $" ({detail})";

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string DisplayName(UserRep user)
            => Blank(string.Join(" ", new[] { user.FirstName, user.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())));

        private static void EnsureSubject(string subjectId)
        {
            if (!IdentitySubjects.IsValidId(subjectId))
                throw new IdentityAdminException(IdentityAdminErrorKind.Invalid, "Invalid subject id.");
        }

        private static IdentityAdminException SubjectNotFound(SubjectType type, string id)
            => new IdentityAdminException(IdentityAdminErrorKind.NotFound, $"{(type == SubjectType.Group ? "Group" : "User")} '{id}' was not found.");

        private IdentityAdminException ConfigClientNotFound()
            => new IdentityAdminException(IdentityAdminErrorKind.Misconfigured,
                $"Client '{_options.ConfigClientId}' was not found in realm '{_options.Realm}' (IdentityAdmin:Keycloak:ConfigClientId, defaults to Auth:Oidc:ClientId).");

        // ---- Keycloak representations (only the fields used) ------------------------------------------------------

        private sealed class TokenResponse
        {
            [JsonPropertyName("access_token")] public string AccessToken { get; set; }
            [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
        }

        private sealed class ClientRep
        {
            public string Id { get; set; }
            public string ClientId { get; set; }
            public string Name { get; set; }
            public bool? Enabled { get; set; }
            public Dictionary<string, string> Attributes { get; set; }
        }

        /// <summary>A confidential, service-account-only client: no browser or password flows.</summary>
        private sealed class ClientCreateRep
        {
            public string ClientId { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public bool Enabled { get; set; } = true;
            public string Protocol { get; set; } = "openid-connect";
            public bool PublicClient { get; set; }
            public string ClientAuthenticatorType { get; set; } = "client-secret";
            public bool ServiceAccountsEnabled { get; set; } = true;
            public bool StandardFlowEnabled { get; set; }
            public bool ImplicitFlowEnabled { get; set; }
            public bool DirectAccessGrantsEnabled { get; set; }
            public Dictionary<string, string> Attributes { get; set; }
        }

        private sealed class SecretRep
        {
            public string Value { get; set; }
        }

        private sealed class RoleRep
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
        }

        private sealed class UserRep
        {
            public string Id { get; set; }
            public string Username { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string Email { get; set; }
        }

        private sealed class GroupRep
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public string ParentId { get; set; }
            public List<GroupRep> SubGroups { get; set; }
        }
    }
}
