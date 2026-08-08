using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

            Title = "SimpleDeFence";

            // Run the nav pane to the top edge - the standard Windows 11 app shape.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            ContentFrame.Navigate(typeof(ApplicationsPage));
            _ = Shell.RefreshAsync();
        }

        internal Brush ModeStateToBrush(string modeStateKey)
            => (Brush)s_modeStateToBrush.Convert(modeStateKey, typeof(Brush), null!, null!);

        // Rules is currently the only destination; Connections and Settings arrive in their own
        // plans, at which point this maps item.Tag to a page type. A nav entry pointing at an
        // empty placeholder page would be worse than no entry at all.
        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem)
                return;

            if (ContentFrame.CurrentSourcePageType != typeof(ApplicationsPage))
                ContentFrame.Navigate(typeof(ApplicationsPage), null, new EntranceNavigationTransitionInfo());
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
                    Shell.IsConnected ? "Configuration is locked" : "Not connected",
                    Shell.IsConnected
                        ? "Unlock the configuration before changing the mode."
                        : "Could not reach the SimpleDeFence service. Is it installed and running?");
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
                await ShowMessageAsync("Could not reach the service", ex.Message);
                return;
            }

            // Anything other than MODE_SWITCH is a failure. An unrecognised response must not
            // look like success - on a firewall, a mode change that did not take must not
            // appear to have taken.
            if (resp != MessageType.MODE_SWITCH)
            {
                var (title, body) = resp switch
                {
                    MessageType.RESPONSE_LOCKED => ("SimpleDeFence is currently locked",
                        "Unlock the configuration before changing the mode."),
                    MessageType.COM_ERROR => ("Communication with the service failed",
                        "The mode was not changed."),
                    _ => ("Operation failed", $"The service returned {resp}. The mode was not changed."),
                };
                await ShowMessageAsync(title, body);
            }
        }

        private async System.Threading.Tasks.Task<bool> ConfirmLearningModeAsync()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
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

        private async System.Threading.Tasks.Task ShowMessageAsync(string title, string body)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = body,
                CloseButtonText = "OK",
            };
            await dialog.ShowAsync();
        }
    }
}
