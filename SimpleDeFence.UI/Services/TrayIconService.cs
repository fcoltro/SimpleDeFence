using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
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
        private readonly NotifyIcon _icon;
        private readonly TrayMenuHost _menuHost;
        private readonly MenuFlyout _menu;
        private readonly WindowHotkeys _hotkeys;
        private readonly DispatcherQueue _dispatcher;
        private readonly ToggleMenuFlyoutItem _allowLocalSubnetItem;
        private readonly ToggleMenuFlyoutItem _hostsBlocklistItem;

        /// <summary>The mode the icon and tooltip currently show. IFirewallClient.Changed fires on
        /// every refresh - including ConnectionsPage's periodic auto-refresh - so without this the
        /// icon would be reloaded from disk (a LoadImage plus the DestroyIcon of the one it
        /// replaces) every few seconds for a mode that has not moved.</summary>
        private FirewallMode _shownMode = (FirewallMode)(-1);

        /// <summary>Which artwork variant is on screen. Paired with _shownMode in the early-out
        /// below: without it, a theme switch would be ignored for as long as the mode happened not
        /// to change, which for the mode users sit in all day is indefinitely.</summary>
        private bool? _shownLightTheme;

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

            _allowLocalSubnetItem = new ToggleMenuFlyoutItem { Text = Loc.T(LocKeys.Tray.AllowLocalSubnet), MinWidth = TrayMenuItemMinWidth };
            _hostsBlocklistItem = new ToggleMenuFlyoutItem { Text = Loc.T(LocKeys.Tray.EnableHostsBlocklist), MinWidth = TrayMenuItemMinWidth };

            _menu = BuildMenu();
            _menuHost = new TrayMenuHost();
            // The host window has to come down once the menu it was propping up is gone, or a
            // transparent always-on-top pixel stays at the corner of the screen holding foreground.
            _menu.Closed += (_, _) => _menuHost.Hide();

            // WindowHotkeys owns the main window's only WndProc subclass, and the notification
            // icon needs to receive its callback message through that same subclass - so it is
            // constructed before the icon rather than after, unlike the order this used to run in.
            _hotkeys = new WindowHotkeys(window);
            _hotkeys.SystemColorsChanged += OnSystemColorsChanged;

            _icon = new NotifyIcon(_hotkeys.Handle, _hotkeys);
            // Closing the window now hides it rather than quitting, so the icon has to be a way
            // back and not just a menu host - clicking a tray icon and having nothing happen is
            // how an app looks broken.
            _icon.Selected += ShowMainWindow;
            _icon.ContextMenuRequested += ShowTrayMenu;

            // Icon and tooltip before Create, so the icon never appears in the notification area
            // blank and then fills in.
            UpdateModeIcon();
            _icon.Create();

            ApplyHotkeySetting(ClientSettings.Load().EnableGlobalHotkeys);

            App.Firewall.Changed += OnFirewallChanged;
            UpdateFromState();
        }

        /// <summary>Explains, once ever, that closing the window left the firewall running. Any
        /// failure here is swallowed: the notification is a courtesy, and a machine whose
        /// notification service is unhappy must not take the close path down with it.</summary>
        public void NotifyClosedToTray()
        {
            if (_disposed)
                return;

            var settings = ClientSettings.Load();
            if (settings.TrayCloseNoticeShown)
                return;

            settings.TrayCloseNoticeShown = true;
            settings.Save();

            try
            {
                _icon.ShowNotification(
                    title: Loc.T(LocKeys.Tray.StillRunningTitle),
                    message: Loc.T(LocKeys.Tray.StillRunningBody));
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"Tray close notice failed: {e.Message}");
            }
        }

        private static void ShowMainWindow()
        {
            if (App.MainWindow is MainWindow window)
                window.ShowFromTray();
        }

        /// <summary>Right click on the icon. The shell hands over the point it wants the menu
        /// anchored at, in physical screen pixels; TrayMenuHost takes it from there.</summary>
        private void ShowTrayMenu(int screenX, int screenY)
        {
            if (_disposed)
                return;

            _menuHost.Show(_menu, screenX, screenY);
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

        /// <summary>Width floor for the tray menu's items. The very first right-click on the tray
        /// icon opens a visibly too-narrow menu with every label clipped; every open after that is
        /// correct. Measured on the VM: first open, the presenter is 96px wide with 86px items and
        /// the labels' own text measures 36px; second open, 228px with 218px items. The first
        /// measure runs before the flyout has real font metrics, so the text contributes almost
        /// nothing to the desired width and the popup is on screen at that size before the correct
        /// one is known.
        ///
        /// The floor is set on the items rather than through MenuFlyoutPresenterStyle, which is
        /// where this started: a Style carrying MinWidth had no observable effect at all - neither
        /// open came anywhere near it. An explicit MinWidth on each item is a layout constraint
        /// that does not depend on measuring text, which is precisely the thing that is broken on
        /// that first pass. Above the 218px the English labels settle at, so there is headroom for
        /// longer translations; it is only a floor, so anything longer still grows past it.</summary>
        private const double TrayMenuItemMinWidth = 260;

        private MenuFlyout BuildMenu()
        {
            var menu = new MenuFlyout();

            // On Opening, not once here: this menu is built a single time and parked on
            // ContextFlyout for the life of the process, so a theme chosen later on the Settings
            // page would never reach a style applied at construction. It matters more here than
            // anywhere else - TrayMenuHost puts this menu in a window of its own, with a
            // XamlRoot that shares nothing with the shell.
            menu.Opening += (_, _) => App.ApplyShellStyling(menu);

            var modeNormal = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeNormal), MinWidth = TrayMenuItemMinWidth };
            modeNormal.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.Normal);
            var modeBlockAll = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeBlockAll), MinWidth = TrayMenuItemMinWidth };
            modeBlockAll.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.BlockAll);
            var modeAllowOutgoing = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeAllowOutgoing), MinWidth = TrayMenuItemMinWidth };
            modeAllowOutgoing.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.AllowOutgoing);
            var modeDisabled = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeDisabled), MinWidth = TrayMenuItemMinWidth };
            modeDisabled.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.Disabled);
            var modeLearning = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeLearning), MinWidth = TrayMenuItemMinWidth };
            modeLearning.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.Learning);
            var modeSub = new MenuFlyoutSubItem { Text = Loc.T(LocKeys.Nav.ModeChip), MinWidth = TrayMenuItemMinWidth };
            modeSub.Items.Add(modeNormal);
            modeSub.Items.Add(modeBlockAll);
            modeSub.Items.Add(modeAllowOutgoing);
            modeSub.Items.Add(modeDisabled);
            modeSub.Items.Add(modeLearning);
            menu.Items.Add(modeSub);

            var manage = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Manage), MinWidth = TrayMenuItemMinWidth };
            manage.Click += (_, _) => ShowAndNavigate(typeof(Pages.RulesPage));
            menu.Items.Add(manage);

            var connections = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Connections), MinWidth = TrayMenuItemMinWidth };
            connections.Click += (_, _) => ShowAndNavigate(typeof(Pages.ConnectionsPage));
            menu.Items.Add(connections);

            menu.Items.Add(new MenuFlyoutSeparator());

            var lockItem = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Lock), MinWidth = TrayMenuItemMinWidth };
            lockItem.Click += (_, _) => _ = LockAsync();
            menu.Items.Add(lockItem);

            // Only offered when there is something to elevate to. Offering "run as administrator"
            // to a process already running as administrator relaunches the app to reach the state
            // it is already in.
            //
            // While app.manifest requests requireAdministrator this condition is never false, so
            // the item does not appear at all: every process that gets far enough to build this
            // menu is elevated. The guard is written as a condition rather than the item being
            // deleted because it is the manifest, not this file, that decides - softening the
            // level to highestAvailable brings the item straight back, and a menu that quietly
            // omitted the only way to elevate would be the wrong failure.
            if (!Elevation.IsElevated)
            {
                var elevate = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Elevate), MinWidth = TrayMenuItemMinWidth };
                elevate.Click += (_, _) => _ = ElevateSelfAsync();
                menu.Items.Add(elevate);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            // ToggleMenuFlyoutItem flips IsChecked itself before Click fires, so IsChecked read here
            // is already the value the user asked for. UpdateMenuChecks reconciles both items back
            // to the server's truth after every refresh, whichever way the commit went.
            _allowLocalSubnetItem.Click += (_, _) => _ = ToggleAllowLocalSubnetAsync(_allowLocalSubnetItem.IsChecked);
            menu.Items.Add(_allowLocalSubnetItem);

            _hostsBlocklistItem.Click += (_, _) => _ = ToggleHostsBlocklistAsync(_hostsBlocklistItem.IsChecked);
            menu.Items.Add(_hostsBlocklistItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var whitelistExe = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.WhitelistByExecutable), MinWidth = TrayMenuItemMinWidth };
            whitelistExe.Click += (_, _) => _ = WhitelistByExecutableAsync();
            menu.Items.Add(whitelistExe);

            var whitelistProc = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.WhitelistByProcess), MinWidth = TrayMenuItemMinWidth };
            whitelistProc.Click += (_, _) => _ = WhitelistByProcessAsync();
            menu.Items.Add(whitelistProc);

            var whitelistWin = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.WhitelistByWindow), MinWidth = TrayMenuItemMinWidth };
            whitelistWin.Click += (_, _) => _ = WhitelistByWindowAsync();
            menu.Items.Add(whitelistWin);

            menu.Items.Add(new MenuFlyoutSeparator());

            var quit = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Quit), MinWidth = TrayMenuItemMinWidth };
            // Dispose before Exit, the same order mnuQuit_Click uses (Tray.Visible = false, then
            // ExitThread): process teardown is not guaranteed to run NotifyIcon's finalizer, and an
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

        /// <summary>The user switched Windows between light and dark while we were running.
        /// UpdateModeIcon re-reads the taskbar theme itself, so this only has to ask for the
        /// repaint; its own early-out decides whether anything actually changed.</summary>
        private void OnSystemColorsChanged() => UpdateModeIcon();

        private void UpdateModeIcon()
        {
            var mode = App.Firewall.State?.Mode ?? FirewallMode.Unknown;
            var lightTaskbar = SystemTheme.TaskbarUsesLightTheme();
            if (mode == _shownMode && lightTaskbar == _shownLightTheme)
                return;

            // Colour per mode is the mode chip's palette (Themes/StatusResources.xaml), deliberately,
            // so one firewall state is one colour wherever the user sees it. The tray used to
            // disagree with the chip on three of the five modes - Normal was blue against the chip's
            // green, and BlockAll/AllowOutgoing were red and yellow the exact opposite way round -
            // which meant the same state read as two different things depending on where you looked.
            //
            //   Normal        green   #3DA45D   StatusSuccessBrush
            //   BlockAll      red     #E5484D   StatusInformationBrush
            //   AllowOutgoing yellow  #E0A106   StatusCautionBrush
            //   Learning      blue    #4C9BE8   StatusAccentAltBrush
            //   Disabled      grey    #9A9A9A   StatusNeutralBrush
            var (colour, labelKey) = mode switch
            {
                FirewallMode.Normal => ("green", LocKeys.Tray.ModeNormal),
                FirewallMode.BlockAll => ("red", LocKeys.Tray.ModeBlockAll),
                FirewallMode.AllowOutgoing => ("yellow", LocKeys.Tray.ModeAllowOutgoing),
                FirewallMode.Learning => ("blue", LocKeys.Tray.ModeLearning),
                FirewallMode.Disabled => ("grey", LocKeys.Tray.ModeDisabled),
                _ => ("grey", LocKeys.Common.Unknown),
            };

            // Two variants per colour, because Windows does not recolour third-party tray icons the
            // way it does its own network/volume glyphs - those are drawn by the shell from its own
            // theme-aware assets, while ours is an HICON the shell blits unchanged. A light taskbar
            // needs the dark artwork and vice versa.
            var variant = lightTaskbar ? "lighttheme" : "darktheme";

            // A path on disk, not the ms-appx:/// URI this used to pass to a BitmapImage: LoadImage
            // reads files, and this app is unpackaged, so there is no package to resolve ms-appx
            // against - BaseDirectory is the same Assets\TrayIcons the URI was pointing at anyway.
            var iconPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Assets", "TrayIcons", $"trayicon_{colour}_{variant}.ico");

            _icon.SetIcon(iconPath);
            // Same two-line shape as WinForms' Tray.Text: the product name, then which mode it is
            // in - the whole point of a per-mode icon is knowing the mode without opening anything.
            _icon.SetTooltip($"SimpleDeFence\n{Loc.T(LocKeys.Nav.ModeChip)}: {Loc.T(labelKey)}");
            _shownMode = mode;
            _shownLightTheme = lightTaskbar;
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
        /// guaranteed to run NotifyIcon's finalizer, and relaunching elevated is at least as common
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

            window.ShowFromTray();
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
                FlowDirection = App.UiFlowDirection,
                RequestedTheme = App.UiElementTheme,
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
            _hotkeys.SystemColorsChanged -= OnSystemColorsChanged;
            _icon.Selected -= ShowMainWindow;
            _icon.ContextMenuRequested -= ShowTrayMenu;

            // The icon first, and before the WndProc subclass it sends its callback through is
            // unhooked: NIM_DELETE is what actually takes the icon out of the notification area,
            // and an icon left behind is the ghost the Quit and Elevate paths already go out of
            // their way to avoid.
            _icon.Dispose();
            _menuHost.Dispose();
            _hotkeys.Dispose();
        }
    }
}
