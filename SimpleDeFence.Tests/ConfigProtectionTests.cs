using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SimpleDeFence.Utilities;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class ConfigProtectionTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _configPath;

        public ConfigProtectionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdf-cfgprot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _configPath = Path.Combine(_dir, "config");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Protected_bytes_round_trip()
        {
            var plaintext = Encoding.UTF8.GetBytes("the firewall configuration");

            var sealed_ = ConfigProtection.Protect(plaintext, _configPath);
            Assert.True(ConfigProtection.TryUnprotect(sealed_, _configPath, out var recovered));
            Assert.Equal(plaintext, recovered);
        }

        [Fact]
        public void The_plaintext_is_not_present_in_the_output()
        {
            var plaintext = Encoding.UTF8.GetBytes("BlockAllTraffic=true");
            var sealed_ = ConfigProtection.Protect(plaintext, _configPath);

            Assert.DoesNotContain("BlockAllTraffic", Encoding.UTF8.GetString(sealed_));
        }

        [Fact]
        public void A_tampered_ciphertext_is_rejected()
        {
            // The point of moving to AES-GCM: under the old CBC-with-a-public-key scheme anyone
            // could author a config the service would load as authentic.
            var sealed_ = ConfigProtection.Protect(Encoding.UTF8.GetBytes("payload"), _configPath);
            sealed_[sealed_.Length - 1] ^= 0xFF;

            Assert.False(ConfigProtection.TryUnprotect(sealed_, _configPath, out _));
        }

        [Fact]
        public void A_tampered_nonce_is_rejected()
        {
            var sealed_ = ConfigProtection.Protect(Encoding.UTF8.GetBytes("payload"), _configPath);
            sealed_[6] ^= 0xFF; // inside the nonce, which follows the 5-byte magic

            Assert.False(ConfigProtection.TryUnprotect(sealed_, _configPath, out _));
        }

        [Fact]
        public void Each_write_uses_a_fresh_nonce()
        {
            // A repeated nonce under one key breaks GCM outright, and a constant IV is precisely
            // what the old scheme did.
            var plaintext = Encoding.UTF8.GetBytes("identical input");
            var first = ConfigProtection.Protect(plaintext, _configPath);
            var second = ConfigProtection.Protect(plaintext, _configPath);

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void A_file_from_another_installation_does_not_decrypt()
        {
            // Different config path => different key file => the ciphertext is useless.
            var sealed_ = ConfigProtection.Protect(Encoding.UTF8.GetBytes("secret"), _configPath);
            var otherPath = Path.Combine(_dir, "other-install-config");

            Assert.False(ConfigProtection.TryUnprotect(sealed_, otherPath, out _));
        }

        [Fact]
        public void Legacy_cbc_configs_are_read_and_migrated()
        {
            // Upgrading must not cost anyone their rules. Written with the old scheme's shape -
            // AES-CBC, ASCII key and IV - through the same public API the old build used.
            const string key = "0123456789abcdef";
            const string iv = "fedcba9876543210";
            WriteLegacyCbcFile(new ClientSettings { UiTheme = "dark", Language = "pt-BR" }, _configPath, key, iv);

            var restored = SerializationHelper.DeserializeFromEncryptedFile(
                _configPath, key, iv, new ClientSettings());

            Assert.Equal("dark", restored.UiTheme);
            Assert.Equal("pt-BR", restored.Language);

            // ...and the file is now in the authenticated format, so the next read takes that path.
            Assert.True(ConfigProtection.HasMagic(File.ReadAllBytes(_configPath)));
        }

        [Fact]
        public void A_migrated_config_still_reads_back()
        {
            const string key = "0123456789abcdef";
            const string iv = "fedcba9876543210";
            WriteLegacyCbcFile(new ClientSettings { UiTheme = "light" }, _configPath, key, iv);

            SerializationHelper.DeserializeFromEncryptedFile(_configPath, key, iv, new ClientSettings());
            var second = SerializationHelper.DeserializeFromEncryptedFile(_configPath, key, iv, new ClientSettings());

            Assert.Equal("light", second.UiTheme);
        }

        [Fact]
        public void A_legacy_file_is_refused_once_this_installation_has_migrated()
        {
            // The downgrade: the legacy reader's key is a compile-time constant, so a marker-less
            // file authenticates nothing. Once a key file exists this installation has already
            // migrated, and a file that has gone backwards was written by something other than us.
            const string key = "0123456789abcdef";
            const string iv = "fedcba9876543210";

            // Migrate, which is what creates the key file.
            SerializationHelper.SerializeToEncryptedFile(
                new ClientSettings { UiTheme = "dark" }, _configPath, key, iv);
            Assert.True(File.Exists(ConfigProtection.KeyFilePath(_configPath)));

            // Now drop a forged legacy config in its place, as anyone able to write the file could.
            WriteLegacyCbcFile(new ClientSettings { UiTheme = "light", Language = "fr" }, _configPath, key, iv);

            var restored = SerializationHelper.DeserializeFromEncryptedFile(
                _configPath, key, iv, new ClientSettings());

            Assert.Equal("auto", restored.UiTheme);   // defaults, not the forged content
            Assert.Equal("auto", restored.Language);
        }

        [Fact]
        public void A_legacy_file_is_still_read_when_no_key_file_exists_yet()
        {
            // The other half: refusing the downgrade must not break the upgrade it exists for.
            const string key = "0123456789abcdef";
            const string iv = "fedcba9876543210";
            WriteLegacyCbcFile(new ClientSettings { UiTheme = "dark" }, _configPath, key, iv);
            Assert.False(File.Exists(ConfigProtection.KeyFilePath(_configPath)));

            var restored = SerializationHelper.DeserializeFromEncryptedFile(
                _configPath, key, iv, new ClientSettings());

            Assert.Equal("dark", restored.UiTheme);
        }

        [Fact]
        public void A_corrupt_authenticated_file_is_left_alone_rather_than_overwritten()
        {
            // Failing to authenticate means "altered or written under another key", not "old
            // format". Rewriting it would destroy whatever is really there.
            SerializationHelper.SerializeToEncryptedFile(
                new ClientSettings { UiTheme = "dark" }, _configPath, "unused", "unused");
            var bytes = File.ReadAllBytes(_configPath);
            bytes[bytes.Length - 1] ^= 0xFF;
            File.WriteAllBytes(_configPath, bytes);

            var restored = SerializationHelper.DeserializeFromEncryptedFile(
                _configPath, "unused", "unused", new ClientSettings());

            Assert.Equal("auto", restored.UiTheme); // the default, not the tampered content
            Assert.Equal(bytes, File.ReadAllBytes(_configPath)); // untouched
        }

        [Fact]
        public void Round_trip_through_the_public_helper_preserves_settings()
        {
            var original = new ClientSettings { UiTheme = "dark", ConnectionsAutoRefreshSeconds = 42 };
            SerializationHelper.SerializeToEncryptedFile(original, _configPath, "unused", "unused");

            var restored = SerializationHelper.DeserializeFromEncryptedFile(
                _configPath, "unused", "unused", new ClientSettings());

            Assert.Equal("dark", restored.UiTheme);
            Assert.Equal(42, restored.ConnectionsAutoRefreshSeconds);
        }

        // The outcome the reader reports alongside what it returns. Every one of the refusals
        // below hands back the same default instance a first run gets, so without this the caller
        // cannot tell "nothing was configured yet" from "your configuration was rejected".

        [Fact]
        public void A_missing_file_reports_Missing()
        {
            SerializationHelper.DeserializeFromEncryptedFile(
                _configPath, "unused", "unused", new ClientSettings(), out var outcome);

            Assert.Equal(ConfigLoadOutcome.Missing, outcome);
        }

        [Fact]
        public void A_file_in_the_current_format_reports_Loaded()
        {
            SerializationHelper.SerializeToEncryptedFile(
                new ClientSettings { UiTheme = "dark" }, _configPath, "unused", "unused");

            SerializationHelper.DeserializeFromEncryptedFile(
                _configPath, "unused", "unused", new ClientSettings(), out var outcome);

            Assert.Equal(ConfigLoadOutcome.Loaded, outcome);
        }

        [Fact]
        public void A_legacy_file_reports_Migrated()
        {
            const string key = "0123456789abcdef";
            const string iv = "fedcba9876543210";
            WriteLegacyCbcFile(new ClientSettings { UiTheme = "dark" }, _configPath, key, iv);

            SerializationHelper.DeserializeFromEncryptedFile(
                _configPath, key, iv, new ClientSettings(), out var outcome);

            Assert.Equal(ConfigLoadOutcome.Migrated, outcome);
        }

        [Fact]
        public void A_corrupt_authenticated_file_reports_Unauthenticated()
        {
            SerializationHelper.SerializeToEncryptedFile(
                new ClientSettings { UiTheme = "dark" }, _configPath, "unused", "unused");
            var bytes = File.ReadAllBytes(_configPath);
            bytes[bytes.Length - 1] ^= 0xFF;
            File.WriteAllBytes(_configPath, bytes);

            SerializationHelper.DeserializeFromEncryptedFile(
                _configPath, "unused", "unused", new ClientSettings(), out var outcome);

            Assert.Equal(ConfigLoadOutcome.Unauthenticated, outcome);
        }

        [Fact]
        public void A_refused_downgrade_reports_DowngradeRefused()
        {
            const string key = "0123456789abcdef";
            const string iv = "fedcba9876543210";
            SerializationHelper.SerializeToEncryptedFile(
                new ClientSettings { UiTheme = "dark" }, _configPath, key, iv);
            WriteLegacyCbcFile(new ClientSettings { UiTheme = "light" }, _configPath, key, iv);

            SerializationHelper.DeserializeFromEncryptedFile(
                _configPath, key, iv, new ClientSettings(), out var outcome);

            Assert.Equal(ConfigLoadOutcome.DowngradeRefused, outcome);
        }

        [Fact]
        public void A_file_that_cannot_be_opened_reports_Unreadable_not_Missing()
        {
            // The distinction that matters to the service: an ACL or a sharing violation leaves a
            // perfectly good configuration on disk that it is not honouring, which is nothing like
            // a first run.
            SerializationHelper.SerializeToEncryptedFile(
                new ClientSettings { UiTheme = "dark" }, _configPath, "unused", "unused");

            using (new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                SerializationHelper.DeserializeFromEncryptedFile(
                    _configPath, "unused", "unused", new ClientSettings(), out var outcome);

                Assert.Equal(ConfigLoadOutcome.Unreadable, outcome);
            }
        }

        private static void WriteLegacyCbcFile<T>(T obj, string path, string key, string iv)
            where T : ISerializable<T>
        {
            using var symmetricKey = Aes.Create();
            symmetricKey.Mode = CipherMode.CBC;
            symmetricKey.Key = Encoding.ASCII.GetBytes(key);
            symmetricKey.IV = Encoding.ASCII.GetBytes(iv);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var cryptoStream = new CryptoStream(fs, symmetricKey.CreateEncryptor(), CryptoStreamMode.Write);
            SerializationHelper.Serialize(cryptoStream, obj);
        }
    }
}
