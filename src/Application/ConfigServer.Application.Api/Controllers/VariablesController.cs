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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ConfigServer.Application.Api.Controllers
{
    public sealed class WriteVariablesRequest
    {
        public List<VariableDefinition> Variables { get; set; }
    }

    /// <summary>
    /// Declared variables per app, status checks per environment and the cross-project variable index.
    /// Configuration values are never returned. These endpoints expose the structure of configs: require
    /// authentication + TLS before non-local use. Writes are gated by "ConfigSource:AllowWrite".
    /// </summary>
    [ApiController]
    [Authorize(Policy = ConfigPolicies.Read)]
    public class VariablesController : ConfigApiControllerBase
    {
        public VariablesController(IConfiguration configuration, IConfigCatalog catalog = null, IConfigStore store = null)
            : base(configuration, catalog, store)
        {
        }

        [HttpGet("api/apps/{app}/variables")]
        public async Task<IActionResult> GetDeclared(string app, CancellationToken cancellationToken)
        {
            if (!ConfigNames.IsValidAppName(app)) return BadRequest(Error("Invalid application name."));
            var unavailable = EnsureReadable();
            if (unavailable != null) return unavailable;
            if (await Catalog.ListFilesAsync(app, cancellationToken) == null) return NotFound(Error("Application not found."));

            try
            {
                return Ok(await ConfigCatalogReader.ReadVariablesAsync(Catalog, app, cancellationToken));
            }
            catch (ConfigFormatException ex)
            {
                return FormatError(ex);
            }
        }

        [HttpPut("api/apps/{app}/variables")]
        [Authorize(Policy = ConfigPolicies.Write)]
        [RequireScope(ScopeRequirement.AllEnvironments)]
        [RequestSizeLimit(1_048_576)]
        public async Task<IActionResult> PutDeclared(string app, [FromBody] WriteVariablesRequest body, CancellationToken cancellationToken)
        {
            if (!ConfigNames.IsValidAppName(app)) return BadRequest(Error("Invalid application name."));
            var forbidden = EnsureWritable();
            if (forbidden != null) return forbidden;
            if (body?.Variables == null) return BadRequest(Error("Body must be a JSON object with a 'variables' array."));
            if (await Catalog.ListFilesAsync(app, cancellationToken) == null) return NotFound(Error("Application not found."));

            IEnumerable<string> known;
            try
            {
                var registry = await ConfigCatalogReader.ReadRegistryAsync(Catalog, cancellationToken);
                var overrides = await ConfigCatalogReader.ReadOverrideNamesAsync(Catalog, cancellationToken);
                known = registry.Select(e => e.Name).Concat(overrides.Values.SelectMany(v => v)).ToList();
            }
            catch (ConfigFormatException ex)
            {
                return FormatError(ex);
            }

            var invalid = ReservedFiles.ValidateVariables(body.Variables, known);
            if (invalid != null) return BadRequest(Error(invalid));

            await Store.WriteReservedAsync(app, ConfigNames.VariablesFileName, ReservedFiles.SerializeVariables(body.Variables), cancellationToken);
            return NoContent();
        }

        /// <summary>Status of the environments the caller can read (API keys need all environments of the app).</summary>
        [HttpGet("api/apps/{app}/status")]
        [RequireScope(ScopeRequirement.AllEnvironments, ApiKeysOnly = true)]
        public async Task<IActionResult> GetStatus(string app, CancellationToken cancellationToken)
        {
            if (!ConfigNames.IsValidAppName(app)) return BadRequest(Error("Invalid application name."));
            var unavailable = EnsureReadable();
            if (unavailable != null) return unavailable;

            var snapshot = await ConfigCatalogReader.LoadAsync(Catalog, app, cancellationToken);
            var appSnapshot = snapshot.Apps.FirstOrDefault();
            if (appSnapshot == null) return NotFound(Error("Application not found."));
            // Checked on the full snapshot (hidden overrides still count), then limited to the readable environments.
            var grants = ConfigPrincipal.Grants(User);
            var status = StatusChecker.Check(appSnapshot, snapshot.Registry);
            return Ok(status with { Environments = status.Environments.Where(e => grants.Can(AccessPermission.Read, app, e.Name)).ToList() });
        }

        /// <summary>Variables of the apps the caller can read; usages only count readable override files (API keys need environments ["*"]).</summary>
        [HttpGet("api/variables")]
        [RequireScope(ScopeRequirement.AllEnvironments, ApiKeysOnly = true)]
        public async Task<IActionResult> Index([FromQuery] string search = null, [FromQuery] string app = null, [FromQuery] string env = null,
            [FromQuery] bool declaredOnly = false, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrEmpty(app) && !ConfigNames.IsValidAppName(app)) return BadRequest(Error("Invalid application name."));
            var unavailable = EnsureReadable();
            if (unavailable != null) return unavailable;

            var snapshot = SnapshotAccess.Restrict(await ConfigCatalogReader.LoadAsync(Catalog, null, cancellationToken), ConfigPrincipal.Grants(User));
            var variables = VariableIndex.Build(snapshot, new VariableQuery { Search = search, App = app, Environment = env, DeclaredOnly = declaredOnly });
            return Ok(new { variables, warnings = snapshot.Warnings });
        }

        [HttpGet("api/variables/{*key}")]
        [RequireScope(ScopeRequirement.AllEnvironments, ApiKeysOnly = true)]
        public async Task<IActionResult> GetOne(string key, CancellationToken cancellationToken)
        {
            var unavailable = EnsureReadable();
            if (unavailable != null) return unavailable;

            var grants = ConfigPrincipal.Grants(User);
            var snapshot = SnapshotAccess.Restrict(await ConfigCatalogReader.LoadAsync(Catalog, null, cancellationToken), grants);
            var detail = VariableIndex.GetDetail(snapshot, key, (app, env) => grants.Can(AccessPermission.Read, app, env));
            if (detail == null) return NotFound(Error("Variable not found."));
            return Ok(detail);
        }
    }
}
