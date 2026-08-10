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

        /// <summary>The single selected application row the detail pane is currently editing, or
        /// null when zero/multiple rows are selected (the pane is collapsed in that case).</summary>
        private RuleListItem? _selectedDetailItem;

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

        private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateRemoveButton();
            UpdateDetailPane();
        }

        private void UpdateRemoveButton()
        {
            var count = AppsList.SelectedItems.Count;
            RemoveButton.IsEnabled = count > 0 && !_committing;
            RemoveButton.Content = count > 0
                ? Loc.T(LocKeys.Rules.Remove) + " " + Loc.T(LocKeys.Connections.SectionCount, count)
                : Loc.T(LocKeys.Rules.Remove);
        }

        /// <summary>Shows/hides the detail pane based on selection count, and seeds it from the
        /// single selected row's policy. Only ever touches local UI state - never commits or shows
        /// a dialog, so it is safe to call from a selection-changed handler.</summary>
        private void UpdateDetailPane()
        {
            var selected = AppsList.SelectedItems.Cast<RuleListItem>().ToList();
            if (selected.Count != 1)
            {
                _selectedDetailItem = null;
                DetailPane.Visibility = Visibility.Collapsed;
                return;
            }

            _selectedDetailItem = selected[0];
            DetailPane.Visibility = Visibility.Visible;
            SeedDetailPane(_selectedDetailItem);
        }

        private void SeedDetailPane(RuleListItem item)
        {
            var row = item.Row;
            DetailName.Text = row.Name;
            DetailKind.Text = row.Kind;
            DetailSubjectDetail.Text = row.Detail;
            DetailSubjectDetail.Visibility = string.IsNullOrEmpty(row.Detail) ? Visibility.Collapsed : Visibility.Visible;

            var policy = row.Exception!.Policy;
            bool readOnly = policy.PolicyType == PolicyType.RuleList;
            DetailReadOnlyNote.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
            DetailEditorFields.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;

            if (!readOnly)
            {
                // Reset before seeding so a preset that leaves a field blank (e.g. Blocked) does not
                // keep a value carried over from the previously selected row.
                TcpOutBox.Text = string.Empty;
                UdpOutBox.Text = string.Empty;
                TcpInBox.Text = string.Empty;
                UdpInBox.Text = string.Empty;
                LanOnlyCheck.IsChecked = false;

                // Pattern-matched against the actual ExceptionPolicy subclass shapes in
                // SimpleDeFence.Core/ExceptionPolicy.cs - not the PolicyType enum - so this stays
                // correct if a subclass ever changes without a matching PolicyType edit.
                switch (policy)
                {
                    case HardBlockPolicy:
                        PresetBlocked.IsChecked = true;
                        break;
                    case UnrestrictedPolicy { LocalNetworkOnly: true }:
                        PresetUnrestrictedLan.IsChecked = true;
                        break;
                    case UnrestrictedPolicy:
                        PresetUnrestricted.IsChecked = true;
                        break;
                    case TcpUdpPolicy tcpUdp:
                        PresetTcpUdp.IsChecked = true;
                        TcpOutBox.Text = tcpUdp.AllowedRemoteTcpConnectPorts ?? string.Empty;
                        UdpOutBox.Text = tcpUdp.AllowedRemoteUdpConnectPorts ?? string.Empty;
                        TcpInBox.Text = tcpUdp.AllowedLocalTcpListenerPorts ?? string.Empty;
                        UdpInBox.Text = tcpUdp.AllowedLocalUdpListenerPorts ?? string.Empty;
                        LanOnlyCheck.IsChecked = tcpUdp.LocalNetworkOnly;
                        break;
                }
            }

            UpdateTcpUdpFieldsEnabled();
            UpdateApplyButtonEnabled();
        }

        /// <summary>Local UI state only (which fields are enabled) - fires from a RadioButton's own
        /// Checked event, so per the Task 4 reentrancy fix it must never commit or show a dialog.</summary>
        private void PresetRadio_Checked(object sender, RoutedEventArgs e) => UpdateTcpUdpFieldsEnabled();

        private void UpdateTcpUdpFieldsEnabled()
        {
            // StackPanel (unlike its Control children) has no IsEnabled of its own to cascade, so
            // each field is toggled individually.
            bool enabled = PresetTcpUdp.IsChecked == true;
            TcpOutBox.IsEnabled = enabled;
            UdpOutBox.IsEnabled = enabled;
            TcpInBox.IsEnabled = enabled;
            UdpInBox.IsEnabled = enabled;
            LanOnlyCheck.IsEnabled = enabled;
        }

        private void UpdateApplyButtonEnabled()
        {
            var readOnly = _selectedDetailItem?.Row.Exception!.Policy.PolicyType == PolicyType.RuleList;
            ApplyButton.IsEnabled = _selectedDetailItem is not null && !readOnly && !_committing;
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

        /// <summary>
        /// Plain Button.Click handler - the same safe shape RemoveButton_Click already uses. This is
        /// the only place in the detail pane allowed to call CommitAsync/show a dialog; the
        /// RadioButton/CheckBox handlers above only ever touch local UI state, per the Task 4
        /// reentrancy fix (firing a ContentDialog synchronously from inside another control's own
        /// event dispatch corrupts WinUI against the sample client's synchronously-completing tasks).
        /// </summary>
        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing || _selectedDetailItem is null)
                return;

            var original = _selectedDetailItem.Row.Exception!;
            if (original.Policy.PolicyType == PolicyType.RuleList)
                return; // Apply is disabled for this row; guard in case that ever drifts.

            // Captured now, not read back off _selectedDetailItem after the commit: RefreshAsync
            // below rebuilds _apps, which resets AppsList's selection and (via
            // AppsList_SelectionChanged -> UpdateDetailPane) nulls _selectedDetailItem out from
            // under this handler.
            var rowName = _selectedDetailItem.Row.Name;
            var newPolicy = BuildPolicyFromEditor();

            FirewallExceptionV3 edited;
            try
            {
                edited = RuleEdit.ApplyPreset(original, newPolicy);
            }
            catch (InvalidOperationException ex)
            {
                // Unreachable given the guard above, but a thrown exception must not look like
                // nothing happened.
                await ShowResultAsync(Loc.T(LocKeys.Rules.DetailApplyFailedTitle), ex.Message);
                return;
            }

            var resp = await CommitAsync(profile =>
                profile.AddExceptions(new List<FirewallExceptionV3> { edited }));

            if (resp == MessageType.PUT_SETTINGS)
            {
                // Refresh first: RefreshAsync's own success path unconditionally sets
                // Notice.IsOpen = false, so setting the confirmation before refreshing would just
                // have it wiped out again before it was ever seen. A lightweight, non-blocking
                // InfoBar (unlike Remove's dialog) is enough here - Apply is a frequent, low-risk
                // action on a single row, and the refreshed row already shows the new policy text.
                // Success (Success severity) and failure (Error severity, from
                // ShowResultAsync/ShowNotice elsewhere) are always paired with distinct
                // title/message text, never signalled by colour alone.
                await RefreshAsync();
                ShowNotice(InfoBarSeverity.Success, Loc.T(LocKeys.Rules.DetailApplySuccess), rowName);
            }
            else
            {
                await ShowResultAsync(Loc.T(LocKeys.Rules.DetailApplyFailedTitle), FailureDetail(resp,
                    LocKeys.Rules.DetailApplyFailedLockedDetail, LocKeys.Rules.DetailApplyFailedGenericDetail));
            }
        }

        /// <summary>Reads the preset editor's current state into the matching ExceptionPolicy
        /// subclass. TCP/UDP is the fallback branch: it is the only preset besides the mutually
        /// exclusive radios above, and exactly one of the four is always checked once a row has been
        /// seeded (SeedDetailPane always sets one).</summary>
        private ExceptionPolicy BuildPolicyFromEditor()
        {
            if (PresetBlocked.IsChecked == true)
                return new HardBlockPolicy();

            if (PresetUnrestricted.IsChecked == true)
                return new UnrestrictedPolicy { LocalNetworkOnly = false };

            if (PresetUnrestrictedLan.IsChecked == true)
                return new UnrestrictedPolicy { LocalNetworkOnly = true };

            return new TcpUdpPolicy
            {
                AllowedRemoteTcpConnectPorts = NormalizePorts(TcpOutBox.Text),
                AllowedRemoteUdpConnectPorts = NormalizePorts(UdpOutBox.Text),
                AllowedLocalTcpListenerPorts = NormalizePorts(TcpInBox.Text),
                AllowedLocalUdpListenerPorts = NormalizePorts(UdpInBox.Text),
                LocalNetworkOnly = LanOnlyCheck.IsChecked == true,
            };
        }

        private static string? NormalizePorts(string text)
        {
            var trimmed = text?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
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
            UpdateApplyButtonEnabled();
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
                UpdateApplyButtonEnabled();
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
