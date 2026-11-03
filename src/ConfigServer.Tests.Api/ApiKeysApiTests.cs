using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    [TestClass]
    public class ApiKeysApiTests
    {
        private AuthTestHost _host;
        private string _admin;

        [TestInitialize]
        public void Setup()
        {
            _host = new AuthTestHost();
            _admin = _host.Token(new[] { "cfg-admins" });
        }

        [TestCleanup]
        public void Cleanup() => _host.Dispose();

        private static async Task<JsonDocument> ReadJson(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        private Task<HttpResponseMessage> Admin(HttpMethod method, string url, string json = null)
            => _host.Client.SendAsync(AuthTestHost.Request(method, url, bearer: _admin, json: json));

        private Task<HttpResponseMessage> WithKey(string key, HttpMethod method, string url, string json = null)
            => _host.Client.SendAsync(AuthTestHost.Request(method, url, apiKey: key, json: json));

        private async Task<(string Id, string Secret)> CreateKey(string json)
        {
            var response = await Admin(HttpMethod.Post, "/api/keys", json);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var doc = await ReadJson(response);
            return (doc.RootElement.GetProperty("key").GetProperty("id").GetString(), doc.RootElement.GetProperty("secret").GetString());
        }

        [TestMethod]
        public async Task Create_returns_metadata_and_secret_once_and_list_never_leaks_it()
        {
            var response = await Admin(HttpMethod.Post, "/api/keys", "{\"name\":\"ci\",\"apps\":[\"web\"],\"permission\":\"write\",\"expiresAt\":\"2999-01-01T00:00:00Z\"}");

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            var created = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(created);
            var key = doc.RootElement.GetProperty("key");
            var secret = doc.RootElement.GetProperty("secret").GetString();
            StringAssert.StartsWith(secret, "cfs_" + key.GetProperty("prefix").GetString() + "_");
            Assert.AreEqual("ci", key.GetProperty("name").GetString());
            CollectionAssert.AreEqual(new[] { "web" }, key.GetProperty("apps").EnumerateArray().Select(a => a.GetString()).ToArray());
            CollectionAssert.AreEqual(new[] { "*" }, key.GetProperty("environments").EnumerateArray().Select(a => a.GetString()).ToArray());
            Assert.AreEqual("write", key.GetProperty("permission").GetString());
            Assert.AreEqual("active", key.GetProperty("status").GetString());
            Assert.AreEqual(2999, key.GetProperty("expiresAt").GetDateTimeOffset().Year);
            Assert.AreEqual(JsonValueKind.Null, key.GetProperty("revokedAt").ValueKind);
            Assert.IsFalse(key.TryGetProperty("hash", out _));
            Assert.IsFalse(created.Contains("sha256:"));

            var list = await (await Admin(HttpMethod.Get, "/api/keys")).Content.ReadAsStringAsync();
            Assert.IsFalse(list.Contains(secret), "secret leaked in list");
            Assert.IsFalse(list.Contains("sha256:"), "hash leaked in list");
            Assert.IsFalse(list.Contains("\"hash\""), "hash field in list");
            using var listDoc = JsonDocument.Parse(list);
            var item = listDoc.RootElement.EnumerateArray().Single();
            foreach (var field in new[] { "id", "name", "prefix", "apps", "environments", "permission", "createdAt", "expiresAt", "revokedAt", "lastUsedAt", "status" })
                Assert.IsTrue(item.TryGetProperty(field, out _), field);
        }

        [TestMethod]
        [DataRow("{\"name\":\"\"}")]
        [DataRow("{\"apps\":[\"web\"]}")]
        [DataRow("{\"name\":\"x\",\"permission\":\"admin\"}")]
        [DataRow("{\"name\":\"x\",\"expiresAt\":\"2000-01-01T00:00:00Z\"}")]
        [DataRow("{\"name\":\"x\",\"expiresAt\":\"tomorrow\"}")]
        [DataRow("{\"name\":\"x\",\"apps\":[]}")]
        [DataRow("{\"name\":\"x\",\"environments\":[\"../x\"]}")]
        public async Task Create_rejects_invalid_requests_with_error(string json)
        {
            var response = await Admin(HttpMethod.Post, "/api/keys", json);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            using var doc = await ReadJson(response);
            Assert.IsFalse(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
            Assert.IsEmpty(await _host.Keys.ListAsync());
        }

        [TestMethod]
        public async Task Key_authenticates_via_both_headers_and_records_last_use()
        {
            var (id, secret) = await CreateKey("{\"name\":\"ci\"}");

            Assert.AreEqual(HttpStatusCode.OK, (await WithKey(secret, HttpMethod.Get, "/config/web/prod")).StatusCode);
            var request = new HttpRequestMessage(HttpMethod.Get, "/config/web/prod");
            request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", secret);
            Assert.AreEqual(HttpStatusCode.OK, (await _host.Client.SendAsync(request)).StatusCode);

            using var me = await ReadJson(await WithKey(secret, HttpMethod.Get, "/api/auth/me"));
            Assert.AreEqual("ci", me.RootElement.GetProperty("name").GetString());
            Assert.AreEqual("apikey", me.RootElement.GetProperty("via").GetString());
            CollectionAssert.AreEqual(new[] { "config:read" }, me.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray());

            Assert.IsNotNull((await _host.Keys.GetAsync(id)).LastUsedAt);
        }

        [TestMethod]
        public async Task Invalid_keys_return_401_with_error()
        {
            var (_, secret) = await CreateKey("{\"name\":\"ci\"}");
            var cases = new Dictionary<string, string>
            {
                ["garbage"] = "Malformed API key.",
                [ApiKeyToken.Format(secret.Substring(4, ApiKeyToken.PrefixLength), ApiKeyToken.NewSecret())] = "Invalid API key."
            };

            foreach (var (key, message) in cases)
            {
                var response = await WithKey(key, HttpMethod.Get, "/config/web/prod");
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                CollectionAssert.Contains(response.Headers.WwwAuthenticate.Select(h => h.Scheme).ToList(), "ApiKey");
                using var doc = await ReadJson(response);
                Assert.AreEqual(message, doc.RootElement.GetProperty("error").GetString());
            }
        }

        [TestMethod]
        public async Task Expired_key_returns_401()
        {
            const string secret = "cfs_expired1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            await _host.Keys.AddAsync(new ApiKey
            {
                Id = "old",
                Name = "old",
                Prefix = "expired1",
                Hash = ApiKeyToken.Hash(secret),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
            });

            var response = await WithKey(secret, HttpMethod.Get, "/config/web/prod");

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            using var doc = await ReadJson(response);
            Assert.AreEqual("API key expired.", doc.RootElement.GetProperty("error").GetString());
            using var list = await ReadJson(await Admin(HttpMethod.Get, "/api/keys"));
            Assert.AreEqual("expired", list.RootElement[0].GetProperty("status").GetString());
        }

        [TestMethod]
        public async Task Read_key_cannot_write_and_no_key_reaches_api_keys()
        {
            var (_, reader) = await CreateKey("{\"name\":\"reader\"}");
            var (_, writer) = await CreateKey("{\"name\":\"writer\",\"permission\":\"write\"}");

            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(reader, HttpMethod.Put, "/api/apps/web/files/qa.json", "{\"content\":\"{}\"}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Created, (await WithKey(writer, HttpMethod.Put, "/api/apps/web/files/qa.json", "{\"content\":\"{}\"}")).StatusCode);

            foreach (var key in new[] { reader, writer })
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Get, "/api/keys")).StatusCode);
                Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Post, "/api/keys", "{\"name\":\"escalate\"}")).StatusCode);
            }
            Assert.AreEqual(2, (await _host.Keys.ListAsync()).Count);
        }

        [TestMethod]
        public async Task Scoped_read_key_is_limited_to_its_apps_and_environments()
        {
            var (_, key) = await CreateKey("{\"name\":\"web-prod\",\"apps\":[\"web\"],\"environments\":[\"prod\"]}");

            Assert.AreEqual(HttpStatusCode.OK, (await WithKey(key, HttpMethod.Get, "/config/web/prod")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await WithKey(key, HttpMethod.Get, "/config/web/PROD")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Get, "/config/web/dev")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Get, "/config/api/prod")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Get, "/api/apps/api/files")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Get, "/api/apps/web/files/dev.json")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await WithKey(key, HttpMethod.Get, "/api/apps/web/files/default.json")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await WithKey(key, HttpMethod.Get, "/api/apps/web/variables")).StatusCode);
            // Endpoints spanning every environment need environments ["*"].
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Get, "/api/apps/web/overrides")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Get, "/api/apps/web/status")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Get, "/api/variables")).StatusCode);

            CollectionAssert.AreEqual(new[] { "web" }, await Strings(key, "/api/apps", null));
            CollectionAssert.AreEqual(new[] { "prod" }, await Strings(key, "/api/environments", "name"));
            CollectionAssert.AreEqual(new[] { "default.json", "prod.json" }, await Strings(key, "/api/apps/web/files", "name"));

            var denied = await WithKey(key, HttpMethod.Get, "/config/api/prod");
            using var doc = await ReadJson(denied);
            StringAssert.Contains(doc.RootElement.GetProperty("error").GetString(), "api");
        }

        [TestMethod]
        public async Task App_scoped_key_sees_only_its_apps_in_the_variable_index()
        {
            var (_, key) = await CreateKey("{\"name\":\"web-all-envs\",\"apps\":[\"web\"]}");

            using var doc = await ReadJson(await WithKey(key, HttpMethod.Get, "/api/variables"));
            var apps = doc.RootElement.GetProperty("variables").EnumerateArray()
                .SelectMany(v => v.GetProperty("usedBy").EnumerateArray().Select(u => u.GetProperty("app").GetString()))
                .Distinct().ToArray();

            CollectionAssert.AreEqual(new[] { "web" }, apps);
            Assert.AreEqual(HttpStatusCode.OK, (await WithKey(key, HttpMethod.Get, "/api/apps/web/overrides")).StatusCode);
        }

        [TestMethod]
        public async Task Scoped_write_key_changes_only_its_environments()
        {
            var (_, key) = await CreateKey("{\"name\":\"web-dev\",\"apps\":[\"web\"],\"environments\":[\"dev\"],\"permission\":\"write\"}");

            Assert.AreEqual(HttpStatusCode.NoContent, (await WithKey(key, HttpMethod.Put, "/api/apps/web/files/dev.json", "{\"content\":\"{}\"}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Put, "/api/apps/web/files/prod.json", "{\"content\":\"{}\"}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Put, "/api/apps/web/files/default.json", "{\"content\":\"{}\"}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Delete, "/api/apps/web/files/prod.json")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Put, "/api/apps/api/files/dev.json", "{\"content\":\"{}\"}")).StatusCode);

            Assert.AreEqual(HttpStatusCode.OK, (await WithKey(key, HttpMethod.Put, "/api/apps/web/overrides",
                "{\"changes\":[{\"key\":\"debug\",\"scope\":\"dev\",\"value\":false}]}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Put, "/api/apps/web/overrides",
                "{\"changes\":[{\"key\":\"debug\",\"scope\":\"dev\",\"value\":true},{\"key\":\"debug\",\"scope\":\"prod\",\"value\":true}]}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Put, "/api/apps/web/overrides",
                "{\"changes\":[{\"key\":\"debug\",\"scope\":\"default\",\"value\":true}]}")).StatusCode);

            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Post, "/api/environments", "{\"name\":\"staging\"}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await WithKey(key, HttpMethod.Put, "/api/apps/web/variables", "{\"variables\":[]}")).StatusCode);
        }

        [TestMethod]
        public async Task Unrestricted_write_key_can_manage_environments()
        {
            var (_, key) = await CreateKey("{\"name\":\"ops\",\"permission\":\"write\"}");

            Assert.AreEqual(HttpStatusCode.Created, (await WithKey(key, HttpMethod.Post, "/api/environments", "{\"name\":\"staging\"}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, (await WithKey(key, HttpMethod.Put, "/api/apps/web/files/default.json", "{\"content\":\"{}\"}")).StatusCode);
        }

        [TestMethod]
        public async Task Revoke_is_idempotent_and_disables_the_key()
        {
            var (id, secret) = await CreateKey("{\"name\":\"ci\"}");

            Assert.AreEqual(HttpStatusCode.NoContent, (await Admin(HttpMethod.Delete, "/api/keys/" + id)).StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, (await Admin(HttpMethod.Delete, "/api/keys/" + id)).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await Admin(HttpMethod.Delete, "/api/keys/unknown")).StatusCode);

            var response = await WithKey(secret, HttpMethod.Get, "/config/web/prod");
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            using var doc = await ReadJson(response);
            Assert.AreEqual("API key revoked.", doc.RootElement.GetProperty("error").GetString());

            using var list = await ReadJson(await Admin(HttpMethod.Get, "/api/keys"));
            Assert.AreEqual("revoked", list.RootElement[0].GetProperty("status").GetString());
            Assert.AreNotEqual(JsonValueKind.Null, list.RootElement[0].GetProperty("revokedAt").ValueKind);
        }

        [TestMethod]
        public async Task Rotate_issues_new_secret_and_old_one_stops_working()
        {
            var (id, oldSecret) = await CreateKey("{\"name\":\"ci\",\"apps\":[\"web\"]}");

            var response = await Admin(HttpMethod.Post, $"/api/keys/{id}/rotate");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var doc = await ReadJson(response);
            var newSecret = doc.RootElement.GetProperty("secret").GetString();
            Assert.AreEqual(id, doc.RootElement.GetProperty("key").GetProperty("id").GetString());
            CollectionAssert.AreEqual(new[] { "web" }, doc.RootElement.GetProperty("key").GetProperty("apps").EnumerateArray().Select(a => a.GetString()).ToArray());
            Assert.AreNotEqual(oldSecret, newSecret);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await WithKey(oldSecret, HttpMethod.Get, "/config/web/prod")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await WithKey(newSecret, HttpMethod.Get, "/config/web/prod")).StatusCode);

            Assert.AreEqual(HttpStatusCode.NotFound, (await Admin(HttpMethod.Post, "/api/keys/unknown/rotate")).StatusCode);
            await Admin(HttpMethod.Delete, "/api/keys/" + id);
            var revoked = await Admin(HttpMethod.Post, $"/api/keys/{id}/rotate");
            Assert.AreEqual(HttpStatusCode.BadRequest, revoked.StatusCode);
            using var error = await ReadJson(revoked);
            Assert.IsFalse(string.IsNullOrEmpty(error.RootElement.GetProperty("error").GetString()));
        }

        [TestMethod]
        public async Task Api_key_header_wins_over_bearer_token()
        {
            var (_, key) = await CreateKey("{\"name\":\"reader\"}");
            var request = AuthTestHost.Request(HttpMethod.Get, "/api/keys", apiKey: key);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _admin);

            Assert.AreEqual(HttpStatusCode.Forbidden, (await _host.Client.SendAsync(request)).StatusCode);
        }

        private async Task<string[]> Strings(string key, string url, string property)
        {
            var response = await WithKey(key, HttpMethod.Get, url);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, url);
            using var doc = await ReadJson(response);
            return doc.RootElement.EnumerateArray().Select(e => property == null ? e.GetString() : e.GetProperty(property).GetString()).ToArray();
        }
    }
}
