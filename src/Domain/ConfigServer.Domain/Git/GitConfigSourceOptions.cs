using System;

namespace ConfigServer.Domain.Git
{
    public class GitConfigSourceOptions
    {
        /// <summary>URL (or local path) of the config repository.</summary>
        public string RepositoryUrl { get; set; }

        /// <summary>Branch to track.</summary>
        public string Branch { get; set; } = "main";

        /// <summary>Directory where the repository is cloned.</summary>
        public string LocalPath { get; set; }

        /// <summary>Optional username for authentication.</summary>
        public string Username { get; set; }

        /// <summary>Optional token/password for authentication. Never logged.</summary>
        public string Token { get; set; }

        /// <summary>Minimum time between fetches.</summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

        // Keeps the token out of accidental logging of the options.
        public override string ToString() =>
            $"GitConfigSourceOptions {{ RepositoryUrl = {RepositoryUrl}, Branch = {Branch}, LocalPath = {LocalPath} }}";
    }
}
