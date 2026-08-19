using SimpleDeFence.Localization;
using SimpleDeFence.UI.Services;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.ViewModels
{
    /// <summary>
    /// Backs the always-visible mode chip. Status is ambient rather than a destination, so this
    /// lives on the shell instead of a Status page.
    /// </summary>
    internal sealed class ShellViewModel : ObservableObject
    {
        private readonly IFirewallClient _client;

        private string _modeLabel = Loc.T(LocKeys.Common.Connecting);
        private string _modeGlyph = "";      // Segoe Fluent: Unknown
        private string _modeStateKey = "Neutral";
        private string _statusLine = string.Empty;
        private bool _isConnected;
        private bool _isLocked;
        private bool _isBusy;

        public ShellViewModel(IFirewallClient client) => _client = client;

        public string ModeLabel { get => _modeLabel; private set => Set(ref _modeLabel, value); }
        public string ModeGlyph { get => _modeGlyph; private set => Set(ref _modeGlyph, value); }
        public string ModeStateKey { get => _modeStateKey; private set => Set(ref _modeStateKey, value); }
        public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }
        // CanSwitchMode is computed from these three and has no backing field of its own, so each
        // must announce it. Without this a bound control keeps its old enabled state - including
        // staying enabled during an in-flight switch, which invites overlapping pipe calls.
        public bool IsConnected
        {
            get => _isConnected;
            private set { if (Set(ref _isConnected, value)) OnPropertyChanged(nameof(CanSwitchMode)); }
        }
        public bool IsLocked
        {
            get => _isLocked;
            private set { if (Set(ref _isLocked, value)) OnPropertyChanged(nameof(CanSwitchMode)); }
        }
        public bool IsBusy
        {
            get => _isBusy;
            private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(CanSwitchMode)); }
        }

        /// <summary>True only when the user may actually change the mode.</summary>
        public bool CanSwitchMode => IsConnected && !IsLocked && !IsBusy;

        public FirewallMode CurrentMode => _client.State?.Mode ?? FirewallMode.Unknown;

        public async Task RefreshAsync()
        {
            IsBusy = true;
            try
            {
                await _client.RefreshAsync();
            }
            finally
            {
                // A throw here must not leave the mode control disabled for the rest of the
                // app's life.
                IsBusy = false;
            }

            Update();
        }

        public async Task<MessageType> SwitchModeAsync(FirewallMode mode)
        {
            IsBusy = true;
            MessageType resp;
            try
            {
                resp = await _client.SwitchModeAsync(mode);
            }
            finally
            {
                IsBusy = false;
            }

            Update();
            return resp;
        }

        private void Update()
        {
            IsConnected = _client.Connected;
            IsLocked = _client.State?.Locked ?? false;

            var mode = CurrentMode;

            if (!IsConnected)
            {
                ModeLabel = Loc.T(LocKeys.Status.NotConnected);
                ModeGlyph = "";       // Segoe Fluent: Error
                ModeStateKey = "Neutral";
                StatusLine = _client.LastError ?? string.Empty;
            }
            else
            {
                ModeLabel = FirewallModes.LabelFor(mode);
                ModeGlyph = GlyphFor(mode);
                ModeStateKey = StateKeyFor(mode);
                StatusLine = IsLocked
                    ? Loc.T(LocKeys.Status.Locked)
                    : FirewallModes.DescriptionFor(mode);
            }

            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(CanSwitchMode));
        }

        // Segoe Fluent Icons glyphs. Paired with a colour AND a word everywhere they are used,
        // so status never depends on colour alone.
        //
        // Normal is the shield, not the padlock it used to be: this app has a second, unrelated
        // lock concept (IsLocked - the server locked behind a password, shown with E72E on the
        // Settings page), and spending the padlock on a mode made the two read as the same thing.
        // BlockAll was worse than vague - it was E785, the *open* padlock, i.e. the exact opposite
        // of what that mode does. Learning was E9CE, a question mark, which is the glyph for "no
        // idea" rather than "the firewall is learning".
        private static string GlyphFor(FirewallMode mode) => mode switch
        {
            FirewallMode.Normal => "",          // Shield - protected, as recommended
            FirewallMode.BlockAll => "",        // Circle-slash - nothing gets through
            FirewallMode.AllowOutgoing => "",   // Arrow leaving a box - outbound allowed
            FirewallMode.Disabled => "",        // Warning triangle - not protecting anything
            FirewallMode.Learning => "",        // Lightbulb - learning what to allow
            _ => "",                            // Exclamation in a circle - unknown state
        };

        private static string StateKeyFor(FirewallMode mode) => mode switch
        {
            FirewallMode.Normal => "Success",
            FirewallMode.BlockAll => "Information",
            FirewallMode.AllowOutgoing => "Caution",
            FirewallMode.Disabled => "Neutral",
            FirewallMode.Learning => "AccentAlt",
            _ => "Neutral",
        };
    }
}



