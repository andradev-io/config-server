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
using ConfigServer.Domain.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    [TestClass]
    public class ManagementApiTests
    {
        private const string SecretValue = "S3cr3t-V4lue-do-not-leak";

        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cfgmgmt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "web"));
            Directory.CreateDirectory(Path.Combine(_dir, "api"));
            File.WriteAllText(Path.Combine(_dir, "_environments.json"),
                "{\"environments\":[{\"name\":\"dev\",\"description\":\"Development\",\"order\":1},{\"name\":\"prod\",\"description\":\"\",\"order\":2}]}");
            File.WriteAllText(Path.Combine(_dir, "web", "default.json"),
                "{\"database\":{\"host\":\"localhost\",\"port\":5432,\"password\":\"" + SecretValue + "\"},\"debug\":false,\"extra\":1}");
            File.WriteAllText(Path.Combine(_dir, "web", "dev.env"), "database__port=6000\ndebug=true\n");
            File.WriteAllText(Path.Combine(_dir, "web", "qa.yaml"), "database:\n  host: qa-host\n");
            File.WriteAllText(Path.Combine(_dir, "api", "default.json"), "{\"database\":{\"host\":\"api-db\"}}");
            File.WriteAllText(Path.Combine(_dir, "api", "prod.json"), "{\"database\":{\"host\":\"prod-db\"}}");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private (HttpClient Client, WebApplicationFactory<Program> Factory) Create(bool allowWrite = true, Action<IServiceCollection> services = null)
        {
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string>("ConfigSource:Path", _dir),
                    new KeyValuePair<string, string>("ConfigSource:AllowWrite", allowWrite ? "true" : "false")
                }));
                if (services != null) b.ConfigureServices(services);
            });
            return (factory.CreateClient(), factory);
        }

        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        private static async Task<JsonDocument> ReadJson(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        private string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { _dir }.Concat(parts).ToArray()));

        private string Seed(string app, string variablesJson)
        {
            File.WriteAllText(Path.Combine(_dir, app, "_variables.json"), variablesJson);
            return variablesJson;
        }

        // ---- environments: read ----

        [TestMethod]
        public async Task Environments_lists_declared_then_inferred_with_app_counts()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/api/environments");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
            using var doc = await ReadJson(response);
            var envs = doc.RootElement.EnumerateArray().ToList();
            CollectionAssert.AreEqual(new[] { "dev", "prod", "qa" }, envs.Select(e => e.GetProperty("name").GetString()).ToArray());
            Assert.IsTrue(envs[0].GetProperty("declared").GetBoolean());
            Assert.AreEqual("Development", envs[0].GetProperty("description").GetString());
            Assert.AreEqual(1, envs[0].GetProperty("appCount").GetInt32());
            Assert.AreEqual(1, envs[1].GetProperty("appCount").GetInt32());
            Assert.IsFalse(envs[2].GetProperty("declared").GetBoolean());
            Assert.AreEqual(1, envs[2].GetProperty("appCount").GetInt32());
        }

        [TestMethod]
        public async Task Environments_read_works_when_writes_disabled_and_no_store()
        {
            var (client, factory) = Create(false, s => s.Remove(s.Single(d => d.ServiceType == typeof(IConfigStore))));
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/environments")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/variables")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/apps/web/status")).StatusCode);
        }

        [TestMethod]
        public async Task Management_reads_return_501_without_catalog()
        {
            var (client, factory) = Create(true, s => s.Remove(s.Single(d => d.ServiceType == typeof(IConfigCatalog))));
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.NotImplemented, (await client.GetAsync("/api/environments")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotImplemented, (await client.GetAsync("/api/variables")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotImplemented, (await client.GetAsync("/api/apps/web/status")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotImplemented, (await client.PostAsync("/api/environments", Json("{\"name\":\"qa2\"}"))).StatusCode);
        }

        // ---- environments: create ----

        [TestMethod]
        public async Task Create_environment_returns_201_and_persists_registry()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.PostAsync("/api/environments", Json("{\"name\":\"staging\",\"description\":\"Pre-prod\"}"));

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            using var doc = await ReadJson(response);
            Assert.AreEqual(3, doc.RootElement.GetProperty("order").GetInt32());
            StringAssert.Contains(Read("_environments.json"), "\"staging\"");
            using var list = await ReadJson(await client.GetAsync("/api/environments"));
            Assert.IsTrue(list.RootElement.EnumerateArray().Any(e => e.GetProperty("name").GetString() == "staging" && e.GetProperty("declared").GetBoolean()));
        }

        [TestMethod]
        [DataRow("default")]
        [DataRow("DEFAULT")]
        [DataRow("-bad")]
        [DataRow("a.b")]
        [DataRow("_x")]
        [DataRow("")]
        public async Task Create_environment_rejects_invalid_names(string name)
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.PostAsync("/api/environments", Json(JsonSerializer.Serialize(new { name })));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            using var doc = await ReadJson(response);
            Assert.IsTrue(doc.RootElement.TryGetProperty("error", out _));
        }

        [TestMethod]
        public async Task Create_environment_conflicts_case_insensitively_with_declared_and_inferred()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.Conflict, (await client.PostAsync("/api/environments", Json("{\"name\":\"DEV\"}"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.Conflict, (await client.PostAsync("/api/environments", Json("{\"name\":\"QA\"}"))).StatusCode);
        }

        [TestMethod]
        public async Task Create_environment_is_forbidden_when_writes_disabled()
        {
            var (client, factory) = Create(false);
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PostAsync("/api/environments", Json("{\"name\":\"staging\"}"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PutAsync("/api/environments/dev", Json("{\"description\":\"x\"}"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/environments/dev")).StatusCode);
        }

        [TestMethod]
        public async Task Create_environment_returns_501_without_store()
        {
            var (client, factory) = Create(true, s => s.Remove(s.Single(d => d.ServiceType == typeof(IConfigStore))));
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.NotImplemented, (await client.PostAsync("/api/environments", Json("{\"name\":\"staging\"}"))).StatusCode);
        }

        [TestMethod]
        public async Task Create_environment_scaffolds_empty_and_copied_files_only_where_missing()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.PostAsync("/api/environments",
                Json("{\"name\":\"stg\",\"scaffold\":{\"apps\":[\"web\",\"api\"],\"copyFrom\":\"qa\"}}"));

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            Assert.AreEqual(Read("web", "qa.yaml"), Read("web", "stg.yaml"));
            Assert.AreEqual("{}", Read("api", "stg.json").Trim());
        }

        [TestMethod]
        public async Task Create_environment_scaffold_skips_apps_with_existing_override()
        {
            File.WriteAllText(Path.Combine(_dir, "api", "stg.json"), "{\"keep\":true}");
            var (client, factory) = Create();
            using var f_ = factory;

            // "stg" is inferred from api/stg.json, so POST conflicts; PUT declares it instead.
            Assert.AreEqual(HttpStatusCode.Conflict, (await client.PostAsync("/api/environments", Json("{\"name\":\"stg\",\"scaffold\":{\"apps\":[\"web\"]}}"))).StatusCode);
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "web", "stg.json")));

            // Scaffold into an app that already has the override (dev) leaves it alone.
            var response = await client.PostAsync("/api/environments", Json("{\"name\":\"new1\",\"scaffold\":{\"apps\":[\"api\"],\"copyFrom\":\"prod\"}}"));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            Assert.AreEqual(Read("api", "prod.json"), Read("api", "new1.json"));
            Assert.AreEqual("{\"keep\":true}", Read("api", "stg.json"));
        }

        [TestMethod]
        public async Task Create_environment_rejects_unknown_scaffold_app()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.PostAsync("/api/environments", Json("{\"name\":\"stg\",\"scaffold\":{\"apps\":[\"ghost\"]}}"));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            StringAssert.DoesNotMatch(Read("_environments.json"), new System.Text.RegularExpressions.Regex("stg"));
        }

        // ---- environments: update / delete ----

        [TestMethod]
        public async Task Update_environment_edits_declared_and_declares_implicit()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.NoContent, (await client.PutAsync("/api/environments/dev", Json("{\"description\":\"Dev env\",\"order\":9}"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, (await client.PutAsync("/api/environments/qa", Json("{\"description\":\"QA\",\"order\":3}"))).StatusCode);

            using var list = await ReadJson(await client.GetAsync("/api/environments"));
            var envs = list.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString());
            Assert.AreEqual("Dev env", envs["dev"].GetProperty("description").GetString());
            Assert.AreEqual(9, envs["dev"].GetProperty("order").GetInt32());
            Assert.IsTrue(envs["qa"].GetProperty("declared").GetBoolean());
        }

        [TestMethod]
        public async Task Update_environment_returns_404_and_400()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.NotFound, (await client.PutAsync("/api/environments/ghost", Json("{\"description\":\"x\"}"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsync("/api/environments/default", Json("{\"description\":\"x\"}"))).StatusCode);
        }

        [TestMethod]
        public async Task Delete_environment_removes_only_registry_entry_by_default()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/environments/dev")).StatusCode);

            Assert.IsFalse(Read("_environments.json").Contains("\"dev\""));
            Assert.IsTrue(File.Exists(Path.Combine(_dir, "web", "dev.env")));
            using var list = await ReadJson(await client.GetAsync("/api/environments"));
            Assert.IsFalse(list.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "dev").GetProperty("declared").GetBoolean());
        }

        [TestMethod]
        public async Task Delete_environment_conflicts_for_implicit_without_delete_files()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.Conflict, (await client.DeleteAsync("/api/environments/qa")).StatusCode);
            Assert.IsTrue(File.Exists(Path.Combine(_dir, "web", "qa.yaml")));
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/environments/ghost")).StatusCode);
        }

        [TestMethod]
        public async Task Delete_environment_with_delete_files_removes_override_files_everywhere()
        {
            File.WriteAllText(Path.Combine(_dir, "api", "dev.yml"), "a: 1\n");
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/environments/dev?deleteFiles=true")).StatusCode);

            Assert.IsFalse(File.Exists(Path.Combine(_dir, "web", "dev.env")));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "api", "dev.yml")));
            Assert.IsTrue(File.Exists(Path.Combine(_dir, "web", "default.json")));
            Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/environments/qa?deleteFiles=true")).StatusCode);
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "web", "qa.yaml")));
        }

        // ---- variables: declare ----

        [TestMethod]
        public async Task Variables_get_is_empty_when_none_declared_and_404_for_unknown_app()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/api/apps/web/variables");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(0, (await ReadJson(response)).RootElement.GetArrayLength());
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/apps/ghost/variables")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/apps/..x/variables")).StatusCode);
        }

        [TestMethod]
        public async Task Variables_put_round_trips()
        {
            var (client, factory) = Create();
            using var f_ = factory;
            const string body = "{\"variables\":[{\"key\":\"database.host\",\"type\":\"string\",\"required\":true,\"secret\":false,\"description\":\"DB\",\"environments\":null}," +
                                "{\"key\":\"debug\",\"type\":\"boolean\",\"required\":true,\"environments\":[\"dev\",\"qa\"]}]}";

            Assert.AreEqual(HttpStatusCode.NoContent, (await client.PutAsync("/api/apps/web/variables", Json(body))).StatusCode);

            using var doc = await ReadJson(await client.GetAsync("/api/apps/web/variables"));
            var items = doc.RootElement.EnumerateArray().ToList();
            Assert.AreEqual(2, items.Count);
            Assert.AreEqual("database.host", items[0].GetProperty("key").GetString());
            Assert.AreEqual(JsonValueKind.Null, items[0].GetProperty("environments").ValueKind);
            Assert.AreEqual(2, items[1].GetProperty("environments").GetArrayLength());
            Assert.IsTrue(File.Exists(Path.Combine(_dir, "web", "_variables.json")));
        }

        [TestMethod]
        [DataRow("{\"variables\":[{\"key\":\"\",\"type\":\"string\"}]}")]
        [DataRow("{\"variables\":[{\"key\":\"a\",\"type\":\"string\"},{\"key\":\"a\",\"type\":\"number\"}]}")]
        [DataRow("{\"variables\":[{\"key\":\"a\",\"type\":\"int\"}]}")]
        [DataRow("{\"variables\":[{\"key\":\"a\",\"type\":\"string\",\"environments\":[\"nope\"]}]}")]
        [DataRow("{}")]
        public async Task Variables_put_validates(string body)
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.PutAsync("/api/apps/web/variables", Json(body));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            using var doc = await ReadJson(response);
            Assert.IsFalse(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "web", "_variables.json")));
        }

        [TestMethod]
        public async Task Variables_put_accepts_implicit_environments_and_gates_writes()
        {
            var (client, factory) = Create();
            using var f_ = factory;
            Assert.AreEqual(HttpStatusCode.NoContent, (await client.PutAsync("/api/apps/web/variables",
                Json("{\"variables\":[{\"key\":\"a\",\"type\":\"string\",\"environments\":[\"qa\"]}]}"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.PutAsync("/api/apps/ghost/variables", Json("{\"variables\":[]}"))).StatusCode);

            var (ro, roFactory) = Create(false);
            using var f2_ = roFactory;
            Assert.AreEqual(HttpStatusCode.Forbidden, (await ro.PutAsync("/api/apps/web/variables", Json("{\"variables\":[]}"))).StatusCode);

            var (noStore, noStoreFactory) = Create(true, s => s.Remove(s.Single(d => d.ServiceType == typeof(IConfigStore))));
            using var f3_ = noStoreFactory;
            Assert.AreEqual(HttpStatusCode.NotImplemented, (await noStore.PutAsync("/api/apps/web/variables", Json("{\"variables\":[]}"))).StatusCode);
        }

        // ---- status ----

        [TestMethod]
        public async Task Status_reports_missing_mismatches_secrets_and_undeclared()
        {
            Seed("web", "{\"variables\":[" +
                "{\"key\":\"database.host\",\"type\":\"string\",\"required\":true}," +
                "{\"key\":\"database.port\",\"type\":\"number\",\"required\":true}," +
                "{\"key\":\"database.password\",\"type\":\"string\",\"required\":true,\"secret\":true}," +
                "{\"key\":\"debug\",\"type\":\"string\",\"required\":true}," +
                "{\"key\":\"api.key\",\"type\":\"string\",\"required\":true,\"environments\":[\"prod\"]}]}");
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/api/apps/web/status");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains(SecretValue), "status must never contain values");
            using var doc = JsonDocument.Parse(body);
            Assert.AreEqual("web", doc.RootElement.GetProperty("app").GetString());
            var envs = doc.RootElement.GetProperty("environments").EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString());
            CollectionAssert.AreEquivalent(new[] { "dev", "prod", "qa" }, envs.Keys.ToArray());

            var qa = envs["qa"];
            Assert.IsFalse(qa.GetProperty("ok").GetBoolean());
            var mismatch = qa.GetProperty("typeMismatches").EnumerateArray().Single();
            Assert.AreEqual("debug", mismatch.GetProperty("key").GetString());
            Assert.AreEqual("string", mismatch.GetProperty("expected").GetString());
            Assert.AreEqual("boolean", mismatch.GetProperty("actual").GetString());

            // dev.env values are strings: "6000" satisfies number and "true" satisfies string.
            var dev = envs["dev"];
            Assert.IsTrue(dev.GetProperty("ok").GetBoolean());
            Assert.AreEqual("database.password", dev.GetProperty("secretsInPlainText").EnumerateArray().Single().GetString());

            CollectionAssert.AreEqual(new[] { "api.key" }, envs["prod"].GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.AreEqual("extra", doc.RootElement.GetProperty("undeclared").EnumerateArray().Single().GetString());
        }

        [TestMethod]
        public async Task Status_flags_invalid_override_and_handles_unknown_app()
        {
            File.WriteAllText(Path.Combine(_dir, "web", "qa.yaml"), "a: [unclosed\n");
            var (client, factory) = Create();
            using var f_ = factory;

            using var doc = await ReadJson(await client.GetAsync("/api/apps/web/status"));
            var qa = doc.RootElement.GetProperty("environments").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "qa");
            Assert.IsFalse(qa.GetProperty("ok").GetBoolean());
            Assert.IsFalse(string.IsNullOrEmpty(qa.GetProperty("error").GetString()));

            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/apps/ghost/status")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/apps/..x/status")).StatusCode);
        }

        // ---- cross-project index ----

        [TestMethod]
        public async Task Index_lists_variables_with_usage_and_never_returns_values()
        {
            Seed("web", "{\"variables\":[{\"key\":\"database.password\",\"type\":\"string\",\"required\":true,\"secret\":true}," +
                        "{\"key\":\"unused.thing\",\"type\":\"number\"}]}");
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/api/variables");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
            var body = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains(SecretValue), "index must never contain values");
            Assert.IsFalse(body.Contains("localhost") || body.Contains("qa-host") || body.Contains("api-db"), "index must never contain values");

            using var doc = JsonDocument.Parse(body);
            Assert.AreEqual(0, doc.RootElement.GetProperty("warnings").GetArrayLength());
            var variables = doc.RootElement.GetProperty("variables").EnumerateArray().ToList();
            var keys = variables.Select(v => v.GetProperty("key").GetString()).ToList();
            CollectionAssert.AreEqual(keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), keys);

            var host = variables.Single(v => v.GetProperty("key").GetString() == "database.host");
            Assert.AreEqual(2, host.GetProperty("appCount").GetInt32());
            Assert.AreEqual("string", host.GetProperty("type").GetString());

            var password = variables.Single(v => v.GetProperty("key").GetString() == "database.password");
            var usage = password.GetProperty("usedBy").EnumerateArray().Single();
            Assert.AreEqual("web", usage.GetProperty("app").GetString());
            Assert.IsTrue(usage.GetProperty("secret").GetBoolean());
            Assert.IsTrue(usage.GetProperty("declared").GetBoolean());
            Assert.AreEqual("default.json", usage.GetProperty("definedIn").EnumerateArray().Single().GetString());

            var unused = variables.Single(v => v.GetProperty("key").GetString() == "unused.thing");
            Assert.AreEqual("number", unused.GetProperty("type").GetString());
            Assert.AreEqual(0, unused.GetProperty("usedBy")[0].GetProperty("definedIn").GetArrayLength());
        }

        [TestMethod]
        public async Task Index_filters_and_reports_warnings_for_invalid_and_oversized_files()
        {
            File.WriteAllText(Path.Combine(_dir, "web", "broken.json"), "{ nope");
            File.WriteAllText(Path.Combine(_dir, "api", "huge.json"), "{\"pad\":\"" + new string('x', 1_048_600) + "\"}");
            var (client, factory) = Create();
            using var f_ = factory;

            using var doc = await ReadJson(await client.GetAsync("/api/variables?search=HOST&app=api"));
            var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray().ToList();
            CollectionAssert.AreEquivalent(new[] { "broken.json", "huge.json" }, warnings.Select(w => w.GetProperty("file").GetString()).ToArray());
            Assert.IsTrue(warnings.All(w => !string.IsNullOrEmpty(w.GetProperty("error").GetString())));
            var variables = doc.RootElement.GetProperty("variables").EnumerateArray().ToList();
            Assert.AreEqual(1, variables.Count);
            Assert.AreEqual("database.host", variables[0].GetProperty("key").GetString());
            Assert.AreEqual(1, variables[0].GetProperty("usedBy").GetArrayLength());

            using var byEnv = await ReadJson(await client.GetAsync("/api/variables?env=qa"));
            CollectionAssert.AreEqual(new[] { "database.host" },
                byEnv.RootElement.GetProperty("variables").EnumerateArray().Select(v => v.GetProperty("key").GetString()).ToArray());

            using var declaredOnly = await ReadJson(await client.GetAsync("/api/variables?declaredOnly=true"));
            Assert.AreEqual(0, declaredOnly.RootElement.GetProperty("variables").GetArrayLength());

            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/variables?app=..x")).StatusCode);
        }

        [TestMethod]
        public async Task Variable_detail_includes_environment_matrix_and_404()
        {
            Seed("web", "{\"variables\":[{\"key\":\"database.host\",\"type\":\"string\",\"required\":true}]}");
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/api/variables/database.host");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains(SecretValue));
            using var doc = JsonDocument.Parse(body);
            Assert.AreEqual("database.host", doc.RootElement.GetProperty("key").GetString());
            var usedBy = doc.RootElement.GetProperty("usedBy").EnumerateArray().ToDictionary(u => u.GetProperty("app").GetString());
            var web = usedBy["web"].GetProperty("byEnvironment");
            Assert.AreEqual("inherited", web.GetProperty("dev").GetString());
            Assert.AreEqual("defined", web.GetProperty("qa").GetString());
            Assert.AreEqual("inherited", web.GetProperty("prod").GetString());
            Assert.AreEqual("defined", usedBy["api"].GetProperty("byEnvironment").GetProperty("prod").GetString());

            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/variables/does.not.exist")).StatusCode);
        }

        [TestMethod]
        public async Task Variable_detail_marks_required_but_absent_as_missing()
        {
            Seed("api", "{\"variables\":[{\"key\":\"queue.url\",\"type\":\"string\",\"required\":true}]}");
            var (client, factory) = Create();
            using var f_ = factory;

            using var doc = await ReadJson(await client.GetAsync("/api/variables/queue.url"));

            var matrix = doc.RootElement.GetProperty("usedBy")[0].GetProperty("byEnvironment");
            Assert.AreEqual("missing", matrix.GetProperty("prod").GetString());
            Assert.AreEqual("missing", matrix.GetProperty("dev").GetString());
        }

        // ---- reserved files ----

        [TestMethod]
        public async Task Reserved_files_are_hidden_and_protected_in_the_files_api()
        {
            Seed("web", "{\"variables\":[]}");
            var (client, factory) = Create();
            using var f_ = factory;

            using var list = await ReadJson(await client.GetAsync("/api/apps/web/files"));
            Assert.IsFalse(list.RootElement.EnumerateArray().Any(f => f.GetProperty("name").GetString().StartsWith("_")));

            var content = new StringContent("{\"content\":\"{}\"}", Encoding.UTF8, "application/json");
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsync("/api/apps/web/files/_variables.json", content)).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsync("/api/apps/web/files/_environments.json",
                new StringContent("{\"content\":\"{}\"}", Encoding.UTF8, "application/json"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/apps/web/files/_variables.json")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/apps/web/files/_variables.json")).StatusCode);
            Assert.IsTrue(File.Exists(Path.Combine(_dir, "web", "_variables.json")));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "web", "_environments.json")));
        }

        [TestMethod]
        public async Task Reserved_files_are_never_treated_as_overrides()
        {
            Seed("web", "{\"variables\":[]}");
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/config/web/_variables")).StatusCode);
            using var list = await ReadJson(await client.GetAsync("/api/environments"));
            Assert.IsFalse(list.RootElement.EnumerateArray().Any(e => e.GetProperty("name").GetString().StartsWith("_")));
        }

        [TestMethod]
        public async Task Corrupt_registry_is_reported_not_overwritten()
        {
            File.WriteAllText(Path.Combine(_dir, "_environments.json"), "{ broken");
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/environments")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PostAsync("/api/environments", Json("{\"name\":\"stg\"}"))).StatusCode);
            Assert.AreEqual("{ broken", Read("_environments.json"));

            // The index keeps working and records the problem as a warning.
            using var doc = await ReadJson(await client.GetAsync("/api/variables"));
            Assert.AreEqual("_environments.json", doc.RootElement.GetProperty("warnings")[0].GetProperty("file").GetString());
        }
    }
}
