using Xunit;

namespace SimpleDeFence.Tests
{
    public class PublisherTrustTests
    {
        private const string Discord = "CN=Discord Inc., O=Discord Inc., L=San Francisco, S=California, C=US";
        private const string Microsoft = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        private static readonly string[] Trusted = { Discord, Microsoft };

        [Fact]
        public void A_validly_signed_program_from_a_trusted_publisher_is_trusted()
        {
            Assert.Equal(Discord, PublisherTrust.TrustedSubject(Trusted,
                @"C:\Users\me\AppData\Local\Discord\app-1.0.9\Discord.exe", Discord, certValid: true, @"C:\Windows"));
        }

        [Fact]
        public void The_subject_must_match_exactly_apart_from_case()
        {
            Assert.NotNull(PublisherTrust.TrustedSubject(Trusted, @"C:\Apps\a.exe", Discord.ToUpperInvariant(), true, @"C:\Windows"));
            Assert.Null(PublisherTrust.TrustedSubject(Trusted, @"C:\Apps\a.exe", "CN=Discord Inc.", true, @"C:\Windows"));
        }

        [Fact]
        public void A_signature_Windows_does_not_verify_is_not_trusted()
        {
            Assert.Null(PublisherTrust.TrustedSubject(Trusted, @"C:\Apps\a.exe", Discord, certValid: false, @"C:\Windows"));
        }

        [Fact]
        public void An_unsigned_program_is_not_trusted()
        {
            Assert.Null(PublisherTrust.TrustedSubject(Trusted, @"C:\Apps\a.exe", null, certValid: false, @"C:\Windows"));
        }

        [Fact]
        public void Windows_components_are_never_trusted_by_publisher()
        {
            Assert.Null(PublisherTrust.TrustedSubject(Trusted, @"C:\Windows\System32\svchost.exe", Microsoft, true, @"C:\Windows"));
            Assert.NotNull(PublisherTrust.TrustedSubject(Trusted, @"C:\Program Files\Microsoft VS Code\Code.exe", Microsoft, true, @"C:\Windows"));
        }

        [Theory]
        [InlineData(Discord, "Discord Inc.")]
        [InlineData("CN=\"Foo, Inc.\", O=\"Foo, Inc.\", C=US", "Foo, Inc.")]
        [InlineData("O=Only Org, C=US", "Only Org")]
        [InlineData("C=US", "C=US")]
        [InlineData("CN=\"Say \"\"hi\"\"\", C=US", "Say \"hi\"")]
        public void The_display_name_is_the_common_name(string subject, string expected)
        {
            Assert.Equal(expected, PublisherTrust.DisplayName(subject));
        }
    }
}
