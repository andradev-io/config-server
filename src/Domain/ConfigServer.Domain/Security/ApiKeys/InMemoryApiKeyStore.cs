using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConfigServer.Domain.Security.ApiKeys
{
    /// <summary>Process-local store (tests, throwaway setups). Keys are lost on restart.</summary>
    public sealed class InMemoryApiKeyStore : IApiKeyStore
    {
        private readonly object _gate = new object();
        private readonly List<ApiKey> _keys = new List<ApiKey>();

        public Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult<IReadOnlyList<ApiKey>>(_keys.ToList());
        }

        public Task<ApiKey> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(ApiKeyCollection.ById(_keys, id));
        }

        public Task<ApiKey> FindByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(ApiKeyCollection.ByPrefix(_keys, prefix));
        }

        public Task AddAsync(ApiKey key, CancellationToken cancellationToken = default)
        {
            lock (_gate) ApiKeyCollection.Add(_keys, key);
            return Task.CompletedTask;
        }

        public Task<ApiKey> UpdateAsync(string id, Func<ApiKey, ApiKey> update, CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(ApiKeyCollection.Update(_keys, id, update, out _));
        }
    }

    /// <summary>Shared list rules of the stores.</summary>
    internal static class ApiKeyCollection
    {
        public static ApiKey ById(List<ApiKey> keys, string id)
            => id == null ? null : keys.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));

        public static ApiKey ByPrefix(List<ApiKey> keys, string prefix)
            => prefix == null ? null : keys.FirstOrDefault(k => string.Equals(k.Prefix, prefix, StringComparison.Ordinal));

        public static void Add(List<ApiKey> keys, ApiKey key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (string.IsNullOrEmpty(key.Id) || string.IsNullOrEmpty(key.Prefix) || string.IsNullOrEmpty(key.Hash))
                throw new ArgumentException("API key id, prefix and hash are required.", nameof(key));
            if (ById(keys, key.Id) != null) throw new InvalidOperationException($"An API key with id '{key.Id}' already exists.");
            if (ByPrefix(keys, key.Prefix) != null) throw new InvalidOperationException($"An API key with prefix '{key.Prefix}' already exists.");
            keys.Add(key);
        }

        /// <summary>Applies <paramref name="update"/> in place; <paramref name="changed"/> is false when nothing was written.</summary>
        public static ApiKey Update(List<ApiKey> keys, string id, Func<ApiKey, ApiKey> update, out bool changed)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            changed = false;
            var index = keys.FindIndex(k => string.Equals(k.Id, id, StringComparison.Ordinal));
            if (index < 0) return null;

            var current = keys[index];
            var next = update(current);
            if (next == null || ReferenceEquals(next, current) || next == current) return current;
            if (!string.Equals(next.Id, current.Id, StringComparison.Ordinal) || !string.Equals(next.Prefix, current.Prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("The id and prefix of an API key cannot change.");

            keys[index] = next;
            changed = true;
            return next;
        }
    }
}
