using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ConfigServer.Application.Api.Configuration;
using ConfigServer.Domain.Git;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConfigServer.Application.Api.Authentication
{
    public static class AuthServiceCollectionExtensions
    {
        public const string ApiKeyStorePathKey = "ApiKeys:StorePath";
        public const string DefaultApiKeyStorePath = "data/api-keys.json";

        /// <summary>
        /// Authentication (Auth:Mode None|Oidc + API keys), the config:read/write/admin policies and the API key scope filter.
        /// Throws <see cref="InvalidOperationException"/> on invalid Auth options or an API key store inside the config repository.
        /// </summary>
        public static IServiceCollection AddConfigServerAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
        {
            var options = AuthOptions.Load(configuration);
            options.Validate(environment);
            services.AddSingleton(options);

            AddApiKeys(services, configuration, environment);

            var authentication = services.AddAuthentication(AuthSchemes.Selector)
                .AddPolicyScheme(AuthSchemes.Selector, "API key or bearer token", o =>
                {
                    o.ForwardDefaultSelector = context =>
                        ApiKeyAuthenticationHandler.HasApiKey(context.Request) ? AuthSchemes.ApiKey
                        : options.Mode == AuthMode.Oidc ? AuthSchemes.Bearer
                        : AuthSchemes.None;
                })
                .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, null);

            if (options.Mode == AuthMode.None)
                authentication.AddScheme<AuthenticationSchemeOptions, NoAuthenticationHandler>(AuthSchemes.None, null);
            else
                authentication.AddJwtBearer(AuthSchemes.Bearer, jwt => ConfigureJwtBearer(jwt, options.Oidc));

            // Coarse policies (permission held in at least one scope); AccessScopeFilter checks the app/environment.
            services.AddAuthorization(o =>
            {
                o.AddPolicy(ConfigPolicies.Read, p => p.RequireAuthenticatedUser().RequireClaim(ConfigClaimTypes.Role, ConfigPolicies.Read));
                o.AddPolicy(ConfigPolicies.Write, p => p.RequireAuthenticatedUser().RequireClaim(ConfigClaimTypes.Role, ConfigPolicies.Write));
                o.AddPolicy(ConfigPolicies.Admin, p => p.RequireAuthenticatedUser()
                    .RequireClaim(ConfigClaimTypes.Role, ConfigPolicies.Admin)
                    .RequireAssertion(c => !ConfigPrincipal.IsApiKey(c.User)));
                // Endpoints without [Authorize]/[AllowAnonymous] need read access.
                o.FallbackPolicy = o.GetPolicy(ConfigPolicies.Read);
            });
            services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, JsonAuthorizationResultHandler>();
            services.Configure<MvcOptions>(o => o.Filters.Add<AccessScopeFilter>());
            return services;
        }

        private static void ConfigureJwtBearer(JwtBearerOptions jwt, OidcAuthOptions oidc)
        {
            jwt.Authority = oidc.Authority;
            if (oidc.MetadataAddress != null) jwt.MetadataAddress = oidc.MetadataAddress;
            jwt.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
            // Keep raw claim names ("roles", "realm_access", ...) so Auth:Oidc:RoleClaim paths match the token.
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters.ValidAudiences = oidc.ValidAudiences.ToArray();
            jwt.TokenValidationParameters.NameClaimType = "name";
            jwt.TokenValidationParameters.RoleClaimType = ConfigClaimTypes.Role;
            jwt.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(ConfigPrincipal).FullName);
                    ConfigPrincipal.ApplyOidcRoles(context.Principal, oidc, logger);
                    return Task.CompletedTask;
                }
            };
        }

        /// <summary>
        /// JSON file store at "ApiKeys:StorePath" (relative to the content root; default data/api-keys.json) unless another
        /// <see cref="IApiKeyStore"/> is registered. The file must not be inside the config source (filesystem or Git clone).
        /// </summary>
        private static void AddApiKeys(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
        {
            var configured = configuration[ApiKeyStorePathKey];
            var storePath = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DefaultApiKeyStorePath : configured,
                environment?.ContentRootPath ?? Directory.GetCurrentDirectory());
            EnsureOutside(storePath, configuration[ConfigSourceServiceCollectionExtensions.PathKey], ConfigSourceServiceCollectionExtensions.PathKey);

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<IApiKeyStore>(sp =>
            {
                EnsureOutside(storePath, sp.GetService<GitConfigSourceOptions>()?.LocalPath, "the Git config source LocalPath");
                return new JsonFileApiKeyStore(storePath);
            });
            services.TryAddSingleton(sp => new ApiKeyManager(sp.GetRequiredService<IApiKeyStore>(), sp.GetRequiredService<TimeProvider>()));
        }

        private static void EnsureOutside(string storePath, string configRoot, string label)
        {
            if (string.IsNullOrWhiteSpace(configRoot)) return;
            var root = Path.GetFullPath(configRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (storePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{ApiKeyStorePathKey} '{storePath}' is inside the configuration source ({label} '{configRoot}'). " +
                    "Keep API key hashes out of the config repository: point it to a directory outside.");
        }
    }
}
