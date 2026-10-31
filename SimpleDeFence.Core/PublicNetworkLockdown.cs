using System;
using System.Collections.Generic;

namespace SimpleDeFence
{
    /// <summary>
    /// What public network lockdown turns off while the machine is on a network Windows calls
    /// Public. Kept out of the service so the list can be tested and read in one place.
    ///
    /// Everything here lets other machines on the same network reach this one: the "Allow local
    /// subnet" setting, and the special exceptions for sharing and remote access. At home they are
    /// what makes a printer or a second PC work; on a café's Wi-Fi the "local network" is every
    /// stranger in the room. Exceptions that only let this machine reach out - DNS, DHCP, time,
    /// updates, VPN - stay as they are, because without them the network does not work at all.
    ///
    /// Nothing is removed from the configuration. The rules are left out when they are assembled,
    /// and come back by themselves on the next network that is not Public.
    /// </summary>
    public static class PublicNetworkLockdown
    {
        /// <summary>Special exceptions (by their database Name) that are suspended on a Public
        /// network.</summary>
        public static IReadOnlyCollection<string> SuspendedSpecialExceptions => Suspended;

        private static readonly HashSet<string> Suspended = new(StringComparer.OrdinalIgnoreCase)
        {
            "File_and_Printer_Sharing",
            "Pingable_local_machine",
            "Windows_Network_Discovery",
            "Windows_Remote_Assistance",
            "Windows_Remote_Desktop",
        };

        /// <summary>True when lockdown is in force: the setting is on and a Public network is
        /// connected.</summary>
        public static bool Applies(ServerConfiguration config, bool onPublicNetwork)
            => config.PublicNetworkLockdown && onPublicNetwork;

        public static bool AllowsLocalSubnet(ServerConfiguration config, bool onPublicNetwork)
            => config.ActiveProfile.AllowLocalSubnet && !Applies(config, onPublicNetwork);

        public static bool AllowsSpecialException(ServerConfiguration config, bool onPublicNetwork, string name)
            => !(Applies(config, onPublicNetwork) && Suspended.Contains(name));
    }
}
