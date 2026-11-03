using System;
using ConfigServer.Domain.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConfigServer.Domain.Git
{
    public static class GitConfigSourceServiceCollectionExtensions
    {
        public static IServiceCollection AddGitConfigSource(this IServiceCollection services, Action<GitConfigSourceOptions> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var options = new GitConfigSourceOptions();
            configure(options);

            services.TryAddSingleton<ISecretResolver, PassThroughSecretResolver>();
            services.AddSingleton(options);
            services.AddSingleton(sp =>
                new GitConfigSource(sp.GetRequiredService<GitConfigSourceOptions>(), sp.GetRequiredService<ISecretResolver>()));
            services.AddSingleton<IConfigSource>(sp => sp.GetRequiredService<GitConfigSource>());
            services.AddSingleton<IConfigCatalog>(sp => sp.GetRequiredService<GitConfigSource>());
            return services;
        }
    }
}
