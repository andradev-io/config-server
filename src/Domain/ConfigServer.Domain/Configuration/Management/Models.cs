using System;
using System.Collections.Generic;
using System.Linq;

namespace ConfigServer.Domain.Configuration.Management
{
    /// <summary>Allowed variable types.</summary>
    public static class VariableTypes
    {
        public static readonly IReadOnlyList<string> All = new[] { "string", "number", "boolean", "array", "object" };

        public static bool IsValid(string type) => type != null && All.Contains(type, StringComparer.Ordinal);
    }

    /// <summary>A declared environment of the registry (source root "_environments.json").</summary>
    public sealed class EnvironmentDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public int Order { get; set; }
    }

    /// <summary>A declared variable of an app ("{app}/_variables.json"). Key is a dotted path of the merged tree.</summary>
    public sealed class VariableDefinition
    {
        public string Key { get; set; }
        public string Type { get; set; }
        public bool Required { get; set; }
        public bool Secret { get; set; }
        public string Description { get; set; }

        /// <summary>Null = applies to all environments; otherwise the environments where the variable is required.</summary>
        public List<string> Environments { get; set; }

        public bool AppliesTo(string environment)
            => Environments == null || Environments.Any(e => string.Equals(e, environment, StringComparison.OrdinalIgnoreCase));

        public bool IsRequiredFor(string environment) => Required && AppliesTo(environment);
    }

    /// <summary>An environment as listed by the API: declared in the registry or inferred from override file names.</summary>
    public sealed record EnvironmentInfo(string Name, string Description, int Order, bool Declared, int AppCount);

    public sealed record SnapshotWarning(string App, string File, string Error);
}
