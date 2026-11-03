using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ConfigServer.Application.Api;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    /// <summary>Per-application/environment RBAC from token roles (config:&lt;permission&gt;[:&lt;app&gt;[:&lt;env&gt;]]).</summary>
    [TestClass]
    public class RbacApiTests
    {
        private const string Billing = "billing-service";
        private static readonly string[] DevWriter = { "config:write:billing-service:dev" };

        private AuthTestHost _host;

        [TestInitialize]
        public void Setup()
        {
            _host = new AuthTestHost();
            SeedBilling(_host.Dir);
        }

        [TestCleanup]
        public void Cleanup() => _host.Dispose();

        private static void SeedBilling(string dir)
        {
            var app = Path.Combine(dir, Billing);
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "default.json"), "{\"name\":\"billing\",\"timeout\":30}");
            File.WriteAllText(Path.Combine(app, "dev.json"), "{\"timeout\":10,\"debug\":true}");
            File.WriteAllText(Path.Combine(app, "stg.json"), "{\"timeout\":20}");
            File.WriteAllText(Path.Combine(app, "prd.json"), "{\"timeout\":60,\"prdOnly\":\"x\"}");
        }

        private Task<HttpResponseMessage> Send(HttpMethod method, string url, string[] roles, string json = null)
            => _host.Client.SendAsync(AuthTestHost.Request(method, url, bearer: _host.Token(roles), json: json));

        private async Task<HttpStatusCode> Status(HttpMethod method, string url, string[] roles, string json = null)
            => (await Send(method, url, roles, json)).StatusCode;

        private async Task<JsonDocument> Json(string url, string[] roles)
        {
            var response = await Send(HttpMethod.Get, url, roles);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, url + ": " + await response.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        private static string[] Strings(JsonElement array, string property = null)
            => array.EnumerateArray().Select(e => property == null ? e.GetString() : e.GetProperty(property).GetString()).ToArray();

        private static string[] Names(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).ToArray();

        [TestMethod]
        [DataRow("GET", "/config/billing-service/dev", HttpStatusCode.OK)]
        [DataRow("GET", "/config/billing-service/prd", HttpStatusCode.Forbidden)]
        [DataRow("GET", "/config/billing-service/stg", HttpStatusCode.Forbidden)]
        [DataRow("GET", "/config/web/dev", HttpStatusCode.Forbidden)]
        [DataRow("GET", "/api/apps/billing-service/files/default.json", HttpStatusCode.OK)]
        [DataRow("GET", "/api/apps/billing-service/files/dev.json", HttpStatusCode.OK)]
        [DataRow("GET", "/api/apps/billing-service/files/prd.json", HttpStatusCode.Forbidden)]
        [DataRow("GET", "/api/apps/web/files", HttpStatusCode.Forbidden)]
        [DataRow("GET", "/api/apps/web/files/default.json", HttpStatusCode.Forbidden)]
        [DataRow("PUT", "/api/apps/billing-service/files/dev.json", HttpStatusCode.NoContent)]
        [DataRow("PUT", "/api/apps/billing-service/files/default.json", HttpStatusCode.Forbidden)]
        [DataRow("PUT", "/api/apps/billing-service/files/prd.json", HttpStatusCode.Forbidden)]
        [DataRow("PUT", "/api/apps/billing-service/files/qa.json", HttpStatusCode.Forbidden)]
        [DataRow("PUT", "/api/apps/web/files/dev.json", HttpStatusCode.Forbidden)]
        [DataRow("DELETE", "/api/apps/billing-service/files/prd.json", HttpStatusCode.Forbidden)]
        [DataRow("DELETE", "/api/apps/billing-service/files/default.json", HttpStatusCode.Forbidden)]
        [DataRow("GET", "/api/apps/billing-service/variables", HttpStatusCode.OK)]
        [DataRow("PUT", "/api/apps/billing-service/variables", HttpStatusCode.Forbidden)]
        [DataRow("GET", "/api/apps/web/overrides", HttpStatusCode.Forbidden)]
        [DataRow("GET", "/api/apps/web/status", HttpStatusCode.Forbidden)]
        [DataRow("POST", "/api/environments", HttpStatusCode.Forbidden)]
        [DataRow("PUT", "/api/environments/dev", HttpStatusCode.Forbidden)]
        [DataRow("DELETE", "/api/environments/prod", HttpStatusCode.Forbidden)]
        [DataRow("GET", "/api/keys", HttpStatusCode.Forbidden)]
        [DataRow("POST", "/api/keys", HttpStatusCode.Forbidden)]
        public async Task Dev_writer_of_one_app_matrix(string method, string url, HttpStatusCode expected)
        {
            string body = null;
            if (url.Contains("/files/")) body = "{\"content\":\"{}\"}";
            else if (url.EndsWith("/variables")) body = "{\"variables\":[]}";
            else if (url == "/api/environments") body = "{\"name\":\"staging\"}";
            else if (url.StartsWith("/api/environments/")) body = "{\"description\":\"x\"}";
            else if (url == "/api/keys") body = "{\"name\":\"x\"}";

            var response = await Send(new HttpMethod(method), url, DevWriter, method == "GET" || method == "DELETE" ? null : body);

            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
            if (expected == HttpStatusCode.Forbidden)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.IsFalse(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
            }
        }

        [TestMethod]
        public async Task Dev_writer_lists_are_filtered()
        {
            using (var apps = await Json("/api/apps", DevWriter))
                CollectionAssert.AreEqual(new[] { Billing }, Strings(apps.RootElement));

            using (var environments = await Json("/api/environments", DevWriter))
            {
                CollectionAssert.AreEqual(new[] { "dev" }, Strings(environments.RootElement, "name"));
                Assert.AreEqual(1, environments.RootElement[0].GetProperty("appCount").GetInt32());
            }

            using (var files = await Json("/api/apps/billing-service/files", DevWriter))
                CollectionAssert.AreEquivalent(new[] { "default.json", "dev.json" }, Strings(files.RootElement, "name"));

            using (var status = await Json("/api/apps/billing-service/status", DevWriter))
                CollectionAssert.AreEqual(new[] { "dev" }, Strings(status.RootElement.GetProperty("environments"), "name"));
        }

        [TestMethod]
        public async Task Overrides_grid_is_limited_to_readable_scopes_without_default()
        {
            using var doc = await Json("/api/apps/billing-service/overrides", new[] { "config:write:billing-service:dev", "config:read:billing-service:stg" });
            var root = doc.RootElement;

            CollectionAssert.AreEqual(new[] { "dev", "stg" }, Strings(root.GetProperty("scopes")));
            var variables = root.GetProperty("variables").EnumerateArray().ToDictionary(v => v.GetProperty("key").GetString());
            CollectionAssert.AreEquivalent(new[] { "debug", "name", "timeout" }, variables.Keys.ToArray(), "keys of hidden prd.json must not leak");

            var timeout = variables["timeout"];
            CollectionAssert.AreEquivalent(new[] { "dev", "stg" }, Names(timeout.GetProperty("values")));
            CollectionAssert.AreEquivalent(new[] { "dev", "stg" }, Names(timeout.GetProperty("effective")));
            Assert.AreEqual(10, timeout.GetProperty("values").GetProperty("dev").GetProperty("value").GetInt32());

            var name = variables["name"];
            Assert.AreEqual(0, Names(name.GetProperty("values")).Length);
            Assert.AreEqual("default", name.GetProperty("effective").GetProperty("dev").GetProperty("from").GetString());
        }

        [TestMethod]
        public async Task Overrides_put_checks_every_change_scope_and_writes_nothing_when_one_is_denied()
        {
            const string url = "/api/apps/billing-service/overrides";
            var devFile = Path.Combine(_host.Dir, Billing, "dev.json");

            Assert.AreEqual(HttpStatusCode.OK, await Status(HttpMethod.Put, url, DevWriter, "{\"changes\":[{\"key\":\"timeout\",\"scope\":\"dev\",\"value\":11}]}"));
            var afterAllowed = File.ReadAllText(devFile);
            StringAssert.Contains(afterAllowed, "11");

            Assert.AreEqual(HttpStatusCode.Forbidden, await Status(HttpMethod.Put, url, DevWriter, "{\"changes\":[{\"key\":\"timeout\",\"scope\":\"prd\",\"value\":1}]}"));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Status(HttpMethod.Put, url, DevWriter, "{\"changes\":[{\"key\":\"timeout\",\"scope\":\"default\",\"value\":1}]}"));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Status(HttpMethod.Put, url, DevWriter,
                "{\"changes\":[{\"key\":\"timeout\",\"scope\":\"dev\",\"value\":12},{\"key\":\"timeout\",\"scope\":\"stg\",\"value\":12}]}"));
            Assert.AreEqual(afterAllowed, File.ReadAllText(devFile));
            StringAssert.Contains(File.ReadAllText(Path.Combine(_host.Dir, Billing, "stg.json")), "20");
        }

        [TestMethod]
        public async Task Variable_index_only_shows_readable_apps_and_environments()
        {
            using (var index = await Json("/api/variables", DevWriter))
            {
                var usages = index.RootElement.GetProperty("variables").EnumerateArray().SelectMany(v => v.GetProperty("usedBy").EnumerateArray()).ToList();
                CollectionAssert.AreEqual(new[] { Billing }, usages.Select(u => u.GetProperty("app").GetString()).Distinct().ToArray());
                Assert.IsFalse(index.RootElement.GetProperty("variables").EnumerateArray().Any(v => v.GetProperty("key").GetString() == "prdOnly"));
                Assert.IsTrue(usages.All(u => Strings(u.GetProperty("environments")).All(e => e == "dev")));
            }

            using var detail = await Json("/api/variables/timeout", DevWriter);
            var usage = detail.RootElement.GetProperty("usedBy").EnumerateArray().Single();
            CollectionAssert.AreEqual(new[] { "dev" }, Names(usage.GetProperty("byEnvironment")));
            CollectionAssert.AreEquivalent(new[] { "default.json", "dev.json" }, Strings(usage.GetProperty("definedIn")));
        }

        [TestMethod]
        public async Task Me_returns_grants_and_coarse_roles()
        {
            using var me = await Json("/api/auth/me", new[] { "config:write:billing-service:dev", "config:read:billing-service:stg", "config:read:web" });

            CollectionAssert.AreEqual(new[] { "config:read", "config:write" }, Strings(me.RootElement.GetProperty("roles")));
            var grants = me.RootElement.GetProperty("grants").EnumerateArray()
                .Select(g => $"{g.GetProperty("permission").GetString()}|{g.GetProperty("app").GetString()}|{g.GetProperty("environment").GetString()}")
                .ToArray();
            CollectionAssert.AreEqual(new[] { "write|billing-service|dev", "read|billing-service|stg", "read|web|*" }, grants);

            using var admin = await Json("/api/auth/me", new[] { "config:admin" });
            CollectionAssert.AreEqual(new[] { "admin|*|*" }, admin.RootElement.GetProperty("grants").EnumerateArray()
                .Select(g => $"{g.GetProperty("permission").GetString()}|{g.GetProperty("app").GetString()}|{g.GetProperty("environment").GetString()}").ToArray());
        }

        [TestMethod]
        public async Task Global_read_still_reads_everything_but_cannot_write()
        {
            var reader = new[] { "config:read" };

            Assert.AreEqual(HttpStatusCode.OK, await Status(HttpMethod.Get, "/config/billing-service/prd", reader));
            Assert.AreEqual(HttpStatusCode.OK, await Status(HttpMethod.Get, "/config/web/dev", reader));
            using (var apps = await Json("/api/apps", reader))
                CollectionAssert.AreEquivalent(new[] { "api", Billing, "web" }, Strings(apps.RootElement));
            using (var grid = await Json("/api/apps/billing-service/overrides", reader))
                CollectionAssert.AreEqual(new[] { "default", "dev", "prod", "prd", "stg" }, Strings(grid.RootElement.GetProperty("scopes")));
            using (var environments = await Json("/api/environments", reader))
                CollectionAssert.AreEqual(new[] { "dev", "prod", "prd", "stg" }, Strings(environments.RootElement, "name"));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Status(HttpMethod.Put, "/api/apps/web/files/dev.json", reader, "{\"content\":\"{}\"}"));
        }

        [TestMethod]
        public async Task App_wide_role_covers_every_environment_of_that_app_including_default()
        {
            var webReader = new[] { "config:read:web" };
            var webWriter = new[] { "config:write:web" };

            Assert.AreEqual(HttpStatusCode.OK, await Status(HttpMethod.Get, "/config/web/prod", webReader));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Status(HttpMethod.Get, "/config/api/prod", webReader));
            using (var grid = await Json("/api/apps/web/overrides", webReader))
                Assert.AreEqual("default", Strings(grid.RootElement.GetProperty("scopes"))[0]);
            using (var apps = await Json("/api/apps", webReader))
                CollectionAssert.AreEqual(new[] { "web" }, Strings(apps.RootElement));

            Assert.AreEqual(HttpStatusCode.NoContent, await Status(HttpMethod.Put, "/api/apps/web/files/default.json", webWriter, "{\"content\":\"{}\"}"));
            Assert.AreEqual(HttpStatusCode.NoContent, await Status(HttpMethod.Put, "/api/apps/web/variables", webWriter, "{\"variables\":[]}"));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Status(HttpMethod.Post, "/api/environments", webWriter, "{\"name\":\"staging\"}"));
        }

        [TestMethod]
        public async Task Environment_names_in_roles_are_case_sensitive()
        {
            var roles = new[] { "config:read:web:prod" };

            Assert.AreEqual(HttpStatusCode.OK, await Status(HttpMethod.Get, "/config/web/prod", roles));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Status(HttpMethod.Get, "/config/web/PROD", roles));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Status(HttpMethod.Get, "/config/Web/prod", roles));
        }

        [TestMethod]
        public async Task Scoped_admin_and_invalid_roles_are_ignored()
        {
            var roles = new[] { "config:admin:web", "config:read:web:../x", "config:read:web" };

            Assert.AreEqual(HttpStatusCode.Forbidden, await Status(HttpMethod.Get, "/api/keys", roles));
            Assert.AreEqual(HttpStatusCode.OK, await Status(HttpMethod.Get, "/config/web/dev", roles));
            using var me = await Json("/api/auth/me", roles);
            CollectionAssert.AreEqual(new[] { "config:read" }, Strings(me.RootElement.GetProperty("roles")));
            Assert.AreEqual(1, me.RootElement.GetProperty("grants").GetArrayLength());

            Assert.AreEqual(HttpStatusCode.Forbidden, await Status(HttpMethod.Get, "/config/web/dev", new[] { "config:admin:web" }));
        }

        [TestMethod]
        public async Task Token_cannot_spoof_grants_with_other_claim_type_casing()
        {
            var token = _host.Token(new[] { "unmapped" }, claims: new Dictionary<string, object>
            {
                ["URN:CONFIG-SERVER:GRANT"] = new[] { "config:admin:*:*" },
                ["Urn:Config-Server:Role"] = new[] { "config:admin", "config:read" },
                ["urn:config-server:grant"] = "config:admin:*:*"
            });

            foreach (var url in new[] { "/api/keys", "/config/web/prod", "/api/apps" })
                Assert.AreEqual(HttpStatusCode.Forbidden,
                    (await _host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, url, bearer: token))).StatusCode, url);
        }

        [TestMethod]
        public async Task Keycloak_client_roles_with_placeholder_and_union_of_paths()
        {
            using var host = new AuthTestHost("roles,resource_access.{clientId}.roles");
            SeedBilling(host.Dir);
            var token = host.Token(claims: new Dictionary<string, object>
            {
                ["roles"] = new[] { "config:read:web" },
                ["resource_access"] = new Dictionary<string, object>
                {
                    ["config-web"] = new Dictionary<string, object> { ["roles"] = new[] { "config:write:billing-service:dev" } },
                    ["other-client"] = new Dictionary<string, object> { ["roles"] = new[] { "config:admin" } }
                }
            });

            async Task<HttpStatusCode> Call(HttpMethod method, string url, string json = null)
                => (await host.Client.SendAsync(AuthTestHost.Request(method, url, bearer: token, json: json))).StatusCode;

            Assert.AreEqual(HttpStatusCode.OK, await Call(HttpMethod.Get, "/config/web/prod"));
            Assert.AreEqual(HttpStatusCode.NoContent, await Call(HttpMethod.Put, "/api/apps/billing-service/files/dev.json", "{\"content\":\"{}\"}"));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Call(HttpMethod.Put, "/api/apps/web/files/dev.json", "{\"content\":\"{}\"}"));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Call(HttpMethod.Get, "/api/keys"));
        }

        [TestMethod]
        public async Task Role_map_can_target_scoped_roles()
        {
            using var host = new AuthTestHost(settings: new Dictionary<string, string>
            {
                ["Auth:Oidc:RoleMap:billing-devs"] = "config:write:billing-service:dev,config:read:billing-service:stg"
            });
            SeedBilling(host.Dir);
            var token = host.Token(new[] { "billing-devs" });

            async Task<HttpStatusCode> Call(HttpMethod method, string url, string json = null)
                => (await host.Client.SendAsync(AuthTestHost.Request(method, url, bearer: token, json: json))).StatusCode;

            Assert.AreEqual(HttpStatusCode.OK, await Call(HttpMethod.Get, "/config/billing-service/stg"));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Call(HttpMethod.Get, "/config/billing-service/prd"));
            Assert.AreEqual(HttpStatusCode.NoContent, await Call(HttpMethod.Put, "/api/apps/billing-service/files/dev.json", "{\"content\":\"{}\"}"));
            Assert.AreEqual(HttpStatusCode.Forbidden, await Call(HttpMethod.Put, "/api/apps/billing-service/files/stg.json", "{\"content\":\"{}\"}"));
        }

        [TestMethod]
        public async Task Api_key_grants_are_reported_by_me_and_never_admin()
        {
            var admin = _host.Token(new[] { "config:admin" });
            var created = await _host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Post, "/api/keys", bearer: admin,
                json: "{\"name\":\"ci\",\"apps\":[\"web\"],\"environments\":[\"prod\"],\"permission\":\"write\"}"));
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            using var issued = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var secret = issued.RootElement.GetProperty("secret").GetString();

            var response = await _host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/api/auth/me", apiKey: secret));
            using var me = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            CollectionAssert.AreEqual(new[] { "config:read", "config:write" }, Strings(me.RootElement.GetProperty("roles")));
            var grant = me.RootElement.GetProperty("grants").EnumerateArray().Single();
            Assert.AreEqual("write", grant.GetProperty("permission").GetString());
            Assert.AreEqual("web", grant.GetProperty("app").GetString());
            Assert.AreEqual("prod", grant.GetProperty("environment").GetString());
        }

        [TestMethod]
        public async Task Mode_none_is_global_admin()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cfgrbac-" + Guid.NewGuid().ToString("N"));
            AuthTestHost.Seed(dir);
            try
            {
                using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                {
                    b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string> { ["ConfigSource:Path"] = dir }));
                    b.ConfigureTestServices(s =>
                    {
                        s.RemoveAll<IApiKeyStore>();
                        s.AddSingleton<IApiKeyStore>(new InMemoryApiKeyStore());
                    });
                });
                using var me = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/api/auth/me"));

                var grant = me.RootElement.GetProperty("grants").EnumerateArray().Single();
                Assert.AreEqual("admin", grant.GetProperty("permission").GetString());
                Assert.AreEqual("*", grant.GetProperty("app").GetString());
                Assert.AreEqual("*", grant.GetProperty("environment").GetString());
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
