using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence;
using SimpleDeFence.DatabaseClasses;
using SimpleDeFence.Localization;
using SimpleDeFence.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    /// <summary>One selectable application-rule row.</summary>
    public sealed class RuleListItem
    {
        public RuleRow Row { get; init; } = null!;
        public string Name => Row.Name;
        public string Kind => Row.Kind;
        public string Detail => Row.Detail;
        public string Policy => Row.Policy;
        public bool IsBlocked => Row.IsBlocked;

        /// <summary>Blocked entries are tinted, the same signal ExceptionRow/SettingsForm give via
        /// row colour.</summary>
        // global:: is required - inside SimpleDeFence.UI.Pages a bare "Windows" binds to
        // SimpleDeFence.Windows, not the platform namespace.
        public global::Microsoft.UI.Xaml.Media.Brush RowBackground => IsBlocked
            ? new global::Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0x40, 0xE8, 0x11, 0x23))
            : new global::Microsoft.UI.Xaml.Media.SolidColorBrush(global::Microsoft.UI.Colors.Transparent);
    }

    /// <summary>One special-exception row with its toggle.</summary>
    public sealed class SpecialListItem
    {
        public RuleRow Row { get; init; } = null!;
        public string Name => Row.Name;
        public bool IsOn
        {
            get => Row.Enabled;
            set => ToggleRequested?.Invoke(this, value);
        }

        public event EventHandler<bool>? ToggleRequested;
    }

    public sealed partial class RulesPage : Page
    {
        private readonly ObservableCollection<RuleListItem> _apps = new();
        private readonly ObservableCollection<SpecialListItem> _specialRecommended = new();
        private readonly ObservableCollection<SpecialListItem> _specialOptional = new();
        private IReadOnlyList<RuleRow> _rows = Array.Empty<RuleRow>();
        private bool _busy;
        private bool _committing;

        public RulesPage()
        {
            InitializeComponent();
            // Keeps this page instance (and its filter/expander state) alive across navigating to
            // Connections and back, instead of Frame recreating it - same rationale as
            // ConnectionsPage.
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
            AppsList.ItemsSource = _apps;
            SpecialRecommendedList.ItemsSource = _specialRecommended;
            SpecialOptionalList.ItemsSource = _specialOptional;
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
                    _rows = Array.Empty<RuleRow>();
                }
                else
                {
                    Notice.IsOpen = false;
                    var db = await App.Firewall.GetAppDatabaseAsync();
                    _rows = RuleListBuilder.Build(App.Firewall.Config.ActiveProfile, db ?? new AppDatabase());
                }
            }
            catch (Exception ex)
            {
                // Never let a throw mid-refresh look like a clean, empty screen - surface it and
                // keep whatever rows were already loaded on screen.
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
            }
            finally
            {
                // A throw must not leave the page busy forever, permanently disabling Refresh.
                SetBusy(false);
            }

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var term = FilterBox.Text?.Trim() ?? string.Empty;
            var filtered = RuleListBuilder.Filter(_rows, term);

            _apps.Clear();
            foreach (var row in filtered.Where(r => r.Group == RuleGroup.Applications))
                _apps.Add(new RuleListItem { Row = row });

            RebuildSpecial(_specialRecommended, filtered, RuleGroup.SpecialRecommended);
            RebuildSpecial(_specialOptional, filtered, RuleGroup.SpecialOptional);

            AppsHeader.Text = Loc.T(LocKeys.Rules.SectionApplications)
                + " " + Loc.T(LocKeys.Connections.SectionCount, _apps.Count);
            SpecialRecommendedHeader.Text = Loc.T(LocKeys.Rules.SectionSpecialRecommended)
                + " " + Loc.T(LocKeys.Connections.SectionCount, _specialRecommended.Count);
            SpecialOptionalHeader.Text = Loc.T(LocKeys.Rules.SectionSpecialOptional)
                + " " + Loc.T(LocKeys.Connections.SectionCount, _specialOptional.Count);

            SetEmpty(AppsEmpty, _apps.Count, LocKeys.Rules.EmptyApplications, term);
            var specialCount = _specialRecommended.Count + _specialOptional.Count;
            SetEmpty(SpecialEmpty, specialCount, LocKeys.Rules.EmptySpecial, term);

            UpdateRemoveButton();
        }

        private void RebuildSpecial(ObservableCollection<SpecialListItem> target,
            IReadOnlyList<RuleRow> rows, RuleGroup group)
        {
            target.Clear();
            foreach (var row in rows.Where(r => r.Group == group))
            {
                var item = new SpecialListItem { Row = row };
                // The ToggleSwitch fires this synchronously from inside its own Toggled handling
                // (via the x:Bind TwoWay setter), still on that control's call stack. Against the
                // sample client every awaited step below completes synchronously (Task.FromResult),
                // so without deferring, ContentDialog.ShowAsync() and the collection rebuild in
                // RefreshAsync would run nested inside the ToggleSwitch's own event dispatch -
                // WinUI does not tolerate that reentrancy and crashes with an access violation
                // (reproduced: Microsoft.UI.Xaml.dll, 0xc0000005). Enqueueing via DispatcherQueue
                // lets the ToggleSwitch's own call stack unwind first.
                item.ToggleRequested += (_, enabled) => DispatcherQueue.TryEnqueue(() => _ = ToggleSpecialAsync(item, enabled));
                target.Add(item);
            }
        }

        private static void SetEmpty(TextBlock target, int count, string emptyKey, string term)
        {
            if (count != 0)
            {
                target.Visibility = Visibility.Collapsed;
                return;
            }

            target.Text = term.Length == 0 ? Loc.T(emptyKey) : Loc.T(LocKeys.Rules.EmptyFiltered);
            target.Visibility = Visibility.Visible;
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRemoveButton();

        private void UpdateRemoveButton()
        {
            var count = AppsList.SelectedItems.Count;
            RemoveButton.IsEnabled = count > 0 && !_committing;
            RemoveButton.Content = count > 0
                ? Loc.T(LocKeys.Rules.Remove) + " " + Loc.T(LocKeys.Connections.SectionCount, count)
                : Loc.T(LocKeys.Rules.Remove);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = AppsList.SelectedItems.Cast<RuleListItem>().ToList();
            if (selected.Count == 0 || _committing)
                return;

            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = Loc.T(LocKeys.Rules.RemoveConfirmTitle),
                Content = Loc.T(LocKeys.Rules.RemoveConfirmBody, selected.Count),
                PrimaryButtonText = Loc.T(LocKeys.Rules.Remove),
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            var ids = selected.Select(s => s.Row.Exception!.Id).ToList();
            var resp = await CommitAsync(profile => RuleEdit.RemoveExceptions(profile, ids));

            if (resp == MessageType.PUT_SETTINGS)
            {
                await ShowResultAsync(Loc.T(LocKeys.Rules.RemoveSuccessTitle),
                    Loc.T(LocKeys.Rules.RemoveSuccessBody, ids.Count));
                await RefreshAsync();
            }
            else
            {
                await ShowResultAsync(Loc.T(LocKeys.Rules.RemoveFailedTitle), FailureDetail(resp,
                    LocKeys.Rules.RemoveFailedLockedDetail, LocKeys.Rules.RemoveFailedGenericDetail));
            }
        }

        private async Task ToggleSpecialAsync(SpecialListItem item, bool enabled)
        {
            // Serializes with Remove and with other toggles: only one commit in flight at a time.
            // The ToggleSwitch already shows the new value (its IsOn setter fired this handler via
            // the TwoWay binding) - RefreshAsync below is what reconciles it back to the truth,
            // whichever way the commit went.
            if (_committing)
                return;

            var id = item.Row.SpecialId!;
            var resp = await CommitAsync(profile => RuleEdit.SetSpecialEnabled(profile, id, enabled));

            if (resp == MessageType.PUT_SETTINGS)
            {
                await RefreshAsync();
            }
            else
            {
                // The toggle already flipped visually; revert it to the truth.
                await RefreshAsync();
                await ShowResultAsync(Loc.T(LocKeys.Rules.SpecialToggleFailedTitle), FailureDetail(resp,
                    LocKeys.Rules.SpecialToggleFailedLockedDetail, LocKeys.Rules.SpecialToggleFailedGenericDetail));
            }
        }

        /// <summary>One serialized commit path; concurrent commits are refused, never raced.</summary>
        private async Task<MessageType> CommitAsync(Action<ServerProfileConfiguration> mutate)
        {
            _committing = true;
            UpdateRemoveButton();
            try
            {
                return await App.Firewall.CommitProfileChangesAsync(mutate);
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
                return MessageType.COM_ERROR;
            }
            finally
            {
                _committing = false;
                UpdateRemoveButton();
            }
        }

        private static string FailureDetail(MessageType resp, string lockedKey, string genericKey) => resp switch
        {
            MessageType.RESPONSE_LOCKED => Loc.T(lockedKey),
            _ => Loc.T(genericKey, resp),
        };

        private async Task ShowResultAsync(string title, string body)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
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
                // Single-dialog-per-XamlRoot rule; fall back to the InfoBar rather than crash.
                ShowNotice(InfoBarSeverity.Informational, title, body);
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
