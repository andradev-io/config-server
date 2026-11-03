using System;
using System.Linq;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ConfigServer.Application.Api.Authentication
{
    /// <summary>Auth:Mode=None (Development only): every request is the synthetic admin "local".</summary>
    public sealed class NoAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public NoAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(ConfigPrincipal.ForNone(), Scheme.Name)));
    }

    /// <summary>Failure of the API key scheme; its message is safe to return to the client.</summary>
    public sealed class ApiKeyAuthenticationException : Exception
    {
        public ApiKeyAuthenticationException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// API keys from "X-Api-Key: {key}" or "Authorization: ApiKey {key}". Revoked/expired keys fail (401).
    /// The principal gets config:read (+ config:write for write keys), never admin, plus its app/environment scope.
    /// </summary>
    public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string HeaderName = "X-Api-Key";
        public const string AuthorizationScheme = "ApiKey";

        private readonly ApiKeyManager _keys;

        public ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyManager keys)
            : base(options, logger, encoder)
        {
            _keys = keys;
        }

        /// <summary>True when the request carries an API key in either header (used by the scheme selector).</summary>
        public static bool HasApiKey(HttpRequest request)
            => request.Headers.ContainsKey(HeaderName) || ReadAuthorizationKey(request) != null;

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var presented = Request.Headers.TryGetValue(HeaderName, out var header) ? header.FirstOrDefault()?.Trim() : ReadAuthorizationKey(Request);
            if (string.IsNullOrEmpty(presented)) return AuthenticateResult.NoResult();

            var result = await _keys.AuthenticateAsync(presented, Context.RequestAborted);
            if (!result.Succeeded) return AuthenticateResult.Fail(new ApiKeyAuthenticationException(result.Failure));

            return AuthenticateResult.Success(new AuthenticationTicket(ConfigPrincipal.ForApiKey(result.Key), Scheme.Name));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.Append(HeaderNames.WWWAuthenticate, AuthorizationScheme + " header=\"" + HeaderName + "\"");
            return Task.CompletedTask;
        }

        private static string ReadAuthorizationKey(HttpRequest request)
        {
            var value = request.Headers.Authorization.FirstOrDefault();
            if (value == null || !value.StartsWith(AuthorizationScheme + " ", StringComparison.OrdinalIgnoreCase)) return null;
            var key = value.Substring(AuthorizationScheme.Length + 1).Trim();
            return key.Length == 0 ? null : key;
        }
    }
}
