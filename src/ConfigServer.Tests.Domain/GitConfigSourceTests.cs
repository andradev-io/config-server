using System;
using System.IO;
using System.Threading.Tasks;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Git;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Domain
{
    [TestClass]
    public class GitConfigSourceTests
    {
        private string _root;
        private string _origin;
        private string _clone;
        private string _branch;
        private static readonly Signature Author = new Signature("test", "test@example.com", DateTimeOffset.UtcNow);

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "cfgsrv-" + Guid.NewGuid().ToString("N"));
            _origin = Path.Combine(_root, "origin");
            _clone = Path.Combine(_root, "clone");
            Directory.CreateDirectory(_origin);
            Repository.Init(_origin);
            WriteFile("app/default.json", "{\"a\":1}");
            WriteFile("app/dev.json", "{\"a\":2}");
            Commit("initial");
            using var repo = new Repository(_origin);
            _branch = repo.Head.FriendlyName;
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (!Directory.Exists(_root)) return;
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        private void WriteFile(string relative, string content)
        {
            var path = Path.Combine(_origin, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        private void Commit(string message)
        {
            using var repo = new Repository(_origin);
            Commands.Stage(repo, "*");
            repo.Commit(message, Author, Author);
        }

        private GitConfigSource CreateSource(TimeSpan? refresh = null, ISecretResolver resolver = null) =>
            new GitConfigSource(new GitConfigSourceOptions
            {
                RepositoryUrl = _origin,
                Branch = _branch,
                LocalPath = _clone,
                RefreshInterval = refresh ?? TimeSpan.Zero
            }, resolver);

        [TestMethod]
        public async Task GetDefaultAsync_ReturnsDefaultFile()
        {
            using var source = CreateSource();
            Assert.AreEqual("{\"a\":1}", (await source.GetDefaultAsync("app")).Content);
        }

        [TestMethod]
        public async Task GetOverrideAsync_ReturnsEnvironmentFile()
        {
            using var source = CreateSource();
            Assert.AreEqual("{\"a\":2}", (await source.GetOverrideAsync("app", "dev")).Content);
        }

        [TestMethod]
        public async Task MissingFiles_ReturnNull()
        {
            using var source = CreateSource();
            Assert.IsNull(await source.GetOverrideAsync("app", "prod"));
            Assert.IsNull(await source.GetDefaultAsync("unknown"));
        }

        [TestMethod]
        public async Task LookupOrder_FindsYamlAndEnv_JsonWins()
        {
            WriteFile("app/staging.yml", "a: 5");
            WriteFile("app/qa.env", "A=7");
            WriteFile("app/both.yaml", "a: 1");
            WriteFile("app/both.json", "{\"a\":2}");
            Commit("formats");
            using var source = CreateSource();

            var yml = await source.GetOverrideAsync("app", "staging");
            Assert.AreEqual(ConfigServer.Domain.Configuration.Formats.ConfigFormat.Yaml, yml.Format);
            var env = await source.GetOverrideAsync("app", "qa");
            Assert.AreEqual(ConfigServer.Domain.Configuration.Formats.ConfigFormat.Env, env.Format);
            var both = await source.GetOverrideAsync("app", "both");
            Assert.AreEqual(ConfigServer.Domain.Configuration.Formats.ConfigFormat.Json, both.Format);
        }

        [TestMethod]
        [DataRow("..")]
        [DataRow("../x")]
        [DataRow("a/b")]
        [DataRow("a\\b")]
        [DataRow("a..b")]
        public void InvalidNames_Throw(string name)
        {
            using var source = CreateSource();
            Assert.Throws<ArgumentException>(() => source.GetDefaultAsync(name));
            Assert.Throws<ArgumentException>(() => source.GetOverrideAsync("app", name));
        }

        [TestMethod]
        public async Task Refresh_PicksUpNewCommit()
        {
            using var source = CreateSource();
            Assert.AreEqual("{\"a\":2}", (await source.GetOverrideAsync("app", "dev")).Content);

            WriteFile("app/dev.json", "{\"a\":3}");
            Commit("update");

            Assert.AreEqual("{\"a\":3}", (await source.GetOverrideAsync("app", "dev")).Content);
        }

        [TestMethod]
        public async Task Refresh_NotBeforeInterval()
        {
            using var source = CreateSource(TimeSpan.FromHours(1));
            Assert.AreEqual("{\"a\":2}", (await source.GetOverrideAsync("app", "dev")).Content);

            WriteFile("app/dev.json", "{\"a\":3}");
            Commit("update");

            Assert.AreEqual("{\"a\":2}", (await source.GetOverrideAsync("app", "dev")).Content);
        }

        [TestMethod]
        public async Task SecretResolver_IsCalledOnFileContent()
        {
            using var source = CreateSource(resolver: new UpperResolver());
            Assert.AreEqual("{\"A\":1}", (await source.GetDefaultAsync("app")).Content);
        }

        [TestMethod]
        public void AddGitConfigSource_RegistersIConfigSource()
        {
            var services = new ServiceCollection();
            services.AddGitConfigSource(o => { o.RepositoryUrl = _origin; o.LocalPath = _clone; o.Branch = _branch; });
            using var provider = services.BuildServiceProvider();
            Assert.IsInstanceOfType<GitConfigSource>(provider.GetRequiredService<IConfigSource>());
            Assert.IsInstanceOfType<PassThroughSecretResolver>(provider.GetRequiredService<ISecretResolver>());
        }

        private sealed class UpperResolver : ISecretResolver
        {
            public Task<string> ResolveAsync(string json, System.Threading.CancellationToken ct) =>
                Task.FromResult(json.ToUpperInvariant());
        }
    }
}
