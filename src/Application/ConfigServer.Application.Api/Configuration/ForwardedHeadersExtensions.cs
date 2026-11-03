using System;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConfigServer.Application.Api.Configuration
{
    /// <summary>
    /// "ForwardedHeaders": honours X-Forwarded-For/Proto/Host from a reverse proxy (nginx, Traefik, ...) so the client address,
    /// the scheme and the host are the real ones (the sandbox rate limiter keys anonymous callers on the address).
    /// Off by default: trusting these headers from an untrusted caller lets it spoof its address.
    /// <list type="bullet">
    /// <item><c>Enabled</c> (default false).</item>
    /// <item><c>KnownProxies</c> / <c>KnownNetworks</c>: IP addresses / CIDR ranges (e.g. 10.42.0.0/16) of the proxies to trust,
    /// in addition to loopback.</item>
    /// <item><c>TrustAllProxies</c> (default false): trust any sender (clears the known lists). Only for an API reachable
    /// solely through the proxy, e.g. an in-cluster Service behind nginx with no other ingress or NodePort.</item>
    /// <item><c>ForwardLimit</c> (default 1): how many proxies in the chain to read; 0 = all.</item>
    /// </list>
    /// </summary>
    public static class ForwardedHeadersExtensions
    {
        public const string SectionName = "ForwardedHeaders";

        public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue<bool>(SectionName + ":Enabled");

        /// <summary>Configures <see cref="ForwardedHeadersOptions"/> when enabled. Throws <see cref="InvalidOperationException"/> on a bad address or network.</summary>
        public static IServiceCollection AddConfigServerForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
        {
            if (!IsEnabled(configuration)) return services;

            var section = configuration.GetSection(SectionName);
            var trustAll = section.GetValue<bool>("TrustAllProxies");
            var limit = section.GetValue("ForwardLimit", 1);
            var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? Array.Empty<string>();
            var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? Array.Empty<string>();

            var parsedProxies = proxies.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p =>
                IPAddress.TryParse(p.Trim(), out var address) ? address
                    : throw new InvalidOperationException($"{SectionName}:KnownProxies '{p}' is not an IP address.")).ToList();
            var parsedNetworks = networks.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n =>
                System.Net.IPNetwork.TryParse(n.Trim(), out var network) ? network
                    : throw new InvalidOperationException($"{SectionName}:KnownNetworks '{n}' is not a CIDR range (e.g. 10.42.0.0/16).")).ToList();

            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                options.ForwardLimit = limit <= 0 ? null : limit;
                if (trustAll)
                {
                    options.KnownProxies.Clear();
                    options.KnownIPNetworks.Clear();
                    return;
                }
                foreach (var proxy in parsedProxies) options.KnownProxies.Add(proxy);
                foreach (var network in parsedNetworks) options.KnownIPNetworks.Add(network);
            });
            return services;
        }

        /// <summary>Adds the forwarded headers middleware when enabled; must be the first middleware.</summary>
        public static IApplicationBuilder UseConfigServerForwardedHeaders(this IApplicationBuilder app, IConfiguration configuration)
            => IsEnabled(configuration) ? app.UseForwardedHeaders() : app;
    }
}
