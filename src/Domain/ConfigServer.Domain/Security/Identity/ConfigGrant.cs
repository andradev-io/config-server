using System;
using ConfigServer.Domain.Configuration;

namespace ConfigServer.Domain.Security.Identity
{
    /// <summary>
    /// A grant that can be assigned in the identity provider: one validated <see cref="AccessGrant"/> and the client role
    /// name that carries it, <c>config:&lt;perm&gt;[:&lt;app&gt;[:&lt;env&gt;]]</c> (shortest form: trailing "*" segments are left
    /// out, so (read, *, *) = "config:read", (write, billing, *) = "config:write:billing", (write, *, dev) = "config:write:*:dev").
    /// Every role name built here is parsed back with <see cref="AccessGrants.ParseRole"/>, the one role parser of the
    /// server, and must yield the same grant; anything else is refused.
    /// </summary>
    public sealed class ConfigGrant : IEquatable<ConfigGrant>
    {
        /// <summary>Keycloak limits role names to 255 characters.</summary>
        public const int MaxRoleNameLength = 255;

        private const string W = AccessGrant.Wildcard;

        private ConfigGrant(AccessPermission permission, string app, string environment, string roleName)
        {
            Permission = permission;
            App = app;
            Environment = environment;
            RoleName = roleName;
        }

        public AccessPermission Permission { get; }

        /// <summary>Application name or "*".</summary>
        public string App { get; }

        /// <summary>Environment name or "*".</summary>
        public string Environment { get; }

        /// <summary>The client role name for this grant (shortest form, lower-case prefix and permission).</summary>
        public string RoleName { get; }

        public AccessGrant ToAccessGrant() => new AccessGrant(Permission, App, Environment);

        /// <summary>
        /// Validates a grant request. <paramref name="permission"/> is read|write|admin (ignore case, required);
        /// <paramref name="app"/>/<paramref name="environment"/> null or empty = "*", otherwise a valid <see cref="ConfigNames"/>
        /// name (case-sensitive). Admin is global only. <paramref name="error"/> explains a refusal.
        /// </summary>
        public static bool TryCreate(string permission, string app, string environment, out ConfigGrant grant, out string error)
        {
            grant = null;
            if (string.IsNullOrWhiteSpace(permission))
                return Fail(out error, "'permission' is required (read, write or admin).");
            if (!AccessPermissions.TryParse(permission.Trim(), out var parsed))
                return Fail(out error, $"Unknown permission '{permission}' (use read, write or admin).");

            app = string.IsNullOrEmpty(app) ? W : app;
            environment = string.IsNullOrEmpty(environment) ? W : environment;
            if (app != W && !ConfigNames.IsValidAppName(app))
                return Fail(out error, $"Invalid application name '{app}'.");
            if (environment != W && !ConfigNames.IsValidEnvironmentName(environment))
                return Fail(out error, $"Invalid environment name '{environment}'.");
            if (parsed == AccessPermission.Admin && (app != W || environment != W))
                return Fail(out error, "config:admin is global only: application and environment must be '*'.");

            var roleName = BuildRoleName(parsed, app, environment);
            if (!roleName.StartsWith(ConfigRoles.Prefix + ":", StringComparison.Ordinal))
                return Fail(out error, "Refusing to build a role outside the 'config:' namespace.");
            if (roleName.Length > MaxRoleNameLength)
                return Fail(out error, $"The role name would exceed {MaxRoleNameLength} characters.");
            // Single source of truth: the server must read back exactly the grant that is being assigned.
            if (AccessGrants.ParseRole(roleName, out var roundTrip, out var reason) != RoleParseStatus.Valid
                || roundTrip != new AccessGrant(parsed, app, environment))
                return Fail(out error, $"The role '{roleName}' would not be read back as this grant ({reason ?? "mismatch"}).");

            grant = new ConfigGrant(parsed, app, environment, roleName);
            error = null;
            return true;
        }

        /// <summary><see cref="TryCreate"/> that throws <see cref="ArgumentException"/>.</summary>
        public static ConfigGrant Create(AccessPermission permission, string app = W, string environment = W)
            => TryCreate(permission.ToName(), app, environment, out var grant, out var error) ? grant : throw new ArgumentException(error);

        /// <summary>
        /// The grant carried by an existing role name (any accepted spelling, e.g. "Config:READ:web:*"), or false when the
        /// name is not a valid config grant (other roles, invalid names, app-scoped admin).
        /// </summary>
        public static bool TryFromRoleName(string roleName, out ConfigGrant grant)
        {
            grant = null;
            if (AccessGrants.ParseRole(roleName, out var parsed, out _) != RoleParseStatus.Valid) return false;
            return TryCreate(parsed.Permission.ToName(), parsed.App, parsed.Environment, out grant, out _);
        }

        /// <summary>True when <paramref name="roleName"/> carries exactly this grant (whatever its spelling).</summary>
        public bool MatchesRoleName(string roleName) => TryFromRoleName(roleName, out var other) && Equals(other);

        /// <summary>Shortest role string: config:{perm}[:{app}[:{env}]] (trailing "*" segments dropped).</summary>
        public static string BuildRoleName(AccessPermission permission, string app, string environment)
        {
            var role = ConfigRoles.Prefix + ":" + permission.ToName();
            if (environment != W) return role + ":" + app + ":" + environment;
            return app != W ? role + ":" + app : role;
        }

        public bool Equals(ConfigGrant other)
            => other != null && Permission == other.Permission
               && string.Equals(App, other.App, StringComparison.Ordinal)
               && string.Equals(Environment, other.Environment, StringComparison.Ordinal);

        public override bool Equals(object obj) => Equals(obj as ConfigGrant);

        public override int GetHashCode() => HashCode.Combine(Permission, App, Environment);

        public override string ToString() => RoleName;

        private static bool Fail(out string error, string message)
        {
            error = message;
            return false;
        }
    }
}
