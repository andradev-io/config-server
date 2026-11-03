using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ConfigServer.Domain.Security.ApiKeys
{
    /// <summary>
    /// Key format "cfs_{prefix}_{secret}": prefix = 8 chars [a-z0-9] (public id used for lookup),
    /// secret = 32 random bytes base64url. Stored hash = "sha256:" + hex of SHA-256 over the whole key (UTF-8).
    /// The secret is high-entropy, so a fast hash is enough (no password KDF needed).
    /// </summary>
    public static class ApiKeyToken
    {
        public const string Marker = "cfs_";
        public const int PrefixLength = 8;
        public const int SecretBytes = 32;
        public const string HashAlgorithm = "sha256:";

        private const string PrefixAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        private const int MaxKeyLength = 256;

        // Compared against when the prefix is unknown, so lookups take similar time.
        private static readonly string DummyHash = Hash(Marker + "00000000_" + new string('A', 43));

        public static string NewPrefix() => RandomNumberGenerator.GetString(PrefixAlphabet, PrefixLength);

        public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

        public static string Format(string prefix, string secret) => Marker + prefix + "_" + secret;

        /// <summary>Parses the prefix out of a presented key; false when the shape is wrong.</summary>
        public static bool TryParse(string key, out string prefix)
        {
            prefix = null;
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength) return false;
            if (!key.StartsWith(Marker, StringComparison.Ordinal)) return false;
            var separator = Marker.Length + PrefixLength;
            if (key.Length < separator + 2 || key[separator] != '_') return false;

            var candidate = key.Substring(Marker.Length, PrefixLength);
            foreach (var c in candidate)
                if (PrefixAlphabet.IndexOf(c) < 0) return false;
            for (var i = separator + 1; i < key.Length; i++)
                if (!IsBase64UrlChar(key[i])) return false;

            prefix = candidate;
            return true;
        }

        public static string Hash(string key)
            => HashAlgorithm + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key ?? string.Empty))).ToLowerInvariant();

        /// <summary>Constant-time comparison of the presented key's hash with the stored one.</summary>
        public static bool Verify(string key, string storedHash)
        {
            var expected = storedHash ?? DummyHash;
            var actual = Hash(key);
            var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
            return storedHash != null && equal;
        }

        private static bool IsBase64UrlChar(char c)
            => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
    }
}
