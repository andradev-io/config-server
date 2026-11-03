using ConfigServer.Application.Api.Configuration;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Configuration.Formats;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ConfigServer.Application.Api.Controllers
{
    /// <summary>
    /// Shared helpers for the management endpoints. Reads need an <see cref="IConfigCatalog"/> (501 otherwise);
    /// writes also need "ConfigSource:AllowWrite" (403) and an <see cref="IConfigStore"/> (501).
    /// </summary>
    public abstract class ConfigApiControllerBase : ControllerBase
    {
        private readonly IConfiguration _configuration;

        protected IConfigCatalog Catalog { get; }
        protected IConfigStore Store { get; }

        protected ConfigApiControllerBase(IConfiguration configuration, IConfigCatalog catalog, IConfigStore store)
        {
            _configuration = configuration;
            Catalog = catalog;
            Store = store;
        }

        /// <summary>501 when the active source cannot enumerate its files; otherwise null.</summary>
        protected IActionResult EnsureReadable()
            => Catalog == null ? NotImplementedSource() : null;

        /// <summary>403 when writes are disabled, 501 when there is no catalog or store; otherwise null.</summary>
        protected IActionResult EnsureWritable()
        {
            if (!_configuration.IsWriteAllowed())
                return StatusCode(StatusCodes.Status403Forbidden, Error("Writing configuration files is disabled (ConfigSource:AllowWrite=false)."));
            return Catalog == null || Store == null ? NotImplementedSource() : null;
        }

        protected IActionResult NotImplementedSource()
            => StatusCode(StatusCodes.Status501NotImplemented, Error("The active configuration source does not support this operation."));

        protected static object Error(string message) => new { error = message, line = (int?)null, column = (int?)null };

        protected IActionResult FormatError(ConfigFormatException ex)
            => BadRequest(new { error = ex.Message, line = ex.Line, column = ex.Column });
    }
}
