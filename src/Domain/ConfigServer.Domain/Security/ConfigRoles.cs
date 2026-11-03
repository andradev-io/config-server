using System;
using System.Collections.Generic;
using System.Linq;

namespace ConfigServer.Domain.Security
{
    /// <summary>
    /// The server's global (coarse) roles. Hierarchy: admin implies write, write implies read. Policies with the same
    /// names check these roles; a principal holds a role when it has that permission in at least one scope.
    /// Scoped roles (config:write:billing:dev) are parsed by <see cref="AccessGrants"/>.
    /// </summary>
    public static class ConfigRoles
    {
        /// <summary>First segment of every config role string.</summary>
        public const string Prefix = "config";

        public const string Read = "config:read";
        public const string Write = "config:write";
        public const string Admin = "config:admin";

        /// <summary>Lowest to highest.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Read, Write, Admin };

        public static bool IsKnown(string role) => Normalize(role) != null;

        /// <summary>The canonical (lower-case) role name, or null when the value is not a config role.</summary>
        public static string Normalize(string role)
            => role == null ? null : All.FirstOrDefault(r => string.Equals(r, role.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Adds the implied roles (admin => write, read; write => read). Unknown values are dropped. Result is ordered lowest first.</summary>
        public static IReadOnlyList<string> Expand(IEnumerable<string> roles)
        {
            var highest = -1;
            foreach (var role in roles ?? Enumerable.Empty<string>())
            {
                var normalized = Normalize(role);
                if (normalized == null) continue;
                highest = Math.Max(highest, IndexOf(normalized));
            }
            return highest < 0 ? Array.Empty<string>() : All.Take(highest + 1).ToArray();
        }

        private static int IndexOf(string role)
        {
            for (var i = 0; i < All.Count; i++)
                if (All[i] == role) return i;
            return -1;
        }
    }
}
