using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Domain.Security;
using ConfigServer.Domain.Security.Identity;

namespace ConfigServer.Application.Api.Demo
{
    /// <summary>
    /// <see cref="IIdentityAdmin"/> of the sandbox: a simulated identity provider kept in memory, one copy per visitor workspace
    /// (seeded with a few users, groups and grants, lost on restart). It makes the Access and OIDC API keys screens fully usable
    /// without touching a real identity provider. Nothing here changes what a token grants: the visitor's own access still comes
    /// from their token roles, and machine credential secrets are random strings that authenticate nothing.
    /// </summary>
    public sealed class DemoIdentityAdmin : IIdentityAdmin
    {
        public const string ProviderName = "demo";

        public const string Note =
            "Sandbox: users, groups and credentials are simulated and kept in memory for your workspace only. " +
            "Grants made here do not change your own access, and the secrets of OIDC API keys do not authenticate.";

        private static readonly IReadOnlyList<(IdentityUser User, string[] Groups)> SeedUsers = new[]
        {
            (new IdentityUser("alice", "alice", "Alice Johnson", "alice@demo.test"), new[] { "billing-team", "platform-admins" }),
            (new IdentityUser("bob", "bob", "Bob Martin", "bob@demo.test"), new[] { "billing-team", "readers" }),
            (new IdentityUser("carol", "carol", "Carol Nguyen", "carol@demo.test"), new[] { "readers" }),
            (new IdentityUser("dave", "dave", "Dave Okafor", "dave@demo.test"), new[] { "platform-admins" }),
            (new IdentityUser("erin", "erin", "Erin Costa", "erin@demo.test"), Array.Empty<string>())
        };

        private static readonly IReadOnlyList<IdentityGroup> SeedGroups = new[]
        {
            new IdentityGroup("billing-team", "billing-team", "/billing-team"),
            new IdentityGroup("platform-admins", "platform-admins", "/platform-admins"),
            new IdentityGroup("readers", "readers", "/readers")
        };

        private sealed record Machine(string Id, string ClientId, string Name, IReadOnlyList<ConfigGrant> Grants, DateTimeOffset CreatedAt);

        private sealed class State
        {
            public readonly object Gate = new object();
            public readonly Dictionary<(SubjectType, string), List<ConfigGrant>> Grants = new Dictionary<(SubjectType, string), List<ConfigGrant>>();
            public readonly List<Machine> Machines = new List<Machine>();
        }

        private readonly DemoWorkspaceAccessor _accessor;
        private readonly DemoOptions _options;
        private readonly string _configClientId;
        private readonly string _tokenEndpoint;
        private readonly TimeProvider _time;
        private readonly ConcurrentDictionary<string, State> _states = new ConcurrentDictionary<string, State>(StringComparer.Ordinal);

        public DemoIdentityAdmin(DemoWorkspaceAccessor accessor, DemoWorkspaces workspaces, DemoOptions options, TimeProvider time,
            string configClientId, string tokenEndpoint)
        {
            _accessor = accessor;
            _options = options;
            _time = time;
            _configClientId = configClientId;
            _tokenEndpoint = tokenEndpoint;
            workspaces.Removed += id => _states.TryRemove(id, out _);
        }

        public Task<IdentityAdminInfo> GetInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IdentityAdminInfo(ProviderName, true, _configClientId, Note, _tokenEndpoint));

        public Task<IReadOnlyList<IdentityUser>> SearchUsersAsync(string search, int first, int max, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IdentityUser>>(SeedUsers.Select(u => u.User)
                .Where(u => Matches(search, u.Username, u.DisplayName, u.Email)).Skip(first).Take(max).ToList());

        public Task<IReadOnlyList<IdentityGroup>> SearchGroupsAsync(string search, int first, int max, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IdentityGroup>>(SeedGroups
                .Where(g => Matches(search, g.Name)).Skip(first).Take(max).ToList());

        public Task<IReadOnlyList<SubjectGrant>> GetGrantsAsync(SubjectType type, string subjectId, CancellationToken cancellationToken = default)
        {
            RequireSubject(type, subjectId);
            var state = Current();
            lock (state.Gate)
            {
                var grants = state.Grants[(type, subjectId)].Select(g => SubjectGrant.From(g)).ToList();
                if (type == SubjectType.User)
                    foreach (var group in GroupsOf(subjectId))
                        grants.AddRange(state.Grants[(SubjectType.Group, group.Id)].Select(g => SubjectGrant.From(g, inherited: true, via: group.Name)));
                return Task.FromResult(SubjectGrant.Collect(grants));
            }
        }

        public Task<SubjectGrant> GrantAsync(SubjectType type, string subjectId, ConfigGrant grant, CancellationToken cancellationToken = default)
        {
            RequireSubject(type, subjectId);
            var state = Current();
            lock (state.Gate)
            {
                var held = state.Grants[(type, subjectId)];
                if (!held.Contains(grant)) held.Add(grant);
                return Task.FromResult(SubjectGrant.From(grant));
            }
        }

        public Task<bool> RevokeAsync(SubjectType type, string subjectId, ConfigGrant grant, CancellationToken cancellationToken = default)
        {
            RequireSubject(type, subjectId);
            var state = Current();
            lock (state.Gate) return Task.FromResult(state.Grants[(type, subjectId)].Remove(grant));
        }

        public Task<IReadOnlyList<MachineClient>> ListMachineClientsAsync(CancellationToken cancellationToken = default)
        {
            var state = Current();
            lock (state.Gate)
                return Task.FromResult<IReadOnlyList<MachineClient>>(state.Machines.OrderBy(m => m.CreatedAt).Select(ToClient).ToList());
        }

        public Task<MachineClientSecret> CreateMachineClientAsync(string name, IReadOnlyList<ConfigGrant> grants, CancellationToken cancellationToken = default)
        {
            if (!MachineClients.IsValidName(name))
                throw new IdentityAdminException(IdentityAdminErrorKind.Invalid, $"'name' is required (at most {MachineClients.MaxNameLength} characters, no control characters).");
            if (grants == null || grants.Count == 0 || grants.Count > MachineClients.MaxGrants)
                throw new IdentityAdminException(IdentityAdminErrorKind.Invalid, $"Between 1 and {MachineClients.MaxGrants} grants are required.");
            if (grants.Any(g => g.Permission == AccessPermission.Admin))
                throw new IdentityAdminException(IdentityAdminErrorKind.Invalid, "Machine credentials cannot hold config:admin (use read or write).");

            var state = Current();
            lock (state.Gate)
            {
                if (state.Machines.Count >= _options.MaxApiKeysPerWorkspace)
                    throw new DemoLimitException($"Sandbox limit: a workspace can hold at most {_options.MaxApiKeysPerWorkspace} OIDC API keys. Delete some first.");
                var machine = new Machine(Guid.NewGuid().ToString("N"), ClientIdFor(name), name.Trim(), grants.Distinct().ToList(), _time.GetUtcNow());
                state.Machines.Add(machine);
                return Task.FromResult(new MachineClientSecret(ToClient(machine), NewSecret()));
            }
        }

        public Task<MachineClientSecret> RotateMachineClientSecretAsync(string id, CancellationToken cancellationToken = default)
        {
            var state = Current();
            lock (state.Gate)
            {
                var machine = state.Machines.FirstOrDefault(m => m.Id == id)
                    ?? throw new IdentityAdminException(IdentityAdminErrorKind.NotFound, $"OIDC API key '{id}' was not found.");
                return Task.FromResult(new MachineClientSecret(ToClient(machine), NewSecret()));
            }
        }

        public Task<bool> DeleteMachineClientAsync(string id, CancellationToken cancellationToken = default)
        {
            var state = Current();
            lock (state.Gate) return Task.FromResult(state.Machines.RemoveAll(m => m.Id == id) > 0);
        }

        // ---- helpers ----

        private State Current() => _states.GetOrAdd(_accessor.Current.Id, _ => Seed());

        private static State Seed()
        {
            var state = new State();
            foreach (var (user, _) in SeedUsers) state.Grants[(SubjectType.User, user.Id)] = new List<ConfigGrant>();
            foreach (var group in SeedGroups) state.Grants[(SubjectType.Group, group.Id)] = new List<ConfigGrant>();

            state.Grants[(SubjectType.Group, "billing-team")].Add(ConfigGrant.Create(AccessPermission.Write, "billing-service"));
            state.Grants[(SubjectType.Group, "readers")].Add(ConfigGrant.Create(AccessPermission.Read));
            state.Grants[(SubjectType.Group, "platform-admins")].Add(ConfigGrant.Create(AccessPermission.Write));
            state.Grants[(SubjectType.User, "erin")].Add(ConfigGrant.Create(AccessPermission.Read, "web-portal", "prd"));
            return state;
        }

        private static IEnumerable<IdentityGroup> GroupsOf(string userId)
        {
            var names = SeedUsers.First(u => u.User.Id == userId).Groups;
            return SeedGroups.Where(g => names.Contains(g.Id));
        }

        private static void RequireSubject(SubjectType type, string subjectId)
        {
            if (!IdentitySubjects.IsValidId(subjectId))
                throw new IdentityAdminException(IdentityAdminErrorKind.Invalid, "Invalid subject id.");
            var exists = type == SubjectType.User ? SeedUsers.Any(u => u.User.Id == subjectId) : SeedGroups.Any(g => g.Id == subjectId);
            if (!exists)
                throw new IdentityAdminException(IdentityAdminErrorKind.NotFound, $"The {type.ToName()} '{subjectId}' was not found.");
        }

        private static bool Matches(string search, params string[] values)
            => string.IsNullOrWhiteSpace(search) || values.Any(v => v != null && v.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

        private static MachineClient ToClient(Machine m)
            => new MachineClient(m.Id, m.ClientId, m.Name, SubjectGrant.Collect(m.Grants.Select(g => SubjectGrant.From(g))), true, m.CreatedAt);

        private static string ClientIdFor(string name)
        {
            var slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 24) slug = slug.Substring(0, 24).Trim('-');
            if (slug.Length == 0) slug = "key";
            return MachineClients.ClientIdPrefix + slug + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        }

        /// <summary>Random, prefixed so it is recognisable as a sandbox secret; it authenticates nothing.</summary>
        private static string NewSecret()
            => "demo_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
