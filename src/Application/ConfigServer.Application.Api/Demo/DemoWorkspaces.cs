using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ConfigServer.Application.Api.Demo
{
    /// <summary>A visitor's workspace: <c>{root}/config</c> is their config source, <c>{root}/state</c> holds their API keys.</summary>
    public sealed record DemoWorkspace(string Id, string Root)
    {
        public string ConfigPath => Path.Combine(Root, DemoWorkspaces.ConfigDirectory);

        public string StatePath => Path.Combine(Root, DemoWorkspaces.StateDirectory);
    }

    /// <summary>The sandbox is full: no new workspace can be created right now (HTTP 503).</summary>
    public sealed class DemoCapacityException : Exception
    {
        public DemoCapacityException(string message) : base(message)
        {
        }
    }

    /// <summary>A per-workspace sandbox limit was hit (HTTP 413).</summary>
    public sealed class DemoLimitException : Exception
    {
        public DemoLimitException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Creates, tracks and deletes the visitors' workspaces under <see cref="DemoOptions.WorkspacesPath"/>. A workspace is
    /// keyed by the first 16 hex characters of sha256(token sub), seeded from the read-only template on first use (copied to a
    /// temporary directory, then renamed, so it appears atomically) and deleted once idle longer than the TTL.
    /// </summary>
    public sealed class DemoWorkspaces
    {
        public const string ConfigDirectory = "config";
        public const string StateDirectory = "state";
        private const string MarkerFile = ".last-access";
        private const string TempMarker = ".tmp-";
        private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(30);

        private readonly DemoOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<DemoWorkspaces> _logger;
        private readonly object _gate = new object();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _touched = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        public DemoWorkspaces(DemoOptions options, TimeProvider time, ILogger<DemoWorkspaces> logger)
        {
            _options = options;
            _time = time;
            _logger = logger;
            Root = DemoOptions.Full(options.WorkspacesPath);
            Template = DemoOptions.Full(options.TemplatePath);
        }

        public string Root { get; }

        public string Template { get; }

        /// <summary>Raised (with the workspace id) after a workspace was deleted, so per-workspace caches can drop it.</summary>
        public event Action<string> Removed;

        /// <summary>The workspace id of a token subject: first 16 hex characters of sha256(sub).</summary>
        public static string IdFor(string subject)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(subject ?? string.Empty));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        public static bool IsWorkspaceId(string name)
            => name != null && name.Length == 16 && name.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        public DemoWorkspace Get(string id) => new DemoWorkspace(id, Path.Combine(Root, id));

        /// <summary>
        /// The workspace for <paramref name="id"/>, created from the template when missing and marked as used.
        /// Throws <see cref="DemoCapacityException"/> when a new one is needed but <see cref="DemoOptions.MaxWorkspaces"/> is reached.
        /// </summary>
        public DemoWorkspace GetOrCreate(string id)
        {
            var workspace = Get(id);
            if (!Directory.Exists(workspace.Root))
            {
                lock (_gate)
                {
                    if (!Directory.Exists(workspace.Root)) Create(workspace);
                }
            }
            Touch(workspace);
            return workspace;
        }

        public IReadOnlyList<string> ListIds()
            => Directory.Exists(Root)
                ? Directory.EnumerateDirectories(Root).Select(Path.GetFileName).Where(IsWorkspaceId).OrderBy(n => n, StringComparer.Ordinal).ToList()
                : new List<string>();

        public int Count => ListIds().Count;

        /// <summary>Deletes workspaces idle longer than the TTL and stale temporary directories. Returns how many workspaces were deleted.</summary>
        public int Cleanup()
        {
            if (!Directory.Exists(Root)) return 0;
            var now = _time.GetUtcNow();
            var removed = 0;

            foreach (var directory in Directory.EnumerateDirectories(Root).ToList())
            {
                var name = Path.GetFileName(directory);
                if (name.Contains(TempMarker, StringComparison.Ordinal))
                {
                    if (now - new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero) > TimeSpan.FromHours(1)) TryDelete(directory);
                    continue;
                }
                if (!IsWorkspaceId(name)) continue;

                if (now - LastAccess(directory) <= _options.IdleTtl) continue;
                if (!TryDelete(directory)) continue;
                _touched.TryRemove(name, out _);
                removed++;
                _logger.LogInformation("Demo workspace {Workspace} deleted (idle for more than {Minutes} minutes).", name, _options.IdleTtlMinutes);
                Removed?.Invoke(name);
            }
            return removed;
        }

        private void Create(DemoWorkspace workspace)
        {
            var existing = ListIds().Count;
            if (existing >= _options.MaxWorkspaces)
                throw new DemoCapacityException("The sandbox is full right now: too many visitors are using it. Please try again in a few minutes.");

            Directory.CreateDirectory(Root);
            var temp = Path.Combine(Root, workspace.Id + TempMarker + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDirectory(Template, Path.Combine(temp, ConfigDirectory));
                Directory.CreateDirectory(Path.Combine(temp, StateDirectory));
                File.WriteAllText(Path.Combine(temp, StateDirectory, MarkerFile), string.Empty);
                Directory.Move(temp, workspace.Root);
                _logger.LogInformation("Demo workspace {Workspace} created ({Count}/{Max}).", workspace.Id, existing + 1, _options.MaxWorkspaces);
            }
            catch (IOException) when (Directory.Exists(workspace.Root))
            {
                // Another process won the race: use its workspace.
            }
            finally
            {
                if (Directory.Exists(temp)) TryDelete(temp);
            }
        }

        private void Touch(DemoWorkspace workspace)
        {
            var now = _time.GetUtcNow();
            if (_touched.TryGetValue(workspace.Id, out var last) && now - last < TouchInterval) return;
            _touched[workspace.Id] = now;
            try
            {
                var marker = Path.Combine(workspace.StatePath, MarkerFile);
                Directory.CreateDirectory(workspace.StatePath);
                if (!File.Exists(marker)) File.WriteAllText(marker, string.Empty);
                File.SetLastWriteTimeUtc(marker, now.UtcDateTime);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not mark demo workspace {Workspace} as used.", workspace.Id);
            }
        }

        private static DateTimeOffset LastAccess(string workspaceDirectory)
        {
            var marker = Path.Combine(workspaceDirectory, StateDirectory, MarkerFile);
            var time = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : Directory.GetLastWriteTimeUtc(workspaceDirectory);
            return new DateTimeOffset(time, TimeSpan.Zero);
        }

        private bool TryDelete(string directory)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete demo directory {Directory}.", Path.GetFileName(directory));
                return false;
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            foreach (var directory in Directory.EnumerateDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
