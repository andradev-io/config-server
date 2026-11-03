using System.Text.Json;
using ConfigServer.Domain.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Domain
{
    [TestClass]
    public class ConfigMergerTests
    {
        private const string Default = @"{
            ""logging"": { ""minimumLevel"": ""error"", ""outputs"": [ { ""type"": ""file"" } ] },
            ""integrations"": { ""api"": { ""api1"": { ""baseUrl"": ""http://localhost"", ""endpoints"": { ""get"": ""endpoint1"" } } } }
        }";

        [TestMethod]
        public void OverrideReplacesOnlyChangedFields()
        {
            var overrideJson = @"{ ""logging"": { ""minimumLevel"": ""debug"" }, ""integrations"": { ""api"": { ""api1"": { ""baseUrl"": ""https://dev"" } } } }";

            using var result = JsonDocument.Parse(ConfigMerger.Merge(Default, overrideJson));
            var root = result.RootElement;

            Assert.AreEqual("debug", root.GetProperty("logging").GetProperty("minimumLevel").GetString());
            Assert.AreEqual("file", root.GetProperty("logging").GetProperty("outputs")[0].GetProperty("type").GetString());
            var api1 = root.GetProperty("integrations").GetProperty("api").GetProperty("api1");
            Assert.AreEqual("https://dev", api1.GetProperty("baseUrl").GetString());
            Assert.AreEqual("endpoint1", api1.GetProperty("endpoints").GetProperty("get").GetString());
        }

        [TestMethod]
        public void OverrideAddsNewProperties()
        {
            using var result = JsonDocument.Parse(ConfigMerger.Merge(Default, @"{ ""extra"": 1 }"));

            Assert.AreEqual(1, result.RootElement.GetProperty("extra").GetInt32());
            Assert.IsTrue(result.RootElement.TryGetProperty("logging", out _));
        }

        [TestMethod]
        public void ArraysAreReplacedNotMerged()
        {
            var overrideJson = @"{ ""logging"": { ""outputs"": [ { ""type"": ""console"" }, { ""type"": ""seq"" } ] } }";

            using var result = JsonDocument.Parse(ConfigMerger.Merge(Default, overrideJson));
            var outputs = result.RootElement.GetProperty("logging").GetProperty("outputs");

            Assert.AreEqual(2, outputs.GetArrayLength());
            Assert.AreEqual("console", outputs[0].GetProperty("type").GetString());
        }

        [TestMethod]
        public void LaterOverridesWin()
        {
            using var result = JsonDocument.Parse(ConfigMerger.Merge(
                Default,
                @"{ ""logging"": { ""minimumLevel"": ""info"" } }",
                @"{ ""logging"": { ""minimumLevel"": ""warn"" } }"));

            Assert.AreEqual("warn", result.RootElement.GetProperty("logging").GetProperty("minimumLevel").GetString());
        }

        [TestMethod]
        public void NoOverridesReturnsBase()
        {
            using var result = JsonDocument.Parse(ConfigMerger.Merge(Default, null as string, " "));

            Assert.AreEqual("error", result.RootElement.GetProperty("logging").GetProperty("minimumLevel").GetString());
        }

        [TestMethod]
        public void InvalidJsonThrows()
        {
            Assert.Throws<JsonException>(() => ConfigMerger.Merge(Default, "{ not json"));
        }
    }
}
