using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ConfigServer.Domain.Security.Identity
{
    /// <summary>Who receives a grant in the identity provider.</summary>
    public enum SubjectType
    {
        User,
        Group
    }

    public static class SubjectTypes
    {
        public static string ToName(this SubjectType type) => type == SubjectType.Group ? "group" : "user";

        /// <summary>"user" | "group" (ignore case).</summary>
        public static bool TryParse(string value, out SubjectType type)
        {
            type = SubjectType.User;
            if (string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(value, "group", StringComparison.OrdinalIgnoreCase)) return false;
            type = SubjectType.Group;
            return true;
        }
    }

    /// <summary>Identifier rules for users and groups sent to the identity provider (they become URL path segments).</summary>
    public static class IdentitySubjects
    {
        public const int MaxIdLength = 255;

        /// <summary>Non-empty, at most 255 characters, no control characters, no '/' or '\', not "." or "..".</summary>
        public static bool IsValidId(string id)
            => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxIdLength && id != "." && id != ".."
               && !id.Any(c => char.IsControl(c) || c == '/' || c == '\\');
    }

    public sealed record IdentityUser(string Id, string Username, string DisplayName, string Email);

    public sealed record IdentityGroup(string Id, string Name, string Path);

    /// <summary>
    /// One config grant held by a subject through a client role. <see cref="Role"/> is the role name as stored in the
    /// identity provider; <see cref="Inherited"/> grants come from the group named <see cref="Via"/> (users only).
    /// </summary>
    public sealed record SubjectGrant(
        string Permission,
        string App,
        string Environment,
        string Role,
        bool Inherited,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string Via = null)
    {
        /// <summary>The grant carried by <paramref name="roleName"/>, or null when it is not a valid config grant role.</summary>
        public static SubjectGrant FromRoleName(string roleName, bool inherited = false, string via = null)
            => ConfigGrant.TryFromRoleName(roleName, out var grant) ? From(grant, roleName, inherited, via) : null;

        public static SubjectGrant From(ConfigGrant grant, string roleName = null, bool inherited = false, string via = null)
            => new SubjectGrant(grant.Permission.ToName(), grant.App, grant.Environment, roleName ?? grant.RoleName, inherited, inherited ? via : null);

        /// <summary>
        /// Config grants among role names: invalid/other roles dropped, each (role, inherited, via) once, direct grants first,
        /// then by via, app, environment and permission.
        /// </summary>
        public static IReadOnlyList<SubjectGrant> Collect(IEnumerable<SubjectGrant> grants)
            => (grants ?? Enumerable.Empty<SubjectGrant>())
                .Where(g => g != null)
                .GroupBy(g => (g.Role, g.Inherited, g.Via))
                .Select(g => g.First())
                .OrderBy(g => g.Inherited ? 1 : 0)
                .ThenBy(g => g.Via ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(g => g.App == AccessGrant.Wildcard ? 0 : 1).ThenBy(g => g.App, StringComparer.Ordinal)
                .ThenBy(g => g.Environment == AccessGrant.Wildcard ? 0 : 1).ThenBy(g => g.Environment, StringComparer.Ordinal)
                .ThenBy(g => g.Permission, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>
    /// What <c>GET api/access/info</c> returns. <see cref="ConfigClientId"/> is the client that holds the grant roles;
    /// <see cref="TokenEndpoint"/> is the public OIDC token endpoint machine credentials request tokens from (null when unknown).
    /// </summary>
    public sealed record IdentityAdminInfo(string Provider, bool Enabled, string ConfigClientId, string Note, string TokenEndpoint = null);

    /// <summary>
    /// A machine credential ("OIDC API key") managed in the identity provider: a confidential client with a service account
    /// that holds config grants. <see cref="Id"/> is the provider's internal id (used in the API), <see cref="ClientId"/>
    /// what the credential presents when it requests a token. The secret is never part of this record.
    /// </summary>
    public sealed record MachineClient(
        string Id,
        string ClientId,
        string Name,
        IReadOnlyList<SubjectGrant> Grants,
        bool Enabled,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CreatedAt = null);

    /// <summary>A created or rotated machine credential together with its secret, which is only ever returned once.</summary>
    public sealed record MachineClientSecret(MachineClient Client, string Secret);

    /// <summary>Rules shared by every provider for machine credentials.</summary>
    public static class MachineClients
    {
        /// <summary>Prefix of the client ids this server creates (and the only clients it lists or changes).</summary>
        public const string ClientIdPrefix = "cfs-";

        public const int MaxNameLength = 100;
        public const int MaxGrants = 50;

        /// <summary>Non-empty after trimming, at most <see cref="MaxNameLength"/> characters, no control characters.</summary>
        public static bool IsValidName(string name)
            => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength && !name.Any(char.IsControl);
    }

    public enum IdentityAdminErrorKind
    {
        /// <summary>No identity provider is configured (IdentityAdmin:Provider=None).</summary>
        NotConfigured,

        /// <summary>The user or group does not exist.</summary>
        NotFound,

        /// <summary>The request is invalid (bad subject id, ...).</summary>
        Invalid,

        /// <summary>The identity provider rejected the server's admin credentials.</summary>
        Unauthorized,

        /// <summary>The admin service account lacks a permission in the identity provider.</summary>
        Denied,

        /// <summary>The settings do not match the identity provider (unknown realm or config client, ...).</summary>
        Misconfigured,

        /// <summary>The identity provider is unreachable, timed out, failed or answered something unexpected.</summary>
        Unavailable
    }

    /// <summary>
    /// A failure of an <see cref="IIdentityAdmin"/> call. <see cref="Exception.Message"/> is safe to return to an admin:
    /// it names the setting or the provider permission to fix and never contains credentials.
    /// </summary>
    public sealed class IdentityAdminException : Exception
    {
        public IdentityAdminException(IdentityAdminErrorKind kind, string message, Exception inner = null) : base(message, inner)
        {
            Kind = kind;
        }

        public IdentityAdminErrorKind Kind { get; }
    }
}
