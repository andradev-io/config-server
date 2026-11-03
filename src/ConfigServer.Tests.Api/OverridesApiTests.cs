using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ConfigServer.Application.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    [TestClass]
    public class OverridesApiTests
    {
        private const string SecretValue = "S3cr3t-V4lue-do-not-leak";

        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cfgovr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "web"));
            Directory.CreateDirectory(Path.Combine(_dir, "api"));
            File.WriteAllText(Path.Combine(_dir, "_environments.json"),
                "{\"environments\":[{\"name\":\"dev\",\"order\":1},{\"name\":\"prod\",\"order\":2}]}");
            File.WriteAllText(Path.Combine(_dir, "web", "default.json"),
                "{\"database\":{\"host\":\"localhost\",\"port\":5432,\"password\":\"" + SecretValue + "\"},\"debug\":false,\"tags\":[\"a\",\"b\"]}");
            File.WriteAllText(Path.Combine(_dir, "web", "_variables.json"),
                "{\"variables\":[{\"key\":\"database.password\",\"type\":\"string\",\"secret\":true},{\"key\":\"database.port\",\"type\":\"number\"}]}");
            File.WriteAllText(Path.Combine(_dir, "web", "dev.env"), "# dev settings\ndatabase__port=6000\ndebug=true\n");
            File.WriteAllText(Path.Combine(_dir, "web", "qa.yaml"), "# note\ndatabase:\n  host: qa-host\n");
            File.WriteAllText(Path.Combine(_dir, "api", "default.yaml"), "greeting: hi\n");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private (HttpClient Client, WebApplicationFactory<Program> Factory) Create(bool allowWrite = true)
        {
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string>("ConfigSource:Path", _dir),
                    new KeyValuePair<string, string>("ConfigSource:AllowWrite", allowWrite ? "true" : "false")
                })));
            return (factory.CreateClient(), factory);
        }

        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        private static async Task<JsonDocument> ReadJson(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        private string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { _dir }.Concat(parts).ToArray()));

        private static Task<HttpResponseMessage> Put(HttpClient client, string app, string body, bool dryRun = false)
            => client.PutAsync($"/api/apps/{app}/overrides?dryRun={(dryRun ? "true" : "false")}", Json(body));

        private static JsonElement Variable(JsonDocument doc, string key)
            => doc.RootElement.GetProperty("variables").EnumerateArray().Single(v => v.GetProperty("key").GetString() == key);

        // ---- GET ----

        [TestMethod]
        public async Task Get_returns_grid_with_scopes_values_and_effective()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/api/apps/web/overrides");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
            using var doc = await ReadJson(response);
            Assert.AreEqual("web", doc.RootElement.GetProperty("app").GetString());
            Assert.IsFalse(string.IsNullOrEmpty(doc.RootElement.GetProperty("etag").GetString()));
            CollectionAssert.AreEqual(new[] { "default", "dev", "prod", "qa" },
                doc.RootElement.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToArray());

            var port = Variable(doc, "database.port");
            Assert.AreEqual("number", port.GetProperty("type").GetString());
            Assert.IsTrue(port.GetProperty("declared").GetBoolean());
            Assert.IsFalse(port.GetProperty("secret").GetBoolean());
            Assert.AreEqual(5432, port.GetProperty("values").GetProperty("default").GetProperty("value").GetInt32());
            Assert.AreEqual("json", port.GetProperty("values").GetProperty("default").GetProperty("format").GetString());
            Assert.AreEqual("dev.env", port.GetProperty("values").GetProperty("dev").GetProperty("file").GetString());
            Assert.AreEqual("env", port.GetProperty("values").GetProperty("dev").GetProperty("format").GetString());
            Assert.IsFalse(port.GetProperty("values").TryGetProperty("prod", out _));

            var effective = port.GetProperty("effective");
            Assert.AreEqual("dev", effective.GetProperty("dev").GetProperty("from").GetString());
            Assert.AreEqual("default", effective.GetProperty("prod").GetProperty("from").GetString());
            Assert.AreEqual(5432, effective.GetProperty("prod").GetProperty("value").GetInt32());

            var tags = Variable(doc, "tags");
            Assert.AreEqual("array", tags.GetProperty("type").GetString());
            Assert.AreEqual(2, tags.GetProperty("values").GetProperty("default").GetProperty("value").GetArrayLength());
            Assert.IsFalse(tags.GetProperty("declared").GetBoolean());
        }

        [TestMethod]
        public async Task Get_masks_secrets_and_never_leaks_the_value()
        {
            File.WriteAllText(Path.Combine(_dir, "web", "prod.json"), "{\"database\":{\"password\":\"" + SecretValue + "-prod\"}}");
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/api/apps/web/overrides");
            var text = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsFalse(text.Contains(SecretValue), "secret value leaked");
            using var doc = JsonDocument.Parse(text);
            var secret = Variable(doc, "database.password");
            Assert.IsTrue(secret.GetProperty("secret").GetBoolean());
            var cell = secret.GetProperty("values").GetProperty("default");
            Assert.IsTrue(cell.GetProperty("masked").GetBoolean());
            Assert.AreEqual("default.json", cell.GetProperty("file").GetString());
            Assert.IsFalse(cell.TryGetProperty("value", out _));
            var effective = secret.GetProperty("effective").GetProperty("prod");
            Assert.IsTrue(effective.GetProperty("masked").GetBoolean());
            Assert.AreEqual("prod", effective.GetProperty("from").GetString());
            Assert.IsFalse(effective.TryGetProperty("value", out _));
        }

        [TestMethod]
        public async Task Get_validates_names_and_missing_app()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/apps/..bad/overrides")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/apps/nope/overrides")).StatusCode);
        }

        // ---- PUT: success ----

        [TestMethod]
        public async Task Put_sets_values_creates_scope_file_and_reports_changed_keys()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await Put(client, "web",
                "{\"changes\":[{\"key\":\"database.host\",\"scope\":\"prod\",\"value\":\"prod-db\"},{\"key\":\"debug\",\"scope\":\"default\",\"value\":true}]}");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var doc = await ReadJson(response);
            var files = doc.RootElement.GetProperty("files").EnumerateArray().ToList();
            var prod = files.Single(f => f.GetProperty("name").GetString() == "prod.json");
            Assert.IsTrue(prod.GetProperty("created").GetBoolean());
            Assert.AreEqual("database.host", prod.GetProperty("changedKeys")[0].GetString());
            var def = files.Single(f => f.GetProperty("name").GetString() == "default.json");
            Assert.IsFalse(def.GetProperty("created").GetBoolean());

            using var written = JsonDocument.Parse(Read("web", "prod.json"));
            Assert.AreEqual("prod-db", written.RootElement.GetProperty("database").GetProperty("host").GetString());
            using var changedDefault = JsonDocument.Parse(Read("web", "default.json"));
            Assert.IsTrue(changedDefault.RootElement.GetProperty("debug").GetBoolean());
            Assert.AreEqual(5432, changedDefault.RootElement.GetProperty("database").GetProperty("port").GetInt32());

            // the grid now sees the new cell
            using var grid = await ReadJson(await client.GetAsync("/api/apps/web/overrides"));
            Assert.AreEqual("prod.json", Variable(grid, "database.host").GetProperty("values").GetProperty("prod").GetProperty("file").GetString());
        }

        [TestMethod]
        public async Task Put_sets_array_and_object_values()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await Put(client, "web",
                "{\"changes\":[{\"key\":\"tags\",\"scope\":\"prod\",\"value\":[\"x\",\"y\",\"z\"]},{\"key\":\"cache\",\"scope\":\"prod\",\"value\":{\"ttl\":30,\"mode\":\"lru\"}}]}");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var written = JsonDocument.Parse(Read("web", "prod.json"));
            Assert.AreEqual(3, written.RootElement.GetProperty("tags").GetArrayLength());
            Assert.AreEqual(30, written.RootElement.GetProperty("cache").GetProperty("ttl").GetInt32());
        }

        [TestMethod]
        public async Task Put_remove_deletes_key_and_prunes_empty_parents()
        {
            File.WriteAllText(Path.Combine(_dir, "web", "prod.json"), "{\"a\":{\"b\":{\"c\":1}},\"keep\":2}");
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await Put(client, "web", "{\"changes\":[{\"key\":\"a.b.c\",\"scope\":\"prod\",\"remove\":true}]}");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var written = JsonDocument.Parse(Read("web", "prod.json"));
            Assert.IsFalse(written.RootElement.TryGetProperty("a", out _));
            Assert.AreEqual(2, written.RootElement.GetProperty("keep").GetInt32());

            // value omitted also removes
            var again = await Put(client, "web", "{\"changes\":[{\"key\":\"keep\",\"scope\":\"prod\"}]}");
            Assert.AreEqual(HttpStatusCode.OK, again.StatusCode);
            using var emptied = JsonDocument.Parse(Read("web", "prod.json"));
            Assert.AreEqual(0, emptied.RootElement.EnumerateObject().Count());
        }

        [TestMethod]
        public async Task Put_edits_yaml_and_env_files_in_their_own_format_and_warns_about_comments()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await Put(client, "web",
                "{\"changes\":[{\"key\":\"database.port\",\"scope\":\"dev\",\"value\":7001},{\"key\":\"database.host\",\"scope\":\"qa\",\"value\":\"qa2\"}]}");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var env = Read("web", "dev.env");
            StringAssert.Contains(env, "database__port=7001");
            StringAssert.Contains(env, "debug=true");
            StringAssert.Contains(Read("web", "qa.yaml"), "host: qa2");
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "web", "dev.json")));

            using var doc = await ReadJson(response);
            var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
            Assert.IsTrue(warnings.Any(w => w.StartsWith("dev.env")), "dev.env comment warning");
            Assert.IsTrue(warnings.Any(w => w.StartsWith("qa.yaml")), "qa.yaml comment warning");
            Assert.IsFalse(warnings.Any(w => w.StartsWith("default.json")));
        }

        [TestMethod]
        public async Task Put_creates_new_scope_file_in_the_format_of_a_yaml_default()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await Put(client, "api", "{\"changes\":[{\"key\":\"greeting\",\"scope\":\"prod\",\"value\":\"hello\"}]}");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "api", "prod.json")));
            StringAssert.Contains(Read("api", "prod.yaml"), "greeting: hello");
            using var doc = await ReadJson(response);
            Assert.AreEqual("prod.yaml", doc.RootElement.GetProperty("files")[0].GetProperty("name").GetString());
            Assert.IsTrue(doc.RootElement.GetProperty("files")[0].GetProperty("created").GetBoolean());
        }

        [TestMethod]
        public async Task Put_secret_value_is_stored_but_never_echoed_and_warns()
        {
            var (client, factory) = Create();
            using var f_ = factory;
            const string newSecret = "NewS3cret-xyz";

            var response = await Put(client, "web",
                "{\"changes\":[{\"key\":\"database.password\",\"scope\":\"prod\",\"value\":\"" + newSecret + "\"}]}");
            var text = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsFalse(text.Contains(newSecret), "secret echoed");
            StringAssert.Contains(text, "database.password: stored in plain text in prod.json until a secret resolver is configured");
            StringAssert.Contains(Read("web", "prod.json"), newSecret);

            var get = await (await client.GetAsync("/api/apps/web/overrides")).Content.ReadAsStringAsync();
            Assert.IsFalse(get.Contains(newSecret));
        }

        [TestMethod]
        public async Task Put_dry_run_writes_nothing_and_skips_write_gate()
        {
            var (client, factory) = Create(allowWrite: false);
            using var f_ = factory;
            var before = Read("web", "default.json");

            var response = await Put(client, "web",
                "{\"changes\":[{\"key\":\"debug\",\"scope\":\"default\",\"value\":true},{\"key\":\"x\",\"scope\":\"prod\",\"value\":1}]}", dryRun: true);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var doc = await ReadJson(response);
            Assert.AreEqual(2, doc.RootElement.GetProperty("files").GetArrayLength());
            Assert.AreEqual(before, Read("web", "default.json"));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "web", "prod.json")));
        }

        [TestMethod]
        public async Task Put_dry_run_still_validates_names()
        {
            var (client, factory) = Create(allowWrite: false);
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.BadRequest, (await Put(client, "..bad", "{\"changes\":[]}", dryRun: true)).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest,
                (await Put(client, "web", "{\"changes\":[{\"key\":\"a\",\"scope\":\"nope\",\"value\":1}]}", dryRun: true)).StatusCode);
        }

        // ---- PUT: failures ----

        [TestMethod]
        public async Task Put_invalid_changes_return_400_and_write_nothing()
        {
            var (client, factory) = Create();
            using var f_ = factory;
            var before = Read("web", "default.json");
            const string valid = "{\"key\":\"debug\",\"scope\":\"default\",\"value\":true}";

            var cases = new Dictionary<string, string>
            {
                ["unknown scope"] = "{\"key\":\"a\",\"scope\":\"nowhere\",\"value\":1}",
                ["reserved scope"] = "{\"key\":\"a\",\"scope\":\"_variables\",\"value\":1}",
                ["invalid key"] = "{\"key\":\"a..b\",\"scope\":\"prod\",\"value\":1}",
                ["empty key"] = "{\"key\":\"\",\"scope\":\"prod\",\"value\":1}",
                ["child under scalar"] = "{\"key\":\"debug.child\",\"scope\":\"default\",\"value\":1}",
                ["scalar over object with children"] = "{\"key\":\"database\",\"scope\":\"default\",\"value\":1}",
                ["wrong declared type"] = "{\"key\":\"database.port\",\"scope\":\"prod\",\"value\":\"abc\"}"
            };

            foreach (var (name, change) in cases)
            {
                var response = await Put(client, "web", "{\"changes\":[" + valid + "," + change + "]}");
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, name);
                using var doc = await ReadJson(response);
                Assert.IsFalse(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()), name);
                Assert.AreEqual(before, Read("web", "default.json"), name + ": default.json must be untouched");
                Assert.IsFalse(File.Exists(Path.Combine(_dir, "web", "prod.json")), name);
            }
        }

        [TestMethod]
        public async Task Put_error_names_the_key_and_scope()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await Put(client, "web", "{\"changes\":[{\"key\":\"database.port\",\"scope\":\"prod\",\"value\":\"abc\"}]}");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            using var doc = await ReadJson(response);
            Assert.AreEqual("database.port", doc.RootElement.GetProperty("key").GetString());
            Assert.AreEqual("prod", doc.RootElement.GetProperty("scope").GetString());
        }

        [TestMethod]
        public async Task Put_without_changes_array_returns_400()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.BadRequest, (await Put(client, "web", "{}")).StatusCode);
        }

        [TestMethod]
        public async Task Put_returns_403_when_writes_are_disabled()
        {
            var (client, factory) = Create(allowWrite: false);
            using var f_ = factory;
            var before = Read("web", "default.json");

            var response = await Put(client, "web", "{\"changes\":[{\"key\":\"debug\",\"scope\":\"default\",\"value\":true}]}");

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.AreEqual(before, Read("web", "default.json"));
        }

        [TestMethod]
        public async Task Put_returns_409_when_etag_does_not_match_and_accepts_the_current_one()
        {
            var (client, factory) = Create();
            using var f_ = factory;
            using var grid = await ReadJson(await client.GetAsync("/api/apps/web/overrides"));
            var etag = grid.RootElement.GetProperty("etag").GetString();

            // someone edits a file behind our back
            File.WriteAllText(Path.Combine(_dir, "web", "qa.yaml"), "database:\n  host: changed\n");

            var stale = await Put(client, "web",
                "{\"ifMatch\":\"" + etag + "\",\"changes\":[{\"key\":\"debug\",\"scope\":\"default\",\"value\":true}]}");
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            using var conflict = await ReadJson(stale);
            Assert.AreEqual("Config changed on disk; reload.", conflict.RootElement.GetProperty("error").GetString());
            StringAssert.Contains(Read("web", "default.json"), "\"debug\":false");

            using var fresh = await ReadJson(await client.GetAsync("/api/apps/web/overrides"));
            var current = fresh.RootElement.GetProperty("etag").GetString();
            Assert.AreNotEqual(etag, current);
            var ok = await Put(client, "web",
                "{\"ifMatch\":\"" + current + "\",\"changes\":[{\"key\":\"debug\",\"scope\":\"default\",\"value\":true}]}");
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
            using var after = await ReadJson(ok);
            Assert.AreNotEqual(current, after.RootElement.GetProperty("etag").GetString());
        }
    }
}
