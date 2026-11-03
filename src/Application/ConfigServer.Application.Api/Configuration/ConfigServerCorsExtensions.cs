using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConfigServer.Application.Api.Configuration
{
    public static class ConfigServerCorsExtensions
    {
        public const string PolicyName = "ConfigServerCors";

        /// <summary>Origins come from "Cors:AllowedOrigins" (array). Empty list means no cross-origin access.</summary>
        public static IServiceCollection AddConfigServerCors(this IServiceCollection services, IConfiguration configuration)
        {
            var origins = (configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>())
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .ToArray();

            services.AddCors(options => options.AddPolicy(PolicyName, policy =>
            {
                if (origins.Length > 0)
                    policy.WithOrigins(origins)
                        .WithMethods("GET", "POST", "PUT", "DELETE")
                        .WithHeaders("Content-Type", "Authorization", "X-Api-Key");
            }));
            return services;
        }
    }
}
