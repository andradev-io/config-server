using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Domain.Configuration.Formats;

namespace ConfigServer.Domain.Configuration
{
    public sealed record ConfigFileInfo(string Name, ConfigFormat Format, long Size);

    public enum ConfigWriteResult
    {
        Created,
        Updated,
        /// <summary>Another file with the same logical name but a different extension already exists.</summary>
        Conflict
    }

    public enum ConfigDeleteResult
    {
        Deleted,
        NotFound,
        /// <summary>A default.* file cannot be deleted while other config files exist for the application.</summary>
        Conflict
    }

    /// <summary>
    /// Files-management contract for an editor UI. Callers must validate names with <see cref="ConfigNames"/> first;
    /// implementations throw <see cref="System.ArgumentException"/> for invalid names.
    /// </summary>
    public interface IConfigStore
    {
        /// <summary>Applications (sub-directories) that contain config files.</summary>
        Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken cancellationToken = default);

        /// <summary>Files of an application (default first, then by name), or null when the application does not exist.</summary>
        Task<IReadOnlyList<ConfigFileInfo>> ListFilesAsync(string application, CancellationToken cancellationToken = default);

        /// <summary>Raw file text, or null when missing.</summary>
        Task<string> ReadAsync(string application, string fileName, CancellationToken cancellationToken = default);

        /// <summary>Creates or overwrites a file (UTF-8 without BOM, LF line endings). Content is not validated here.</summary>
        Task<ConfigWriteResult> WriteAsync(string application, string fileName, string content, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates or overwrites a reserved file: "_environments.json" (application null = source root) or
        /// "{application}/_variables.json" (the application directory must exist). Same encoding rules as <see cref="WriteAsync"/>.
        /// </summary>
        Task WriteReservedAsync(string application, string fileName, string content, CancellationToken cancellationToken = default);

        Task<ConfigDeleteResult> DeleteAsync(string application, string fileName, CancellationToken cancellationToken = default);
    }
}
