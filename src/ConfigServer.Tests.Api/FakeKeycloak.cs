using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ConfigServer.Tests.Api
{
    internal sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// In-memory stand-in for the parts of the Keycloak token endpoint and Admin REST API used by KeycloakIdentityAdmin.
    /// Records every request; <see cref="Intercept"/> can answer a request instead (error injection).
    /// </summary>
    internal sealed class FakeKeycloak : HttpMessageHandler
    {
        public const string BaseUrl = "http://kc.test";
        public const string Realm = "acme";
        public const string AdminClientId = "config-manager-admin";
        public const string AdminSecret = "super-secret-value";
        public const string ConfigClientId = "config-web";
        public const string ClientUuid = "11111111-aaaa-bbbb-cccc-000000000001";

        public sealed record Recorded(HttpMethod Method, Uri Uri, string Authorization, string Body);

        public sealed record Group(string Id, string Name, string Path, string ParentId);

        public sealed record User(string Id, string Username, string FirstName, string LastName, string Email);

        /// <summary>A client created through POST clients (or added with <see cref="AddForeignClient"/>).</summary>
        public sealed class Client
        {
            public string Id { get; set; }
            public string ClientId { get; set; }
            public string Name { get; set; }
            public string ServiceAccountUserId { get; set; }
            public string Secret { get; set; }
            public int SecretVersion { get; set; } = 1;
            public Dictionary<string, string> Attributes { get; } = new Dictionary<string, string>();
        }

        /// <summary>Clients other than the config client, by internal id.</summary>
        public Dictionary<string, Client> Clients { get; } = new Dictionary<string, Client>();

        public int DeletedClients { get; private set; }

        /// <summary>False: POST clients answers 201 without a Location header.</summary>
        public bool SendLocationHeader { get; set; } = true;

        /// <summary>A client this server did not create (no marker attribute, other client id).</summary>
        public Client AddForeignClient(string clientId, bool withPrefix = false, bool withMarker = false)
        {
            var number = 900 + Clients.Count;
            var client = new Client { Id = "client-" + number, ClientId = withPrefix ? "cfs-" + clientId : clientId, Name = clientId, ServiceAccountUserId = "sa-" + number, Secret = "x" };
            if (withMarker) client.Attributes["managed-by"] = "config-server";
            Clients[client.Id] = client;
            AddUser(client.ServiceAccountUserId, "service-account-" + clientId);
            return client;
        }

        public List<Recorded> Requests { get; } = new List<Recorded>();
        public int ExpiresIn { get; set; } = 300;
        public int TokensIssued { get; private set; }
        public HashSet<string> ValidTokens { get; } = new HashSet<string>();

        /// <summary>Client roles of the config client: name => id.</summary>
        public Dictionary<string, string> Roles { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public Dictionary<string, List<string>> UserMappings { get; } = new Dictionary<string, List<string>>();
        public Dictionary<string, List<string>> GroupMappings { get; } = new Dictionary<string, List<string>>();
        public Dictionary<string, List<string>> UserGroups { get; } = new Dictionary<string, List<string>>();
        public Dictionary<string, Group> Groups { get; } = new Dictionary<string, Group>();
        public List<User> Users { get; } = new List<User>();
        public bool ConfigClientExists { get; set; } = true;

        /// <summary>Returns a response to short-circuit a request (null = normal handling).</summary>
        public Func<HttpRequestMessage, HttpResponseMessage> Intercept { get; set; }

        public string AddRole(string name)
        {
            var id = "role-" + (Roles.Count + 1);
            Roles[name] = id;
            return id;
        }

        public void AddUser(string id, string username = null, string first = null, string last = null, string email = null)
        {
            Users.Add(new User(id, username ?? id, first, last, email));
            UserMappings.TryAdd(id, new List<string>());
            UserGroups.TryAdd(id, new List<string>());
        }

        public void AddGroup(string id, string name, string parentId = null)
        {
            var path = (parentId != null ? Groups[parentId].Path : string.Empty) + "/" + name;
            Groups[id] = new Group(id, name, path, parentId);
            GroupMappings.TryAdd(id, new List<string>());
        }

        public IEnumerable<Recorded> AdminCalls => Requests.Where(r => r.Uri.AbsolutePath.StartsWith("/admin/", StringComparison.Ordinal));

        public int Count(HttpMethod method, string pathContains)
            => Requests.Count(r => r.Method == method && r.Uri.AbsolutePath.Contains(pathContains, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded(request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), body));

            var intercepted = Intercept?.Invoke(request);
            if (intercepted != null) return intercepted;

            var path = request.RequestUri.AbsolutePath;
            if (path == $"/realms/{Realm}/protocol/openid-connect/token") return Token(body);

            var prefix = $"/admin/realms/{Realm}/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return Json(HttpStatusCode.NotFound, new { error = "Realm not found." });
            if (request.Headers.Authorization?.Scheme != "Bearer" || !ValidTokens.Contains(request.Headers.Authorization.Parameter))
                return Json(HttpStatusCode.Unauthorized, new { error = "HTTP 401 Unauthorized" });

            // Segments stay escaped in AbsolutePath: unescape each one like Keycloak's JAX-RS path params.
            var segments = path.Substring(prefix.Length).Split('/').Select(Uri.UnescapeDataString).ToArray();
            var query = ParseQuery(request.RequestUri.Query);
            return Admin(request.Method, segments, query, body);
        }

        private HttpResponseMessage Token(string body)
        {
            var form = ParseQuery("?" + body);
            if (form.GetValueOrDefault("grant_type") != "client_credentials"
                || form.GetValueOrDefault("client_id") != AdminClientId || form.GetValueOrDefault("client_secret") != AdminSecret)
                return Json(HttpStatusCode.Unauthorized, new { error = "invalid_client", error_description = "Invalid client or Invalid client credentials" });

            var token = "tok-" + ++TokensIssued;
            ValidTokens.Add(token);
            return Json(HttpStatusCode.OK, new { access_token = token, expires_in = ExpiresIn, token_type = "Bearer" });
        }

        private HttpResponseMessage Admin(HttpMethod method, string[] s, Dictionary<string, string> query, string body)
        {
            // clients?clientId=[&search=true]
            if (s.Length == 1 && s[0] == "clients" && method == HttpMethod.Get)
            {
                var wanted = query.GetValueOrDefault("clientId");
                var search = query.GetValueOrDefault("search") == "true";
                var found = new List<object>();
                if (ConfigClientExists && wanted == ConfigClientId) found.Add(new { id = ClientUuid, clientId = ConfigClientId });
                found.AddRange(Clients.Values
                    .Where(c => wanted == null || (search ? c.ClientId.Contains(wanted, StringComparison.OrdinalIgnoreCase) : c.ClientId == wanted))
                    .Select(ClientJson));
                return Json(HttpStatusCode.OK, found.ToArray());
            }

            // clients (create)
            if (s.Length == 1 && s[0] == "clients" && method == HttpMethod.Post)
            {
                var node = JsonNode.Parse(body);
                var clientId = node["clientId"].GetValue<string>();
                if (Clients.Values.Any(c => c.ClientId == clientId) || clientId == ConfigClientId)
                    return Json(HttpStatusCode.Conflict, new { errorMessage = $"Client {clientId} already exists" });
                var number = Clients.Count + 1 + DeletedClients;
                var client = new Client
                {
                    Id = "client-" + number,
                    ClientId = clientId,
                    Name = node["name"]?.GetValue<string>(),
                    ServiceAccountUserId = "sa-" + number,
                    Secret = "secret-" + number + "-1"
                };
                foreach (var pair in node["attributes"]?.AsObject() ?? new JsonObject()) client.Attributes[pair.Key] = pair.Value?.GetValue<string>();
                Clients[client.Id] = client;
                AddUser(client.ServiceAccountUserId, "service-account-" + clientId);
                var created = new HttpResponseMessage(HttpStatusCode.Created);
                if (SendLocationHeader) created.Headers.Location = new Uri($"{BaseUrl}/admin/realms/{Realm}/clients/{client.Id}");
                return created;
            }

            // clients/{id}[/service-account-user|/client-secret] of the clients created through the API
            if (s.Length >= 2 && s[0] == "clients" && Clients.TryGetValue(s[1], out var known))
            {
                if (s.Length == 2 && method == HttpMethod.Get) return Json(HttpStatusCode.OK, ClientJson(known));
                if (s.Length == 2 && method == HttpMethod.Delete)
                {
                    Clients.Remove(known.Id);
                    DeletedClients++;
                    Users.RemoveAll(u => u.Id == known.ServiceAccountUserId);
                    UserMappings.Remove(known.ServiceAccountUserId);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (s.Length == 3 && s[2] == "service-account-user" && method == HttpMethod.Get)
                    return Json(HttpStatusCode.OK, new { id = known.ServiceAccountUserId, username = "service-account-" + known.ClientId });
                if (s.Length == 3 && s[2] == "client-secret")
                {
                    if (method == HttpMethod.Post) known.Secret = "secret-" + known.Id.Substring("client-".Length) + "-" + ++known.SecretVersion;
                    return Json(HttpStatusCode.OK, new { type = "secret", value = known.Secret });
                }
            }
            if (s.Length >= 2 && s[0] == "clients" && s[1] != ClientUuid)
                return Json(HttpStatusCode.NotFound, new { error = "Could not find client" });

            // clients/{cid}/roles[/{name}]
            if (s.Length >= 3 && s[0] == "clients" && s[2] == "roles")
            {
                if (!ConfigClientExists || s[1] != ClientUuid) return Json(HttpStatusCode.NotFound, new { error = "Could not find client" });
                if (s.Length == 4 && method == HttpMethod.Get)
                    return Roles.TryGetValue(s[3], out var roleId)
                        ? Json(HttpStatusCode.OK, new { id = roleId, name = s[3], clientRole = true })
                        : Json(HttpStatusCode.NotFound, new { error = "Could not find role" });
                if (s.Length == 3 && method == HttpMethod.Post)
                {
                    var name = JsonNode.Parse(body)["name"].GetValue<string>();
                    if (Roles.ContainsKey(name)) return Json(HttpStatusCode.Conflict, new { errorMessage = $"Role with name {name} already exists" });
                    AddRole(name);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
            }

            // users|groups/{id}/role-mappings/clients/{cid}
            if (s.Length == 5 && (s[0] == "users" || s[0] == "groups") && s[2] == "role-mappings" && s[3] == "clients")
            {
                var mappings = s[0] == "users" ? UserMappings : GroupMappings;
                if (!mappings.TryGetValue(s[1], out var mapped))
                    return Json(HttpStatusCode.NotFound, new { error = s[0] == "users" ? "User not found" : "Could not find group by id" });
                if (s[4] != ClientUuid) return Json(HttpStatusCode.NotFound, new { error = "Could not find client" });

                if (method == HttpMethod.Get)
                    return Json(HttpStatusCode.OK, mapped.Select(n => new { id = Roles[n], name = n, clientRole = true }).ToArray());

                var roles = JsonNode.Parse(body).AsArray().Select(r => (Id: r["id"]?.GetValue<string>(), Name: r["name"]?.GetValue<string>())).ToList();
                if (roles.Any(r => r.Name == null || !Roles.TryGetValue(r.Name, out var id) || id != r.Id))
                    return Json(HttpStatusCode.NotFound, new { error = "Role not found" });
                if (method == HttpMethod.Post)
                    foreach (var role in roles.Where(r => !mapped.Contains(r.Name))) mapped.Add(role.Name);
                else if (method == HttpMethod.Delete)
                    mapped.RemoveAll(n => roles.Any(r => r.Name == n));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            // users/{id}/groups
            if (s.Length == 3 && s[0] == "users" && s[2] == "groups" && method == HttpMethod.Get)
                return UserGroups.TryGetValue(s[1], out var groupIds)
                    ? Json(HttpStatusCode.OK, groupIds.Select(id => GroupJson(Groups[id])).ToArray())
                    : Json(HttpStatusCode.NotFound, new { error = "User not found" });

            // groups/{id}
            if (s.Length == 2 && s[0] == "groups" && method == HttpMethod.Get)
                return Groups.TryGetValue(s[1], out var group)
                    ? Json(HttpStatusCode.OK, GroupJson(group))
                    : Json(HttpStatusCode.NotFound, new { error = "Could not find group by id" });

            // users?search=
            if (s.Length == 1 && s[0] == "users" && method == HttpMethod.Get)
            {
                var search = query.GetValueOrDefault("search");
                var first = int.Parse(query.GetValueOrDefault("first") ?? "0");
                var max = int.Parse(query.GetValueOrDefault("max") ?? "100");
                return Json(HttpStatusCode.OK, Users
                    .Where(u => search == null || u.Username.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .Skip(first).Take(max)
                    .Select(u => new { id = u.Id, username = u.Username, firstName = u.FirstName, lastName = u.LastName, email = u.Email, enabled = true })
                    .ToArray());
            }

            // groups?search= : top-level groups with their whole subtree.
            if (s.Length == 1 && s[0] == "groups" && method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, Groups.Values.Where(g => g.ParentId == null).Select(Tree).ToArray());

            return Json(HttpStatusCode.NotFound, new { error = "Unknown endpoint" });
        }

        private static object ClientJson(Client c)
            => new { id = c.Id, clientId = c.ClientId, name = c.Name, enabled = true, serviceAccountsEnabled = true, attributes = c.Attributes };

        private object Tree(Group group)
            => new { id = group.Id, name = group.Name, path = group.Path, subGroups = Groups.Values.Where(g => g.ParentId == group.Id).Select(Tree).ToArray() };

        private static object GroupJson(Group g) => new { id = g.Id, name = g.Name, path = g.Path, parentId = g.ParentId };

        public static HttpResponseMessage Json(HttpStatusCode status, object value)
            => new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in (query ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                result[key] = value;
            }
            return result;
        }
    }
}
