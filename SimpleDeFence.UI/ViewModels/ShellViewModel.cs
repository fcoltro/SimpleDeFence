using Microsoft.UI.Dispatching;
using SimpleDeFence.Localization;
using SimpleDeFence.UI.Services;
using System.Collections.Generic;
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
        private readonly DispatcherQueue? _dispatcher;

        private string _modeLabel = Loc.T(LocKeys.Common.Connecting);
        private string _modeGlyph = "";      // Segoe Fluent: Unknown
        private string _modeStateKey = "Neutral";
        private string _statusLine = string.Empty;
        private bool _isConnected;
        private bool _isLocked;
        private bool _isBusy;
        private string _degradedMessage = string.Empty;

        public ShellViewModel(IFirewallClient client)
        {
            _client = client;

            // FirewallClient.CommandAsync refreshes after every command and raises Changed, so that
            // "callers reaching IFirewallClient directly (TrayIconService's mode submenu) get that
            // for free instead of each having to remember to refresh" - its words. The tray icon was
            // subscribed and this was not, so a mode switched from the tray menu repainted the tray
            // icon and left the chip showing the previous mode until something else refreshed it.
            //
            // Not unsubscribed: App.OnLaunched builds exactly one MainWindow, and closing it ends
            // the process, so this view model and the client it listens to have the same lifetime.
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            _client.Changed += OnClientChanged;
        }

        /// <summary>Changed completes on whichever thread the refresh behind it ran on, and Update()
        /// writes properties bound to XAML, so it is marshalled rather than assumed to be on the UI
        /// thread - the same reason TrayIconService.OnFirewallChanged does it.</summary>
        private void OnClientChanged(object? sender, System.EventArgs e)
        {
            if (_dispatcher is null || _dispatcher.HasThreadAccess)
                Update();
            else
                _dispatcher.TryEnqueue(Update);
        }

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

        /// <summary>
        /// What the service is failing to do, or empty when it is doing everything it is set to.
        /// The mode chip cannot carry this: a firewall in Normal that could not install its rules
        /// still reads "Normal", which is exactly the reassurance that should not be given.
        /// </summary>
        public string DegradedMessage
        {
            get => _degradedMessage;
            private set { if (Set(ref _degradedMessage, value)) OnPropertyChanged(nameof(IsDegraded)); }
        }

        public bool IsDegraded => !string.IsNullOrEmpty(DegradedMessage);

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

                // An unelevated instance is told apart from a genuinely absent service. The control
                // pipe admits Administrators and SYSTEM only, so running without elevation - which
                // app.manifest's highestAvailable permits, rather than refusing to start - produces
                // exactly the same connection failure as the service being stopped, and LastError
                // would offer the user an access-denied message they can do nothing with. Naming
                // the real cause, and the tray item that resolves it, is the difference between a
                // dead end and an instruction.
                StatusLine = Services.Elevation.IsElevated
                    ? (_client.LastError ?? string.Empty)
                    : Loc.T(LocKeys.Status.NotConnectedNeedsAdmin);
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

            // Not connected means we know nothing about the service's health, and an alarming
            // banner left over from the last connection would be a guess presented as fact.
            DegradedMessage = IsConnected
                ? DescribeDegradation(_client.State?.Degraded ?? ServiceDegradation.None)
                : string.Empty;

            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(CanSwitchMode));
        }

        /// <summary>One sentence per thing that is wrong, in the order they matter.</summary>
        private static string DescribeDegradation(ServiceDegradation degraded)
        {
            if (degraded == ServiceDegradation.None)
                return string.Empty;

            var reasons = new List<string>(5);

            if ((degraded & ServiceDegradation.InitializationFailed) != 0)
                reasons.Add(Loc.T(LocKeys.Status.Degraded.InitFailed));
            if ((degraded & ServiceDegradation.ConfigurationUnreadable) != 0)
                reasons.Add(Loc.T(LocKeys.Status.Degraded.ConfigUnreadable));
            if ((degraded & ServiceDegradation.RulesIncomplete) != 0)
                reasons.Add(Loc.T(LocKeys.Status.Degraded.RulesIncomplete));
            if ((degraded & ServiceDegradation.AppDatabaseUnavailable) != 0)
                reasons.Add(Loc.T(LocKeys.Status.Degraded.DatabaseUnavailable));
            if ((degraded & ServiceDegradation.HostsBlocklistUnavailable) != 0)
                reasons.Add(Loc.T(LocKeys.Status.Degraded.HostsUnavailable));

            return string.Join(" ", reasons);
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



