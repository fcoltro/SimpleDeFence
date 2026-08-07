using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence;
using System;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    public sealed partial class StatusPage : Page
    {
        private FirewallMode _lastMode = FirewallMode.Unknown;
        private bool _lastLocked;
        private bool _connected;
        private bool _busy;

        // Set while the radio list is being reselected from server state, so the resulting
        // SelectionChanged is not mistaken for the user asking to switch modes.
        private bool _syncingSelection;

        // Labels match the WinForms GUI (Resources/Messages.resx) so both name modes identically
        // while they run side by side.
        private static readonly (FirewallMode Mode, string Label)[] ModeChoices =
        {
            (FirewallMode.Normal, "Normal"),
            (FirewallMode.BlockAll, "Block all"),
            (FirewallMode.AllowOutgoing, "Allow outgoing"),
            (FirewallMode.Disabled, "Disabled"),
            (FirewallMode.Learning, "Autolearn"),
        };

        public StatusPage()
        {
            InitializeComponent();

            foreach (var choice in ModeChoices)
                ModeRadios.Items.Add(choice.Label);

            Loaded += StatusPage_Loaded;
        }

        private async void StatusPage_Loaded(object sender, RoutedEventArgs e)
            => await RefreshAsync();

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
            => await RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_busy)
                return;

            SetBusy(true);
            StatusText.Text = "Connecting...";

            await App.Firewall.RefreshAsync();

            _connected = App.Firewall.Connected;
            if (App.Firewall.State is { } state)
            {
                _lastMode = state.Mode;
                _lastLocked = state.Locked;
            }

            if (!_connected)
                ShowNotice(InfoBarSeverity.Error, "Not connected", App.Firewall.LastError ?? string.Empty);

            SetBusy(false);
            UpdateDisplay();
        }

        private async void ModeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection || _busy || !_connected)
                return;

            int index = ModeRadios.SelectedIndex;
            if (index < 0 || index >= ModeChoices.Length)
                return;

            var mode = ModeChoices[index].Mode;
            if (mode == _lastMode)
                return;

            // Learning mode lets all traffic through, so it gets the same confirmation the
            // WinForms GUI shows before switching.
            if (mode == FirewallMode.Learning && !await ConfirmLearningModeAsync())
            {
                UpdateDisplay();    // put the selection back on the mode the service reports
                return;
            }

            await SwitchModeAsync(mode);
        }

        private async Task SwitchModeAsync(FirewallMode mode)
        {
            SetBusy(true);

            MessageType resp;
            try
            {
                resp = await App.Firewall.SwitchModeAsync(mode);
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowNotice(InfoBarSeverity.Error, "Could not reach the service", ex.Message);
                UpdateDisplay();
                return;
            }

            SetBusy(false);

            // Mirrors TinyWallController.SetMode/DefaultPopups, except that an unrecognised
            // response is reported as a failure rather than a success - on a firewall, a mode
            // switch that did not take must not look like it did.
            switch (resp)
            {
                case MessageType.MODE_SWITCH:
                    _lastMode = mode;
                    ShowNotice(InfoBarSeverity.Success, LabelFor(mode), DescriptionFor(mode));
                    break;

                case MessageType.RESPONSE_LOCKED:
                    _lastLocked = true;
                    ShowNotice(InfoBarSeverity.Warning, "SimpleDeFence is currently locked",
                        "Unlock the configuration before changing the mode.");
                    break;

                case MessageType.COM_ERROR:
                    ShowNotice(InfoBarSeverity.Error, "Communication with the service failed",
                        "The mode was not changed.");
                    break;

                default:
                    ShowNotice(InfoBarSeverity.Error, "Operation failed",
                        $"The service returned {resp}. The mode was not changed.");
                    break;
            }

            UpdateDisplay();
        }

        private async Task<bool> ConfirmLearningModeAsync()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Start automatic learning?",
                Content = "In automatic learning mode SimpleDeFence allows all traffic and remembers "
                        + "which applications used the network, then adds exceptions for them when you "
                        + "leave the mode. Rules cannot be learned for Special Exceptions.\n\n"
                        + "Only use this on a system you are confident is free of malware.",
                PrimaryButtonText = "Enter learning mode",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private void UpdateDisplay()
        {
            if (!_connected)
            {
                StatusText.Text = "Not connected";
                LockNote.Visibility = Visibility.Collapsed;
                ModeRadios.IsEnabled = false;
                return;
            }

            StatusText.Text = _lastLocked
                ? $"Connected - mode: {LabelFor(_lastMode)} (locked)"
                : $"Connected - mode: {LabelFor(_lastMode)}";

            _syncingSelection = true;
            // Unknown is not offered as a choice, so it clears the selection rather than picking one.
            ModeRadios.SelectedIndex = IndexOf(_lastMode);
            _syncingSelection = false;

            LockNote.Visibility = _lastLocked ? Visibility.Visible : Visibility.Collapsed;
            ModeRadios.IsEnabled = !_busy && !_lastLocked;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            Busy.IsActive = busy;
            RefreshButton.IsEnabled = !busy;
            ModeRadios.IsEnabled = !busy && _connected && !_lastLocked;
        }

        private void ShowNotice(InfoBarSeverity severity, string title, string message)
        {
            Notice.Severity = severity;
            Notice.Title = title;
            Notice.Message = message;
            Notice.IsOpen = true;
        }

        private static int IndexOf(FirewallMode mode)
        {
            for (int i = 0; i < ModeChoices.Length; ++i)
            {
                if (ModeChoices[i].Mode == mode)
                    return i;
            }
            return -1;
        }

        private static string LabelFor(FirewallMode mode)
        {
            int i = IndexOf(mode);
            return i >= 0 ? ModeChoices[i].Label : "Unknown";
        }

        private static string DescriptionFor(FirewallMode mode) => mode switch
        {
            FirewallMode.Normal => "The firewall is now operating as recommended.",
            FirewallMode.AllowOutgoing => "The firewall now allows outgoing connections.",
            FirewallMode.BlockAll => "The firewall is now blocking all incoming and outgoing traffic.",
            FirewallMode.Disabled => "The firewall is now disabled.",
            FirewallMode.Learning => "The firewall is now learning while letting all traffic through.",
            _ => string.Empty,
        };
    }
}
