using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ConfigServer.Domain.Configuration
{
    /// <summary>
    /// Read-side enumeration of a configuration source (apps, files, raw text, reserved "_*" files).
    /// Implemented by every source that can list its files (filesystem and Git); writes live in <see cref="IConfigStore"/>.
    /// Implementations return raw text (no secret resolution) and throw <see cref="System.ArgumentException"/> for invalid names.
    /// </summary>
    public interface IConfigCatalog
    {
        /// <summary>Applications (sub-directories) that contain config files.</summary>
        Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken cancellationToken = default);

        /// <summary>Config files of an application (default first, then by name; reserved "_*" files excluded), or null when the application does not exist.</summary>
        Task<IReadOnlyList<ConfigFileInfo>> ListFilesAsync(string application, CancellationToken cancellationToken = default);

        /// <summary>Raw text of a config file, or null when missing.</summary>
        Task<string> ReadAsync(string application, string fileName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Raw text of a reserved file: "_environments.json" (application null = source root) or "{application}/_variables.json".
        /// Null when missing.
        /// </summary>
        Task<string> ReadReservedAsync(string application, string fileName, CancellationToken cancellationToken = default);
    }
}
