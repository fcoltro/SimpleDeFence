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

        private string _modeLabel = "Connecting...";
        private string _modeGlyph = "";      // Segoe Fluent: Unknown
        private string _modeStateKey = "Neutral";
        private string _statusLine = string.Empty;
        private bool _isConnected;
        private bool _isLocked;
        private bool _busy;

        public ShellViewModel(IFirewallClient client) => _client = client;

        public string ModeLabel { get => _modeLabel; private set => Set(ref _modeLabel, value); }
        public string ModeGlyph { get => _modeGlyph; private set => Set(ref _modeGlyph, value); }
        public string ModeStateKey { get => _modeStateKey; private set => Set(ref _modeStateKey, value); }
        public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }
        public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
        public bool IsLocked { get => _isLocked; private set => Set(ref _isLocked, value); }
        public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }

        /// <summary>True only when the user may actually change the mode.</summary>
        public bool CanSwitchMode => IsConnected && !IsLocked && !IsBusy;

        public FirewallMode CurrentMode => _client.State?.Mode ?? FirewallMode.Unknown;

        public async Task RefreshAsync()
        {
            IsBusy = true;
            await _client.RefreshAsync();
            IsBusy = false;
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
                ModeLabel = "Not connected";
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
                    ? "Configuration is locked"
                    : FirewallModes.DescriptionFor(mode);
            }

            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(CanSwitchMode));
        }

        // Segoe Fluent Icons glyphs. Paired with a colour AND a word everywhere they are used,
        // so status never depends on colour alone.
        private static string GlyphFor(FirewallMode mode) => mode switch
        {
            FirewallMode.Normal => "",          // Lock (protected)
            FirewallMode.BlockAll => "",        // BlockContact (locked down)
            FirewallMode.AllowOutgoing => "",   // Forward (relaxed)
            FirewallMode.Disabled => "",        // Warning (inactive)
            FirewallMode.Learning => "",        // Info (transient)
            _ => "",
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

