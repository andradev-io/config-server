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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ConfigServer.Application.Api.Controllers
{
    /// <summary>
    /// Shared plumbing of the controllers that drive the identity provider (<see cref="IIdentityAdmin"/>): the 501 gate when
    /// management is off, the mapping of <see cref="IdentityAdminException"/> to {error} responses, and the check that a
    /// named application or environment exists.
    /// </summary>
    public abstract class IdentityAdminControllerBase : ControllerBase
    {
        private readonly IConfigCatalog _catalog;

        protected IdentityAdminControllerBase(IIdentityAdmin admin, IConfigCatalog catalog)
        {
            Admin = admin;
            _catalog = catalog;
        }

        protected IIdentityAdmin Admin { get; }

        /// <summary>501 when management is disabled; maps <see cref="IdentityAdminException"/> to {error} responses.</summary>
        protected async Task<IActionResult> Run(Func<Task<IActionResult>> action, CancellationToken cancellationToken)
        {
            var info = await Admin.GetInfoAsync(cancellationToken);
            if (!info.Enabled)
                return StatusCode(StatusCodes.Status501NotImplemented, Error(info.Note ?? DisabledIdentityAdmin.DisabledMessage));
            try
            {
                return await action();
            }
            catch (IdentityAdminException ex)
            {
                var status = ex.Kind switch
                {
                    IdentityAdminErrorKind.NotFound => StatusCodes.Status404NotFound,
                    IdentityAdminErrorKind.Invalid => StatusCodes.Status400BadRequest,
                    IdentityAdminErrorKind.NotConfigured => StatusCodes.Status501NotImplemented,
                    // The identity provider (or the server's settings for it) failed: not the caller's fault.
                    _ => StatusCodes.Status502BadGateway
                };
                return StatusCode(status, Error(ex.Message));
            }
        }

        /// <summary>
        /// A named application or environment must exist in the config source (exact case: role names are case-sensitive).
        /// Skipped when the source cannot enumerate its files.
        /// </summary>
        protected async Task<IActionResult> ValidateKnownNamesAsync(ConfigGrant grant, CancellationToken cancellationToken)
        {
            const string w = AccessGrant.Wildcard;
            if (_catalog == null || (grant.App == w && grant.Environment == w)) return null;
            try
            {
                if (grant.App != w)
                {
                    var apps = await _catalog.ListAppsAsync(cancellationToken) ?? Array.Empty<string>();
                    if (!apps.Contains(grant.App, StringComparer.Ordinal)) return BadRequest(Error(Unknown("application", grant.App, apps)));
                }
                if (grant.Environment != w)
                {
                    var registry = await ConfigCatalogReader.ReadRegistryAsync(_catalog, cancellationToken);
                    var overrides = await ConfigCatalogReader.ReadOverrideNamesAsync(_catalog, cancellationToken);
                    var environments = registry.Select(e => e.Name).Concat(overrides.Values.SelectMany(v => v)).Distinct(StringComparer.Ordinal).ToList();
                    if (!environments.Contains(grant.Environment, StringComparer.Ordinal))
                        return BadRequest(Error(Unknown("environment", grant.Environment, environments)));
                }
                return null;
            }
            catch (ConfigFormatException ex)
            {
                return BadRequest(Error($"Cannot check the environment name: {ex.Message}"));
            }
        }

        private static string Unknown(string kind, string name, IEnumerable<string> known)
        {
            var near = known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            return near == null
                ? $"Unknown {kind} '{name}'."
                : $"Unknown {kind} '{name}' (names are case-sensitive; did you mean '{near}'?).";
        }

        protected string Actor() => User.Identity?.Name ?? ConfigPrincipal.Name(User) ?? "unknown";

        protected static object Error(string message) => new { error = message };
    }
}
