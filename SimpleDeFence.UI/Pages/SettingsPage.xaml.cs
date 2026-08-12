using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence.Localization;
using System;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private bool _busy;
        private bool _committing;

        /// <summary>Guards every Toggled/SelectionChanged handler against firing its own commit
        /// while SeedControls() is programmatically setting IsOn/SelectedIndex from the just-
        /// refreshed config - without this, seeding a ToggleSwitch's IsOn fires Toggled exactly as
        /// a user click would, which would recommit the value that is only being re-synced, not
        /// changed. Same rationale as SettingsForm.LoadingSettings (the WinForms equivalent this
        /// page replaces), which every one of its ItemCheck handlers checks first for the same
        /// reason.</summary>
        private bool _seeding;

        private ClientSettings _clientSettings = new();

        public SettingsPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
            Loaded += async (_, _) => await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (_busy)
                return;

            SetBusy(true);
            try
            {
                await App.Firewall.RefreshAsync();

                if (!App.Firewall.Connected || App.Firewall.Config is null)
                {
                    ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Status.NotConnected),
                        App.Firewall.LastError ?? string.Empty);
                }
                else
                {
                    Notice.IsOpen = false;
                    _seeding = true;
                    try
                    {
                        SeedControls();
                    }
                    finally
                    {
                        _seeding = false;
                    }
                }
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SeedControls()
        {
            _clientSettings = ClientSettings.Load();
            ThemeCombo.SelectedIndex = _clientSettings.UiTheme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };

            var config = App.Firewall.Config!;
            AllowLocalSubnetToggle.IsOn = config.ActiveProfile.AllowLocalSubnet;
            DisplayOffBlockToggle.IsOn = config.ActiveProfile.DisplayOffBlock;

            EnableBlocklistsToggle.IsOn = config.Blocklists.EnableBlocklists;
            EnableHostsBlocklistToggle.IsOn = config.Blocklists.EnableHostsBlocklist;
            EnablePortBlocklistToggle.IsOn = config.Blocklists.EnablePortBlocklist;
            UpdateBlocklistSubTogglesEnabled();
        }

        /// <summary>Hosts/Ports blocklist toggles are only meaningful while the master toggle is
        /// on - same disabled-when-master-off relationship WinForms' chkEnableBlocklists_
        /// CheckedChanged already has between chkEnableBlocklists and chkHostsBlocklist/
        /// chkBlockMalwarePorts.</summary>
        private void UpdateBlocklistSubTogglesEnabled()
        {
            var enabled = EnableBlocklistsToggle.IsOn;
            EnableHostsBlocklistToggle.IsEnabled = enabled;
            EnablePortBlocklistToggle.IsEnabled = enabled;
        }

        /// <summary>ComboBox.SelectionChanged is a plain top-level event, not nested inside
        /// another control's own dispatch, so committing directly here is safe per the Task 4
        /// (Rules) reentrancy rule - but the change is local-only (no server IPC), so there is
        /// nothing to defer regardless.</summary>
        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_seeding || ThemeCombo.SelectedItem is not ComboBoxItem item)
                return;

            var theme = (string)item.Tag;
            if (theme == _clientSettings.UiTheme)
                return;

            _clientSettings.UiTheme = theme;
            _clientSettings.Save();
            App.ApplyTheme(theme);
        }

        /// <summary>ToggleSwitch fires Toggled synchronously from inside its own event dispatch -
        /// deferring via DispatcherQueue.TryEnqueue before committing is the same fix Rules Task 4
        /// applied for the identical reentrancy hazard (see this plan's Global Constraints).</summary>
        private void AllowLocalSubnetToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // _seeding: ignore the Toggled fired by SeedControls programmatically setting IsOn to
            // the just-refreshed value - that is a re-sync, not a user change. _committing: refuse
            // a second commit while one is already in flight, the same guard
            // RulesPage.ToggleSpecialAsync uses, rather than the narrower _busy (which only guards
            // Refresh) this handler used before self-review caught the gap.
            if (_seeding || _committing) return;
            var value = AllowLocalSubnetToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.ActiveProfile.AllowLocalSubnet = value));
        }

        private void DisplayOffBlockToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = DisplayOffBlockToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.ActiveProfile.DisplayOffBlock = value));
        }

        private void EnableBlocklistsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = EnableBlocklistsToggle.IsOn;
            UpdateBlocklistSubTogglesEnabled();
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.Blocklists.EnableBlocklists = value));
        }

        private void EnableHostsBlocklistToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = EnableHostsBlocklistToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.Blocklists.EnableHostsBlocklist = value));
        }

        private void EnablePortBlocklistToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = EnablePortBlocklistToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.Blocklists.EnablePortBlocklist = value));
        }

        /// <summary>Shared by every immediate-commit toggle in this page (Protection, Blocklists,
        /// and later Updates/Security's Lock-hosts-file): commits, then refreshes either way so
        /// every toggle's visual state reconciles back to the server's truth - the same pattern
        /// RulesPage.ToggleSpecialAsync uses for the identical reason.</summary>
        private async Task CommitToggleAsync(Action<ServerConfiguration> mutate)
        {
            var resp = await CommitAsync(mutate);
            await RefreshAsync();

            if (resp != MessageType.PUT_SETTINGS)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.CommitFailedTitle), FailureDetail(resp,
                    LocKeys.Settings.CommitFailedLockedDetail, LocKeys.Settings.CommitFailedStaleDetail,
                    LocKeys.Settings.CommitFailedGenericDetail));
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        /// <summary>One serialized commit path for every server-side setting in this page,
        /// mirroring RulesPage.CommitAsync's shape exactly (same _committing guard, same
        /// exception -> InfoBar handling).</summary>
        private async Task<MessageType> CommitAsync(Action<ServerConfiguration> mutate)
        {
            _committing = true;
            UpdateControlsEnabled();
            try
            {
                return await App.Firewall.CommitConfigChangesAsync(mutate);
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
                return MessageType.COM_ERROR;
            }
            finally
            {
                _committing = false;
                UpdateControlsEnabled();
            }
        }

        /// <summary>Placeholder for now (only General exists, and it has nothing to disable while
        /// committing since it never reaches CommitAsync); Tasks 5-8 extend this to disable their
        /// own groups' controls while _committing is true, the same pattern
        /// RulesPage.UpdateRemoveButton/UpdateApplyButtonEnabled/UpdateAddButtonEnabled use.</summary>
        private void UpdateControlsEnabled()
        {
        }

        private static string FailureDetail(MessageType resp, string lockedKey, string staleKey, string genericKey) => resp switch
        {
            MessageType.RESPONSE_LOCKED => Loc.T(lockedKey),
            MessageType.RESPONSE_STALE_CHANGESET => Loc.T(staleKey),
            _ => Loc.T(genericKey, resp),
        };

        private Task ShowResultAsync(string title, string body)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = body,
                CloseButtonText = Loc.T(LocKeys.Common.Ok),
            };

            return TryShowDialogAsync(dialog, title, body);
        }

        /// <summary>Every ContentDialog.ShowAsync() call site in this page routes through here -
        /// same rationale as RulesPage.TryShowDialogAsync (only one ContentDialog can be open per
        /// XamlRoot; this page has no process-wide UnhandledException backstop).</summary>
        private async Task<ContentDialogResult> TryShowDialogAsync(ContentDialog dialog, string fallbackTitle, string fallbackMessage)
        {
            try
            {
                return await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                ShowNotice(InfoBarSeverity.Informational, fallbackTitle, fallbackMessage);
                return ContentDialogResult.None;
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            Busy.IsActive = busy;
            RefreshButton.IsEnabled = !busy;
        }

        private void ShowNotice(InfoBarSeverity severity, string title, string message)
        {
            Notice.Severity = severity;
            Notice.Title = title;
            Notice.Message = message;
            Notice.IsOpen = true;
        }
    }
}
