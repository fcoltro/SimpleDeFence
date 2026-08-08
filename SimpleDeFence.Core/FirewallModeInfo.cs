using System.Collections.Generic;

namespace SimpleDeFence
{
    /// <summary>How a single firewall mode is presented to the user.</summary>
    public sealed class FirewallModeInfo
    {
        public FirewallMode Mode { get; }
        public string Label { get; }
        public string Description { get; }

        public FirewallModeInfo(FirewallMode mode, string label, string description)
        {
            Mode = mode;
            Label = label;
            Description = description;
        }
    }

    /// <summary>
    /// The user-selectable firewall modes and their wording. Labels match the WinForms GUI's
    /// Messages.resx so both GUIs name modes identically while they run side by side.
    /// Lives in Core so it is shared and unit-testable without a UI.
    /// </summary>
    public static class FirewallModes
    {
        private static readonly FirewallModeInfo[] _selectable =
        {
            new FirewallModeInfo(FirewallMode.Normal, "Normal",
                "The firewall is operating as recommended."),
            new FirewallModeInfo(FirewallMode.BlockAll, "Block all",
                "The firewall is blocking all incoming and outgoing traffic."),
            new FirewallModeInfo(FirewallMode.AllowOutgoing, "Allow outgoing",
                "The firewall allows outgoing connections."),
            new FirewallModeInfo(FirewallMode.Disabled, "Disabled",
                "The firewall is disabled."),
            new FirewallModeInfo(FirewallMode.Learning, "Autolearn",
                "The firewall is learning while letting all traffic through."),
        };

        public static IReadOnlyList<FirewallModeInfo> Selectable => _selectable;

        /// <summary>Index into <see cref="Selectable"/>, or -1 for modes the user cannot pick.</summary>
        public static int IndexOf(FirewallMode mode)
        {
            for (int i = 0; i < _selectable.Length; ++i)
            {
                if (_selectable[i].Mode == mode)
                    return i;
            }
            return -1;
        }

        public static string LabelFor(FirewallMode mode)
        {
            int i = IndexOf(mode);
            return i >= 0 ? _selectable[i].Label : "Unknown";
        }

        public static string DescriptionFor(FirewallMode mode)
        {
            int i = IndexOf(mode);
            return i >= 0 ? _selectable[i].Description : string.Empty;
        }
    }
}
