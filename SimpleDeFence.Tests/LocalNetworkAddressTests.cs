using System.Net;
using SimpleDeFence.Utilities;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>The Connections page marks local traffic in green, so what counts as local decides
    /// what a user is told is harmless. Getting it wrong in the permissive direction is the bad
    /// direction: a public address shown green reads as "this stayed on your network" when it did
    /// not.</summary>
    public class LocalNetworkAddressTests
    {
        [Theory]
        [InlineData("127.0.0.1")]        // loopback
        [InlineData("10.0.0.5")]         // RFC1918 10/8
        [InlineData("172.16.0.1")]       // RFC1918 172.16/12, low edge
        [InlineData("172.31.255.254")]   // RFC1918 172.16/12, high edge
        [InlineData("192.168.1.1")]      // RFC1918 192.168/16
        [InlineData("169.254.10.20")]    // link-local
        [InlineData("224.0.0.251")]      // multicast (mDNS)
        [InlineData("::1")]              // IPv6 loopback
        [InlineData("fe80::1")]          // IPv6 link-local
        [InlineData("fd00::1")]          // IPv6 unique-local
        [InlineData("ff02::fb")]         // IPv6 multicast
        public void Local_addresses_are_recognised(string ip)
        {
            Assert.True(IpAddrMask.IsLocalNetwork(ip), $"{ip} should be local");
        }

        [Theory]
        [InlineData("8.8.8.8")]
        [InlineData("142.250.72.14")]
        [InlineData("203.0.113.9")]
        [InlineData("2606:4700:4700::1111")]
        public void Public_addresses_are_not_local(string ip)
        {
            Assert.False(IpAddrMask.IsLocalNetwork(ip), $"{ip} should not be local");
        }

        [Theory]
        [InlineData("172.15.0.1")]   // one below the private block
        [InlineData("172.32.0.1")]   // one above it
        [InlineData("11.0.0.1")]     // adjacent to 10/8
        [InlineData("192.167.1.1")]  // adjacent to 192.168/16
        [InlineData("192.169.1.1")]
        public void Addresses_just_outside_the_private_blocks_are_not_local(string ip)
        {
            // 172.16/12 is the easy one to get wrong - it is not 172.16/16 and not 172/8.
            Assert.False(IpAddrMask.IsLocalNetwork(ip), $"{ip} is outside the private ranges");
        }

        [Theory]
        [InlineData("0.0.0.0")]
        [InlineData("::")]
        public void The_unspecified_address_is_not_local(string ip)
        {
            // A socket bound to 0.0.0.0 is listening on every interface. That is the exposed case,
            // and marking it local would put the reassuring colour on exactly the wrong row.
            Assert.False(IpAddrMask.IsLocalNetwork(ip), $"{ip} means all interfaces, not local");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-an-address")]
        [InlineData("999.1.1.1")]
        public void Unusable_values_are_not_local(string? ip)
        {
            Assert.False(IpAddrMask.IsLocalNetwork(ip));
        }

        [Fact]
        public void The_IPAddress_overload_agrees_with_the_string_one()
        {
            Assert.True(IpAddrMask.IsLocalNetwork(IPAddress.Parse("192.168.0.10")));
            Assert.False(IpAddrMask.IsLocalNetwork(IPAddress.Parse("1.1.1.1")));
            Assert.False(IpAddrMask.IsLocalNetwork((IPAddress?)null));
        }
    }
}
