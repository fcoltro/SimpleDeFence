using SimpleDeFence.Utilities;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class Pbkdf2Tests
    {
        /// <summary>A record produced by the pre-SHA256 scheme: PBKDF2-HMAC-SHA1, 150,000
        /// iterations, 16-byte output, salt stored as raw text. Hard-coded rather than generated so
        /// that this test keeps pinning the old format even after the code that wrote it is gone -
        /// an existing user's password file has to keep working across the upgrade.</summary>
        private const string LegacyPassword = "correct horse battery staple";
        private const string LegacyRecord =
            "Rfc2898;aB3xY7kQ;150000;16;" + LegacyHash;
        // Computed independently (Python hashlib.pbkdf2_hmac) against the old scheme's exact
        // parameters, so this pins the real legacy format rather than whatever the current code
        // happens to produce.
        private const string LegacyHash = "+gL5ur2nHQgl/LRW97Bhew==";

        [Fact]
        public void New_hashes_use_sha256_and_the_current_iteration_count()
        {
            var record = Pbkdf2.GetHashForStorage("hunter2");
            var fields = record.Split(';');

            Assert.Equal("Rfc2898-SHA256", fields[0]);
            Assert.Equal(Pbkdf2.DefaultIterations.ToString(), fields[2]);
            Assert.Equal(Pbkdf2.DefaultHashBytes.ToString(), fields[3]);
        }

        [Fact]
        public void A_new_hash_verifies_its_own_password()
        {
            var record = Pbkdf2.GetHashForStorage("hunter2");
            Assert.True(Pbkdf2.CompareHash(record, "hunter2"));
        }

        [Fact]
        public void A_new_hash_rejects_a_wrong_password()
        {
            var record = Pbkdf2.GetHashForStorage("hunter2");
            Assert.False(Pbkdf2.CompareHash(record, "hunter3"));
        }

        [Fact]
        public void Every_hash_gets_a_distinct_salt()
        {
            var first = Pbkdf2.GetHashForStorage("same password").Split(';')[1];
            var second = Pbkdf2.GetHashForStorage("same password").Split(';')[1];

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void The_salt_is_the_full_configured_length()
        {
            var salt = System.Convert.FromBase64String(Pbkdf2.GetHashForStorage("x").Split(';')[1]);
            Assert.Equal(Pbkdf2.DefaultSaltBytes, salt.Length);
        }

        [Fact]
        public void A_legacy_sha1_record_still_verifies_its_password()
        {
            // The upgrade must not lock out anyone who already had a password set.
            Assert.True(Pbkdf2.CompareHash(LegacyRecord, LegacyPassword));
        }

        [Fact]
        public void A_legacy_sha1_record_still_rejects_a_wrong_password()
        {
            Assert.False(Pbkdf2.CompareHash(LegacyRecord, "not the password"));
        }

        [Fact]
        public void Legacy_records_are_flagged_for_upgrade()
        {
            Assert.True(Pbkdf2.NeedsUpgrade(LegacyRecord));
        }

        [Fact]
        public void Current_records_are_not_flagged_for_upgrade()
        {
            Assert.False(Pbkdf2.NeedsUpgrade(Pbkdf2.GetHashForStorage("hunter2")));
        }

        [Fact]
        public void A_record_with_too_few_iterations_is_flagged_for_upgrade()
        {
            // Raising DefaultIterations in future must pull existing records up with it.
            var weak = "Rfc2898-SHA256;AAAAAAAAAAAAAAAAAAAAAA==;1000;32;AAAA";
            Assert.True(Pbkdf2.NeedsUpgrade(weak));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a record")]
        [InlineData("Rfc2898-SHA256;only;three;fields")]
        [InlineData("Unknown-Algorithm;AAAA;600000;32;AAAA")]
        [InlineData("Rfc2898-SHA256;!!!not base64!!!;600000;32;AAAA")]
        [InlineData("Rfc2898-SHA256;AAAA;notanumber;32;AAAA")]
        [InlineData("Rfc2898-SHA256;AAAA;0;32;AAAA")]
        public void Malformed_records_are_rejected_rather_than_throwing(string record)
        {
            // A corrupt or truncated password file must fail closed, not take the service down:
            // PasswordLock.Unlock runs this inside the firewall service.
            Assert.False(Pbkdf2.CompareHash(record, "anything"));
        }
    }
}
