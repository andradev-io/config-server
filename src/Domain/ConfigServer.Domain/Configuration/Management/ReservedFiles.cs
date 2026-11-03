using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using ConfigServer.Domain.Configuration.Formats;

namespace ConfigServer.Domain.Configuration.Management
{
    /// <summary>Parsing, validation and serialization of the reserved files "_environments.json" and "_variables.json".</summary>
    public static class ReservedFiles
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private sealed class EnvironmentsFile { public List<EnvironmentDefinition> Environments { get; set; } }
        private sealed class VariablesFile { public List<VariableDefinition> Variables { get; set; } }

        public static List<EnvironmentDefinition> ParseEnvironments(string text)
            => Parse<EnvironmentsFile>(text, ConfigNames.EnvironmentsFileName)?.Environments?.Where(e => e != null).ToList()
               ?? new List<EnvironmentDefinition>();

        public static string SerializeEnvironments(IEnumerable<EnvironmentDefinition> environments)
            => JsonSerializer.Serialize(new EnvironmentsFile { Environments = environments.ToList() }, Options) + "\n";

        public static List<VariableDefinition> ParseVariables(string text)
            => Parse<VariablesFile>(text, ConfigNames.VariablesFileName)?.Variables?.Where(v => v != null).ToList()
               ?? new List<VariableDefinition>();

        public static string SerializeVariables(IEnumerable<VariableDefinition> variables)
            => JsonSerializer.Serialize(new VariablesFile { Variables = variables.ToList() }, Options) + "\n";

        private static T Parse<T>(string text, string fileName) where T : class
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(text, Options);
            }
            catch (JsonException ex)
            {
                int? line = ex.LineNumber.HasValue ? (int?)(ex.LineNumber.Value + 1) : null;
                int? column = ex.BytePositionInLine.HasValue ? (int?)(ex.BytePositionInLine.Value + 1) : null;
                throw new ConfigFormatException(fileName + " is invalid: " + ex.Message, line, column, ex);
            }
        }

        /// <summary>
        /// Validates and normalizes variable declarations (types lower-cased, keys trimmed, an empty environment list means all).
        /// Returns an error message, or null when valid.
        /// </summary>
        public static string ValidateVariables(IList<VariableDefinition> variables, IEnumerable<string> knownEnvironments)
        {
            var known = new HashSet<string>(knownEnvironments ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < variables.Count; i++)
            {
                var variable = variables[i];
                if (variable == null) return $"variables[{i}] must be an object.";
                variable.Key = variable.Key?.Trim();
                if (string.IsNullOrEmpty(variable.Key)) return $"variables[{i}]: key is required.";
                if (variable.Key.StartsWith(".", StringComparison.Ordinal) || variable.Key.EndsWith(".", StringComparison.Ordinal) || variable.Key.Contains("..", StringComparison.Ordinal))
                    return $"variables[{i}]: key '{variable.Key}' is not a valid dotted path.";
                if (!seen.Add(variable.Key)) return $"Duplicate variable key '{variable.Key}'.";

                variable.Type = variable.Type?.Trim().ToLowerInvariant();
                if (!VariableTypes.IsValid(variable.Type))
                    return $"variables[{i}] ('{variable.Key}'): type must be one of {string.Join(", ", VariableTypes.All)}.";

                variable.Description ??= string.Empty;
                if (variable.Environments != null && variable.Environments.Count == 0) variable.Environments = null;
                if (variable.Environments == null) continue;

                foreach (var environment in variable.Environments)
                    if (string.IsNullOrWhiteSpace(environment) || !known.Contains(environment))
                        return $"variables[{i}] ('{variable.Key}'): unknown environment '{environment}'.";
            }
            return null;
        }
    }
}
