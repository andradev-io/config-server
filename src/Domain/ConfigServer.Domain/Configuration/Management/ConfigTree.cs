using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ConfigServer.Domain.Configuration.Management
{
    /// <summary>Helpers over the common <see cref="JsonNode"/> tree: leaf flattening, dotted-path lookup and type checks.</summary>
    public static class ConfigTree
    {
        private static readonly Regex PlaceholderRegex = new Regex(@"^\$\{.*\}$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

        /// <summary>
        /// Leaf keys (dotted paths) of a tree. Objects are walked; arrays, scalars, nulls and empty objects are leaves.
        /// A non-object root has no keys.
        /// </summary>
        public static IReadOnlyDictionary<string, JsonNode> Flatten(JsonNode tree)
        {
            var result = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            if (tree is JsonObject root) Walk(root, string.Empty, result);
            return result;
        }

        private static void Walk(JsonObject node, string prefix, Dictionary<string, JsonNode> result)
        {
            foreach (var property in node)
            {
                var key = prefix.Length == 0 ? property.Key : prefix + "." + property.Key;
                if (property.Value is JsonObject child && child.Count > 0) Walk(child, key, result);
                else result[key] = property.Value;
            }
        }

        /// <summary>Resolves a dotted path (also to non-leaf nodes). Found = true even when the value is null.</summary>
        public static bool TryGetPath(JsonNode tree, string key, out JsonNode node)
        {
            node = null;
            if (tree is not JsonObject obj || string.IsNullOrEmpty(key)) return false;
            return TryGet(obj, key.Split('.'), 0, out node);
        }

        private static bool TryGet(JsonObject obj, string[] parts, int start, out JsonNode node)
        {
            node = null;
            // Property names may themselves contain dots, so try the shortest name first and extend it.
            for (var end = start + 1; end <= parts.Length; end++)
            {
                var name = string.Join(".", parts, start, end - start);
                if (!obj.TryGetPropertyValue(name, out var value)) continue;
                if (end == parts.Length) { node = value; return true; }
                if (value is JsonObject child && TryGet(child, parts, end, out node)) return true;
            }
            return false;
        }

        /// <summary>Whether a dotted key is well formed (non-empty segments).</summary>
        public static bool IsValidKey(string key)
            => !string.IsNullOrWhiteSpace(key) && key == key.Trim()
               && !key.StartsWith(".", StringComparison.Ordinal) && !key.EndsWith(".", StringComparison.Ordinal)
               && !key.Contains("..", StringComparison.Ordinal);

        /// <summary>
        /// Sets the value at a dotted path, creating intermediate objects (an existing property whose name contains dots is reused).
        /// Returns false with an error when an intermediate node is not an object, or a scalar/array would replace an object that has children.
        /// The value is cloned. Returns changed = false when the tree already held an equal value.
        /// </summary>
        public static bool TrySet(JsonNode tree, string key, JsonNode value, out bool changed, out string error)
        {
            changed = false;
            error = null;
            if (tree is not JsonObject root) { error = "The file root is not an object."; return false; }
            if (!IsValidKey(key)) { error = $"'{key}' is not a valid dotted key."; return false; }

            var parts = key.Split('.');
            if (TryLocate(root, parts, 0, out var owner, out var name))
                return Assign(owner, name, key, value, ref changed, out error);

            var current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var existing = current.TryGetPropertyValue(parts[i], out var child) ? child : null;
                if (existing == null && !current.ContainsKey(parts[i]))
                {
                    var created = new JsonObject();
                    current[parts[i]] = created;
                    current = created;
                    changed = true;
                }
                else if (existing is JsonObject obj) current = obj;
                else
                {
                    error = $"Cannot set '{key}': '{string.Join(".", parts, 0, i + 1)}' is a {TypeOf(existing)}, not an object.";
                    return false;
                }
            }
            return Assign(current, parts[^1], key, value, ref changed, out error);
        }

        private static bool Assign(JsonObject owner, string name, string key, JsonNode value, ref bool changed, out string error)
        {
            error = null;
            if (owner.TryGetPropertyValue(name, out var existing))
            {
                if (existing is JsonObject children && children.Count > 0 && value is not JsonObject)
                {
                    error = $"Cannot set '{key}' to a {TypeOf(value)}: it has child keys.";
                    return false;
                }
                if (existing != null && value != null && existing.ToJsonString() == value.ToJsonString()) return true;
            }
            owner[name] = value?.DeepClone();
            changed = true;
            return true;
        }

        /// <summary>Removes the value at a dotted path and prunes parent objects left empty. False when the path does not exist.</summary>
        public static bool Remove(JsonNode tree, string key)
        {
            if (tree is not JsonObject root || string.IsNullOrEmpty(key)) return false;
            return RemoveCore(root, key.Split('.'), 0);
        }

        private static bool RemoveCore(JsonObject obj, string[] parts, int start)
        {
            for (var end = start + 1; end <= parts.Length; end++)
            {
                var name = string.Join(".", parts, start, end - start);
                if (!obj.TryGetPropertyValue(name, out var value)) continue;
                if (end == parts.Length) { obj.Remove(name); return true; }
                if (value is JsonObject child && RemoveCore(child, parts, end))
                {
                    if (child.Count == 0) obj.Remove(name);
                    return true;
                }
            }
            return false;
        }

        private static bool TryLocate(JsonObject obj, string[] parts, int start, out JsonObject owner, out string name)
        {
            owner = null;
            name = null;
            for (var end = start + 1; end <= parts.Length; end++)
            {
                var candidate = string.Join(".", parts, start, end - start);
                if (!obj.TryGetPropertyValue(candidate, out var value)) continue;
                if (end == parts.Length) { owner = obj; name = candidate; return true; }
                if (value is JsonObject child && TryLocate(child, parts, end, out owner, out name)) return true;
            }
            return false;
        }

        /// <summary>string, number, boolean, array, object, or null for a missing/null node.</summary>
        public static string TypeOf(JsonNode node)
        {
            switch (node)
            {
                case null: return "null";
                case JsonArray _: return "array";
                case JsonObject _: return "object";
                case JsonValue value:
                    switch (value.GetValueKind())
                    {
                        case JsonValueKind.String: return "string";
                        case JsonValueKind.Number: return "number";
                        case JsonValueKind.True:
                        case JsonValueKind.False: return "boolean";
                        default: return "null";
                    }
                default: return "null";
            }
        }

        /// <summary>
        /// Whether a node satisfies a declared type. Null never matches. Strings that parse as a number or boolean
        /// satisfy number/boolean (values from .env are always strings).
        /// </summary>
        public static bool Matches(string type, JsonNode node)
        {
            var actual = TypeOf(node);
            if (actual == "null") return false;
            if (string.Equals(actual, type, StringComparison.Ordinal)) return true;
            if (actual != "string") return false;

            var text = node.GetValue<string>();
            switch (type)
            {
                case "number":
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                case "boolean":
                    return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        /// <summary>True when the value is absent, empty, "${...}" or "secret://..." (i.e. not a plain-text value).</summary>
        public static bool IsPlaceholder(JsonNode node)
        {
            var type = TypeOf(node);
            if (type == "null") return true;
            if (type != "string") return false;
            var text = node.GetValue<string>().Trim();
            return text.Length == 0
                || text.StartsWith("secret://", StringComparison.OrdinalIgnoreCase)
                || PlaceholderRegex.IsMatch(text);
        }
    }
}
