using ConfigServer.Application.Api.Authentication;
using ConfigServer.Application.Api.Configuration;
using ConfigServer.Application.Api.Demo;
using ConfigServer.Application.Api.IdentityAdmin;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ConfigServer.Application.Api
{
    public class Startup
    {
        public IHostEnvironment Environment { get; }
        public IConfiguration Configuration { get; }
        public Startup(IHostEnvironment environment, IConfiguration configuration)
        {
            Environment = environment;
            Configuration = configuration;
        }

        

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddConfigServerForwardedHeaders(Configuration);
            services.AddHealthChecks();
            services.AddControllers();
            // First: the sandbox registers its own sources, key store and identity admin before the TryAdd defaults.
            services.AddConfigServerDemo(Configuration);
            services.AddConfigSource(Configuration);
            services.AddConfigServerCors(Configuration);
            services.AddConfigServerAuth(Configuration, Environment);
            services.AddConfigServerIdentityAdmin(Configuration);
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // First: later middleware (HTTPS redirection, the sandbox rate limiter) must see the real client address and scheme.
            app.UseConfigServerForwardedHeaders(Configuration);

            if (env.IsDevelopment())
                app.UseDeveloperExceptionPage();

            // Resolve now so an API key store path inside the config source fails startup, not the first request.
            app.ApplicationServices.GetRequiredService<IApiKeyStore>();

            app.UseHttpsRedirection();

            // Configuration content must never be cached by browsers or proxies.
            app.Use(async (context, next) =>
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["Cache-Control"] = "no-store";
                    return System.Threading.Tasks.Task.CompletedTask;
                });
                await next();
            });

            app.UseRouting();

            app.UseCors(ConfigServerCorsExtensions.PolicyName);

            app.UseAuthentication();
            app.UseConfigServerDemo();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHealthChecks("/health").AllowAnonymous();
                endpoints.MapControllers();
            });
        }
    }
}
