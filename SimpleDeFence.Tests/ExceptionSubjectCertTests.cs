using System.IO;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class ExceptionSubjectCertTests
    {
        /// <summary>A file carrying an *embedded* Authenticode signature.
        ///
        /// Not a Windows system binary: most of those (notepad.exe, ntdll.dll) are signed through a
        /// security catalog rather than embedded in the file, and the API under test only reads
        /// embedded signatures - so testing against one asserts a failure that is not a bug.
        /// Resolved at runtime, and the test skips rather than fails if the machine has none.</summary>
        private static string? FindEmbeddedSignedBinary()
        {
            foreach (var candidate in new[]
            {
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        [Fact]
        public void CertSubject_reads_the_authenticode_signer_of_a_signed_binary()
        {
            // CertSubject swallows every failure (catch {}), so without this test a change to the
            // certificate API would silently turn certificate-based rules into "no subject" and
            // nothing would fail loudly.
            var signed = FindEmbeddedSignedBinary();
            if (signed is null)
                return; // nothing embedded-signed on this machine to test against

            var subject = new ExecutableSubject(signed);

            Assert.False(string.IsNullOrEmpty(subject.CertSubject));
            Assert.Contains("Microsoft", subject.CertSubject!);
        }

        [Fact]
        public void CertSubject_is_null_for_an_unsigned_file()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sdf-unsigned-" + System.Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(tmp, "not a signed executable");
            try
            {
                Assert.Null(new ExecutableSubject(tmp).CertSubject);
            }
            finally
            {
                File.Delete(tmp);
            }
        }
    }
}
