using System;
using System.IO;
using System.Text;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>Guards the check that stands between a downloaded file and Process.Start. The GUI
    /// updater used to run whatever arrived without consulting DownloadHash at all, while the
    /// service's own updater had always verified - these pin the rule that anything unverified is
    /// refused, including the cases that are easy to let through by accident.</summary>
    public class PayloadVerificationTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _file;
        private const string Payload = "pretend this is an installer";

        // sha256("pretend this is an installer"), computed by Hasher itself in the ctor.
        private readonly string _realHash;

        public PayloadVerificationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdf-verify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "payload.msi");
            File.WriteAllText(_file, Payload, Encoding.UTF8);
            _realHash = Hasher.HashFile(_file);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void A_matching_hash_verifies()
        {
            Assert.True(Hasher.FileMatchesHash(_file, _realHash));
        }

        [Fact]
        public void Case_differences_in_the_published_hash_still_verify()
        {
            // The descriptor is hand-edited at release time; hex casing must not decide whether an
            // update installs.
            Assert.True(Hasher.FileMatchesHash(_file, _realHash.ToUpperInvariant()));
            Assert.True(Hasher.FileMatchesHash(_file, _realHash.ToLowerInvariant()));
        }

        [Fact]
        public void A_wrong_hash_is_refused()
        {
            var wrong = new string('a', _realHash.Length);
            Assert.False(Hasher.FileMatchesHash(_file, wrong));
        }

        [Fact]
        public void Altering_the_file_by_one_byte_is_refused()
        {
            File.WriteAllText(_file, Payload + "!", Encoding.UTF8);
            Assert.False(Hasher.FileMatchesHash(_file, _realHash));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_descriptor_that_publishes_no_hash_is_refused(string? published)
        {
            // The live updates/update.json ships DownloadHash as "". Treating absent as "nothing to
            // check, go ahead" is exactly how unsigned code gets executed, so absent must be a
            // refusal, not a pass.
            Assert.False(Hasher.FileMatchesHash(_file, published));
        }

        [Fact]
        public void A_file_that_cannot_be_read_is_refused()
        {
            var missing = Path.Combine(_dir, "not-here.msi");
            Assert.False(Hasher.FileMatchesHash(missing, _realHash));
        }
    }
}
