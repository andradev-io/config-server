using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using ConfigServer.Application.Api;
using ConfigServer.Application.Api.Controllers;
using ConfigServer.Domain.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    [TestClass]
    public class FilesAndFormatsApiTests
    {
        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cfgapi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "app"));
            File.WriteAllText(Path.Combine(_dir, "app", "default.json"), "{\"a\":1,\"n\":{\"x\":1,\"y\":2}}");
            File.WriteAllText(Path.Combine(_dir, "app", "prod.yaml"), "n:\n  y: 3\nflag: true\n");
            File.WriteAllText(Path.Combine(_dir, "app", "qa.env"), "n__y=9\nname=\"two words\"\n");
            Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        // In-memory config is added last so it overrides appsettings.*.json (UseSetting would not).
        private void Configure(IWebHostBuilder builder, bool allowWrite)
            => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("ConfigSource:Path", _dir),
                new System.Collections.Generic.KeyValuePair<string, string>("ConfigSource:AllowWrite", allowWrite ? "true" : "false")
            }));

        private (HttpClient Client, WebApplicationFactory<Program> Factory) Create(bool allowWrite = true)
        {
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                Configure(b, allowWrite);
            });
            return (factory.CreateClient(), factory);
        }

        private static StringContent Body(string content)
            => new StringContent(JsonSerializer.Serialize(new { content }), System.Text.Encoding.UTF8, "application/json");

        // ---- GET /config ----

        [TestMethod]
        public async Task Config_json_default_merges_json_and_yaml()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/config/app/prod");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(1, doc.RootElement.GetProperty("n").GetProperty("x").GetInt32());
            Assert.AreEqual(3, doc.RootElement.GetProperty("n").GetProperty("y").GetInt32());
            Assert.IsTrue(doc.RootElement.GetProperty("flag").GetBoolean());
        }

        [TestMethod]
        public async Task Config_yaml_format()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/config/app/prod?format=yaml");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/yaml", response.Content.Headers.ContentType?.MediaType);
            var text = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(text, "flag: true");
            StringAssert.Contains(text, "y: 3");
        }

        [TestMethod]
        public async Task Config_env_format_from_env_override()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/config/app/qa?format=env");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/plain", response.Content.Headers.ContentType?.MediaType);
            var text = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(text, "a=1");
            StringAssert.Contains(text, "n__x=1");
            StringAssert.Contains(text, "n__y=9");
            StringAssert.Contains(text, "name=\"two words\"");
        }

        [TestMethod]
        public async Task Config_unknown_format_is_400()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/config/app/prod?format=xml")).StatusCode);
        }

        [TestMethod]
        public async Task Config_invalid_content_is_400_with_line()
        {
            File.WriteAllText(Path.Combine(_dir, "app", "bad.env"), "A=1\nB=2\nbroken line\n");
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/config/app/bad");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(3, doc.RootElement.GetProperty("line").GetInt32());
            Assert.IsTrue(doc.RootElement.TryGetProperty("error", out _));
            Assert.IsTrue(doc.RootElement.TryGetProperty("column", out _));
        }

        [TestMethod]
        public async Task Config_missing_override_is_404()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/config/app/dev")).StatusCode);
        }

        // ---- files API: reads ----

        [TestMethod]
        public async Task Lists_apps_and_files()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var apps = await client.GetFromJsonAsync<string[]>("/api/apps");
            CollectionAssert.AreEqual(new[] { "app" }, apps);

            var response = await client.GetAsync("/api/apps/app/files");
            Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = doc.RootElement.EnumerateArray().ToList();
            CollectionAssert.AreEqual(new[] { "default.json", "prod.yaml", "qa.env" },
                items.Select(i => i.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual("yaml", items[1].GetProperty("format").GetString());
            Assert.IsTrue(items[0].GetProperty("size").GetInt64() > 0);
        }

        [TestMethod]
        public async Task Get_file_returns_raw_text_and_404()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.GetAsync("/api/apps/app/files/prod.yaml");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("n:\n  y: 3\nflag: true\n", await response.Content.ReadAsStringAsync());

            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/apps/app/files/nope.json")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/apps/missing/files")).StatusCode);
        }

        // ---- files API: writes ----

        [TestMethod]
        public async Task Put_creates_then_updates_with_lf_and_no_bom()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var created = await client.PutAsync("/api/apps/app/files/dev.json", Body("{\r\n  \"a\": 2\r\n}"));
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

            var path = Path.Combine(_dir, "app", "dev.json");
            var bytes = File.ReadAllBytes(path);
            Assert.AreNotEqual((byte)0xEF, bytes[0]);
            Assert.IsFalse(bytes.Contains((byte)'\r'));
            Assert.AreEqual("{\n  \"a\": 2\n}", File.ReadAllText(path));

            var updated = await client.PutAsync("/api/apps/app/files/dev.json", Body("{\"a\":3}"));
            Assert.AreEqual(HttpStatusCode.NoContent, updated.StatusCode);
            Assert.AreEqual("{\"a\":3}", File.ReadAllText(path));
        }

        [TestMethod]
        public async Task Put_creates_new_app()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var created = await client.PutAsync("/api/apps/fresh/files/default.yml", Body("a: 1\n"));
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            CollectionAssert.Contains(await client.GetFromJsonAsync<string[]>("/api/apps"), "fresh");
        }

        [TestMethod]
        public async Task Put_invalid_content_is_400_with_line()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.PutAsync("/api/apps/app/files/dev.env", Body("A=1\nB=2\nbroken\n"));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(3, doc.RootElement.GetProperty("line").GetInt32());
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "app", "dev.env")));
        }

        [TestMethod]
        public async Task Put_same_logical_name_other_extension_is_409()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            var response = await client.PutAsync("/api/apps/app/files/prod.json", Body("{}"));

            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "app", "prod.json")));
        }

        [TestMethod]
        public async Task Writes_forbidden_when_not_allowed()
        {
            var (client, factory) = Create(allowWrite: false);
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PutAsync("/api/apps/app/files/dev.json", Body("{}"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/apps/app/files/prod.yaml")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/apps/app/files")).StatusCode);
            Assert.IsTrue(File.Exists(Path.Combine(_dir, "app", "prod.yaml")));
        }

        [TestMethod]
        [DataRow("a..b", "dev.json")]
        [DataRow("app", "dev.txt")]
        [DataRow("app", "dev")]
        [DataRow("app", ".json")]
        [DataRow("app", "-x.json")]
        [DataRow("app", "a.b.json")]
        [DataRow("app", "..%5Cx.json")]
        [DataRow("app", "%2E%2E.json")]
        [DataRow(".hidden", "dev.json")]
        public async Task Bad_names_are_400(string app, string file)
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsync($"/api/apps/{app}/files/{file}", Body("{}"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/apps/{app}/files/{file}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/apps/{app}/files/{file}")).StatusCode);
        }

        [TestMethod]
        [DataRow("..", "x.json")]
        [DataRow("a/b", "x.json")]
        [DataRow("app", "../x.json")]
        [DataRow("app", "..\\x.json")]
        [DataRow("app", "")]
        public async Task Controller_rejects_traversal_directly(string app, string file)
        {
            var store = new Application.Api.Configuration.FileSystemConfigSource(_dir);
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string, string>("ConfigSource:AllowWrite", "true") })
                .Build();
            var controller = new FilesController(configuration, store);

            var put = await controller.PutFile(app, file, new WriteFileRequest { Content = "{}" }, default);
            var get = await controller.GetFile(app, file, default);

            Assert.IsInstanceOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(put);
            Assert.IsInstanceOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(get);
            await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(app, file, "{}"));
        }

        [TestMethod]
        public async Task Delete_removes_file_and_404s_when_missing()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/apps/app/files/qa.env")).StatusCode);
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "app", "qa.env")));
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/apps/app/files/qa.env")).StatusCode);
        }

        [TestMethod]
        public async Task Delete_default_conflicts_while_overrides_exist()
        {
            var (client, factory) = Create();
            using var f_ = factory;

            Assert.AreEqual(HttpStatusCode.Conflict, (await client.DeleteAsync("/api/apps/app/files/default.json")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/apps/app/files/prod.yaml")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/apps/app/files/qa.env")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/apps/app/files/default.json")).StatusCode);
        }

        [TestMethod]
        public async Task Returns_501_when_no_store_registered()
        {
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                Configure(b, true);
                b.ConfigureServices(s =>
                {
                    var descriptor = s.Single(d => d.ServiceType == typeof(IConfigStore));
                    s.Remove(descriptor);
                });
            });
            using var f_ = factory;
            var client = factory.CreateClient();

            Assert.AreEqual(HttpStatusCode.NotImplemented, (await client.GetAsync("/api/apps")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotImplemented, (await client.PutAsync("/api/apps/app/files/dev.json", Body("{}"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotImplemented, (await client.DeleteAsync("/api/apps/app/files/prod.yaml")).StatusCode);
        }
    }
}
