using SimpleDeFence.Localization;
using System.Collections.Generic;

namespace SimpleDeFence
{
    /// <summary>How a single firewall mode is presented to the user.</summary>
    public sealed class FirewallModeInfo
    {
        public FirewallMode Mode { get; }

        private readonly string _labelKey;
        private readonly string _descriptionKey;

        // Resolved on each access, not baked in at construction: this array is built once as a
        // static readonly field, so if Label/Description captured a string at that point they
        // would never reflect a later Loc.SetCulture call.
        public string Label => Loc.T(_labelKey);
        public string Description => Loc.T(_descriptionKey);

        public FirewallModeInfo(FirewallMode mode, string labelKey, string descriptionKey)
        {
            Mode = mode;
            _labelKey = labelKey;
            _descriptionKey = descriptionKey;
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
            new FirewallModeInfo(FirewallMode.Normal, LocKeys.Mode.NormalLabel, LocKeys.Mode.NormalDescription),
            new FirewallModeInfo(FirewallMode.BlockAll, LocKeys.Mode.BlockAllLabel, LocKeys.Mode.BlockAllDescription),
            new FirewallModeInfo(FirewallMode.AllowOutgoing, LocKeys.Mode.AllowOutgoingLabel, LocKeys.Mode.AllowOutgoingDescription),
            new FirewallModeInfo(FirewallMode.Disabled, LocKeys.Mode.DisabledLabel, LocKeys.Mode.DisabledDescription),
            new FirewallModeInfo(FirewallMode.Learning, LocKeys.Mode.LearningLabel, LocKeys.Mode.LearningDescription),
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
            return i >= 0 ? _selectable[i].Label : Loc.T(LocKeys.Common.Unknown);
        }

        public static string DescriptionFor(FirewallMode mode)
        {
            int i = IndexOf(mode);
            return i >= 0 ? _selectable[i].Description : string.Empty;
        }
    }
}
