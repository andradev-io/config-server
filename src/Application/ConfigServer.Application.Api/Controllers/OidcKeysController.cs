using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Security;
using ConfigServer.Domain.Security.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ConfigServer.Application.Api.Controllers
{
    public sealed class OidcKeyRequestBody
    {
        public string Name { get; set; }

        public List<GrantRequestBody> Grants { get; set; }
    }

    /// <summary>
    /// "OIDC API keys": machine credentials (a confidential client with a service account) created in the identity provider
    /// through <see cref="IIdentityAdmin"/>, holding the same config grants as users. They request a token with the
    /// client_credentials flow and call the API with it as a Bearer. Global admin only, never reachable with an API key;
    /// 501 when IdentityAdmin:Provider=None. The secret is returned once, on create and rotate, and is never logged.
    /// </summary>
    [ApiController]
    [Route("api/oidc-keys")]
    [Authorize(Policy = ConfigPolicies.Admin)]
    public class OidcKeysController : IdentityAdminControllerBase
    {
        private readonly ILogger<OidcKeysController> _logger;

        public OidcKeysController(IIdentityAdmin admin, ILogger<OidcKeysController> logger, IConfigCatalog catalog = null)
            : base(admin, catalog)
        {
            _logger = logger;
        }

        [HttpGet]
        public Task<IActionResult> List(CancellationToken cancellationToken)
            => Run(async () => Ok(await Admin.ListMachineClientsAsync(cancellationToken)), cancellationToken);

        [HttpPost]
        public Task<IActionResult> Create([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] OidcKeyRequestBody body,
            CancellationToken cancellationToken)
            => Run(async () =>
            {
                if (body == null) return BadRequest(Error("Body must be a JSON object {name, grants:[{permission, app?, environment?}]}."));
                if (!MachineClients.IsValidName(body.Name))
                    return BadRequest(Error($"'name' is required (at most {MachineClients.MaxNameLength} characters, no control characters)."));
                var requested = body.Grants ?? new List<GrantRequestBody>();
                if (requested.Count == 0) return BadRequest(Error("At least one grant is required."));
                if (requested.Count > MachineClients.MaxGrants) return BadRequest(Error($"At most {MachineClients.MaxGrants} grants per credential."));

                var grants = new List<ConfigGrant>();
                foreach (var item in requested)
                {
                    if (item == null) return BadRequest(Error("Every grant must be an object {permission, app?, environment?}."));
                    if (!ConfigGrant.TryCreate(item.Permission, item.App, item.Environment, out var grant, out var error))
                        return BadRequest(Error(error));
                    if (grant.Permission == AccessPermission.Admin)
                        return BadRequest(Error("Machine credentials cannot hold config:admin (use read or write)."));
                    var unknown = await ValidateKnownNamesAsync(grant, cancellationToken);
                    if (unknown != null) return unknown;
                    if (!grants.Contains(grant)) grants.Add(grant);
                }

                var created = await Admin.CreateMachineClientAsync(body.Name.Trim(), grants, cancellationToken);
                _logger.LogInformation("OIDC key: {Actor} created machine credential {ClientId} with {Grants} grant(s).",
                    Actor(), created.Client.ClientId, created.Client.Grants.Count);
                return StatusCode(StatusCodes.Status201Created, await Result(created, cancellationToken));
            }, cancellationToken);

        [HttpPost("{id}/rotate")]
        public Task<IActionResult> Rotate(string id, CancellationToken cancellationToken)
            => Run(async () =>
            {
                if (!IdentitySubjects.IsValidId(id)) return BadRequest(Error("Invalid id."));
                var rotated = await Admin.RotateMachineClientSecretAsync(id, cancellationToken);
                _logger.LogInformation("OIDC key: {Actor} rotated the secret of machine credential {ClientId}.", Actor(), rotated.Client.ClientId);
                return Ok(await Result(rotated, cancellationToken));
            }, cancellationToken);

        /// <summary>Idempotent 204: an unknown credential (or one this server did not create) is simply not there.</summary>
        [HttpDelete("{id}")]
        public Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
            => Run(async () =>
            {
                if (!IdentitySubjects.IsValidId(id)) return BadRequest(Error("Invalid id."));
                var removed = await Admin.DeleteMachineClientAsync(id, cancellationToken);
                _logger.LogInformation("OIDC key: {Actor} deleted machine credential {Id} (existed: {Removed}).", Actor(), id, removed);
                return NoContent();
            }, cancellationToken);

        private async Task<object> Result(MachineClientSecret created, CancellationToken cancellationToken)
        {
            var info = await Admin.GetInfoAsync(cancellationToken);
            return new
            {
                client = created.Client,
                clientId = created.Client.ClientId,
                secret = created.Secret,
                tokenEndpoint = info.TokenEndpoint
            };
        }
    }
}
