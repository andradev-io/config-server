using System.Linq;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Application.Api.Demo;
using ConfigServer.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConfigServer.Application.Api.Controllers
{
    /// <summary>What the SPA needs to sign in (anonymous) and who the caller is.</summary>
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly AuthOptions _options;
        private readonly DemoOptions _demo;

        public AuthController(AuthOptions options, DemoOptions demo)
        {
            _options = options;
            _demo = demo;
        }

        [HttpGet("info")]
        [AllowAnonymous]
        public IActionResult Info()
        {
            var oidc = _options.Mode == AuthMode.Oidc
                ? new { authority = _options.Oidc.Authority, clientId = _options.Oidc.ClientId, scopes = _options.Oidc.Scopes }
                : null;
            var demo = _demo.Enabled ? new { enabled = true, idleTtlMinutes = _demo.IdleTtlMinutes, notice = _demo.Notice } : null;
            return Ok(new { mode = _options.Mode == AuthMode.Oidc ? "oidc" : "none", oidc, apiKeys = true, demo });
        }

        /// <summary>
        /// The caller: <c>roles</c> = coarse levels held in at least one scope (config:admin only when global),
        /// <c>grants</c> = the exact scopes ({permission: read|write|admin, app, environment}, "*" = all).
        /// </summary>
        [HttpGet("me")]
        [Authorize]
        public IActionResult Me()
            => Ok(new
            {
                name = ConfigPrincipal.Name(User),
                roles = ConfigPrincipal.Roles(User),
                via = ConfigPrincipal.Via(User),
                grants = ConfigPrincipal.Grants(User).Grants
                    .Select(g => new { permission = g.Permission.ToName(), app = g.App, environment = g.Environment })
                    .ToList()
            });
    }
}
