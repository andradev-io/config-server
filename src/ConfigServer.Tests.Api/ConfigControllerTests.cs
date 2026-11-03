using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Application.Api;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Configuration.Formats;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    [TestClass]
    public class ConfigControllerTests
    {
        private sealed class FakeConfigSource : IConfigSource
        {
            public Dictionary<string, string> Defaults { get; } = new();
            public Dictionary<string, string> Overrides { get; } = new();

            private static ConfigDocument Json(string content) => content == null ? null : new ConfigDocument(content, ConfigFormat.Json);

            public Task<ConfigDocument> GetDefaultAsync(string application, CancellationToken cancellationToken = default)
                => Task.FromResult(Json(Defaults.GetValueOrDefault(application)));

            public Task<ConfigDocument> GetOverrideAsync(string application, string environment, CancellationToken cancellationToken = default)
                => Task.FromResult(Json(Overrides.GetValueOrDefault(application + "/" + environment)));
        }

        private static (HttpClient Client, WebApplicationFactory<Program> Factory) Create(FakeConfigSource source)
        {
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                b.ConfigureServices(s => s.AddSingleton<IConfigSource>(source)));
            return (factory.CreateClient(), factory);
        }

        [TestMethod]
        public async Task Merges_default_with_override()
        {
            var source = new FakeConfigSource();
            source.Defaults["app"] = "{\"a\":1,\"nested\":{\"x\":1,\"y\":2}}";
            source.Overrides["app/prod"] = "{\"nested\":{\"y\":3}}";
            var (client, factory) = Create(source);
            using var _ = factory;

            var response = await client.GetAsync("/config/app/prod");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(1, doc.RootElement.GetProperty("a").GetInt32());
            Assert.AreEqual(1, doc.RootElement.GetProperty("nested").GetProperty("x").GetInt32());
            Assert.AreEqual(3, doc.RootElement.GetProperty("nested").GetProperty("y").GetInt32());
        }

        [TestMethod]
        public async Task Missing_override_returns_404()
        {
            var source = new FakeConfigSource();
            source.Defaults["app"] = "{\"a\":1}";
            var (client, factory) = Create(source);
            using var _ = factory;

            var response = await client.GetAsync("/config/app/dev");

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }

        [TestMethod]
        public async Task Missing_default_returns_404()
        {
            var (client, factory) = Create(new FakeConfigSource());
            using var _ = factory;

            var response = await client.GetAsync("/config/nope/dev");

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }

        [TestMethod]
        public async Task Invalid_json_returns_400()
        {
            var source = new FakeConfigSource();
            source.Defaults["app"] = "{\"a\":1}";
            source.Overrides["app/dev"] = "{not json";
            var (client, factory) = Create(source);
            using var _ = factory;

            var response = await client.GetAsync("/config/app/dev");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [TestMethod]
        [DataRow("a..b", "dev")]
        [DataRow("a%5Cb", "dev")]
        [DataRow("%20", "dev")]
        [DataRow("app", "%20")]
        public async Task Bad_names_return_400(string application, string environment)
        {
            var source = new FakeConfigSource();
            source.Defaults["app"] = "{}";
            var (client, factory) = Create(source);
            using var _ = factory;

            var response = await client.GetAsync($"/config/{application}/{environment}");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Dot segments and encoded slashes are normalized by the server before routing, so test them on the action.
        [TestMethod]
        [DataRow("..", "dev")]
        [DataRow("app", "..")]
        [DataRow("a/b", "dev")]
        [DataRow("app", "d/ev")]
        [DataRow("app", "d\\ev")]
        [DataRow("", "dev")]
        [DataRow("app", null)]
        public async Task Controller_rejects_bad_names_directly(string application, string environment)
        {
            var controller = new ConfigServer.Application.Api.Controllers.ConfigController(new FakeConfigSource());

            var result = await controller.Get(application, environment, null, CancellationToken.None);

            Assert.IsInstanceOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        }
    }
}
