using System;
using ConfigServer.Application.Api.Authentication;
using Microsoft.Extensions.Configuration;

namespace ConfigServer.Application.Api.IdentityAdmin
{
    public enum IdentityAdminProvider
    {
        /// <summary>Grants are managed in the identity provider's own console only (default).</summary>
        None,

        /// <summary>Keycloak Admin REST API with a confidential service-account client (client_credentials).</summary>
        Keycloak
    }

    /// <summary>"IdentityAdmin" section. Load with <see cref="Load"/> and check with <see cref="Validate"/> (throws on startup).</summary>
    public sealed class IdentityAdminOptions
    {
        public const string SectionName = "IdentityAdmin";

        public IdentityAdminProvider Provider { get; set; } = IdentityAdminProvider.None;

        public KeycloakAdminOptions Keycloak { get; set; } = new KeycloakAdminOptions();

        /// <summary>Reads the section; Keycloak:ConfigClientId falls back to Auth:Oidc:ClientId.</summary>
        public static IdentityAdminOptions Load(IConfiguration configuration)
        {
            var section = configuration.GetSection(SectionName);
            var options = new IdentityAdminOptions();

            var provider = section["Provider"];
            if (!string.IsNullOrWhiteSpace(provider))
            {
                if (!Enum.TryParse<IdentityAdminProvider>(provider.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    throw new InvalidOperationException($"IdentityAdmin:Provider '{provider}' is not supported. Use 'None' or 'Keycloak'.");
                options.Provider = parsed;
            }

            var keycloak = section.GetSection("Keycloak");
            options.Keycloak = new KeycloakAdminOptions
            {
                BaseUrl = Blank(keycloak["BaseUrl"])?.TrimEnd('/'),
                Realm = Blank(keycloak["Realm"]),
                AdminClientId = Blank(keycloak["AdminClientId"]),
                AdminClientSecret = Blank(keycloak["AdminClientSecret"]),
                ConfigClientId = Blank(keycloak["ConfigClientId"]) ?? Blank(configuration[AuthOptions.SectionName + ":Oidc:ClientId"]),
                TokenEndpoint = Blank(keycloak["TokenEndpoint"]) ?? PublicTokenEndpoint(configuration[AuthOptions.SectionName + ":Oidc:Authority"]),
                RequireHttps = keycloak.GetValue("RequireHttps", true),
                TimeoutSeconds = keycloak.GetValue("TimeoutSeconds", KeycloakAdminOptions.DefaultTimeoutSeconds)
            };
            return options;
        }

        /// <summary>Throws <see cref="InvalidOperationException"/> naming the setting to fix. Never includes the secret.</summary>
        public void Validate()
        {
            if (Provider != IdentityAdminProvider.Keycloak) return;

            const string p = SectionName + ":Keycloak:";
            var k = Keycloak ?? new KeycloakAdminOptions();
            if (string.IsNullOrWhiteSpace(k.BaseUrl))
                throw new InvalidOperationException($"{p}BaseUrl is required when {SectionName}:Provider=Keycloak (e.g. https://keycloak.example.com).");
            if (!Uri.TryCreate(k.BaseUrl, UriKind.Absolute, out var baseUrl) || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(baseUrl.Query) || !string.IsNullOrEmpty(baseUrl.Fragment))
                throw new InvalidOperationException($"{p}BaseUrl '{k.BaseUrl}' must be an absolute http(s) URL without query or fragment.");
            if (baseUrl.Scheme != Uri.UriSchemeHttps && k.RequireHttps && !baseUrl.IsLoopback)
                throw new InvalidOperationException(
                    $"{p}BaseUrl must use https: the admin client secret and tokens travel to it (set {p}RequireHttps=false only on a private network).");
            if (string.IsNullOrWhiteSpace(k.Realm))
                throw new InvalidOperationException($"{p}Realm is required (the realm that holds the users and the config client).");
            if (string.IsNullOrWhiteSpace(k.AdminClientId))
                throw new InvalidOperationException($"{p}AdminClientId is required (a confidential client with service accounts enabled, e.g. config-manager-admin).");
            if (string.IsNullOrWhiteSpace(k.AdminClientSecret))
                throw new InvalidOperationException(
                    $"{p}AdminClientSecret is required. Provide it through an environment variable (IdentityAdmin__Keycloak__AdminClientSecret) or a secret store, not appsettings.json.");
            if (string.IsNullOrWhiteSpace(k.ConfigClientId))
                throw new InvalidOperationException($"{p}ConfigClientId (or Auth:Oidc:ClientId) is required: the client whose roles carry the config grants.");
            if (k.TimeoutSeconds < 1 || k.TimeoutSeconds > 300)
                throw new InvalidOperationException($"{p}TimeoutSeconds must be between 1 and 300.");
        }

        /// <summary>The token endpoint clients reach publicly: derived from the issuer (Auth:Oidc:Authority), not from the admin BaseUrl.</summary>
        private static string PublicTokenEndpoint(string authority)
            => string.IsNullOrWhiteSpace(authority) ? null : authority.Trim().TrimEnd('/') + "/protocol/openid-connect/token";

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>"IdentityAdmin:Keycloak". <see cref="AdminClientSecret"/> is never logged or returned.</summary>
    public sealed class KeycloakAdminOptions
    {
        public const int DefaultTimeoutSeconds = 15;

        /// <summary>Keycloak base URL including any context path (e.g. http://localhost:8080, or https://host/auth for Keycloak &lt; 17).</summary>
        public string BaseUrl { get; set; }

        public string Realm { get; set; }

        /// <summary>Confidential client with service accounts enabled (client_credentials), e.g. config-manager-admin.</summary>
        public string AdminClientId { get; set; }

        public string AdminClientSecret { get; set; }

        /// <summary>The client (clientId, not the internal id) whose client roles carry the grants. Defaults to Auth:Oidc:ClientId.</summary>
        public string ConfigClientId { get; set; }

        /// <summary>
        /// Public OIDC token endpoint shown to machine credentials. Defaults to <c>{Auth:Oidc:Authority}/protocol/openid-connect/token</c>
        /// (the admin BaseUrl may be an internal host); null falls back to the BaseUrl one.
        /// </summary>
        public string TokenEndpoint { get; set; }

        /// <summary>BaseUrl must be https unless it is a loopback address or this is false.</summary>
        public bool RequireHttps { get; set; } = true;

        public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

        public override string ToString() => $"Keycloak {BaseUrl} realm={Realm} adminClient={AdminClientId} configClient={ConfigClientId}";
    }
}
