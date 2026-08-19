using Microsoft.UI.Xaml;
using SimpleDeFence.Localization;
using SimpleDeFence.UI.Services;
using System;

namespace SimpleDeFence.UI
{
    public partial class App : Application
    {
        private Window? m_window;

        // Shared by every page so they agree on connection state and the config changeset.
        // The real client is the default in every configuration; sample data is opt-in only,
        // so fabricated firewall state can never be shown to a user by accident.
        internal static IFirewallClient Firewall { get; private set; } = new FirewallClient();

        // WinUI 3 has no implicit "current window" the way WinForms did. Pickers (FileOpenPicker,
        // etc.) need an owner HWND to initialize against, obtained via
        // WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow).
        internal static Window? MainWindow { get; private set; }

        // Held for the process's lifetime: nothing else references the tray icon, and letting it be
        // collected would take the notification-area icon with it. Task 7's "Enable global hotkeys"
        // toggle calls into it (ApplyHotkeySetting), which is the other reason it is kept.
        private static TrayIconService? _tray;
        private static ConnectionLogService? _connectionLog;

        /// <summary>Last connection-log write failure, for the Settings page to surface.</summary>
        internal static string? ConnectionLogError => _connectionLog?.LastError;

        /// <summary>Called by the Settings page after it persists any logging value, so the writer
        /// starts, stops or re-times immediately instead of at next launch - the same arrangement
        /// NotifyHotkeySettingChanged uses for the tray's hotkeys.</summary>
        internal static void NotifyConnectionLogSettingsChanged(ClientSettings settings)
            => _connectionLog?.ApplySettings(settings);

        /// <summary>Applies a persisted "auto"/"light"/"dark" theme string to the window's root
        /// element. Called at launch (this file) and immediately on change from the Settings page
        /// (Task 4's General group), so both share one mapping from the stored string to
        /// ElementTheme.</summary>
        internal static void ApplyTheme(string uiTheme)
        {
            if (MainWindow?.Content is not Microsoft.UI.Xaml.FrameworkElement root)
                return;

            root.RequestedTheme = uiTheme switch
            {
                "light" => Microsoft.UI.Xaml.ElementTheme.Light,
                "dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
                _ => Microsoft.UI.Xaml.ElementTheme.Default,
            };

            // RequestedTheme only reaches the XAML tree. The minimise/maximise/close glyphs are not
            // in it - with ExtendsContentIntoTitleBar the caption buttons are drawn by
            // AppWindowTitleBar, which keeps whatever colours it was given and does not follow an
            // element's RequestedTheme at all. Switching to Dark therefore left black glyphs on the
            // now-dark title bar and they vanished.
            ApplyCaptionButtonColors(root);

            // "auto" resolves against the OS theme, which can change while the app is running, and
            // ActualTheme is also what ApplyCaptionButtonColors reads - so the buttons have to be
            // recoloured again whenever the effective theme moves under us. Subscribed once;
            // ApplyTheme runs on every settings change and at launch.
            if (!_actualThemeHooked)
            {
                _actualThemeHooked = true;
                root.ActualThemeChanged += (sender, _) => ApplyCaptionButtonColors(sender);
            }
        }

        private static bool _actualThemeHooked;

        private static void ApplyCaptionButtonColors(Microsoft.UI.Xaml.FrameworkElement root)
        {
            if (MainWindow?.AppWindow?.TitleBar is not { } titleBar)
                return;

            var dark = root.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark;
            var foreground = dark ? global::Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                                  : global::Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);

            // Transparent backgrounds so the Mica backdrop keeps running under the buttons; only
            // the hover/pressed washes are painted, in the direction that shows up on each theme.
            titleBar.ButtonBackgroundColor = global::Windows.UI.Color.FromArgb(0x00, 0, 0, 0);
            titleBar.ButtonInactiveBackgroundColor = global::Windows.UI.Color.FromArgb(0x00, 0, 0, 0);
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = dark
                ? global::Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)
                : global::Windows.UI.Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E);
            titleBar.ButtonHoverBackgroundColor = dark
                ? global::Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
                : global::Windows.UI.Color.FromArgb(0x14, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = dark
                ? global::Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
                : global::Windows.UI.Color.FromArgb(0x24, 0x00, 0x00, 0x00);
        }

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // Must run before any XAML is touched: MainWindow's constructor resolves Loc.T for
            // its Title, and every {loc:Loc} markup extension in the tree resolves during
            // InitializeComponent - both need the right culture selected first.
            var langOverride = ArgValue("--lang");
            if (langOverride is not null)
                Loc.SetCulture(langOverride);
            else
            {
                var savedLanguage = ClientSettings.Load().Language;
                if (savedLanguage != "auto")
                    Loc.SetCulture(savedLanguage);
                else
                    Loc.UseSystemCulture();
            }

            // --sample-locked also implies sample data; it simulates a locked service so the
            // GUI's refusal handling can be exercised.
            bool locked = HasSwitch("--sample-locked");
            if (locked || HasSwitch("--sample-data"))
                Firewall = new SampleFirewallClient(locked);

            m_window = new MainWindow();
            MainWindow = m_window;
            ApplyTheme(ClientSettings.Load().UiTheme);
            m_window.Activate();

            // After Activate(): the tray icon subclasses the window's WndProc (for WM_HOTKEY) and
            // hangs its dialogs off the window's XamlRoot, so the window has to exist and be shown
            // first.
            _tray = new TrayIconService();

            // Independent of any page: the log has to keep recording while the user is on Rules or
            // Settings, or has closed the window to the tray, which is exactly when an unattended
            // record is worth having. Reads its own enabled/interval settings; off by default.
            _connectionLog = new ConnectionLogService(m_window.DispatcherQueue);

            // Closing the main window (title-bar X, Alt+F4) ends the process the same way Quit
            // does - WinUI's default behavior when the last window closes - but unlike Quit, that
            // path never touched the tray icon on its own. Without this, closing the window this
            // way leaves a ghost icon in the notification area until Explorer notices.
            // TrayIconService.Dispose() is idempotent (guarded by _disposed), so this is harmless
            // even when Quit or ElevateSelfAsync already disposed it first.
            m_window.Closed += (_, _) =>
            {
                _tray?.Dispose();
                _connectionLog?.Dispose();
            };
        }

        /// <summary>Called by the Settings page's "Enable global keyboard shortcuts" toggle
        /// (Task 7) after it persists the new value, so the already-running tray icon's
        /// RegisterHotKey/UnregisterHotKey state changes immediately rather than only on next
        /// launch.</summary>
        internal void NotifyHotkeySettingChanged(bool enabled) => _tray?.ApplyHotkeySetting(enabled);

        private static bool HasSwitch(string name)
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Reads "--name value" from the command line, or null if absent.</summary>
        private static string? ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; ++i)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
