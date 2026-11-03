using System;
using System.Collections.Generic;
using System.Linq;
using ConfigServer.Domain.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ConfigServer.Application.Api.Authentication
{
    public enum AuthMode
    {
        /// <summary>No authentication: every request is admin. Development only.</summary>
        None,

        /// <summary>JWT bearer tokens from any OpenID Connect provider (Keycloak, Auth0, Entra ID, ...).</summary>
        Oidc
    }

    /// <summary>"Auth" section. Load with <see cref="Load"/> and check with <see cref="Validate"/> (throws on startup).</summary>
    public sealed class AuthOptions
    {
        public const string SectionName = "Auth";

        public AuthMode Mode { get; set; } = AuthMode.Oidc;

        public OidcAuthOptions Oidc { get; set; } = new OidcAuthOptions();

        public static AuthOptions Load(IConfiguration configuration)
        {
            var section = configuration.GetSection(SectionName);
            var options = new AuthOptions();

            var mode = section["Mode"];
            if (!string.IsNullOrWhiteSpace(mode))
            {
                if (!Enum.TryParse<AuthMode>(mode.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    throw new InvalidOperationException($"Auth:Mode '{mode}' is not supported. Use 'None' (Development only) or 'Oidc'.");
                options.Mode = parsed;
            }

            var oidc = section.GetSection("Oidc");
            options.Oidc = new OidcAuthOptions
            {
                Authority = Blank(oidc["Authority"]),
                MetadataAddress = Blank(oidc["MetadataAddress"]),
                Audience = Blank(oidc["Audience"]),
                ClientId = Blank(oidc["ClientId"]),
                Scopes = oidc.GetSection("Scopes").Get<string[]>()?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray(),
                RequireHttpsMetadata = oidc.GetValue("RequireHttpsMetadata", true),
                RoleClaim = Blank(oidc["RoleClaim"]) ?? OidcAuthOptions.DefaultRoleClaim,
                RoleMap = new Dictionary<string, string>(
                    oidc.GetSection("RoleMap").GetChildren().Where(c => c.Value != null).ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase)
            };
            if (options.Oidc.Scopes == null || options.Oidc.Scopes.Length == 0) options.Oidc.Scopes = OidcAuthOptions.DefaultScopes.ToArray();
            return options;
        }

        /// <summary>Throws <see cref="InvalidOperationException"/> with a message naming the setting to fix.</summary>
        public void Validate(IHostEnvironment environment)
        {
            if (Mode == AuthMode.None)
            {
                if (environment == null || !environment.IsDevelopment())
                    throw new InvalidOperationException(
                        $"Auth:Mode=None disables authentication and is only allowed in the Development environment (current: '{environment?.EnvironmentName}'). " +
                        "Set Auth:Mode=Oidc and configure Auth:Oidc (Authority, Audience).");
                return;
            }

            var oidc = Oidc ?? new OidcAuthOptions();
            if (string.IsNullOrWhiteSpace(oidc.Authority))
                throw new InvalidOperationException("Auth:Oidc:Authority is required when Auth:Mode=Oidc (the issuer URL of your OpenID Connect provider).");
            if (!Uri.TryCreate(oidc.Authority, UriKind.Absolute, out var authority) || (authority.Scheme != Uri.UriSchemeHttps && authority.Scheme != Uri.UriSchemeHttp))
                throw new InvalidOperationException($"Auth:Oidc:Authority '{oidc.Authority}' must be an absolute http(s) URL.");
            if (oidc.RequireHttpsMetadata && authority.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Auth:Oidc:Authority must use https (set Auth:Oidc:RequireHttpsMetadata=false only for a local identity provider).");
            if (oidc.MetadataAddress != null)
            {
                if (!Uri.TryCreate(oidc.MetadataAddress, UriKind.Absolute, out var metadata) || (metadata.Scheme != Uri.UriSchemeHttps && metadata.Scheme != Uri.UriSchemeHttp))
                    throw new InvalidOperationException($"Auth:Oidc:MetadataAddress '{oidc.MetadataAddress}' must be an absolute http(s) URL (the OpenID Connect discovery document).");
                if (oidc.RequireHttpsMetadata && metadata.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Auth:Oidc:MetadataAddress must use https (set Auth:Oidc:RequireHttpsMetadata=false only for a private-network identity provider).");
            }
            if (oidc.ValidAudiences.Count == 0)
                throw new InvalidOperationException("Auth:Oidc:Audience (or Auth:Oidc:ClientId) is required so that tokens issued for other clients are rejected.");
            if (string.IsNullOrWhiteSpace(oidc.RoleClaim)
                || oidc.RoleClaim.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 0)
                throw new InvalidOperationException("Auth:Oidc:RoleClaim must not be empty (default 'roles').");
            if (oidc.RoleClaim.Contains(RoleClaimResolver.ClientIdPlaceholder, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(oidc.ClientId))
                throw new InvalidOperationException(
                    $"Auth:Oidc:RoleClaim '{oidc.RoleClaim}' uses {RoleClaimResolver.ClientIdPlaceholder} but Auth:Oidc:ClientId is empty.");

            foreach (var pair in oidc.RoleMap ?? new Dictionary<string, string>())
            {
                var targets = (pair.Value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (targets.Length == 0 || targets.Any(t => AccessGrants.ParseRole(t, out _, out _) != RoleParseStatus.Valid))
                    throw new InvalidOperationException(
                        $"Auth:Oidc:RoleMap:{pair.Key} = '{pair.Value}' is invalid. Map to config:<read|write|admin>[:<app>[:<env>]] " +
                        "(admin is global only), e.g. config:read or config:write:billing-service:dev.");
            }
        }

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public sealed class OidcAuthOptions
    {
        public const string DefaultRoleClaim = "roles";
        public static readonly IReadOnlyList<string> DefaultScopes = new[] { "openid", "profile" };

        /// <summary>Issuer URL; the discovery document is read from {Authority}/.well-known/openid-configuration.</summary>
        public string Authority { get; set; }

        /// <summary>
        /// Optional discovery document URL used instead of {Authority}/.well-known/openid-configuration, e.g. an internal address
        /// while <see cref="Authority"/> stays the public issuer that tokens carry and the SPA uses. The document's own issuer and
        /// jwks_uri are used as published, so its jwks_uri must be reachable from the server.
        /// </summary>
        public string MetadataAddress { get; set; }

        /// <summary>Expected "aud" of access tokens. Falls back to <see cref="ClientId"/> when empty.</summary>
        public string Audience { get; set; }

        /// <summary>Public client id of the SPA (exposed by GET api/auth/info).</summary>
        public string ClientId { get; set; }

        public string[] Scopes { get; set; }

        public bool RequireHttpsMetadata { get; set; } = true;

        /// <summary>
        /// Where the roles are in the token: a dotted path ("roles", "realm_access.roles", "https://example.com/roles") or
        /// several separated by ',' (union). "{clientId}" is replaced by <see cref="ClientId"/>, so Keycloak client roles are
        /// "resource_access.{clientId}.roles". Example: "roles,realm_access.roles,resource_access.{clientId}.roles".
        /// </summary>
        public string RoleClaim { get; set; } = DefaultRoleClaim;

        /// <summary><see cref="RoleClaim"/> split on ',' with the client id placeholder resolved.</summary>
        public IReadOnlyList<string> RoleClaimPaths => RoleClaimResolver.ResolvePaths(RoleClaim, ClientId);

        /// <summary>
        /// External role => config role string(s), comma separated: config:&lt;read|write|admin&gt;[:&lt;app&gt;[:&lt;env&gt;]]
        /// (admin global only). Keys are case-insensitive and cannot contain ':'.
        /// </summary>
        public Dictionary<string, string> RoleMap { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> ValidAudiences
            => !string.IsNullOrWhiteSpace(Audience) ? new[] { Audience }
                : !string.IsNullOrWhiteSpace(ClientId) ? new[] { ClientId }
                : Array.Empty<string>();
    }
}
