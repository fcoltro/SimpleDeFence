using Microsoft.UI.Dispatching;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence.Localization;
using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
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

            PopulateLanguages();
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if ((string)item.Tag == _clientSettings.Language)
                {
                    LanguageCombo.SelectedItem = item;
                    break;
                }
            }

            // A saved language whose file no longer ships would otherwise leave the picker blank,
            // which looks broken and hides what the app is actually doing - which is exactly what
            // "Use system setting" describes, since Loc has fallen back to resolving it itself.
            if (LanguageCombo.SelectedItem is null && LanguageCombo.Items.Count > 0)
                LanguageCombo.SelectedIndex = 0;
            AskForExceptionDetailsToggle.IsOn = _clientSettings.AskForExceptionDetails;
            EnableHotkeysToggle.IsOn = _clientSettings.EnableGlobalHotkeys;
            // Through the clamping property, so a hand-edited or legacy 0 shows as the default
            // rather than as a NumberBox pinned to its minimum.
            AutoRefreshIntervalBox.Value = _clientSettings.ConnectionsAutoRefreshInterval.TotalSeconds;

            ConnectionLogToggle.IsOn = _clientSettings.ConnectionLogEnabled;
            ConnectionLogPathBox.Text = _clientSettings.ConnectionLogPath;
            ConnectionLogIntervalBox.Value = _clientSettings.ConnectionLogInterval.TotalSeconds;
            ConnectionLogMaxSizeBox.Value = _clientSettings.ConnectionLogMaxFileSizeMb == 0
                ? ClientSettings.DefaultLogMaxFileSizeMb
                : _clientSettings.ConnectionLogMaxFileSizeMb;

            // A log that silently stopped writing is worse than no log, so the last write failure
            // is surfaced here rather than only living on the service.
            var logError = App.ConnectionLogError;
            if (_clientSettings.ConnectionLogEnabled && logError is not null)
                ShowNotice(InfoBarSeverity.Warning, Loc.T(LocKeys.Settings.LoggingWriteFailedTitle), logError);

            var config = App.Firewall.Config!;
            AllowLocalSubnetToggle.IsOn = config.ActiveProfile.AllowLocalSubnet;
            DisplayOffBlockToggle.IsOn = config.ActiveProfile.DisplayOffBlock;

            EnableBlocklistsToggle.IsOn = config.Blocklists.EnableBlocklists;
            EnableHostsBlocklistToggle.IsOn = config.Blocklists.EnableHostsBlocklist;
            EnablePortBlocklistToggle.IsOn = config.Blocklists.EnablePortBlocklist;
            UpdateBlocklistSubTogglesEnabled();

            LockHostsFileToggle.IsOn = config.LockHostsFile;

            var hasPassword = App.Firewall.State?.HasPassword ?? false;
            var locked = App.Firewall.State?.Locked ?? false;

            PasswordStatusText.Text = Loc.T(hasPassword ? LocKeys.Settings.SecurityPasswordSet : LocKeys.Settings.SecurityPasswordNotSet);
            PasswordNotSetDetailText.Visibility = hasPassword ? Visibility.Collapsed : Visibility.Visible;
            LockStatusText.Text = Loc.T(locked ? LocKeys.Settings.SecurityLockedStatus : LocKeys.Settings.SecurityUnlockedStatus);
            UnlockPanel.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;

            // RemovePasswordButton/LockNowButton's enabled state is derived from the same
            // hasPassword/locked/_committing inputs UpdateControlsEnabled() already computes for
            // every other button in this group - calling it here instead of re-deriving those two
            // booleans a second time keeps that derivation in exactly one place.
            UpdateControlsEnabled();

            AutoUpdateCheckToggle.IsOn = config.AutoUpdateCheck;

            // GetEntryAssembly, not GetExecutingAssembly. Executing is SimpleDeFence.UI, whose own
            // <Version> is 1.0.0 and has nothing to do with the product - About reported 1.0.0 for a
            // 0.1.0 build. Entry is SimpleDeFence.exe, which is what the installer and Add/Remove
            // Programs agree on. Informational version first so a suffix like -rc1 survives.
            var app = System.Reflection.Assembly.GetEntryAssembly();
            var version =
                app?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? app?.GetName().Version?.ToString()
                ?? "0.0.0";
            // Strip the +<commit> source-revision suffix the SDK appends; it is build plumbing.
            var plus = version.IndexOf('+');
            if (plus > 0)
                version = version.Substring(0, plus);

            AboutVersionCard.Header = Loc.T(LocKeys.Settings.AboutVersion, version);
            AboutVersionCard.Description = Loc.T(LocKeys.Settings.AboutTagline);
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
            SaveClientSettings();
            App.ApplyTheme(theme);
        }

        /// <summary>
        /// Builds the picker from the languages that actually ship rather than from a hand-kept
        /// list in XAML. Dropping in a new Strings.xx.json is then the whole job, and the list can
        /// never offer a language that silently resolves to English. Each language is named in
        /// itself - someone looking for Deutsch is not helped by an entry reading "German".
        /// </summary>
        private void PopulateLanguages()
        {
            if (LanguageCombo.Items.Count > 0)
                return;

            LanguageCombo.Items.Add(new ComboBoxItem
            {
                Content = Loc.T(LocKeys.Settings.GeneralLanguageAuto),
                Tag = "auto",
            });

            foreach (var (tag, name) in Loc.AvailableCultures()
                                           .Select(c => (Tag: c, Name: LanguageName(c)))
                                           .OrderBy(l => l.Name, StringComparer.CurrentCulture))
            {
                LanguageCombo.Items.Add(new ComboBoxItem { Content = name, Tag = tag });
            }
        }

        /// <summary>A language's own name for itself, capitalised. .NET's NativeName is lower case
        /// for several languages ("português (Brasil)", "français", "italiano"); mixed with the ones
        /// that are not ("English", "Deutsch") an uncapitalised entry reads as a typo.</summary>
        private static string LanguageName(string culture)
        {
            CultureInfo info;
            try
            {
                info = CultureInfo.GetCultureInfo(culture);
            }
            catch (CultureNotFoundException)
            {
                // Cannot happen for a shipped file, but a raw tag beats an empty row.
                return culture;
            }

            var name = info.NativeName;
            if (name.Length == 0)
                return culture;

            // ToUpper through the language's own TextInfo, not the machine's: Turkish capitalises
            // "i" as "I" only outside Turkish, where it is "İ".
            return info.TextInfo.ToUpper(name.Substring(0, 1)) + name.Substring(1);
        }

        /// <summary>Same local-only, no-server-IPC shape as ThemeCombo_SelectionChanged. Persists
        /// the choice and re-resolves Loc's active culture immediately - but per this task's scope
        /// (see the WinUI exe-merge plan's Task 7 notes), that only takes effect for code-behind
        /// labels that call Loc.T() on their next refresh (e.g. PasswordStatusText/LockStatusText/
        /// AboutVersionCard here). Static XAML {loc:Loc} bindings resolve once at
        /// InitializeComponent() time and only pick up a new culture on the next app launch, which
        /// is what Step 3 (App.xaml.cs's persisted-language startup check) provides.</summary>
        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_seeding || LanguageCombo.SelectedItem is not ComboBoxItem item)
                return;

            var language = (string)item.Tag;
            _clientSettings.Language = language;
            SaveClientSettings();

            App.SelectLanguage(language);

            // Said out loud rather than left for the user to notice: most of what is on screen was
            // resolved at InitializeComponent and will not change until the app is started again,
            // so a picker that appeared to do nothing would read as a broken setting.
            ShowNotice(InfoBarSeverity.Informational,
                Loc.T(LocKeys.Settings.GeneralLanguageRestartTitle),
                Loc.T(LocKeys.Settings.GeneralLanguageRestartDetail));
        }

        private void AskForExceptionDetailsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding)
                return;
            _clientSettings.AskForExceptionDetails = AskForExceptionDetailsToggle.IsOn;
            SaveClientSettings();
        }

        /// <summary>NumberBox raises ValueChanged while the page is still being populated, and an
        /// empty box reports NaN - both would otherwise write nonsense over a good setting. The
        /// running Connections page is not poked directly: it re-reads the interval on Loaded, and
        /// reaching it from here would mean this page holding a reference to another page.</summary>
        private void AutoRefreshIntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_seeding || double.IsNaN(args.NewValue))
                return;

            var seconds = (int)Math.Clamp(args.NewValue,
                ClientSettings.MinAutoRefreshSeconds, ClientSettings.MaxAutoRefreshSeconds);
            if (seconds == _clientSettings.ConnectionsAutoRefreshSeconds)
                return;

            _clientSettings.ConnectionsAutoRefreshSeconds = seconds;
            SaveClientSettings();
        }

        private void ConnectionLogToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding)
                return;

            _clientSettings.ConnectionLogEnabled = ConnectionLogToggle.IsOn;
            SaveClientSettings();
            App.NotifyConnectionLogSettingsChanged(_clientSettings);
        }

        /// <summary>Committed on LostFocus rather than TextChanged: this is a filesystem path, and
        /// reconfiguring the writer on every keystroke would have it chasing every half-typed
        /// directory the user passes through on the way to the real one.</summary>
        private void ConnectionLogPathBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_seeding)
                return;

            var text = ConnectionLogPathBox.Text?.Trim() ?? string.Empty;
            if (text == _clientSettings.ConnectionLogPath)
                return;

            // Anything unusable is refused here rather than at write time, where the only place to
            // report it would be a timer tick with no UI. Empty is always valid - it means default.
            if (text.Length > 0)
            {
                string? failure = null;
                try
                {
                    if (!Path.IsPathFullyQualified(text))
                        failure = text;
                    else
                        _ = Path.GetFullPath(text);
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }

                if (failure is not null)
                {
                    ShowNotice(InfoBarSeverity.Warning, Loc.T(LocKeys.Settings.LoggingWriteFailedTitle), failure);
                    ConnectionLogPathBox.Text = _clientSettings.ConnectionLogPath;
                    return;
                }
            }

            _clientSettings.ConnectionLogPath = text;
            SaveClientSettings();
            App.NotifyConnectionLogSettingsChanged(_clientSettings);
        }

        private void ConnectionLogIntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_seeding || double.IsNaN(args.NewValue))
                return;

            var seconds = (int)Math.Clamp(args.NewValue,
                ClientSettings.MinLogIntervalSeconds, ClientSettings.MaxLogIntervalSeconds);
            if (seconds == _clientSettings.ConnectionLogIntervalSeconds)
                return;

            _clientSettings.ConnectionLogIntervalSeconds = seconds;
            SaveClientSettings();
            App.NotifyConnectionLogSettingsChanged(_clientSettings);
        }

        private void ConnectionLogMaxSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_seeding || double.IsNaN(args.NewValue))
                return;

            var megabytes = (int)Math.Clamp(args.NewValue,
                ClientSettings.MinLogMaxFileSizeMb, ClientSettings.MaxLogMaxFileSizeMb);
            if (megabytes == _clientSettings.ConnectionLogMaxFileSizeMb)
                return;

            _clientSettings.ConnectionLogMaxFileSizeMb = megabytes;
            SaveClientSettings();
            App.NotifyConnectionLogSettingsChanged(_clientSettings);
        }

        /// <summary>Opens the folder the log lives in, creating it first: pointing Explorer at a
        /// directory that does not exist yet just fails, and it will not exist until logging has
        /// been on long enough to write something.</summary>
        private async void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = Path.GetDirectoryName(_clientSettings.ResolvedConnectionLogPath);
                if (string.IsNullOrEmpty(folder))
                    return;

                Directory.CreateDirectory(folder);
                await global::Windows.System.Launcher.LaunchFolderPathAsync(folder);
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Warning, Loc.T(LocKeys.Settings.LoggingWriteFailedTitle), ex.Message);
            }
        }

        private void EnableHotkeysToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding)
                return;
            _clientSettings.EnableGlobalHotkeys = EnableHotkeysToggle.IsOn;
            SaveClientSettings();
            (Application.Current as App)?.NotifyHotkeySettingChanged(_clientSettings.EnableGlobalHotkeys);
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

        private void LockHostsFileToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = LockHostsFileToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.LockHostsFile = value));
        }

        private void AutoUpdateCheckToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = AutoUpdateCheckToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.AutoUpdateCheck = value));
        }

        /// <summary>Runs the whole interactive check/confirm/download flow via
        /// Services.Updater - see that class for the ContentDialog/HttpClient port of
        /// SimpleDeFence/UpdateChecker.cs's Updater.</summary>
        private async void CheckForUpdatesNow_Click(object sender, RoutedEventArgs e)
        {
            await Services.Updater.CheckForUpdatesAsync(Content.XamlRoot);
        }

        /// <summary>Same safe shape as RulesPage's Add pickers: MenuFlyoutItem/Button.Click is a
        /// plain top-level handler, so committing and showing a result dialog directly from here
        /// is fine per the reentrancy rule.</summary>
        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;

            var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".tws");

            if (App.MainWindow is null)
                return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            global::Windows.Storage.StorageFile? file;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceImportFailedTitle), ex.Message);
                return;
            }

            if (file is null)
                return; // Cancelled - not an error, no dialog, no notice.

            ConfigExport imported;
            try
            {
                var buffer = await global::Windows.Storage.FileIO.ReadBufferAsync(file);
                imported = SerializationHelper.Deserialize(buffer.ToArray(), new ConfigExport());
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceImportFailedTitle), ex.Message);
                return;
            }

            // Import replaces every rule and setting in the current configuration - far more
            // destructive than Rules' single-rule remove, which already confirms. Same
            // TryShowDialogAsync/XamlRoot/CloseButtonText/PrimaryButtonText wiring as
            // RulesPage.RemoveButton_Click's confirm dialog.
            var confirmTitle = Loc.T(LocKeys.Settings.MaintenanceImportConfirmTitle);
            var confirmBody = Loc.T(LocKeys.Settings.MaintenanceImportConfirmBody);
            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                FlowDirection = App.UiFlowDirection,
                RequestedTheme = App.UiElementTheme,
                Title = confirmTitle,
                Content = confirmBody,
                PrimaryButtonText = Loc.T(LocKeys.Settings.MaintenanceImportConfirmConfirm),
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
                DefaultButton = ContentDialogButton.Close,
            };
            // If another dialog is already up (single-dialog-per-XamlRoot), the fallback InfoBar
            // shows the same confirm text this dialog would have, and ContentDialogResult.None
            // (never Primary) means Import safely does not proceed without an actual confirmation -
            // treated exactly like a cancelled file picker: no error dialog, no commit.
            if (await TryShowDialogAsync(confirm, confirmTitle, confirmBody) != ContentDialogResult.Primary)
                return;

            // Replace the whole server config - import means "become this document", not a
            // targeted mutation. Profiles is assigned before ActiveProfileName so the ActiveProfile
            // cache invalidation that setter triggers finds the new profile list, not the old one.
            var resp = await CommitAsync(config =>
            {
                config.LockHostsFile = imported.Service.LockHostsFile;
                config.AutoUpdateCheck = imported.Service.AutoUpdateCheck;
                config.StartupMode = imported.Service.StartupMode;
                config.Blocklists = imported.Service.Blocklists;
                config.Profiles = imported.Service.Profiles;
                config.ActiveProfileName = imported.Service.ActiveProfileName;
            });

            if (resp == MessageType.PUT_SETTINGS)
            {
                // The imported Controller (theme) is local-only and applies regardless of the
                // server commit's outcome having already succeeded - saving it after a successful
                // commit keeps the two in step, matching "import means become this document" for
                // the client-local half too.
                imported.Controller.Save();
                App.ApplyTheme(imported.Controller.UiTheme);

                await RefreshAsync();
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceImportSuccessTitle),
                    Loc.T(LocKeys.Settings.MaintenanceImportSuccessBody, file.Name));
            }
            else
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceImportFailedTitle), FailureDetail(resp,
                    LocKeys.Settings.CommitFailedLockedDetail, LocKeys.Settings.CommitFailedStaleDetail,
                    LocKeys.Settings.CommitFailedGenericDetail));
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;

            var picker = new global::Windows.Storage.Pickers.FileSavePicker();
            picker.FileTypeChoices.Add(Loc.T(LocKeys.Settings.MaintenanceFilePickerName), new List<string> { ".tws" });
            picker.SuggestedFileName = "SimpleDeFence";

            if (App.MainWindow is null)
                return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            global::Windows.Storage.StorageFile? file;
            try
            {
                file = await picker.PickSaveFileAsync();
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceExportFailedTitle), ex.Message);
                return;
            }

            if (file is null)
                return; // Cancelled - not an error, no dialog, no notice.

            try
            {
                var export = new ConfigExport
                {
                    Service = App.Firewall.Config ?? new ServerConfiguration(),
                    Controller = ClientSettings.Load(),
                };
                var bytes = SerializationHelper.Serialize(export);
                await global::Windows.Storage.FileIO.WriteBytesAsync(file, bytes);

                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceExportSuccessTitle),
                    Loc.T(LocKeys.Settings.MaintenanceExportSuccessBody, file.Path));
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceExportFailedTitle), ex.Message);
            }
        }

        /// <summary>Shared by every immediate-commit toggle in this page (Protection, Blocklists,
        /// and later Updates/Security's Lock-hosts-file): commits, then refreshes either way so
        /// every toggle's visual state reconciles back to the server's truth - the same pattern
        /// RulesPage.ToggleSpecialAsync uses for the identical reason.</summary>
        private async Task CommitToggleAsync(Action<ServerConfiguration> mutate)
        {
            // Serializes with other toggles: each Toggled handler's own _committing check happens
            // before the DispatcherQueue.TryEnqueue hop, so two toggles flipped within the same
            // dispatcher pump iteration could both pass that check and both reach here before
            // either commit starts - the same guard RulesPage.ToggleSpecialAsync places inside
            // itself, for the identical reason, rather than relying solely on the caller's check.
            if (_committing)
                return;

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

        private async void SetPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;

            var password = NewPasswordBox.Password;
            if (password != NewPasswordConfirmBox.Password)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityPasswordMismatchTitle),
                    Loc.T(LocKeys.Settings.SecurityPasswordMismatchDetail));
                return;
            }

            await SetPasswordAsync(password, Loc.T(LocKeys.Settings.SecurityPasswordUpdatedBody));
        }

        private async void RemovePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;
            await SetPasswordAsync(string.Empty, Loc.T(LocKeys.Settings.SecurityPasswordRemovedBody));
        }

        private async Task SetPasswordAsync(string password, string successBody)
        {
            _committing = true;
            UpdateControlsEnabled();
            MessageType resp;
            try
            {
                resp = await App.Firewall.SetPasswordAsync(password);
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityPasswordUpdateFailedTitle), ex.Message);
                return;
            }
            finally
            {
                _committing = false;
                UpdateControlsEnabled();
            }

            NewPasswordBox.Password = string.Empty;
            NewPasswordConfirmBox.Password = string.Empty;

            if (resp == MessageType.SET_PASSPHRASE)
            {
                await RefreshAsync();
                ShowNotice(InfoBarSeverity.Success, Loc.T(LocKeys.Settings.SecurityPasswordUpdatedTitle), successBody);
            }
            else
            {
                await RefreshAsync();
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityPasswordUpdateFailedTitle), FailureDetail(resp,
                    LocKeys.Settings.CommitFailedLockedDetail, LocKeys.Settings.CommitFailedStaleDetail,
                    LocKeys.Settings.CommitFailedGenericDetail));
            }
        }

        private async void LockNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;

            _committing = true;
            UpdateControlsEnabled();
            MessageType resp;
            try
            {
                resp = await App.Firewall.LockAsync();
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityLockFailedTitle), ex.Message);
                return;
            }
            finally
            {
                _committing = false;
                UpdateControlsEnabled();
            }

            await RefreshAsync();
            if (resp != MessageType.LOCK)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityLockFailedTitle), FailureDetail(resp,
                    LocKeys.Settings.CommitFailedLockedDetail, LocKeys.Settings.CommitFailedStaleDetail,
                    LocKeys.Settings.CommitFailedGenericDetail));
            }
        }

        private async void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;

            var password = UnlockPasswordBox.Password;
            _committing = true;
            UpdateControlsEnabled();
            MessageType resp;
            try
            {
                resp = await App.Firewall.UnlockAsync(password);
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityUnlockFailedTitle), ex.Message);
                return;
            }
            finally
            {
                _committing = false;
                UpdateControlsEnabled();
            }

            UnlockPasswordBox.Password = string.Empty;
            await RefreshAsync();

            if (resp != MessageType.UNLOCK)
            {
                // A wrong password is the common failure here, not a lock/changeset condition -
                // FailureDetail's generic branch ("The service returned {0}") would be honest but
                // unhelpful, so this uses its own specific wording instead.
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityUnlockFailedTitle),
                    Loc.T(LocKeys.Settings.SecurityUnlockFailedDetail));
            }
        }

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

        private void AboutHomepage_Click(object sender, RoutedEventArgs e)
            => OpenUrl("https://github.com/fcoltro/SimpleDeFence");

        private void AboutUpstream_Click(object sender, RoutedEventArgs e)
            => OpenUrl("https://github.com/pylorak/TinyWall");

        private async void AboutLicense_Click(object sender, RoutedEventArgs e)
            => await ShowEmbeddedTextAsync(Loc.T(LocKeys.Settings.AboutLicense), "SimpleDeFence.UI.License.txt");

        private async void AboutAttributions_Click(object sender, RoutedEventArgs e)
            => await ShowEmbeddedTextAsync(Loc.T(LocKeys.Settings.AboutAttributions), "SimpleDeFence.UI.ThirdParty.txt");

        /// <summary>Shows an embedded text document in a dialog. Embedded rather than read from
        /// disk: the files these replaced were not part of the publish output, so every click on
        /// Licence or Attributions raised an error instead of showing anything. A resource compiled
        /// into the assembly cannot be missing at runtime.
        ///
        /// Monospaced and not wrapped, because both documents are laid out in columns that reflow
        /// into nonsense - the licence has its own line breaks and the attributions are a table.</summary>
        private async Task ShowEmbeddedTextAsync(string title, string resourceName)
        {
            string body;
            try
            {
                var asm = typeof(SettingsPage).Assembly;
                using var stream = asm.GetManifestResourceStream(resourceName)
                    ?? throw new System.IO.FileNotFoundException(resourceName);
                using var reader = new System.IO.StreamReader(stream);
                body = await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Settings.AboutReadFailed), ex.Message);
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                FlowDirection = App.UiFlowDirection,
                RequestedTheme = App.UiElementTheme,
                Title = title,
                CloseButtonText = Loc.T(LocKeys.Common.Ok),
                Content = new ScrollViewer
                {
                    MaxHeight = 460,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = body,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Cascadia Mono, monospace"),
                        FontSize = 12,
                        IsTextSelectionEnabled = true,
                        TextWrapping = TextWrapping.NoWrap,
                    },
                },
            };

            // One dialog per XamlRoot, the rule this file already follows elsewhere.
            try
            {
                await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                ShowNotice(InfoBarSeverity.Informational, title, Loc.T(LocKeys.Common.AnotherDialogOpenDetail));
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Settings.AboutLinkFailedTitle), ex.Message);
            }
        }

        /// <summary>Disables the Security group's buttons while a commit is in flight, the same
        /// pattern RulesPage.UpdateRemoveButton/UpdateApplyButtonEnabled/UpdateAddButtonEnabled
        /// use. Tasks 7-8 extend this further for their own groups' controls.</summary>
        private void UpdateControlsEnabled()
        {
            SetPasswordButton.IsEnabled = !_committing;
            var hasPassword = App.Firewall.State?.HasPassword ?? false;
            var locked = App.Firewall.State?.Locked ?? false;
            RemovePasswordButton.IsEnabled = hasPassword && !_committing;
            // Locking without a password is a server-side no-op (PasswordLock.Locked's setter is
            // gated on HasPassword) - disabling the button when there is nothing to lock with
            // keeps the UI from offering an action that would silently do nothing.
            LockNowButton.IsEnabled = hasPassword && !locked && !_committing;
            UnlockButton.IsEnabled = !_committing;
            ImportButton.IsEnabled = !_committing;
            ExportButton.IsEnabled = !_committing;
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
                FlowDirection = App.UiFlowDirection,
                RequestedTheme = App.UiElementTheme,
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

        /// <summary>Every local (non-server) setting on this page saves through here. ClientSettings.Save
        /// is best-effort by design - a failed write must not crash the page or undo the change, which is
        /// already applied in memory - but it used to be silent too, so a setting could quietly fail to
        /// persist and simply be missing after a restart with nothing to explain it. The write still
        /// cannot fail loudly; it can at least fail visibly.</summary>
        private void SaveClientSettings()
        {
            _clientSettings.Save();

            var error = _clientSettings.LastSaveError;
            if (error is null)
                return;

            ShowNotice(InfoBarSeverity.Warning,
                Loc.T(LocKeys.Settings.LocalSaveFailedTitle),
                Loc.T(LocKeys.Settings.LocalSaveFailedDetail, error.Message));
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
