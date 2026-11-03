using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ConfigServer.Domain.Configuration.Formats;

namespace ConfigServer.Domain.Configuration.Management
{
    public sealed record GridCell(bool Masked, JsonNode Value, ConfigFormat Format, string File);

    public sealed record GridEffective(bool Masked, JsonNode Value, string From);

    public sealed class GridVariable
    {
        public string Key { get; init; }
        public string Type { get; init; }
        public bool Secret { get; init; }
        public bool Declared { get; init; }
        public Dictionary<string, GridCell> Values { get; } = new Dictionary<string, GridCell>(StringComparer.Ordinal);
        public Dictionary<string, GridEffective> Effective { get; } = new Dictionary<string, GridEffective>(StringComparer.Ordinal);
    }

    /// <summary>Builds the variables x scopes grid of an app. Secret values are masked here, never returned.</summary>
    public static class OverridesGrid
    {
        public const string DefaultScope = "default";

        public static IReadOnlyList<GridVariable> Build(AppSnapshot app, IReadOnlyList<string> environments)
        {
            var declared = app.VariablesError == null ? app.Declared : Array.Empty<VariableDefinition>();
            var declaredByKey = declared.GroupBy(d => d.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var defaultFile = app.Default?.Tree != null ? app.Default : null;
            var envFiles = new Dictionary<string, ParsedFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in app.Overrides)
                if (o.File?.Tree != null) envFiles[o.Environment] = o.File;

            var allFiles = new List<ParsedFile>();
            if (defaultFile != null) allFiles.Add(defaultFile);
            allFiles.AddRange(app.Overrides.Where(o => o.File?.Tree != null).Select(o => o.File));

            var keys = new HashSet<string>(declaredByKey.Keys, StringComparer.Ordinal);
            foreach (var file in allFiles) keys.UnionWith(file.Leaves.Keys);

            var result = new List<GridVariable>();
            foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                declaredByKey.TryGetValue(key, out var declaration);
                var secret = declaration?.Secret ?? false;
                string inferred = null;

                var nodes = new Dictionary<string, (ParsedFile File, JsonNode Node)>(StringComparer.OrdinalIgnoreCase);
                if (defaultFile != null && TryDefined(defaultFile, key, declaration != null, out var dn)) nodes[DefaultScope] = (defaultFile, dn);
                foreach (var env in envFiles)
                    if (TryDefined(env.Value, key, declaration != null, out var en)) nodes[env.Key] = (env.Value, en);

                foreach (var node in nodes.Values)
                {
                    var type = ConfigTree.TypeOf(node.Node);
                    if (type != "null") { inferred = type; break; }
                }

                var variable = new GridVariable
                {
                    Key = key,
                    Type = declaration?.Type ?? inferred ?? "string",
                    Secret = secret,
                    Declared = declaration != null
                };

                if (nodes.TryGetValue(DefaultScope, out var d)) variable.Values[DefaultScope] = Cell(d, secret);
                foreach (var env in environments)
                {
                    if (nodes.TryGetValue(env, out var own))
                    {
                        variable.Values[env] = Cell(own, secret);
                        variable.Effective[env] = Eff(own, env, secret);
                    }
                    else if (nodes.TryGetValue(DefaultScope, out var inherited))
                    {
                        variable.Effective[env] = Eff(inherited, DefaultScope, secret);
                    }
                }
                result.Add(variable);
            }
            return result;
        }

        private static GridCell Cell((ParsedFile File, JsonNode Node) item, bool secret)
            => new GridCell(secret, secret ? null : item.Node, item.File.Format, item.File.Name);

        private static GridEffective Eff((ParsedFile File, JsonNode Node) item, string from, bool secret)
            => new GridEffective(secret, secret ? null : item.Node, from);

        private static bool TryDefined(ParsedFile file, string key, bool declared, out JsonNode node)
        {
            node = null;
            return file.Leaves.TryGetValue(key, out node) || (declared && ConfigTree.TryGetPath(file.Tree, key, out node));
        }
    }

    /// <summary>One requested change: set a value (or remove the key) in a scope ("default" or an environment).</summary>
    public sealed class OverrideChange
    {
        public string Key { get; set; }
        public string Scope { get; set; }
        public JsonNode Value { get; set; }
        public bool Remove { get; set; }
    }

    /// <summary>An invalid change (maps to HTTP 400).</summary>
    public sealed class OverrideException : Exception
    {
        public string Key { get; }
        public string Scope { get; }

        public OverrideException(string message, string key = null, string scope = null) : base(message)
        {
            Key = key;
            Scope = scope;
        }
    }

    /// <summary>A scope file after applying the changes: mutated tree plus what changed.</summary>
    public sealed class FilePlan
    {
        public string Scope { get; init; }
        public string Name { get; init; }
        public ConfigFormat Format { get; init; }
        public bool Created { get; init; }
        public JsonNode Tree { get; init; }
        public List<string> ChangedKeys { get; } = new List<string>();

        /// <summary>Renders the tree in the file's format and re-parses it to make sure the output is valid.</summary>
        public string Render()
        {
            var text = Format.Render(Tree);
            Format.Parse(text);
            return text;
        }
    }

    /// <summary>Applies override changes to the parsed trees of an app, all-or-nothing (nothing is written here).</summary>
    public static class OverridesEditor
    {
        /// <summary>
        /// Validates and applies <paramref name="changes"/>. Returns one plan per scope file that actually changes.
        /// Throws <see cref="OverrideException"/> on the first invalid change. Secret writes add a plain-text warning.
        /// </summary>
        public static IReadOnlyList<FilePlan> Plan(AppSnapshot app, IReadOnlyList<string> environments,
            IReadOnlyList<OverrideChange> changes, ICollection<string> warnings)
        {
            var declared = app.VariablesError == null ? app.Declared : Array.Empty<VariableDefinition>();
            var plans = new Dictionary<string, FilePlan>(StringComparer.OrdinalIgnoreCase);
            var order = new List<FilePlan>();
            var secretWarned = new HashSet<string>(StringComparer.Ordinal);

            foreach (var change in changes)
            {
                if (change == null) throw new OverrideException("Each change must be an object.");
                var key = change.Key?.Trim();
                if (!ConfigTree.IsValidKey(key)) throw new OverrideException($"'{change.Key}' is not a valid dotted key.", change.Key, change.Scope);

                var scope = CanonicalScope(change.Scope, environments);
                if (scope == null) throw new OverrideException($"Unknown scope '{change.Scope}'.", key, change.Scope);

                if (!plans.TryGetValue(scope, out var plan))
                {
                    plan = CreatePlan(app, scope);
                    plans[scope] = plan;
                    order.Add(plan);
                }

                var declaration = declared.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
                var remove = change.Remove || change.Value == null;
                var changed = false;
                if (remove)
                {
                    changed = ConfigTree.Remove(plan.Tree, key);
                }
                else
                {
                    if (declaration != null && !ConfigTree.Matches(declaration.Type, change.Value))
                        throw new OverrideException($"Value for '{key}' must be of type {declaration.Type}.", key, scope);
                    var value = plan.Format == ConfigFormat.Env ? Stringify(change.Value) : change.Value;
                    if (!ConfigTree.TrySet(plan.Tree, key, value, out changed, out var error))
                        throw new OverrideException(error, key, scope);
                    if (declaration?.Secret == true && secretWarned.Add(plan.Name + "\n" + key))
                        warnings.Add($"{key}: stored in plain text in {plan.Name} until a secret resolver is configured");
                }
                if (changed && !plan.ChangedKeys.Contains(key, StringComparer.Ordinal)) plan.ChangedKeys.Add(key);
            }
            return order.Where(p => p.ChangedKeys.Count > 0).ToList();
        }

        private static string CanonicalScope(string scope, IReadOnlyList<string> environments)
        {
            if (string.IsNullOrWhiteSpace(scope)) return null;
            if (string.Equals(scope, OverridesGrid.DefaultScope, StringComparison.OrdinalIgnoreCase)) return OverridesGrid.DefaultScope;
            return environments.FirstOrDefault(e => string.Equals(e, scope, StringComparison.OrdinalIgnoreCase));
        }

        private static FilePlan CreatePlan(AppSnapshot app, string scope)
        {
            var existing = scope == OverridesGrid.DefaultScope
                ? app.Default
                : app.Overrides.FirstOrDefault(o => string.Equals(o.Environment, scope, StringComparison.OrdinalIgnoreCase))?.File;

            if (existing != null)
            {
                if (existing.Tree == null) throw new OverrideException($"{existing.Name} cannot be edited: {existing.Error}", null, scope);
                if (existing.Tree is not JsonObject)
                    throw new OverrideException($"{existing.Name} cannot be edited: the root is not an object.", null, scope);
                return new FilePlan { Scope = scope, Name = existing.Name, Format = existing.Format, Created = false, Tree = existing.Tree };
            }

            // New file: same extension as the default when it is yaml/yml/env, otherwise json.
            var extension = ".json";
            var format = ConfigFormat.Json;
            if (app.Default != null && app.Default.Format != ConfigFormat.Json)
            {
                format = app.Default.Format;
                extension = app.Default.Name.Substring(app.Default.Name.LastIndexOf('.'));
            }
            return new FilePlan { Scope = scope, Name = scope + extension, Format = format, Created = true, Tree = new JsonObject() };
        }

        /// <summary>Values destined for .env files are strings.</summary>
        private static JsonNode Stringify(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    var copy = new JsonObject();
                    foreach (var p in obj) copy[p.Key] = Stringify(p.Value);
                    return copy;
                case JsonArray array:
                    var list = new JsonArray();
                    foreach (var item in array) list.Add(Stringify(item));
                    return list;
                case JsonValue value:
                    return value.GetValueKind() == JsonValueKind.String ? value.DeepClone() : JsonValue.Create(value.ToJsonString());
                default:
                    return node;
            }
        }
    }

    /// <summary>Detects comments that a parse/render round trip would drop.</summary>
    public static class CommentDetector
    {
        private static readonly Regex HashComment = new Regex(@"(^|\s)#", RegexOptions.CultureInvariant);

        public static bool HasComments(ConfigFormat format, string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            if (format != ConfigFormat.Json) return HashComment.IsMatch(raw);
            try
            {
                using var _ = JsonDocument.Parse(raw, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Disallow });
                return false;
            }
            catch (JsonException)
            {
                return true;
            }
        }
    }
}
