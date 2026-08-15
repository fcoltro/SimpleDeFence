using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SimpleDeFence.Localization;
using System;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Owns the tray icon and its context menu, replacing SimpleDeFenceController.cs's Tray/TrayMenu
    /// fields (see the net10 exe-merge design doc, Decision 3). Built entirely on top of the
    /// already-working IFirewallClient abstraction - no WinForms/IPC internals ported here.
    /// </summary>
    internal sealed class TrayIconService : IDisposable
    {
        private readonly TaskbarIcon _icon;
        private readonly WindowHotkeys _hotkeys;
        private readonly DispatcherQueue _dispatcher;
        private readonly ToggleMenuFlyoutItem _allowLocalSubnetItem;
        private readonly ToggleMenuFlyoutItem _hostsBlocklistItem;

        /// <summary>The mode the icon and tooltip currently show. IFirewallClient.Changed fires on
        /// every refresh - including ConnectionsPage's periodic auto-refresh - so without this the
        /// icon would be rebuilt (a BitmapImage plus the HICON H.NotifyIcon derives from it) every
        /// few seconds for a mode that has not moved.</summary>
        private FirewallMode _shownMode = (FirewallMode)(-1);

        private bool _disposed;

        private const int HOTKEY_EXECUTABLE = 1;
        private const int HOTKEY_PROCESS = 2;
        private const int HOTKEY_WINDOW = 3;
        private const uint MOD_CONTROL = 0x2;
        private const uint MOD_ALT = 0x1;
        private const uint VK_E = 0x45;
        private const uint VK_P = 0x50;
        private const uint VK_W = 0x57;

        public TrayIconService()
        {
            var window = App.MainWindow
                ?? throw new InvalidOperationException("The tray icon needs the main window's HWND; create it after App.MainWindow is set.");

            _dispatcher = window.DispatcherQueue;

            _icon = new TaskbarIcon
            {
                // SecondWindow, not the PopupMenu default: PopupMenu mode renders the flyout as a
                // native Win32 menu, which cannot host WinUI's MenuFlyoutSubItem (the mode
                // submenu) or ToggleMenuFlyoutItem checkmarks.
                ContextMenuMode = ContextMenuMode.SecondWindow,
            };

            _allowLocalSubnetItem = new ToggleMenuFlyoutItem { Text = Loc.T(LocKeys.Tray.AllowLocalSubnet) };
            _hostsBlocklistItem = new ToggleMenuFlyoutItem { Text = Loc.T(LocKeys.Tray.EnableHostsBlocklist) };

            _icon.ContextFlyout = BuildMenu();
            // Nothing puts a TaskbarIcon into the visual tree here (it is created in code, not
            // declared in XAML), so it never gets a Loaded pass of its own to create the icon.
            // enablesEfficiencyMode: false because this app has a real, visible main window -
            // ForceCreate's default puts the process into EcoQoS, which is for tray-only apps.
            _icon.ForceCreate(enablesEfficiencyMode: false);

            _hotkeys = new WindowHotkeys(window);
            ApplyHotkeySetting(ClientSettings.Load().EnableGlobalHotkeys);

            App.Firewall.Changed += OnFirewallChanged;
            UpdateFromState();
        }

        /// <summary>Called from the Settings page when the user flips "Enable global hotkeys" -
        /// registers/unregisters all three at once, matching SimpleDeFenceController's
        /// ApplyControllerSettings/SetHotkey all-or-nothing behavior for this toggle.</summary>
        public void ApplyHotkeySetting(bool enabled)
        {
            if (enabled)
            {
                Register(HOTKEY_EXECUTABLE, VK_E, () => _ = WhitelistByExecutableAsync());
                Register(HOTKEY_PROCESS, VK_P, () => _ = WhitelistByProcessAsync());
                Register(HOTKEY_WINDOW, VK_W, () => _ = WhitelistByWindowAsync());
            }
            else
            {
                _hotkeys.UnregisterHotkey(HOTKEY_EXECUTABLE);
                _hotkeys.UnregisterHotkey(HOTKEY_PROCESS);
                _hotkeys.UnregisterHotkey(HOTKEY_WINDOW);
            }
        }

        /// <summary>RegisterHotKey fails (and WindowHotkeys throws) whenever another process already
        /// owns the combination - including a still-running WinForms SimpleDeFence, which claims
        /// exactly these three. One unavailable hotkey must not cost the user their tray icon, and it
        /// is not something they can act on, so it is skipped rather than reported: the same action
        /// is always still on the menu.</summary>
        private void Register(int id, uint virtualKey, Action callback)
        {
            try
            {
                _hotkeys.RegisterHotkey(id, virtualKey, MOD_CONTROL | MOD_ALT, callback);
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        private MenuFlyout BuildMenu()
        {
            var menu = new MenuFlyout();

            var modeNormal = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeNormal) };
            modeNormal.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.Normal);
            var modeBlockAll = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeBlockAll) };
            modeBlockAll.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.BlockAll);
            var modeAllowOutgoing = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeAllowOutgoing) };
            modeAllowOutgoing.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.AllowOutgoing);
            var modeDisabled = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeDisabled) };
            modeDisabled.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.Disabled);
            var modeLearning = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeLearning) };
            modeLearning.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.Learning);
            var modeSub = new MenuFlyoutSubItem { Text = Loc.T(LocKeys.Nav.ModeChip) };
            modeSub.Items.Add(modeNormal);
            modeSub.Items.Add(modeBlockAll);
            modeSub.Items.Add(modeAllowOutgoing);
            modeSub.Items.Add(modeDisabled);
            modeSub.Items.Add(modeLearning);
            menu.Items.Add(modeSub);

            var manage = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Manage) };
            manage.Click += (_, _) => ShowAndNavigate(typeof(Pages.RulesPage));
            menu.Items.Add(manage);

            var connections = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Connections) };
            connections.Click += (_, _) => ShowAndNavigate(typeof(Pages.ConnectionsPage));
            menu.Items.Add(connections);

            menu.Items.Add(new MenuFlyoutSeparator());

            var lockItem = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Lock) };
            lockItem.Click += (_, _) => _ = LockAsync();
            menu.Items.Add(lockItem);

            var elevate = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Elevate) };
            elevate.Click += (_, _) => _ = ElevateSelfAsync();
            menu.Items.Add(elevate);

            menu.Items.Add(new MenuFlyoutSeparator());

            // ToggleMenuFlyoutItem flips IsChecked itself before Click fires, so IsChecked read here
            // is already the value the user asked for. UpdateMenuChecks reconciles both items back
            // to the server's truth after every refresh, whichever way the commit went.
            _allowLocalSubnetItem.Click += (_, _) => _ = ToggleAllowLocalSubnetAsync(_allowLocalSubnetItem.IsChecked);
            menu.Items.Add(_allowLocalSubnetItem);

            _hostsBlocklistItem.Click += (_, _) => _ = ToggleHostsBlocklistAsync(_hostsBlocklistItem.IsChecked);
            menu.Items.Add(_hostsBlocklistItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var whitelistExe = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.WhitelistByExecutable) };
            whitelistExe.Click += (_, _) => _ = WhitelistByExecutableAsync();
            menu.Items.Add(whitelistExe);

            var whitelistProc = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.WhitelistByProcess) };
            whitelistProc.Click += (_, _) => _ = WhitelistByProcessAsync();
            menu.Items.Add(whitelistProc);

            var whitelistWin = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.WhitelistByWindow) };
            whitelistWin.Click += (_, _) => _ = WhitelistByWindowAsync();
            menu.Items.Add(whitelistWin);

            menu.Items.Add(new MenuFlyoutSeparator());

            var quit = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Quit) };
            // Dispose before Exit, the same order mnuQuit_Click uses (Tray.Visible = false, then
            // ExitThread): process teardown is not guaranteed to run TaskbarIcon's finalizer, and an
            // icon left in the notification area after the app is gone is a ghost the user has to
            // hover over to clear.
            quit.Click += (_, _) =>
            {
                Dispose();
                Microsoft.UI.Xaml.Application.Current.Exit();
            };
            menu.Items.Add(quit);

            return menu;
        }

        /// <summary>IFirewallClient.Changed can complete on whatever thread its refresh ran on, and
        /// everything UpdateFromState touches is XAML - so it is marshalled onto the UI thread
        /// rather than assumed to be there already.</summary>
        private void OnFirewallChanged(object? sender, EventArgs e)
            => _dispatcher.TryEnqueue(UpdateFromState);

        private void UpdateFromState()
        {
            if (_disposed)
                return;

            UpdateModeIcon();
            UpdateMenuChecks();
        }

        private void UpdateModeIcon()
        {
            var mode = App.Firewall.State?.Mode ?? FirewallMode.Unknown;
            if (mode == _shownMode)
                return;

            // Normal uses the app's own icon (Assets/AppIcon.ico), not WinForms' Resources.Icons.firewall -
            // that legacy asset is an unrelated red/blue "house" glyph that doesn't match this app's actual
            // branding (a blue shield), a mismatch that was faithfully ported from WinForms before anyone
            // could see it rendered live. The other four modes' colored shields already share AppIcon's
            // shield silhouette, so only the default/most-seen state needed the swap.
            var (iconUri, labelKey) = mode switch
            {
                FirewallMode.Normal => ("ms-appx:///Assets/AppIcon.ico", LocKeys.Tray.ModeNormal),
                FirewallMode.AllowOutgoing => ("ms-appx:///Assets/TrayIcons/shield_red_small.ico", LocKeys.Tray.ModeAllowOutgoing),
                FirewallMode.BlockAll => ("ms-appx:///Assets/TrayIcons/shield_yellow_small.ico", LocKeys.Tray.ModeBlockAll),
                FirewallMode.Disabled => ("ms-appx:///Assets/TrayIcons/shield_grey_small.ico", LocKeys.Tray.ModeDisabled),
                FirewallMode.Learning => ("ms-appx:///Assets/TrayIcons/shield_blue_small.ico", LocKeys.Tray.ModeLearning),
                _ => ("ms-appx:///Assets/TrayIcons/shield_grey_small.ico", LocKeys.Common.Unknown),
            };

            _icon.IconSource = new BitmapImage(new Uri(iconUri));
            // Same two-line shape as WinForms' Tray.Text: the product name, then which mode it is
            // in - the whole point of a per-mode icon is knowing the mode without opening anything.
            _icon.ToolTipText = $"SimpleDeFence\n{Loc.T(LocKeys.Nav.ModeChip)}: {Loc.T(labelKey)}";
            _shownMode = mode;
        }

        /// <summary>Reconciles the two checkable items with the server's configuration, the same job
        /// TrayMenu_Opening does in WinForms (there, on every menu open; here, on every refresh).
        /// Without it both would read as unchecked no matter what the firewall is actually doing.</summary>
        private void UpdateMenuChecks()
        {
            var config = App.Firewall.Config;
            _allowLocalSubnetItem.IsChecked = config?.ActiveProfile.AllowLocalSubnet ?? false;
            _hostsBlocklistItem.IsChecked = config?.Blocklists.EnableBlocklists ?? false;
        }

        private static async Task SwitchModeAsync(FirewallMode mode)
            => await App.Firewall.SwitchModeAsync(mode);

        private static async Task LockAsync()
            => await App.Firewall.LockAsync();

        /// <summary>Matches SimpleDeFenceController.cs's mnuElevate_Click: relaunch this executable
        /// with the "runas" verb, then quit so the elevated instance takes over. That call site goes
        /// through Utils.StartProcess, which lives in the WinForms project this one does not
        /// reference, so the two settings it applies for asAdmin are inlined here.
        /// ProcessManager.ExecutablePath (not Assembly.Location) is the real exe path.
        ///
        /// Instance method, not static: the success path needs to Dispose() this TrayIconService
        /// before exiting, the same reason the Quit handler does - process teardown is not
        /// guaranteed to run TaskbarIcon's finalizer, and relaunching elevated is at least as common
        /// an exit path as Quit, so it must not risk a ghost icon either.</summary>
        private async Task ElevateSelfAsync()
        {
            var path = SimpleDeFence.Windows.ProcessManager.ExecutablePath;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(path)
                {
                    WorkingDirectory = System.IO.Path.GetDirectoryName(path),
                    UseShellExecute = true,
                    Verb = "runas",
                };
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                // Declining the UAC prompt lands here too (Win32Exception 1223). Either way this
                // instance keeps running - exiting would leave the user with no GUI at all.
                await ShowMessageAsync(Loc.T(LocKeys.Tray.ElevateFailedTitle), ex.Message);
                return;
            }

            // Same order the Quit handler uses: dispose the tray icon, then exit. Dispose() is
            // idempotent (guarded by _disposed), so this is safe even if MainWindow's own Closed
            // handler (App.xaml.cs) also fires and disposes again as Exit() tears the window down.
            Dispose();
            Microsoft.UI.Xaml.Application.Current.Exit();
        }

        private static async Task ToggleAllowLocalSubnetAsync(bool value)
            => await App.Firewall.CommitConfigChangesAsync(c => c.ActiveProfile.AllowLocalSubnet = value);

        private static async Task ToggleHostsBlocklistAsync(bool value)
            => await App.Firewall.CommitConfigChangesAsync(c => c.Blocklists.EnableBlocklists = value);

        private static async Task WhitelistByExecutableAsync()
            => await Pages.RulesPage.QuickAddExecutableAsync(AskForExceptionDetails());

        private static async Task WhitelistByProcessAsync()
            => await Pages.RulesPage.QuickAddProcessAsync(AskForExceptionDetails());

        private static async Task WhitelistByWindowAsync()
            => await Pages.RulesPage.QuickAddWindowAsync(AskForExceptionDetails());

        private static bool AskForExceptionDetails() => ClientSettings.Load().AskForExceptionDetails;

        private static void ShowAndNavigate(Type pageType)
        {
            if (App.MainWindow is not MainWindow window)
                return;

            window.Activate();
            window.NavigateTo(pageType);
        }

        /// <summary>The tray's own message dialog. Attached to the main window's XamlRoot because
        /// the tray has no page of its own; silently skipped if another dialog already holds that
        /// XamlRoot, the same one-dialog-per-XamlRoot rule RulesPage documents.</summary>
        private static async Task ShowMessageAsync(string title, string body)
        {
            var xamlRoot = App.MainWindow?.Content?.XamlRoot;
            if (xamlRoot is null)
                return;

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = body,
                CloseButtonText = Loc.T(LocKeys.Common.Ok),
            };

            try
            {
                await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            App.Firewall.Changed -= OnFirewallChanged;
            _hotkeys.Dispose();
            _icon.Dispose();
        }
    }
}
