using System.Collections.Generic;
using System.Security.Claims;
using ConfigServer.Domain.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Domain
{
    [TestClass]
    public class RoleClaimResolverTests
    {
        private static Claim C(string type, string value) => new Claim(type, value);

        [TestMethod]
        public void Repeated_flat_claim_yields_each_value_whole()
        {
            var claims = new[] { C("roles", "config-admins"), C("roles", "two words"), C("sub", "u1") };

            CollectionAssert.AreEqual(new[] { "config-admins", "two words" }, (List<string>)RoleClaimResolver.Extract(claims, "roles"));
        }

        [TestMethod]
        public void Single_string_claim_is_space_separated()
        {
            var claims = new[] { C("groups", "readers  writers\tadmins") };

            CollectionAssert.AreEqual(new[] { "readers", "writers", "admins" }, (List<string>)RoleClaimResolver.Extract(claims, "groups"));
        }

        [TestMethod]
        public void Json_array_claim_value_is_expanded()
        {
            var claims = new[] { C("roles", "[\"a\",\"b c\"]") };

            CollectionAssert.AreEqual(new[] { "a", "b c" }, (List<string>)RoleClaimResolver.Extract(claims, "roles"));
        }

        [TestMethod]
        public void Keycloak_realm_and_client_roles_via_dotted_path()
        {
            var claims = new[]
            {
                C("realm_access", "{\"roles\":[\"offline_access\",\"cfg-admin\"]}"),
                C("resource_access", "{\"config.server\":{\"roles\":[\"writer\"]},\"account\":{\"roles\":[\"manage-account\"]}}")
            };

            CollectionAssert.AreEqual(new[] { "offline_access", "cfg-admin" }, (List<string>)RoleClaimResolver.Extract(claims, "realm_access.roles"));
            // Keys that contain dots ("config.server") are matched greedily.
            CollectionAssert.AreEqual(new[] { "writer" }, (List<string>)RoleClaimResolver.Extract(claims, "resource_access.config.server.roles"));
        }

        [TestMethod]
        public void Auth0_namespaced_claim_with_dots_and_slashes()
        {
            var claims = new[] { C("https://config.example.com/roles", "admin"), C("https://config.example.com/roles", "reader") };

            CollectionAssert.AreEqual(new[] { "admin", "reader" }, (List<string>)RoleClaimResolver.Extract(claims, "https://config.example.com/roles"));
        }

        [TestMethod]
        public void Nested_string_value_is_space_separated()
        {
            var claims = new[] { C("app_metadata", "{\"authorization\":{\"roles\":\"a b\"}}") };

            CollectionAssert.AreEqual(new[] { "a", "b" }, (List<string>)RoleClaimResolver.Extract(claims, "app_metadata.authorization.roles"));
        }

        [TestMethod]
        public void Missing_or_malformed_paths_yield_nothing()
        {
            var claims = new[] { C("realm_access", "{not json"), C("roles", "") };

            Assert.IsEmpty(RoleClaimResolver.Extract(claims, "realm_access.roles"));
            Assert.IsEmpty(RoleClaimResolver.Extract(claims, "roles"));
            Assert.IsEmpty(RoleClaimResolver.Extract(claims, "nope.roles"));
            Assert.IsEmpty(RoleClaimResolver.Extract(claims, ""));
            Assert.IsEmpty(RoleClaimResolver.Extract(null, "roles"));
        }

        [TestMethod]
        public void Map_applies_role_map_case_insensitively_and_hierarchy()
        {
            var map = new Dictionary<string, string> { ["cfg-writers"] = "config:write", ["ops"] = "config:read, config:admin" };

            CollectionAssert.AreEqual(new[] { ConfigRoles.Read, ConfigRoles.Write },
                (string[])RoleClaimResolver.MapToConfigRoles(new[] { "CFG-WRITERS", "unrelated" }, map));
            CollectionAssert.AreEqual(new[] { ConfigRoles.Read, ConfigRoles.Write, ConfigRoles.Admin },
                (string[])RoleClaimResolver.MapToConfigRoles(new[] { "ops" }, map));
        }

        [TestMethod]
        public void Map_passes_through_config_role_names_and_drops_unknown()
        {
            CollectionAssert.AreEqual(new[] { ConfigRoles.Read }, (string[])RoleClaimResolver.MapToConfigRoles(new[] { "config:read" }, null));
            CollectionAssert.AreEqual(new[] { ConfigRoles.Read, ConfigRoles.Write, ConfigRoles.Admin },
                (string[])RoleClaimResolver.MapToConfigRoles(new[] { "Config:Admin" }, new Dictionary<string, string>()));
            Assert.IsEmpty(RoleClaimResolver.MapToConfigRoles(new[] { "admin", "" }, null));
        }

        [TestMethod]
        public void Resolve_paths_splits_on_commas_and_replaces_client_id()
        {
            CollectionAssert.AreEqual(new[] { "roles", "realm_access.roles", "resource_access.config-web.roles" },
                (List<string>)RoleClaimResolver.ResolvePaths(" roles, realm_access.roles ,,resource_access.{clientId}.roles,roles", "config-web"));
            CollectionAssert.AreEqual(new[] { "resource_access.my.client.roles" },
                (List<string>)RoleClaimResolver.ResolvePaths("resource_access.{CLIENTID}.roles", " my.client "));
            // Without a client id the placeholder path is dropped (startup validation rejects that configuration).
            CollectionAssert.AreEqual(new[] { "roles" }, (List<string>)RoleClaimResolver.ResolvePaths("roles,resource_access.{clientId}.roles", null));
            Assert.IsEmpty(RoleClaimResolver.ResolvePaths("  ", "x"));
        }

        [TestMethod]
        public void Keycloak_client_roles_with_dashed_client_id_and_union_of_paths()
        {
            var claims = new[]
            {
                C("roles", "[\"config:read\",\"shared\"]"),
                C("realm_access", "{\"roles\":[\"offline_access\",\"shared\"]}"),
                C("resource_access", "{\"config-web\":{\"roles\":[\"config:write:billing-service:dev\"]},\"other-client\":{\"roles\":[\"nope\"]}}")
            };
            var paths = RoleClaimResolver.ResolvePaths("roles,realm_access.roles,resource_access.{clientId}.roles", "config-web");

            CollectionAssert.AreEqual(new[] { "config:read", "shared", "offline_access", "config:write:billing-service:dev" },
                (List<string>)RoleClaimResolver.Extract(claims, paths));
            // A comma list given directly to Extract is read the same way; a missing path contributes nothing.
            CollectionAssert.AreEqual(new[] { "config:write:billing-service:dev" },
                (List<string>)RoleClaimResolver.Extract(claims, "missing.roles, resource_access.config-web.roles"));
        }

        [TestMethod]
        public void Map_to_role_strings_passes_scoped_roles_and_role_map_targets()
        {
            var map = new Dictionary<string, string> { ["billing-devs"] = "config:write:billing-service:dev, config:read:billing-service:stg" };

            CollectionAssert.AreEqual(
                new[] { "config:read:web:prd", "config:write:billing-service:dev", "config:read:billing-service:stg", "Config:Admin" },
                (List<string>)RoleClaimResolver.MapToRoleStrings(new[] { " config:read:web:prd ", "BILLING-DEVS", "other", "Config:Admin" }, map));
            // The legacy global mapping keeps only global roles.
            CollectionAssert.AreEqual(new[] { ConfigRoles.Read, ConfigRoles.Write, ConfigRoles.Admin },
                (string[])RoleClaimResolver.MapToConfigRoles(new[] { "config:read:web:prd", "BILLING-DEVS", "Config:Admin" }, map));
            Assert.IsEmpty(RoleClaimResolver.MapToConfigRoles(new[] { "config:read:web:prd" }, map));
        }

        [TestMethod]
        public void Expand_applies_hierarchy()
        {
            CollectionAssert.AreEqual(new[] { ConfigRoles.Read, ConfigRoles.Write }, (string[])ConfigRoles.Expand(new[] { "config:write" }));
            CollectionAssert.AreEqual(new[] { ConfigRoles.Read }, (string[])ConfigRoles.Expand(new[] { "config:read", "x" }));
            Assert.IsEmpty(ConfigRoles.Expand(null));
        }
    }
}
