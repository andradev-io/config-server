using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Domain.Configuration.Formats;

namespace ConfigServer.Domain.Configuration
{
    /// <summary>A raw configuration file: its text and the format implied by its extension.</summary>
    public sealed record ConfigDocument(string Content, ConfigFormat Format);

    /// <summary>
    /// Where configuration files come from (e.g. a Git repo). Returns the raw file, or null when it does not exist.
    /// For each logical name (default / environment) the lookup order is .json, .yaml, .yml, .env (first found wins).
    /// </summary>
    public interface IConfigSource
    {
        /// <summary>Default config for an application (default.*).</summary>
        Task<ConfigDocument> GetDefaultAsync(string application, CancellationToken cancellationToken = default);

        /// <summary>Partial override for an application in one environment ({environment}.*).</summary>
        Task<ConfigDocument> GetOverrideAsync(string application, string environment, CancellationToken cancellationToken = default);
    }
}
