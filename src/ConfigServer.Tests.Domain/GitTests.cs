using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Domain
{
    [TestClass]
    public class GitTests
    {
        [TestMethod]
        [DataRow("https://user:token@github.com/dev-vinicius-andrade/config-server-files.git")]
        public void GetRepositoryStatus(string uriString)
        {
            var uri = new Uri(uriString);
            Assert.AreEqual("github.com", uri.Host);
        }
    }
}
