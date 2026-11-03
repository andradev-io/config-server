using System;
using System.Collections.Generic;
using System.Linq;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Security.ApiKeys;

namespace ConfigServer.Domain.Security
{
    /// <summary>Permission levels, lowest to highest. Each level implies the lower ones (admin => write => read).</summary>
    public enum AccessPermission
    {
        Read = 1,
        Write = 2,
        Admin = 3
    }

    public static class AccessPermissions
    {
        public static string ToName(this AccessPermission permission) => permission switch
        {
            AccessPermission.Read => "read",
            AccessPermission.Write => "write",
            AccessPermission.Admin => "admin",
            _ => throw new ArgumentOutOfRangeException(nameof(permission))
        };

        /// <summary>"read" | "write" | "admin" (ignore case).</summary>
        public static bool TryParse(string value, out AccessPermission permission)
        {
            permission = default;
            foreach (var candidate in new[] { AccessPermission.Read, AccessPermission.Write, AccessPermission.Admin })
            {
                if (!string.Equals(value, candidate.ToName(), StringComparison.OrdinalIgnoreCase)) continue;
                permission = candidate;
                return true;
            }
            return false;
        }

        /// <summary>The legacy global role name ("config:read", ...).</summary>
        public static string ToRoleName(this AccessPermission permission) => ConfigRoles.Prefix + ":" + permission.ToName();
    }

    /// <summary>A permission on one application (or "*") and one environment (or "*").</summary>
    public sealed record AccessGrant(AccessPermission Permission, string App, string Environment)
    {
        public const string Wildcard = "*";

        public bool IsGlobal => App == Wildcard && Environment == Wildcard;

        /// <summary>Canonical role string: config:{permission}:{app}:{environment}.</summary>
        public string ToRole() => $"{ConfigRoles.Prefix}:{Permission.ToName()}:{App}:{Environment}";

        public override string ToString() => ToRole();
    }

    public enum RoleParseStatus
    {
        Valid,

        /// <summary>Not a "config:..." role (other roles of the token): silently ignored.</summary>
        NotConfigRole,

        /// <summary>A "config:..." role with an unknown permission, bad app/environment name or too many segments.</summary>
        Invalid,

        /// <summary>"config:admin:{app}[:{env}]": admin is global only.</summary>
        ScopedAdmin
    }

    /// <summary>
    /// The access of one caller, as a set of <see cref="AccessGrant"/>s. Built from role strings
    /// <c>config:&lt;read|write|admin&gt;[:&lt;app&gt;[:&lt;env&gt;]]</c> (app/env omitted = "*") or from an API key's scope;
    /// users and API keys are enforced through the same checks. App names are compared case-sensitively; environment
    /// names case-sensitively for roles and case-insensitively for API keys (their historical contract).
    /// </summary>
    public sealed class AccessGrants
    {
        private const string W = AccessGrant.Wildcard;

        public static readonly AccessGrants None = new AccessGrants(Array.Empty<AccessGrant>());

        public static readonly AccessGrants GlobalAdmin = new AccessGrants(new[] { new AccessGrant(AccessPermission.Admin, W, W) });

        private readonly StringComparer _environmentComparer;

        public AccessGrants(IEnumerable<AccessGrant> grants, StringComparer environmentComparer = null)
        {
            _environmentComparer = environmentComparer ?? StringComparer.Ordinal;
            Grants = Normalize(grants ?? Enumerable.Empty<AccessGrant>());
        }

        /// <summary>Normalized grants: no duplicates, none covered by another one, broadest first.</summary>
        public IReadOnlyList<AccessGrant> Grants { get; }

        public bool IsEmpty => Grants.Count == 0;

        /// <summary>True when the environment names are compared case-insensitively (API keys).</summary>
        public bool IgnoresEnvironmentCase => ReferenceEquals(_environmentComparer, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Parses one role string. Valid forms: config:read, config:write:billing, config:read:billing:prd, config:write:*:dev.
        /// The "config" prefix and the permission ignore case; app/env names are case-sensitive and follow <see cref="ConfigNames"/>.
        /// </summary>
        public static RoleParseStatus ParseRole(string role, out AccessGrant grant, out string reason)
        {
            grant = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(role)) return Fail(out reason, RoleParseStatus.NotConfigRole, "empty role");

            var parts = role.Trim().Split(':');
            if (!string.Equals(parts[0], ConfigRoles.Prefix, StringComparison.OrdinalIgnoreCase))
                return Fail(out reason, RoleParseStatus.NotConfigRole, "not a config role");
            if (parts.Length < 2 || parts.Length > 4)
                return Fail(out reason, RoleParseStatus.Invalid, "expected config:<read|write|admin>[:<app>[:<env>]]");
            if (!AccessPermissions.TryParse(parts[1], out var permission))
                return Fail(out reason, RoleParseStatus.Invalid, $"unknown permission '{parts[1]}' (use read, write or admin)");

            var app = parts.Length > 2 ? parts[2] : W;
            var environment = parts.Length > 3 ? parts[3] : W;
            if (app != W && !ConfigNames.IsValidAppName(app))
                return Fail(out reason, RoleParseStatus.Invalid, $"invalid application name '{app}'");
            if (environment != W && !ConfigNames.IsValidEnvironmentName(environment))
                return Fail(out reason, RoleParseStatus.Invalid, $"invalid environment name '{environment}'");
            if (permission == AccessPermission.Admin && (app != W || environment != W))
                return Fail(out reason, RoleParseStatus.ScopedAdmin, "config:admin is global only; scoped admin roles are ignored");

            grant = new AccessGrant(permission, app, environment);
            return RoleParseStatus.Valid;
        }

        /// <summary>
        /// Grants from role strings. Invalid "config:..." roles (including app-scoped admin) are reported to
        /// <paramref name="onIgnored"/> (role, status, reason) and skipped; other roles are skipped silently.
        /// </summary>
        public static AccessGrants FromRoles(IEnumerable<string> roles, Action<string, RoleParseStatus, string> onIgnored = null)
        {
            var grants = new List<AccessGrant>();
            foreach (var role in roles ?? Enumerable.Empty<string>())
            {
                var status = ParseRole(role, out var grant, out var reason);
                if (status == RoleParseStatus.Valid) grants.Add(grant);
                else if (status != RoleParseStatus.NotConfigRole) onIgnored?.Invoke(role, status, reason);
            }
            return new AccessGrants(grants);
        }

        /// <summary>
        /// An API key's apps x environments with its permission (write => write grants, otherwise read). Never admin.
        /// Environments compare case-insensitively, like <see cref="ApiKey.AllowsEnvironment"/>. Invalid stored names are dropped.
        /// </summary>
        public static AccessGrants FromApiKey(ApiKey key)
        {
            if (key == null) return None;
            var permission = ApiKeyPermission.Normalize(key.Permission) == ApiKeyPermission.Write ? AccessPermission.Write : AccessPermission.Read;
            var apps = key.AllApps ? new[] { W } : key.Apps.Where(ConfigNames.IsValidAppName).ToArray();
            var environments = key.AllEnvironments ? new[] { W } : key.Environments.Where(ConfigNames.IsValidEnvironmentName).ToArray();
            var grants = apps.SelectMany(app => environments.Select(environment => new AccessGrant(permission, app, environment)));
            return new AccessGrants(grants, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when a grant with at least <paramref name="permission"/> covers <paramref name="app"/> and <paramref name="environment"/>.
        /// Pass "*" to require all apps / all environments (only a wildcard grant covers "*").
        /// </summary>
        public bool Can(AccessPermission permission, string app, string environment)
            => app != null && environment != null
               && Grants.Any(g => g.Permission >= permission && MatchesApp(g, app) && MatchesEnvironment(g, environment));

        /// <summary>True when at least one environment of <paramref name="app"/> is covered.</summary>
        public bool CanAnyEnvironment(AccessPermission permission, string app)
            => app != null && Grants.Any(g => g.Permission >= permission && MatchesApp(g, app));

        /// <summary>True when every environment of <paramref name="app"/> is covered (app "*" = all apps).</summary>
        public bool CanAllEnvironments(AccessPermission permission, string app) => Can(permission, app, W);

        /// <summary>True when <paramref name="environment"/> is covered for at least one application.</summary>
        public bool CanAnyApp(AccessPermission permission, string environment)
            => environment != null && Grants.Any(g => g.Permission >= permission && MatchesEnvironment(g, environment));

        /// <summary>True when any grant has at least <paramref name="permission"/>.</summary>
        public bool HasAny(AccessPermission permission) => Grants.Any(g => g.Permission >= permission);

        /// <summary>True when <paramref name="permission"/> applies to every app and environment.</summary>
        public bool HasGlobal(AccessPermission permission) => Can(permission, W, W);

        /// <summary>default.* is part of every merged config: reading it needs read on at least one environment of the app.</summary>
        public bool CanReadDefault(string app) => CanAnyEnvironment(AccessPermission.Read, app);

        /// <summary>Changing default.* affects every environment: it needs write on all environments of the app.</summary>
        public bool CanWriteDefault(string app) => CanAllEnvironments(AccessPermission.Write, app);

        /// <summary>The candidates the caller has any grant (at least <paramref name="permission"/>) on, in input order.</summary>
        public IReadOnlyList<string> AllowedApps(IEnumerable<string> apps, AccessPermission permission = AccessPermission.Read)
            => (apps ?? Enumerable.Empty<string>()).Where(a => CanAnyEnvironment(permission, a)).ToList();

        /// <summary>The candidate environments of <paramref name="app"/> the caller can access, in input order.</summary>
        public IReadOnlyList<string> AllowedEnvironments(string app, IEnumerable<string> environments, AccessPermission permission = AccessPermission.Read)
            => (environments ?? Enumerable.Empty<string>()).Where(e => Can(permission, app, e)).ToList();

        /// <summary>Legacy coarse roles: each permission level held in at least one scope ("config:admin" only when global).</summary>
        public IReadOnlyList<string> Roles
        {
            get
            {
                var highest = Grants.Count == 0 ? 0 : (int)Grants.Max(g => g.Permission);
                return ConfigRoles.All.Take(highest).ToArray();
            }
        }

        public bool EnvironmentEquals(string a, string b) => _environmentComparer.Equals(a, b);

        private bool MatchesApp(AccessGrant grant, string app)
            => grant.App == W || string.Equals(grant.App, app, StringComparison.Ordinal);

        private bool MatchesEnvironment(AccessGrant grant, string environment)
            => grant.Environment == W || _environmentComparer.Equals(grant.Environment, environment);

        private bool Covers(AccessGrant broad, AccessGrant narrow)
            => broad.Permission >= narrow.Permission && MatchesApp(broad, narrow.App) && MatchesEnvironment(broad, narrow.Environment);

        private IReadOnlyList<AccessGrant> Normalize(IEnumerable<AccessGrant> grants)
        {
            // Admin is global only (defensive: the parser already refuses scoped admin).
            var candidates = grants
                .Where(g => g != null && g.App != null && g.Environment != null && Enum.IsDefined(g.Permission))
                .Where(g => g.Permission != AccessPermission.Admin || g.IsGlobal)
                .ToList();

            var distinct = new List<AccessGrant>();
            foreach (var grant in candidates)
                if (!distinct.Any(d => d.Permission == grant.Permission && d.App == grant.App && _environmentComparer.Equals(d.Environment, grant.Environment)))
                    distinct.Add(grant);

            // Distinct grants never cover each other both ways, so "covered by another" is well defined.
            return distinct
                .Where((grant, i) => !distinct.Where((_, j) => j != i).Any(other => Covers(other, grant)))
                .OrderBy(g => g.App == W ? 0 : 1).ThenBy(g => g.App, StringComparer.Ordinal)
                .ThenBy(g => g.Environment == W ? 0 : 1).ThenBy(g => g.Environment, StringComparer.Ordinal)
                .ThenByDescending(g => g.Permission)
                .ToList();
        }

        private static RoleParseStatus Fail(out string reason, RoleParseStatus status, string message)
        {
            reason = message;
            return status;
        }
    }
}
