using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ConfigServer.Domain.Configuration.Formats;
using ConfigServer.Domain.Configuration.Management;
using ConfigServer.Domain.Security;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Domain
{
    [TestClass]
    public class AccessGrantsTests
    {
        private const AccessPermission R = AccessPermission.Read;
        private const AccessPermission W = AccessPermission.Write;
        private const AccessPermission A = AccessPermission.Admin;

        private static AccessGrants Roles(params string[] roles) => AccessGrants.FromRoles(roles);

        [TestMethod]
        [DataRow("config:read", AccessPermission.Read, "*", "*")]
        [DataRow("config:write:billing-service", AccessPermission.Write, "billing-service", "*")]
        [DataRow("config:read:billing-service:prd", AccessPermission.Read, "billing-service", "prd")]
        [DataRow("config:write:*:dev", AccessPermission.Write, "*", "dev")]
        [DataRow("config:read:*:*", AccessPermission.Read, "*", "*")]
        [DataRow("config:admin", AccessPermission.Admin, "*", "*")]
        [DataRow("config:admin:*:*", AccessPermission.Admin, "*", "*")]
        [DataRow("  Config:WRITE:web.api:qa_1  ", AccessPermission.Write, "web.api", "qa_1")]
        public void Parses_valid_roles(string role, AccessPermission permission, string app, string environment)
        {
            Assert.AreEqual(RoleParseStatus.Valid, AccessGrants.ParseRole(role, out var grant, out _));
            Assert.AreEqual(new AccessGrant(permission, app, environment), grant);
        }

        [TestMethod]
        [DataRow("offline_access", RoleParseStatus.NotConfigRole)]
        [DataRow("configurator", RoleParseStatus.NotConfigRole)]
        [DataRow("", RoleParseStatus.NotConfigRole)]
        [DataRow("config", RoleParseStatus.Invalid)]
        [DataRow("config:", RoleParseStatus.Invalid)]
        [DataRow("config:owner", RoleParseStatus.Invalid)]
        [DataRow("config:read:", RoleParseStatus.Invalid)]
        [DataRow("config:read:billing:", RoleParseStatus.Invalid)]
        [DataRow("config:read:billing:prd:extra", RoleParseStatus.Invalid)]
        [DataRow("config:read:../etc", RoleParseStatus.Invalid)]
        [DataRow("config:read:billing:default", RoleParseStatus.Invalid)]
        [DataRow("config:read:billing:p.rd", RoleParseStatus.Invalid)]
        [DataRow("config:read:bil ling", RoleParseStatus.Invalid)]
        [DataRow("config:admin:billing", RoleParseStatus.ScopedAdmin)]
        [DataRow("config:admin:*:prd", RoleParseStatus.ScopedAdmin)]
        public void Rejects_invalid_roles(string role, RoleParseStatus expected)
        {
            Assert.AreEqual(expected, AccessGrants.ParseRole(role, out var grant, out var reason));
            Assert.IsNull(grant);
            Assert.IsFalse(string.IsNullOrEmpty(reason));
        }

        [TestMethod]
        public void From_roles_reports_ignored_config_roles_only()
        {
            var ignored = new List<(string Role, RoleParseStatus Status)>();

            var grants = AccessGrants.FromRoles(new[] { "offline_access", "config:admin:web", "config:bogus", "config:read:web" },
                (role, status, _) => ignored.Add((role, status)));

            CollectionAssert.AreEqual(new[] { ("config:admin:web", RoleParseStatus.ScopedAdmin), ("config:bogus", RoleParseStatus.Invalid) }, ignored);
            CollectionAssert.AreEqual(new[] { new AccessGrant(R, "web", "*") }, grants.Grants.ToArray());
            Assert.IsFalse(grants.HasAny(A));
        }

        [TestMethod]
        public void Write_implies_read_and_admin_implies_everything()
        {
            var writer = Roles("config:write:web:dev");
            Assert.IsTrue(writer.Can(R, "web", "dev"));
            Assert.IsTrue(writer.Can(W, "web", "dev"));
            Assert.IsFalse(writer.Can(A, "web", "dev"));

            var admin = Roles("config:admin");
            Assert.IsTrue(admin.Can(W, "anything", "prd"));
            Assert.IsTrue(admin.HasGlobal(A));
            Assert.IsTrue(admin.HasGlobal(W));
            CollectionAssert.AreEqual(new[] { ConfigRoles.Read, ConfigRoles.Write, ConfigRoles.Admin }, admin.Roles.ToArray());
        }

        [TestMethod]
        public void Wildcards_and_scopes()
        {
            var grants = Roles("config:write:billing-service:dev", "config:read:billing-service:stg", "config:read:*:qa");

            Assert.IsTrue(grants.Can(W, "billing-service", "dev"));
            Assert.IsFalse(grants.Can(W, "billing-service", "stg"));
            Assert.IsTrue(grants.Can(R, "billing-service", "stg"));
            Assert.IsFalse(grants.Can(R, "billing-service", "prd"));
            Assert.IsTrue(grants.Can(R, "other-app", "qa"));
            Assert.IsFalse(grants.Can(R, "other-app", "dev"));

            // "*" requests need a wildcard grant.
            Assert.IsFalse(grants.CanAllEnvironments(R, "billing-service"));
            Assert.IsFalse(grants.HasGlobal(R));
            Assert.IsTrue(grants.CanAnyEnvironment(W, "billing-service"));
            Assert.IsFalse(grants.CanAnyEnvironment(W, "other-app"));
            Assert.IsTrue(grants.CanAnyEnvironment(R, "other-app"));
            Assert.IsTrue(grants.CanAnyApp(R, "qa"));
            Assert.IsFalse(grants.CanAnyApp(R, "prd"));

            CollectionAssert.AreEqual(new[] { "billing-service", "x" }, grants.AllowedApps(new[] { "billing-service", "x" }).ToArray());
            CollectionAssert.AreEqual(new[] { "billing-service" }, grants.AllowedApps(new[] { "billing-service", "x" }, W).ToArray());
            CollectionAssert.AreEqual(new[] { "dev", "stg", "qa" },
                grants.AllowedEnvironments("billing-service", new[] { "dev", "stg", "prd", "qa" }).ToArray());
            CollectionAssert.AreEqual(new[] { ConfigRoles.Read, ConfigRoles.Write }, grants.Roles.ToArray());
        }

        [TestMethod]
        public void App_wide_grant_covers_every_environment_of_that_app_only()
        {
            var grants = Roles("config:read:demo-app");

            Assert.IsTrue(grants.Can(R, "demo-app", "prd"));
            Assert.IsTrue(grants.CanAllEnvironments(R, "demo-app"));
            Assert.IsFalse(grants.Can(R, "billing", "prd"));
            Assert.IsFalse(grants.HasGlobal(R));
            Assert.IsFalse(grants.HasAny(W));
        }

        [TestMethod]
        public void Default_file_rule_read_with_any_environment_write_needs_all()
        {
            var scoped = Roles("config:write:web:dev");
            Assert.IsTrue(scoped.CanReadDefault("web"));
            Assert.IsFalse(scoped.CanWriteDefault("web"));
            Assert.IsFalse(scoped.CanReadDefault("api"));

            var appWide = Roles("config:write:web");
            Assert.IsTrue(appWide.CanWriteDefault("web"));
            Assert.IsFalse(appWide.CanWriteDefault("api"));
            Assert.IsTrue(Roles("config:write").CanWriteDefault("api"));
            Assert.IsFalse(Roles("config:write:*:dev").CanWriteDefault("api"));
        }

        [TestMethod]
        public void Names_are_case_sensitive_for_roles()
        {
            var grants = Roles("config:read:Web:Dev");

            Assert.IsTrue(grants.Can(R, "Web", "Dev"));
            Assert.IsFalse(grants.Can(R, "web", "Dev"));
            Assert.IsFalse(grants.Can(R, "Web", "dev"));
            Assert.IsFalse(grants.Can(R, null, "Dev"));
            Assert.IsFalse(grants.Can(R, "Web", null));
        }

        [TestMethod]
        public void Grants_are_deduplicated_and_covered_ones_dropped()
        {
            var grants = Roles("config:read", "config:read:web:dev", "config:write:web:dev", "config:write:web:dev", "config:write:web", "config:read:api");

            CollectionAssert.AreEqual(new[] { new AccessGrant(R, "*", "*"), new AccessGrant(W, "web", "*") }, grants.Grants.ToArray());
            Assert.AreEqual("config:write:web:*", grants.Grants[1].ToRole());
        }

        [TestMethod]
        public void Scoped_admin_passed_directly_is_dropped()
        {
            var grants = new AccessGrants(new[] { new AccessGrant(A, "web", "*"), new AccessGrant(R, "web", "dev") });

            CollectionAssert.AreEqual(new[] { new AccessGrant(R, "web", "dev") }, grants.Grants.ToArray());
            Assert.IsTrue(AccessGrants.GlobalAdmin.HasGlobal(A));
            Assert.IsTrue(AccessGrants.None.IsEmpty);
            Assert.IsEmpty(AccessGrants.None.Roles);
        }

        [TestMethod]
        public void Api_key_scope_becomes_grants_never_admin_and_ignores_environment_case()
        {
            var key = new ApiKey { Apps = new[] { "web", "api" }, Environments = new[] { "prod", "dev" }, Permission = ApiKeyPermission.Write };

            var grants = AccessGrants.FromApiKey(key);

            Assert.AreEqual(4, grants.Grants.Count);
            Assert.IsTrue(grants.Grants.All(g => g.Permission == W));
            Assert.IsTrue(grants.Can(W, "web", "PROD"));
            Assert.IsFalse(grants.Can(W, "WEB", "prod"));
            Assert.IsFalse(grants.Can(W, "web", "qa"));
            Assert.IsFalse(grants.CanWriteDefault("web"));
            Assert.IsTrue(grants.IgnoresEnvironmentCase);

            var unrestricted = AccessGrants.FromApiKey(new ApiKey());
            CollectionAssert.AreEqual(new[] { new AccessGrant(R, "*", "*") }, unrestricted.Grants.ToArray());
            Assert.IsFalse(unrestricted.HasAny(A));
            Assert.IsFalse(AccessGrants.FromApiKey(new ApiKey { Permission = "admin" }).HasAny(W));
            Assert.IsTrue(AccessGrants.FromApiKey(new ApiKey { Apps = Array.Empty<string>() }).IsEmpty);
        }

        [TestMethod]
        public void Snapshot_is_restricted_to_readable_apps_and_environments()
        {
            static ParsedFile File(string name) => new ParsedFile { Name = name, Format = ConfigFormat.Json, Tree = new JsonObject() };
            var snapshot = new ConfigSnapshot
            {
                Apps = new[]
                {
                    new AppSnapshot
                    {
                        Name = "web", Default = File("default.json"),
                        Overrides = new[] { new EnvironmentFile { Environment = "dev", File = File("dev.json") }, new EnvironmentFile { Environment = "prd", File = File("prd.json") } }
                    },
                    new AppSnapshot { Name = "api", Default = File("default.json") }
                },
                Warnings = new[]
                {
                    new SnapshotWarning(null, "_environments.json", "x"), new SnapshotWarning("web", "prd.json", "x"),
                    new SnapshotWarning("web", "dev.json", "x"), new SnapshotWarning("web", "_variables.json", "x"), new SnapshotWarning("api", "default.json", "x")
                }
            };

            var restricted = SnapshotAccess.Restrict(snapshot, Roles("config:read:web:dev"));

            Assert.AreEqual("web", restricted.Apps.Single().Name);
            CollectionAssert.AreEqual(new[] { "dev" }, restricted.Apps.Single().Overrides.Select(o => o.Environment).ToArray());
            Assert.IsNotNull(restricted.Apps.Single().Default);
            CollectionAssert.AreEqual(new[] { "_environments.json", "dev.json", "_variables.json" }, restricted.Warnings.Select(w => w.File).ToArray());
            Assert.AreSame(snapshot, SnapshotAccess.Restrict(snapshot, Roles("config:read")));
        }
    }
}
