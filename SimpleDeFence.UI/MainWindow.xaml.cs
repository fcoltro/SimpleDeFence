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

            // Run the nav pane to the top edge - the standard Windows 11 app shape.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            ContentFrame.Navigate(typeof(ConnectionsPage));
            _ = Shell.RefreshAsync();

            // NavigationView starts in whatever DisplayMode its initial width lands on, but
            // DisplayModeChanged only fires on a subsequent change - without this call the chip
            // still shows its full text on a window that opens narrow until the user resizes it.
            UpdateChipLayout();
        }

        /// <summary>Mirrors what NavigationViewItem does automatically for the menu items above the
        /// chip: when the pane is closed, drop the text and centre what is left, so the footer
        /// degrades to a compact indicator instead of an empty husk (ChipTextStack's comment in
        /// MainWindow.xaml has the full story). DisplayModeChanged alone is not enough - the pane
        /// toggle button collapses the pane to the 48px rail without changing DisplayMode at all -
        /// so PaneOpened/PaneClosed are handled too.</summary>
        private void Nav_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
            => UpdateChipLayout();

        private void Nav_PaneOpened(NavigationView sender, object args) => UpdateChipLayout();

        private void Nav_PaneClosed(NavigationView sender, object args) => UpdateChipLayout();

        private void UpdateChipLayout()
        {
            // IsPaneOpen alone, deliberately not "DisplayMode == Expanded || IsPaneOpen": at any
            // width at or above ExpandedModeThresholdWidth the pane toggle button collapses the
            // pane to the 48px icon rail while DisplayMode *stays* Expanded, so that extra clause
            // forced the expanded branch in exactly the state this method exists to handle -
            // measured live at a 900px-wide window with the pane toggled closed, ModeChip came out
            // 24x69: the full text stack still laid out inside a 24px-wide clip, i.e. the empty
            // husk, unchanged. IsPaneOpen is true in every state where the pane is actually wide
            // enough for the text (Expanded open, and the Minimal overlay, which opens at
            // OpenPaneLength) and false in every state where it is not.
            var expanded = Nav.IsPaneOpen;
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

        internal Brush ModeStateToBrush(string modeStateKey)
            => (Brush)s_modeStateToBrush.Convert(modeStateKey, typeof(Brush), null!, null!);

        /// <summary>The window's one page-navigation entry point, shared by the nav pane and the
        /// tray menu's Manage/Connections items (TrayIconService.ShowAndNavigate). Re-navigating to
        /// the page already showing is skipped so the entrance transition does not replay - and so a
        /// NavigationCacheMode.Enabled page is not needlessly re-created, losing its filter state.</summary>
        public void NavigateTo(Type pageType)
        {
            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
        }

        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item)
                return;

            var targetType = (string)item.Tag switch
            {
                "connections" => typeof(ConnectionsPage),
                "rules" => typeof(RulesPage),
                "settings" => typeof(SettingsPage),
                _ => typeof(ConnectionsPage),
            };

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
                Title = title,
                Content = body,
                CloseButtonText = Loc.T(LocKeys.Common.Ok),
            };
            await dialog.ShowAsync();
        }
    }
}
