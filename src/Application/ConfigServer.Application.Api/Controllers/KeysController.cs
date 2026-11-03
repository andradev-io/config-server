using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConfigServer.Application.Api.Controllers
{
    public sealed class CreateApiKeyRequest
    {
        public string Name { get; set; }
        public string[] Apps { get; set; }
        public string[] Environments { get; set; }
        public string Permission { get; set; }

        /// <summary>ISO 8601 date/time (UTC assumed without offset) or null for no expiry.</summary>
        public string ExpiresAt { get; set; }
    }

    /// <summary>
    /// API key management (config:admin, never reachable with an API key). Hashes are never returned;
    /// the secret is returned once by create and rotate.
    /// </summary>
    [ApiController]
    [Route("api/keys")]
    [Authorize(Policy = ConfigPolicies.Admin)]
    public class KeysController : ControllerBase
    {
        private readonly ApiKeyManager _keys;

        public KeysController(ApiKeyManager keys)
        {
            _keys = keys;
        }

        [HttpGet]
        public async Task<IActionResult> List(CancellationToken cancellationToken)
        {
            var now = _keys.Now;
            var keys = await _keys.ListAsync(cancellationToken);
            return Ok(keys.Select(k => ToJson(k, now)).ToList());
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest body, CancellationToken cancellationToken)
        {
            if (body == null) return BadRequest(Error("Body must be a JSON object with a 'name'."));

            DateTimeOffset? expiresAt = null;
            if (!string.IsNullOrWhiteSpace(body.ExpiresAt))
            {
                if (!DateTimeOffset.TryParse(body.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    return BadRequest(Error("'expiresAt' must be an ISO 8601 date/time or null."));
                expiresAt = parsed;
            }

            try
            {
                var issued = await _keys.CreateAsync(new ApiKeyCreateRequest
                {
                    Name = body.Name,
                    Apps = body.Apps,
                    Environments = body.Environments,
                    Permission = body.Permission,
                    ExpiresAt = expiresAt
                }, cancellationToken);
                return Created($"api/keys/{issued.Key.Id}", Issued(issued));
            }
            catch (ApiKeyValidationException ex)
            {
                return BadRequest(Error(ex.Message));
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Revoke(string id, CancellationToken cancellationToken)
        {
            var revoked = await _keys.RevokeAsync(id, cancellationToken);
            return revoked == null ? NotFound(Error("API key not found.")) : NoContent();
        }

        [HttpPost("{id}/rotate")]
        public async Task<IActionResult> Rotate(string id, CancellationToken cancellationToken)
        {
            try
            {
                var issued = await _keys.RotateAsync(id, cancellationToken);
                return issued == null ? NotFound(Error("API key not found.")) : Ok(Issued(issued));
            }
            catch (ApiKeyValidationException ex)
            {
                return BadRequest(Error(ex.Message));
            }
        }

        private object Issued(ApiKeyIssued issued) => new { key = ToJson(issued.Key, _keys.Now), secret = issued.Secret };

        // Explicit projection: the hash never leaves the server.
        private static object ToJson(ApiKey key, DateTimeOffset now) => new
        {
            id = key.Id,
            name = key.Name,
            prefix = key.Prefix,
            apps = key.Apps,
            environments = key.Environments,
            permission = key.Permission,
            createdAt = key.CreatedAt,
            expiresAt = key.ExpiresAt,
            revokedAt = key.RevokedAt,
            lastUsedAt = key.LastUsedAt,
            status = key.StatusAt(now)
        };

        private static object Error(string message) => new { error = message };
    }
}
