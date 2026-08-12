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
                        SeedGeneral();
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

        private void SeedGeneral()
        {
            _clientSettings = ClientSettings.Load();
            ThemeCombo.SelectedIndex = _clientSettings.UiTheme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };
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
