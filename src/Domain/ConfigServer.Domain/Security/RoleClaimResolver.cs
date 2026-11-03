using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConfigServer.Domain.Security
{
    /// <summary>
    /// Reads external roles out of token claims and maps them to config role strings. IdP-neutral:
    /// the role location is a dotted path ("roles", "realm_access.roles", "resource_access.my-client.roles")
    /// or a claim name that itself contains dots or slashes ("https://example.com/roles"). Several locations
    /// may be listed separated by ',' (their roles are united).
    /// </summary>
    public static class RoleClaimResolver
    {
        /// <summary>Placeholder in a role claim path replaced by the configured client id.</summary>
        public const string ClientIdPlaceholder = "{clientId}";

        private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n' };

        /// <summary>
        /// Splits a role claim setting on ',' (trimmed, empty entries dropped) and replaces <see cref="ClientIdPlaceholder"/>
        /// (ignore case) with <paramref name="clientId"/>. Paths that still need a client id when none is given are dropped.
        /// </summary>
        public static IReadOnlyList<string> ResolvePaths(string roleClaim, string clientId)
        {
            if (string.IsNullOrWhiteSpace(roleClaim)) return Array.Empty<string>();
            var paths = new List<string>();
            foreach (var raw in roleClaim.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var usesClientId = raw.Contains(ClientIdPlaceholder, StringComparison.OrdinalIgnoreCase);
                if (usesClientId && string.IsNullOrWhiteSpace(clientId)) continue;
                var path = usesClientId ? raw.Replace(ClientIdPlaceholder, clientId.Trim(), StringComparison.OrdinalIgnoreCase) : raw;
                if (!paths.Contains(path, StringComparer.Ordinal)) paths.Add(path);
            }
            return paths;
        }

        /// <summary>Union (first-seen order, no duplicates) of <see cref="Extract(IEnumerable{Claim}, string)"/> over each path.</summary>
        public static IReadOnlyList<string> Extract(IEnumerable<Claim> claims, IEnumerable<string> paths)
        {
            if (claims == null || paths == null) return Array.Empty<string>();
            var list = claims.Where(c => c != null).ToList();
            return paths.SelectMany(p => ExtractOne(list, p)).Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Values found at <paramref name="path"/> (a ',' separated list is read as several paths, see
        /// <see cref="Extract(IEnumerable{Claim}, IEnumerable{string})"/>). A claim whose type equals the whole path wins; otherwise
        /// the longest claim-type prefix holding a JSON object is walked by the remaining segments (keys containing dots are matched
        /// greedily). JSON arrays yield one role per element; a single string value is split on whitespace. A claim type
        /// repeated several times (how JWT handlers expand JSON arrays) yields each value whole.
        /// </summary>
        public static IReadOnlyList<string> Extract(IEnumerable<Claim> claims, string path)
        {
            if (claims == null || string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
            return Extract(claims, path.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        private static IEnumerable<string> ExtractOne(List<Claim> list, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
            path = path.Trim();
            var segments = path.Split('.');
            var results = new List<string>();

            for (var take = segments.Length; take >= 1 && results.Count == 0; take--)
            {
                var type = string.Join(".", segments, 0, take);
                var matching = list.Where(c => string.Equals(c.Type, type, StringComparison.Ordinal)).ToList();
                if (matching.Count == 0) continue;

                foreach (var claim in matching)
                {
                    if (take == segments.Length)
                    {
                        AddClaimValue(claim.Value, matching.Count > 1, results);
                        continue;
                    }
                    if (TryParse(claim.Value) is JsonObject root && Walk(root, segments, take) is JsonNode node)
                        AddNode(node, results);
                }
            }

            return results.Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Maps external roles to config role strings (not validated: <see cref="AccessGrants.FromRoles"/> parses them).
        /// Values already named like a config role ("config:read", "config:write:billing:dev", ...) pass through; then
        /// <paramref name="roleMap"/> (case-insensitive keys) adds its targets. A map value may list several roles separated by ','.
        /// </summary>
        public static IReadOnlyList<string> MapToRoleStrings(IEnumerable<string> externalRoles, IReadOnlyDictionary<string, string> roleMap)
        {
            var roles = new List<string>();
            foreach (var external in externalRoles ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(external)) continue;
                if (IsConfigRoleString(external)) roles.Add(external.Trim());
                if (roleMap == null) continue;

                var mapped = Lookup(roleMap, external);
                if (mapped == null) continue;
                roles.AddRange(mapped.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            return roles.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Legacy: the global config roles (hierarchy applied) of <see cref="MapToRoleStrings"/>; scoped role strings are dropped.
        /// Authorization uses <see cref="AccessGrants"/> instead.
        /// </summary>
        public static IReadOnlyList<string> MapToConfigRoles(IEnumerable<string> externalRoles, IReadOnlyDictionary<string, string> roleMap)
            => ConfigRoles.Expand(MapToRoleStrings(externalRoles, roleMap));

        private static bool IsConfigRoleString(string value)
            => value.Trim().StartsWith(ConfigRoles.Prefix + ":", StringComparison.OrdinalIgnoreCase);

        private static string Lookup(IReadOnlyDictionary<string, string> map, string key)
        {
            if (map.TryGetValue(key, out var value)) return value;
            foreach (var pair in map)
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }

        private static JsonNode Walk(JsonNode node, string[] segments, int index)
        {
            if (index == segments.Length) return node;
            if (node is not JsonObject obj) return null;
            for (var end = segments.Length; end > index; end--)
            {
                var key = string.Join(".", segments, index, end - index);
                if (obj.TryGetPropertyValue(key, out var child) && child != null && Walk(child, segments, end) is JsonNode found)
                    return found;
            }
            return null;
        }

        private static void AddClaimValue(string value, bool repeated, List<string> results)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim();
            if (trimmed[0] == '[' && TryParse(trimmed) is JsonArray array)
            {
                AddNode(array, results);
                return;
            }
            if (repeated) results.Add(trimmed);
            else results.AddRange(trimmed.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));
        }

        private static void AddNode(JsonNode node, List<string> results)
        {
            switch (node)
            {
                case JsonArray array:
                    foreach (var item in array)
                        if (item is JsonValue element)
                            results.Add(element.TryGetValue<string>(out var text) ? text.Trim() : element.ToJsonString());
                    break;
                case JsonValue scalar when scalar.TryGetValue<string>(out var single):
                    results.AddRange(single.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));
                    break;
            }
        }

        private static JsonNode TryParse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var first = value.TrimStart()[0];
            if (first != '{' && first != '[') return null;
            try
            {
                return JsonNode.Parse(value);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
