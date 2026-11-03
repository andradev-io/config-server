using System;
using System.Text.RegularExpressions;

namespace ConfigServer.Domain.Configuration
{
    /// <summary>Validation rules for application and file names used by the files-management API.</summary>
    public static class ConfigNames
    {
        private static readonly Regex FileNameRegex = new Regex(
            @"^(default|[A-Za-z0-9][A-Za-z0-9_-]*)\.(json|ya?ml|env)\z", RegexOptions.CultureInvariant);

        private static readonly Regex AppNameRegex = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant);

        public const string EnvironmentsFileName = "_environments.json";
        public const string VariablesFileName = "_variables.json";

        private static readonly Regex EnvironmentNameRegex = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant);

        /// <summary>Reserved files start with "_" and are never environment overrides.</summary>
        public static bool IsReservedFileName(string fileName)
            => !string.IsNullOrEmpty(fileName) && fileName[0] == '_';

        /// <summary>Environment name rule: letters, digits, '_' and '-', starting with a letter or digit, and not "default".</summary>
        public static bool IsValidEnvironmentName(string name)
            => !string.IsNullOrEmpty(name) && EnvironmentNameRegex.IsMatch(name)
               && !string.Equals(name, "default", StringComparison.OrdinalIgnoreCase);

        /// <summary>True for the reserved files the server manages: root "_environments.json" or per-app "_variables.json".</summary>
        public static bool IsKnownReservedFile(string application, string fileName)
            => application == null
                ? string.Equals(fileName, EnvironmentsFileName, StringComparison.Ordinal)
                : string.Equals(fileName, VariablesFileName, StringComparison.Ordinal) && IsValidAppName(application);

        public static bool IsValidAppName(string name)
            => !string.IsNullOrEmpty(name) && AppNameRegex.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

        public static bool IsValidFileName(string fileName)
            => !string.IsNullOrEmpty(fileName) && FileNameRegex.IsMatch(fileName);

        /// <summary>File name without its extension ("dev.yaml" -> "dev").</summary>
        public static string LogicalName(string fileName)
        {
            var dot = fileName.LastIndexOf('.');
            return dot < 0 ? fileName : fileName.Substring(0, dot);
        }

        public static bool IsDefault(string fileName)
            => string.Equals(LogicalName(fileName), "default", StringComparison.Ordinal);
    }
}
