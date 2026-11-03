using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Configuration.Formats;

namespace ConfigServer.Application.Api.Configuration
{
    /// <summary>
    /// Filesystem <see cref="IConfigSource"/> and <see cref="IConfigStore"/>: {root}/{app}/default.* and {root}/{app}/{env}.*
    /// (.json, .yaml, .yml, .env). Replace the source with the Git-backed one via
    /// <see cref="ConfigSourceServiceCollectionExtensions.AddConfigSource"/>.
    /// </summary>
    public class FileSystemConfigSource : IConfigSource, IConfigStore, IConfigCatalog
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly string _root;
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);

        public FileSystemConfigSource(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Config path is required.", nameof(root));
            _root = Path.GetFullPath(root);
        }

        // ---- IConfigSource ----

        public Task<ConfigDocument> GetDefaultAsync(string application, CancellationToken cancellationToken = default)
            => ReadDocumentAsync(application, "default", cancellationToken);

        public Task<ConfigDocument> GetOverrideAsync(string application, string environment, CancellationToken cancellationToken = default)
            => ReadDocumentAsync(application, environment, cancellationToken);

        private async Task<ConfigDocument> ReadDocumentAsync(string application, string name, CancellationToken cancellationToken)
        {
            // Reserved files ("_*") are never environment overrides.
            if (ConfigNames.IsReservedFileName(name)) return null;
            foreach (var extension in ConfigFormatExtensions.LookupExtensions)
            {
                var path = ResolveInsideRoot(application, name + extension);
                if (!File.Exists(path)) continue;
                var content = await File.ReadAllTextAsync(path, cancellationToken);
                return new ConfigDocument(content, ConfigFormatExtensions.FromExtension(extension).Value);
            }
            return null;
        }

        // ---- IConfigStore ----

        public Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> apps = Directory.Exists(_root)
                ? Directory.EnumerateDirectories(_root)
                    .Select(Path.GetFileName)
                    .Where(ConfigNames.IsValidAppName)
                    .Where(name => EnumerateConfigFiles(Path.Combine(_root, name)).Any())
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();
            return Task.FromResult(apps);
        }

        public Task<IReadOnlyList<ConfigFileInfo>> ListFilesAsync(string application, CancellationToken cancellationToken = default)
        {
            RequireValidApp(application);
            var directory = ResolveInsideRoot(application, null);
            if (!Directory.Exists(directory)) return Task.FromResult<IReadOnlyList<ConfigFileInfo>>(null);

            IReadOnlyList<ConfigFileInfo> files = EnumerateConfigFiles(directory)
                .Select(path => new FileInfo(path))
                .Select(info => new ConfigFileInfo(info.Name, ConfigFormatExtensions.FromFileName(info.Name).Value, info.Length))
                .OrderBy(file => ConfigNames.IsDefault(file.Name) ? 0 : 1)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult(files);
        }

        public async Task<string> ReadAsync(string application, string fileName, CancellationToken cancellationToken = default)
        {
            var path = ResolveFile(application, fileName);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
        }

        public async Task<ConfigWriteResult> WriteAsync(string application, string fileName, string content, CancellationToken cancellationToken = default)
        {
            var path = ResolveFile(application, fileName);
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                var logical = ConfigNames.LogicalName(fileName);
                foreach (var extension in ConfigFormatExtensions.LookupExtensions)
                {
                    var sibling = logical + extension;
                    if (string.Equals(sibling, fileName, StringComparison.Ordinal)) continue;
                    if (File.Exists(ResolveInsideRoot(application, sibling))) return ConfigWriteResult.Conflict;
                }

                var existed = File.Exists(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var normalized = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
                await File.WriteAllTextAsync(path, normalized, Utf8NoBom, cancellationToken);
                return existed ? ConfigWriteResult.Updated : ConfigWriteResult.Created;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public async Task<string> ReadReservedAsync(string application, string fileName, CancellationToken cancellationToken = default)
        {
            var path = ResolveReserved(application, fileName);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
        }

        public async Task WriteReservedAsync(string application, string fileName, string content, CancellationToken cancellationToken = default)
        {
            var path = ResolveReserved(application, fileName);
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var normalized = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
                await File.WriteAllTextAsync(path, normalized, Utf8NoBom, cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public async Task<ConfigDeleteResult> DeleteAsync(string application, string fileName, CancellationToken cancellationToken = default)
        {
            var path = ResolveFile(application, fileName);
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(path)) return ConfigDeleteResult.NotFound;
                if (ConfigNames.IsDefault(fileName) &&
                    EnumerateConfigFiles(Path.GetDirectoryName(path)).Any(p => !ConfigNames.IsDefault(Path.GetFileName(p))))
                    return ConfigDeleteResult.Conflict;

                File.Delete(path);
                return ConfigDeleteResult.Deleted;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        // ---- helpers ----

        private static IEnumerable<string> EnumerateConfigFiles(string directory)
            => Directory.EnumerateFiles(directory).Where(p => ConfigNames.IsValidFileName(Path.GetFileName(p)));

        private static void RequireValidApp(string application)
        {
            if (!ConfigNames.IsValidAppName(application)) throw new ArgumentException("Invalid application name.", nameof(application));
        }

        private string ResolveFile(string application, string fileName)
        {
            RequireValidApp(application);
            if (!ConfigNames.IsValidFileName(fileName)) throw new ArgumentException("Invalid file name.", nameof(fileName));
            return ResolveInsideRoot(application, fileName);
        }

        private string ResolveReserved(string application, string fileName)
        {
            if (!ConfigNames.IsKnownReservedFile(application, fileName)) throw new ArgumentException("Invalid reserved file.", nameof(fileName));
            return application == null ? Path.Combine(_root, fileName) : ResolveInsideRoot(application, fileName);
        }

        /// <summary>Resolves {root}/{application}[/{file}] and verifies the full path stays inside the root.</summary>
        private string ResolveInsideRoot(string application, string file)
        {
            if (string.IsNullOrWhiteSpace(application) || (file != null && string.IsNullOrWhiteSpace(file)))
                throw new ArgumentException("Name is required.");

            var path = Path.GetFullPath(file == null ? Path.Combine(_root, application) : Path.Combine(_root, application, file));
            var rootWithSeparator = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
                (file != null && !path.StartsWith(Path.GetFullPath(Path.Combine(_root, application)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Name escapes the config root.");
            return path;
        }
    }
}
