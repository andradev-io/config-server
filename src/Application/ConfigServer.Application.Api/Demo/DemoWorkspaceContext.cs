using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace ConfigServer.Application.Api.Demo
{
    /// <summary>
    /// The workspace of the request being served. <see cref="DemoWorkspaceMiddleware"/> sets it once the caller is
    /// authenticated; the API key store sets <see cref="ItemKey"/> earlier, while it authenticates a key (a key belongs to the
    /// workspace it was created in).
    /// </summary>
    public sealed class DemoWorkspaceAccessor
    {
        public const string ItemKey = "config-server.demo.workspace-id";

        private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _http;
        private readonly DemoWorkspaces _workspaces;

        public DemoWorkspaceAccessor(Microsoft.AspNetCore.Http.IHttpContextAccessor http, DemoWorkspaces workspaces)
        {
            _http = http;
            _workspaces = workspaces;
        }

        /// <summary>Id of the current request's workspace, or null (anonymous request, no HTTP context).</summary>
        public string CurrentId => _http.HttpContext?.Items[ItemKey] as string;

        /// <summary>The current request's workspace. Throws outside an authenticated request.</summary>
        public DemoWorkspace Current
        {
            get
            {
                var id = CurrentId ?? throw new InvalidOperationException("No demo workspace is bound to this request.");
                return _workspaces.Get(id);
            }
        }

        /// <summary>Binds the request to <paramref name="id"/> unless it already is bound (a key lookup never overrides the caller's own workspace).</summary>
        public void BindIfUnbound(string id)
        {
            var context = _http.HttpContext;
            if (context != null && !context.Items.ContainsKey(ItemKey)) context.Items[ItemKey] = id;
        }
    }

    /// <summary>
    /// Runs after authentication: rejects oversized bodies, binds the request to the caller's workspace (created from the template
    /// on first use; 503 when the sandbox is full) and turns sandbox limit exceptions into {error} responses.
    /// </summary>
    public sealed class DemoWorkspaceMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly DemoOptions _options;
        private readonly DemoWorkspaces _workspaces;

        public DemoWorkspaceMiddleware(RequestDelegate next, DemoOptions options, DemoWorkspaces workspaces)
        {
            _next = next;
            _options = options;
            _workspaces = workspaces;
        }

        public async Task Invoke(HttpContext context)
        {
            if (context.Request.ContentLength > _options.MaxRequestBodyBytes)
            {
                await Reject(context, StatusCodes.Status413PayloadTooLarge,
                    $"The request body is too large for the sandbox (limit {_options.MaxRequestBodyBytes / 1024} KB).");
                return;
            }
            var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit != null && !bodyLimit.IsReadOnly) bodyLimit.MaxRequestBodySize = _options.MaxRequestBodyBytes;

            if (context.User?.Identity?.IsAuthenticated == true)
            {
                var id = context.Items[DemoWorkspaceAccessor.ItemKey] as string;
                if (id == null)
                {
                    var subject = context.User.FindFirst("sub")?.Value;
                    if (string.IsNullOrEmpty(subject))
                    {
                        await Reject(context, StatusCodes.Status401Unauthorized, "The token has no 'sub' claim, so the sandbox cannot give you a workspace.");
                        return;
                    }
                    id = DemoWorkspaces.IdFor(subject);
                    context.Items[DemoWorkspaceAccessor.ItemKey] = id;
                }

                try
                {
                    _workspaces.GetOrCreate(id);
                }
                catch (DemoCapacityException ex)
                {
                    context.Response.Headers["Retry-After"] = "120";
                    await Reject(context, StatusCodes.Status503ServiceUnavailable, ex.Message);
                    return;
                }
            }

            try
            {
                await _next(context);
            }
            catch (DemoLimitException ex) when (!context.Response.HasStarted)
            {
                await Reject(context, StatusCodes.Status413PayloadTooLarge, ex.Message);
            }
        }

        private static Task Reject(HttpContext context, int status, string message)
        {
            // No Response.Clear(): headers set by earlier middleware (CORS) must survive on error responses.
            context.Response.StatusCode = status;
            return context.Response.WriteAsJsonAsync(new { error = message });
        }
    }

    public static class DemoApplicationBuilderExtensions
    {
        /// <summary>Adds <see cref="DemoWorkspaceMiddleware"/>; call after UseAuthentication.</summary>
        public static IApplicationBuilder UseDemoWorkspaces(this IApplicationBuilder app)
            => app.UseMiddleware<DemoWorkspaceMiddleware>();
    }
}
