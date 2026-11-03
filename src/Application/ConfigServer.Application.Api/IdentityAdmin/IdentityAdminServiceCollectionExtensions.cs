using System;
using ConfigServer.Domain.Security.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConfigServer.Application.Api.IdentityAdmin
{
    public static class IdentityAdminServiceCollectionExtensions
    {
        /// <summary>
        /// Registers <see cref="IIdentityAdmin"/> from the "IdentityAdmin" section: <see cref="DisabledIdentityAdmin"/> for
        /// Provider=None (default), <see cref="KeycloakIdentityAdmin"/> (typed HttpClient) for Provider=Keycloak. Uses TryAdd,
        /// so another <see cref="IIdentityAdmin"/> registered first wins. Throws <see cref="InvalidOperationException"/>
        /// on invalid options.
        /// </summary>
        public static IServiceCollection AddConfigServerIdentityAdmin(this IServiceCollection services, IConfiguration configuration)
        {
            var options = IdentityAdminOptions.Load(configuration);
            options.Validate();
            services.TryAddSingleton(options);

            if (options.Provider != IdentityAdminProvider.Keycloak)
            {
                services.TryAddSingleton<IIdentityAdmin, DisabledIdentityAdmin>();
                return services;
            }

            services.TryAddSingleton(options.Keycloak);
            services.TryAddSingleton<KeycloakAdminCache>();
            services.TryAddSingleton(TimeProvider.System);
            services.AddHttpClient<KeycloakIdentityAdmin>(client => client.Timeout = TimeSpan.FromSeconds(options.Keycloak.TimeoutSeconds))
                // Never log the bearer token (or any other header value), whatever the log level.
                .RedactLoggedHeaders(_ => true);
            services.TryAddTransient<IIdentityAdmin>(sp => sp.GetRequiredService<KeycloakIdentityAdmin>());
            return services;
        }
    }
}
