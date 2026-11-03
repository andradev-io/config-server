using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Configuration.Formats;
using ConfigServer.Domain.Configuration.Management;
using ConfigServer.Domain.Security;
using ConfigServer.Domain.Security.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ConfigServer.Application.Api.Controllers
{
    public sealed class GrantRequestBody
    {
        public string Permission { get; set; }

        /// <summary>Application name or "*" (default).</summary>
        public string App { get; set; }

        /// <summary>Environment name or "*" (default).</summary>
        public string Environment { get; set; }
    }

    /// <summary>
    /// Who holds which config grants, managed in the identity provider through <see cref="IIdentityAdmin"/> (global admin only;
    /// never reachable with an API key). The identity provider stays the source of truth: grants are client roles
    /// config:&lt;perm&gt;[:&lt;app&gt;[:&lt;env&gt;]] mapped to users or groups, effective at the subject's next token refresh.
    /// 501 when IdentityAdmin:Provider=None; provider failures are 502 {error} naming what to fix.
    /// </summary>
    [ApiController]
    [Route("api/access")]
    [Authorize(Policy = ConfigPolicies.Admin)]
    public class AccessController : IdentityAdminControllerBase
    {
        public const int DefaultMax = 20;
        public const int MaxPageSize = 100;
        public const int MaxSearchLength = 200;

        private readonly ILogger<AccessController> _logger;

        public AccessController(IIdentityAdmin admin, ILogger<AccessController> logger, IConfigCatalog catalog = null)
            : base(admin, catalog)
        {
            _logger = logger;
        }

        [HttpGet("info")]
        public async Task<IActionResult> Info(CancellationToken cancellationToken)
        {
            var info = await Admin.GetInfoAsync(cancellationToken);
            return Ok(new
            {
                provider = info.Provider,
                enabled = info.Enabled,
                configClientId = info.ConfigClientId,
                note = info.Note,
                tokenEndpoint = info.TokenEndpoint
            });
        }

        [HttpGet("users")]
        public Task<IActionResult> Users([FromQuery] string search, [FromQuery] int? first, [FromQuery] int? max, CancellationToken cancellationToken)
            => Run(async () =>
            {
                var page = ValidatePage(search, first, max);
                if (page.Error != null) return page.Error;
                return Ok(await Admin.SearchUsersAsync(Trimmed(search), page.First, page.Max, cancellationToken));
            }, cancellationToken);

        [HttpGet("groups")]
        public Task<IActionResult> Groups([FromQuery] string search, [FromQuery] int? first, [FromQuery] int? max, CancellationToken cancellationToken)
            => Run(async () =>
            {
                var page = ValidatePage(search, first, max);
                if (page.Error != null) return page.Error;
                return Ok(await Admin.SearchGroupsAsync(Trimmed(search), page.First, page.Max, cancellationToken));
            }, cancellationToken);

        [HttpGet("subjects/{type}/{id}/grants")]
        public Task<IActionResult> Grants(string type, string id, CancellationToken cancellationToken)
            => Run(async () =>
            {
                var subject = ValidateSubject(type, id);
                if (subject.Error != null) return subject.Error;
                return Ok(await Admin.GetGrantsAsync(subject.Type, id, cancellationToken));
            }, cancellationToken);

        [HttpPost("subjects/{type}/{id}/grants")]
        public Task<IActionResult> Grant(string type, string id,
            [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] GrantRequestBody body,
            CancellationToken cancellationToken)
            => Run(async () =>
            {
                var subject = ValidateSubject(type, id);
                if (subject.Error != null) return subject.Error;
                if (body == null) return BadRequest(Error("Body must be a JSON object {permission, app?, environment?}."));
                if (!ConfigGrant.TryCreate(body.Permission, body.App, body.Environment, out var grant, out var error))
                    return BadRequest(Error(error));
                var unknown = await ValidateKnownNamesAsync(grant, cancellationToken);
                if (unknown != null) return unknown;

                var granted = await Admin.GrantAsync(subject.Type, id, grant, cancellationToken);
                _logger.LogInformation("Access: {Actor} granted role {Role} to {SubjectType} {SubjectId}.",
                    Actor(), granted.Role, subject.Type.ToName(), id);
                return StatusCode(StatusCodes.Status201Created, granted);
            }, cancellationToken);

        /// <summary>
        /// Removes the subject's direct mappings of the grant (idempotent 204). Names are only checked for syntax, so grants on
        /// applications or environments that no longer exist can still be revoked.
        /// </summary>
        [HttpDelete("subjects/{type}/{id}/grants")]
        public Task<IActionResult> Revoke(string type, string id, [FromQuery] string permission, [FromQuery] string app,
            [FromQuery] string environment, CancellationToken cancellationToken)
            => Run(async () =>
            {
                var subject = ValidateSubject(type, id);
                if (subject.Error != null) return subject.Error;
                if (!ConfigGrant.TryCreate(permission, app, environment, out var grant, out var error))
                    return BadRequest(Error(error));

                var removed = await Admin.RevokeAsync(subject.Type, id, grant, cancellationToken);
                _logger.LogInformation("Access: {Actor} revoked role {Role} from {SubjectType} {SubjectId} (mapping removed: {Removed}).",
                    Actor(), grant.RoleName, subject.Type.ToName(), id, removed);
                return NoContent();
            }, cancellationToken);

        private (IActionResult Error, int First, int Max) ValidatePage(string search, int? first, int? max)
        {
            if (search != null && search.Length > MaxSearchLength)
                return (BadRequest(Error($"'search' is limited to {MaxSearchLength} characters.")), 0, 0);
            if (first < 0) return (BadRequest(Error("'first' must be 0 or more.")), 0, 0);
            if (max < 1 || max > MaxPageSize) return (BadRequest(Error($"'max' must be between 1 and {MaxPageSize}.")), 0, 0);
            return (null, first ?? 0, max ?? DefaultMax);
        }

        private (IActionResult Error, SubjectType Type) ValidateSubject(string type, string id)
        {
            if (!SubjectTypes.TryParse(type, out var parsed))
                return (BadRequest(Error("Subject type must be 'user' or 'group'.")), default);
            if (!IdentitySubjects.IsValidId(id))
                return (BadRequest(Error($"Invalid {parsed.ToName()} id.")), default);
            return (null, parsed);
        }

        private static string Trimmed(string search) => string.IsNullOrWhiteSpace(search) ? null : search.Trim();
    }
}
