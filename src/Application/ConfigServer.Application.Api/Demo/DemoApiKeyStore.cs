using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Domain.Security.ApiKeys;

namespace ConfigServer.Application.Api.Demo
{
    /// <summary>
    /// Local API keys of the sandbox: each workspace has its own JSON file (<c>{workspace}/state/api-keys.json</c>) and a key
    /// authenticates into the workspace it was created in. Authentication only knows the key's public prefix, so a
    /// prefix to workspace index (filled on creation, rebuilt lazily by scanning the workspaces, at most every
    /// <see cref="RescanInterval"/>) finds the owner and binds the request to that workspace.
    /// </summary>
    public sealed class DemoApiKeyStore : IApiKeyStore
    {
        public static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(10);

        private readonly DemoWorkspaceAccessor _accessor;
        private readonly DemoWorkspaces _workspaces;
        private readonly DemoOptions _options;
        private readonly TimeProvider _time;
        private readonly ConcurrentDictionary<string, JsonFileApiKeyStore> _stores = new ConcurrentDictionary<string, JsonFileApiKeyStore>();
        private readonly ConcurrentDictionary<string, string> _index = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _scanGate = new SemaphoreSlim(1, 1);
        private DateTimeOffset _lastScan = DateTimeOffset.MinValue;

        public DemoApiKeyStore(DemoWorkspaceAccessor accessor, DemoWorkspaces workspaces, DemoOptions options, TimeProvider time)
        {
            _accessor = accessor;
            _workspaces = workspaces;
            _options = options;
            _time = time;
            workspaces.Removed += id =>
            {
                _stores.TryRemove(id, out _);
                foreach (var pair in _index.Where(p => p.Value == id).ToList()) _index.TryRemove(pair.Key, out _);
            };
        }

        private JsonFileApiKeyStore Store(string workspaceId)
            => _stores.GetOrAdd(workspaceId, id => new JsonFileApiKeyStore(Path.Combine(_workspaces.Get(id).StatePath, "api-keys.json")));

        private JsonFileApiKeyStore Current() => Store(_accessor.Current.Id);

        public Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default)
            => Current().ListAsync(cancellationToken);

        public Task<ApiKey> GetAsync(string id, CancellationToken cancellationToken = default)
            => Current().GetAsync(id, cancellationToken);

        public async Task AddAsync(ApiKey key, CancellationToken cancellationToken = default)
        {
            var workspaceId = _accessor.Current.Id;
            var store = Store(workspaceId);
            if ((await store.ListAsync(cancellationToken)).Count >= _options.MaxApiKeysPerWorkspace)
                throw new DemoLimitException($"Sandbox limit: a workspace can hold at most {_options.MaxApiKeysPerWorkspace} API keys. Revoke or delete some first.");
            await store.AddAsync(key, cancellationToken);
            _index[key.Prefix] = workspaceId;
        }

        public Task<ApiKey> UpdateAsync(string id, Func<ApiKey, ApiKey> update, CancellationToken cancellationToken = default)
            => Current().UpdateAsync(id, update, cancellationToken);

        public async Task<ApiKey> FindByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(prefix)) return null;

            if (!_index.TryGetValue(prefix, out var workspaceId))
            {
                await RescanAsync(cancellationToken);
                if (!_index.TryGetValue(prefix, out workspaceId)) return null;
            }

            var key = await Store(workspaceId).FindByPrefixAsync(prefix, cancellationToken);
            if (key == null)
            {
                _index.TryRemove(prefix, out _);
                return null;
            }

            // Authenticating with a key: the request works on the workspace the key was created in.
            _accessor.BindIfUnbound(workspaceId);
            return key;
        }

        /// <summary>Reads every workspace's key file into the index (throttled: unknown prefixes cannot make it scan on every request).</summary>
        private async Task RescanAsync(CancellationToken cancellationToken)
        {
            await _scanGate.WaitAsync(cancellationToken);
            try
            {
                var now = _time.GetUtcNow();
                if (now - _lastScan < RescanInterval) return;
                _lastScan = now;

                foreach (var id in _workspaces.ListIds())
                {
                    if (!File.Exists(Path.Combine(_workspaces.Get(id).StatePath, "api-keys.json"))) continue;
                    foreach (var key in await Store(id).ListAsync(cancellationToken))
                        _index.TryAdd(key.Prefix, id);
                }
            }
            finally
            {
                _scanGate.Release();
            }
        }
    }
}
