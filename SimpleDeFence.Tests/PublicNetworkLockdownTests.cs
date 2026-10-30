using Xunit;

namespace SimpleDeFence.Tests
{
    public class PublicNetworkLockdownTests
    {
        private static ServerConfiguration Config(bool lockdown, bool allowLocalSubnet = true)
        {
            var config = new ServerConfiguration { ActiveProfileName = "Default", PublicNetworkLockdown = lockdown };
            config.ActiveProfile.AllowLocalSubnet = allowLocalSubnet;
            return config;
        }

        [Fact]
        public void On_a_public_network_lockdown_overrides_allow_local_subnet()
        {
            Assert.False(PublicNetworkLockdown.AllowsLocalSubnet(Config(lockdown: true), onPublicNetwork: true));
        }

        [Fact]
        public void Off_a_public_network_allow_local_subnet_is_kept()
        {
            Assert.True(PublicNetworkLockdown.AllowsLocalSubnet(Config(lockdown: true), onPublicNetwork: false));
        }

        [Fact]
        public void With_lockdown_off_a_public_network_changes_nothing()
        {
            var config = Config(lockdown: false);
            Assert.True(PublicNetworkLockdown.AllowsLocalSubnet(config, onPublicNetwork: true));
            Assert.True(PublicNetworkLockdown.AllowsSpecialException(config, onPublicNetwork: true, "File_and_Printer_Sharing"));
        }

        [Fact]
        public void Lockdown_never_turns_on_a_local_subnet_the_user_left_off()
        {
            Assert.False(PublicNetworkLockdown.AllowsLocalSubnet(Config(lockdown: true, allowLocalSubnet: false), onPublicNetwork: false));
        }

        [Theory]
        [InlineData("File_and_Printer_Sharing", false)]
        [InlineData("windows_remote_desktop", false)]
        [InlineData("Windows_Network_Discovery", false)]
        [InlineData("Windows_DNS_Client", true)]
        [InlineData("Windows_DHCP_Client", true)]
        [InlineData("VPN_L2TP_IPSec", true)]
        public void Only_sharing_and_remote_access_are_suspended(string name, bool allowed)
        {
            Assert.Equal(allowed, PublicNetworkLockdown.AllowsSpecialException(Config(lockdown: true), onPublicNetwork: true, name));
        }
    }
}
