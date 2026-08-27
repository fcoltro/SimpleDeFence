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

        /// <summary>The tray icon, once created. MainWindow needs it to explain, on the first
        /// close, that hiding to the tray is not the same as quitting.</summary>
        internal static TrayIconService? Tray => _tray;
        private static ConnectionLogService? _connectionLog;

        /// <summary>Last connection-log write failure, for the Settings page to surface.</summary>
        internal static string? ConnectionLogError => _connectionLog?.LastError;

        /// <summary>Called by the Settings page after it persists any logging value, so the writer
        /// starts, stops or re-times immediately instead of at next launch - the same arrangement
        /// NotifyHotkeySettingChanged uses for the tray's hotkeys.</summary>
        internal static void NotifyConnectionLogSettingsChanged(ClientSettings settings)
            => _connectionLog?.ApplySettings(settings);

        /// <summary>
        /// Selects the UI language for this process. One place rather than three: the app's own
        /// startup, the uninstall prompt's host and the Settings picker all resolve "auto" the same
        /// way, and disagreeing about it shows up as an app that is translated on one path and
        /// English on another.
        ///
        /// What it deliberately does not try to do is move the strings WinUI draws for its own
        /// controls - a ToggleSwitch's On/Off, for one. Those come from the framework's resources,
        /// which follow the framework's language rather than ours; neither
        /// Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride nor the App SDK's
        /// own ApplicationLanguages changed them in this unpackaged, self-contained build (tried
        /// and measured - the switches still read "Off" beside Arabic text). Where such a label is
        /// on screen the app supplies it instead; see LocKeys.Common.On.
        /// </summary>
        internal static void SelectLanguage(string? requested)
        {
            if (string.IsNullOrWhiteSpace(requested) || requested == "auto")
                Loc.UseSystemCulture();
            else
                Loc.SetCulture(requested);
        }

        /// <summary>
        /// Which way the UI runs for the language in use. Arabic and Persian are read right to
        /// left, and a shell left at LeftToRight puts their navigation, back arrows and dialog
        /// buttons on the wrong side of the window - translated text in a mirror-image layout.
        /// Every window and every ContentDialog is set from this one property: a dialog is
        /// parented to the popup root rather than to the page that opened it, so it does not
        /// inherit the flip and has to be told.
        /// </summary>
        internal static FlowDirection UiFlowDirection
            => Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        /// <summary>
        /// The theme every popup has to be told, for exactly the reason <see cref="UiFlowDirection"/>
        /// documents above: a ContentDialog, a Flyout and a MenuFlyout are all parented to the
        /// XamlRoot's popup root, which is a sibling of the window content rather than a descendant
        /// of it, so the RequestedTheme <see cref="ApplyTheme"/> sets on that content never reaches
        /// them. Switching the app to Dark left every dialog and menu rendering light - the flip
        /// side of the RTL bug that was already fixed here, and missed because the two travel
        /// through the same hole in the tree.
        ///
        /// Kept as stored state rather than recomputed, because "auto" has to resolve against the
        /// window's *actual* theme - ElementTheme.Default on a popup resolves against the popup
        /// root, which follows the OS and not the app's own setting, so handing Default straight
        /// through would reintroduce the bug for every user who leaves the theme on auto.
        /// </summary>
        internal static Microsoft.UI.Xaml.ElementTheme UiElementTheme { get; private set; }
            = Microsoft.UI.Xaml.ElementTheme.Default;

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

            // Resolved, not copied: see UiElementTheme. ActualTheme is read after RequestedTheme is
            // assigned so "auto" lands on whatever the OS is currently giving the window, and the
            // ActualThemeChanged subscription below keeps it current when the OS flips underneath a
            // running app.
            UiElementTheme = root.ActualTheme;

            // RequestedTheme only reaches the XAML tree. The minimise/maximise/close glyphs are not
            // in it - with ExtendsContentIntoTitleBar the caption buttons are drawn by
            // AppWindowTitleBar, which keeps whatever colours it was given and does not follow an
            // element's RequestedTheme at all. Switching to Dark therefore left black glyphs on the
            // now-dark title bar and they vanished.
            ApplyCaptionButtonColors(root);

            // "auto" resolves against the OS theme, which can change while the app is running, and
            // ActualTheme is also what ApplyCaptionButtonColors and UiElementTheme read - so both
            // have to be redone whenever the effective theme moves under us. Subscribed once;
            // ApplyTheme runs on every settings change and at launch.
            if (!_actualThemeHooked)
            {
                _actualThemeHooked = true;
                root.ActualThemeChanged += (sender, _) =>
                {
                    UiElementTheme = sender.ActualTheme;
                    ApplyCaptionButtonColors(sender);
                };
            }
        }

        /// <summary>
        /// Gives a flyout the shell's flow direction and theme. A ContentDialog takes the two
        /// properties directly, in its initializer; a flyout cannot, because FlyoutBase is a plain
        /// DependencyObject with neither property, and the presenter that does have them is
        /// generated only when the flyout opens. A Style is the only handle onto it, and it is
        /// built fresh on each call because Styles seal once applied and so cannot be updated in
        /// place when the theme changes - which means this has to be called at open time, not once
        /// at construction.
        /// </summary>
        internal static void ApplyShellStyling(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase flyout)
        {
            switch (flyout)
            {
                case Microsoft.UI.Xaml.Controls.MenuFlyout menu:
                    menu.MenuFlyoutPresenterStyle =
                        BuildPresenterStyle(typeof(Microsoft.UI.Xaml.Controls.MenuFlyoutPresenter));
                    break;
                case Microsoft.UI.Xaml.Controls.Flyout plain:
                    plain.FlyoutPresenterStyle =
                        BuildPresenterStyle(typeof(Microsoft.UI.Xaml.Controls.FlyoutPresenter));
                    break;
            }
        }

        private static Microsoft.UI.Xaml.Style BuildPresenterStyle(Type presenterType)
        {
            var style = new Microsoft.UI.Xaml.Style(presenterType);
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(
                Microsoft.UI.Xaml.FrameworkElement.RequestedThemeProperty, UiElementTheme));
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(
                Microsoft.UI.Xaml.FrameworkElement.FlowDirectionProperty, UiFlowDirection));
            return style;
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
            SelectLanguage(ArgValue("--lang") ?? ClientSettings.Load().Language);

            // --sample-locked also implies sample data; it simulates a locked service so the
            // GUI's refusal handling can be exercised.
            bool locked = HasSwitch("--sample-locked");

            // --sample-degraded stands in for a service that came up but is not enforcing the
            // configuration: the real conditions (WFP refusing a filter, a database that will not
            // load) cannot be produced to order, so the warning that reports them would otherwise
            // never be seen before a user saw it.
            var degraded = HasSwitch("--sample-degraded")
                ? ServiceDegradation.RulesIncomplete | ServiceDegradation.AppDatabaseUnavailable
                : ServiceDegradation.None;

            if (locked || degraded != ServiceDegradation.None || HasSwitch("--sample-data"))
                Firewall = new SampleFirewallClient(locked, degraded);

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
