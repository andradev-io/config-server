using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Configuration.Formats;
using ConfigServer.Domain.Configuration.Management;
using ConfigServer.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ConfigServer.Application.Api.Controllers
{
    public sealed class WriteOverridesRequest
    {
        public List<OverrideChange> Changes { get; set; }

        /// <summary>Optional etag returned by GET; a mismatch yields 409.</summary>
        public string IfMatch { get; set; }
    }

    /// <summary>
    /// Overrides grid (variables x scopes) of an app. Secret values are never returned. These endpoints expose and modify
    /// configuration: require authentication + TLS before non-local use. Writes are gated by "ConfigSource:AllowWrite".
    /// </summary>
    [ApiController]
    [Authorize(Policy = ConfigPolicies.Read)]
    public class OverridesController : ConfigApiControllerBase
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> AppLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        public OverridesController(IConfiguration configuration, IConfigCatalog catalog = null, IConfigStore store = null)
            : base(configuration, catalog, store)
        {
        }

        /// <summary>
        /// The grid limited to the environments the caller can read; the "default" scope (column and values) only when the
        /// caller can read every environment of the app. Effective values of readable environments still say when they come
        /// from the default. API keys need all environments (403 otherwise).
        /// </summary>
        [HttpGet("api/apps/{app}/overrides")]
        [RequireScope(ScopeRequirement.AllEnvironments, ApiKeysOnly = true)]
        public async Task<IActionResult> Get(string app, CancellationToken cancellationToken)
        {
            if (!ConfigNames.IsValidAppName(app)) return BadRequest(Error("Invalid application name."));
            var unavailable = EnsureReadable();
            if (unavailable != null) return unavailable;
            if (await Catalog.ListFilesAsync(app, cancellationToken) == null) return NotFound(Error("Application not found."));

            try
            {
                var etag = await ComputeEtagAsync(app, cancellationToken);
                var snapshot = await ConfigCatalogReader.LoadAsync(Catalog, app, cancellationToken);
                var appSnapshot = snapshot.Apps.FirstOrDefault();
                if (appSnapshot == null) return NotFound(Error("Application not found."));

                var grants = ConfigPrincipal.Grants(User);
                var showDefault = grants.CanAllEnvironments(AccessPermission.Read, app);
                var environments = grants.AllowedEnvironments(app, await KnownEnvironmentsAsync(cancellationToken));
                var scopes = new List<string>();
                if (showDefault) scopes.Add(OverridesGrid.DefaultScope);
                scopes.AddRange(environments);

                var variables = OverridesGrid.Build(SnapshotAccess.Restrict(appSnapshot, grants), environments)
                    .Select(v => ToJson(v, showDefault)).ToList();
                var warnings = snapshot.Warnings
                    .Where(w => (w.App == null || w.App == app) && SnapshotAccess.IsVisible(w, grants))
                    .Select(w => (w.File + ": " + w.Error))
                    .ToList();
                return Ok(new { app, etag, scopes, variables, warnings });
            }
            catch (ConfigFormatException ex)
            {
                return FormatError(ex);
            }
        }

        [HttpPut("api/apps/{app}/overrides")]
        [Authorize(Policy = ConfigPolicies.Write)]
        [RequestSizeLimit(4_194_304)]
        public async Task<IActionResult> Put(string app, [FromBody] WriteOverridesRequest body, [FromQuery] bool dryRun = false, CancellationToken cancellationToken = default)
        {
            if (!ConfigNames.IsValidAppName(app)) return BadRequest(Error("Invalid application name."));
            var unavailable = dryRun ? EnsureReadable() : EnsureWritable();
            if (unavailable != null) return unavailable;
            if (body?.Changes == null) return BadRequest(Error("Body must be a JSON object with a 'changes' array."));
            var outOfScope = OutOfScopeChange(app, body.Changes);
            if (outOfScope != null) return StatusCode(StatusCodes.Status403Forbidden, new { error = outOfScope });
            if (await Catalog.ListFilesAsync(app, cancellationToken) == null) return NotFound(Error("Application not found."));

            var gate = AppLocks.GetOrAdd(app, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (body.IfMatch != null && !string.Equals(body.IfMatch, await ComputeEtagAsync(app, cancellationToken), StringComparison.Ordinal))
                    return Conflict(Error("Config changed on disk; reload."));

                var snapshot = await ConfigCatalogReader.LoadAsync(Catalog, app, cancellationToken);
                var appSnapshot = snapshot.Apps.FirstOrDefault();
                if (appSnapshot == null) return NotFound(Error("Application not found."));
                var environments = await KnownEnvironmentsAsync(cancellationToken);

                var warnings = new List<string>();
                IReadOnlyList<FilePlan> plans;
                var rendered = new List<(FilePlan Plan, string Text)>();
                try
                {
                    plans = OverridesEditor.Plan(appSnapshot, environments, body.Changes, warnings);
                    foreach (var plan in plans)
                    {
                        rendered.Add((plan, plan.Render()));
                        if (plan.Created) continue;
                        var raw = await Catalog.ReadAsync(app, plan.Name, cancellationToken);
                        if (CommentDetector.HasComments(plan.Format, raw))
                            warnings.Add($"{plan.Name}: comments and formatting are not preserved when the file is rewritten");
                    }
                }
                catch (OverrideException ex)
                {
                    return BadRequest(new { error = ex.Message, key = ex.Key, scope = ex.Scope });
                }
                catch (ConfigFormatException ex)
                {
                    return BadRequest(new { error = ex.Message, key = (string)null, scope = (string)null });
                }

                if (!dryRun)
                {
                    foreach (var (plan, text) in rendered)
                        if (await Store.WriteAsync(app, plan.Name, text, cancellationToken) == ConfigWriteResult.Conflict)
                            return Conflict(Error($"A file named '{ConfigNames.LogicalName(plan.Name)}' with a different extension already exists."));
                }

                var files = plans.Select(p => new { name = p.Name, created = p.Created, changedKeys = p.ChangedKeys }).ToList();
                var etag = dryRun || plans.Count == 0 ? null : await ComputeEtagAsync(app, cancellationToken);
                return Ok(new { files, warnings, etag });
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Every change needs write on (app, scope); the "default" scope (also an empty scope) affects every environment and
        /// needs write on (app, *). One denied change refuses the whole request (nothing is written).
        /// </summary>
        private string OutOfScopeChange(string app, IEnumerable<OverrideChange> changes)
        {
            var grants = ConfigPrincipal.Grants(User);
            if (grants.CanAllEnvironments(AccessPermission.Write, app)) return null;
            foreach (var change in changes)
            {
                var target = change?.Scope;
                if (string.IsNullOrEmpty(target) || string.Equals(target, OverridesGrid.DefaultScope, StringComparison.OrdinalIgnoreCase))
                    return $"Changing the default scope affects every environment; it needs write access to all environments of '{app}'.";
                if (!grants.Can(AccessPermission.Write, app, target))
                    return $"Access denied: no write access to environment '{target}' of application '{app}'.";
            }
            return null;
        }

        /// <summary>Environment names (declared + inferred from override files of all apps), in catalog order.</summary>
        private async Task<List<string>> KnownEnvironmentsAsync(CancellationToken cancellationToken)
        {
            var registry = await ConfigCatalogReader.ReadRegistryAsync(Catalog, cancellationToken);
            var overrides = await ConfigCatalogReader.ReadOverrideNamesAsync(Catalog, cancellationToken);
            return EnvironmentCatalog.Build(registry, overrides).Select(e => e.Name).ToList();
        }

        /// <summary>SHA-256 over the names and contents of all the app's config files and its _variables.json.</summary>
        private async Task<string> ComputeEtagAsync(string app, CancellationToken cancellationToken)
        {
            var files = await Catalog.ListFilesAsync(app, cancellationToken) ?? Array.Empty<ConfigFileInfo>();
            var builder = new StringBuilder();
            foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
                builder.Append(file.Name).Append('\0').Append(await Catalog.ReadAsync(app, file.Name, cancellationToken)).Append('\0');
            builder.Append(ConfigNames.VariablesFileName).Append('\0').Append(await Catalog.ReadReservedAsync(app, ConfigNames.VariablesFileName, cancellationToken));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
        }

        private static object ToJson(GridVariable variable, bool showDefault)
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (scope, cell) in variable.Values.Where(v => showDefault || v.Key != OverridesGrid.DefaultScope))
                values[scope] = cell.Masked
                    ? new Dictionary<string, object> { ["masked"] = true, ["file"] = cell.File }
                    : new Dictionary<string, object> { ["value"] = cell.Value, ["format"] = cell.Format.ToName(), ["file"] = cell.File };

            var effective = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (env, item) in variable.Effective)
                effective[env] = item.Masked
                    ? new Dictionary<string, object> { ["masked"] = true, ["from"] = item.From }
                    : new Dictionary<string, object> { ["value"] = item.Value, ["from"] = item.From };

            return new { key = variable.Key, type = variable.Type, secret = variable.Secret, declared = variable.Declared, values, effective };
        }
    }
}
