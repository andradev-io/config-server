using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace ConfigServer.Application.Api.Authentication
{
    /// <summary>
    /// 401/403 from the authorization policies with a JSON body <c>{error}</c>. The challenge of the selected scheme sets
    /// WWW-Authenticate (Bearer or ApiKey); an ApiKey challenge is added so clients learn both are accepted.
    /// </summary>
    public sealed class JsonAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new AuthorizationMiddlewareResultHandler();

        public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Challenged)
            {
                await ForEachScheme(policy, scheme => context.ChallengeAsync(scheme));
                var challenges = context.Response.Headers[HeaderNames.WWWAuthenticate];
                if (!challenges.Any(c => c != null && c.StartsWith(ApiKeyAuthenticationHandler.AuthorizationScheme, StringComparison.OrdinalIgnoreCase)))
                    context.Response.Headers.Append(HeaderNames.WWWAuthenticate,
                        ApiKeyAuthenticationHandler.AuthorizationScheme + " header=\"" + ApiKeyAuthenticationHandler.HeaderName + "\"");
                await WriteAsync(context, StatusCodes.Status401Unauthorized, await ChallengeMessageAsync(context));
                return;
            }

            if (authorizeResult.Forbidden)
            {
                await ForEachScheme(policy, scheme => context.ForbidAsync(scheme));
                await WriteAsync(context, StatusCodes.Status403Forbidden,
                    ConfigPrincipal.IsApiKey(context.User) && policy.Requirements.Any(IsAdminRequirement)
                        ? "API keys cannot manage API keys; sign in as an administrator."
                        : "Insufficient role for this operation.");
                return;
            }

            await _default.HandleAsync(next, context, policy, authorizeResult);
        }

        private static bool IsAdminRequirement(IAuthorizationRequirement requirement)
            => requirement is Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement claims
               && claims.ClaimType == ConfigClaimTypes.Role
               && (claims.AllowedValues?.Contains(ConfigPolicies.Admin) ?? false);

        private static async Task ForEachScheme(AuthorizationPolicy policy, Func<string, Task> action)
        {
            if (policy.AuthenticationSchemes.Count == 0)
            {
                await action(null);
                return;
            }
            foreach (var scheme in policy.AuthenticationSchemes)
                await action(scheme);
        }

        private static async Task<string> ChallengeMessageAsync(HttpContext context)
        {
            // Handlers cache their result per request, so this does not re-validate.
            var result = await context.AuthenticateAsync();
            return result.Failure switch
            {
                ApiKeyAuthenticationException apiKey => apiKey.Message,
                null => "Authentication required: send a bearer token (Authorization: Bearer ...) or an API key (X-Api-Key).",
                _ => "Invalid or expired bearer token."
            };
        }

        private static Task WriteAsync(HttpContext context, int status, string message)
        {
            if (context.Response.HasStarted) return Task.CompletedTask;
            context.Response.StatusCode = status;
            return context.Response.WriteAsJsonAsync(new { error = message });
        }
    }
}
