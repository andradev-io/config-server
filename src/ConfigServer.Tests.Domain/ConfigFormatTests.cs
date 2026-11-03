using System.Text.Json.Nodes;
using ConfigServer.Domain.Configuration;
using ConfigServer.Domain.Configuration.Formats;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Domain
{
    [TestClass]
    public class ConfigFormatTests
    {
        [TestMethod]
        [DataRow("a.json", ConfigFormat.Json)]
        [DataRow("a.yaml", ConfigFormat.Yaml)]
        [DataRow("a.yml", ConfigFormat.Yaml)]
        [DataRow("a.env", ConfigFormat.Env)]
        [DataRow("A.JSON", ConfigFormat.Json)]
        public void Detects_format_from_file_name(string name, ConfigFormat expected)
            => Assert.AreEqual(expected, ConfigFormatExtensions.FromFileName(name));

        [TestMethod]
        public void Unknown_extension_is_null()
        {
            Assert.IsNull(ConfigFormatExtensions.FromFileName("a.txt"));
            Assert.IsNull(ConfigFormatExtensions.FromFileName("noext"));
        }

        // ---- JSON ----

        [TestMethod]
        public void Json_roundtrip()
        {
            var tree = ConfigFormat.Json.Parse("{\"a\":1,\"b\":{\"c\":[1,\"x\",true,null]}}");
            var again = ConfigFormat.Json.Parse(ConfigFormat.Json.Render(tree));
            Assert.IsTrue(JsonNode.DeepEquals(tree, again));
        }

        [TestMethod]
        public void Json_invalid_reports_line()
        {
            var ex = Assert.Throws<ConfigFormatException>(() => ConfigFormat.Json.Parse("{\n  \"a\": 1,\n  \"b\": nope\n}"));
            Assert.AreEqual(3, ex.Line);
            Assert.IsNotNull(ex.Column);
        }

        // ---- YAML ----

        [TestMethod]
        public void Yaml_scalars_keep_types()
        {
            var tree = ConfigFormat.Yaml.Parse("i: 5\nf: 1.5\nb: true\nn: null\ns: hello\nq: \"5\"\nl:\n  - 1\n  - two\n");
            Assert.AreEqual(5L, (long)tree["i"]);
            Assert.AreEqual(1.5, (double)tree["f"]);
            Assert.AreEqual(true, (bool)tree["b"]);
            Assert.IsNull(tree["n"]);
            Assert.AreEqual("hello", (string)tree["s"]);
            Assert.AreEqual("5", (string)tree["q"]);
            Assert.AreEqual(2, tree["l"].AsArray().Count);
        }

        [TestMethod]
        public void Yaml_anchors_and_aliases()
        {
            var tree = ConfigFormat.Yaml.Parse("base: &b\n  x: 1\ncopy: *b\nmerged:\n  <<: *b\n  y: 2\n");
            Assert.AreEqual(1L, (long)tree["copy"]["x"]);
            Assert.AreEqual(1L, (long)tree["merged"]["x"]);
            Assert.AreEqual(2L, (long)tree["merged"]["y"]);
        }

        [TestMethod]
        public void Yaml_roundtrip_preserves_types()
        {
            var tree = ConfigFormat.Json.Parse("{\"a\":\"true\",\"b\":true,\"c\":\"12\",\"d\":12,\"e\":null,\"f\":\"multi\\nline\",\"g\":{\"h\":[1,\"x\"]},\"i\":\"a: b\",\"j\":\"\"}");
            var yaml = ConfigFormat.Yaml.Render(tree);
            Assert.IsFalse(yaml.StartsWith("---"));
            var again = ConfigFormat.Yaml.Parse(yaml);
            Assert.IsTrue(JsonNode.DeepEquals(tree, again), yaml);
        }

        [TestMethod]
        public void Yaml_invalid_reports_line()
        {
            var ex = Assert.Throws<ConfigFormatException>(() => ConfigFormat.Yaml.Parse("a: 1\nb: [1, 2\nc: 3\n"));
            Assert.IsNotNull(ex.Line);
            Assert.IsTrue(ex.Line >= 2);
        }

        [TestMethod]
        public void Yaml_empty_is_empty_object()
            => Assert.AreEqual(0, ConfigFormat.Yaml.Parse("# nothing\n").AsObject().Count);

        // ---- ENV ----

        [TestMethod]
        public void Env_parses_comments_quotes_export_and_nesting()
        {
            var text = "# comment\n\nA=1\nexport B = two # trailing\nC=\"hello world # not comment\"\nD='single $x'\nE=\"line\\nbreak\"\nF__G__H=deep\nF__I=2\nEMPTY=\n";
            var tree = ConfigFormat.Env.Parse(text);
            Assert.AreEqual("1", (string)tree["A"]);
            Assert.AreEqual("two", (string)tree["B"]);
            Assert.AreEqual("hello world # not comment", (string)tree["C"]);
            Assert.AreEqual("single $x", (string)tree["D"]);
            Assert.AreEqual("line\nbreak", (string)tree["E"]);
            Assert.AreEqual("deep", (string)tree["F"]["G"]["H"]);
            Assert.AreEqual("2", (string)tree["F"]["I"]);
            Assert.AreEqual("", (string)tree["EMPTY"]);
        }

        [TestMethod]
        public void Env_nesting_example_from_contract()
        {
            var tree = ConfigFormat.Env.Parse("A__B=1");
            Assert.AreEqual("{\"A\":{\"B\":\"1\"}}", tree.ToJsonString());
        }

        [TestMethod]
        public void Env_indexed_keys_become_arrays()
        {
            var tree = ConfigFormat.Env.Parse("L__0=a\nL__1=b\n");
            Assert.AreEqual("{\"L\":[\"a\",\"b\"]}", tree.ToJsonString());
        }

        [TestMethod]
        public void Env_render_flattens_and_quotes()
        {
            var tree = ConfigFormat.Json.Parse("{\"A\":{\"B\":1,\"C\":[\"x\",\"y z\"]},\"D\":true,\"E\":\"has # hash\",\"F\":null,\"G\":\"q\\\"uote\"}");
            var env = ConfigFormat.Env.Render(tree);
            Assert.AreEqual("A__B=1\nA__C__0=x\nA__C__1=\"y z\"\nD=true\nE=\"has # hash\"\nF=\nG=\"q\\\"uote\"\n", env);
        }

        [TestMethod]
        public void Env_roundtrip_gives_strings()
        {
            var tree = ConfigFormat.Json.Parse("{\"A\":{\"B\":\"x y\",\"C\":[\"1\",\"2\"]},\"D\":\"he said \\\"hi\\\"\"}");
            var again = ConfigFormat.Env.Parse(ConfigFormat.Env.Render(tree));
            Assert.IsTrue(JsonNode.DeepEquals(tree, again));
        }

        [TestMethod]
        public void Env_invalid_line_reports_line_number()
        {
            var ex = Assert.Throws<ConfigFormatException>(() => ConfigFormat.Env.Parse("A=1\nB=2\nnot a pair\n"));
            Assert.AreEqual(3, ex.Line);
            Assert.AreEqual(1, ex.Column);
        }

        [TestMethod]
        public void Env_unterminated_quote_reports_position()
        {
            var ex = Assert.Throws<ConfigFormatException>(() => ConfigFormat.Env.Parse("A=1\nB=\"oops\n"));
            Assert.AreEqual(2, ex.Line);
            Assert.AreEqual(3, ex.Column);
        }

        [TestMethod]
        public void Env_render_requires_object_root()
            => Assert.Throws<ConfigFormatException>(() => ConfigFormat.Env.Render(JsonNode.Parse("[1]")));

        // ---- cross-format merge ----

        [TestMethod]
        public void Merge_documents_across_formats()
        {
            var json = new ConfigDocument("{\"a\":1,\"n\":{\"x\":1,\"y\":2},\"l\":[1,2]}", ConfigFormat.Json);
            var yaml = new ConfigDocument("n:\n  y: 3\nl:\n  - 9\n", ConfigFormat.Yaml);
            var env = new ConfigDocument("n__z=4\na=override\n", ConfigFormat.Env);

            var merged = ConfigFormat.Json.Parse(ConfigMerger.MergeDocuments(ConfigFormat.Json, json, yaml, env));

            Assert.AreEqual("override", (string)merged["a"]);
            Assert.AreEqual(1, (int)merged["n"]["x"]);
            Assert.AreEqual(3, (int)merged["n"]["y"]);
            Assert.AreEqual("4", (string)merged["n"]["z"]);
            Assert.AreEqual(1, merged["l"].AsArray().Count);
        }

        [TestMethod]
        public void Merge_documents_renders_requested_format()
        {
            var json = new ConfigDocument("{\"a\":{\"b\":1}}", ConfigFormat.Json);
            var yaml = new ConfigDocument("a:\n  c: two\n", ConfigFormat.Yaml);

            Assert.AreEqual("a__b=1\na__c=two\n", ConfigMerger.MergeDocuments(ConfigFormat.Env, json, yaml));
        }
    }
}
