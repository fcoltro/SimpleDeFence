using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SimpleDeFence.Localization;
using SimpleDeFence.UI.Pages;
using SimpleDeFence.UI.Themes;
using SimpleDeFence.UI.ViewModels;
using System;

namespace SimpleDeFence.UI
{
    public sealed partial class MainWindow : Window
    {
        // Bound from XAML as a function instead of "Converter={StaticResource ModeStateToBrush}":
        // an x:Bind converter lookup rooted at <Window> needs SetConverterLookupRoot(FrameworkElement),
        // and Window is not one, so that path fails to build here (CS1503 in generated code).
        // Calling the same converter directly reuses it and its brushes without hitting that codegen.
        private static readonly ModeStateToBrushConverter s_modeStateToBrush = new();

        internal ShellViewModel Shell { get; }

        public MainWindow()
        {
            Shell = new ShellViewModel(App.Firewall);
            InitializeComponent();

            Title = Loc.T(LocKeys.App.Name);

            // Set on the root rather than on the Window: Window has no FlowDirection, and the
            // whole tree below the root - nav pane, title bar row, pages - inherits from here.
            if (Content is FrameworkElement root)
                root.FlowDirection = App.UiFlowDirection;

            // Run the nav pane to the top edge - the standard Windows 11 app shape.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            ContentFrame.Navigate(typeof(ConnectionsPage));
            _ = Shell.RefreshAsync();

            // NavigationView starts in whatever DisplayMode its initial width lands on, but
            // DisplayModeChanged only fires on a subsequent change - without this call the chip
            // still shows its full text on a window that opens narrow until the user resizes it.
            UpdateChipLayout(Nav.IsPaneOpen);

            // Closing the window hides it instead of ending the process. A firewall that stops
            // running because its window was closed is not doing its job, and the tray icon
            // disappearing with it took away the only way back. Quit on the tray menu remains the
            // way out, and it is the only one.
            AppWindow.Closing += AppWindow_Closing;

            ApplyMinimumWindowSize();
            KeepTitleBarClearOfCaptionButtons();
        }

        /// <summary>
        /// Leaves the title bar row reading left to right even in a right-to-left language, so the
        /// app icon and title stay at the left edge. The minimise/maximise/close buttons do not
        /// move with the flip - they are drawn over a window whose Win32 layout is still
        /// left-to-right, and they keep the right-hand 138px - so a flipped title bar puts the
        /// title directly underneath them. (AppWindowTitleBar.RightInset, which exists to reserve
        /// exactly that space, reports 0 here: the caption buttons come from Window's
        /// ExtendsContentIntoTitleBar, not from AppWindowTitleBar's own.) The left edge is the only
        /// space actually free, so that is where the title goes.
        /// </summary>
        private void KeepTitleBarClearOfCaptionButtons()
        {
            if (App.UiFlowDirection == FlowDirection.RightToLeft)
                AppTitleBar.FlowDirection = FlowDirection.LeftToRight;
        }

        /// <summary>Floor on how small the window can be dragged. There was none, so it could be
        /// narrowed until rows clipped and labels wrapped a character at a time.</summary>
        private void ApplyMinimumWindowSize()
        {
            if (AppWindow.Presenter is not OverlappedPresenter presenter)
                return;

            // PreferredMinimum* are physical pixels, not DIPs, so the same numbers would give a
            // 150% display a two-thirds-size floor. Scale them by the window's DPI.
            const double MinWidthDips = 900;
            const double MinHeightDips = 600;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            double scale = dpi == 0 ? 1.0 : dpi / 96.0;

            presenter.PreferredMinimumWidth = (int)Math.Round(MinWidthDips * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinHeightDips * scale);
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern uint GetDpiForWindow(IntPtr hwnd);
        }

        /// <summary>Mirrors what NavigationViewItem does automatically for the menu items above the
        /// chip: when the pane is closed, drop the text and centre what is left, so the footer
        /// degrades to a compact indicator instead of an empty husk (ChipTextStack's comment in
        /// MainWindow.xaml has the full story). DisplayModeChanged alone is not enough - the pane
        /// toggle button collapses the pane to the 48px rail without changing DisplayMode at all -
        /// so PaneOpened/PaneClosed are handled too.</summary>
        private void Nav_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
            => UpdateChipLayout(Nav.IsPaneOpen);

        // Opening/Closing fire as the transition starts; Opened/Closed only once it has finished.
        // Reacting to the finished pair alone is what made the chip visibly lag the pane - it was
        // being told to change shape after the pane had already reached its new width, so the full
        // text sat in the 48px rail for the length of the animation. These two do the work, and
        // they cannot read Nav.IsPaneOpen for it: at this point it still holds the *old* value, so
        // the state being moved to is passed in explicitly.
        private void Nav_PaneOpening(NavigationView sender, object args) => UpdateChipLayout(true);

        private void Nav_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args) => UpdateChipLayout(false);

        // Kept as the authoritative reconcile. Normally a no-op re-application of what the pair
        // above already did, but a close can be cancelled through PaneClosingEventArgs.Cancel, and
        // then this is what puts the chip back.
        private void Nav_PaneOpened(NavigationView sender, object args) => UpdateChipLayout(Nav.IsPaneOpen);

        private void Nav_PaneClosed(NavigationView sender, object args) => UpdateChipLayout(Nav.IsPaneOpen);

        private void UpdateChipLayout(bool expanded)
        {
            // `expanded` is the pane-open state, deliberately not "DisplayMode == Expanded ||
            // IsPaneOpen": at any
            // width at or above ExpandedModeThresholdWidth the pane toggle button collapses the
            // pane to the 48px icon rail while DisplayMode *stays* Expanded, so that extra clause
            // forced the expanded branch in exactly the state this method exists to handle -
            // measured live at a 900px-wide window with the pane toggled closed, ModeChip came out
            // 24x69: the full text stack still laid out inside a 24px-wide clip, i.e. the empty
            // husk, unchanged. IsPaneOpen is true in every state where the pane is actually wide
            // enough for the text (Expanded open, and the Minimal overlay, which opens at
            // OpenPaneLength) and false in every state where it is not.
            ChipTextStack.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

            // On the rail the chip stops being a full-width bar and becomes a square centred in the
            // rail, on the same vertical centre line as the NavigationViewItems above it (they
            // centre themselves the same way: 40px wide inside the 48px rail). Width/Height are
            // cleared back to Auto (double.NaN) when the pane reopens, or the square would survive
            // into the expanded layout and clip the text.
            //
            // The centring is done with an explicit left inset, NOT HorizontalAlignment.Center:
            // NavigationView's pane-footer presenter anchors its content to the rail's left edge
            // and sizes to it, so a Center alignment there is simply ignored - measured live, the
            // chip came back at exactly PaneRoot.X with Center set. Deriving the inset from
            // CompactPaneLength keeps it correct if that ever changes.
            var inset = Math.Max(0, (Nav.CompactPaneLength - CompactSize) / 2);
            ModeChip.HorizontalAlignment = expanded ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            ModeChip.Width = expanded ? double.NaN : CompactSize;
            ModeChip.Height = expanded ? double.NaN : CompactSize;
            ModeChip.Margin = expanded ? new Thickness(12, 8, 12, 16) : new Thickness(inset, 8, inset, 16);

            // Padding sits on ChipBody rather than the Button so ChipAccent can meet the Button's
            // own edge. Collapsed, the glyph is all ChipBody still holds, so it drops its padding
            // entirely and centres in whatever the accent bar leaves.
            ChipBody.Padding = expanded ? new Thickness(12, 8, 12, 8) : new Thickness(0);
            ChipBody.ColumnSpacing = expanded ? 10 : 0;
            ChipBody.HorizontalAlignment = expanded ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        }

        /// <summary>Side of the square the chip collapses to on the icon rail. The rail is 48px
        /// wide; 32 leaves 8px either side, matching the inset NavigationViewItem's own icon
        /// column uses.</summary>
        private const double CompactSize = 32;

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            args.Cancel = true;
            sender.Hide();

            // Say so the first time, and only the first time. A window that disappears when you
            // close it reads as a crash unless something tells you the firewall is still up -
            // but after the user knows, repeating it every close is nagging.
            App.Tray?.NotifyClosedToTray();
        }

        /// <summary>Brings the window back from the tray. Activate() alone is not enough once the
        /// window has been hidden with AppWindow.Hide() - it has to be shown again first.</summary>
        public void ShowFromTray()
        {
            AppWindow.Show();
            Activate();
        }

        internal Brush ModeStateToBrush(string modeStateKey)
            => (Brush)s_modeStateToBrush.Convert(modeStateKey, typeof(Brush), null!, null!);

        /// <summary>The pane items and the pages they show, in one place. Keeping the two
        /// directions apart is what let the pane drift out of step with the frame: the tag-to-page
        /// mapping existed and the page-to-tag one did not.</summary>
        private static readonly (string Tag, Type Page)[] NavDestinations =
        {
            ("connections", typeof(ConnectionsPage)),
            ("rules",       typeof(RulesPage)),
            ("settings",    typeof(SettingsPage)),
        };

        /// <summary>The window's one page-navigation entry point, shared by the nav pane and the
        /// tray menu's Manage/Connections items (TrayIconService.ShowAndNavigate). Re-navigating to
        /// the page already showing is skipped so the entrance transition does not replay - and so a
        /// NavigationCacheMode.Enabled page is not needlessly re-created, losing its filter state.</summary>
        public void NavigateTo(Type pageType)
        {
            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());

            // The pane has to follow, not just the frame. Coming from the tray this used to move
            // the frame alone, leaving the pane highlighting the page you were on a moment ago -
            // and worse, leaving it *selected*: clicking that item then set SelectedItem to what it
            // already was, which raises no SelectionChanged, so the pane appeared dead until you
            // picked some third page first.
            SelectPaneItemFor(pageType);
        }

        /// <summary>Moves the pane highlight onto the item for <paramref name="pageType"/>. Setting
        /// SelectedItem re-enters Nav_SelectionChanged, but that lands back in NavigateTo with the
        /// frame already on this page, so the navigate is skipped and this runs once more against an
        /// item that is now already selected - no assignment, no third pass.</summary>
        private void SelectPaneItemFor(Type pageType)
        {
            string? tag = null;
            foreach (var (t, page) in NavDestinations)
            {
                if (page == pageType) { tag = t; break; }
            }
            if (tag is null)
                return;

            foreach (var entry in Nav.MenuItems)
            {
                if (entry is NavigationViewItem item && (string)item.Tag == tag)
                {
                    if (!ReferenceEquals(Nav.SelectedItem, item))
                        Nav.SelectedItem = item;
                    return;
                }
            }
        }

        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item)
                return;

            var tag = (string)item.Tag;
            var targetType = typeof(ConnectionsPage);
            foreach (var (t, page) in NavDestinations)
            {
                if (t == tag) { targetType = page; break; }
            }

            NavigateTo(targetType);
        }

        private async void ModeChip_Click(object sender, RoutedEventArgs e)
        {
            // Guard re-entrancy. Without this a double-click (or a held Enter key while the chip
            // has focus) interleaves two refreshes and can leave two flyouts open, so two mode
            // switches race and the chip ends up showing whichever finished last rather than what
            // the user actually chose - a stale success could paint over a real failure.
            ModeChip.IsEnabled = false;
            try
            {
                await ModeChipClickCoreAsync();
            }
            finally
            {
                ModeChip.IsEnabled = true;
            }
        }

        private async System.Threading.Tasks.Task ModeChipClickCoreAsync()
        {
            await Shell.RefreshAsync();

            if (!Shell.CanSwitchMode)
            {
                await ShowMessageAsync(
                    Shell.IsConnected ? Loc.T(LocKeys.Status.Locked) : Loc.T(LocKeys.Status.NotConnected),
                    Shell.IsConnected
                        ? Loc.T(LocKeys.Status.LockedDetail)
                        : Loc.T(LocKeys.Status.NotConnectedDetail));
                return;
            }

            var menu = new MenuFlyout();
            foreach (var info in FirewallModes.Selectable)
            {
                var mode = info.Mode;
                var item = new MenuFlyoutItem { Text = info.Label };
                item.Click += async (_, _) => await ApplyModeAsync(mode);
                menu.Items.Add(item);
            }

            // FlyoutBase exposes neither FlowDirection nor RequestedTheme, so the presenter is
            // styled instead - see App.ApplyShellStyling. The direction did arrive on its own, from
            // the chip this is shown at; the theme does not, because the presenter lives in the
            // popup root rather than under the flipped-and-themed window content.
            App.ApplyShellStyling(menu);
            menu.ShowAt(ModeChip);
        }

        private async System.Threading.Tasks.Task ApplyModeAsync(FirewallMode mode)
        {
            if (mode == Shell.CurrentMode)
                return;

            // Learning lets all traffic through, so it keeps the confirmation the WinForms GUI shows.
            if (mode == FirewallMode.Learning && !await ConfirmLearningModeAsync())
                return;

            MessageType resp;
            try
            {
                resp = await Shell.SwitchModeAsync(mode);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync(Loc.T(LocKeys.Mode.SwitchFailedUnreachableTitle), ex.Message);
                return;
            }

            // Anything other than MODE_SWITCH is a failure. An unrecognised response must not
            // look like success - on a firewall, a mode change that did not take must not
            // appear to have taken.
            if (resp != MessageType.MODE_SWITCH)
            {
                var (title, body) = resp switch
                {
                    MessageType.RESPONSE_LOCKED => (Loc.T(LocKeys.Mode.SwitchFailedLockedTitle),
                        Loc.T(LocKeys.Status.LockedDetail)),
                    MessageType.COM_ERROR => (Loc.T(LocKeys.Mode.SwitchFailedComErrorTitle),
                        Loc.T(LocKeys.Mode.SwitchFailedComErrorDetail)),
                    _ => (Loc.T(LocKeys.Mode.SwitchFailedGenericTitle),
                        Loc.T(LocKeys.Mode.SwitchFailedGenericDetail, resp)),
                };
                await ShowMessageAsync(title, body);
            }
        }

        private async System.Threading.Tasks.Task<bool> ConfirmLearningModeAsync()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                FlowDirection = App.UiFlowDirection,
                RequestedTheme = App.UiElementTheme,
                Title = Loc.T(LocKeys.Mode.LearningConfirmTitle),
                Content = Loc.T(LocKeys.Mode.LearningConfirmBody),
                PrimaryButtonText = Loc.T(LocKeys.Mode.LearningConfirmConfirm),
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
                DefaultButton = ContentDialogButton.Close,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async System.Threading.Tasks.Task ShowMessageAsync(string title, string body)
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
            await dialog.ShowAsync();
        }
    }
}
