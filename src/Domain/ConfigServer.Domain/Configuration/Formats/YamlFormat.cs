using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ConfigServer.Domain.Configuration.Formats
{
    internal static class YamlFormat
    {
        private const int MaxDepth = 100;
        private static readonly Regex IntRegex = new Regex(@"^[-+]?[0-9]+\z", RegexOptions.CultureInvariant);
        private static readonly Regex FloatRegex = new Regex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?\z", RegexOptions.CultureInvariant);

        public static JsonNode Parse(string content)
        {
            var stream = new YamlStream();
            using (var reader = new StringReader(content))
            {
                var parser = new Parser(reader);
                try
                {
                    stream.Load(parser);
                }
                catch (YamlException ex)
                {
                    throw new ConfigFormatException("Invalid YAML: " + (ex.InnerException?.Message ?? ex.Message),
                        ToInt(ex.Start.Line), ToInt(ex.Start.Column), ex);
                }
                catch (InvalidOperationException ex)
                {
                    // YamlDotNet's scanner can fail this way on truncated flow collections; report the last position it reached.
                    var mark = parser.Current?.End ?? default;
                    throw new ConfigFormatException("Invalid YAML: unexpected end of input or malformed structure.",
                        ToInt(mark.Line), ToInt(mark.Column), ex);
                }
            }

            if (stream.Documents.Count == 0) return new JsonObject();
            return Convert(stream.Documents[0].RootNode, 0);
        }

        private static int? ToInt(long value) => value > 0 ? (int?)value : null;

        private static JsonNode Convert(YamlNode node, int depth)
        {
            if (depth > MaxDepth) throw Error("YAML nesting is too deep (or contains a recursive alias).", node);

            switch (node)
            {
                case YamlScalarNode scalar:
                    return ConvertScalar(scalar);

                case YamlSequenceNode sequence:
                    var array = new JsonArray();
                    foreach (var child in sequence.Children) array.Add(Convert(child, depth + 1));
                    return array;

                case YamlMappingNode mapping:
                    var obj = new JsonObject();
                    // Merge keys ("<<") first so explicit keys override them.
                    foreach (var pair in mapping.Children.Where(p => IsMergeKey(p.Key)))
                        foreach (var source in MergeSources(pair.Value))
                            foreach (var item in (JsonObject)Convert(source, depth + 1))
                                obj[item.Key] = item.Value?.DeepClone();
                    foreach (var pair in mapping.Children)
                    {
                        if (!(pair.Key is YamlScalarNode key)) throw Error("YAML mapping keys must be scalars.", pair.Key);
                        if (IsMergeKey(key)) continue;
                        obj[key.Value ?? string.Empty] = Convert(pair.Value, depth + 1);
                    }
                    return obj;

                default:
                    throw Error("Unsupported YAML node.", node);
            }
        }

        private static bool IsMergeKey(YamlNode key)
            => key is YamlScalarNode k && k.Value == "<<" && k.Style == ScalarStyle.Plain;

        private static IEnumerable<YamlNode> MergeSources(YamlNode value)
        {
            if (value is YamlMappingNode) return new[] { value };
            if (value is YamlSequenceNode seq && seq.Children.All(c => c is YamlMappingNode)) return seq.Children;
            throw Error("A '<<' merge key requires a mapping or a list of mappings.", value);
        }

        private static ConfigFormatException Error(string message, YamlNode node)
            => new ConfigFormatException(message, ToInt(node.Start.Line), ToInt(node.Start.Column));

        private static JsonNode ConvertScalar(YamlScalarNode scalar)
        {
            var text = scalar.Value ?? string.Empty;
            var plain = scalar.Style == ScalarStyle.Plain && scalar.Tag.IsEmpty;
            if (plain && TryResolvePlain(text, out var resolved)) return resolved;
            return JsonValue.Create(text);
        }

        /// <summary>True when a plain scalar is not a string (null, bool or number); <paramref name="node"/> is null for YAML null.</summary>
        private static bool TryResolvePlain(string text, out JsonNode node)
        {
            node = null;
            if (text.Length == 0 || text == "~" || text == "null" || text == "Null" || text == "NULL") return true;
            if (text == "true" || text == "True" || text == "TRUE") { node = JsonValue.Create(true); return true; }
            if (text == "false" || text == "False" || text == "FALSE") { node = JsonValue.Create(false); return true; }
            if (IntRegex.IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            {
                node = JsonValue.Create(l);
                return true;
            }
            if (FloatRegex.IsMatch(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && !double.IsInfinity(d) && !double.IsNaN(d))
            {
                node = JsonValue.Create(d);
                return true;
            }
            return false;
        }

        public static string Render(JsonNode tree)
        {
            var stream = new YamlStream(new YamlDocument(ToYaml(tree)));
            using (var writer = new StringWriter { NewLine = "\n" })
            {
                stream.Save(writer, false);
                var text = writer.ToString().Replace("\r\n", "\n");
                if (text.StartsWith("---\n", StringComparison.Ordinal)) text = text.Substring(4);
                else if (text.StartsWith("--- ", StringComparison.Ordinal)) text = text.Substring(4);
                if (text.EndsWith("...\n", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 4);
                return text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n";
            }
        }

        private static YamlNode ToYaml(JsonNode node)
        {
            switch (node)
            {
                case null:
                    return new YamlScalarNode("null");
                case JsonObject obj:
                    var mapping = new YamlMappingNode();
                    foreach (var item in obj) mapping.Add(StringNode(item.Key), ToYaml(item.Value));
                    return mapping;
                case JsonArray array:
                    var sequence = new YamlSequenceNode();
                    foreach (var item in array) sequence.Add(ToYaml(item));
                    return sequence;
                default:
                    var value = (JsonValue)node;
                    switch (value.GetValueKind())
                    {
                        case JsonValueKind.String: return StringNode(value.GetValue<string>());
                        case JsonValueKind.True: return new YamlScalarNode("true");
                        case JsonValueKind.False: return new YamlScalarNode("false");
                        case JsonValueKind.Null: return new YamlScalarNode("null");
                        default: return new YamlScalarNode(value.ToJsonString());
                    }
            }
        }

        // Strings that would read back as another type (or span lines) are double-quoted so types survive a round trip.
        private static YamlScalarNode StringNode(string text)
        {
            var scalar = new YamlScalarNode(text);
            if (TryResolvePlain(text, out _) || text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
                scalar.Style = ScalarStyle.DoubleQuoted;
            return scalar;
        }
    }
}
