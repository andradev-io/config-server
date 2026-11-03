using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace ConfigServer.Domain.Security.ApiKeys
{
    public static class ApiKeyPermission
    {
        public const string Read = "read";
        public const string Write = "write";

        /// <summary>Canonical permission ("read" when empty), or null when unknown.</summary>
        public static string Normalize(string permission)
        {
            if (string.IsNullOrWhiteSpace(permission)) return Read;
            var value = permission.Trim();
            if (string.Equals(value, Read, StringComparison.OrdinalIgnoreCase)) return Read;
            if (string.Equals(value, Write, StringComparison.OrdinalIgnoreCase)) return Write;
            return null;
        }

        /// <summary>Config roles granted by a key permission. Never admin.</summary>
        public static string[] Roles(string permission)
            => Normalize(permission) == Write ? new[] { ConfigRoles.Read, ConfigRoles.Write } : new[] { ConfigRoles.Read };
    }

    public static class ApiKeyStatus
    {
        public const string Active = "active";
        public const string Expired = "expired";
        public const string Revoked = "revoked";
    }

    /// <summary>
    /// A stored API key. Only the SHA-256 <see cref="Hash"/> of the full key is kept; the secret is shown once.
    /// <see cref="Apps"/> / <see cref="Environments"/> hold names or the wildcard "*".
    /// </summary>
    public sealed record ApiKey
    {
        public const string Wildcard = "*";

        public string Id { get; init; }
        public string Name { get; init; }
        public string Prefix { get; init; }
        public string Hash { get; init; }
        public string[] Apps { get; init; } = { Wildcard };
        public string[] Environments { get; init; } = { Wildcard };
        public string Permission { get; init; } = ApiKeyPermission.Read;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public DateTimeOffset? RevokedAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }

        public string StatusAt(DateTimeOffset now)
            => RevokedAt != null ? ApiKeyStatus.Revoked
                : ExpiresAt != null && ExpiresAt.Value <= now ? ApiKeyStatus.Expired
                : ApiKeyStatus.Active;

        [JsonIgnore]
        public bool AllApps => Apps == null || Apps.Contains(Wildcard);

        [JsonIgnore]
        public bool AllEnvironments => Environments == null || Environments.Contains(Wildcard);

        /// <summary>App names are case-sensitive (like the files on disk).</summary>
        public bool AllowsApp(string app)
            => AllApps || (app != null && Apps.Contains(app, StringComparer.Ordinal));

        /// <summary>Environment names are compared case-insensitively (like the catalog does).</summary>
        public bool AllowsEnvironment(string environment)
            => AllEnvironments || (environment != null && Environments.Contains(environment, StringComparer.OrdinalIgnoreCase));
    }
}
