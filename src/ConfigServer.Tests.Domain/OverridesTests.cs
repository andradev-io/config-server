using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ConfigServer.Domain.Configuration.Formats;
using ConfigServer.Domain.Configuration.Management;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Domain
{
    [TestClass]
    public class ConfigTreeEditTests
    {
        [TestMethod]
        public void TrySet_creates_intermediate_objects_and_clones_the_value()
        {
            var tree = new JsonObject();
            var value = JsonNode.Parse("[1,2]");

            Assert.IsTrue(ConfigTree.TrySet(tree, "a.b.c", value, out var changed, out var error));

            Assert.IsTrue(changed);
            Assert.IsNull(error);
            Assert.AreEqual("{\"a\":{\"b\":{\"c\":[1,2]}}}", tree.ToJsonString());
            Assert.IsNull(value.Parent, "the caller's node must not be re-parented");
        }

        [TestMethod]
        public void TrySet_replaces_existing_and_reports_unchanged_for_equal_values()
        {
            var tree = JsonNode.Parse("{\"a\":{\"b\":1}}");

            Assert.IsTrue(ConfigTree.TrySet(tree, "a.b", JsonValue.Create(1), out var same, out _));
            Assert.IsFalse(same);
            Assert.IsTrue(ConfigTree.TrySet(tree, "a.b", JsonValue.Create(2), out var changed, out _));
            Assert.IsTrue(changed);
            Assert.AreEqual(2, tree["a"]["b"].GetValue<int>());
        }

        [TestMethod]
        public void TrySet_fails_when_a_parent_is_a_scalar_or_array()
        {
            var tree = JsonNode.Parse("{\"s\":\"x\",\"list\":[1]}");

            Assert.IsFalse(ConfigTree.TrySet(tree, "s.child", JsonValue.Create(1), out _, out var error));
            StringAssert.Contains(error, "'s'");
            Assert.IsFalse(ConfigTree.TrySet(tree, "list.0", JsonValue.Create(1), out _, out _));
            Assert.AreEqual("{\"s\":\"x\",\"list\":[1]}", tree.ToJsonString());
        }

        [TestMethod]
        public void TrySet_refuses_to_replace_an_object_with_children_by_a_scalar_but_allows_an_object()
        {
            var tree = JsonNode.Parse("{\"a\":{\"b\":1}}");

            Assert.IsFalse(ConfigTree.TrySet(tree, "a", JsonValue.Create("x"), out _, out _));
            Assert.IsTrue(ConfigTree.TrySet(tree, "a", JsonNode.Parse("{\"z\":1}"), out _, out _));
            Assert.AreEqual("{\"a\":{\"z\":1}}", tree.ToJsonString());
        }

        [TestMethod]
        public void TrySet_rejects_invalid_keys_and_non_object_roots()
        {
            Assert.IsFalse(ConfigTree.TrySet(new JsonObject(), "a..b", JsonValue.Create(1), out _, out _));
            Assert.IsFalse(ConfigTree.TrySet(new JsonObject(), ".a", JsonValue.Create(1), out _, out _));
            Assert.IsFalse(ConfigTree.TrySet(new JsonArray(), "a", JsonValue.Create(1), out _, out _));
        }

        [TestMethod]
        public void Remove_deletes_the_leaf_and_prunes_empty_parents_only()
        {
            var tree = JsonNode.Parse("{\"a\":{\"b\":{\"c\":1},\"keep\":2},\"x\":{\"y\":{\"z\":3}}}");

            Assert.IsTrue(ConfigTree.Remove(tree, "a.b.c"));
            Assert.AreEqual("{\"a\":{\"keep\":2},\"x\":{\"y\":{\"z\":3}}}", tree.ToJsonString());
            Assert.IsTrue(ConfigTree.Remove(tree, "x.y.z"));
            Assert.AreEqual("{\"a\":{\"keep\":2}}", tree.ToJsonString());
        }

        [TestMethod]
        public void Remove_returns_false_for_missing_paths()
        {
            var tree = JsonNode.Parse("{\"a\":{\"b\":1}}");

            Assert.IsFalse(ConfigTree.Remove(tree, "a.c"));
            Assert.IsFalse(ConfigTree.Remove(tree, "q.r"));
            Assert.AreEqual("{\"a\":{\"b\":1}}", tree.ToJsonString());
        }

        [TestMethod]
        public void Set_and_Remove_handle_property_names_containing_dots()
        {
            var tree = JsonNode.Parse("{\"logging\":{\"a.b\":1}}");

            Assert.IsTrue(ConfigTree.TrySet(tree, "logging.a.b", JsonValue.Create(2), out _, out _));
            Assert.AreEqual("{\"logging\":{\"a.b\":2}}", tree.ToJsonString());
            Assert.IsTrue(ConfigTree.Remove(tree, "logging.a.b"));
            Assert.AreEqual("{}", tree.ToJsonString());
        }
    }

    [TestClass]
    public class OverridesEditorTests
    {
        private static AppSnapshot Snapshot(FakeCatalog catalog, string app = "web")
            => ConfigCatalogReader.LoadAsync(catalog, app).GetAwaiter().GetResult().Apps.Single();

        private static OverrideChange Set(string scope, string key, string json)
            => new OverrideChange { Scope = scope, Key = key, Value = JsonNode.Parse(json) };

        [TestMethod]
        public void Env_files_receive_strings_and_unchanged_values_are_not_reported()
        {
            var catalog = new FakeCatalog();
            catalog.Files["web/default.json"] = "{\"port\":1}";
            catalog.Files["web/dev.env"] = "port=6000\n";

            var plans = OverridesEditor.Plan(Snapshot(catalog), new[] { "dev" },
                new[] { Set("dev", "port", "6000"), Set("dev", "flag", "true") }, new List<string>());

            var plan = plans.Single();
            CollectionAssert.AreEqual(new[] { "flag" }, plan.ChangedKeys);
            Assert.AreEqual("port=6000\nflag=true\n", plan.Render());
        }

        [TestMethod]
        public void New_scope_file_follows_the_default_format_and_extension()
        {
            var catalog = new FakeCatalog();
            catalog.Files["web/default.yml"] = "a: 1\n";

            var plan = OverridesEditor.Plan(Snapshot(catalog), new[] { "dev" }, new[] { Set("dev", "a", "2") }, new List<string>()).Single();

            Assert.IsTrue(plan.Created);
            Assert.AreEqual("dev.yml", plan.Name);
            Assert.AreEqual(ConfigFormat.Yaml, plan.Format);
        }

        [TestMethod]
        public void Invalid_change_throws_with_key_and_scope()
        {
            var catalog = new FakeCatalog();
            catalog.Files["web/default.json"] = "{\"a\":1}";
            catalog.Files["web/_variables.json"] = "{\"variables\":[{\"key\":\"a\",\"type\":\"number\"}]}";
            var snapshot = Snapshot(catalog);

            var ex = Assert.Throws<OverrideException>(() => OverridesEditor.Plan(snapshot, new[] { "dev" }, new[] { Set("dev", "a", "\"x\"") }, new List<string>()));
            Assert.AreEqual("a", ex.Key);
            Assert.AreEqual("dev", ex.Scope);
            Assert.Throws<OverrideException>(() => OverridesEditor.Plan(snapshot, new[] { "dev" }, new[] { Set("qa", "a", "1") }, new List<string>()));
        }

        [TestMethod]
        public void Grid_masks_secrets_and_resolves_effective_source()
        {
            var catalog = new FakeCatalog();
            catalog.Files["web/default.json"] = "{\"pw\":\"s3\",\"n\":1}";
            catalog.Files["web/dev.json"] = "{\"pw\":\"d3\"}";
            catalog.Files["web/_variables.json"] = "{\"variables\":[{\"key\":\"pw\",\"type\":\"string\",\"secret\":true}]}";

            var grid = OverridesGrid.Build(Snapshot(catalog), new[] { "dev", "prod" });

            var pw = grid.Single(v => v.Key == "pw");
            Assert.IsTrue(pw.Values["dev"].Masked);
            Assert.IsNull(pw.Values["dev"].Value);
            Assert.AreEqual("default", pw.Effective["prod"].From);
            Assert.IsNull(pw.Effective["prod"].Value);
            var n = grid.Single(v => v.Key == "n");
            Assert.AreEqual("number", n.Type);
            Assert.IsFalse(n.Values.ContainsKey("dev"));
        }

        [TestMethod]
        public void CommentDetector_flags_hash_comments_and_json_comments()
        {
            Assert.IsTrue(CommentDetector.HasComments(ConfigFormat.Yaml, "# c\na: 1\n"));
            Assert.IsTrue(CommentDetector.HasComments(ConfigFormat.Env, "A=1 # trailing\n"));
            Assert.IsFalse(CommentDetector.HasComments(ConfigFormat.Env, "A=b#c\n"));
            Assert.IsTrue(CommentDetector.HasComments(ConfigFormat.Json, "{\n // c\n \"a\": 1}"));
            Assert.IsFalse(CommentDetector.HasComments(ConfigFormat.Json, "{\"a\":\"// not a comment\"}"));
        }
    }
}
