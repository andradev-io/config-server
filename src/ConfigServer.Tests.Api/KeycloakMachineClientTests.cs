using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ConfigServer.Application.Api.IdentityAdmin;
using ConfigServer.Domain.Security;
using ConfigServer.Domain.Security.Identity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    /// <summary>The machine-credential ("OIDC API key") part of KeycloakIdentityAdmin against the in-memory Keycloak.</summary>
    [TestClass]
    public class KeycloakMachineClientTests
    {
        private FakeKeycloak _kc;
        private ManualClock _clock;
        private KeycloakAdminCache _cache;

        [TestInitialize]
        public void Setup()
        {
            _kc = new FakeKeycloak();
            _clock = new ManualClock();
            _cache = new KeycloakAdminCache();
        }

        private KeycloakIdentityAdmin Admin(string tokenEndpoint = null)
            => new KeycloakIdentityAdmin(new HttpClient(_kc, disposeHandler: false), new KeycloakAdminOptions
            {
                BaseUrl = FakeKeycloak.BaseUrl,
                Realm = FakeKeycloak.Realm,
                AdminClientId = FakeKeycloak.AdminClientId,
                AdminClientSecret = FakeKeycloak.AdminSecret,
                ConfigClientId = FakeKeycloak.ConfigClientId,
                TokenEndpoint = tokenEndpoint
            }, _cache, _clock);

        private static ConfigGrant G(AccessPermission permission, string app = "*", string environment = "*")
            => ConfigGrant.Create(permission, app, environment);

        private static async Task<IdentityAdminException> Fails(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (IdentityAdminException ex)
            {
                return ex;
            }
            Assert.Fail("Expected an IdentityAdminException.");
            return null;
        }

        [TestMethod]
        public async Task Info_carries_the_public_token_endpoint()
        {
            var derived = await Admin().GetInfoAsync();
            Assert.AreEqual($"{FakeKeycloak.BaseUrl}/realms/{FakeKeycloak.Realm}/protocol/openid-connect/token", derived.TokenEndpoint,
                "falls back to the admin base URL when no public endpoint is configured");

            var configured = await Admin("https://auth.example.com/realms/acme/protocol/openid-connect/token").GetInfoAsync();
            Assert.AreEqual("https://auth.example.com/realms/acme/protocol/openid-connect/token", configured.TokenEndpoint);
        }

        [TestMethod]
        public async Task Create_makes_a_service_account_only_client_with_the_marker_and_grants()
        {
            var created = await Admin().CreateMachineClientAsync("Billing CI", new[] { G(AccessPermission.Read, "billing"), G(AccessPermission.Write, "billing", "dev") });

            var client = _kc.Clients.Values.Single();
            Assert.AreEqual(client.Id, created.Client.Id);
            Assert.AreEqual(client.ClientId, created.Client.ClientId);
            StringAssert.Matches(client.ClientId, new System.Text.RegularExpressions.Regex("^cfs-billing-ci-[a-z0-9]{6}$"));
            Assert.AreEqual("Billing CI", created.Client.Name);
            Assert.AreEqual("secret-1-1", created.Secret);
            Assert.AreEqual("config-server", client.Attributes["managed-by"]);
            Assert.IsTrue(client.Attributes.ContainsKey("config-server.created-at"));
            Assert.IsNotNull(created.Client.CreatedAt);

            var post = _kc.AdminCalls.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/clients"));
            var body = JsonNode.Parse(post.Body);
            Assert.IsTrue(body["serviceAccountsEnabled"].GetValue<bool>());
            Assert.IsFalse(body["publicClient"].GetValue<bool>());
            Assert.IsFalse(body["standardFlowEnabled"].GetValue<bool>());
            Assert.IsFalse(body["implicitFlowEnabled"].GetValue<bool>());
            Assert.IsFalse(body["directAccessGrantsEnabled"].GetValue<bool>());
            Assert.AreEqual("client-secret", body["clientAuthenticatorType"].GetValue<string>());

            CollectionAssert.AreEquivalent(new[] { "config:read:billing", "config:write:billing:dev" }, _kc.UserMappings[client.ServiceAccountUserId]);
            CollectionAssert.AreEquivalent(new[] { "config:read:billing", "config:write:billing:dev" }, created.Client.Grants.Select(g => g.Role).ToArray());
            Assert.AreEqual(1, _kc.Count(HttpMethod.Post, "/role-mappings/clients/"), "all grants are mapped in one call");
        }

        [TestMethod]
        public async Task Create_reuses_existing_roles_and_resolves_the_id_without_a_location_header()
        {
            _kc.AddRole("config:read");
            _kc.SendLocationHeader = false;

            var created = await Admin().CreateMachineClientAsync("ci", new[] { G(AccessPermission.Read) });

            Assert.AreEqual(1, _kc.Roles.Count, "no duplicate role");
            Assert.AreEqual(_kc.Clients.Values.Single().Id, created.Client.Id);
        }

        [TestMethod]
        public async Task Create_without_grants_is_allowed_and_slugs_odd_names()
        {
            var created = await Admin().CreateMachineClientAsync("  ¡¡ Ünïcode / name!!  ", Array.Empty<ConfigGrant>());

            StringAssert.Matches(created.Client.ClientId, new System.Text.RegularExpressions.Regex("^cfs-[a-z0-9-]+-[a-z0-9]{6}$"));
            Assert.IsEmpty(created.Client.Grants);
            Assert.AreEqual(0, _kc.Count(HttpMethod.Post, "/role-mappings/clients/"));
        }

        [TestMethod]
        public async Task Create_with_only_symbols_in_the_name_still_gets_a_client_id()
        {
            var created = await Admin().CreateMachineClientAsync("!!!", Array.Empty<ConfigGrant>());

            StringAssert.Matches(created.Client.ClientId, new System.Text.RegularExpressions.Regex("^cfs-key-[a-z0-9]{6}$"));
        }

        [TestMethod]
        public async Task Create_refuses_admin_grants_and_bad_names_before_calling_keycloak()
        {
            var admin = await Fails(() => Admin().CreateMachineClientAsync("ci", new[] { G(AccessPermission.Admin) }));
            Assert.AreEqual(IdentityAdminErrorKind.Invalid, admin.Kind);
            StringAssert.Contains(admin.Message, "config:admin");

            foreach (var name in new[] { null, "", "   ", new string('x', 101), "a\nb" })
                Assert.AreEqual(IdentityAdminErrorKind.Invalid, (await Fails(() => Admin().CreateMachineClientAsync(name, Array.Empty<ConfigGrant>()))).Kind);

            Assert.IsEmpty(_kc.Requests);
        }

        [TestMethod]
        public async Task Create_removes_the_half_made_client_when_a_later_step_fails()
        {
            _kc.Intercept = r => r.Method == HttpMethod.Post && r.RequestUri.AbsolutePath.Contains("/role-mappings/clients/")
                ? FakeKeycloak.Json(HttpStatusCode.Forbidden, new { error = "forbidden" })
                : null;

            var ex = await Fails(() => Admin().CreateMachineClientAsync("ci", new[] { G(AccessPermission.Read) }));

            Assert.AreEqual(IdentityAdminErrorKind.Denied, ex.Kind);
            StringAssert.Contains(ex.Message, "manage-users");
            Assert.IsEmpty(_kc.Clients, "the incomplete credential must not stay behind");
            Assert.AreEqual(1, _kc.DeletedClients);
        }

        [TestMethod]
        public async Task Create_403_names_manage_clients()
        {
            _kc.Intercept = r => r.Method == HttpMethod.Post && r.RequestUri.AbsolutePath.EndsWith("/clients")
                ? FakeKeycloak.Json(HttpStatusCode.Forbidden, new { error = "forbidden" })
                : null;

            var ex = await Fails(() => Admin().CreateMachineClientAsync("ci", new[] { G(AccessPermission.Read) }));

            Assert.AreEqual(IdentityAdminErrorKind.Denied, ex.Kind);
            StringAssert.Contains(ex.Message, "manage-clients");
            StringAssert.Contains(ex.Message, FakeKeycloak.AdminClientId);
        }

        [TestMethod]
        public async Task Create_retries_a_taken_client_id()
        {
            var attempts = 0;
            _kc.Intercept = r =>
            {
                if (r.Method != HttpMethod.Post || !r.RequestUri.AbsolutePath.EndsWith("/clients")) return null;
                return ++attempts == 1 ? FakeKeycloak.Json(HttpStatusCode.Conflict, new { errorMessage = "exists" }) : null;
            };

            var created = await Admin().CreateMachineClientAsync("ci", Array.Empty<ConfigGrant>());

            Assert.AreEqual(2, attempts);
            Assert.AreEqual(1, _kc.Clients.Count);
            Assert.IsNotNull(created.Secret);
        }

        [TestMethod]
        public async Task List_returns_only_clients_with_the_prefix_and_the_marker()
        {
            var mine = await Admin().CreateMachineClientAsync("mine", new[] { G(AccessPermission.Read, "web", "prod") });
            _kc.AddForeignClient("other-app");                                      // wrong prefix, no marker
            _kc.AddForeignClient("lookalike", withPrefix: true);                     // prefix but no marker
            _kc.AddForeignClient("marker-only", withMarker: true);                   // marker but no prefix

            var list = await Admin().ListMachineClientsAsync();

            var item = list.Single();
            Assert.AreEqual(mine.Client.Id, item.Id);
            Assert.AreEqual("mine", item.Name);
            Assert.IsTrue(item.Enabled);
            Assert.AreEqual("config:read:web:prod", item.Grants.Single().Role);
            Assert.IsNotNull(item.CreatedAt);
            StringAssert.Contains(_kc.Requests.First(r => r.Uri.AbsolutePath.EndsWith("/clients") && r.Method == HttpMethod.Get && r.Uri.Query.Contains("search=true")).Uri.Query, "clientId=cfs-");
        }

        [TestMethod]
        public async Task Rotate_issues_a_new_secret_and_keeps_the_grants()
        {
            var created = await Admin().CreateMachineClientAsync("ci", new[] { G(AccessPermission.Write, "web") });

            var rotated = await Admin().RotateMachineClientSecretAsync(created.Client.Id);

            Assert.AreNotEqual(created.Secret, rotated.Secret);
            Assert.AreEqual("secret-1-2", rotated.Secret);
            Assert.AreEqual("config:write:web", rotated.Client.Grants.Single().Role);
        }

        [TestMethod]
        public async Task Rotate_and_delete_refuse_clients_that_were_not_created_here()
        {
            var foreign = _kc.AddForeignClient("billing-api");
            var lookalike = _kc.AddForeignClient("lookalike", withPrefix: true);
            var markerOnly = _kc.AddForeignClient("marker-only", withMarker: true);

            foreach (var client in new[] { foreign, lookalike, markerOnly })
            {
                Assert.AreEqual(IdentityAdminErrorKind.NotFound, (await Fails(() => Admin().RotateMachineClientSecretAsync(client.Id))).Kind);
                Assert.IsFalse(await Admin().DeleteMachineClientAsync(client.Id));
            }

            Assert.AreEqual(3, _kc.Clients.Count, "foreign clients are never touched");
            Assert.AreEqual(0, _kc.DeletedClients);
            Assert.AreEqual(0, _kc.Count(HttpMethod.Delete, "/clients/"));
            Assert.AreEqual(0, _kc.Count(HttpMethod.Post, "/client-secret"));
        }

        [TestMethod]
        public async Task Delete_removes_the_client_and_is_idempotent()
        {
            var created = await Admin().CreateMachineClientAsync("ci", new[] { G(AccessPermission.Read) });

            Assert.IsTrue(await Admin().DeleteMachineClientAsync(created.Client.Id));
            Assert.IsFalse(await Admin().DeleteMachineClientAsync(created.Client.Id));
            Assert.IsFalse(await Admin().DeleteMachineClientAsync("unknown-id"));
            Assert.IsEmpty(_kc.Clients);
            Assert.IsEmpty(await Admin().ListMachineClientsAsync());
        }

        [TestMethod]
        public async Task Ids_are_validated_before_they_become_path_segments()
        {
            foreach (var id in new[] { "", "..", "a/b", "a\\b" })
            {
                Assert.AreEqual(IdentityAdminErrorKind.Invalid, (await Fails(() => Admin().RotateMachineClientSecretAsync(id))).Kind);
                Assert.AreEqual(IdentityAdminErrorKind.Invalid, (await Fails(() => Admin().DeleteMachineClientAsync(id))).Kind);
            }
            Assert.IsEmpty(_kc.Requests);
        }

        [TestMethod]
        public async Task The_secret_never_appears_in_errors()
        {
            var created = await Admin().CreateMachineClientAsync("ci", Array.Empty<ConfigGrant>());
            _kc.Intercept = r => r.Method == HttpMethod.Post && r.RequestUri.AbsolutePath.EndsWith("/client-secret")
                ? FakeKeycloak.Json(HttpStatusCode.InternalServerError, new { errorMessage = "boom" })
                : null;

            var ex = await Fails(() => Admin().RotateMachineClientSecretAsync(created.Client.Id));

            Assert.AreEqual(IdentityAdminErrorKind.Unavailable, ex.Kind);
            Assert.IsFalse(ex.Message.Contains(created.Secret));
        }
    }
}
