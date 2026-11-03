using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Domain.Configuration;

namespace ConfigServer.Domain.Security.ApiKeys
{
    /// <summary>Invalid API key request (maps to HTTP 400).</summary>
    public sealed class ApiKeyValidationException : Exception
    {
        public ApiKeyValidationException(string message) : base(message)
        {
        }
    }

    public sealed class ApiKeyCreateRequest
    {
        public string Name { get; init; }

        /// <summary>Null = all apps ("*"). Empty is invalid.</summary>
        public IReadOnlyList<string> Apps { get; init; }

        /// <summary>Null = all environments ("*"). Empty is invalid.</summary>
        public IReadOnlyList<string> Environments { get; init; }

        /// <summary>"read" (default) or "write".</summary>
        public string Permission { get; init; }

        public DateTimeOffset? ExpiresAt { get; init; }
    }

    /// <summary>A created or rotated key: metadata plus the full secret key, which is shown only this once.</summary>
    public sealed record ApiKeyIssued(ApiKey Key, string Secret);

    public sealed record ApiKeyAuthenticationResult(ApiKey Key, string Failure)
    {
        public bool Succeeded => Key != null;
    }

    /// <summary>Create, rotate, revoke and authenticate API keys on top of an <see cref="IApiKeyStore"/>.</summary>
    public sealed class ApiKeyManager
    {
        public const int MaxNameLength = 100;

        /// <summary>LastUsedAt is written at most once per interval per key.</summary>
        public static readonly TimeSpan UsageWriteInterval = TimeSpan.FromMinutes(1);

        private readonly IApiKeyStore _store;
        private readonly TimeProvider _time;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsageWrite = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        public ApiKeyManager(IApiKeyStore store, TimeProvider time = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _time = time ?? TimeProvider.System;
        }

        public DateTimeOffset Now => _time.GetUtcNow();

        public Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default) => _store.ListAsync(cancellationToken);

        public async Task<ApiKeyIssued> CreateAsync(ApiKeyCreateRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ApiKeyValidationException("Body must be a JSON object with a 'name'.");
            var now = Now;

            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name)) throw new ApiKeyValidationException("'name' is required.");
            if (name.Length > MaxNameLength) throw new ApiKeyValidationException($"'name' must be at most {MaxNameLength} characters.");

            var permission = ApiKeyPermission.Normalize(request.Permission)
                ?? throw new ApiKeyValidationException($"Unknown permission '{request.Permission}'. Use '{ApiKeyPermission.Read}' or '{ApiKeyPermission.Write}'.");
            if (request.ExpiresAt != null && request.ExpiresAt.Value <= now)
                throw new ApiKeyValidationException("'expiresAt' must be in the future.");

            var apps = NormalizeScope(request.Apps, "apps", ConfigNames.IsValidAppName, StringComparer.Ordinal);
            var environments = NormalizeScope(request.Environments, "environments", ConfigNames.IsValidEnvironmentName, StringComparer.OrdinalIgnoreCase);

            for (var attempt = 0; ; attempt++)
            {
                var prefix = ApiKeyToken.NewPrefix();
                if (await _store.FindByPrefixAsync(prefix, cancellationToken) != null)
                {
                    if (attempt < 5) continue;
                    throw new InvalidOperationException("Could not allocate a unique API key prefix.");
                }

                var secret = ApiKeyToken.Format(prefix, ApiKeyToken.NewSecret());
                var key = new ApiKey
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    Prefix = prefix,
                    Hash = ApiKeyToken.Hash(secret),
                    Apps = apps,
                    Environments = environments,
                    Permission = permission,
                    CreatedAt = now,
                    ExpiresAt = request.ExpiresAt?.ToUniversalTime()
                };
                await _store.AddAsync(key, cancellationToken);
                return new ApiKeyIssued(key, secret);
            }
        }

        /// <summary>New secret for the same id and metadata; the old secret stops working. Null for an unknown id.</summary>
        public async Task<ApiKeyIssued> RotateAsync(string id, CancellationToken cancellationToken = default)
        {
            string secret = null;
            var revoked = false;
            var updated = await _store.UpdateAsync(id, current =>
            {
                if (current.RevokedAt != null)
                {
                    revoked = true;
                    return current;
                }
                secret = ApiKeyToken.Format(current.Prefix, ApiKeyToken.NewSecret());
                return current with { Hash = ApiKeyToken.Hash(secret) };
            }, cancellationToken);

            if (updated == null) return null;
            if (revoked) throw new ApiKeyValidationException("A revoked API key cannot be rotated.");
            return new ApiKeyIssued(updated, secret);
        }

        /// <summary>Marks the key revoked (idempotent: the first revocation time is kept). Null for an unknown id.</summary>
        public Task<ApiKey> RevokeAsync(string id, CancellationToken cancellationToken = default)
        {
            var now = Now;
            return _store.UpdateAsync(id, current => current.RevokedAt != null ? current : current with { RevokedAt = now }, cancellationToken);
        }

        /// <summary>Verifies a presented key (shape, hash, revoked, expired) and records its use (throttled).</summary>
        public async Task<ApiKeyAuthenticationResult> AuthenticateAsync(string presented, CancellationToken cancellationToken = default)
        {
            if (!ApiKeyToken.TryParse(presented, out var prefix))
                return new ApiKeyAuthenticationResult(null, "Malformed API key.");

            var key = await _store.FindByPrefixAsync(prefix, cancellationToken);
            if (!ApiKeyToken.Verify(presented, key?.Hash))
                return new ApiKeyAuthenticationResult(null, "Invalid API key.");

            var now = Now;
            switch (key.StatusAt(now))
            {
                case ApiKeyStatus.Revoked:
                    return new ApiKeyAuthenticationResult(null, "API key revoked.");
                case ApiKeyStatus.Expired:
                    return new ApiKeyAuthenticationResult(null, "API key expired.");
            }

            await RecordUsageAsync(key, now, cancellationToken);
            return new ApiKeyAuthenticationResult(key, null);
        }

        private async Task RecordUsageAsync(ApiKey key, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (key.LastUsedAt is { } last && now - last < UsageWriteInterval) return;
            if (_lastUsageWrite.TryGetValue(key.Id, out var written) && now - written < UsageWriteInterval) return;
            _lastUsageWrite[key.Id] = now;
            try
            {
                await _store.UpdateAsync(key.Id, current => current with { LastUsedAt = now }, cancellationToken);
            }
            catch (IOException)
            {
                // Usage tracking is best effort; it must not fail the request.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string[] NormalizeScope(IReadOnlyList<string> values, string field, Func<string, bool> isValid, StringComparer comparer)
        {
            if (values == null) return new[] { ApiKey.Wildcard };
            if (values.Count == 0) throw new ApiKeyValidationException($"'{field}' must not be empty; omit it or use [\"*\"] for all.");

            var result = new List<string>();
            foreach (var raw in values)
            {
                var value = raw?.Trim();
                if (value == ApiKey.Wildcard) return new[] { ApiKey.Wildcard };
                if (string.IsNullOrEmpty(value) || !isValid(value)) throw new ApiKeyValidationException($"Invalid name '{raw}' in '{field}'.");
                if (!result.Contains(value, comparer)) result.Add(value);
            }
            return result.ToArray();
        }
    }
}
