using System;
using System.Runtime.InteropServices;

namespace SimpleDeFence.Windows
{
    /// <summary>
    /// Asks Windows whether the machine is connected to a network the user marked Public - the
    /// choice Windows offers on first joining a network ("Public" or "Private" in Settings &gt;
    /// Network &amp; internet). A café, hotel or airport network is Public unless someone said
    /// otherwise, and that is the case public network lockdown exists for.
    ///
    /// Read through the Network List Manager, the same COM object the Settings page uses. Its
    /// interfaces are declared here by hand, in vtable order, because there is no managed wrapper
    /// for them in the framework; only the methods up to the one called are needed, but every
    /// earlier one has to be present to keep the slots where Windows puts them.
    /// </summary>
    public static class NetworkCategory
    {
        /// <summary>
        /// True when at least one connected network is in the Public category. A machine on a
        /// Private network and a Public one at the same time - wired at home and a phone hotspot,
        /// say - counts as on a Public network: the hotspot's other users can reach it either way.
        /// Throws when the Network List Manager cannot be reached; the caller decides what that
        /// means.
        /// </summary>
        public static bool IsConnectedToPublicNetwork()
        {
            object? manager = null;
            IEnumNetworks? networks = null;
            try
            {
                manager = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_NetworkListManager, true)!)!;
                Marshal.ThrowExceptionForHR(((INetworkListManager)manager).GetNetworks(NLM_ENUM_NETWORK_CONNECTED, out networks));

                while (networks.Next(1, out INetwork? network, out uint fetched) == S_OK && fetched == 1 && network is not null)
                {
                    try
                    {
                        if (network.GetCategory(out int category) == S_OK && category == NLM_NETWORK_CATEGORY_PUBLIC)
                            return true;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(network);
                    }
                }

                return false;
            }
            finally
            {
                if (networks is not null)
                    Marshal.ReleaseComObject(networks);
                if (manager is not null)
                    Marshal.ReleaseComObject(manager);
            }
        }

        private const int S_OK = 0;
        private const int NLM_ENUM_NETWORK_CONNECTED = 0x01;
        private const int NLM_NETWORK_CATEGORY_PUBLIC = 0x00;

        private static readonly Guid CLSID_NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

        [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetworkListManager
        {
            [PreserveSig] int GetNetworks(int flags, out IEnumNetworks networks);
        }

        [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IEnumNetworks
        {
            [PreserveSig] int get__NewEnum(out IntPtr enumVariant);
            [PreserveSig] int Next(uint celt, out INetwork? network, out uint fetched);
        }

        [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface INetwork
        {
            [PreserveSig] int GetName(out IntPtr name);
            [PreserveSig] int SetName(IntPtr name);
            [PreserveSig] int GetDescription(out IntPtr description);
            [PreserveSig] int SetDescription(IntPtr description);
            [PreserveSig] int GetNetworkId(out Guid id);
            [PreserveSig] int GetDomainType(out int domainType);
            [PreserveSig] int GetNetworkConnections(out IntPtr connections);
            [PreserveSig] int GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
            [PreserveSig] int get_IsConnectedToInternet(out short connected);
            [PreserveSig] int get_IsConnected(out short connected);
            [PreserveSig] int GetConnectivity(out int connectivity);
            [PreserveSig] int GetCategory(out int category);
        }
    }
}
