using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ConfigServer.Domain.Security.ApiKeys
{
    /// <summary>
    /// API keys in one JSON file (<c>{"version":1,"keys":[...]}</c>, hashes only). Writes are atomic (temp file + rename)
    /// and serialized by an in-process lock; the file is re-read when its timestamp or size changes. One server process per file.
    /// On Unix the file is created with mode 0600.
    /// </summary>
    public sealed class JsonFileApiKeyStore : IApiKeyStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private List<ApiKey> _cache;
        private DateTime _cacheWriteTime;
        private long _cacheLength = -1;

        public JsonFileApiKeyStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("API key store path is required.", nameof(path));
            FilePath = Path.GetFullPath(path);
        }

        public string FilePath { get; }

        public Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default)
            => ReadAsync<IReadOnlyList<ApiKey>>(keys => keys.ToList(), cancellationToken);

        public Task<ApiKey> GetAsync(string id, CancellationToken cancellationToken = default)
            => ReadAsync(keys => ApiKeyCollection.ById(keys, id), cancellationToken);

        public Task<ApiKey> FindByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
            => ReadAsync(keys => ApiKeyCollection.ByPrefix(keys, prefix), cancellationToken);

        public async Task AddAsync(ApiKey key, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var keys = (await LoadAsync(cancellationToken)).ToList();
                ApiKeyCollection.Add(keys, key);
                await SaveAsync(keys, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<ApiKey> UpdateAsync(string id, Func<ApiKey, ApiKey> update, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var keys = (await LoadAsync(cancellationToken)).ToList();
                var result = ApiKeyCollection.Update(keys, id, update, out var changed);
                if (changed) await SaveAsync(keys, cancellationToken);
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<T> ReadAsync<T>(Func<List<ApiKey>, T> read, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return read(await LoadAsync(cancellationToken));
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Caller holds the gate. Returns the cached list (do not mutate it).</summary>
        private async Task<List<ApiKey>> LoadAsync(CancellationToken cancellationToken)
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                _cache = new List<ApiKey>();
                _cacheLength = -1;
                return _cache;
            }
            if (_cache != null && info.LastWriteTimeUtc == _cacheWriteTime && info.Length == _cacheLength) return _cache;

            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            StoreDocument document;
            try
            {
                document = await JsonSerializer.DeserializeAsync<StoreDocument>(stream, JsonOptions, cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"The API key store '{FilePath}' is not valid JSON: {ex.Message}", ex);
            }

            _cache = (document?.Keys ?? new List<ApiKey>()).Where(k => k != null).ToList();
            _cacheWriteTime = info.LastWriteTimeUtc;
            _cacheLength = info.Length;
            return _cache;
        }

        /// <summary>Caller holds the gate.</summary>
        private async Task SaveAsync(List<ApiKey> keys, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, "." + Path.GetFileName(FilePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                await using (var stream = new FileStream(temp, options))
                {
                    await JsonSerializer.SerializeAsync(stream, new StoreDocument { Version = 1, Keys = keys }, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                File.Move(temp, FilePath, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }

            var info = new FileInfo(FilePath);
            _cache = keys;
            _cacheWriteTime = info.LastWriteTimeUtc;
            _cacheLength = info.Length;
        }

        private sealed class StoreDocument
        {
            public int Version { get; set; }
            public List<ApiKey> Keys { get; set; }
        }
    }
}
