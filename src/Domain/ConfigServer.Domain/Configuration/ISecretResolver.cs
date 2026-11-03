using System.Threading;
using System.Threading.Tasks;

namespace ConfigServer.Domain.Configuration
{
    /// <summary>
    /// Extension point to resolve secrets (e.g. SOPS-encrypted values, secret:// references) in raw config JSON.
    /// </summary>
    public interface ISecretResolver
    {
        Task<string> ResolveAsync(string json, CancellationToken cancellationToken);
    }

    /// <summary>Default resolver: returns the JSON unchanged.</summary>
    public sealed class PassThroughSecretResolver : ISecretResolver
    {
        public Task<string> ResolveAsync(string json, CancellationToken cancellationToken) => Task.FromResult(json);
    }
}
