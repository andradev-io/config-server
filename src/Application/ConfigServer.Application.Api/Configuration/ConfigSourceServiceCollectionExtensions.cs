using ConfigServer.Domain.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConfigServer.Application.Api.Configuration
{
    public static class ConfigSourceServiceCollectionExtensions
    {
        public const string PathKey = "ConfigSource:Path";
        public const string AllowWriteKey = "ConfigSource:AllowWrite";

        /// <summary>
        /// Whether config files may be written: "ConfigSource:AllowWrite", or always in the demo sandbox
        /// ("Demo:Enabled": visitors edit their own disposable workspace).
        /// </summary>
        public static bool IsWriteAllowed(this IConfiguration configuration)
            => configuration.GetValue<bool>(AllowWriteKey) || configuration.GetValue<bool>("Demo:Enabled");

        /// <summary>
        /// Single registration point for <see cref="IConfigSource"/>. Currently the filesystem placeholder
        /// (path from "ConfigSource:Path"); it also implements <see cref="IConfigStore"/> for the files API.
        /// The Git source does not register an <see cref="IConfigStore"/> (writes are not supported there yet).
        /// </summary>
        public static IServiceCollection AddConfigSource(this IServiceCollection services, IConfiguration configuration)
        {
            var path = configuration[PathKey];
            services.TryAddSingleton(_ => new FileSystemConfigSource(path));
            services.TryAddSingleton<IConfigSource>(sp => sp.GetRequiredService<FileSystemConfigSource>());
            services.TryAddSingleton<IConfigStore>(sp => sp.GetRequiredService<FileSystemConfigSource>());
            services.TryAddSingleton<IConfigCatalog>(sp => sp.GetRequiredService<FileSystemConfigSource>());
            return services;
        }
    }
}
