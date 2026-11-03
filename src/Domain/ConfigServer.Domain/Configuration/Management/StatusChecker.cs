using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ConfigServer.Domain.Configuration.Management
{
    public sealed record TypeMismatch(string Key, string Expected, string Actual);

    public sealed record EnvironmentStatus(
        string Name,
        bool Ok,
        string Error,
        IReadOnlyList<string> Missing,
        IReadOnlyList<TypeMismatch> TypeMismatches,
        IReadOnlyList<string> SecretsInPlainText);

    public sealed record AppStatus(string App, IReadOnlyList<EnvironmentStatus> Environments, IReadOnlyList<string> Undeclared);

    /// <summary>Checks the declared variables of an app against the merged config (default + override) of each environment. Never exposes values.</summary>
    public static class StatusChecker
    {
        public static AppStatus Check(AppSnapshot app, IReadOnlyList<EnvironmentDefinition> registry)
        {
            var declared = app.VariablesError == null ? app.Declared : Array.Empty<VariableDefinition>();
            var names = EnvironmentCatalog.NamesForApp(registry, app.Overrides.Select(o => o.Environment));
            var environments = names.Select(name => CheckEnvironment(app, declared, name)).ToList();
            return new AppStatus(app.Name, environments, Undeclared(app, declared));
        }

        private static EnvironmentStatus CheckEnvironment(AppSnapshot app, IReadOnlyList<VariableDefinition> declared, string environment)
        {
            var overrideFile = app.Overrides
                .FirstOrDefault(o => string.Equals(o.Environment, environment, StringComparison.OrdinalIgnoreCase))?.File;

            var error = app.Default?.Error != null ? $"{app.Default.Name}: {app.Default.Error}"
                : overrideFile?.Error != null ? $"{overrideFile.Name}: {overrideFile.Error}"
                : app.VariablesError;
            if (error != null)
                return new EnvironmentStatus(environment, false, error, Array.Empty<string>(), Array.Empty<TypeMismatch>(), Array.Empty<string>());

            var merged = ConfigMerger.MergeTrees(app.Default?.Tree ?? new JsonObject(), overrideFile?.Tree);
            var missing = new List<string>();
            var mismatches = new List<TypeMismatch>();
            var secrets = new List<string>();

            foreach (var variable in declared)
            {
                var found = ConfigTree.TryGetPath(merged, variable.Key, out var value);

                if (variable.IsRequiredFor(environment))
                {
                    if (!found || ConfigTree.TypeOf(value) == "null") missing.Add(variable.Key);
                    else if (!ConfigTree.Matches(variable.Type, value))
                        mismatches.Add(new TypeMismatch(variable.Key, variable.Type, ConfigTree.TypeOf(value)));
                }

                if (variable.Secret && found && !ConfigTree.IsPlaceholder(value)) secrets.Add(variable.Key);
            }

            return new EnvironmentStatus(environment, missing.Count == 0 && mismatches.Count == 0, null, missing, mismatches, secrets);
        }

        private static IReadOnlyList<string> Undeclared(AppSnapshot app, IReadOnlyList<VariableDefinition> declared)
        {
            if (declared.Count == 0 || app.Default?.Tree == null) return Array.Empty<string>();
            return app.Default.Leaves.Keys
                .Where(key => !declared.Any(d => key == d.Key || key.StartsWith(d.Key + ".", StringComparison.Ordinal)))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
        }
    }
}
