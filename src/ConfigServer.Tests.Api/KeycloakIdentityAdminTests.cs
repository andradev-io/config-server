using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ConfigServer.Application.Api.IdentityAdmin;
using ConfigServer.Domain.Security;
using ConfigServer.Domain.Security.Identity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    /// <summary>KeycloakIdentityAdmin against an in-memory Keycloak (fake HttpMessageHandler, no network).</summary>
    [TestClass]
    public class KeycloakIdentityAdminTests
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
            _kc.AddUser("u1", "alice", "Alice", "Liddell", "alice@example.com");
        }

        private static KeycloakAdminOptions Options(string secret = FakeKeycloak.AdminSecret) => new KeycloakAdminOptions
        {
            BaseUrl = FakeKeycloak.BaseUrl,
            Realm = FakeKeycloak.Realm,
            AdminClientId = FakeKeycloak.AdminClientId,
            AdminClientSecret = secret,
            ConfigClientId = FakeKeycloak.ConfigClientId
        };

        /// <summary>A new instance per call (typed clients are transient); the cache is the shared singleton.</summary>
        private KeycloakIdentityAdmin Admin(KeycloakAdminOptions options = null)
            => new KeycloakIdentityAdmin(new HttpClient(_kc, disposeHandler: false), options ?? Options(), _cache, _clock);

        private static ConfigGrant G(AccessPermission permission, string app = "*", string environment = "*")
            => ConfigGrant.Create(permission, app, environment);

        [TestMethod]
        public async Task Info_needs_no_network()
        {
            var info = await Admin().GetInfoAsync();

            Assert.AreEqual("keycloak", info.Provider);
            Assert.IsTrue(info.Enabled);
            Assert.AreEqual(FakeKeycloak.ConfigClientId, info.ConfigClientId);
            StringAssert.Contains(info.Note, "next token refresh");
            Assert.IsEmpty(_kc.Requests);
        }

        [TestMethod]
        public async Task Fetches_a_client_credentials_token_once_and_reuses_it_and_the_client_id()
        {
            await Admin().GetGrantsAsync(SubjectType.User, "u1");
            await Admin().GetGrantsAsync(SubjectType.User, "u1");
            await Admin().SearchUsersAsync(null, 0, 10);

            Assert.AreEqual(1, _kc.TokensIssued);
            var token = _kc.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/protocol/openid-connect/token"));
            Assert.AreEqual(HttpMethod.Post, token.Method);
            StringAssert.Contains(token.Body, "grant_type=client_credentials");
            StringAssert.Contains(token.Body, "client_id=" + FakeKeycloak.AdminClientId);
            Assert.IsTrue(_kc.AdminCalls.All(r => r.Authorization == "Bearer tok-1"));
            Assert.AreEqual(1, _kc.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/clients")), "the config client id is resolved once");
            StringAssert.Contains(_kc.Requests.First(r => r.Uri.AbsolutePath.EndsWith("/clients")).Uri.Query, "clientId=config-web");
        }

        [TestMethod]
        public async Task Refreshes_the_token_shortly_before_it_expires()
        {
            _kc.ExpiresIn = 300;
            await Admin().SearchUsersAsync(null, 0, 10);

            _clock.Now = _clock.Now.AddSeconds(260);
            await Admin().SearchUsersAsync(null, 0, 10);
            Assert.AreEqual(1, _kc.TokensIssued, "still valid for more than the 30 s margin");

            _clock.Now = _clock.Now.AddSeconds(15);
            await Admin().SearchUsersAsync(null, 0, 10);
            Assert.AreEqual(2, _kc.TokensIssued, "refreshed within 30 s of expiry");
        }

        [TestMethod]
        public async Task Refreshes_the_token_once_on_401_and_retries()
        {
            await Admin().SearchUsersAsync(null, 0, 10);
            _kc.ValidTokens.Clear(); // e.g. Keycloak restarted or the session was revoked

            var users = await Admin().SearchUsersAsync(null, 0, 10);

            Assert.AreEqual(1, users.Count);
            Assert.AreEqual(2, _kc.TokensIssued);
            Assert.AreEqual("Bearer tok-2", _kc.AdminCalls.Last().Authorization);
        }

        [TestMethod]
        public async Task Grant_creates_the_missing_role_with_an_encoded_name_and_maps_it()
        {
            var granted = await Admin().GrantAsync(SubjectType.User, "u1", G(AccessPermission.Write, "web", "dev"));

            Assert.AreEqual(new SubjectGrant("write", "web", "dev", "config:write:web:dev", false), granted);
            Assert.IsTrue(_kc.Roles.ContainsKey("config:write:web:dev"));
            CollectionAssert.AreEqual(new[] { "config:write:web:dev" }, _kc.UserMappings["u1"]);

            // ':' must travel escaped inside the single role-name path segment.
            var lookups = _kc.Requests.Where(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.Contains("/roles/")).ToList();
            Assert.AreEqual(2, lookups.Count, "looked up before (404) and after creation");
            Assert.IsTrue(lookups.All(r => r.Uri.AbsoluteUri.EndsWith($"/clients/{FakeKeycloak.ClientUuid}/roles/config%3Awrite%3Aweb%3Adev", StringComparison.Ordinal)),
                lookups[0].Uri.AbsoluteUri);
            var create = _kc.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/roles"));
            StringAssert.Contains(create.Body, "\"name\":\"config:write:web:dev\"");
            var map = _kc.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/role-mappings/"));
            StringAssert.EndsWith(map.Uri.AbsolutePath, $"/users/u1/role-mappings/clients/{FakeKeycloak.ClientUuid}");
            StringAssert.Contains(map.Body, "\"id\":\"" + _kc.Roles["config:write:web:dev"] + "\"");
        }

        [TestMethod]
        public async Task Grant_encodes_wildcard_role_names()
        {
            await Admin().GrantAsync(SubjectType.User, "u1", G(AccessPermission.Read, "*", "prod"));

            Assert.IsTrue(_kc.Roles.ContainsKey("config:read:*:prod"));
            Assert.IsTrue(_kc.Requests.Any(r => r.Uri.AbsoluteUri.EndsWith("/roles/config%3Aread%3A%2A%3Aprod", StringComparison.Ordinal)
                                                || r.Uri.AbsoluteUri.EndsWith("/roles/config%3Aread%3A*%3Aprod", StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task Grant_reuses_an_existing_role_and_is_idempotent()
        {
            _kc.AddRole("config:read:web");

            await Admin().GrantAsync(SubjectType.User, "u1", G(AccessPermission.Read, "web"));
            var again = await Admin().GrantAsync(SubjectType.User, "u1", G(AccessPermission.Read, "web"));

            Assert.AreEqual("config:read:web", again.Role);
            Assert.AreEqual(0, _kc.Count(HttpMethod.Post, "/roles"), "no role created");
            Assert.AreEqual(1, _kc.Count(HttpMethod.Post, "/role-mappings/"), "mapped once");
            CollectionAssert.AreEqual(new[] { "config:read:web" }, _kc.UserMappings["u1"]);
        }

        [TestMethod]
        public async Task Grant_accepts_an_existing_mapping_spelled_differently()
        {
            _kc.AddRole("config:read:web:*");
            _kc.UserMappings["u1"].Add("config:read:web:*");

            var granted = await Admin().GrantAsync(SubjectType.User, "u1", G(AccessPermission.Read, "web"));

            Assert.AreEqual("config:read:web:*", granted.Role);
            Assert.IsFalse(_kc.Roles.ContainsKey("config:read:web"));
            Assert.AreEqual(0, _kc.Count(HttpMethod.Post, "/role-mappings/"));
        }

        [TestMethod]
        public async Task Grant_to_a_group_maps_the_group()
        {
            _kc.AddGroup("g1", "billing-devs");

            await Admin().GrantAsync(SubjectType.Group, "g1", G(AccessPermission.Write, "web"));

            CollectionAssert.AreEqual(new[] { "config:write:web" }, _kc.GroupMappings["g1"]);
            Assert.IsTrue(_kc.Requests.Any(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith($"/groups/g1/role-mappings/clients/{FakeKeycloak.ClientUuid}")));
        }

        [TestMethod]
        public async Task Revoke_unmaps_every_spelling_keeps_the_role_and_is_idempotent()
        {
            _kc.AddRole("config:read:web");
            _kc.AddRole("config:read:web:*");
            _kc.AddRole("config:write:web");
            _kc.UserMappings["u1"].AddRange(new[] { "config:read:web", "config:read:web:*", "config:write:web" });

            Assert.IsTrue(await Admin().RevokeAsync(SubjectType.User, "u1", G(AccessPermission.Read, "web")));

            CollectionAssert.AreEqual(new[] { "config:write:web" }, _kc.UserMappings["u1"]);
            Assert.IsTrue(_kc.Roles.ContainsKey("config:read:web"), "the role itself is kept");
            var delete = _kc.Requests.Single(r => r.Method == HttpMethod.Delete);
            StringAssert.Contains(delete.Body, "\"name\":\"config:read:web\"");
            StringAssert.Contains(delete.Body, "\"name\":\"config:read:web:*\"");

            Assert.IsFalse(await Admin().RevokeAsync(SubjectType.User, "u1", G(AccessPermission.Read, "web")));
            Assert.AreEqual(1, _kc.Requests.Count(r => r.Method == HttpMethod.Delete), "nothing left to unmap");
        }

        [TestMethod]
        public async Task User_grants_include_ones_inherited_from_groups_and_parent_groups()
        {
            foreach (var role in new[] { "config:read:api", "config:write:web:dev", "config:read", "uma_protection", "config:admin:web" })
                _kc.AddRole(role);
            _kc.AddGroup("root", "engineering");
            _kc.AddGroup("child", "web-team", parentId: "root");
            _kc.AddGroup("other", "ops");
            _kc.UserGroups["u1"].AddRange(new[] { "child", "other" });
            _kc.UserMappings["u1"].AddRange(new[] { "config:read:api", "uma_protection" });
            _kc.GroupMappings["child"].AddRange(new[] { "config:write:web:dev", "config:admin:web" });
            _kc.GroupMappings["root"].Add("config:read");

            var grants = await Admin().GetGrantsAsync(SubjectType.User, "u1");

            CollectionAssert.AreEqual(new[]
            {
                new SubjectGrant("read", "api", "*", "config:read:api", false),
                new SubjectGrant("read", "*", "*", "config:read", true, "engineering"),
                new SubjectGrant("write", "web", "dev", "config:write:web:dev", true, "web-team")
            }, grants.ToArray());
        }

        [TestMethod]
        public async Task Group_grants_are_its_direct_mappings_only()
        {
            _kc.AddRole("config:read");
            _kc.AddGroup("g1", "team");
            _kc.GroupMappings["g1"].Add("config:read");

            var grants = await Admin().GetGrantsAsync(SubjectType.Group, "g1");

            Assert.AreEqual(new SubjectGrant("read", "*", "*", "config:read", false), grants.Single());
            Assert.AreEqual(0, _kc.Count(HttpMethod.Get, "/groups/g1/groups"));
        }

        [TestMethod]
        public async Task Searches_users_and_maps_names()
        {
            _kc.AddUser("u2", "bob");

            var users = await Admin().SearchUsersAsync("ALI", 0, 5);

            Assert.AreEqual(new IdentityUser("u1", "alice", "Alice Liddell", "alice@example.com"), users.Single());
            var call = _kc.AdminCalls.Last();
            StringAssert.Contains(call.Uri.Query, "search=ALI");
            StringAssert.Contains(call.Uri.Query, "first=0");
            StringAssert.Contains(call.Uri.Query, "max=5");
            Assert.IsNull((await Admin().SearchUsersAsync("bob", 0, 5)).Single().DisplayName);
        }

        [TestMethod]
        public async Task Searches_groups_flattening_the_tree()
        {
            _kc.AddGroup("root", "engineering");
            _kc.AddGroup("child", "web-team", parentId: "root");
            _kc.AddGroup("ops", "ops");

            var all = await Admin().SearchGroupsAsync(null, 0, 20);
            var matches = await Admin().SearchGroupsAsync("web", 0, 20);

            CollectionAssert.AreEquivalent(new[] { "/engineering", "/engineering/web-team", "/ops" }, all.Select(g => g.Path).ToArray());
            Assert.AreEqual(new IdentityGroup("child", "web-team", "/engineering/web-team"), matches.Single());
        }

        [TestMethod]
        public async Task Bad_client_secret_is_reported_without_leaking_it()
        {
            var ex = await Assert.ThrowsAsync<IdentityAdminException>(() => Admin(Options("wrong-secret")).SearchUsersAsync(null, 0, 10));

            Assert.AreEqual(IdentityAdminErrorKind.Unauthorized, ex.Kind);
            StringAssert.Contains(ex.Message, FakeKeycloak.AdminClientId);
            StringAssert.Contains(ex.Message, "invalid_client");
            Assert.IsFalse(ex.Message.Contains("wrong-secret"), ex.Message);
            Assert.IsFalse(ex.Message.Contains(FakeKeycloak.AdminSecret), ex.Message);
        }

        [TestMethod]
        [DataRow("/role-mappings/", "POST", "manage-users")]
        [DataRow("/roles", "POST", "manage-clients")]
        [DataRow("/clients", "GET", "view-clients")]
        public async Task Missing_realm_management_roles_are_named(string pathPart, string method, string role)
        {
            _kc.Intercept = r => r.Method.Method == method && r.RequestUri.AbsolutePath.Contains(pathPart) && r.RequestUri.AbsolutePath.StartsWith("/admin/")
                ? FakeKeycloak.Json(HttpStatusCode.Forbidden, new { error = "HTTP 403 Forbidden" })
                : null;

            var ex = await Assert.ThrowsAsync<IdentityAdminException>(() => Admin().GrantAsync(SubjectType.User, "u1", G(AccessPermission.Read, "web")));

            Assert.AreEqual(IdentityAdminErrorKind.Denied, ex.Kind);
            StringAssert.Contains(ex.Message, "lacks permission");
            StringAssert.Contains(ex.Message, $"realm-management role '{role}'");
        }

        [TestMethod]
        public async Task Unknown_subjects_are_not_found_and_nothing_is_created()
        {
            var ex = await Assert.ThrowsAsync<IdentityAdminException>(() => Admin().GrantAsync(SubjectType.User, "nobody", G(AccessPermission.Read)));

            Assert.AreEqual(IdentityAdminErrorKind.NotFound, ex.Kind);
            Assert.AreEqual("User 'nobody' was not found.", ex.Message);
            Assert.IsEmpty(_kc.Roles);

            var group = await Assert.ThrowsAsync<IdentityAdminException>(() => Admin().GetGrantsAsync(SubjectType.Group, "nogroup"));
            Assert.AreEqual(IdentityAdminErrorKind.NotFound, group.Kind);
            StringAssert.StartsWith(group.Message, "Group 'nogroup'");
        }

        [TestMethod]
        public async Task Missing_config_client_is_a_configuration_error()
        {
            _kc.ConfigClientExists = false;

            var ex = await Assert.ThrowsAsync<IdentityAdminException>(() => Admin().GetGrantsAsync(SubjectType.User, "u1"));

            Assert.AreEqual(IdentityAdminErrorKind.Misconfigured, ex.Kind);
            StringAssert.Contains(ex.Message, "Client 'config-web' was not found in realm 'acme'");
        }

        [TestMethod]
        public async Task Unreachable_keycloak_is_unavailable()
        {
            _kc.Intercept = _ => throw new HttpRequestException("Connection refused (kc.test:80)");

            var ex = await Assert.ThrowsAsync<IdentityAdminException>(() => Admin().SearchUsersAsync(null, 0, 10));

            Assert.AreEqual(IdentityAdminErrorKind.Unavailable, ex.Kind);
            StringAssert.Contains(ex.Message, FakeKeycloak.BaseUrl);
        }

        [TestMethod]
        public async Task Escapes_subject_ids_and_refuses_path_segments()
        {
            _kc.AddUser("f:ldap:bob");

            await Admin().GetGrantsAsync(SubjectType.User, "f:ldap:bob");
            Assert.IsTrue(_kc.AdminCalls.Any(r => r.Uri.AbsoluteUri.Contains("/users/f%3Aldap%3Abob/role-mappings/")));

            var ex = await Assert.ThrowsAsync<IdentityAdminException>(() => Admin().GetGrantsAsync(SubjectType.User, ".."));
            Assert.AreEqual(IdentityAdminErrorKind.Invalid, ex.Kind);
        }
    }
}
