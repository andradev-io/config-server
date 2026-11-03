using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Configuration.Formats;
using ConfigServer.Domain.Configuration.Management;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Domain
{
    /// <summary>In-memory catalog: key "app/file" (or "_environments.json" at the root) -> text.</summary>
    internal sealed class FakeCatalog : IConfigCatalog
    {
        public Dictionary<string, string> Files { get; } = new Dictionary<string, string>();

        public Task<IReadOnlyList<string>> ListAppsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Files.Keys.Where(k => k.Contains('/')).Select(k => k.Split('/')[0]).Distinct().OrderBy(k => k).ToList());

        public Task<IReadOnlyList<ConfigFileInfo>> ListFilesAsync(string application, CancellationToken cancellationToken = default)
        {
            var files = Files.Where(p => p.Key.StartsWith(application + "/") && ConfigNames.IsValidFileName(p.Key.Substring(application.Length + 1)))
                .Select(p => new ConfigFileInfo(p.Key.Substring(application.Length + 1), ConfigFormatExtensions.FromFileName(p.Key).Value, p.Value.Length))
                .OrderBy(f => ConfigNames.IsDefault(f.Name) ? 0 : 1).ThenBy(f => f.Name).ToList();
            return Task.FromResult<IReadOnlyList<ConfigFileInfo>>(files.Count == 0 && !Files.Keys.Any(k => k.StartsWith(application + "/")) ? null : files);
        }

        public Task<string> ReadAsync(string application, string fileName, CancellationToken cancellationToken = default)
            => Task.FromResult(Files.TryGetValue(application + "/" + fileName, out var text) ? text : null);

        public Task<string> ReadReservedAsync(string application, string fileName, CancellationToken cancellationToken = default)
            => Task.FromResult(Files.TryGetValue(application == null ? fileName : application + "/" + fileName, out var text) ? text : null);
    }

    [TestClass]
    public class ConfigTreeTests
    {
        [TestMethod]
        public void Flatten_walks_objects_and_keeps_arrays_and_empty_objects_as_leaves()
        {
            var tree = JsonNode.Parse("{\"a\":{\"b\":1,\"c\":{\"d\":\"x\"}},\"list\":[1,2],\"empty\":{},\"n\":null}");
            var leaves = ConfigTree.Flatten(tree);

            CollectionAssert.AreEquivalent(new[] { "a.b", "a.c.d", "list", "empty", "n" }, leaves.Keys.ToArray());
            Assert.AreEqual("array", ConfigTree.TypeOf(leaves["list"]));
        }

        [TestMethod]
        public void Flatten_is_consistent_across_formats()
        {
            var json = ConfigFormat.Json.Parse("{\"Db\":{\"Host\":\"h\",\"Ports\":[1,2]}}");
            var yaml = ConfigFormat.Yaml.Parse("Db:\n  Host: h\n  Ports: [1, 2]\n");
            var env = ConfigFormat.Env.Parse("Db__Host=h\nDb__Ports__0=1\nDb__Ports__1=2\n");

            CollectionAssert.AreEqual(ConfigTree.Flatten(json).Keys.ToArray(), ConfigTree.Flatten(yaml).Keys.ToArray());
            CollectionAssert.AreEqual(ConfigTree.Flatten(json).Keys.ToArray(), ConfigTree.Flatten(env).Keys.ToArray());
        }

        [TestMethod]
        public void TryGetPath_resolves_nested_and_non_leaf_nodes()
        {
            var tree = JsonNode.Parse("{\"a\":{\"b\":{\"c\":1}},\"z\":null}");

            Assert.IsTrue(ConfigTree.TryGetPath(tree, "a.b.c", out var leaf));
            Assert.AreEqual(1, leaf.GetValue<int>());
            Assert.IsTrue(ConfigTree.TryGetPath(tree, "a.b", out var obj));
            Assert.IsInstanceOfType<JsonObject>(obj);
            Assert.IsTrue(ConfigTree.TryGetPath(tree, "z", out var nul));
            Assert.IsNull(nul);
            Assert.IsFalse(ConfigTree.TryGetPath(tree, "a.x", out _));
        }

        [TestMethod]
        public void Matches_accepts_env_strings_for_number_and_boolean()
        {
            Assert.IsTrue(ConfigTree.Matches("number", JsonValue.Create("42")));
            Assert.IsTrue(ConfigTree.Matches("number", JsonValue.Create("4.5")));
            Assert.IsFalse(ConfigTree.Matches("number", JsonValue.Create("abc")));
            Assert.IsTrue(ConfigTree.Matches("boolean", JsonValue.Create("TRUE")));
            Assert.IsFalse(ConfigTree.Matches("boolean", JsonValue.Create("yes")));
            Assert.IsTrue(ConfigTree.Matches("number", JsonValue.Create(3)));
            Assert.IsFalse(ConfigTree.Matches("string", JsonValue.Create(3)));
            Assert.IsFalse(ConfigTree.Matches("string", null));
            Assert.IsTrue(ConfigTree.Matches("array", new JsonArray(1)));
            Assert.IsTrue(ConfigTree.Matches("object", new JsonObject()));
        }

        [TestMethod]
        public void IsPlaceholder_recognizes_references_and_empty_values()
        {
            Assert.IsTrue(ConfigTree.IsPlaceholder(JsonValue.Create("${DB_PASSWORD}")));
            Assert.IsTrue(ConfigTree.IsPlaceholder(JsonValue.Create("secret://vault/db")));
            Assert.IsTrue(ConfigTree.IsPlaceholder(JsonValue.Create("  ")));
            Assert.IsTrue(ConfigTree.IsPlaceholder(null));
            Assert.IsFalse(ConfigTree.IsPlaceholder(JsonValue.Create("hunter2")));
            Assert.IsFalse(ConfigTree.IsPlaceholder(JsonValue.Create(12)));
        }
    }

    [TestClass]
    public class ReservedFilesTests
    {
        [TestMethod]
        public void Variables_round_trip_keeps_null_environments()
        {
            var text = ReservedFiles.SerializeVariables(new[]
            {
                new VariableDefinition { Key = "a.b", Type = "string", Required = true, Description = "d" }
            });
            StringAssert.Contains(text, "\"environments\": null");

            var parsed = ReservedFiles.ParseVariables(text);
            Assert.AreEqual(1, parsed.Count);
            Assert.AreEqual("a.b", parsed[0].Key);
            Assert.IsNull(parsed[0].Environments);
        }

        [TestMethod]
        public void Parse_reports_invalid_json()
        {
            var ex = Assert.Throws<ConfigFormatException>(() => ReservedFiles.ParseVariables("{ not json"));
            StringAssert.Contains(ex.Message, "_variables.json");
        }

        [TestMethod]
        public void Validate_rejects_bad_declarations()
        {
            var envs = new[] { "dev" };
            VariableDefinition V(string key, string type = "string", params string[] on)
                => new VariableDefinition { Key = key, Type = type, Environments = on.Length == 0 ? null : on.ToList() };

            Assert.IsNull(ReservedFiles.ValidateVariables(new List<VariableDefinition> { V("a"), V("b", "number", "DEV") }, envs));
            Assert.IsNotNull(ReservedFiles.ValidateVariables(new List<VariableDefinition> { V("") }, envs));
            Assert.IsNotNull(ReservedFiles.ValidateVariables(new List<VariableDefinition> { V("a"), V("a") }, envs));
            Assert.IsNotNull(ReservedFiles.ValidateVariables(new List<VariableDefinition> { V("a", "int") }, envs));
            Assert.IsNotNull(ReservedFiles.ValidateVariables(new List<VariableDefinition> { V("a", "string", "prod") }, envs));
        }

        [TestMethod]
        public void Environment_names_follow_the_rule()
        {
            Assert.IsTrue(ConfigNames.IsValidEnvironmentName("dev-1_a"));
            Assert.IsFalse(ConfigNames.IsValidEnvironmentName("default"));
            Assert.IsFalse(ConfigNames.IsValidEnvironmentName("DEFAULT"));
            Assert.IsFalse(ConfigNames.IsValidEnvironmentName("-dev"));
            Assert.IsFalse(ConfigNames.IsValidEnvironmentName("_dev"));
            Assert.IsFalse(ConfigNames.IsValidEnvironmentName("a.b"));
        }
    }

    [TestClass]
    public class StatusAndIndexTests
    {
        private static FakeCatalog Sample()
        {
            var c = new FakeCatalog();
            c.Files["_environments.json"] = "{\"environments\":[{\"name\":\"dev\",\"description\":\"Development\",\"order\":1},{\"name\":\"prod\",\"order\":2}]}";
            c.Files["web/default.json"] = "{\"database\":{\"host\":\"localhost\",\"port\":5432,\"password\":\"hunter2-plain\"},\"debug\":false,\"extra\":1}";
            c.Files["web/dev.env"] = "database__port=6000\ndebug=true\n";
            c.Files["web/qa.yaml"] = "database:\n  host: qa-host\n";
            c.Files["web/_variables.json"] = ReservedFiles.SerializeVariables(new[]
            {
                new VariableDefinition { Key = "database.host", Type = "string", Required = true },
                new VariableDefinition { Key = "database.port", Type = "number", Required = true },
                new VariableDefinition { Key = "database.password", Type = "string", Required = true, Secret = true },
                new VariableDefinition { Key = "debug", Type = "boolean", Required = true },
                new VariableDefinition { Key = "api.key", Type = "string", Required = true, Environments = new List<string> { "prod" } },
            });
            c.Files["api/default.json"] = "{\"database\":{\"host\":\"api-db\"},\"flag\":\"${FLAG}\"}";
            c.Files["api/prod.json"] = "{\"database\":{\"host\":\"prod-db\"}}";
            return c;
        }

        [TestMethod]
        public async Task Status_checks_required_variables_per_environment()
        {
            var snapshot = await ConfigCatalogReader.LoadAsync(Sample(), "web");
            var status = StatusChecker.Check(snapshot.Apps.Single(), snapshot.Registry);

            CollectionAssert.AreEqual(new[] { "dev", "prod", "qa" }, status.Environments.Select(e => e.Name).ToArray());

            var dev = status.Environments.Single(e => e.Name == "dev");
            Assert.IsTrue(dev.Ok, "dev.env strings satisfy number/boolean");
            CollectionAssert.AreEqual(new[] { "database.password" }, dev.SecretsInPlainText.ToArray());

            var prod = status.Environments.Single(e => e.Name == "prod");
            CollectionAssert.AreEqual(new[] { "api.key" }, prod.Missing.ToArray());
            Assert.IsFalse(prod.Ok);

            var qa = status.Environments.Single(e => e.Name == "qa");
            Assert.IsTrue(qa.Ok);

            CollectionAssert.AreEqual(new[] { "extra" }, status.Undeclared.ToArray());
        }

        [TestMethod]
        public async Task Status_reports_type_mismatches_and_invalid_files()
        {
            var c = Sample();
            c.Files["web/dev.env"] = "database__port=abc\n";
            c.Files["web/qa.yaml"] = "database: [unclosed\n";
            var snapshot = await ConfigCatalogReader.LoadAsync(c, "web");
            var status = StatusChecker.Check(snapshot.Apps.Single(), snapshot.Registry);

            var dev = status.Environments.Single(e => e.Name == "dev");
            Assert.IsFalse(dev.Ok);
            var mismatch = dev.TypeMismatches.Single();
            Assert.AreEqual("database.port", mismatch.Key);
            Assert.AreEqual("number", mismatch.Expected);
            Assert.AreEqual("string", mismatch.Actual);

            var qa = status.Environments.Single(e => e.Name == "qa");
            Assert.IsFalse(qa.Ok);
            Assert.IsNotNull(qa.Error);
        }

        [TestMethod]
        public async Task Status_treats_null_as_missing_and_placeholders_as_safe()
        {
            var c = new FakeCatalog();
            c.Files["app/default.json"] = "{\"a\":null,\"secret\":\"${SECRET}\"}";
            c.Files["app/dev.json"] = "{}";
            c.Files["app/_variables.json"] = ReservedFiles.SerializeVariables(new[]
            {
                new VariableDefinition { Key = "a", Type = "string", Required = true },
                new VariableDefinition { Key = "secret", Type = "string", Secret = true },
            });
            var snapshot = await ConfigCatalogReader.LoadAsync(c, "app");
            var dev = StatusChecker.Check(snapshot.Apps.Single(), snapshot.Registry).Environments.Single();

            CollectionAssert.AreEqual(new[] { "a" }, dev.Missing.ToArray());
            Assert.AreEqual(0, dev.SecretsInPlainText.Count);
        }

        [TestMethod]
        public async Task Index_lists_usage_across_apps_without_values()
        {
            var snapshot = await ConfigCatalogReader.LoadAsync(Sample());
            var index = VariableIndex.Build(snapshot);

            var host = index.Single(v => v.Key == "database.host");
            Assert.AreEqual("string", host.Type);
            Assert.AreEqual(2, host.AppCount);
            var web = host.UsedBy.Single(u => u.App == "web");
            Assert.IsTrue(web.Declared);
            Assert.IsTrue(web.Required);
            CollectionAssert.AreEqual(new[] { "default.json", "qa.yaml" }, web.DefinedIn.ToArray());
            CollectionAssert.AreEqual(new[] { "qa" }, web.Environments.ToArray());
            Assert.IsFalse(host.UsedBy.Single(u => u.App == "api").Declared);

            // Declared but unused anywhere.
            var apiKey = index.Single(v => v.Key == "api.key");
            Assert.AreEqual(0, apiKey.UsedBy.Single().DefinedIn.Count);

            CollectionAssert.AreEqual(index.Select(v => v.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray(), index.Select(v => v.Key).ToArray());
        }

        [TestMethod]
        public async Task Index_filters_by_search_app_env_and_declared()
        {
            var snapshot = await ConfigCatalogReader.LoadAsync(Sample());

            Assert.IsTrue(VariableIndex.Build(snapshot, new VariableQuery { Search = "HOST" }).All(v => v.Key.Contains("host")));
            Assert.IsTrue(VariableIndex.Build(snapshot, new VariableQuery { App = "api" }).All(v => v.UsedBy.All(u => u.App == "api")));
            CollectionAssert.AreEquivalent(new[] { "database.host", "database.password", "database.port", "debug" },
                VariableIndex.Build(snapshot, new VariableQuery { Environment = "dev" }).Select(v => v.Key).ToArray());
            CollectionAssert.AreEquivalent(new[] { "api.key", "database.host", "database.password", "database.port", "debug" },
                VariableIndex.Build(snapshot, new VariableQuery { Environment = "prod" }).Select(v => v.Key).ToArray());
            Assert.IsTrue(VariableIndex.Build(snapshot, new VariableQuery { DeclaredOnly = true }).All(v => v.UsedBy.Any(u => u.Declared)));
            Assert.IsFalse(VariableIndex.Build(snapshot, new VariableQuery { DeclaredOnly = true }).Any(v => v.Key == "flag"));
        }

        [TestMethod]
        public async Task Detail_builds_environment_matrix()
        {
            var snapshot = await ConfigCatalogReader.LoadAsync(Sample());

            var detail = VariableIndex.GetDetail(snapshot, "database.host");
            var web = detail.UsedBy.Single(u => u.App == "web");
            Assert.AreEqual("inherited", web.ByEnvironment["dev"]);
            Assert.AreEqual("defined", web.ByEnvironment["qa"]);
            Assert.AreEqual("inherited", web.ByEnvironment["prod"]);
            Assert.AreEqual("defined", detail.UsedBy.Single(u => u.App == "api").ByEnvironment["prod"]);

            var apiKey = VariableIndex.GetDetail(snapshot, "api.key").UsedBy.Single();
            Assert.AreEqual("missing", apiKey.ByEnvironment["prod"]);

            Assert.IsNull(VariableIndex.GetDetail(snapshot, "nope"));
        }

        [TestMethod]
        public async Task Loader_skips_invalid_and_oversized_files_with_warnings()
        {
            var c = Sample();
            c.Files["web/broken.json"] = "{ nope";
            c.Files["web/huge.json"] = "{\"pad\":\"" + new string('x', (int)ConfigCatalogReader.MaxScanBytes) + "\"}";
            var snapshot = await ConfigCatalogReader.LoadAsync(c);

            CollectionAssert.AreEquivalent(new[] { "broken.json", "huge.json" }, snapshot.Warnings.Select(w => w.File).ToArray());
            Assert.IsTrue(snapshot.Warnings.All(w => w.App == "web"));
            Assert.IsTrue(VariableIndex.Build(snapshot).Any());
        }

        [TestMethod]
        public void Environment_list_merges_declared_and_inferred()
        {
            var registry = new List<EnvironmentDefinition>
            {
                new EnvironmentDefinition { Name = "prod", Order = 2 },
                new EnvironmentDefinition { Name = "dev", Order = 1, Description = "Development" },
            };
            var overrides = new Dictionary<string, List<string>>
            {
                ["a"] = new List<string> { "dev", "qa" },
                ["b"] = new List<string> { "DEV", "zeta" },
            };

            var list = EnvironmentCatalog.Build(registry, overrides);

            CollectionAssert.AreEqual(new[] { "dev", "prod", "qa", "zeta" }, list.Select(e => e.Name).ToArray());
            Assert.AreEqual(2, list[0].AppCount);
            Assert.IsTrue(list[0].Declared);
            Assert.AreEqual(0, list[1].AppCount);
            Assert.IsFalse(list[2].Declared);
        }
    }
}
