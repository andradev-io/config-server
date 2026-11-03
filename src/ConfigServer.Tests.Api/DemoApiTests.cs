using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ConfigServer.Application.Api.Demo;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Api
{
    /// <summary>Test host in Demo mode: a temp copy of the repo's demo-data as template, a temp workspaces directory.</summary>
    internal sealed class DemoHost : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cfgdemo-" + Guid.NewGuid().ToString("N"));
        public string Template => Path.Combine(Root, "template");
        public string Workspaces { get; }
        public AuthTestHost Host { get; }

        public static string RepoDemoData()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "demo-data"))) dir = dir.Parent;
            return dir == null ? throw new DirectoryNotFoundException("demo-data not found above " + AppContext.BaseDirectory)
                : Path.Combine(dir.FullName, "demo-data");
        }

        public DemoHost(IDictionary<string, string> settings = null, string workspaces = null, string template = null)
        {
            Workspaces = workspaces ?? Path.Combine(Root, "workspaces");
            if (template == null)
            {
                Copy(RepoDemoData(), Template);
                template = Template;
            }

            var values = new Dictionary<string, string>
            {
                ["Demo:Enabled"] = "true",
                ["Demo:TemplatePath"] = template,
                ["Demo:WorkspacesPath"] = Workspaces
            };
            foreach (var pair in settings ?? new Dictionary<string, string>()) values[pair.Key] = pair.Value;

            Host = new AuthTestHost(settings: values, configureServices: services =>
            {
                // AuthTestHost swaps in an in-memory key store; the sandbox keeps its per-workspace one.
                services.RemoveAll<IApiKeyStore>();
                services.AddSingleton<IApiKeyStore, DemoApiKeyStore>();
            });
        }

        public DemoWorkspaces Manager => Host.Factory.Services.GetRequiredService<DemoWorkspaces>();

        public string Visitor(string sub, params string[] roles)
            => Host.Token(roles.Length == 0 ? new[] { "config:admin" } : roles, claims: new Dictionary<string, object> { ["sub"] = sub });

        public Task<HttpResponseMessage> Send(HttpMethod method, string url, string bearer = null, string apiKey = null, string json = null)
            => Host.Client.SendAsync(AuthTestHost.Request(method, url, bearer: bearer, apiKey: apiKey, json: json));

        public Task<HttpResponseMessage> Put(string bearer, string app, string file, string content)
            => Send(HttpMethod.Put, $"/api/apps/{app}/files/{file}", bearer, json: JsonSerializer.Serialize(new { content }));

        public static async Task<string> Text(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

        public static async Task<JsonElement> Json(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        public static void Copy(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (var dir in Directory.GetDirectories(source)) Copy(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        public static string Snapshot(string directory)
            => string.Join("|", Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => Path.GetRelativePath(directory, p) + "=" + File.ReadAllText(p)));

        public void Dispose()
        {
            Host.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    [TestClass]
    public class DemoApiTests
    {
        private DemoHost _demo;

        [TestCleanup]
        public void Cleanup() => _demo?.Dispose();

        // ---- auth info ----

        [TestMethod]
        public async Task Info_reports_the_sandbox_when_enabled_and_null_when_not()
        {
            _demo = new DemoHost(new Dictionary<string, string> { ["Demo:IdleTtlMinutes"] = "45", ["Demo:Notice"] = "Sandbox: be nice." });
            var info = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/auth/info"));
            Assert.AreEqual("oidc", info.GetProperty("mode").GetString());
            Assert.IsTrue(info.GetProperty("apiKeys").GetBoolean());
            var demo = info.GetProperty("demo");
            Assert.IsTrue(demo.GetProperty("enabled").GetBoolean());
            Assert.AreEqual(45, demo.GetProperty("idleTtlMinutes").GetInt32());
            Assert.AreEqual("Sandbox: be nice.", demo.GetProperty("notice").GetString());

            using var plain = new AuthTestHost();
            var plainInfo = await DemoHost.Json(await plain.Client.GetAsync("/api/auth/info"));
            Assert.AreEqual(JsonValueKind.Null, plainInfo.GetProperty("demo").ValueKind);
        }

        [TestMethod]
        public async Task Default_notice_is_used_when_none_is_configured()
        {
            _demo = new DemoHost();
            var info = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/auth/info"));
            StringAssert.Contains(info.GetProperty("demo").GetProperty("notice").GetString(), "sandbox");
        }

        // ---- workspaces: seeding and isolation ----

        [TestMethod]
        public async Task First_request_seeds_a_workspace_from_the_template_named_by_the_sub_hash()
        {
            _demo = new DemoHost();
            var alice = _demo.Visitor("alice-sub");
            var response = await _demo.Send(HttpMethod.Get, "/api/apps", alice);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var id = DemoWorkspaces.IdFor("alice-sub");
            Assert.AreEqual(16, id.Length);
            var workspace = Path.Combine(_demo.Workspaces, id);
            Assert.IsTrue(File.Exists(Path.Combine(workspace, "config", "billing-service", "default.yaml")));
            Assert.IsTrue(Directory.Exists(Path.Combine(workspace, "state")));
            CollectionAssert.AreEqual(new[] { "billing-service", "notifications", "web-portal" },
                (await DemoHost.Json(response)).EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.AreEqual(0, Directory.GetDirectories(_demo.Workspaces).Count(d => d.Contains(".tmp-")), "no temporary directory is left behind");
        }

        [TestMethod]
        public async Task Visitors_are_isolated_and_the_template_is_never_written()
        {
            _demo = new DemoHost();
            var before = DemoHost.Snapshot(_demo.Template);
            var alice = _demo.Visitor("alice");
            var bob = _demo.Visitor("bob");

            var put = await _demo.Put(alice, "billing-service", "dev.env", "logging__minimumLevel=alice-only\n");
            Assert.IsTrue(put.IsSuccessStatusCode, await DemoHost.Text(put));
            var created = await _demo.Put(alice, "billing-service", "qa.json", "{\"qa\":true}");
            Assert.IsTrue(created.IsSuccessStatusCode, await DemoHost.Text(created));

            var aliceMerged = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/config/billing-service/dev", alice));
            Assert.AreEqual("alice-only", aliceMerged.GetProperty("logging").GetProperty("minimumLevel").GetString());
            var bobMerged = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/config/billing-service/dev", bob));
            Assert.AreEqual("debug", bobMerged.GetProperty("logging").GetProperty("minimumLevel").GetString());

            var bobFiles = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/apps/billing-service/files", bob));
            Assert.IsFalse(bobFiles.EnumerateArray().Any(f => f.GetProperty("name").GetString() == "qa.json"));
            var aliceFiles = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/apps/billing-service/files", alice));
            Assert.IsTrue(aliceFiles.EnumerateArray().Any(f => f.GetProperty("name").GetString() == "qa.json"));

            var deleted = await _demo.Send(HttpMethod.Delete, "/api/apps/billing-service/files/stg.yaml", alice);
            Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await _demo.Send(HttpMethod.Get, "/config/billing-service/stg", bob)).StatusCode);

            Assert.AreEqual(before, DemoHost.Snapshot(_demo.Template));
        }

        [TestMethod]
        public async Task Management_endpoints_are_isolated_too()
        {
            _demo = new DemoHost();
            var alice = _demo.Visitor("alice");
            var bob = _demo.Visitor("bob");

            var add = await _demo.Send(HttpMethod.Post, "/api/environments", alice, json: "{\"name\":\"qa\",\"order\":9}");
            Assert.IsTrue(add.IsSuccessStatusCode, await DemoHost.Text(add));

            string Names(JsonElement json) => string.Join(",", json.EnumerateArray().Select(e => e.GetProperty("name").GetString()));
            StringAssert.Contains(Names(await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/environments", alice))), "qa");
            Assert.IsFalse(Names(await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/environments", bob))).Contains("qa"));
        }

        [TestMethod]
        public async Task Writes_work_even_when_AllowWrite_is_false()
        {
            _demo = new DemoHost(new Dictionary<string, string> { ["ConfigSource:AllowWrite"] = "false" });
            var response = await _demo.Put(_demo.Visitor("alice"), "notifications", "dev.json", "{\"logging\":{\"minimumLevel\":\"trace\"}}");
            Assert.IsTrue(response.IsSuccessStatusCode, await DemoHost.Text(response));
        }

        [TestMethod]
        public async Task The_shipped_demo_data_parses_and_merges_for_every_app_and_environment()
        {
            _demo = new DemoHost(template: DemoHost.RepoDemoData());
            var visitor = _demo.Visitor("tour");
            var apps = (await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/apps", visitor))).EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.AreEqual(3, apps.Count);

            foreach (var app in apps)
            {
                foreach (var env in new[] { "dev", "stg", "prd" })
                {
                    var merged = await _demo.Send(HttpMethod.Get, $"/config/{app}/{env}", visitor);
                    Assert.AreEqual(HttpStatusCode.OK, merged.StatusCode, $"{app}/{env}: {await DemoHost.Text(merged)}");
                }
                var status = await _demo.Send(HttpMethod.Get, $"/api/apps/{app}/status", visitor);
                Assert.AreEqual(HttpStatusCode.OK, status.StatusCode, $"{app} status: {await DemoHost.Text(status)}");
                var overrides = await _demo.Send(HttpMethod.Get, $"/api/apps/{app}/overrides", visitor);
                Assert.AreEqual(HttpStatusCode.OK, overrides.StatusCode, $"{app} overrides: {await DemoHost.Text(overrides)}");
            }
            var variables = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/variables", visitor));
            Assert.IsTrue(variables.GetProperty("variables").GetArrayLength() > 10);
        }

        // ---- cleanup ----

        [TestMethod]
        public async Task Idle_workspaces_are_deleted_and_come_back_fresh()
        {
            _demo = new DemoHost();
            var alice = _demo.Visitor("alice");
            var bob = _demo.Visitor("bob");
            await _demo.Put(alice, "notifications", "dev.json", "{\"edited\":true}");
            await _demo.Send(HttpMethod.Get, "/api/apps", bob);

            Assert.AreEqual(0, _demo.Manager.Cleanup(), "nobody is idle yet");
            Assert.AreEqual(2, _demo.Manager.Count);

            var marker = Path.Combine(_demo.Workspaces, DemoWorkspaces.IdFor("alice"), "state", ".last-access");
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddHours(-3));
            Assert.AreEqual(1, _demo.Manager.Cleanup());
            Assert.IsFalse(Directory.Exists(Path.Combine(_demo.Workspaces, DemoWorkspaces.IdFor("alice"))));
            Assert.IsTrue(Directory.Exists(Path.Combine(_demo.Workspaces, DemoWorkspaces.IdFor("bob"))));

            var merged = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/config/notifications/dev", alice));
            Assert.IsFalse(merged.TryGetProperty("edited", out _), "alice starts again from the template");
        }

        [TestMethod]
        public void Cleanup_removes_stale_temporary_directories_only()
        {
            _demo = new DemoHost();
            var stale = Path.Combine(_demo.Workspaces, "0123456789abcdef.tmp-old");
            var fresh = Path.Combine(_demo.Workspaces, "0123456789abcdef.tmp-new");
            var foreign = Path.Combine(_demo.Workspaces, "not-a-workspace");
            foreach (var dir in new[] { stale, fresh, foreign }) Directory.CreateDirectory(dir);
            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-5));
            Directory.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddHours(-5));

            Assert.AreEqual(0, _demo.Manager.Cleanup());
            Assert.IsFalse(Directory.Exists(stale));
            Assert.IsTrue(Directory.Exists(fresh));
            Assert.IsTrue(Directory.Exists(foreign), "directories that are not workspaces are never touched");
        }

        // ---- caps ----

        [TestMethod]
        public async Task A_full_sandbox_turns_new_visitors_away_but_keeps_existing_ones()
        {
            _demo = new DemoHost(new Dictionary<string, string> { ["Demo:MaxWorkspaces"] = "1" });
            var alice = _demo.Visitor("alice");
            Assert.AreEqual(HttpStatusCode.OK, (await _demo.Send(HttpMethod.Get, "/api/apps", alice)).StatusCode);

            var bob = await _demo.Send(HttpMethod.Get, "/api/apps", _demo.Visitor("bob"));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, bob.StatusCode);
            StringAssert.Contains((await DemoHost.Json(bob)).GetProperty("error").GetString(), "full");

            Assert.AreEqual(HttpStatusCode.OK, (await _demo.Send(HttpMethod.Get, "/api/apps", alice)).StatusCode);
        }

        [TestMethod]
        public async Task File_count_cap_blocks_new_files_but_allows_edits()
        {
            var files = Directory.GetFiles(DemoHost.RepoDemoData(), "*", SearchOption.AllDirectories).Length;
            _demo = new DemoHost(new Dictionary<string, string> { ["Demo:MaxFilesPerWorkspace"] = files.ToString() });
            var alice = _demo.Visitor("alice");

            var edit = await _demo.Put(alice, "billing-service", "dev.env", "logging__minimumLevel=info\n");
            Assert.IsTrue(edit.IsSuccessStatusCode, await DemoHost.Text(edit));

            var extra = await _demo.Put(alice, "billing-service", "qa.json", "{}");
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, extra.StatusCode);
            StringAssert.Contains((await DemoHost.Json(extra)).GetProperty("error").GetString(), "files");
        }

        [TestMethod]
        public async Task Size_cap_blocks_writes_that_would_exceed_it()
        {
            var size = Directory.GetFiles(DemoHost.RepoDemoData(), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            _demo = new DemoHost(new Dictionary<string, string> { ["Demo:MaxBytesPerWorkspace"] = (size + 200).ToString() });
            var alice = _demo.Visitor("alice");

            var small = await _demo.Put(alice, "billing-service", "qa.json", "{\"a\":1}");
            Assert.IsTrue(small.IsSuccessStatusCode, await DemoHost.Text(small));

            var big = await _demo.Put(alice, "billing-service", "big.json", "{\"v\":\"" + new string('x', 400) + "\"}");
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
            StringAssert.Contains((await DemoHost.Json(big)).GetProperty("error").GetString(), "KB");
            Assert.AreEqual(HttpStatusCode.NotFound, (await _demo.Send(HttpMethod.Get, "/api/apps/billing-service/files/big.json", alice)).StatusCode);
        }

        [TestMethod]
        public async Task Oversized_request_bodies_are_rejected()
        {
            _demo = new DemoHost(new Dictionary<string, string> { ["Demo:MaxRequestBodyBytes"] = "200" });
            var response = await _demo.Put(_demo.Visitor("alice"), "billing-service", "qa.json", "{\"v\":\"" + new string('x', 500) + "\"}");
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            StringAssert.Contains((await DemoHost.Json(response)).GetProperty("error").GetString(), "too large");
        }

        // ---- rate limit ----

        [TestMethod]
        public async Task Rate_limit_answers_429_per_visitor_and_spares_health()
        {
            _demo = new DemoHost(new Dictionary<string, string> { ["Demo:RequestsPerMinute"] = "5" });
            var alice = _demo.Visitor("alice");
            for (var i = 0; i < 5; i++)
                Assert.AreEqual(HttpStatusCode.OK, (await _demo.Send(HttpMethod.Get, "/api/auth/me", alice)).StatusCode, "request " + i);

            var limited = await _demo.Send(HttpMethod.Get, "/api/auth/me", alice);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace((await DemoHost.Json(limited)).GetProperty("error").GetString()));

            Assert.AreEqual(HttpStatusCode.OK, (await _demo.Send(HttpMethod.Get, "/api/auth/me", _demo.Visitor("bob"))).StatusCode, "another visitor has their own budget");
            Assert.AreEqual(HttpStatusCode.OK, (await _demo.Send(HttpMethod.Get, "/health")).StatusCode);
        }

        // ---- API keys ----

        [TestMethod]
        public async Task Local_api_keys_belong_to_the_workspace_that_created_them()
        {
            _demo = new DemoHost();
            var alice = _demo.Visitor("alice");
            var bob = _demo.Visitor("bob");
            await _demo.Put(alice, "billing-service", "dev.env", "logging__minimumLevel=alice-only\n");

            var created = await _demo.Send(HttpMethod.Post, "/api/keys", alice,
                json: "{\"name\":\"ci\",\"apps\":[\"billing-service\"],\"environments\":[\"dev\"],\"permission\":\"read\"}");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await DemoHost.Text(created));
            var secret = (await DemoHost.Json(created)).GetProperty("secret").GetString();

            var viaKey = await _demo.Send(HttpMethod.Get, "/config/billing-service/dev", apiKey: secret);
            Assert.AreEqual(HttpStatusCode.OK, viaKey.StatusCode, await DemoHost.Text(viaKey));
            Assert.AreEqual("alice-only", (await DemoHost.Json(viaKey)).GetProperty("logging").GetProperty("minimumLevel").GetString());
            Assert.AreEqual(HttpStatusCode.Forbidden, (await _demo.Send(HttpMethod.Get, "/config/billing-service/prd", apiKey: secret)).StatusCode,
                "the key keeps its environment scope");

            Assert.AreEqual(0, (await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/keys", bob))).GetArrayLength(), "bob sees no keys");
            Assert.AreEqual(1, (await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/keys", alice))).GetArrayLength());
        }

        [TestMethod]
        public async Task A_key_still_works_after_a_restart_and_after_its_workspace_is_deleted_it_does_not()
        {
            _demo = new DemoHost();
            var alice = _demo.Visitor("alice");
            var created = await _demo.Send(HttpMethod.Post, "/api/keys", alice, json: "{\"name\":\"ci\"}");
            var secret = (await DemoHost.Json(created)).GetProperty("secret").GetString();

            // A second host over the same directories has an empty prefix index: it is rebuilt by scanning the workspaces.
            using (var restarted = new DemoHost(workspaces: _demo.Workspaces, template: _demo.Template))
            {
                var response = await restarted.Send(HttpMethod.Get, "/config/notifications/dev", apiKey: secret);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await DemoHost.Text(response));
            }

            File.SetLastWriteTimeUtc(Path.Combine(_demo.Workspaces, DemoWorkspaces.IdFor("alice"), "state", ".last-access"), DateTime.UtcNow.AddHours(-3));
            Assert.AreEqual(1, _demo.Manager.Cleanup());
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await _demo.Send(HttpMethod.Get, "/config/notifications/dev", apiKey: secret)).StatusCode);
        }

        [TestMethod]
        public async Task Unknown_keys_are_rejected_without_a_workspace()
        {
            _demo = new DemoHost();
            var response = await _demo.Send(HttpMethod.Get, "/config/notifications/dev", apiKey: "cfs_abcd1234_" + new string('A', 43));
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.AreEqual(0, _demo.Manager.Count);
        }

        [TestMethod]
        public async Task The_key_count_is_capped_per_workspace()
        {
            _demo = new DemoHost(new Dictionary<string, string> { ["Demo:MaxApiKeysPerWorkspace"] = "1" });
            var alice = _demo.Visitor("alice");
            Assert.AreEqual(HttpStatusCode.Created, (await _demo.Send(HttpMethod.Post, "/api/keys", alice, json: "{\"name\":\"one\"}")).StatusCode);
            var second = await _demo.Send(HttpMethod.Post, "/api/keys", alice, json: "{\"name\":\"two\"}");
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, second.StatusCode);
        }

        // ---- simulated identity administration ----

        [TestMethod]
        public async Task Access_screen_works_against_the_simulated_provider_and_is_per_workspace()
        {
            _demo = new DemoHost();
            var alice = _demo.Visitor("alice");
            var bob = _demo.Visitor("bob");

            var info = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/access/info", alice));
            Assert.AreEqual("demo", info.GetProperty("provider").GetString());
            Assert.IsTrue(info.GetProperty("enabled").GetBoolean());
            StringAssert.Contains(info.GetProperty("note").GetString(), "simulated");

            var groups = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/access/groups", alice));
            CollectionAssert.AreEquivalent(new[] { "billing-team", "platform-admins", "readers" },
                groups.EnumerateArray().Select(g => g.GetProperty("name").GetString()).ToArray());
            var users = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/access/users?search=ali", alice));
            Assert.AreEqual("alice", users[0].GetProperty("username").GetString());

            var seeded = await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/access/subjects/user/bob/grants", alice));
            Assert.IsTrue(seeded.EnumerateArray().Any(g => g.GetProperty("inherited").GetBoolean() && g.GetProperty("via").GetString() == "billing-team"));

            var grant = await _demo.Send(HttpMethod.Post, "/api/access/subjects/group/readers/grants", alice,
                json: "{\"permission\":\"write\",\"app\":\"notifications\",\"environment\":\"dev\"}");
            Assert.AreEqual(HttpStatusCode.Created, grant.StatusCode, await DemoHost.Text(grant));

            bool HasGrant(JsonElement list) => list.EnumerateArray().Any(g => g.GetProperty("app").GetString() == "notifications" && g.GetProperty("environment").GetString() == "dev");
            Assert.IsTrue(HasGrant(await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/access/subjects/group/readers/grants", alice))));
            Assert.IsFalse(HasGrant(await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/access/subjects/group/readers/grants", bob))), "bob's sandbox is unchanged");

            var revoke = await _demo.Send(HttpMethod.Delete, "/api/access/subjects/group/readers/grants?permission=write&app=notifications&environment=dev", alice);
            Assert.AreEqual(HttpStatusCode.NoContent, revoke.StatusCode);
            Assert.IsFalse(HasGrant(await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/access/subjects/group/readers/grants", alice))));

            var missing = await _demo.Send(HttpMethod.Get, "/api/access/subjects/user/nobody/grants", alice);
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }

        [TestMethod]
        public async Task Access_screens_stay_admin_only_in_the_sandbox()
        {
            _demo = new DemoHost();
            var reader = _demo.Visitor("reader", "config:read");
            Assert.AreEqual(HttpStatusCode.Forbidden, (await _demo.Send(HttpMethod.Get, "/api/access/info", reader)).StatusCode, "access screens stay admin-only");
        }

        [TestMethod]
        public async Task Oidc_keys_are_simulated_with_a_one_time_secret_that_authenticates_nothing()
        {
            _demo = new DemoHost();
            var alice = _demo.Visitor("alice");
            var bob = _demo.Visitor("bob");

            var created = await _demo.Send(HttpMethod.Post, "/api/oidc-keys", alice,
                json: "{\"name\":\"Deploy bot\",\"grants\":[{\"permission\":\"read\",\"app\":\"billing-service\",\"environment\":\"dev\"}]}");
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await DemoHost.Text(created));
            var body = await DemoHost.Json(created);
            var secret = body.GetProperty("secret").GetString();
            StringAssert.StartsWith(body.GetProperty("clientId").GetString(), "cfs-deploy-bot-");
            StringAssert.StartsWith(secret, "demo_");
            StringAssert.Contains(body.GetProperty("tokenEndpoint").GetString(), "/protocol/openid-connect/token");
            var id = body.GetProperty("client").GetProperty("id").GetString();

            var listed = await DemoHost.Text(await _demo.Send(HttpMethod.Get, "/api/oidc-keys", alice));
            StringAssert.Contains(listed, "Deploy bot");
            Assert.IsFalse(listed.Contains(secret), "the secret is only returned once");
            Assert.AreEqual(0, (await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/oidc-keys", bob))).GetArrayLength());

            var rotated = await DemoHost.Json(await _demo.Send(HttpMethod.Post, $"/api/oidc-keys/{id}/rotate", alice));
            Assert.AreNotEqual(secret, rotated.GetProperty("secret").GetString());

            // The simulated secret is not a credential: presenting it as a bearer token fails.
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await _demo.Send(HttpMethod.Get, "/api/apps", secret)).StatusCode);

            Assert.AreEqual(HttpStatusCode.NoContent, (await _demo.Send(HttpMethod.Delete, $"/api/oidc-keys/{id}", alice)).StatusCode);
            Assert.AreEqual(0, (await DemoHost.Json(await _demo.Send(HttpMethod.Get, "/api/oidc-keys", alice))).GetArrayLength());
        }

        [TestMethod]
        public async Task Oidc_key_count_is_capped_per_workspace()
        {
            _demo = new DemoHost(new Dictionary<string, string> { ["Demo:MaxApiKeysPerWorkspace"] = "1" });
            var alice = _demo.Visitor("alice");
            const string body = "{\"name\":\"bot\",\"grants\":[{\"permission\":\"read\"}]}";
            Assert.AreEqual(HttpStatusCode.Created, (await _demo.Send(HttpMethod.Post, "/api/oidc-keys", alice, json: body)).StatusCode);
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, (await _demo.Send(HttpMethod.Post, "/api/oidc-keys", alice, json: body)).StatusCode);
        }

        [TestMethod]
        public async Task A_token_without_sub_gets_no_workspace()
        {
            _demo = new DemoHost();
            var token = _demo.Host.Token(new[] { "config:admin" }, claims: new Dictionary<string, object> { ["sub"] = "" });
            var response = await _demo.Send(HttpMethod.Get, "/api/apps", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.AreEqual(0, _demo.Manager.Count);
        }

        [TestMethod]
        public void Workspace_ids_are_stable_hashes_of_the_sub()
        {
            Assert.AreEqual(DemoWorkspaces.IdFor("alice"), DemoWorkspaces.IdFor("alice"));
            Assert.AreNotEqual(DemoWorkspaces.IdFor("alice"), DemoWorkspaces.IdFor("bob"));
            Assert.IsTrue(DemoWorkspaces.IsWorkspaceId(DemoWorkspaces.IdFor("alice")));
            Assert.IsFalse(DemoWorkspaces.IsWorkspaceId("../etc/passwd"));
        }
    }

    /// <summary>Startup guardrails: the sandbox refuses unsafe settings instead of running.</summary>
    [TestClass]
    public class DemoGuardrailTests
    {
        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cfgdemo-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "template"));
        }

        [TestCleanup]
        public void Cleanup() => Directory.Delete(_dir, true);

        private IConfiguration Config(params (string Key, string Value)[] overrides)
        {
            var values = new Dictionary<string, string>
            {
                ["Demo:Enabled"] = "true",
                ["Demo:TemplatePath"] = Path.Combine(_dir, "template"),
                ["Demo:WorkspacesPath"] = Path.Combine(_dir, "workspaces"),
                ["Auth:Mode"] = "Oidc"
            };
            foreach (var (key, value) in overrides) values[key] = value;
            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        private static InvalidOperationException Fails(IConfiguration configuration)
            => Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddConfigServerDemo(configuration));

        [TestMethod]
        public void Valid_settings_register_the_sandbox()
        {
            var services = new ServiceCollection().AddConfigServerDemo(Config());
            Assert.IsTrue(services.Any(s => s.ServiceType == typeof(DemoWorkspaces)));
        }

        [TestMethod]
        public void Disabled_demo_registers_only_its_options_and_skips_validation()
        {
            var services = new ServiceCollection().AddConfigServerDemo(Config(("Demo:Enabled", "false"), ("Auth:Mode", "None"), ("Demo:TemplatePath", "")));
            Assert.IsFalse(services.Any(s => s.ServiceType == typeof(DemoWorkspaces)));
            Assert.IsTrue(services.Any(s => s.ServiceType == typeof(DemoOptions)));
        }

        [TestMethod]
        public void Auth_mode_none_is_refused()
            => StringAssert.Contains(Fails(Config(("Auth:Mode", "None"))).Message, "Auth:Mode=Oidc");

        [TestMethod]
        public void A_real_identity_provider_is_refused()
            => StringAssert.Contains(Fails(Config(("IdentityAdmin:Provider", "Keycloak"))).Message, "IdentityAdmin");

        [TestMethod]
        public void Missing_paths_are_refused()
        {
            StringAssert.Contains(Fails(Config(("Demo:TemplatePath", ""))).Message, "Demo:TemplatePath");
            StringAssert.Contains(Fails(Config(("Demo:WorkspacesPath", ""))).Message, "Demo:WorkspacesPath");
            StringAssert.Contains(Fails(Config(("Demo:TemplatePath", Path.Combine(_dir, "nope")))).Message, "does not exist");
        }

        [TestMethod]
        public void Nested_template_and_workspaces_are_refused()
        {
            var template = Path.Combine(_dir, "template");
            StringAssert.Contains(Fails(Config(("Demo:WorkspacesPath", Path.Combine(template, "ws")))).Message, "separate directories");
            StringAssert.Contains(Fails(Config(("Demo:WorkspacesPath", template))).Message, "separate directories");
            StringAssert.Contains(Fails(Config(("Demo:WorkspacesPath", _dir))).Message, "separate directories");
        }

        [TestMethod]
        public void The_regular_config_source_cannot_overlap_the_sandbox()
        {
            StringAssert.Contains(Fails(Config(("ConfigSource:Path", Path.Combine(_dir, "workspaces", "x")))).Message, "ConfigSource:Path");
            StringAssert.Contains(Fails(Config(("ConfigSource:Path", Path.Combine(_dir, "template")))).Message, "ConfigSource:Path");
            new ServiceCollection().AddConfigServerDemo(Config(("ConfigSource:Path", Path.Combine(_dir, "elsewhere"))));
        }

        [TestMethod]
        public void Limits_must_be_positive()
        {
            StringAssert.Contains(Fails(Config(("Demo:IdleTtlMinutes", "0"))).Message, "IdleTtlMinutes");
            StringAssert.Contains(Fails(Config(("Demo:MaxWorkspaces", "0"))).Message, "MaxWorkspaces");
            StringAssert.Contains(Fails(Config(("Demo:RequestsPerMinute", "0"))).Message, "RequestsPerMinute");
        }

        [TestMethod]
        public void The_host_does_not_start_with_auth_mode_none()
        {
            var demo = Path.Combine(_dir, "template");
            Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                using var host = new AuthTestHost(settings: new Dictionary<string, string>
                {
                    ["Auth:Mode"] = "None",
                    ["Demo:Enabled"] = "true",
                    ["Demo:TemplatePath"] = demo,
                    ["Demo:WorkspacesPath"] = Path.Combine(_dir, "workspaces")
                });
            });
        }
    }
}
