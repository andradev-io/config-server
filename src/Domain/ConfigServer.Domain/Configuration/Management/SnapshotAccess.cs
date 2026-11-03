using System.Linq;
using ConfigServer.Domain.Security;

namespace ConfigServer.Domain.Configuration.Management
{
    /// <summary>Limits snapshots to what a caller may read (see <see cref="AccessGrants"/>).</summary>
    public static class SnapshotAccess
    {
        /// <summary>
        /// Apps with at least one readable environment; each keeps its default file (part of every merged config), its
        /// declared variables and only the override files of readable environments. Warnings of hidden apps/files are dropped.
        /// </summary>
        public static ConfigSnapshot Restrict(ConfigSnapshot snapshot, AccessGrants grants)
        {
            if (snapshot == null || grants == null || grants.HasGlobal(AccessPermission.Read)) return snapshot;
            return new ConfigSnapshot
            {
                Registry = snapshot.Registry,
                Apps = snapshot.Apps.Where(a => grants.CanReadDefault(a.Name)).Select(a => Restrict(a, grants)).ToList(),
                Warnings = snapshot.Warnings.Where(w => IsVisible(w, grants)).ToList()
            };
        }

        /// <summary>The app with only the override files of environments the caller can read.</summary>
        public static AppSnapshot Restrict(AppSnapshot app, AccessGrants grants)
        {
            if (app == null || grants == null || grants.CanAllEnvironments(AccessPermission.Read, app.Name)) return app;
            return new AppSnapshot
            {
                Name = app.Name,
                Default = app.Default,
                Overrides = app.Overrides.Where(o => grants.Can(AccessPermission.Read, app.Name, o.Environment)).ToList(),
                Declared = app.Declared,
                VariablesError = app.VariablesError
            };
        }

        /// <summary>Registry warnings (no app) stay; app warnings need read on the app, and on the environment for override files.</summary>
        public static bool IsVisible(SnapshotWarning warning, AccessGrants grants)
        {
            if (warning == null) return false;
            if (warning.App == null) return true;
            if (!grants.CanReadDefault(warning.App)) return false;
            var file = warning.File ?? string.Empty;
            if (ConfigNames.IsReservedFileName(file) || ConfigNames.IsDefault(file)) return true;
            return grants.Can(AccessPermission.Read, warning.App, ConfigNames.LogicalName(file));
        }
    }
}
