using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.RateLimiting;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Application.Api.IdentityAdmin;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Security.ApiKeys;
using ConfigServer.Domain.Security.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConfigServer.Application.Api.Demo
{
    /// <summary>Deletes idle workspaces: once at startup, then regularly.</summary>
    public sealed class DemoCleanupService : BackgroundService
    {
        private readonly DemoWorkspaces _workspaces;
        private readonly DemoOptions _options;
        private readonly ILogger<DemoCleanupService> _logger;

        public DemoCleanupService(DemoWorkspaces workspaces, DemoOptions options, ILogger<DemoCleanupService> logger)
        {
            _workspaces = workspaces;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromMinutes(Math.Clamp(_options.IdleTtlMinutes / 4.0, 1, 5));
            using var timer = new PeriodicTimer(interval);
            do
            {
                try
                {
                    _workspaces.Cleanup();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Demo workspace cleanup failed; it will be retried.");
                }
            }
            while (await WaitAsync(timer, stoppingToken));
        }

        private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
        {
            try
            {
                return await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    public static class DemoServiceCollectionExtensions
    {
        /// <summary>
        /// Registers <see cref="DemoOptions"/> always (the auth info endpoint reports it) and, when "Demo:Enabled", the
        /// sandbox services: per-visitor workspaces behind <see cref="IConfigSource"/>/<see cref="IConfigStore"/>/<see cref="IConfigCatalog"/>,
        /// per-workspace API keys, the simulated <see cref="IIdentityAdmin"/>, the idle cleanup and the rate limiter.
        /// Call it before the other registrations: they use TryAdd, so the sandbox services win. Throws
        /// <see cref="InvalidOperationException"/> on invalid settings.
        /// </summary>
        public static IServiceCollection AddConfigServerDemo(this IServiceCollection services, IConfiguration configuration)
        {
            var options = DemoOptions.Load(configuration);
            services.TryAddSingleton(options);
            if (!options.Enabled) return services;

            var auth = AuthOptions.Load(configuration);
            options.Validate(auth, IdentityAdminOptions.Load(configuration), configuration);

            services.AddHttpContextAccessor();
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<DemoWorkspaces>();
            services.AddSingleton<DemoWorkspaceAccessor>();
            services.AddSingleton<DemoConfigSource>();
            services.AddSingleton<IConfigSource>(sp => sp.GetRequiredService<DemoConfigSource>());
            services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<DemoConfigSource>());
            services.AddSingleton<IConfigCatalog>(sp => sp.GetRequiredService<DemoConfigSource>());
            services.AddSingleton<IApiKeyStore, DemoApiKeyStore>();

            var tokenEndpoint = Blank(configuration[DemoOptions.SectionName + ":TokenEndpoint"])
                ?? (string.IsNullOrWhiteSpace(auth.Oidc.Authority) ? null : auth.Oidc.Authority.Trim().TrimEnd('/') + "/protocol/openid-connect/token");
            services.AddSingleton<IIdentityAdmin>(sp => new DemoIdentityAdmin(
                sp.GetRequiredService<DemoWorkspaceAccessor>(), sp.GetRequiredService<DemoWorkspaces>(), options,
                sp.GetRequiredService<TimeProvider>(), auth.Oidc.ClientId, tokenEndpoint));

            services.AddHostedService<DemoCleanupService>();
            AddRateLimiting(services, options);
            return services;
        }

        /// <summary>
        /// Rate limiting and workspace binding; call after UseAuthentication and before UseAuthorization. Does nothing
        /// when the sandbox is off.
        /// </summary>
        public static IApplicationBuilder UseConfigServerDemo(this IApplicationBuilder app)
        {
            if (!app.ApplicationServices.GetRequiredService<DemoOptions>().Enabled) return app;
            // Resolve now so a broken sandbox directory fails startup, not the first visitor.
            app.ApplicationServices.GetRequiredService<DemoWorkspaces>();
            app.UseRateLimiter();
            return app.UseDemoWorkspaces();
        }

        private static void AddRateLimiting(IServiceCollection services, DemoOptions options)
        {
            services.AddRateLimiter(limiter =>
            {
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.OnRejected = async (context, cancellationToken) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                        context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                    await context.HttpContext.Response.WriteAsJsonAsync(
                        new { error = "Too many requests: the sandbox limits how fast each visitor can call it. Wait a moment and try again." }, cancellationToken);
                };
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    // Health probes come from the platform, not from visitors.
                    if (context.Request.Path.StartsWithSegments("/health")) return RateLimitPartition.GetNoLimiter("health");

                    // A visitor (token sub, or the workspace an API key belongs to); anonymous callers share their address.
                    var visitor = context.Items[DemoWorkspaceAccessor.ItemKey] as string
                        ?? (context.User?.Identity?.IsAuthenticated == true ? context.User.FindFirst("sub")?.Value : null);
                    var key = visitor != null ? "v:" + visitor : "ip:" + context.Connection.RemoteIpAddress;
                    return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.RequestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    });
                });
            });
        }

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
