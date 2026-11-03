using System;
using System.Collections.Generic;
using System.Linq;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ConfigServer.Application.Api.Authentication
{
    public enum ScopeRequirement
    {
        /// <summary>The endpoint reads or changes every environment of the route app: the permission must cover (app, *).</summary>
        AllEnvironments,

        /// <summary>The endpoint changes server-wide state: the permission must cover (*, *).</summary>
        Global
    }

    /// <summary>Extra scope requirement checked by <see cref="AccessScopeFilter"/> with the endpoint's permission.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RequireScopeAttribute : Attribute
    {
        public RequireScopeAttribute(ScopeRequirement requirement) => Requirement = requirement;

        public ScopeRequirement Requirement { get; }

        /// <summary>
        /// Only API keys must meet it (their historical contract); users get a view filtered to their environments instead.
        /// </summary>
        public bool ApiKeysOnly { get; set; }
    }

    /// <summary>
    /// Global MVC filter (runs after the coarse policies) enforcing the caller's <see cref="AccessGrants"/> for users and API keys.
    /// The endpoint permission is write when an [Authorize(Policy = config:write)] applies, otherwise read (admin endpoints are
    /// left to the admin policy). Route "app"/"application" needs the permission on at least one environment of that app,
    /// "environment" on (app, environment); "fileName" "{env}.*" on (app, env), while "default.*" needs read on any environment
    /// to be read and write on (app, *) to be changed. <see cref="RequireScopeAttribute"/> adds (app, *) or (*, *) requirements.
    /// Listing endpoints filter their output in the controller.
    /// </summary>
    public sealed class AccessScopeFilter : IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var metadata = context.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<IAllowAnonymous>().Any()) return;

            var permission = RequiredPermission(metadata);
            if (permission == AccessPermission.Admin) return;

            var user = context.HttpContext.User;
            var denied = Check(context, ConfigPrincipal.Grants(user), permission, ConfigPrincipal.IsApiKey(user));
            if (denied != null)
                context.Result = new ObjectResult(new { error = denied }) { StatusCode = StatusCodes.Status403Forbidden };
        }

        public static AccessPermission RequiredPermission(IEnumerable<object> metadata)
        {
            var policies = metadata.OfType<IAuthorizeData>().Select(a => a.Policy).Where(p => p != null).ToList();
            if (policies.Contains(ConfigPolicies.Admin)) return AccessPermission.Admin;
            return policies.Contains(ConfigPolicies.Write) ? AccessPermission.Write : AccessPermission.Read;
        }

        private static string Check(AuthorizationFilterContext context, AccessGrants grants, AccessPermission permission, bool isApiKey)
        {
            var values = context.RouteData.Values;
            var app = Route(values, "app") ?? Route(values, "application");
            var verb = permission.ToName();

            if (app != null)
            {
                if (!grants.CanAnyEnvironment(permission, app))
                    return $"Access denied: no {verb} access to application '{app}'.";

                var environment = Route(values, "environment");
                if (environment != null && !grants.Can(permission, app, environment))
                    return $"Access denied: no {verb} access to environment '{environment}' of application '{app}'.";

                var fileName = Route(values, "fileName");
                if (!string.IsNullOrEmpty(fileName))
                {
                    if (ConfigNames.IsDefault(fileName))
                    {
                        if (permission >= AccessPermission.Write && !grants.CanWriteDefault(app))
                            return $"Changing the default file affects every environment; it needs write access to all environments of '{app}'.";
                    }
                    else if (!grants.Can(permission, app, ConfigNames.LogicalName(fileName)))
                        return $"Access denied: no {verb} access to environment '{ConfigNames.LogicalName(fileName)}' of application '{app}'.";
                }
            }

            foreach (var requirement in context.ActionDescriptor.EndpointMetadata.OfType<RequireScopeAttribute>())
            {
                if (requirement.ApiKeysOnly && !isApiKey) continue;
                if (requirement.Requirement == ScopeRequirement.Global && !grants.HasGlobal(permission))
                    return $"This operation changes server-wide state; it needs {verb} access to every application and environment.";
                if (requirement.Requirement == ScopeRequirement.AllEnvironments && !CoversAllEnvironments(grants, permission, app))
                    return isApiKey && requirement.ApiKeysOnly
                        ? "This endpoint spans every environment of an app; it needs an API key with environments [\"*\"]."
                        : $"This operation spans every environment; it needs {verb} access to all environments of '{app ?? "*"}'.";
            }
            return null;
        }

        /// <summary>(app, *) when the route names an app; otherwise at least one grant on all environments of some app.</summary>
        private static bool CoversAllEnvironments(AccessGrants grants, AccessPermission permission, string app)
            => app != null
                ? grants.CanAllEnvironments(permission, app)
                : grants.Grants.Any(g => g.Permission >= permission && g.Environment == AccessGrant.Wildcard);

        private static string Route(Microsoft.AspNetCore.Routing.RouteValueDictionary values, string name)
            => values.TryGetValue(name, out var value) && value?.ToString() is { Length: > 0 } text ? text : null;
    }
}
