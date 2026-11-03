using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConfigServer.Domain.Configuration.Formats
{
    /// <summary>
    /// .env: KEY=value lines, '#' comments, optional "export ", single/double quotes. Values are strings;
    /// keys containing "__" become nested objects, and objects keyed 0..n-1 become arrays.
    /// </summary>
    internal static class EnvFormat
    {
        private const string Separator = "__";

        public static JsonNode Parse(string content)
        {
            var root = new JsonObject();
            var lines = content.TrimStart('﻿').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i].TrimEnd('\r');
                var lineNumber = i + 1;
                var trimmed = raw.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                var offset = raw.Length - trimmed.Length; // leading whitespace

                if (trimmed.StartsWith("export", StringComparison.Ordinal) && trimmed.Length > 6 && char.IsWhiteSpace(trimmed[6]))
                {
                    var rest = trimmed.Substring(6).TrimStart();
                    offset += trimmed.Length - rest.Length;
                    trimmed = rest;
                }

                var eq = trimmed.IndexOf('=');
                if (eq < 0)
                    throw new ConfigFormatException($"Invalid .env line: expected KEY=value.", lineNumber, offset + 1);
                var key = trimmed.Substring(0, eq).TrimEnd();
                if (key.Length == 0)
                    throw new ConfigFormatException("Invalid .env line: empty key.", lineNumber, offset + 1);
                if (key.Any(char.IsWhiteSpace))
                    throw new ConfigFormatException($"Invalid .env key '{key}': keys must not contain whitespace.", lineNumber, offset + 1);

                var value = ParseValue(trimmed.Substring(eq + 1), lineNumber, offset + eq + 2);
                Set(root, key, value);
            }
            return ArraysFromIndexedObjects(root);
        }

        private static string ParseValue(string text, int line, int column)
        {
            var start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            column += start;
            text = text.Substring(start);
            if (text.Length == 0) return string.Empty;

            var quote = text[0];
            if (quote == '"' || quote == '\'')
            {
                var sb = new StringBuilder();
                var i = 1;
                for (; i < text.Length; i++)
                {
                    var c = text[i];
                    if (c == quote) break;
                    if (quote == '"' && c == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        switch (text[i])
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            default: sb.Append('\\').Append(text[i]); break;
                        }
                        continue;
                    }
                    sb.Append(c);
                }
                if (i >= text.Length)
                    throw new ConfigFormatException("Unterminated quoted value.", line, column);
                var after = text.Substring(i + 1).Trim();
                if (after.Length > 0 && after[0] != '#')
                    throw new ConfigFormatException("Unexpected characters after the closing quote.", line, column + i + 1);
                return sb.ToString();
            }

            // Unquoted: an inline comment starts at whitespace followed by '#'.
            for (var i = 0; i < text.Length - 1; i++)
                if (char.IsWhiteSpace(text[i]) && text[i + 1] == '#')
                    return text.Substring(0, i).TrimEnd();
            return text.TrimEnd();
        }

        private static void Set(JsonObject root, string key, string value)
        {
            var parts = key.Split(new[] { Separator }, StringSplitOptions.None);
            if (parts.Any(p => p.Length == 0)) parts = new[] { key }; // malformed nesting: keep the key literal

            var current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!(current[parts[i]] is JsonObject next))
                {
                    next = new JsonObject();
                    current[parts[i]] = next;
                }
                current = next;
            }
            current[parts[parts.Length - 1]] = JsonValue.Create(value);
        }

        private static JsonNode ArraysFromIndexedObjects(JsonNode node)
        {
            if (!(node is JsonObject obj)) return node;

            var keys = obj.Select(p => p.Key).ToList();
            foreach (var key in keys)
            {
                var child = obj[key];
                var converted = ArraysFromIndexedObjects(child);
                if (!ReferenceEquals(child, converted)) obj[key] = converted;
            }

            if (keys.Count == 0) return obj;
            var indexes = new SortedDictionary<int, JsonNode>();
            foreach (var key in keys)
            {
                if (key.Length > 1 && key[0] == '0') return obj;
                if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return obj;
                indexes[index] = obj[key];
            }
            if (indexes.Keys.Where((k, pos) => k != pos).Any()) return obj;

            var array = new JsonArray();
            foreach (var item in indexes.Values) array.Add(item?.DeepClone());
            return array;
        }

        public static string Render(JsonNode tree)
        {
            if (!(tree is JsonObject root))
                throw new ConfigFormatException(".env output requires the configuration root to be an object.");

            var lines = new List<string>();
            Flatten(root, null, lines);
            return lines.Count == 0 ? string.Empty : string.Join("\n", lines) + "\n";
        }

        private static void Flatten(JsonNode node, string prefix, List<string> lines)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var item in obj)
                        Flatten(item.Value, prefix == null ? item.Key : prefix + Separator + item.Key, lines);
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                        Flatten(array[i], prefix + Separator + i.ToString(CultureInfo.InvariantCulture), lines);
                    break;
                default:
                    lines.Add(prefix + "=" + Quote(ScalarText(node)));
                    break;
            }
        }

        private static string ScalarText(JsonNode node)
        {
            if (node == null) return string.Empty;
            var value = (JsonValue)node;
            switch (value.GetValueKind())
            {
                case JsonValueKind.String: return value.GetValue<string>();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.Null: return string.Empty;
                default: return value.ToJsonString();
            }
        }

        private static string Quote(string text)
        {
            var needsQuotes = text.Any(c => char.IsWhiteSpace(c) || c == '#' || c == '"' || c == '\'' || c == '\\' || c == '=');
            if (!needsQuotes) return text;
            var sb = new StringBuilder("\"");
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
