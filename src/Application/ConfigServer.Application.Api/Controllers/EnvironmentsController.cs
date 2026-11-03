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
    public sealed class ScaffoldRequest
    {
        public List<string> Apps { get; set; }
        public string CopyFrom { get; set; }
    }

    public sealed class CreateEnvironmentRequest
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public int? Order { get; set; }
        public ScaffoldRequest Scaffold { get; set; }
    }

    public sealed class UpdateEnvironmentRequest
    {
        public string Description { get; set; }
        public int? Order { get; set; }
    }

    /// <summary>
    /// Environments as first-class entities: declared ones live in the source root "_environments.json", implicit ones are
    /// inferred from override file names. These endpoints expose the structure of configs: require authentication + TLS
    /// before non-local use. Writes are gated by "ConfigSource:AllowWrite".
    /// </summary>
    [ApiController]
    [Route("api/environments")]
    [Authorize(Policy = ConfigPolicies.Read)]
    public class EnvironmentsController : ConfigApiControllerBase
    {
        // Registry updates are read-modify-write on one file.
        private static readonly SemaphoreSlim RegistryGate = new SemaphoreSlim(1, 1);

        public EnvironmentsController(IConfiguration configuration, IConfigCatalog catalog = null, IConfigStore store = null)
            : base(configuration, catalog, store)
        {
        }

        [HttpGet]
        public async Task<IActionResult> List(CancellationToken cancellationToken)
        {
            var unavailable = EnsureReadable();
            if (unavailable != null) return unavailable;
            try
            {
                var registry = await ConfigCatalogReader.ReadRegistryAsync(Catalog, cancellationToken);
                var overrides = await ConfigCatalogReader.ReadOverrideNamesAsync(Catalog, cancellationToken);
                // Callers only see the override files they can read (appCount counts those) and the environments they
                // have a grant on in at least one app.
                var grants = ConfigPrincipal.Grants(User);
                var visible = overrides
                    .Select(p => (App: p.Key, Names: grants.AllowedEnvironments(p.Key, p.Value).ToList()))
                    .Where(p => p.Names.Count > 0)
                    .ToDictionary(p => p.App, p => p.Names, StringComparer.Ordinal);
                return Ok(EnvironmentCatalog.Build(registry, visible).Where(e => grants.CanAnyApp(AccessPermission.Read, e.Name)).ToList());
            }
            catch (ConfigFormatException ex)
            {
                return FormatError(ex);
            }
        }

        [HttpPost]
        [Authorize(Policy = ConfigPolicies.Write)]
        [RequireScope(ScopeRequirement.Global)]
        public async Task<IActionResult> Create([FromBody] CreateEnvironmentRequest body, CancellationToken cancellationToken)
        {
            var nameError = ValidateName(body?.Name);
            if (nameError != null) return nameError;
            var forbidden = EnsureWritable();
            if (forbidden != null) return forbidden;

            await RegistryGate.WaitAsync(cancellationToken);
            try
            {
                List<EnvironmentDefinition> registry;
                Dictionary<string, List<string>> overrides;
                try
                {
                    registry = await ConfigCatalogReader.ReadRegistryAsync(Catalog, cancellationToken);
                    overrides = await ConfigCatalogReader.ReadOverrideNamesAsync(Catalog, cancellationToken);
                }
                catch (ConfigFormatException ex)
                {
                    return FormatError(ex);
                }

                if (Exists(registry, overrides, body.Name))
                    return Conflict(Error($"Environment '{body.Name}' already exists. Use PUT api/environments/{{name}} to declare an inferred one."));

                var scaffoldApps = await ValidateScaffoldAsync(body.Scaffold, cancellationToken);
                if (scaffoldApps.Error != null) return scaffoldApps.Error;

                var created = new EnvironmentDefinition
                {
                    Name = body.Name,
                    Description = body.Description ?? string.Empty,
                    Order = body.Order ?? (registry.Count == 0 ? 1 : registry.Max(e => e.Order) + 1)
                };
                registry.Add(created);
                await Store.WriteReservedAsync(null, ConfigNames.EnvironmentsFileName, ReservedFiles.SerializeEnvironments(registry), cancellationToken);

                var scaffolded = new List<string>();
                foreach (var app in scaffoldApps.Apps)
                    if (await ScaffoldAsync(app, body.Name, body.Scaffold.CopyFrom, cancellationToken)) scaffolded.Add(app);

                var appCount = overrides.Values.Count(list => list.Contains(body.Name, StringComparer.OrdinalIgnoreCase)) + scaffolded.Count;
                return Created($"api/environments/{body.Name}",
                    new { name = created.Name, description = created.Description, order = created.Order, declared = true, appCount, scaffolded });
            }
            finally
            {
                RegistryGate.Release();
            }
        }

        [HttpPut("{name}")]
        [Authorize(Policy = ConfigPolicies.Write)]
        [RequireScope(ScopeRequirement.Global)]
        public async Task<IActionResult> Update(string name, [FromBody] UpdateEnvironmentRequest body, CancellationToken cancellationToken)
        {
            var nameError = ValidateName(name);
            if (nameError != null) return nameError;
            if (body == null) return BadRequest(Error("Body must be a JSON object with 'description' and 'order'."));
            var forbidden = EnsureWritable();
            if (forbidden != null) return forbidden;

            await RegistryGate.WaitAsync(cancellationToken);
            try
            {
                List<EnvironmentDefinition> registry;
                Dictionary<string, List<string>> overrides;
                try
                {
                    registry = await ConfigCatalogReader.ReadRegistryAsync(Catalog, cancellationToken);
                    overrides = await ConfigCatalogReader.ReadOverrideNamesAsync(Catalog, cancellationToken);
                }
                catch (ConfigFormatException ex)
                {
                    return FormatError(ex);
                }

                var entry = registry.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    var inferred = overrides.Values.SelectMany(v => v).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    if (inferred == null) return NotFound(Error("Environment not found."));
                    entry = new EnvironmentDefinition { Name = inferred, Order = registry.Count == 0 ? 1 : registry.Max(e => e.Order) + 1 };
                    registry.Add(entry);
                }

                entry.Description = body.Description ?? string.Empty;
                if (body.Order.HasValue) entry.Order = body.Order.Value;
                await Store.WriteReservedAsync(null, ConfigNames.EnvironmentsFileName, ReservedFiles.SerializeEnvironments(registry), cancellationToken);
                return NoContent();
            }
            finally
            {
                RegistryGate.Release();
            }
        }

        [HttpDelete("{name}")]
        [Authorize(Policy = ConfigPolicies.Write)]
        [RequireScope(ScopeRequirement.Global)]
        public async Task<IActionResult> Delete(string name, [FromQuery] bool deleteFiles = false, CancellationToken cancellationToken = default)
        {
            var nameError = ValidateName(name);
            if (nameError != null) return nameError;
            var forbidden = EnsureWritable();
            if (forbidden != null) return forbidden;

            await RegistryGate.WaitAsync(cancellationToken);
            try
            {
                List<EnvironmentDefinition> registry;
                Dictionary<string, List<string>> overrides;
                try
                {
                    registry = await ConfigCatalogReader.ReadRegistryAsync(Catalog, cancellationToken);
                    overrides = await ConfigCatalogReader.ReadOverrideNamesAsync(Catalog, cancellationToken);
                }
                catch (ConfigFormatException ex)
                {
                    return FormatError(ex);
                }

                var declared = registry.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
                var inferred = overrides.Values.Any(list => list.Contains(name, StringComparer.OrdinalIgnoreCase));
                if (!declared && !inferred) return NotFound(Error("Environment not found."));
                if (!declared && !deleteFiles)
                    return Conflict(Error("The environment is only inferred from override files; use deleteFiles=true to delete them."));

                if (deleteFiles)
                {
                    foreach (var app in overrides.Where(p => p.Value.Contains(name, StringComparer.OrdinalIgnoreCase)).Select(p => p.Key))
                    {
                        var files = await Catalog.ListFilesAsync(app, cancellationToken) ?? Array.Empty<ConfigFileInfo>();
                        foreach (var file in files.Where(f => !ConfigNames.IsDefault(f.Name)
                                     && string.Equals(ConfigNames.LogicalName(f.Name), name, StringComparison.OrdinalIgnoreCase)))
                            await Store.DeleteAsync(app, file.Name, cancellationToken);
                    }
                }

                if (declared)
                    await Store.WriteReservedAsync(null, ConfigNames.EnvironmentsFileName, ReservedFiles.SerializeEnvironments(registry), cancellationToken);
                return NoContent();
            }
            finally
            {
                RegistryGate.Release();
            }
        }

        private IActionResult ValidateName(string name)
            => ConfigNames.IsValidEnvironmentName(name)
                ? null
                : BadRequest(Error("Invalid environment name. Use letters, digits, '_' and '-' (starting with a letter or digit); 'default' is reserved."));

        private static bool Exists(IEnumerable<EnvironmentDefinition> registry, Dictionary<string, List<string>> overrides, string name)
            => registry.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
               || overrides.Values.Any(list => list.Contains(name, StringComparer.OrdinalIgnoreCase));

        private async Task<(IActionResult Error, IReadOnlyList<string> Apps)> ValidateScaffoldAsync(ScaffoldRequest scaffold, CancellationToken cancellationToken)
        {
            if (scaffold?.Apps == null || scaffold.Apps.Count == 0) return (null, Array.Empty<string>());
            if (scaffold.CopyFrom != null && !ConfigNames.IsValidEnvironmentName(scaffold.CopyFrom))
                return (BadRequest(Error("Invalid scaffold.copyFrom environment name.")), null);

            var known = await Catalog.ListAppsAsync(cancellationToken);
            var apps = scaffold.Apps.Distinct(StringComparer.Ordinal).ToList();
            foreach (var app in apps)
            {
                if (!ConfigNames.IsValidAppName(app)) return (BadRequest(Error($"Invalid application name '{app}' in scaffold.apps.")), null);
                if (!known.Contains(app, StringComparer.Ordinal)) return (BadRequest(Error($"Unknown application '{app}' in scaffold.apps.")), null);
            }
            return (null, apps);
        }

        /// <summary>Creates {name}.json ({}) or a copy of the copyFrom file; skips apps that already have an override with that name.</summary>
        private async Task<bool> ScaffoldAsync(string app, string name, string copyFrom, CancellationToken cancellationToken)
        {
            var files = await Catalog.ListFilesAsync(app, cancellationToken) ?? Array.Empty<ConfigFileInfo>();
            bool Named(ConfigFileInfo f, string env) => !ConfigNames.IsDefault(f.Name)
                && string.Equals(ConfigNames.LogicalName(f.Name), env, StringComparison.OrdinalIgnoreCase);
            if (files.Any(f => Named(f, name))) return false;

            var fileName = name + ".json";
            var content = "{}\n";
            var source = copyFrom == null ? null : files.FirstOrDefault(f => Named(f, copyFrom));
            if (source != null)
            {
                var text = await Catalog.ReadAsync(app, source.Name, cancellationToken);
                if (text != null)
                {
                    fileName = name + source.Name.Substring(source.Name.LastIndexOf('.'));
                    content = text;
                }
            }
            return await Store.WriteAsync(app, fileName, content, cancellationToken) == ConfigWriteResult.Created;
        }
    }
}
