using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Application.Api.Configuration;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Configuration.Formats;
using ConfigServer.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ConfigServer.Application.Api.Controllers
{
    public sealed class WriteFileRequest
    {
        public string Content { get; set; }
    }

    /// <summary>
    /// Files management for the editor UI. These endpoints expose and modify configuration:
    /// they require authentication and TLS before any non-local use. Writes are gated by "ConfigSource:AllowWrite".
    /// Per-app/environment access is enforced by <see cref="AccessScopeFilter"/>; listings are filtered here.
    /// </summary>
    [ApiController]
    [Route("api/apps")]
    [Authorize(Policy = ConfigPolicies.Read)]
    public class FilesController : ControllerBase
    {
        private readonly IConfigStore _store;
        private readonly IConfiguration _configuration;

        // IConfigStore is optional: sources without write support (Git) do not register it.
        public FilesController(IConfiguration configuration, IConfigStore store = null)
        {
            _configuration = configuration;
            _store = store;
        }

        [HttpGet]
        public async Task<IActionResult> ListApps(CancellationToken cancellationToken)
        {
            if (_store == null) return NotImplementedStore();
            // Only apps the caller has a grant on.
            return Ok(ConfigPrincipal.Grants(User).AllowedApps(await _store.ListAppsAsync(cancellationToken)));
        }

        [HttpGet("{app}/files")]
        public async Task<IActionResult> ListFiles(string app, CancellationToken cancellationToken)
        {
            if (!ConfigNames.IsValidAppName(app)) return BadRequest(Error("Invalid application name."));
            if (_store == null) return NotImplementedStore();

            var files = await _store.ListFilesAsync(app, cancellationToken);
            if (files == null) return NotFound(Error("Application not found."));
            // default.* (readable with any environment of the app, see AccessScopeFilter) + the readable environments' files.
            var grants = ConfigPrincipal.Grants(User);
            return Ok(files
                .Where(f => ConfigNames.IsDefault(f.Name)
                    ? grants.CanReadDefault(app)
                    : grants.Can(AccessPermission.Read, app, ConfigNames.LogicalName(f.Name)))
                .Select(f => new { name = f.Name, format = f.Format.ToName(), size = f.Size }));
        }

        [HttpGet("{app}/files/{fileName}")]
        public async Task<IActionResult> GetFile(string app, string fileName, CancellationToken cancellationToken)
        {
            var invalid = ValidateNames(app, fileName);
            if (invalid != null) return invalid;
            if (_store == null) return NotImplementedStore();

            var content = await _store.ReadAsync(app, fileName, cancellationToken);
            if (content == null) return NotFound(Error("File not found."));
            return Content(content, "text/plain");
        }

        [HttpPut("{app}/files/{fileName}")]
        [Authorize(Policy = ConfigPolicies.Write)]
        [RequestSizeLimit(1_048_576)]
        public async Task<IActionResult> PutFile(string app, string fileName, [FromBody] WriteFileRequest body, CancellationToken cancellationToken)
        {
            var invalid = ValidateNames(app, fileName);
            if (invalid != null) return invalid;
            var forbidden = EnsureWritable();
            if (forbidden != null) return forbidden;
            if (body?.Content == null) return BadRequest(Error("Body must be a JSON object with a string 'content'."));

            try
            {
                ConfigFormatExtensions.FromFileName(fileName).Value.Parse(body.Content);
            }
            catch (ConfigFormatException ex)
            {
                return BadRequest(new { error = ex.Message, line = ex.Line, column = ex.Column });
            }

            switch (await _store.WriteAsync(app, fileName, body.Content, cancellationToken))
            {
                case ConfigWriteResult.Created:
                    return Created($"api/apps/{app}/files/{fileName}", null);
                case ConfigWriteResult.Updated:
                    return NoContent();
                default:
                    return Conflict(Error($"A file named '{ConfigNames.LogicalName(fileName)}' with a different extension already exists."));
            }
        }

        [HttpDelete("{app}/files/{fileName}")]
        [Authorize(Policy = ConfigPolicies.Write)]
        public async Task<IActionResult> DeleteFile(string app, string fileName, CancellationToken cancellationToken)
        {
            var invalid = ValidateNames(app, fileName);
            if (invalid != null) return invalid;
            var forbidden = EnsureWritable();
            if (forbidden != null) return forbidden;

            switch (await _store.DeleteAsync(app, fileName, cancellationToken))
            {
                case ConfigDeleteResult.Deleted:
                    return NoContent();
                case ConfigDeleteResult.NotFound:
                    return NotFound(Error("File not found."));
                default:
                    return Conflict(Error("The default file cannot be deleted while override files exist."));
            }
        }

        private IActionResult ValidateNames(string app, string fileName)
        {
            if (!ConfigNames.IsValidAppName(app)) return BadRequest(Error("Invalid application name."));
            if (ConfigNames.IsReservedFileName(fileName))
                return BadRequest(Error("Reserved files (starting with '_') cannot be managed through the files API; use the environments and variables endpoints."));
            if (!ConfigNames.IsValidFileName(fileName))
                return BadRequest(Error("Invalid file name. Use default.* or {environment}.* with extension .json, .yaml, .yml or .env."));
            return null;
        }

        /// <summary>403 when writes are disabled, 501 when the active source has no store; otherwise null.</summary>
        private IActionResult EnsureWritable()
        {
            if (!_configuration.IsWriteAllowed())
                return StatusCode(StatusCodes.Status403Forbidden, Error("Writing configuration files is disabled (ConfigSource:AllowWrite=false)."));
            return _store == null ? NotImplementedStore() : null;
        }

        private IActionResult NotImplementedStore()
            => StatusCode(StatusCodes.Status501NotImplemented, Error("The active configuration source does not support file management."));

        private static object Error(string message) => new { error = message, line = (int?)null, column = (int?)null };
    }
}
