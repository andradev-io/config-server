using System;
using System.IO;
using ConfigServer.Application.Api.Authentication;
using ConfigServer.Application.Api.Configuration;
using ConfigServer.Application.Api.IdentityAdmin;
using Microsoft.Extensions.Configuration;

namespace ConfigServer.Application.Api.Demo
{
    /// <summary>
    /// "Demo" section: a public sandbox where every signed-in visitor gets an isolated, disposable copy of the sample data.
    /// Load with <see cref="Load"/> and check with <see cref="Validate"/> (throws on startup).
    /// </summary>
    public sealed class DemoOptions
    {
        public const string SectionName = "Demo";

        public const string DefaultNotice =
            "This is a public sandbox: your changes live in a private workspace that is deleted after a while. " +
            "Never enter real secrets or real configuration.";

        public bool Enabled { get; set; }

        /// <summary>Read-only seed directory (same layout as the config source). Never written to.</summary>
        public string TemplatePath { get; set; }

        /// <summary>Root of the per-visitor workspaces (one sub-directory per visitor).</summary>
        public string WorkspacesPath { get; set; }

        public int IdleTtlMinutes { get; set; } = 120;

        public int MaxWorkspaces { get; set; } = 200;

        public long MaxBytesPerWorkspace { get; set; } = 2 * 1024 * 1024;

        public int MaxFilesPerWorkspace { get; set; } = 100;

        public int MaxApiKeysPerWorkspace { get; set; } = 20;

        /// <summary>Requests per minute per visitor (per client address while anonymous).</summary>
        public int RequestsPerMinute { get; set; } = 300;

        public long MaxRequestBodyBytes { get; set; } = 256 * 1024;

        public string Notice { get; set; } = DefaultNotice;

        public TimeSpan IdleTtl => TimeSpan.FromMinutes(IdleTtlMinutes);

        public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue<bool>(SectionName + ":Enabled");

        public static DemoOptions Load(IConfiguration configuration)
        {
            var section = configuration.GetSection(SectionName);
            var defaults = new DemoOptions();
            return new DemoOptions
            {
                Enabled = section.GetValue<bool>("Enabled"),
                TemplatePath = Blank(section["TemplatePath"]),
                WorkspacesPath = Blank(section["WorkspacesPath"]),
                IdleTtlMinutes = section.GetValue("IdleTtlMinutes", defaults.IdleTtlMinutes),
                MaxWorkspaces = section.GetValue("MaxWorkspaces", defaults.MaxWorkspaces),
                MaxBytesPerWorkspace = section.GetValue("MaxBytesPerWorkspace", defaults.MaxBytesPerWorkspace),
                MaxFilesPerWorkspace = section.GetValue("MaxFilesPerWorkspace", defaults.MaxFilesPerWorkspace),
                MaxApiKeysPerWorkspace = section.GetValue("MaxApiKeysPerWorkspace", defaults.MaxApiKeysPerWorkspace),
                RequestsPerMinute = section.GetValue("RequestsPerMinute", defaults.RequestsPerMinute),
                MaxRequestBodyBytes = section.GetValue("MaxRequestBodyBytes", defaults.MaxRequestBodyBytes),
                Notice = Blank(section["Notice"]) ?? DefaultNotice
            };
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> naming the setting to fix. The sandbox must never run without real
        /// authentication, nor next to real data or a real identity provider.
        /// </summary>
        public void Validate(AuthOptions auth, IdentityAdminOptions identityAdmin, IConfiguration configuration)
        {
            if (!Enabled) return;

            if (auth.Mode != AuthMode.Oidc)
                throw new InvalidOperationException("Demo:Enabled requires Auth:Mode=Oidc: every visitor needs their own identity (token 'sub') to get a workspace.");
            if (identityAdmin.Provider != IdentityAdminProvider.None)
                throw new InvalidOperationException("Demo:Enabled cannot be combined with IdentityAdmin:Provider=" + identityAdmin.Provider +
                    ": the sandbox simulates identity management and must never touch a real identity provider. Set IdentityAdmin:Provider=None.");

            const string d = SectionName + ":";
            if (TemplatePath == null) throw new InvalidOperationException(d + "TemplatePath is required (read-only directory with the sample apps).");
            if (WorkspacesPath == null) throw new InvalidOperationException(d + "WorkspacesPath is required (writable directory for the visitors' workspaces).");

            var template = Full(TemplatePath);
            var workspaces = Full(WorkspacesPath);
            if (!Directory.Exists(template))
                throw new InvalidOperationException($"{d}TemplatePath '{TemplatePath}' does not exist or is not a directory.");
            RequireDisjoint(template, workspaces, d + "TemplatePath", d + "WorkspacesPath");

            var source = Blank(configuration[ConfigSourceServiceCollectionExtensions.PathKey]);
            if (source != null)
            {
                var sourceFull = Full(source);
                RequireDisjoint(sourceFull, workspaces, ConfigSourceServiceCollectionExtensions.PathKey, d + "WorkspacesPath");
                RequireDisjoint(sourceFull, template, ConfigSourceServiceCollectionExtensions.PathKey, d + "TemplatePath");
            }

            if (IdleTtlMinutes < 1) throw new InvalidOperationException(d + "IdleTtlMinutes must be at least 1.");
            if (MaxWorkspaces < 1) throw new InvalidOperationException(d + "MaxWorkspaces must be at least 1.");
            if (MaxBytesPerWorkspace < 1) throw new InvalidOperationException(d + "MaxBytesPerWorkspace must be at least 1.");
            if (MaxFilesPerWorkspace < 1) throw new InvalidOperationException(d + "MaxFilesPerWorkspace must be at least 1.");
            if (MaxApiKeysPerWorkspace < 0) throw new InvalidOperationException(d + "MaxApiKeysPerWorkspace cannot be negative.");
            if (RequestsPerMinute < 1) throw new InvalidOperationException(d + "RequestsPerMinute must be at least 1.");
            if (MaxRequestBodyBytes < 1) throw new InvalidOperationException(d + "MaxRequestBodyBytes must be at least 1.");
        }

        public static string Full(string path)
            => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static void RequireDisjoint(string a, string b, string aName, string bName)
        {
            if (Contains(a, b) || Contains(b, a))
                throw new InvalidOperationException($"{aName} '{a}' and {bName} '{b}' must be separate directories (neither may be inside the other).");
        }

        private static bool Contains(string parent, string child)
            => string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
               || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
