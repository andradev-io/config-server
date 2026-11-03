using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConfigServer.Application.Api;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ConfigServer.Tests.Api
{
    /// <summary>
    /// Test host in Auth:Mode=Oidc with a locally generated signing key (no network: the OIDC configuration is static)
    /// and an in-memory API key store. Config files live in a temp directory.
    /// </summary>
    internal sealed class AuthTestHost : IDisposable
    {
        public const string Issuer = "https://idp.test/realms/config";
        public const string Audience = "config-server";

        public readonly SymmetricSecurityKey SigningKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "test-key" };

        public string Dir { get; }
        public InMemoryApiKeyStore Keys { get; } = new InMemoryApiKeyStore();
        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }

        public AuthTestHost(string roleClaim = "realm_access.roles", IDictionary<string, string> settings = null,
            Action<IServiceCollection> configureServices = null)
        {
            Dir = Path.Combine(Path.GetTempPath(), "cfgauth-" + Guid.NewGuid().ToString("N"));
            Seed(Dir);

            var values = new Dictionary<string, string>
            {
                ["ConfigSource:Path"] = Dir,
                ["ConfigSource:AllowWrite"] = "true",
                ["Auth:Mode"] = "Oidc",
                ["Auth:Oidc:Authority"] = Issuer,
                ["Auth:Oidc:Audience"] = Audience,
                ["Auth:Oidc:ClientId"] = "config-web",
                ["Auth:Oidc:RoleClaim"] = roleClaim,
                ["Auth:Oidc:RoleMap:cfg-readers"] = "config:read",
                ["Auth:Oidc:RoleMap:cfg-writers"] = "config:write",
                ["Auth:Oidc:RoleMap:cfg-admins"] = "config:admin"
            };
            foreach (var pair in settings ?? new Dictionary<string, string>()) values[pair.Key] = pair.Value;

            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(values));
                b.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IApiKeyStore>();
                    services.AddSingleton<IApiKeyStore>(Keys);
                    // Configure (not PostConfigure) so the handler wraps it in a static configuration manager: no metadata fetch.
                    services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                    {
                        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                        configuration.SigningKeys.Add(SigningKey);
                        o.Configuration = configuration;
                    });
                    configureServices?.Invoke(services);
                });
            });
            Client = Factory.CreateClient();
        }

        public static void Seed(string dir)
        {
            Directory.CreateDirectory(Path.Combine(dir, "web"));
            Directory.CreateDirectory(Path.Combine(dir, "api"));
            File.WriteAllText(Path.Combine(dir, "_environments.json"),
                "{\"environments\":[{\"name\":\"dev\",\"order\":1},{\"name\":\"prod\",\"order\":2}]}");
            File.WriteAllText(Path.Combine(dir, "web", "default.json"), "{\"name\":\"web\",\"debug\":false}");
            File.WriteAllText(Path.Combine(dir, "web", "dev.json"), "{\"debug\":true}");
            File.WriteAllText(Path.Combine(dir, "web", "prod.json"), "{\"replicas\":3}");
            File.WriteAllText(Path.Combine(dir, "api", "default.json"), "{\"name\":\"api\"}");
            File.WriteAllText(Path.Combine(dir, "api", "prod.json"), "{\"replicas\":2}");
        }

        /// <summary>Signed access token. <paramref name="claims"/> are merged into the payload.</summary>
        public string Token(string[] roles = null, string audience = Audience, DateTimeOffset? expires = null, SecurityKey key = null,
            IDictionary<string, object> claims = null)
        {
            var now = DateTimeOffset.UtcNow;
            var payload = new Dictionary<string, object>
            {
                ["iss"] = Issuer,
                ["aud"] = audience,
                ["sub"] = "user-1",
                ["name"] = "Alice",
                ["iat"] = now.ToUnixTimeSeconds(),
                ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
                ["exp"] = (expires ?? now.AddMinutes(10)).ToUnixTimeSeconds()
            };
            if (roles != null) payload["realm_access"] = new Dictionary<string, object> { ["roles"] = roles };
            foreach (var pair in claims ?? new Dictionary<string, object>()) payload[pair.Key] = pair.Value;

            return new JsonWebTokenHandler().CreateToken(JsonSerializer.Serialize(payload),
                new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256));
        }

        public static HttpRequestMessage Request(HttpMethod method, string url, string bearer = null, string apiKey = null, string json = null)
        {
            var request = new HttpRequestMessage(method, url);
            if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (apiKey != null) request.Headers.Add("X-Api-Key", apiKey);
            if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return request;
        }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }
    }
}
