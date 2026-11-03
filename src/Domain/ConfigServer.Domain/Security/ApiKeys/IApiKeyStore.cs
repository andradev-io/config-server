using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ConfigServer.Domain.Security.ApiKeys
{
    /// <summary>
    /// Persistence of API keys (metadata + hash, never the secret). Registered in DI so other stores (database,
    /// secret manager) can replace the JSON file store. Must live outside the Git config repository.
    /// </summary>
    public interface IApiKeyStore
    {
        /// <summary>All keys, oldest first.</summary>
        Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default);

        /// <summary>The key with this id, or null.</summary>
        Task<ApiKey> GetAsync(string id, CancellationToken cancellationToken = default);

        /// <summary>The key with this public prefix, or null.</summary>
        Task<ApiKey> FindByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

        /// <summary>Adds a key; throws <see cref="InvalidOperationException"/> when the id or prefix already exists.</summary>
        Task AddAsync(ApiKey key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomic read-modify-write: <paramref name="update"/> receives the current record and returns the new one
        /// (returning the same instance writes nothing; id and prefix cannot change). Returns the stored record, or null for an unknown id.
        /// </summary>
        Task<ApiKey> UpdateAsync(string id, Func<ApiKey, ApiKey> update, CancellationToken cancellationToken = default);
    }
}
