using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using ConfigServer.Domain.Security;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;

namespace ConfigServer.Application.Api.Authentication
{
    public static class AuthSchemes
    {
        /// <summary>Default scheme: forwards to <see cref="ApiKey"/> when an API key header is present, otherwise to bearer (Oidc) or <see cref="None"/>.</summary>
        public const string Selector = "ConfigServer";
        public const string ApiKey = "ApiKey";
        public const string None = "None";
        public const string Bearer = JwtBearerDefaults.AuthenticationScheme;
    }

    /// <summary>
    /// Coarse policies: the caller holds the permission in at least one scope (admin: globally, never an API key).
    /// The per-application/environment check is <see cref="AccessScopeFilter"/> plus the listing filters in the controllers.
    /// </summary>
    public static class ConfigPolicies
    {
        public const string Read = ConfigRoles.Read;
        public const string Write = ConfigRoles.Write;
        public const string Admin = ConfigRoles.Admin;
    }

    /// <summary>Claims the server adds itself. Incoming token claims with these types are dropped.</summary>
    public static class ConfigClaimTypes
    {
        public const string Namespace = "urn:config-server:";

        /// <summary>Coarse role (config:read|write|admin) held in at least one scope; checked by the policies.</summary>
        public const string Role = Namespace + "role";

        /// <summary>One <see cref="AccessGrant"/> as its canonical role string config:{permission}:{app}:{environment}.</summary>
        public const string Grant = Namespace + "grant";

        public const string Via = Namespace + "via";
        public const string ApiKeyId = Namespace + "apikey:id";
    }

    public static class AuthVia
    {
        public const string None = "none";
        public const string Oidc = "oidc";
        public const string ApiKey = "apikey";
    }

    /// <summary>Builds the principals of the three authentication paths and reads their grants back.</summary>
    public static class ConfigPrincipal
    {
        public const string AuthenticationType = "config-server";

        public static ClaimsPrincipal ForNone()
        {
            var identity = new ClaimsIdentity(AuthenticationType, ClaimTypes.Name, ConfigClaimTypes.Role);
            identity.AddClaim(new Claim(ClaimTypes.Name, "local"));
            identity.AddClaim(new Claim(ConfigClaimTypes.Via, AuthVia.None));
            AddGrants(identity, AccessGrants.GlobalAdmin);
            return new ClaimsPrincipal(identity);
        }

        public static ClaimsPrincipal ForApiKey(ApiKey key)
        {
            var identity = new ClaimsIdentity(AuthSchemes.ApiKey, ClaimTypes.Name, ConfigClaimTypes.Role);
            identity.AddClaim(new Claim(ClaimTypes.Name, key.Name ?? key.Prefix));
            identity.AddClaim(new Claim(ConfigClaimTypes.Via, AuthVia.ApiKey));
            identity.AddClaim(new Claim(ConfigClaimTypes.ApiKeyId, key.Id));
            // apps x environments with the key's permission; never admin (api/keys is not reachable with a key).
            AddGrants(identity, AccessGrants.FromApiKey(key));
            return new ClaimsPrincipal(identity);
        }

        /// <summary>
        /// Called after a bearer token is validated: strips spoofable server claims, reads the external roles at
        /// <see cref="OidcAuthOptions.RoleClaimPaths"/>, applies <see cref="OidcAuthOptions.RoleMap"/> and adds the grants.
        /// Invalid config roles are logged at debug level, app-scoped admin roles as a warning.
        /// </summary>
        public static void ApplyOidcRoles(ClaimsPrincipal principal, OidcAuthOptions options, ILogger logger = null)
        {
            if (principal == null) return;
            // Ignore case: ClaimsPrincipal.FindAll and the claim policies match claim types case-insensitively.
            foreach (var tokenIdentity in principal.Identities)
                foreach (var claim in tokenIdentity.Claims.Where(c => c.Type.StartsWith(ConfigClaimTypes.Namespace, StringComparison.OrdinalIgnoreCase)).ToList())
                    tokenIdentity.TryRemoveClaim(claim);

            var external = RoleClaimResolver.Extract(principal.Claims, options.RoleClaimPaths);
            var roles = RoleClaimResolver.MapToRoleStrings(external, options.RoleMap);
            var grants = AccessGrants.FromRoles(roles, (role, status, reason) =>
            {
                if (logger == null) return;
                if (status == RoleParseStatus.ScopedAdmin)
                    logger.LogWarning("Ignoring role '{Role}' of '{User}': {Reason}.", role, Name(principal), reason);
                else
                    logger.LogDebug("Ignoring role '{Role}' of '{User}': {Reason}.", role, Name(principal), reason);
            });

            var identity = new ClaimsIdentity(AuthenticationType, ClaimTypes.Name, ConfigClaimTypes.Role);
            identity.AddClaim(new Claim(ConfigClaimTypes.Via, AuthVia.Oidc));
            AddGrants(identity, grants);
            principal.AddIdentity(identity);
        }

        public static string Via(ClaimsPrincipal user) => user?.FindFirst(ConfigClaimTypes.Via)?.Value;

        public static bool IsApiKey(ClaimsPrincipal user) => Via(user) == AuthVia.ApiKey;

        public static IReadOnlyList<string> Roles(ClaimsPrincipal user)
            => ConfigRoles.Expand(user?.FindAll(ConfigClaimTypes.Role).Select(c => c.Value) ?? Enumerable.Empty<string>());

        /// <summary>
        /// The caller's grants (from the server-added grant claims; none when unauthenticated). API keys compare
        /// environment names case-insensitively, users case-sensitively.
        /// </summary>
        public static AccessGrants Grants(ClaimsPrincipal user)
        {
            if (user?.Identity?.IsAuthenticated != true) return AccessGrants.None;
            var parsed = new List<AccessGrant>();
            foreach (var claim in user.FindAll(ConfigClaimTypes.Grant))
                if (AccessGrants.ParseRole(claim.Value, out var grant, out _) == RoleParseStatus.Valid) parsed.Add(grant);
            return new AccessGrants(parsed, IsApiKey(user) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        /// <summary>Display name: the name claim, then common OIDC fallbacks.</summary>
        public static string Name(ClaimsPrincipal user)
        {
            if (user == null) return null;
            foreach (var type in new[] { ClaimTypes.Name, "name", "preferred_username", "email", "sub" })
            {
                var value = user.FindFirst(type)?.Value;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return user.Identity?.Name;
        }

        private static void AddGrants(ClaimsIdentity identity, AccessGrants grants)
        {
            foreach (var grant in grants.Grants)
                identity.AddClaim(new Claim(ConfigClaimTypes.Grant, grant.ToRole()));
            foreach (var role in grants.Roles)
                identity.AddClaim(new Claim(ConfigClaimTypes.Role, role));
        }
    }
}
