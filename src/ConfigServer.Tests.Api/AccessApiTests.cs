using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Application.Api.IdentityAdmin;
using ConfigServer.Domain.Security.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    /// <summary>Records calls and answers from canned data; <see cref="Failure"/> is thrown by every operation when set.</summary>
    internal sealed class FakeIdentityAdmin : IIdentityAdmin
    {
        public bool Enabled { get; set; } = true;
        public IdentityAdminException Failure { get; set; }
        public List<(string Op, SubjectType Type, string Id, ConfigGrant Grant)> Calls { get; } = new List<(string, SubjectType, string, ConfigGrant)>();
        public List<(string Search, int First, int Max)> Searches { get; } = new List<(string, int, int)>();

        public Task<IdentityAdminInfo> GetInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enabled
                ? new IdentityAdminInfo("keycloak", true, "config-web", KeycloakIdentityAdmin.GrantNote, TokenEndpoint)
                : new IdentityAdminInfo("none", false, null, DisabledIdentityAdmin.DisabledMessage));

        public Task<IReadOnlyList<IdentityUser>> SearchUsersAsync(string search, int first, int max, CancellationToken cancellationToken = default)
        {
            Throw();
            Searches.Add((search, first, max));
            return Task.FromResult<IReadOnlyList<IdentityUser>>(new[] { new IdentityUser("u1", "alice", "Alice Liddell", "alice@example.com") });
        }

        public Task<IReadOnlyList<IdentityGroup>> SearchGroupsAsync(string search, int first, int max, CancellationToken cancellationToken = default)
        {
            Throw();
            Searches.Add((search, first, max));
            return Task.FromResult<IReadOnlyList<IdentityGroup>>(new[] { new IdentityGroup("g1", "web-team", "/engineering/web-team") });
        }

        public Task<IReadOnlyList<SubjectGrant>> GetGrantsAsync(SubjectType type, string subjectId, CancellationToken cancellationToken = default)
        {
            Throw();
            Calls.Add(("get", type, subjectId, null));
            return Task.FromResult<IReadOnlyList<SubjectGrant>>(new[]
            {
                SubjectGrant.FromRoleName("config:read:web"),
                SubjectGrant.FromRoleName("config:write:*:dev", inherited: true, via: "devs")
            });
        }

        public Task<SubjectGrant> GrantAsync(SubjectType type, string subjectId, ConfigGrant grant, CancellationToken cancellationToken = default)
        {
            Throw();
            Calls.Add(("grant", type, subjectId, grant));
            return Task.FromResult(SubjectGrant.From(grant));
        }

        public Task<bool> RevokeAsync(SubjectType type, string subjectId, ConfigGrant grant, CancellationToken cancellationToken = default)
        {
            Throw();
            Calls.Add(("revoke", type, subjectId, grant));
            return Task.FromResult(true);
        }

        public const string TokenEndpoint = "https://idp.example.com/realms/acme/protocol/openid-connect/token";
        public const string MachineSecret = "machine-secret-value";

        public List<MachineClient> Machines { get; } = new List<MachineClient>();
        public List<(string Op, string Name, IReadOnlyList<ConfigGrant> Grants, string Id)> MachineCalls { get; }
            = new List<(string, string, IReadOnlyList<ConfigGrant>, string)>();

        public Task<IReadOnlyList<MachineClient>> ListMachineClientsAsync(CancellationToken cancellationToken = default)
        {
            Throw();
            MachineCalls.Add(("list", null, null, null));
            return Task.FromResult<IReadOnlyList<MachineClient>>(Machines.ToList());
        }

        public Task<MachineClientSecret> CreateMachineClientAsync(string name, IReadOnlyList<ConfigGrant> grants, CancellationToken cancellationToken = default)
        {
            Throw();
            MachineCalls.Add(("create", name, grants, null));
            var client = new MachineClient("id-" + (Machines.Count + 1), "cfs-" + name + "-abc123", name,
                SubjectGrant.Collect(grants.Select(g => SubjectGrant.From(g))), true, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            Machines.Add(client);
            return Task.FromResult(new MachineClientSecret(client, MachineSecret));
        }

        public Task<MachineClientSecret> RotateMachineClientSecretAsync(string id, CancellationToken cancellationToken = default)
        {
            Throw();
            MachineCalls.Add(("rotate", null, null, id));
            var client = Machines.FirstOrDefault(m => m.Id == id)
                         ?? throw new IdentityAdminException(IdentityAdminErrorKind.NotFound, $"Machine credential '{id}' was not found.");
            return Task.FromResult(new MachineClientSecret(client, MachineSecret + "-2"));
        }

        public Task<bool> DeleteMachineClientAsync(string id, CancellationToken cancellationToken = default)
        {
            Throw();
            MachineCalls.Add(("delete", null, null, id));
            return Task.FromResult(Machines.RemoveAll(m => m.Id == id) > 0);
        }

        private void Throw()
        {
            if (Failure != null) throw Failure;
        }
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new ConcurrentQueue<(string, LogLevel, string)>();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            private readonly string _category;

            public Logger(CapturingLoggerProvider owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => _owner.Entries.Enqueue((_category, logLevel, formatter(state, exception)));
        }
    }

    [TestClass]
    public class AccessApiTests
    {
        private const string Grants = "/api/access/subjects/user/u1/grants";

        private FakeIdentityAdmin _fake;
        private CapturingLoggerProvider _logs;
        private AuthTestHost _host;
        private string _admin;

        [TestInitialize]
        public void Setup()
        {
            _fake = new FakeIdentityAdmin();
            _logs = new CapturingLoggerProvider();
            _host = new AuthTestHost(configureServices: services =>
            {
                services.RemoveAll<IIdentityAdmin>();
                services.AddSingleton<IIdentityAdmin>(_fake);
                services.AddSingleton<ILoggerProvider>(_logs);
            });
            _admin = _host.Token(new[] { "config:admin" });
        }

        [TestCleanup]
        public void Cleanup() => _host.Dispose();

        private Task<HttpResponseMessage> Send(HttpMethod method, string url, string json = null, string bearer = null)
            => _host.Client.SendAsync(AuthTestHost.Request(method, url, bearer: bearer ?? _admin, json: json));

        private static async Task<JsonElement> Body(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string contains = null)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(status, response.StatusCode, text);
            var error = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
            if (contains != null) StringAssert.Contains(error, contains);
        }

        public static IEnumerable<object[]> Endpoints => new[]
        {
            new object[] { "GET", "/api/access/info", null },
            new object[] { "GET", "/api/access/users", null },
            new object[] { "GET", "/api/access/groups", null },
            new object[] { "GET", Grants, null },
            new object[] { "POST", Grants, "{\"permission\":\"read\"}" },
            new object[] { "DELETE", Grants + "?permission=read", null }
        };

        [TestMethod]
        [DynamicData(nameof(Endpoints))]
        public async Task Requires_authentication(string method, string url, string json)
        {
            var response = await _host.Client.SendAsync(AuthTestHost.Request(new HttpMethod(method), url, json: json));

            await AssertError(response, HttpStatusCode.Unauthorized);
            Assert.IsEmpty(_fake.Calls);
        }

        [TestMethod]
        [DynamicData(nameof(Endpoints))]
        public async Task Requires_global_admin(string method, string url, string json)
        {
            foreach (var roles in new[] { new[] { "config:write" }, new[] { "config:read:web" }, new[] { "config:admin:web" } })
            {
                var response = await Send(new HttpMethod(method), url, json, _host.Token(roles));
                await AssertError(response, HttpStatusCode.Forbidden);
            }
            Assert.IsEmpty(_fake.Calls);
        }

        [TestMethod]
        public async Task Api_keys_cannot_manage_access()
        {
            var created = await Send(HttpMethod.Post, "/api/keys", "{\"name\":\"ci\",\"permission\":\"write\"}");
            var secret = (await Body(created)).GetProperty("secret").GetString();

            var response = await _host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/api/access/info", apiKey: secret));

            await AssertError(response, HttpStatusCode.Forbidden);
        }

        [TestMethod]
        public async Task Info_reports_provider_client_and_note()
        {
            var response = await Send(HttpMethod.Get, "/api/access/info");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var info = await Body(response);
            Assert.AreEqual("keycloak", info.GetProperty("provider").GetString());
            Assert.IsTrue(info.GetProperty("enabled").GetBoolean());
            Assert.AreEqual("config-web", info.GetProperty("configClientId").GetString());
            StringAssert.Contains(info.GetProperty("note").GetString(), "next token refresh");
        }

        [TestMethod]
        public async Task Disabled_provider_answers_501_except_info()
        {
            _fake.Enabled = false;

            var info = await Body(await Send(HttpMethod.Get, "/api/access/info"));
            Assert.AreEqual("none", info.GetProperty("provider").GetString());
            Assert.IsFalse(info.GetProperty("enabled").GetBoolean());

            foreach (var endpoint in Endpoints.Skip(1))
            {
                var response = await Send(new HttpMethod((string)endpoint[0]), (string)endpoint[1], (string)endpoint[2]);
                await AssertError(response, HttpStatusCode.NotImplemented, "IdentityAdmin");
            }
            // 501 wins over validation errors.
            await AssertError(await Send(HttpMethod.Post, "/api/access/subjects/robot/x/grants", "{}"), HttpStatusCode.NotImplemented);
            Assert.IsEmpty(_fake.Calls);
        }

        [TestMethod]
        public async Task Real_registration_defaults_to_disabled()
        {
            using var host = new AuthTestHost();
            var response = await host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/api/access/info", bearer: host.Token(new[] { "config:admin" })));

            var info = await Body(response);
            Assert.AreEqual("none", info.GetProperty("provider").GetString());
            Assert.IsFalse(info.GetProperty("enabled").GetBoolean());
            Assert.IsInstanceOfType<DisabledIdentityAdmin>(host.Factory.Services.GetRequiredService<IIdentityAdmin>());
        }

        [TestMethod]
        public async Task Keycloak_registration_resolves_the_typed_client_without_calling_it()
        {
            using var host = new AuthTestHost(settings: new Dictionary<string, string>
            {
                ["IdentityAdmin:Provider"] = "Keycloak",
                ["IdentityAdmin:Keycloak:BaseUrl"] = "http://localhost:18080/",
                ["IdentityAdmin:Keycloak:Realm"] = "acme",
                ["IdentityAdmin:Keycloak:AdminClientId"] = "config-manager-admin",
                ["IdentityAdmin:Keycloak:AdminClientSecret"] = "s3cret"
            });

            Assert.IsInstanceOfType<KeycloakIdentityAdmin>(host.Factory.Services.GetRequiredService<IIdentityAdmin>());
            var response = await host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/api/access/info", bearer: host.Token(new[] { "config:admin" })));
            var text = await response.Content.ReadAsStringAsync();
            var info = JsonDocument.Parse(text).RootElement;
            Assert.AreEqual("keycloak", info.GetProperty("provider").GetString());
            Assert.AreEqual("config-web", info.GetProperty("configClientId").GetString(), "defaults to Auth:Oidc:ClientId");
            Assert.IsFalse(text.Contains("s3cret"));
        }

        [TestMethod]
        public async Task Lists_users_and_groups_with_paging()
        {
            var users = await Send(HttpMethod.Get, "/api/access/users?search=%20ali%20&first=10&max=5");
            Assert.AreEqual(HttpStatusCode.OK, users.StatusCode);
            var user = (await Body(users)).EnumerateArray().Single();
            Assert.AreEqual("u1", user.GetProperty("id").GetString());
            Assert.AreEqual("alice", user.GetProperty("username").GetString());
            Assert.AreEqual("Alice Liddell", user.GetProperty("displayName").GetString());
            Assert.AreEqual("alice@example.com", user.GetProperty("email").GetString());
            Assert.AreEqual(("ali", 10, 5), _fake.Searches.Last());

            var groups = await Send(HttpMethod.Get, "/api/access/groups");
            var group = (await Body(groups)).EnumerateArray().Single();
            Assert.AreEqual("/engineering/web-team", group.GetProperty("path").GetString());
            Assert.AreEqual(((string)null, 0, 20), _fake.Searches.Last());
        }

        [TestMethod]
        [DataRow("/api/access/users?first=-1", "first")]
        [DataRow("/api/access/users?max=0", "max")]
        [DataRow("/api/access/groups?max=101", "max")]
        public async Task Rejects_bad_paging(string url, string field)
            => await AssertError(await Send(HttpMethod.Get, url), HttpStatusCode.BadRequest, field);

        [TestMethod]
        public async Task Lists_grants_with_via_only_for_inherited()
        {
            var response = await Send(HttpMethod.Get, "/api/access/subjects/group/g-1/grants");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var grants = (await Body(response)).EnumerateArray().ToArray();
            Assert.AreEqual("config:read:web", grants[0].GetProperty("role").GetString());
            Assert.IsFalse(grants[0].GetProperty("inherited").GetBoolean());
            Assert.IsFalse(grants[0].TryGetProperty("via", out _));
            Assert.AreEqual("devs", grants[1].GetProperty("via").GetString());
            Assert.AreEqual(("get", SubjectType.Group, "g-1"), (_fake.Calls[0].Op, _fake.Calls[0].Type, _fake.Calls[0].Id));
        }

        [TestMethod]
        public async Task Grant_returns_201_with_the_subject_grant_and_logs_the_actor()
        {
            var response = await Send(HttpMethod.Post, Grants, "{\"permission\":\"write\",\"app\":\"web\",\"environment\":\"dev\"}");

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            var grant = await Body(response);
            Assert.AreEqual("write", grant.GetProperty("permission").GetString());
            Assert.AreEqual("web", grant.GetProperty("app").GetString());
            Assert.AreEqual("dev", grant.GetProperty("environment").GetString());
            Assert.AreEqual("config:write:web:dev", grant.GetProperty("role").GetString());
            Assert.IsFalse(grant.GetProperty("inherited").GetBoolean());
            Assert.AreEqual("config:write:web:dev", _fake.Calls.Single().Grant.RoleName);

            var log = _logs.Entries.Single(e => e.Category.EndsWith("AccessController") && e.Level == LogLevel.Information);
            StringAssert.Contains(log.Message, "Alice");
            StringAssert.Contains(log.Message, "config:write:web:dev");
            StringAssert.Contains(log.Message, "user u1");
        }

        [TestMethod]
        public async Task Grant_defaults_to_all_apps_and_environments()
        {
            var response = await Send(HttpMethod.Post, "/api/access/subjects/GROUP/g1/grants", "{\"permission\":\"read\"}");

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            var call = _fake.Calls.Single();
            Assert.AreEqual(SubjectType.Group, call.Type);
            Assert.AreEqual("config:read", call.Grant.RoleName);
            Assert.AreEqual("*", (await Body(response)).GetProperty("app").GetString());
        }

        [TestMethod]
        [DataRow("/api/access/subjects/robot/u1/grants", "{\"permission\":\"read\"}", "'user' or 'group'")]
        [DataRow(Grants, "", "JSON object")]
        [DataRow(Grants, "{}", "permission")]
        [DataRow(Grants, "{\"permission\":\"owner\"}", "Unknown permission")]
        [DataRow(Grants, "{\"permission\":\"admin\",\"app\":\"web\"}", "global only")]
        [DataRow(Grants, "{\"permission\":\"read\",\"app\":\"../etc\"}", "Invalid application name")]
        [DataRow(Grants, "{\"permission\":\"read\",\"environment\":\"default\"}", "Invalid environment name")]
        [DataRow(Grants, "{\"permission\":\"read\",\"app\":\"nope\"}", "Unknown application 'nope'")]
        [DataRow(Grants, "{\"permission\":\"read\",\"app\":\"Web\"}", "did you mean 'web'")]
        [DataRow(Grants, "{\"permission\":\"read\",\"app\":\"web\",\"environment\":\"qa\"}", "Unknown environment 'qa'")]
        [DataRow(Grants, "{\"permission\":\"read\",\"environment\":\"PROD\"}", "did you mean 'prod'")]
        public async Task Grant_validation_errors_are_400(string url, string json, string message)
        {
            var request = AuthTestHost.Request(HttpMethod.Post, url, bearer: _admin, json: json);
            if (json == "") request.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");

            await AssertError(await _host.Client.SendAsync(request), HttpStatusCode.BadRequest, message);
            Assert.IsEmpty(_fake.Calls);
        }

        [TestMethod]
        public async Task Grant_accepts_known_apps_and_environments()
        {
            // web/prod exist as files, dev is declared in _environments.json, api has no dev override (still a known env).
            foreach (var json in new[] { "{\"permission\":\"read\",\"app\":\"web\",\"environment\":\"prod\"}",
                                         "{\"permission\":\"write\",\"app\":\"api\",\"environment\":\"dev\"}",
                                         "{\"permission\":\"write\",\"app\":\"*\",\"environment\":\"dev\"}",
                                         "{\"permission\":\"admin\",\"app\":\"*\",\"environment\":\"*\"}" })
                Assert.AreEqual(HttpStatusCode.Created, (await Send(HttpMethod.Post, Grants, json)).StatusCode, json);
            CollectionAssert.AreEqual(new[] { "config:read:web:prod", "config:write:api:dev", "config:write:*:dev", "config:admin" },
                _fake.Calls.Select(c => c.Grant.RoleName).ToArray());
        }

        [TestMethod]
        public async Task Revoke_returns_204_and_only_checks_syntax()
        {
            var response = await Send(HttpMethod.Delete, Grants + "?permission=read&app=removed-app&environment=*");

            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, await response.Content.ReadAsStringAsync());
            var call = _fake.Calls.Single();
            Assert.AreEqual("revoke", call.Op);
            Assert.AreEqual("config:read:removed-app", call.Grant.RoleName);
            Assert.IsTrue(_logs.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("revoked role config:read:removed-app")));
        }

        [TestMethod]
        [DataRow("", "permission")]
        [DataRow("?permission=admin&app=web", "global only")]
        [DataRow("?permission=read&environment=..", "Invalid environment name")]
        public async Task Revoke_validation_errors_are_400(string query, string message)
        {
            await AssertError(await Send(HttpMethod.Delete, Grants + query), HttpStatusCode.BadRequest, message);
            Assert.IsEmpty(_fake.Calls);
        }

        [TestMethod]
        [DataRow(IdentityAdminErrorKind.NotFound, HttpStatusCode.NotFound)]
        [DataRow(IdentityAdminErrorKind.Denied, HttpStatusCode.BadGateway)]
        [DataRow(IdentityAdminErrorKind.Unauthorized, HttpStatusCode.BadGateway)]
        [DataRow(IdentityAdminErrorKind.Misconfigured, HttpStatusCode.BadGateway)]
        [DataRow(IdentityAdminErrorKind.Unavailable, HttpStatusCode.BadGateway)]
        [DataRow(IdentityAdminErrorKind.Invalid, HttpStatusCode.BadRequest)]
        public async Task Provider_errors_map_to_status_and_error(IdentityAdminErrorKind kind, HttpStatusCode status)
        {
            _fake.Failure = new IdentityAdminException(kind, "provider says no: grant realm-management role 'manage-users'.");

            await AssertError(await Send(HttpMethod.Get, Grants), status, "provider says no");
            await AssertError(await Send(HttpMethod.Post, Grants, "{\"permission\":\"read\"}"), status, "manage-users");
        }

        [TestMethod]
        public void Options_validation_names_the_setting_and_never_the_secret()
        {
            IdentityAdminOptions Load(params (string Key, string Value)[] values)
                => IdentityAdminOptions.Load(new ConfigurationBuilder()
                    .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build());

            var valid = new[]
            {
                ("IdentityAdmin:Provider", "keycloak"),
                ("IdentityAdmin:Keycloak:BaseUrl", "https://kc.example.com/"),
                ("IdentityAdmin:Keycloak:Realm", "acme"),
                ("IdentityAdmin:Keycloak:AdminClientId", "config-manager-admin"),
                ("IdentityAdmin:Keycloak:AdminClientSecret", "top-secret"),
                ("Auth:Oidc:ClientId", "config-web")
            };
            var options = Load(valid);
            options.Validate();
            Assert.AreEqual(IdentityAdminProvider.Keycloak, options.Provider);
            Assert.AreEqual("https://kc.example.com", options.Keycloak.BaseUrl);
            Assert.AreEqual("config-web", options.Keycloak.ConfigClientId);
            Assert.IsFalse(options.Keycloak.ToString().Contains("top-secret"));

            Assert.AreEqual(IdentityAdminProvider.None, Load().Provider);
            Load().Validate();
            Assert.Throws<InvalidOperationException>(() => Load(("IdentityAdmin:Provider", "okta")));

            // valid with some keys replaced (null value = removed).
            (string, string)[] With(params (string Key, string Value)[] changes)
                => valid.Where(v => !changes.Any(c => c.Key == v.Item1)).Concat(changes.Where(c => c.Value != null)).ToArray();
            void Fails(string expected, params (string, string)[] changes)
            {
                var ex = Assert.Throws<InvalidOperationException>(() => Load(With(changes)).Validate());
                StringAssert.Contains(ex.Message, expected);
                Assert.IsFalse(ex.Message.Contains("top-secret"), ex.Message);
            }
            Fails("BaseUrl is required", ("IdentityAdmin:Keycloak:BaseUrl", null));
            Fails("must use https", ("IdentityAdmin:Keycloak:BaseUrl", "http://keycloak:8080"));
            Fails("Realm is required", ("IdentityAdmin:Keycloak:Realm", null));
            Fails("AdminClientId is required", ("IdentityAdmin:Keycloak:AdminClientId", null));
            Fails("AdminClientSecret is required", ("IdentityAdmin:Keycloak:AdminClientSecret", null));
            Fails("ConfigClientId", ("Auth:Oidc:ClientId", null));

            Fails("TimeoutSeconds", ("IdentityAdmin:Keycloak:TimeoutSeconds", "0"));
            Load(With(("IdentityAdmin:Keycloak:BaseUrl", "http://localhost:8080"))).Validate();
            Load(With(("IdentityAdmin:Keycloak:BaseUrl", "http://keycloak:8080"), ("IdentityAdmin:Keycloak:RequireHttps", "false"))).Validate();
            Assert.AreEqual("other-client", Load(With(("IdentityAdmin:Keycloak:ConfigClientId", "other-client"))).Keycloak.ConfigClientId);
        }
    }
}
