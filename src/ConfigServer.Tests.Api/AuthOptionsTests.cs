using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ConfigServer.Application.Api;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    [TestClass]
    public class AuthOptionsTests
    {
        private static IConfiguration Config(params (string Key, string Value)[] values)
            => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value))).Build();

        private static IHostEnvironment Env(string name) => new HostingEnvironment { EnvironmentName = name };

        private static readonly (string, string)[] ValidOidc =
        {
            ("Auth:Mode", "Oidc"), ("Auth:Oidc:Authority", "https://idp.example.com/realms/x"), ("Auth:Oidc:Audience", "config-server")
        };

        private static void AssertInvalid(IConfiguration configuration, string environment, string expectedFragment)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => AuthOptions.Load(configuration).Validate(Env(environment)));
            StringAssert.Contains(ex.Message, expectedFragment);
        }

        [TestMethod]
        public void Defaults_are_oidc_with_standard_scopes_and_roles_claim()
        {
            var options = AuthOptions.Load(Config());

            Assert.AreEqual(AuthMode.Oidc, options.Mode);
            CollectionAssert.AreEqual(new[] { "openid", "profile" }, options.Oidc.Scopes);
            Assert.AreEqual("roles", options.Oidc.RoleClaim);
            Assert.IsTrue(options.Oidc.RequireHttpsMetadata);
            AssertInvalid(Config(), Environments.Production, "Auth:Oidc:Authority");
        }

        [TestMethod]
        public void Mode_none_is_allowed_only_in_development()
        {
            AuthOptions.Load(Config(("Auth:Mode", "none"))).Validate(Env(Environments.Development));

            AssertInvalid(Config(("Auth:Mode", "None")), Environments.Production, "Development");
            AssertInvalid(Config(("Auth:Mode", "None")), Environments.Staging, "Development");
        }

        [TestMethod]
        public void Unknown_mode_is_rejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => AuthOptions.Load(Config(("Auth:Mode", "Basic"))));
            StringAssert.Contains(ex.Message, "Auth:Mode");
        }

        [TestMethod]
        public void Valid_oidc_options_pass_and_bind_role_map_and_scopes()
        {
            var options = AuthOptions.Load(Config(ValidOidc.Concat(new[]
            {
                ("Auth:Oidc:Scopes:0", "openid"), ("Auth:Oidc:Scopes:1", "email"),
                ("Auth:Oidc:RoleClaim", "realm_access.roles"), ("Auth:Oidc:RoleMap:Cfg-Admins", "config:admin")
            }).ToArray()));

            options.Validate(Env(Environments.Production));
            CollectionAssert.AreEqual(new[] { "openid", "email" }, options.Oidc.Scopes);
            Assert.AreEqual("realm_access.roles", options.Oidc.RoleClaim);
            Assert.AreEqual("config:admin", options.Oidc.RoleMap["cfg-admins"]);
        }

        [TestMethod]
        public void Authority_must_be_https_unless_metadata_https_is_disabled()
        {
            var http = new[] { ("Auth:Mode", "Oidc"), ("Auth:Oidc:Authority", "http://localhost:8080/realms/x"), ("Auth:Oidc:Audience", "a") };
            AssertInvalid(Config(http), Environments.Production, "https");

            AuthOptions.Load(Config(http.Append(("Auth:Oidc:RequireHttpsMetadata", "false")).ToArray())).Validate(Env(Environments.Production));
            AssertInvalid(Config(("Auth:Oidc:Authority", "not a url"), ("Auth:Oidc:Audience", "a")), Environments.Production, "absolute");
        }

        [TestMethod]
        public void Audience_falls_back_to_client_id_and_one_is_required()
        {
            var options = AuthOptions.Load(Config(("Auth:Oidc:Authority", "https://idp"), ("Auth:Oidc:ClientId", "spa")));
            options.Validate(Env(Environments.Production));
            CollectionAssert.AreEqual(new[] { "spa" }, options.Oidc.ValidAudiences.ToArray());

            AssertInvalid(Config(("Auth:Oidc:Authority", "https://idp")), Environments.Production, "Audience");
        }

        [TestMethod]
        public void Role_map_values_must_be_config_roles()
        {
            AssertInvalid(Config(ValidOidc.Append(("Auth:Oidc:RoleMap:admins", "superuser")).ToArray()), Environments.Production, "RoleMap");
        }

        [TestMethod]
        public void Role_map_accepts_scoped_roles_but_not_scoped_admin()
        {
            var options = AuthOptions.Load(Config(ValidOidc.Append(("Auth:Oidc:RoleMap:billing-devs", "config:write:billing-service:dev, config:read:*:stg")).ToArray()));
            options.Validate(Env(Environments.Production));

            AssertInvalid(Config(ValidOidc.Append(("Auth:Oidc:RoleMap:ops", "config:admin:billing-service")).ToArray()), Environments.Production, "RoleMap");
            AssertInvalid(Config(ValidOidc.Append(("Auth:Oidc:RoleMap:ops", "config:read:bad/app")).ToArray()), Environments.Production, "RoleMap");
        }

        [TestMethod]
        public void Role_claim_paths_resolve_client_id_and_require_it()
        {
            var options = AuthOptions.Load(Config(ValidOidc.Concat(new[]
            {
                ("Auth:Oidc:ClientId", "config-web"), ("Auth:Oidc:RoleClaim", "roles, realm_access.roles, resource_access.{clientId}.roles")
            }).ToArray()));

            options.Validate(Env(Environments.Production));
            CollectionAssert.AreEqual(new[] { "roles", "realm_access.roles", "resource_access.config-web.roles" }, options.Oidc.RoleClaimPaths.ToArray());
            CollectionAssert.AreEqual(new[] { "roles" }, AuthOptions.Load(Config()).Oidc.RoleClaimPaths.ToArray());

            AssertInvalid(Config(ValidOidc.Append(("Auth:Oidc:RoleClaim", "resource_access.{clientId}.roles")).ToArray()), Environments.Production, "ClientId");
            AssertInvalid(Config(ValidOidc.Append(("Auth:Oidc:RoleClaim", " , ")).ToArray()), Environments.Production, "RoleClaim");
        }

        [TestMethod]
        public void Host_fails_to_start_with_mode_none_outside_development()
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment(Environments.Production);
                b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string> { ["Auth:Mode"] = "None" }));
            });

            var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
            StringAssert.Contains(ex.Message, "Development");
        }

        [TestMethod]
        public void Host_fails_to_start_when_key_store_is_inside_config_source()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cfgstore-" + Guid.NewGuid().ToString("N"));
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["ConfigSource:Path"] = dir,
                    ["ApiKeys:StorePath"] = Path.Combine(dir, "secrets", "keys.json")
                })));

            var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
            StringAssert.Contains(ex.Message, "ApiKeys:StorePath");
        }

        [TestMethod]
        public async Task Mode_none_is_admin_and_api_keys_still_apply_their_scope()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cfgnone-" + Guid.NewGuid().ToString("N"));
            AuthTestHost.Seed(dir);
            var store = new InMemoryApiKeyStore();
            try
            {
                using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
                {
                    b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string> { ["ConfigSource:Path"] = dir }));
                    b.ConfigureTestServices(s =>
                    {
                        s.RemoveAll<IApiKeyStore>();
                        s.AddSingleton<IApiKeyStore>(store);
                    });
                });
                var client = factory.CreateClient();

                using (var info = JsonDocument.Parse(await client.GetStringAsync("/api/auth/info")))
                {
                    Assert.AreEqual("none", info.RootElement.GetProperty("mode").GetString());
                    Assert.AreEqual(JsonValueKind.Null, info.RootElement.GetProperty("oidc").ValueKind);
                    Assert.IsTrue(info.RootElement.GetProperty("apiKeys").GetBoolean());
                }
                using (var me = JsonDocument.Parse(await client.GetStringAsync("/api/auth/me")))
                {
                    Assert.AreEqual("local", me.RootElement.GetProperty("name").GetString());
                    Assert.AreEqual("none", me.RootElement.GetProperty("via").GetString());
                    CollectionAssert.Contains(me.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToArray(), "config:admin");
                }

                var created = await client.PostAsync("/api/keys", new StringContent("{\"name\":\"web\",\"apps\":[\"web\"]}", System.Text.Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
                using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                var secret = doc.RootElement.GetProperty("secret").GetString();

                Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/config/web/prod", apiKey: secret))).StatusCode);
                Assert.AreEqual(HttpStatusCode.Forbidden, (await client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/config/api/prod", apiKey: secret))).StatusCode);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(AuthTestHost.Request(HttpMethod.Get, "/config/web/prod", apiKey: "cfs_bad"))).StatusCode);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
