using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Domain.Configuration.Formats;

namespace ConfigServer.Domain.Configuration.Management
{
    /// <summary>A parsed config file. Tree is null (and Error set) when the file is too large or invalid.</summary>
    public sealed class ParsedFile
    {
        public string Name { get; init; }
        public ConfigFormat Format { get; init; }
        public JsonNode Tree { get; init; }
        public string Error { get; init; }
        public IReadOnlyDictionary<string, JsonNode> Leaves { get; init; } = new Dictionary<string, JsonNode>();
    }

    public sealed class EnvironmentFile
    {
        public string Environment { get; init; }
        public ParsedFile File { get; init; }
    }

    public sealed class AppSnapshot
    {
        public string Name { get; init; }
        public ParsedFile Default { get; init; }
        public IReadOnlyList<EnvironmentFile> Overrides { get; init; } = Array.Empty<EnvironmentFile>();
        public IReadOnlyList<VariableDefinition> Declared { get; init; } = Array.Empty<VariableDefinition>();
        public string VariablesError { get; init; }
    }

    public sealed class ConfigSnapshot
    {
        public IReadOnlyList<EnvironmentDefinition> Registry { get; init; } = Array.Empty<EnvironmentDefinition>();
        public IReadOnlyList<AppSnapshot> Apps { get; init; } = Array.Empty<AppSnapshot>();
        public IReadOnlyList<SnapshotWarning> Warnings { get; init; } = Array.Empty<SnapshotWarning>();
    }

    /// <summary>Reads and parses the registry, declared variables and config files from an <see cref="IConfigCatalog"/>.</summary>
    public static class ConfigCatalogReader
    {
        /// <summary>Files larger than this are not scanned.</summary>
        public const long MaxScanBytes = 1_048_576;

        public static async Task<List<EnvironmentDefinition>> ReadRegistryAsync(IConfigCatalog catalog, CancellationToken cancellationToken = default)
        {
            var text = await catalog.ReadReservedAsync(null, ConfigNames.EnvironmentsFileName, cancellationToken);
            return ReservedFiles.ParseEnvironments(text);
        }

        public static async Task<List<VariableDefinition>> ReadVariablesAsync(IConfigCatalog catalog, string application, CancellationToken cancellationToken = default)
        {
            var text = await catalog.ReadReservedAsync(application, ConfigNames.VariablesFileName, cancellationToken);
            return ReservedFiles.ParseVariables(text);
        }

        /// <summary>Environment names inferred from override file names, per application (apps without overrides are omitted).</summary>
        public static async Task<Dictionary<string, List<string>>> ReadOverrideNamesAsync(IConfigCatalog catalog, CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var app in await catalog.ListAppsAsync(cancellationToken))
            {
                var files = await catalog.ListFilesAsync(app, cancellationToken);
                if (files == null) continue;
                var names = OverrideNames(files);
                if (names.Count > 0) result[app] = names;
            }
            return result;
        }

        public static List<string> OverrideNames(IEnumerable<ConfigFileInfo> files)
            => files.Where(f => !ConfigNames.IsDefault(f.Name))
                .Select(f => ConfigNames.LogicalName(f.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>Loads every app (or only <paramref name="onlyApp"/>) with parsed files and declared variables. Invalid files are recorded, not thrown.</summary>
        public static async Task<ConfigSnapshot> LoadAsync(IConfigCatalog catalog, string onlyApp = null, CancellationToken cancellationToken = default)
        {
            var warnings = new List<SnapshotWarning>();
            var registry = new List<EnvironmentDefinition>();
            try
            {
                registry = await ReadRegistryAsync(catalog, cancellationToken);
            }
            catch (ConfigFormatException ex)
            {
                warnings.Add(new SnapshotWarning(null, ConfigNames.EnvironmentsFileName, ex.Message));
            }

            var apps = new List<AppSnapshot>();
            var names = onlyApp != null ? new List<string> { onlyApp } : (await catalog.ListAppsAsync(cancellationToken)).ToList();
            foreach (var name in names)
            {
                var files = await catalog.ListFilesAsync(name, cancellationToken);
                if (files == null) continue;

                ParsedFile defaultFile = null;
                var overrides = new List<EnvironmentFile>();
                foreach (var info in files)
                {
                    var parsed = await ParseFileAsync(catalog, name, info, cancellationToken);
                    if (parsed.Error != null) warnings.Add(new SnapshotWarning(name, info.Name, parsed.Error));
                    if (ConfigNames.IsDefault(info.Name)) defaultFile = parsed;
                    else if (!overrides.Any(o => string.Equals(o.Environment, ConfigNames.LogicalName(info.Name), StringComparison.OrdinalIgnoreCase)))
                        overrides.Add(new EnvironmentFile { Environment = ConfigNames.LogicalName(info.Name), File = parsed });
                }

                var declared = new List<VariableDefinition>();
                string variablesError = null;
                try
                {
                    declared = await ReadVariablesAsync(catalog, name, cancellationToken);
                }
                catch (ConfigFormatException ex)
                {
                    variablesError = ex.Message;
                    warnings.Add(new SnapshotWarning(name, ConfigNames.VariablesFileName, ex.Message));
                }

                apps.Add(new AppSnapshot
                {
                    Name = name,
                    Default = defaultFile,
                    Overrides = overrides,
                    Declared = declared,
                    VariablesError = variablesError
                });
            }
            return new ConfigSnapshot { Registry = registry, Apps = apps, Warnings = warnings };
        }

        private static async Task<ParsedFile> ParseFileAsync(IConfigCatalog catalog, string app, ConfigFileInfo info, CancellationToken cancellationToken)
        {
            if (info.Size > MaxScanBytes)
                return new ParsedFile { Name = info.Name, Format = info.Format, Error = "File exceeds the 1 MB scan limit." };

            var content = await catalog.ReadAsync(app, info.Name, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
                return new ParsedFile { Name = info.Name, Format = info.Format, Tree = new JsonObject() };
            try
            {
                var tree = info.Format.Parse(content);
                return new ParsedFile { Name = info.Name, Format = info.Format, Tree = tree, Leaves = ConfigTree.Flatten(tree) };
            }
            catch (ConfigFormatException ex)
            {
                var where = ex.Line.HasValue ? $" (line {ex.Line}{(ex.Column.HasValue ? ", column " + ex.Column : string.Empty)})" : string.Empty;
                return new ParsedFile { Name = info.Name, Format = info.Format, Error = ex.Message + where };
            }
        }
    }

    /// <summary>Builds the environment list: declared registry entries plus environments inferred from override file names.</summary>
    public static class EnvironmentCatalog
    {
        public static List<EnvironmentInfo> Build(IReadOnlyList<EnvironmentDefinition> registry, IReadOnlyDictionary<string, List<string>> overrideNamesByApp)
        {
            int Count(string name) => overrideNamesByApp.Values.Count(list => list.Contains(name, StringComparer.OrdinalIgnoreCase));

            var declared = registry
                .OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new EnvironmentInfo(e.Name, e.Description ?? string.Empty, e.Order, true, Count(e.Name)))
                .ToList();

            var implicitNames = overrideNamesByApp.Values.SelectMany(v => v)
                .Where(n => !registry.Any(e => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new EnvironmentInfo(n, string.Empty, 0, false, Count(n)));

            // Declared environments come first (by order, then name); inferred ones follow alphabetically.
            return declared.Concat(implicitNames).ToList();
        }

        /// <summary>Environment names for an app: declared registry entries first (by order), then its override-only ones.</summary>
        public static List<string> NamesForApp(IReadOnlyList<EnvironmentDefinition> registry, IEnumerable<string> overrideNames)
        {
            var names = registry.OrderBy(e => e.Order).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => e.Name).ToList();
            names.AddRange(overrideNames
                .Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            return names;
        }
    }
}
