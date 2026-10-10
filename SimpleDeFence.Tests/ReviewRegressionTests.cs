using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using SimpleDeFence.Utilities;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>Pins the fixes from the October 2026 code review, each at the smallest unit the
    /// test project can reach.</summary>
    public class ReviewRegressionTests
    {
        [Theory]
        [InlineData(AppExceptionTimer.For_5_Minutes, 5)]
        [InlineData(AppExceptionTimer.For_30_Minutes, 30)]
        [InlineData(AppExceptionTimer.For_1_Hour, 60)]
        [InlineData(AppExceptionTimer.For_4_Hours, 4 * 60)]
        [InlineData(AppExceptionTimer.For_9_Hours, 9 * 60)]
        [InlineData(AppExceptionTimer.For_24_Hours, 24 * 60)]
        public void Timer_values_are_the_minutes_their_names_say(AppExceptionTimer timer, int minutes)
        {
            // The service prunes with CreationDate.AddMinutes((int)Timer). For_24_Hours was 1140,
            // so "24 hours" ended after 19.
            Assert.Equal(minutes, (int)timer);
        }

        [Theory]
        [InlineData("10.0.0.0/256")]
        [InlineData("10.0.0.0/33")]
        [InlineData("10.0.0.0/-1")]
        [InlineData("10.0.0.0/+8")]
        [InlineData("fe80::/129")]
        public void Out_of_range_prefixes_are_refused(string text)
        {
            // Callers narrow PrefixLen with (byte), so /256 used to become /0 and match every address.
            Assert.ThrowsAny<Exception>(() => IpAddrMask.Parse(text));
        }

        [Theory]
        [InlineData("10.0.0.0/0", 0)]
        [InlineData("10.0.0.0/8", 8)]
        [InlineData("10.0.0.1/32", 32)]
        [InlineData("fe80::/128", 128)]
        public void In_range_prefixes_parse(string text, int prefix)
        {
            Assert.Equal(prefix, IpAddrMask.Parse(text).PrefixLen);
        }

        [Fact]
        public void The_mask_constructor_checks_the_prefix_too()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new IpAddrMask(IPAddress.Parse("10.0.0.0"), 40));
        }

        [Theory]
        [InlineData("https://github.com/fcoltro/SimpleDeFence/releases/download/v1.0.1/SimpleDeFence_x64.msi", true)]
        [InlineData("https://raw.githubusercontent.com/fcoltro/SimpleDeFence/refs/heads/main/updates/profiles.json.gz", true)]
        [InlineData("http://github.com/fcoltro/SimpleDeFence/releases/download/v1.0.1/SimpleDeFence_x64.msi", false)]
        [InlineData("https://github.com/someoneelse/SimpleDeFence/releases/download/v1/x.msi", false)]
        [InlineData("https://evil.example/fcoltro/SimpleDeFence/x.msi", false)]
        [InlineData("https://github.com.evil.example/fcoltro/SimpleDeFence/x.msi", false)]
        [InlineData("https://user@github.com/fcoltro/SimpleDeFence/x.msi", false)]
        [InlineData("https://github.com:8443/fcoltro/SimpleDeFence/x.msi", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Update_urls_are_pinned_to_this_projects_https_locations(string? url, bool allowed)
        {
            Assert.Equal(allowed, UpdateUrlPolicy.IsAllowed(url));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("٨٠")]   // Arabic-Indic digits: char.IsDigit says yes, a port number must not
        public void Port_numbers_accept_ascii_digits_only(string text)
        {
            Assert.Throws<FormatException>(() => text.AsSpan().DecimalToUInt16());
        }

        [Fact]
        public void Normalize_keeps_merging_after_dropping_an_older_duplicate_id()
        {
            // [A (older), B, C, A' (newer, same id as A)]. A is dropped in favour of A'. B and C
            // share a subject and must merge. Normalize used to carry on with the removed A, merge
            // C into it, and so lose C's ports.
            var subjectS = new ExecutableSubject(@"C:\Apps\s.exe");
            var subjectT = new ExecutableSubject(@"C:\Apps\t.exe");

            var a = new FirewallExceptionV3(subjectS, new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "21" })
            {
                CreationDate = new DateTime(2020, 1, 1),
            };
            var b = new FirewallExceptionV3(subjectS, new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "80" });
            var c = new FirewallExceptionV3(subjectS, new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "443" });
            var aNewer = new FirewallExceptionV3(subjectT, new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "22" })
            {
                Id = a.Id,
                CreationDate = new DateTime(2024, 1, 1),
            };

            var profile = new ServerProfileConfiguration("test")
            {
                AppExceptions = new List<FirewallExceptionV3> { a, b, c, aNewer },
            };

            profile.Normalize();

            Assert.Equal(2, profile.AppExceptions.Count);
            Assert.Contains(aNewer, profile.AppExceptions);
            Assert.DoesNotContain(a, profile.AppExceptions);

            var merged = Assert.Single(profile.AppExceptions, e => e.Subject.Equals(subjectS));
            var ports = ((TcpUdpPolicy)merged.Policy).AllowedRemoteTcpConnectPorts!.Split(',');
            Assert.Contains("80", ports);
            Assert.Contains("443", ports);
        }
    }
}
