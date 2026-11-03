using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ConfigServer.Domain.Configuration.Management
{
    public sealed record VariableUsage(
        string App,
        bool Required,
        bool Secret,
        bool Declared,
        IReadOnlyList<string> DefinedIn,
        IReadOnlyList<string> Environments);

    public sealed record VariableUsageDetail(
        string App,
        bool Required,
        bool Secret,
        bool Declared,
        IReadOnlyList<string> DefinedIn,
        IReadOnlyList<string> Environments,
        IReadOnlyDictionary<string, string> ByEnvironment);

    public sealed record VariableEntry(string Key, string Type, IReadOnlyList<VariableUsage> UsedBy, int AppCount);

    public sealed record VariableDetail(string Key, string Type, IReadOnlyList<VariableUsageDetail> UsedBy, int AppCount);

    public sealed class VariableQuery
    {
        public string Search { get; init; }
        public string App { get; init; }
        public string Environment { get; init; }
        public bool DeclaredOnly { get; init; }
    }

    /// <summary>Cross-project variable index built from a <see cref="ConfigSnapshot"/>. Values are never part of the result.</summary>
    public static class VariableIndex
    {
        private sealed class Row
        {
            public AppSnapshot App;
            public VariableDefinition Declaration;
            public List<string> DefinedIn = new List<string>();
            public List<string> Environments = new List<string>();
            public bool InDefault;
            public string InferredType;
        }

        public static IReadOnlyList<VariableEntry> Build(ConfigSnapshot snapshot, VariableQuery query = null)
        {
            query ??= new VariableQuery();
            var entries = new List<VariableEntry>();
            foreach (var (key, rows) in Collect(snapshot).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrEmpty(query.Search) && key.IndexOf(query.Search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (query.DeclaredOnly && !rows.Any(r => r.Declaration != null)) continue;

                var kept = rows.AsEnumerable();
                if (!string.IsNullOrEmpty(query.App))
                    kept = kept.Where(r => string.Equals(r.App.Name, query.App, StringComparison.Ordinal));
                if (!string.IsNullOrEmpty(query.Environment))
                    kept = kept.Where(r => r.Environments.Contains(query.Environment, StringComparer.OrdinalIgnoreCase)
                        || (r.Declaration != null && r.Declaration.IsRequiredFor(query.Environment)));

                var usages = kept.OrderBy(r => r.App.Name, StringComparer.Ordinal).ToList();
                if (usages.Count == 0) continue;
                entries.Add(new VariableEntry(key, TypeOf(rows), usages.Select(ToUsage).ToList(), usages.Count));
            }
            return entries;
        }

        /// <summary>
        /// One key with the per-environment matrix for each app, or null when the key is unknown.
        /// <paramref name="isVisible"/> (app, environment) limits the matrix columns (all when null).
        /// </summary>
        public static VariableDetail GetDetail(ConfigSnapshot snapshot, string key, Func<string, string, bool> isVisible = null)
        {
            if (!Collect(snapshot).TryGetValue(key, out var rows)) return null;
            var usages = rows.OrderBy(r => r.App.Name, StringComparer.Ordinal).Select(r =>
            {
                var envNames = EnvironmentCatalog.NamesForApp(snapshot.Registry, r.App.Overrides.Select(o => o.Environment))
                    .Where(env => isVisible == null || isVisible(r.App.Name, env));
                var matrix = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var env in envNames)
                {
                    matrix[env] = r.Environments.Contains(env, StringComparer.OrdinalIgnoreCase) ? "defined"
                        : r.InDefault ? "inherited"
                        : "missing";
                }
                var usage = ToUsage(r);
                return new VariableUsageDetail(usage.App, usage.Required, usage.Secret, usage.Declared, usage.DefinedIn, usage.Environments, matrix);
            }).ToList();
            return new VariableDetail(key, TypeOf(rows), usages, usages.Count);
        }

        private static VariableUsage ToUsage(Row r)
            => new VariableUsage(r.App.Name, r.Declaration?.Required ?? false, r.Declaration?.Secret ?? false,
                r.Declaration != null, r.DefinedIn, r.Environments);

        private static string TypeOf(List<Row> rows)
            => rows.Select(r => r.Declaration?.Type).FirstOrDefault(t => t != null)
               ?? rows.Select(r => r.InferredType).FirstOrDefault(t => t != null && t != "null")
               ?? "string";

        private static Dictionary<string, List<Row>> Collect(ConfigSnapshot snapshot)
        {
            var result = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
            foreach (var app in snapshot.Apps.OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                var declared = app.VariablesError == null ? app.Declared : Array.Empty<VariableDefinition>();
                var declaredByKey = declared.GroupBy(d => d.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                var files = new List<(ParsedFile File, string Environment)>();
                if (app.Default?.Tree != null) files.Add((app.Default, null));
                foreach (var o in app.Overrides)
                    if (o.File?.Tree != null) files.Add((o.File, o.Environment));

                var keys = new HashSet<string>(declaredByKey.Keys, StringComparer.Ordinal);
                foreach (var (file, _) in files) keys.UnionWith(file.Leaves.Keys);

                foreach (var key in keys)
                {
                    declaredByKey.TryGetValue(key, out var declaration);
                    var row = new Row { App = app, Declaration = declaration };
                    foreach (var (file, environment) in files)
                    {
                        JsonNode node = null;
                        var defined = file.Leaves.TryGetValue(key, out node)
                            || (declaration != null && ConfigTree.TryGetPath(file.Tree, key, out node));
                        if (!defined) continue;

                        row.DefinedIn.Add(file.Name);
                        row.InferredType ??= NonNullType(node);
                        if (environment == null) row.InDefault = true;
                        else row.Environments.Add(environment);
                    }
                    if (!result.TryGetValue(key, out var list)) result[key] = list = new List<Row>();
                    list.Add(row);
                }
            }
            return result;
        }

        private static string NonNullType(JsonNode node)
        {
            var type = ConfigTree.TypeOf(node);
            return type == "null" ? null : type;
        }
    }
}
