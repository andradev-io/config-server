using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace ConfigServer.Domain.Configuration.Formats
{
    /// <summary>Detection, parsing and rendering for <see cref="ConfigFormat"/>. The common tree is a <see cref="JsonNode"/>.</summary>
    public static class ConfigFormatExtensions
    {
        /// <summary>File extensions in lookup order (first found wins).</summary>
        public static readonly IReadOnlyList<string> LookupExtensions = new[] { ".json", ".yaml", ".yml", ".env" };

        /// <summary>Format from a file extension (with or without the dot, case-insensitive), or null when unsupported.</summary>
        public static ConfigFormat? FromExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return null;
            switch (extension.TrimStart('.').ToLowerInvariant())
            {
                case "json": return ConfigFormat.Json;
                case "yaml":
                case "yml": return ConfigFormat.Yaml;
                case "env": return ConfigFormat.Env;
                default: return null;
            }
        }

        /// <summary>Format from a file name such as "default.yaml", or null when the extension is unsupported.</summary>
        public static ConfigFormat? FromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            var dot = fileName.LastIndexOf('.');
            return dot < 0 ? null : FromExtension(fileName.Substring(dot));
        }

        /// <summary>Format from a query value: json, yaml or env (case-insensitive).</summary>
        public static bool TryParseName(string name, out ConfigFormat format)
        {
            switch ((name ?? string.Empty).ToLowerInvariant())
            {
                case "json": format = ConfigFormat.Json; return true;
                case "yaml": format = ConfigFormat.Yaml; return true;
                case "env": format = ConfigFormat.Env; return true;
                default: format = default; return false;
            }
        }

        public static string ToName(this ConfigFormat format) => format.ToString().ToLowerInvariant();

        public static string ContentType(this ConfigFormat format)
        {
            switch (format)
            {
                case ConfigFormat.Json: return "application/json";
                case ConfigFormat.Yaml: return "application/yaml";
                default: return "text/plain";
            }
        }

        /// <summary>Parses text into the common tree. Throws <see cref="ConfigFormatException"/> on invalid input.</summary>
        public static System.Text.Json.Nodes.JsonNode Parse(this ConfigFormat format, string content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            switch (format)
            {
                case ConfigFormat.Json: return JsonFormat.Parse(content);
                case ConfigFormat.Yaml: return YamlFormat.Parse(content);
                case ConfigFormat.Env: return EnvFormat.Parse(content);
                default: throw new ArgumentOutOfRangeException(nameof(format));
            }
        }

        /// <summary>Renders the common tree as text in the given format.</summary>
        public static string Render(this ConfigFormat format, JsonNode tree)
        {
            switch (format)
            {
                case ConfigFormat.Json: return JsonFormat.Render(tree);
                case ConfigFormat.Yaml: return YamlFormat.Render(tree);
                case ConfigFormat.Env: return EnvFormat.Render(tree);
                default: throw new ArgumentOutOfRangeException(nameof(format));
            }
        }
    }
}
