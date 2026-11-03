using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ConfigServer.Domain.Security;
using ConfigServer.Domain.Security.Identity;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Domain
{
    [TestClass]
    public class ConfigGrantTests
    {
        [TestMethod]
        [DataRow("read", null, null, "config:read")]
        [DataRow("read", "*", "*", "config:read")]
        [DataRow("READ", "", "", "config:read")]
        [DataRow("write", "billing-service", null, "config:write:billing-service")]
        [DataRow("write", "billing-service", "*", "config:write:billing-service")]
        [DataRow("read", "billing-service", "prd", "config:read:billing-service:prd")]
        [DataRow("write", "*", "dev", "config:write:*:dev")]
        [DataRow("write", "web.api", "qa_1", "config:write:web.api:qa_1")]
        [DataRow("admin", null, null, "config:admin")]
        [DataRow(" Admin ", "*", "*", "config:admin")]
        public void Builds_the_shortest_role_name_and_parses_it_back(string permission, string app, string environment, string expected)
        {
            Assert.IsTrue(ConfigGrant.TryCreate(permission, app, environment, out var grant, out var error), error);
            Assert.AreEqual(expected, grant.RoleName);
            Assert.IsNull(error);

            // Single source of truth: the role parser reads back exactly this grant.
            Assert.AreEqual(RoleParseStatus.Valid, AccessGrants.ParseRole(grant.RoleName, out var parsed, out _));
            Assert.AreEqual(grant.ToAccessGrant(), parsed);
            Assert.AreEqual(string.IsNullOrEmpty(app) ? "*" : app, grant.App);
            Assert.AreEqual(string.IsNullOrEmpty(environment) ? "*" : environment, grant.Environment);
        }

        [TestMethod]
        [DataRow(null, null, null, "required")]
        [DataRow("", "web", null, "required")]
        [DataRow("owner", null, null, "Unknown permission")]
        [DataRow("read", "../etc", null, "Invalid application name")]
        [DataRow("read", "bil ling", null, "Invalid application name")]
        [DataRow("read", "web:prd", null, "Invalid application name")]
        [DataRow("read", "web", "default", "Invalid environment name")]
        [DataRow("read", "web", "p.rd", "Invalid environment name")]
        [DataRow("read", "web", "dev:x", "Invalid environment name")]
        [DataRow("read", " web", null, "Invalid application name")]
        [DataRow("admin", "web", null, "global only")]
        [DataRow("admin", "*", "dev", "global only")]
        [DataRow("admin", "web", "dev", "global only")]
        public void Rejects_invalid_grants(string permission, string app, string environment, string message)
        {
            Assert.IsFalse(ConfigGrant.TryCreate(permission, app, environment, out var grant, out var error));
            Assert.IsNull(grant);
            StringAssert.Contains(error, message);
        }

        [TestMethod]
        public void Rejects_role_names_longer_than_keycloak_allows()
        {
            var app = "a" + new string('b', ConfigGrant.MaxRoleNameLength);

            Assert.IsFalse(ConfigGrant.TryCreate("read", app, null, out _, out var error));
            StringAssert.Contains(error, "255");
        }

        [TestMethod]
        public void Every_built_role_starts_with_the_config_prefix()
        {
            foreach (var permission in new[] { "read", "write" })
                foreach (var app in new[] { "*", "web" })
                    foreach (var environment in new[] { "*", "dev" })
                    {
                        Assert.IsTrue(ConfigGrant.TryCreate(permission, app, environment, out var grant, out _));
                        StringAssert.StartsWith(grant.RoleName, "config:");
                    }
        }

        [TestMethod]
        [DataRow("config:read:web", AccessPermission.Read, "web", "*")]
        [DataRow("Config:READ:web:*", AccessPermission.Read, "web", "*")]
        [DataRow("config:write:*:dev", AccessPermission.Write, "*", "dev")]
        [DataRow("config:read:*:*", AccessPermission.Read, "*", "*")]
        [DataRow("config:admin", AccessPermission.Admin, "*", "*")]
        public void Reads_grants_from_existing_role_names(string role, AccessPermission permission, string app, string environment)
        {
            Assert.IsTrue(ConfigGrant.TryFromRoleName(role, out var grant));
            Assert.AreEqual(ConfigGrant.Create(permission, app, environment), grant);
        }

        [TestMethod]
        [DataRow("offline_access")]
        [DataRow("uma_protection")]
        [DataRow("config:owner")]
        [DataRow("config:read:../x")]
        [DataRow("config:admin:web")]
        [DataRow("")]
        [DataRow(null)]
        public void Ignores_role_names_that_are_not_valid_grants(string role)
        {
            Assert.IsFalse(ConfigGrant.TryFromRoleName(role, out var grant));
            Assert.IsNull(grant);
            Assert.IsNull(SubjectGrant.FromRoleName(role));
        }

        [TestMethod]
        public void Matches_any_spelling_of_the_same_grant_but_not_other_names()
        {
            var grant = ConfigGrant.Create(AccessPermission.Read, "web");

            Assert.IsTrue(grant.MatchesRoleName("config:read:web"));
            Assert.IsTrue(grant.MatchesRoleName("config:read:web:*"));
            Assert.IsTrue(grant.MatchesRoleName("CONFIG:Read:web"));
            Assert.IsFalse(grant.MatchesRoleName("config:read:Web"), "app names are case-sensitive");
            Assert.IsFalse(grant.MatchesRoleName("config:write:web"));
            Assert.IsFalse(grant.MatchesRoleName("config:read"));
            Assert.IsFalse(grant.MatchesRoleName("config:read:web:dev"));
        }

        [TestMethod]
        public void Create_throws_on_invalid_grants()
        {
            Assert.Throws<ArgumentException>(() => ConfigGrant.Create(AccessPermission.Admin, "web"));
            Assert.Throws<ArgumentException>(() => ConfigGrant.Create(AccessPermission.Read, "../x"));
        }

        [TestMethod]
        public void Subject_grants_keep_the_stored_role_name_and_collect_dedupes_and_orders()
        {
            var grants = SubjectGrant.Collect(new[]
            {
                SubjectGrant.FromRoleName("config:read", inherited: true, via: "everyone"),
                SubjectGrant.FromRoleName("offline_access"),
                SubjectGrant.FromRoleName("Config:WRITE:web:dev"),
                SubjectGrant.FromRoleName("Config:WRITE:web:dev"),
                SubjectGrant.FromRoleName("config:read:api")
            });

            CollectionAssert.AreEqual(new[] { "config:read:api", "Config:WRITE:web:dev", "config:read" }, grants.Select(g => g.Role).ToArray());
            Assert.AreEqual(new SubjectGrant("write", "web", "dev", "Config:WRITE:web:dev", false), grants[1]);
            Assert.AreEqual(new SubjectGrant("read", "*", "*", "config:read", true, "everyone"), grants[2]);
            Assert.IsNull(SubjectGrant.FromRoleName("config:read:api", inherited: false, via: "ignored").Via);
        }

        [TestMethod]
        public void Subject_grant_json_omits_via_for_direct_grants()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

            var direct = JsonSerializer.Serialize(SubjectGrant.FromRoleName("config:read:web"), options);
            var inherited = JsonSerializer.Serialize(SubjectGrant.FromRoleName("config:read:web", true, "team"), options);

            Assert.AreEqual("{\"permission\":\"read\",\"app\":\"web\",\"environment\":\"*\",\"role\":\"config:read:web\",\"inherited\":false}", direct);
            StringAssert.Contains(inherited, "\"via\":\"team\"");
        }

        [TestMethod]
        [DataRow("user", true, SubjectType.User)]
        [DataRow("GROUP", true, SubjectType.Group)]
        [DataRow("robot", false, SubjectType.User)]
        [DataRow(null, false, SubjectType.User)]
        public void Parses_subject_types(string value, bool ok, SubjectType expected)
        {
            Assert.AreEqual(ok, SubjectTypes.TryParse(value, out var type));
            if (ok) Assert.AreEqual(expected, type);
        }

        [TestMethod]
        [DataRow("4f8a2a3e-1b2c-4d5e-8f90-123456789abc", true)]
        [DataRow("f:ldap-1:alice", true)]
        [DataRow("", false)]
        [DataRow(" ", false)]
        [DataRow(".", false)]
        [DataRow("..", false)]
        [DataRow("a/b", false)]
        [DataRow("a\\b", false)]
        [DataRow("a\nb", false)]
        public void Validates_subject_ids(string id, bool valid)
            => Assert.AreEqual(valid, IdentitySubjects.IsValidId(id));

        [TestMethod]
        public async Task Disabled_admin_reports_none_and_refuses_operations()
        {
            var admin = new DisabledIdentityAdmin();

            var info = await admin.GetInfoAsync();
            Assert.AreEqual("none", info.Provider);
            Assert.IsFalse(info.Enabled);
            Assert.IsNull(info.ConfigClientId);
            StringAssert.Contains(info.Note, "IdentityAdmin:Provider");

            var ex = await Assert.ThrowsAsync<IdentityAdminException>(() => admin.SearchUsersAsync(null, 0, 10));
            Assert.AreEqual(IdentityAdminErrorKind.NotConfigured, ex.Kind);
            await Assert.ThrowsAsync<IdentityAdminException>(() => admin.GrantAsync(SubjectType.User, "u", ConfigGrant.Create(AccessPermission.Read)));
        }
    }
}
