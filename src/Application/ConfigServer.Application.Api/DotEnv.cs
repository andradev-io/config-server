using System;
using System.IO;

namespace ConfigServer.Application.Api
{
    /// <summary>
    /// Loads a <c>.env</c> file (searched upwards from the current directory) into process environment variables
    /// so local values and secrets can live in one git-ignored file. Real environment variables always win, and
    /// command-line arguments still override both. Skipped when ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT is Production.
    /// </summary>
    internal static class DotEnv
    {
        public static void Load(string? startDirectory = null)
        {
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                              ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            if (string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase)) return;

            var path = Find(startDirectory ?? Directory.GetCurrentDirectory());
            if (path is null) return;

            foreach (var line in File.ReadAllLines(path))
            {
                if (!TryParse(line, out var name, out var value)) continue;
                if (Environment.GetEnvironmentVariable(name) is null) Environment.SetEnvironmentVariable(name, value);
            }
        }

        internal static string? Find(string directory)
        {
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, ".env");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        internal static bool TryParse(string line, out string name, out string value)
        {
            name = value = string.Empty;
            var text = line.Trim();
            if (text.Length == 0 || text[0] == '#') return false;
            if (text.StartsWith("export ", StringComparison.Ordinal)) text = text[7..].TrimStart();

            var eq = text.IndexOf('=');
            if (eq <= 0) return false;

            name = text[..eq].Trim();
            value = text[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            return name.Length > 0;
        }
    }
}
