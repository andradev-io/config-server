using System;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Configuration.Formats;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConfigServer.Application.Api.Controllers
{
    [ApiController]
    [Authorize(Policy = ConfigPolicies.Read)]
    public class ConfigController : ControllerBase
    {
        private readonly IConfigSource _source;

        public ConfigController(IConfigSource source)
        {
            _source = source;
        }

        /// <summary>
        /// Default config for the application deep-merged with the environment override, rendered as
        /// json (default), yaml or env. 404 if either file is missing; 400 for bad names, unknown format or invalid content.
        /// </summary>
        [HttpGet("config/{application}/{environment}")]
        public async Task<IActionResult> Get(string application, string environment, [FromQuery] string format = null, CancellationToken cancellationToken = default)
        {
            if (!IsValidName(application) || !IsValidName(environment))
                return BadRequest("Invalid application or environment name.");

            var output = ConfigFormat.Json;
            if (!string.IsNullOrEmpty(format) && !ConfigFormatExtensions.TryParseName(format, out output))
                return BadRequest("Unknown format. Use json, yaml or env.");

            var defaults = await _source.GetDefaultAsync(application, cancellationToken);
            if (defaults == null) return NotFound();

            var overrides = await _source.GetOverrideAsync(application, environment, cancellationToken);
            // Unknown environment (e.g. typo "prod" vs "prd") must not silently fall back to the defaults.
            if (overrides == null) return NotFound();

            try
            {
                var baseTree = Parse(defaults, "default");
                var overrideTree = Parse(overrides, environment);
                var merged = ConfigMerger.MergeTrees(baseTree, overrideTree);
                return Content(output.Render(merged), output.ContentType());
            }
            catch (ConfigFormatException ex)
            {
                return BadRequest(new { error = ex.Message, line = ex.Line, column = ex.Column });
            }
        }

        private static System.Text.Json.Nodes.JsonNode Parse(ConfigDocument document, string label)
        {
            try
            {
                return document.Format.Parse(document.Content);
            }
            catch (ConfigFormatException ex)
            {
                throw ex.WithPrefix($"{label} ({document.Format.ToName()}): ");
            }
        }

        private static bool IsValidName(string name)
            => !string.IsNullOrWhiteSpace(name)
               && !name.Contains("..", StringComparison.Ordinal)
               && name.IndexOf('/') < 0
               && name.IndexOf('\\') < 0;
    }
}
