using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    [TestClass]
    public class AuthApiTests
    {
        private static readonly string[] Readers = { "cfg-readers" };
        private static readonly string[] Writers = { "cfg-writers" };
        private static readonly string[] Admins = { "cfg-admins" };

        private AuthTestHost _host;

        [TestInitialize]
        public void Setup() => _host = new AuthTestHost();

        [TestCleanup]
        public void Cleanup() => _host.Dispose();

        private Task<HttpResponseMessage> Send(HttpMethod method, string url, string[] roles, string json = null)
            => _host.Client.SendAsync(AuthTestHost.Request(method, url, bearer: roles == null ? null : _host.Token(roles), json: json));

        private static async Task<JsonDocument> ReadJson(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        [TestMethod]
        public async Task Health_and_auth_info_are_anonymous()
        {
            Assert.AreEqual(HttpStatusCode.OK, (await _host.Client.GetAsync("/health")).StatusCode);

            var response = await _host.Client.GetAsync("/api/auth/info");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var doc = await ReadJson(response);
            Assert.AreEqual("oidc", doc.RootElement.GetProperty("mode").GetString());
            var oidc = doc.RootElement.GetProperty("oidc");
            Assert.AreEqual(AuthTestHost.Issuer, oidc.GetProperty("authority").GetString());
            Assert.AreEqual("config-web", oidc.GetProperty("clientId").GetString());
            CollectionAssert.AreEqual(new[] { "openid", "profile" }, oidc.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToArray());
            Assert.IsTrue(doc.RootElement.GetProperty("apiKeys").GetBoolean());
        }

        [TestMethod]
        public async Task Missing_credentials_return_401_with_challenges_and_error()
        {
            var response = await _host.Client.GetAsync("/config/web/prod");

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            var challenges = response.Headers.WwwAuthenticate.Select(h => h.Scheme).ToList();
            CollectionAssert.Contains(challenges, "Bearer");
            CollectionAssert.Contains(challenges, "ApiKey");
            using var doc = await ReadJson(response);
            Assert.IsFalse(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await _host.Client.GetAsync("/api/auth/me")).StatusCode);
        }

        [TestMethod]
        public async Task Invalid_tokens_return_401()
        {
            var otherKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
            var tokens = new[]
            {
                _host.Token(Admins, key: otherKey),
                _host.Token(Admins, audience: "someone-else"),
                _host.Token(Admins, expires: DateTimeOffset.UtcNow.AddMinutes(-10)),
                "not-a-jwt"
            };

            foreach (var token in tokens)
            {
                var response = await _host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/config/web/prod", bearer: token));
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, token);
                using var doc = await ReadJson(response);
                Assert.AreEqual("Invalid or expired bearer token.", doc.RootElement.GetProperty("error").GetString());
            }
        }

        [TestMethod]
        [DataRow("reader", "GET", "/config/web/prod", HttpStatusCode.OK)]
        [DataRow("reader", "GET", "/api/apps", HttpStatusCode.OK)]
        [DataRow("reader", "GET", "/api/environments", HttpStatusCode.OK)]
        [DataRow("reader", "GET", "/api/variables", HttpStatusCode.OK)]
        [DataRow("reader", "GET", "/api/apps/web/overrides", HttpStatusCode.OK)]
        [DataRow("reader", "PUT", "/api/apps/web/files/qa.json", HttpStatusCode.Forbidden)]
        [DataRow("reader", "DELETE", "/api/apps/web/files/dev.json", HttpStatusCode.Forbidden)]
        [DataRow("reader", "POST", "/api/environments", HttpStatusCode.Forbidden)]
        [DataRow("reader", "PUT", "/api/apps/web/overrides", HttpStatusCode.Forbidden)]
        [DataRow("reader", "GET", "/api/keys", HttpStatusCode.Forbidden)]
        [DataRow("writer", "GET", "/config/web/prod", HttpStatusCode.OK)]
        [DataRow("writer", "PUT", "/api/apps/web/files/qa.json", HttpStatusCode.Created)]
        [DataRow("writer", "DELETE", "/api/apps/web/files/dev.json", HttpStatusCode.NoContent)]
        [DataRow("writer", "POST", "/api/environments", HttpStatusCode.Created)]
        [DataRow("writer", "PUT", "/api/apps/web/overrides", HttpStatusCode.OK)]
        [DataRow("writer", "GET", "/api/keys", HttpStatusCode.Forbidden)]
        [DataRow("writer", "POST", "/api/keys", HttpStatusCode.Forbidden)]
        [DataRow("admin", "GET", "/config/web/prod", HttpStatusCode.OK)]
        [DataRow("admin", "PUT", "/api/apps/web/files/qa.json", HttpStatusCode.Created)]
        [DataRow("admin", "GET", "/api/keys", HttpStatusCode.OK)]
        [DataRow("none", "GET", "/config/web/prod", HttpStatusCode.Forbidden)]
        [DataRow("none", "GET", "/api/apps", HttpStatusCode.Forbidden)]
        public async Task Role_matrix(string role, string method, string url, HttpStatusCode expected)
        {
            var roles = role switch { "reader" => Readers, "writer" => Writers, "admin" => Admins, _ => new[] { "unmapped" } };
            string body = null;
            if (url.EndsWith("/files/qa.json")) body = "{\"content\":\"{}\"}";
            else if (url == "/api/environments") body = "{\"name\":\"staging\"}";
            else if (url.EndsWith("/overrides")) body = "{\"changes\":[{\"key\":\"debug\",\"scope\":\"dev\",\"value\":false}]}";
            else if (url == "/api/keys" && method == "POST") body = "{\"name\":\"x\"}";

            var response = await Send(new HttpMethod(method), url, roles, body);

            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
            if (expected == HttpStatusCode.Forbidden)
            {
                using var doc = await ReadJson(response);
                Assert.IsFalse(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
            }
        }

        [TestMethod]
        public async Task Me_returns_name_expanded_roles_and_via()
        {
            var response = await Send(HttpMethod.Get, "/api/auth/me", Admins);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var doc = await ReadJson(response);
            Assert.AreEqual("Alice", doc.RootElement.GetProperty("name").GetString());
            CollectionAssert.AreEqual(new[] { "config:read", "config:write", "config:admin" },
                doc.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());
            Assert.AreEqual("oidc", doc.RootElement.GetProperty("via").GetString());

            using var none = await ReadJson(await Send(HttpMethod.Get, "/api/auth/me", new[] { "unmapped" }));
            Assert.AreEqual(0, none.RootElement.GetProperty("roles").GetArrayLength());
        }

        [TestMethod]
        public async Task Config_role_names_in_token_pass_through_without_role_map()
        {
            var response = await Send(HttpMethod.Get, "/api/keys", new[] { "config:admin" });

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async Task Token_cannot_spoof_server_claims()
        {
            var token = _host.Token(new[] { "unmapped" }, claims: new Dictionary<string, object>
            {
                ["urn:config-server:role"] = new[] { "config:admin", "config:read" },
                ["urn:config-server:via"] = "apikey"
            });

            var response = await _host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/config/web/prod", bearer: token));
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            response = await _host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/api/keys", bearer: token));
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [TestMethod]
        public async Task Namespaced_role_claim_works_end_to_end()
        {
            using var host = new AuthTestHost("https://config.example.com/roles");
            var token = host.Token(claims: new Dictionary<string, object> { ["https://config.example.com/roles"] = new[] { "cfg-writers", "other" } });

            var read = await host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/config/web/prod", bearer: token));
            var write = await host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Put, "/api/apps/web/files/qa.json", bearer: token, json: "{\"content\":\"{}\"}"));
            var admin = await host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/api/keys", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
            Assert.AreEqual(HttpStatusCode.Created, write.StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, admin.StatusCode);
        }

        [TestMethod]
        public async Task Space_separated_role_claim_works_end_to_end()
        {
            using var host = new AuthTestHost("groups");
            var token = host.Token(claims: new Dictionary<string, object> { ["groups"] = "other cfg-readers" });

            var response = await host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/config/web/prod", bearer: token));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async Task Writes_stay_403_when_allow_write_is_off_even_for_admins()
        {
            using var host = new AuthTestHost(settings: new Dictionary<string, string> { ["ConfigSource:AllowWrite"] = "false" });

            var response = await host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Put, "/api/apps/web/files/qa.json",
                bearer: host.Token(Admins), json: "{\"content\":\"{}\"}"));

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            using var doc = await ReadJson(response);
            StringAssert.Contains(doc.RootElement.GetProperty("error").GetString(), "AllowWrite");
        }

        [TestMethod]
        public async Task Cors_preflight_allows_auth_headers_and_post()
        {
            var request = new HttpRequestMessage(HttpMethod.Options, "/api/keys");
            request.Headers.Add("Origin", "http://localhost:5173");
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "authorization,x-api-key,content-type");

            var response = await _host.Client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            var allowedHeaders = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
            StringAssert.Contains(allowedHeaders, "authorization");
            StringAssert.Contains(allowedHeaders, "x-api-key");
            StringAssert.Contains(string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")), "POST");
        }
    }
}
