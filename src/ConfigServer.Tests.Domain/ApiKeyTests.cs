using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ConfigServer.Domain.Security;
using ConfigServer.Domain.Security.ApiKeys;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConfigServer.Tests.Domain
{
    internal sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestClass]
    public class ApiKeyTokenTests
    {
        [TestMethod]
        public void Generated_key_has_expected_shape_and_parses()
        {
            var prefix = ApiKeyToken.NewPrefix();
            var key = ApiKeyToken.Format(prefix, ApiKeyToken.NewSecret());

            Assert.AreEqual(8, prefix.Length);
            Assert.IsTrue(prefix.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)));
            StringAssert.StartsWith(key, "cfs_" + prefix + "_");
            Assert.IsTrue(key.Length - 13 >= 43, "secret must be >= 32 bytes base64url");
            Assert.IsTrue(ApiKeyToken.TryParse(key, out var parsed));
            Assert.AreEqual(prefix, parsed);
        }

        [TestMethod]
        public void Secrets_are_random()
        {
            Assert.AreNotEqual(ApiKeyToken.NewSecret(), ApiKeyToken.NewSecret());
            Assert.AreNotEqual(ApiKeyToken.NewPrefix(), ApiKeyToken.NewPrefix());
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("abc")]
        [DataRow("cfs_short_secret")]
        [DataRow("xyz_abcdefgh_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [DataRow("cfs_ABCDEFGH_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [DataRow("cfs_abcdefgh-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [DataRow("cfs_abcdefgh_AAAA AAAA")]
        [DataRow("cfs_abcdefgh_")]
        public void Malformed_keys_do_not_parse(string key)
        {
            Assert.IsFalse(ApiKeyToken.TryParse(key, out _));
        }

        [TestMethod]
        public void Hash_is_sha256_hex_and_verifies_in_constant_time_api()
        {
            var key = ApiKeyToken.Format(ApiKeyToken.NewPrefix(), ApiKeyToken.NewSecret());
            var hash = ApiKeyToken.Hash(key);

            StringAssert.StartsWith(hash, "sha256:");
            Assert.AreEqual(7 + 64, hash.Length);
            Assert.IsFalse(hash.Contains(key));
            Assert.IsTrue(ApiKeyToken.Verify(key, hash));
            Assert.IsFalse(ApiKeyToken.Verify(key + "x", hash));
            Assert.IsFalse(ApiKeyToken.Verify(key, null));
            Assert.IsFalse(ApiKeyToken.Verify(key, "sha256:00"));
        }
    }

    [TestClass]
    public class ApiKeyManagerTests
    {
        private ManualTime _time;
        private InMemoryApiKeyStore _store;
        private ApiKeyManager _manager;

        [TestInitialize]
        public void Setup()
        {
            _time = new ManualTime();
            _store = new InMemoryApiKeyStore();
            _manager = new ApiKeyManager(_store, _time);
        }

        [TestMethod]
        public async Task Create_defaults_to_all_apps_envs_and_read_and_stores_only_the_hash()
        {
            var issued = await _manager.CreateAsync(new ApiKeyCreateRequest { Name = "  ci  " });

            Assert.AreEqual("ci", issued.Key.Name);
            CollectionAssert.AreEqual(new[] { "*" }, issued.Key.Apps);
            CollectionAssert.AreEqual(new[] { "*" }, issued.Key.Environments);
            Assert.AreEqual(ApiKeyPermission.Read, issued.Key.Permission);
            Assert.AreEqual(_time.Now, issued.Key.CreatedAt);
            Assert.AreEqual(ApiKeyStatus.Active, issued.Key.StatusAt(_time.Now));
            StringAssert.StartsWith(issued.Secret, "cfs_" + issued.Key.Prefix + "_");

            var stored = (await _store.ListAsync()).Single();
            Assert.AreEqual(ApiKeyToken.Hash(issued.Secret), stored.Hash);
            Assert.IsFalse(stored.Hash.Contains(issued.Secret));
        }

        [TestMethod]
        public async Task Create_normalizes_scope_and_permission()
        {
            var issued = await _manager.CreateAsync(new ApiKeyCreateRequest
            {
                Name = "deploy",
                Apps = new[] { "web", "web", "api" },
                Environments = new[] { "prod", "PROD" },
                Permission = "WRITE",
                ExpiresAt = _time.Now.AddDays(1)
            });

            CollectionAssert.AreEqual(new[] { "web", "api" }, issued.Key.Apps);
            CollectionAssert.AreEqual(new[] { "prod" }, issued.Key.Environments);
            Assert.AreEqual(ApiKeyPermission.Write, issued.Key.Permission);

            var wildcard = await _manager.CreateAsync(new ApiKeyCreateRequest { Name = "w", Apps = new[] { "web", "*" } });
            CollectionAssert.AreEqual(new[] { "*" }, wildcard.Key.Apps);
        }

        [TestMethod]
        public async Task Create_rejects_invalid_requests()
        {
            async Task Rejects(ApiKeyCreateRequest request)
                => await Assert.ThrowsAsync<ApiKeyValidationException>(() => _manager.CreateAsync(request));

            await Rejects(null);
            await Rejects(new ApiKeyCreateRequest { Name = " " });
            await Rejects(new ApiKeyCreateRequest { Name = new string('a', 101) });
            await Rejects(new ApiKeyCreateRequest { Name = "x", Permission = "admin" });
            await Rejects(new ApiKeyCreateRequest { Name = "x", ExpiresAt = _time.Now });
            await Rejects(new ApiKeyCreateRequest { Name = "x", ExpiresAt = _time.Now.AddMinutes(-1) });
            await Rejects(new ApiKeyCreateRequest { Name = "x", Apps = Array.Empty<string>() });
            await Rejects(new ApiKeyCreateRequest { Name = "x", Apps = new[] { "../etc" } });
            await Rejects(new ApiKeyCreateRequest { Name = "x", Environments = new[] { "default" } });
            Assert.IsEmpty(await _store.ListAsync());
        }

        [TestMethod]
        public async Task Authenticate_accepts_the_issued_key_and_rejects_others()
        {
            var issued = await _manager.CreateAsync(new ApiKeyCreateRequest { Name = "ci" });

            Assert.IsTrue((await _manager.AuthenticateAsync(issued.Secret)).Succeeded);
            Assert.AreEqual("Malformed API key.", (await _manager.AuthenticateAsync("nope")).Failure);

            var wrongSecret = ApiKeyToken.Format(issued.Key.Prefix, ApiKeyToken.NewSecret());
            Assert.AreEqual("Invalid API key.", (await _manager.AuthenticateAsync(wrongSecret)).Failure);
            var unknownPrefix = ApiKeyToken.Format("zzzzzzzz", ApiKeyToken.NewSecret());
            Assert.AreEqual("Invalid API key.", (await _manager.AuthenticateAsync(unknownPrefix)).Failure);
        }

        [TestMethod]
        public async Task Expired_key_is_rejected()
        {
            var issued = await _manager.CreateAsync(new ApiKeyCreateRequest { Name = "ci", ExpiresAt = _time.Now.AddHours(1) });
            Assert.IsTrue((await _manager.AuthenticateAsync(issued.Secret)).Succeeded);

            _time.Now = _time.Now.AddHours(1);

            var result = await _manager.AuthenticateAsync(issued.Secret);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("API key expired.", result.Failure);
            Assert.AreEqual(ApiKeyStatus.Expired, (await _store.GetAsync(issued.Key.Id)).StatusAt(_time.Now));
        }

        [TestMethod]
        public async Task Revoke_is_idempotent_and_blocks_the_key()
        {
            var issued = await _manager.CreateAsync(new ApiKeyCreateRequest { Name = "ci" });

            var first = await _manager.RevokeAsync(issued.Key.Id);
            _time.Now = _time.Now.AddMinutes(5);
            var second = await _manager.RevokeAsync(issued.Key.Id);

            Assert.AreEqual(first.RevokedAt, second.RevokedAt);
            Assert.AreEqual(ApiKeyStatus.Revoked, second.StatusAt(_time.Now));
            Assert.AreEqual("API key revoked.", (await _manager.AuthenticateAsync(issued.Secret)).Failure);
            Assert.IsNull(await _manager.RevokeAsync("unknown"));
        }

        [TestMethod]
        public async Task Rotate_keeps_metadata_and_invalidates_old_secret()
        {
            var issued = await _manager.CreateAsync(new ApiKeyCreateRequest { Name = "ci", Apps = new[] { "web" }, Permission = "write" });

            var rotated = await _manager.RotateAsync(issued.Key.Id);

            Assert.AreEqual(issued.Key.Id, rotated.Key.Id);
            Assert.AreEqual(issued.Key.Prefix, rotated.Key.Prefix);
            Assert.AreEqual(issued.Key.CreatedAt, rotated.Key.CreatedAt);
            CollectionAssert.AreEqual(issued.Key.Apps, rotated.Key.Apps);
            Assert.AreEqual(issued.Key.Permission, rotated.Key.Permission);
            Assert.AreNotEqual(issued.Secret, rotated.Secret);
            Assert.IsFalse((await _manager.AuthenticateAsync(issued.Secret)).Succeeded);
            Assert.IsTrue((await _manager.AuthenticateAsync(rotated.Secret)).Succeeded);
        }

        [TestMethod]
        public async Task Rotate_unknown_returns_null_and_revoked_throws()
        {
            Assert.IsNull(await _manager.RotateAsync("unknown"));

            var issued = await _manager.CreateAsync(new ApiKeyCreateRequest { Name = "ci" });
            await _manager.RevokeAsync(issued.Key.Id);

            await Assert.ThrowsAsync<ApiKeyValidationException>(() => _manager.RotateAsync(issued.Key.Id));
            Assert.AreEqual(issued.Key.Hash, (await _store.GetAsync(issued.Key.Id)).Hash);
        }

        [TestMethod]
        public async Task Last_used_is_written_at_most_once_per_interval()
        {
            var issued = await _manager.CreateAsync(new ApiKeyCreateRequest { Name = "ci" });
            var start = _time.Now;

            await _manager.AuthenticateAsync(issued.Secret);
            Assert.AreEqual(start, (await _store.GetAsync(issued.Key.Id)).LastUsedAt);

            _time.Now = start.AddSeconds(30);
            await _manager.AuthenticateAsync(issued.Secret);
            Assert.AreEqual(start, (await _store.GetAsync(issued.Key.Id)).LastUsedAt);

            _time.Now = start.AddSeconds(61);
            await _manager.AuthenticateAsync(issued.Secret);
            Assert.AreEqual(start.AddSeconds(61), (await _store.GetAsync(issued.Key.Id)).LastUsedAt);
        }

        [TestMethod]
        public async Task Usage_write_does_not_undo_a_concurrent_revoke()
        {
            var issued = await _manager.CreateAsync(new ApiKeyCreateRequest { Name = "ci" });
            await _manager.RevokeAsync(issued.Key.Id);

            // A stale snapshot updated by the store's atomic update keeps RevokedAt.
            var updated = await _store.UpdateAsync(issued.Key.Id, k => k with { LastUsedAt = _time.Now });

            Assert.IsNotNull(updated.RevokedAt);
        }

        [TestMethod]
        public void Scope_checks_use_wildcards_and_case_rules()
        {
            var key = new ApiKey { Apps = new[] { "web" }, Environments = new[] { "prod" } };

            Assert.IsTrue(key.AllowsApp("web"));
            Assert.IsFalse(key.AllowsApp("WEB"));
            Assert.IsFalse(key.AllowsApp("api"));
            Assert.IsTrue(key.AllowsEnvironment("PROD"));
            Assert.IsFalse(key.AllowsEnvironment("dev"));
            Assert.IsTrue(new ApiKey().AllowsApp("anything"));
            Assert.IsTrue(new ApiKey().AllowsEnvironment("anything"));
        }

        [TestMethod]
        public void Permission_roles_never_include_admin()
        {
            CollectionAssert.AreEqual(new[] { ConfigRoles.Read }, ApiKeyPermission.Roles("read"));
            CollectionAssert.AreEqual(new[] { ConfigRoles.Read, ConfigRoles.Write }, ApiKeyPermission.Roles("write"));
            CollectionAssert.DoesNotContain(ApiKeyPermission.Roles("admin"), ConfigRoles.Admin);
        }
    }

    [TestClass]
    public class ApiKeyStoreTests
    {
        private string _dir;

        [TestInitialize]
        public void Setup() => _dir = Path.Combine(Path.GetTempPath(), "cfgkeys-" + Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static ApiKey Key(string id, string prefix) => new ApiKey
        {
            Id = id,
            Name = "key " + id,
            Prefix = prefix,
            Hash = ApiKeyToken.Hash(id),
            Apps = new[] { "web" },
            CreatedAt = DateTimeOffset.UnixEpoch
        };

        private IApiKeyStore[] Stores() => new IApiKeyStore[] { new InMemoryApiKeyStore(), new JsonFileApiKeyStore(Path.Combine(_dir, "keys.json")) };

        [TestMethod]
        public async Task Stores_add_get_find_list_update()
        {
            foreach (var store in Stores())
            {
                await store.AddAsync(Key("1", "aaaaaaaa"));
                await store.AddAsync(Key("2", "bbbbbbbb"));

                Assert.AreEqual("key 1", (await store.GetAsync("1")).Name);
                Assert.AreEqual("2", (await store.FindByPrefixAsync("bbbbbbbb")).Id);
                Assert.IsNull(await store.GetAsync("3"));
                Assert.IsNull(await store.FindByPrefixAsync("cccccccc"));
                CollectionAssert.AreEqual(new[] { "1", "2" }, (await store.ListAsync()).Select(k => k.Id).ToArray());

                var updated = await store.UpdateAsync("1", k => k with { Name = "renamed" });
                Assert.AreEqual("renamed", updated.Name);
                Assert.AreEqual("renamed", (await store.GetAsync("1")).Name);
                Assert.IsNull(await store.UpdateAsync("3", k => k with { Name = "x" }));
            }
        }

        [TestMethod]
        public async Task Stores_reject_duplicates_and_identity_changes()
        {
            foreach (var store in Stores())
            {
                await store.AddAsync(Key("1", "aaaaaaaa"));

                await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(Key("1", "bbbbbbbb")));
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(Key("2", "aaaaaaaa")));
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync("1", k => k with { Prefix = "cccccccc" }));
                Assert.AreEqual(1, (await store.ListAsync()).Count);
            }
        }

        [TestMethod]
        public async Task File_store_persists_hashes_only_and_survives_a_new_instance()
        {
            var path = Path.Combine(_dir, "nested", "keys.json");
            var manager = new ApiKeyManager(new JsonFileApiKeyStore(path));
            var issued = await manager.CreateAsync(new ApiKeyCreateRequest { Name = "ci", Environments = new[] { "prod" } });

            var text = File.ReadAllText(path);
            Assert.IsFalse(text.Contains(issued.Secret));
            StringAssert.Contains(text, issued.Key.Hash);
            StringAssert.Contains(text, "\"keys\"");
            Assert.IsFalse(text.Contains("allApps"));
            Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp"));

            var reloaded = new ApiKeyManager(new JsonFileApiKeyStore(path));
            var result = await reloaded.AuthenticateAsync(issued.Secret);
            Assert.IsTrue(result.Succeeded);
            CollectionAssert.AreEqual(new[] { "prod" }, result.Key.Environments);
        }

        [TestMethod]
        public async Task File_store_reloads_after_external_change_and_fails_closed_on_corruption()
        {
            var path = Path.Combine(_dir, "keys.json");
            var store = new JsonFileApiKeyStore(path);
            Assert.IsEmpty(await store.ListAsync());

            await new JsonFileApiKeyStore(path).AddAsync(Key("1", "aaaaaaaa"));
            Assert.AreEqual(1, (await store.ListAsync()).Count);

            File.WriteAllText(path, "{ not json");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListAsync());
        }

        [TestMethod]
        public async Task File_store_serializes_concurrent_updates()
        {
            var path = Path.Combine(_dir, "keys.json");
            var store = new JsonFileApiKeyStore(path);
            await store.AddAsync(Key("1", "aaaaaaaa"));

            await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
                store.UpdateAsync("1", k => k with { Name = k.Name + "." })));

            Assert.AreEqual("key 1" + new string('.', 20), (await new JsonFileApiKeyStore(path).GetAsync("1")).Name);
        }
    }
}
