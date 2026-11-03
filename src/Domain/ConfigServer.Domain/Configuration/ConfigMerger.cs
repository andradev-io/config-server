using System;
using System.Linq;
using System.Text.Json.Nodes;
using ConfigServer.Domain.Configuration.Formats;

namespace ConfigServer.Domain.Configuration
{
    /// <summary>
    /// Merges a default config with partial environment overrides.
    /// Objects merge recursively; any other value (including arrays) in an override replaces the base value.
    /// </summary>
    public static class ConfigMerger
    {
        /// <summary>JSON-only convenience: merges JSON texts and returns compact JSON. Invalid JSON throws <see cref="System.Text.Json.JsonException"/>.</summary>
        public static string Merge(string baseJson, params string[] overrideJsons)
        {
            if (baseJson == null) throw new ArgumentNullException(nameof(baseJson));
            if (overrideJsons == null) throw new ArgumentNullException(nameof(overrideJsons));

            var baseNode = JsonNode.Parse(baseJson, null, JsonFormat.DocumentOptions);
            var overrides = overrideJsons
                .Where(json => !string.IsNullOrWhiteSpace(json))
                .Select(json => JsonNode.Parse(json, null, JsonFormat.DocumentOptions))
                .ToArray();

            var merged = MergeTrees(baseNode, overrides);
            return merged == null ? "null" : merged.ToJsonString();
        }

        /// <summary>
        /// Tree-based merge shared by all formats. Returns a new tree; inputs are not modified.
        /// Null (C#) overrides are skipped; later overrides win.
        /// </summary>
        public static JsonNode MergeTrees(JsonNode baseNode, params JsonNode[] overrides)
        {
            var result = baseNode?.DeepClone();
            if (overrides == null) return result;
            foreach (var over in overrides)
            {
                if (over == null) continue;
                result = MergeNode(result, over);
            }
            return result;
        }

        /// <summary>Parses every document (any format), merges them in order and renders the result in <paramref name="outputFormat"/>.</summary>
        public static string MergeDocuments(ConfigFormat outputFormat, ConfigDocument baseDocument, params ConfigDocument[] overrides)
        {
            if (baseDocument == null) throw new ArgumentNullException(nameof(baseDocument));
            var baseTree = baseDocument.Format.Parse(baseDocument.Content);
            var overrideTrees = (overrides ?? Array.Empty<ConfigDocument>())
                .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Content))
                .Select(d => d.Format.Parse(d.Content))
                .ToArray();
            return outputFormat.Render(MergeTrees(baseTree, overrideTrees));
        }

        private static JsonNode MergeNode(JsonNode baseNode, JsonNode overrideNode)
        {
            if (baseNode is JsonObject baseObject && overrideNode is JsonObject overrideObject)
            {
                foreach (var property in overrideObject)
                {
                    var existing = baseObject[property.Key];
                    if (existing is JsonObject && property.Value is JsonObject)
                        baseObject[property.Key] = MergeNode(existing.DeepClone(), property.Value);
                    else
                        baseObject[property.Key] = property.Value?.DeepClone();
                }
                return baseObject;
            }
            return overrideNode.DeepClone();
        }
    }
}
