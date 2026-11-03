using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Application.Api.Configuration;
using ConfigServer.Domain.Configuration;

namespace ConfigServer.Application.Api.Demo
{
    /// <summary>
    /// The config source of the sandbox: every call is forwarded to a <see cref="FileSystemConfigSource"/> over the calling
    /// visitor's own workspace (<see cref="DemoWorkspaceAccessor"/>), so everything built on <see cref="IConfigSource"/>,
    /// <see cref="IConfigStore"/> and <see cref="IConfigCatalog"/> is isolated per visitor. Writes are capped per workspace
    /// (<see cref="DemoOptions.MaxFilesPerWorkspace"/> / <see cref="DemoOptions.MaxBytesPerWorkspace"/>, else <see cref="DemoLimitException"/>).
    /// </summary>
    public sealed class DemoConfigSource : IConfigSource, IConfigStore, IConfigCatalog
    {
        private sealed class Entry
        {
            public Entry(string configPath)
            {
                Source = new FileSystemConfigSource(configPath);
                Path = configPath;
            }

            public FileSystemConfigSource Source { get; }
            public string Path { get; }
            public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);
        }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly DemoWorkspaceAccessor _accessor;
        private readonly DemoOptions _options;
        private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>();

        public DemoConfigSource(DemoWorkspaceAccessor accessor, DemoWorkspaces workspaces, DemoOptions options)
        {
            _accessor = accessor;
            _options = options;
            workspaces.Removed += id => _entries.TryRemove(id, out _);
        }

        private Entry Current()
        {
            var workspace = _accessor.Current;
            return _entries.GetOrAdd(workspace.Id, _ => new Entry(workspace.ConfigPath));
        }

        // ---- IConfigSource ----

        public Task<ConfigDocument> GetDefaultAsync(string application, CancellationToken cancellationToken = default)
            => Current().Source.GetDefaultAsync(application, cancellationToken);

        public Task<ConfigDocument> GetOverrideAsync(string application, string environment, CancellationToken cancellationToken = default)
            => Current().Source.GetOverrideAsync(application, environment, cancellationToken);

        // ---- IConfigCatalog / read side of IConfigStore ----

        public Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken cancellationToken = default)
            => Current().Source.ListAppsAsync(cancellationToken);

        public Task<IReadOnlyList<ConfigFileInfo>> ListFilesAsync(string application, CancellationToken cancellationToken = default)
            => Current().Source.ListFilesAsync(application, cancellationToken);

        public Task<string> ReadAsync(string application, string fileName, CancellationToken cancellationToken = default)
            => Current().Source.ReadAsync(application, fileName, cancellationToken);

        public Task<string> ReadReservedAsync(string application, string fileName, CancellationToken cancellationToken = default)
            => Current().Source.ReadReservedAsync(application, fileName, cancellationToken);

        // ---- writes (capped) ----

        public async Task<ConfigWriteResult> WriteAsync(string application, string fileName, string content, CancellationToken cancellationToken = default)
        {
            var entry = Current();
            await entry.Gate.WaitAsync(cancellationToken);
            try
            {
                if (ConfigNames.IsValidAppName(application) && ConfigNames.IsValidFileName(fileName))
                    EnforceLimits(entry, Path.Combine(entry.Path, application, fileName), content);
                return await entry.Source.WriteAsync(application, fileName, content, cancellationToken);
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        public async Task WriteReservedAsync(string application, string fileName, string content, CancellationToken cancellationToken = default)
        {
            var entry = Current();
            await entry.Gate.WaitAsync(cancellationToken);
            try
            {
                if (ConfigNames.IsKnownReservedFile(application, fileName))
                    EnforceLimits(entry, application == null ? Path.Combine(entry.Path, fileName) : Path.Combine(entry.Path, application, fileName), content);
                await entry.Source.WriteReservedAsync(application, fileName, content, cancellationToken);
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        public Task<ConfigDeleteResult> DeleteAsync(string application, string fileName, CancellationToken cancellationToken = default)
            => Current().Source.DeleteAsync(application, fileName, cancellationToken);

        private void EnforceLimits(Entry entry, string target, string content)
        {
            var existing = Directory.Exists(entry.Path)
                ? Directory.EnumerateFiles(entry.Path, "*", SearchOption.AllDirectories).Select(p => new FileInfo(p)).ToList()
                : new List<FileInfo>();
            var targetFull = Path.GetFullPath(target);
            var replaced = existing.FirstOrDefault(f => string.Equals(f.FullName, targetFull, System.StringComparison.OrdinalIgnoreCase));

            var newBytes = Utf8NoBom.GetByteCount(content ?? string.Empty);
            var files = existing.Count + (replaced == null ? 1 : 0);
            var bytes = existing.Sum(f => f.Length) - (replaced?.Length ?? 0) + newBytes;

            if (files > _options.MaxFilesPerWorkspace)
                throw new DemoLimitException($"Sandbox limit: a workspace can hold at most {_options.MaxFilesPerWorkspace} files. Delete some files first.");
            if (bytes > _options.MaxBytesPerWorkspace)
                throw new DemoLimitException($"Sandbox limit: a workspace can hold at most {_options.MaxBytesPerWorkspace / 1024} KB of configuration.");
        }
    }
}
