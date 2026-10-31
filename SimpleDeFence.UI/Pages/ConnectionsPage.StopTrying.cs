using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence;
using SimpleDeFence.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    public sealed partial class ConnectionsPage
    {
        private bool _stopBusy;

        /// <summary>
        /// "Stop it trying", from a row's right-click menu: lists the services, scheduled tasks and
        /// startup entries from the same publisher as the row's program, each with a tick for
        /// "keep running", and switches off the ones that are unticked.
        ///
        /// Blocking only refuses a connection; it cannot make the program stop asking. Suites like
        /// Adobe Creative Cloud and Autodesk run updaters, sync agents and analytics that retry a
        /// refusal for ever. Not starting them is the only thing that makes them stop.
        /// </summary>
        private async Task StopTryingAsync(string appName, string appPath)
        {
            if (_stopBusy)
                return;

            _stopBusy = true;
            try
            {
                var list = await App.Firewall.GetBackgroundItemsAsync(appPath);
                if (list is null)
                {
                    ShowNotice(InfoBarSeverity.Warning, appName, Loc.T(LocKeys.Connections.StopUnreachable));
                    return;
                }
                if (list.Publisher.Length == 0 && list.ScopeFolder.Length == 0)
                {
                    ShowNotice(InfoBarSeverity.Informational, appName, Loc.T(LocKeys.Connections.StopUnsigned));
                    return;
                }
                // Who the items belong to: the publisher, or - found by folder - the program itself.
                var who = list.Publisher.Length > 0 ? list.Publisher : appName;
                if (list.Items.Length == 0)
                {
                    ShowNotice(InfoBarSeverity.Informational, appName, Loc.T(LocKeys.Connections.StopNone, who));
                    return;
                }

                var changes = await AskStopTryingAsync(list, appName);
                if (changes is null || changes.Length == 0)
                    return;

                var (result, _) = await App.Firewall.SetBackgroundItemsAsync(appPath, changes);
                if (result == MessageType.SET_BACKGROUND_ITEMS)
                {
                    int off = changes.Count(c => !c.Enable);
                    int on = changes.Length - off;
                    ShowNotice(InfoBarSeverity.Success, who, Loc.T(LocKeys.Connections.StopDone, off, on));
                }
                else
                {
                    var body = result == MessageType.RESPONSE_LOCKED
                        ? Loc.T(LocKeys.Connections.StopLocked)
                        : Loc.T(LocKeys.Connections.StopFailed);
                    ShowNotice(InfoBarSeverity.Warning, who, body);
                }
            }
            finally
            {
                _stopBusy = false;
            }
        }

        /// <summary>The dialog itself. Returns the changes to make, or null when cancelled. A tick
        /// means "keep it running", so the list opens showing things as they are now.</summary>
        private async Task<BackgroundItemChange[]?> AskStopTryingAsync(TwMessageBackgroundItems list, string appName)
        {
            var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
            bool byFolder = list.Publisher.Length == 0;
            var who = byFolder ? appName : list.Publisher;
            var panel = new StackPanel { Spacing = 8, MinWidth = 380, MaxWidth = 560 };
            panel.Children.Add(new TextBlock
            {
                Text = byFolder
                    ? Loc.T(LocKeys.Connections.StopIntroFolder, appName)
                    : Loc.T(LocKeys.Connections.StopIntro, list.Publisher),
                TextWrapping = TextWrapping.Wrap,
            });

            // The program's own signature failed and the publisher came from its folder instead:
            // say so, because a changed program is worth a scan whatever happens to its helpers.
            if (list.PublisherFromFolder)
            {
                panel.Children.Add(new InfoBar
                {
                    Severity = InfoBarSeverity.Warning,
                    IsOpen = true,
                    IsClosable = false,
                    Message = Loc.T(LocKeys.Connections.StopFromFolder, list.Publisher, appName),
                });
            }
            else if (byFolder)
            {
                // No signature anywhere: these were matched on folder alone, which is weaker.
                panel.Children.Add(new InfoBar
                {
                    Severity = InfoBarSeverity.Warning,
                    IsOpen = true,
                    IsClosable = false,
                    Message = Loc.T(LocKeys.Connections.StopUnsignedFolder, appName, list.ScopeFolder),
                });
            }

            var boxes = new List<(BackgroundItem Item, CheckBox Box)>();
            var rows = new StackPanel { Spacing = 4 };
            foreach (var item in list.Items)
            {
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap });
                text.Children.Add(new TextBlock
                {
                    Text = $"{KindLabel(item.Kind)} · {item.Path}",
                    Style = caption,
                    Opacity = 0.7,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                if (item.NeededToOpen)
                {
                    var warning = new TextBlock
                    {
                        Text = Loc.T(LocKeys.Connections.StopNeeded, who),
                        Style = caption,
                        TextWrapping = TextWrapping.Wrap,
                    };
                    // A missing theme resource must not take the dialog down with it.
                    if (Application.Current.Resources.TryGetValue("SystemFillColorCautionBrush", out var caution)
                        && caution is Microsoft.UI.Xaml.Media.Brush cautionBrush)
                        warning.Foreground = cautionBrush;
                    text.Children.Add(warning);
                }

                var box = new CheckBox { Content = text, IsChecked = item.Enabled };
                ToolTipService.SetToolTip(box, item.Path);
                boxes.Add((item, box));
                rows.Children.Add(box);
            }
            panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                FlowDirection = App.UiFlowDirection,
                RequestedTheme = App.UiElementTheme,
                Title = Loc.T(LocKeys.Connections.StopTitle, who),
                Content = panel,
                PrimaryButtonText = Loc.T(LocKeys.Connections.StopApply),
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
                DefaultButton = ContentDialogButton.Primary,
            };
            if (list.Items.Any(i => i.SwitchedOffHere))
                dialog.SecondaryButtonText = Loc.T(LocKeys.Connections.StopAllOn);

            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                ShowNotice(InfoBarSeverity.Informational, who, Loc.T(LocKeys.Common.AnotherDialogOpenDetail));
                return null;
            }

            return result switch
            {
                ContentDialogResult.Primary => boxes
                    .Where(b => (b.Box.IsChecked == true) != b.Item.Enabled)
                    .Select(b => new BackgroundItemChange { Kind = b.Item.Kind, Id = b.Item.Id, Enable = b.Box.IsChecked == true })
                    .ToArray(),
                ContentDialogResult.Secondary => list.Items
                    .Where(i => i.SwitchedOffHere)
                    .Select(i => new BackgroundItemChange { Kind = i.Kind, Id = i.Id, Enable = true })
                    .ToArray(),
                _ => null,
            };
        }

        private static string KindLabel(BackgroundItemKind kind) => kind switch
        {
            BackgroundItemKind.Service => Loc.T(LocKeys.Connections.StopKindService),
            BackgroundItemKind.ScheduledTask => Loc.T(LocKeys.Connections.StopKindTask),
            _ => Loc.T(LocKeys.Connections.StopKindStartup),
        };
    }
}
