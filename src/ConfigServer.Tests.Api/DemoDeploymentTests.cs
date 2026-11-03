using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Application.Api.Configuration;
using ConfigServer.Application.Api.Demo;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    /// <summary>Settings the sandbox deployment relies on: internal OIDC metadata, forwarded headers, token endpoint override, no stray writes.</summary>
    [TestClass]
    public class DemoDeploymentTests
    {
        private DemoHost _demo;

        [TestCleanup]
        public void Cleanup() => _demo?.Dispose();

        private static IConfiguration Config(params (string Key, string Value)[] values)
            => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value))).Build();

        private static readonly (string, string)[] Oidc =
        {
            ("Auth:Mode", "Oidc"), ("Auth:Oidc:Authority", "https://idp.example.com/realms/x"), ("Auth:Oidc:Audience", "config-server")
        };

        private static readonly HostingEnvironment Production = new HostingEnvironment { EnvironmentName = "Production" };

        private static InvalidOperationException Invalid(params (string, string)[] extra)
            => Assert.Throws<InvalidOperationException>(() => AuthOptions.Load(Config(Oidc.Concat(extra).ToArray())).Validate(Production));

        [TestMethod]
        public void Metadata_address_is_optional_and_validated()
        {
            Assert.IsNull(AuthOptions.Load(Config(Oidc)).Oidc.MetadataAddress);

            StringAssert.Contains(Invalid(("Auth:Oidc:MetadataAddress", "not a url")).Message, "Auth:Oidc:MetadataAddress");
            StringAssert.Contains(Invalid(("Auth:Oidc:MetadataAddress", "ftp://idp/x")).Message, "http(s)");
            StringAssert.Contains(Invalid(("Auth:Oidc:MetadataAddress", "http://idp.internal/.well-known/openid-configuration")).Message, "https");

            var internalHttp = AuthOptions.Load(Config(
                ("Auth:Mode", "Oidc"), ("Auth:Oidc:Authority", "https://idp.example.com/realms/x"), ("Auth:Oidc:Audience", "config-server"),
                ("Auth:Oidc:MetadataAddress", "http://idp.internal:9000/idp/.well-known/openid-configuration"), ("Auth:Oidc:RequireHttpsMetadata", "false")));
            internalHttp.Validate(Production);
            Assert.AreEqual("http://idp.internal:9000/idp/.well-known/openid-configuration", internalHttp.Oidc.MetadataAddress);
        }

        [TestMethod]
        public async Task Metadata_address_reaches_the_jwt_bearer_options_and_tokens_still_validate()
        {
            const string address = "https://idp.internal/idp/.well-known/openid-configuration";
            _demo = new DemoHost(new Dictionary<string, string> { ["Auth:Oidc:MetadataAddress"] = address });
            var bearer = _demo.Host.Factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
            Assert.AreEqual(address, bearer.MetadataAddress);
            Assert.AreEqual(AuthTestHost.Issuer, bearer.Authority);
            Assert.AreEqual(HttpStatusCode.OK, (await _demo.Send(HttpMethod.Get, "/api/apps", _demo.Visitor("alice"))).StatusCode);
        }

        private async Task<HttpStatusCode> From(string ip)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/info");
            request.Headers.Add("X-Forwarded-For", ip);
            return (await _demo.Host.Client.SendAsync(request)).StatusCode;
        }

        [TestMethod]
        public async Task Forwarded_for_gives_each_anonymous_client_its_own_rate_limit_bucket()
        {
            _demo = new DemoHost(new Dictionary<string, string>
            {
                ["Demo:RequestsPerMinute"] = "2", ["ForwardedHeaders:Enabled"] = "true", ["ForwardedHeaders:TrustAllProxies"] = "true"
            });

            Assert.AreEqual(HttpStatusCode.OK, await From("203.0.113.1"));
            Assert.AreEqual(HttpStatusCode.OK, await From("203.0.113.1"));
            Assert.AreEqual(HttpStatusCode.TooManyRequests, await From("203.0.113.1"));
            Assert.AreEqual(HttpStatusCode.OK, await From("203.0.113.2"), "another client address is another bucket");
        }

        [TestMethod]
        public async Task Without_forwarded_headers_anonymous_callers_share_one_bucket()
        {
            _demo = new DemoHost(new Dictionary<string, string> { ["Demo:RequestsPerMinute"] = "2" });

            Assert.AreEqual(HttpStatusCode.OK, await From("203.0.113.1"));
            Assert.AreEqual(HttpStatusCode.OK, await From("203.0.113.2"));
            Assert.AreEqual(HttpStatusCode.TooManyRequests, await From("203.0.113.3"), "a spoofed header is ignored unless forwarding is enabled");
        }

        [TestMethod]
        public void Forwarded_headers_settings_are_validated()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
                .AddConfigServerForwardedHeaders(Config(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownProxies:0", "nope"))));
            StringAssert.Contains(ex.Message, "KnownProxies");
            ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
                .AddConfigServerForwardedHeaders(Config(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownNetworks:0", "10.0.0.0"))));
            StringAssert.Contains(ex.Message, "KnownNetworks");
            new ServiceCollection().AddConfigServerForwardedHeaders(Config(("ForwardedHeaders:Enabled", "true"), ("ForwardedHeaders:KnownNetworks:0", "10.42.0.0/16")));
        }

        [TestMethod]
        public async Task Token_endpoint_can_be_overridden_for_the_sandbox_identity_provider()
        {
            _demo = new DemoHost(new Dictionary<string, string> { ["Demo:TokenEndpoint"] = "https://demo.example/idp/token" });
            var alice = _demo.Visitor("alice");
            var info = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/access/info", alice));
            Assert.AreEqual("https://demo.example/idp/token", info.GetProperty("tokenEndpoint").GetString());

            var created = await DemoHost.Json(await _demo.Send(HttpMethod.Post, "/api/oidc-keys", alice,
                json: "{\"name\":\"bot\",\"grants\":[{\"permission\":\"read\"}]}"));
            Assert.AreEqual("https://demo.example/idp/token", created.GetProperty("tokenEndpoint").GetString());
        }

        [TestMethod]
        public async Task The_sandbox_writes_only_under_the_workspaces_directory()
        {
            var stray = Path.Combine(Path.GetTempPath(), "cfgdemo-stray-" + Guid.NewGuid().ToString("N"), "api-keys.json");
            _demo = new DemoHost(new Dictionary<string, string> { ["ApiKeys:StorePath"] = stray });
            var alice = _demo.Visitor("alice");
            Assert.AreEqual(HttpStatusCode.Created, (await _demo.Send(HttpMethod.Post, "/api/keys", alice, json: "{\"name\":\"ci\"}")).StatusCode);
            await _demo.Put(alice, "notifications", "dev.json", "{}");

            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(stray)), "the regular API key file is never used in the sandbox");
            Assert.IsInstanceOfType<DemoApiKeyStore>(_demo.Host.Factory.Services.GetRequiredService<IApiKeyStore>());
            Assert.IsTrue(File.Exists(Path.Combine(_demo.Workspaces, DemoWorkspaces.IdFor("alice"), "state", "api-keys.json")));
            Assert.AreEqual(HttpStatusCode.OK, (await _demo.Send(HttpMethod.Get, "/health")).StatusCode, "/health is anonymous");
        }
    }
}
