using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Configuration.Formats;
using LibGit2Sharp;

namespace ConfigServer.Domain.Git
{
    /// <summary>
    /// Reads {application}/default.* and {application}/{environment}.* (.json, .yaml, .yml, .env) from a Git repository
    /// that is cloned locally and refreshed (fetch + hard reset) at most every RefreshInterval.
    /// </summary>
    public sealed class GitConfigSource : IConfigSource, IConfigCatalog, IDisposable
    {
        private const string RemoteName = "origin";

        private readonly GitConfigSourceOptions _options;
        private readonly ISecretResolver _secretResolver;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private long _lastSyncTimestamp;
        private bool _synced;

        public GitConfigSource(GitConfigSourceOptions options, ISecretResolver secretResolver = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.RepositoryUrl)) throw new ArgumentException("RepositoryUrl is required.", nameof(options));
            if (string.IsNullOrWhiteSpace(options.LocalPath)) throw new ArgumentException("LocalPath is required.", nameof(options));
            if (string.IsNullOrWhiteSpace(options.Branch)) throw new ArgumentException("Branch is required.", nameof(options));
            _secretResolver = secretResolver ?? new PassThroughSecretResolver();
        }

        public Task<ConfigDocument> GetDefaultAsync(string application, CancellationToken cancellationToken = default)
        {
            ValidateName(application, nameof(application));
            return ReadDocumentAsync(application, "default", cancellationToken);
        }

        public Task<ConfigDocument> GetOverrideAsync(string application, string environment, CancellationToken cancellationToken = default)
        {
            ValidateName(application, nameof(application));
            ValidateName(environment, nameof(environment));
            return ReadDocumentAsync(application, environment, cancellationToken);
        }

        private async Task<ConfigDocument> ReadDocumentAsync(string application, string file, CancellationToken cancellationToken)
        {
            // Reserved files ("_*") are never environment overrides.
            if (ConfigNames.IsReservedFileName(file)) return null;
            string content = null;
            ConfigFormat format = default;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureFresh();
                foreach (var extension in ConfigFormatExtensions.LookupExtensions)
                {
                    var path = Path.Combine(_options.LocalPath, application, file + extension);
                    if (!File.Exists(path)) continue;
                    content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    format = ConfigFormatExtensions.FromExtension(extension).Value;
                    break;
                }
            }
            finally
            {
                _gate.Release();
            }
            if (content == null) return null;
            var resolved = await _secretResolver.ResolveAsync(content, cancellationToken).ConfigureAwait(false);
            return new ConfigDocument(resolved, format);
        }

        // ---- IConfigCatalog (raw text, no secret resolution: used for structure scans and status checks) ----

        public Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken cancellationToken = default)
            => WithRepositoryAsync<IReadOnlyList<string>>(root => Directory.Exists(root)
                ? Directory.EnumerateDirectories(root)
                    .Select(Path.GetFileName)
                    .Where(ConfigNames.IsValidAppName)
                    .Where(name => EnumerateConfigFiles(Path.Combine(root, name)).Any())
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>(), cancellationToken);

        public Task<IReadOnlyList<ConfigFileInfo>> ListFilesAsync(string application, CancellationToken cancellationToken = default)
        {
            RequireValidApp(application);
            return WithRepositoryAsync<IReadOnlyList<ConfigFileInfo>>(root =>
            {
                var directory = Path.Combine(root, application);
                if (!Directory.Exists(directory)) return null;
                return EnumerateConfigFiles(directory)
                    .Select(path => new FileInfo(path))
                    .Select(info => new ConfigFileInfo(info.Name, ConfigFormatExtensions.FromFileName(info.Name).Value, info.Length))
                    .OrderBy(file => ConfigNames.IsDefault(file.Name) ? 0 : 1)
                    .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }, cancellationToken);
        }

        public Task<string> ReadAsync(string application, string fileName, CancellationToken cancellationToken = default)
        {
            RequireValidApp(application);
            if (!ConfigNames.IsValidFileName(fileName)) throw new ArgumentException("Invalid file name.", nameof(fileName));
            return WithRepositoryAsync(root => ReadTextOrNull(Path.Combine(root, application, fileName)), cancellationToken);
        }

        public Task<string> ReadReservedAsync(string application, string fileName, CancellationToken cancellationToken = default)
        {
            if (!ConfigNames.IsKnownReservedFile(application, fileName)) throw new ArgumentException("Invalid reserved file.", nameof(fileName));
            return WithRepositoryAsync(root => ReadTextOrNull(application == null
                ? Path.Combine(root, fileName)
                : Path.Combine(root, application, fileName)), cancellationToken);
        }

        private static string ReadTextOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

        private static IEnumerable<string> EnumerateConfigFiles(string directory)
            => Directory.EnumerateFiles(directory).Where(p => ConfigNames.IsValidFileName(Path.GetFileName(p)));

        private static void RequireValidApp(string application)
        {
            if (!ConfigNames.IsValidAppName(application)) throw new ArgumentException("Invalid application name.", nameof(application));
        }

        private async Task<T> WithRepositoryAsync<T>(Func<string, T> action, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureFresh();
                return action(_options.LocalPath);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void EnsureFresh()
        {
            if (_synced && Stopwatch.GetElapsedTime(_lastSyncTimestamp) < _options.RefreshInterval) return;

            if (Repository.IsValid(_options.LocalPath)) Refresh();
            else Clone();

            _lastSyncTimestamp = Stopwatch.GetTimestamp();
            _synced = true;
        }

        private void Clone()
        {
            Directory.CreateDirectory(_options.LocalPath);
            var cloneOptions = new CloneOptions { BranchName = _options.Branch };
            cloneOptions.FetchOptions.CredentialsProvider = CredentialsHandler;
            Repository.Clone(_options.RepositoryUrl, _options.LocalPath, cloneOptions);
        }

        private void Refresh()
        {
            using var repo = new Repository(_options.LocalPath);
            var refSpec = $"+refs/heads/{_options.Branch}:refs/remotes/{RemoteName}/{_options.Branch}";
            Commands.Fetch(repo, RemoteName, new[] { refSpec },
                new FetchOptions { CredentialsProvider = CredentialsHandler }, null);

            var tip = repo.Branches[$"{RemoteName}/{_options.Branch}"]?.Tip
                ?? throw new InvalidOperationException($"Remote branch '{_options.Branch}' not found.");
            repo.Reset(ResetMode.Hard, tip);
        }

        private Credentials CredentialsHandler(string url, string usernameFromUrl, SupportedCredentialTypes types)
        {
            if (string.IsNullOrEmpty(_options.Token)) return null;
            return new UsernamePasswordCredentials
            {
                Username = string.IsNullOrEmpty(_options.Username) ? (usernameFromUrl ?? "git") : _options.Username,
                Password = _options.Token
            };
        }

        private static void ValidateName(string name, string paramName)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", paramName);
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\'))
                throw new ArgumentException("Name must not contain '..', '/' or '\\'.", paramName);
        }

        public void Dispose() => _gate.Dispose();
    }
}
