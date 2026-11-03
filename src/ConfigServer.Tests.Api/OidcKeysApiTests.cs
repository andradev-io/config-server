using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ConfigServer.Domain.Security.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    /// <summary>api/oidc-keys against a fake IIdentityAdmin: admin-only, validation, one-time secret, logging.</summary>
    [TestClass]
    public class OidcKeysApiTests
    {
        private const string Url = "/api/oidc-keys";

        private FakeIdentityAdmin _fake;
        private CapturingLoggerProvider _logs;
        private AuthTestHost _host;
        private string _admin;

        [TestInitialize]
        public void Setup()
        {
            _fake = new FakeIdentityAdmin();
            _logs = new CapturingLoggerProvider();
            _host = new AuthTestHost(configureServices: services =>
            {
                services.RemoveAll<IIdentityAdmin>();
                services.AddSingleton<IIdentityAdmin>(_fake);
                services.AddSingleton<ILoggerProvider>(_logs);
            });
            _admin = _host.Token(new[] { "config:admin" });
        }

        [TestCleanup]
        public void Cleanup() => _host.Dispose();

        private Task<HttpResponseMessage> Send(HttpMethod method, string url, string json = null, string bearer = null)
            => _host.Client.SendAsync(AuthTestHost.Request(method, url, bearer: bearer ?? _admin, json: json));

        private static async Task<JsonElement> Body(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string contains = null)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(status, response.StatusCode, text);
            var error = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
            if (contains != null) StringAssert.Contains(error, contains);
        }

        private const string Valid = "{\"name\":\"ci\",\"grants\":[{\"permission\":\"read\",\"app\":\"web\",\"environment\":\"prod\"}]}";

        public static IEnumerable<object[]> Endpoints => new[]
        {
            new object[] { "GET", Url, null },
            new object[] { "POST", Url, Valid },
            new object[] { "POST", Url + "/id-1/rotate", null },
            new object[] { "DELETE", Url + "/id-1", null }
        };

        [TestMethod]
        [DynamicData(nameof(Endpoints))]
        public async Task Requires_authentication(string method, string url, string json)
        {
            var response = await _host.Client.SendAsync(AuthTestHost.Request(new HttpMethod(method), url, json: json));

            await AssertError(response, HttpStatusCode.Unauthorized);
            Assert.IsEmpty(_fake.MachineCalls);
        }

        [TestMethod]
        [DynamicData(nameof(Endpoints))]
        public async Task Requires_global_admin(string method, string url, string json)
        {
            foreach (var roles in new[] { new[] { "config:write" }, new[] { "config:read:web" }, new[] { "config:admin:web" } })
                await AssertError(await Send(new HttpMethod(method), url, json, _host.Token(roles)), HttpStatusCode.Forbidden);
            Assert.IsEmpty(_fake.MachineCalls);
        }

        [TestMethod]
        public async Task Api_keys_cannot_manage_oidc_keys()
        {
            var created = await Send(HttpMethod.Post, "/api/keys", "{\"name\":\"ci\",\"permission\":\"write\"}");
            var secret = (await Body(created)).GetProperty("secret").GetString();

            var response = await _host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, Url, apiKey: secret));

            await AssertError(response, HttpStatusCode.Forbidden);
            Assert.IsEmpty(_fake.MachineCalls);
        }

        [TestMethod]
        public async Task Disabled_provider_answers_501_before_validating()
        {
            _fake.Enabled = false;

            foreach (var endpoint in Endpoints)
                await AssertError(await Send(new HttpMethod((string)endpoint[0]), (string)endpoint[1], (string)endpoint[2]), HttpStatusCode.NotImplemented, "IdentityAdmin");
            await AssertError(await Send(HttpMethod.Post, Url, "{}"), HttpStatusCode.NotImplemented);
            Assert.IsEmpty(_fake.MachineCalls);
        }

        [TestMethod]
        public async Task Lists_the_credentials_without_any_secret()
        {
            await Send(HttpMethod.Post, Url, Valid);

            var response = await Send(HttpMethod.Get, Url);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(text.Contains(FakeIdentityAdmin.MachineSecret));
            var item = JsonDocument.Parse(text).RootElement.EnumerateArray().Single();
            Assert.AreEqual("ci", item.GetProperty("name").GetString());
            Assert.AreEqual("cfs-ci-abc123", item.GetProperty("clientId").GetString());
            Assert.IsTrue(item.GetProperty("enabled").GetBoolean());
            var grant = item.GetProperty("grants").EnumerateArray().Single();
            Assert.AreEqual("config:read:web:prod", grant.GetProperty("role").GetString());
            Assert.IsFalse(item.TryGetProperty("secret", out _));
        }

        [TestMethod]
        public async Task Create_returns_201_with_the_secret_token_endpoint_and_logs_without_the_secret()
        {
            var response = await Send(HttpMethod.Post, Url,
                "{\"name\":\"  ci  \",\"grants\":[{\"permission\":\"read\",\"app\":\"web\"},{\"permission\":\"write\",\"app\":\"api\",\"environment\":\"prod\"}]}");

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            var body = await Body(response);
            Assert.AreEqual(FakeIdentityAdmin.MachineSecret, body.GetProperty("secret").GetString());
            Assert.AreEqual("cfs-ci-abc123", body.GetProperty("clientId").GetString());
            Assert.AreEqual(FakeIdentityAdmin.TokenEndpoint, body.GetProperty("tokenEndpoint").GetString());
            Assert.AreEqual("ci", body.GetProperty("client").GetProperty("name").GetString());
            Assert.AreEqual(2, body.GetProperty("client").GetProperty("grants").GetArrayLength());

            var call = _fake.MachineCalls.Single(c => c.Op == "create");
            Assert.AreEqual("ci", call.Name);
            CollectionAssert.AreEqual(new[] { "config:read:web", "config:write:api:prod" }, call.Grants.Select(g => g.RoleName).ToArray());

            var all = string.Join("\n", _logs.Entries.Select(e => e.Message));
            Assert.IsFalse(all.Contains(FakeIdentityAdmin.MachineSecret), "the secret must never be logged");
            var log = _logs.Entries.Single(e => e.Category.EndsWith("OidcKeysController") && e.Level == LogLevel.Information);
            StringAssert.Contains(log.Message, "Alice");
            StringAssert.Contains(log.Message, "cfs-ci-abc123");
        }

        [TestMethod]
        public async Task Create_deduplicates_grants_and_accepts_wildcards()
        {
            var response = await Send(HttpMethod.Post, Url,
                "{\"name\":\"ci\",\"grants\":[{\"permission\":\"READ\"},{\"permission\":\"read\",\"app\":\"*\",\"environment\":\"*\"}]}");

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual("config:read", _fake.MachineCalls.Single(c => c.Op == "create").Grants.Single().RoleName);
        }

        [TestMethod]
        [DataRow("", "JSON object")]
        [DataRow("{}", "name")]
        [DataRow("{\"name\":\"ci\"}", "At least one grant")]
        [DataRow("{\"name\":\"ci\",\"grants\":[]}", "At least one grant")]
        [DataRow("{\"name\":\"ci\",\"grants\":[null]}", "Every grant")]
        [DataRow("{\"name\":\"ci\",\"grants\":[{}]}", "permission")]
        [DataRow("{\"name\":\"ci\",\"grants\":[{\"permission\":\"owner\"}]}", "Unknown permission")]
        [DataRow("{\"name\":\"ci\",\"grants\":[{\"permission\":\"admin\"}]}", "cannot hold config:admin")]
        [DataRow("{\"name\":\"ci\",\"grants\":[{\"permission\":\"read\",\"app\":\"../etc\"}]}", "Invalid application name")]
        [DataRow("{\"name\":\"ci\",\"grants\":[{\"permission\":\"read\",\"app\":\"nope\"}]}", "Unknown application 'nope'")]
        [DataRow("{\"name\":\"ci\",\"grants\":[{\"permission\":\"read\",\"app\":\"Web\"}]}", "did you mean 'web'")]
        [DataRow("{\"name\":\"ci\",\"grants\":[{\"permission\":\"read\",\"environment\":\"qa\"}]}", "Unknown environment 'qa'")]
        public async Task Create_validation_errors_are_400(string json, string message)
        {
            var request = AuthTestHost.Request(HttpMethod.Post, Url, bearer: _admin, json: json);
            if (json == "") request.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");

            await AssertError(await _host.Client.SendAsync(request), HttpStatusCode.BadRequest, message);
            Assert.IsEmpty(_fake.MachineCalls);
        }

        [TestMethod]
        public async Task Create_rejects_overlong_or_control_character_names()
        {
            var grants = "\"grants\":[{\"permission\":\"read\"}]";
            await AssertError(await Send(HttpMethod.Post, Url, $"{{\"name\":\"{new string('a', 101)}\",{grants}}}"), HttpStatusCode.BadRequest, "name");
            await AssertError(await Send(HttpMethod.Post, Url, $"{{\"name\":\"a\\nb\",{grants}}}"), HttpStatusCode.BadRequest, "name");
            await AssertError(await Send(HttpMethod.Post, Url, $"{{\"name\":\"   \",{grants}}}"), HttpStatusCode.BadRequest, "name");
            Assert.IsEmpty(_fake.MachineCalls);
        }

        [TestMethod]
        public async Task Rotate_returns_a_new_secret_and_404_for_unknown_credentials()
        {
            await Send(HttpMethod.Post, Url, Valid);

            var response = await Send(HttpMethod.Post, Url + "/id-1/rotate");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var body = await Body(response);
            Assert.AreEqual(FakeIdentityAdmin.MachineSecret + "-2", body.GetProperty("secret").GetString());
            Assert.AreEqual(FakeIdentityAdmin.TokenEndpoint, body.GetProperty("tokenEndpoint").GetString());
            Assert.IsFalse(_logs.Entries.Any(e => e.Message.Contains(FakeIdentityAdmin.MachineSecret)), "the secret must never be logged");

            await AssertError(await Send(HttpMethod.Post, Url + "/nope/rotate"), HttpStatusCode.NotFound, "nope");
        }

        [TestMethod]
        public async Task Delete_is_idempotent()
        {
            await Send(HttpMethod.Post, Url, Valid);

            Assert.AreEqual(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, Url + "/id-1")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, Url + "/id-1")).StatusCode);
            Assert.IsEmpty(_fake.Machines);
        }

        [TestMethod]
        public async Task Provider_failures_are_502_with_the_message()
        {
            _fake.Failure = new IdentityAdminException(IdentityAdminErrorKind.Denied,
                "The admin service account lacks permission to create the machine credential: grant 'manage-clients'.");

            await AssertError(await Send(HttpMethod.Get, Url), HttpStatusCode.BadGateway, "manage-clients");
            await AssertError(await Send(HttpMethod.Post, Url, Valid), HttpStatusCode.BadGateway, "manage-clients");
        }

        [TestMethod]
        public async Task Info_reports_the_token_endpoint()
        {
            var info = await Body(await Send(HttpMethod.Get, "/api/access/info"));

            Assert.AreEqual(FakeIdentityAdmin.TokenEndpoint, info.GetProperty("tokenEndpoint").GetString());
        }

        [TestMethod]
        public async Task Keycloak_token_endpoint_is_derived_from_the_issuer_not_the_admin_url()
        {
            using var host = new AuthTestHost(settings: new Dictionary<string, string>
            {
                ["IdentityAdmin:Provider"] = "Keycloak",
                ["IdentityAdmin:Keycloak:BaseUrl"] = "http://localhost:18080/",
                ["IdentityAdmin:Keycloak:Realm"] = "acme",
                ["IdentityAdmin:Keycloak:AdminClientId"] = "config-manager-admin",
                ["IdentityAdmin:Keycloak:AdminClientSecret"] = "s3cret"
            });

            var response = await host.Client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/api/access/info", bearer: host.Token(new[] { "config:admin" })));

            var info = await Body(response);
            Assert.AreEqual(AuthTestHost.Issuer + "/protocol/openid-connect/token", info.GetProperty("tokenEndpoint").GetString());
        }
    }
}
