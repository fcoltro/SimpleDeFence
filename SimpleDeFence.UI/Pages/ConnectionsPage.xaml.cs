using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SimpleDeFence;
using SimpleDeFence.Localization;
using SimpleDeFence.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    /// <summary>Connected/Open row as shown in a list.</summary>
    public sealed class ConnectionListItem
    {
        public string AppName { get; init; } = string.Empty;
        public string Protocol { get; init; } = string.Empty;
        public string LocalEndpoint { get; init; } = string.Empty;
        public string RemoteEndpoint { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
    }

    /// <summary>Blocked row as shown in a list, carrying what "Allow this app" needs to act on.</summary>
    public sealed class BlockedListItem
    {
        public string AppName { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string When { get; init; } = string.Empty;
        public string? AppPath { get; init; }
        public string? PackageId { get; init; }

        public event EventHandler? AllowRequested;
        public void RequestAllow() => AllowRequested?.Invoke(this, EventArgs.Empty);
        public void AllowButton_Click(object sender, RoutedEventArgs e) => RequestAllow();
    }

    public sealed partial class ConnectionsPage : Page
    {
        private ConnectionsSnapshot _snapshot = new();
        private readonly ObservableCollection<ConnectionListItem> _connected = new();
        private readonly ObservableCollection<ConnectionListItem> _open = new();
        private readonly ObservableCollection<BlockedListItem> _blocked = new();
        private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private bool _busy;
        private bool _allowBusy;

        public ConnectionsPage()
        {
            InitializeComponent();
            // Keeps this page instance (and therefore each Expander's IsExpanded) alive across
            // navigating to Rules and back, instead of Frame recreating it - the closest match to
            // the spec's "collapsible, remembered state" without persisting to disk.
            NavigationCacheMode = NavigationCacheMode.Enabled;
            BlockedList.ItemsSource = _blocked;
            ConnectedList.ItemsSource = _connected;
            OpenList.ItemsSource = _open;
            Loaded += ConnectionsPage_Loaded;
            _autoRefreshTimer.Tick += async (_, _) => await RefreshAsync();
            Unloaded += (_, _) => _autoRefreshTimer.Stop();
        }

        private async void AutoRefreshToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (AutoRefreshToggle.IsOn)
            {
                // Refresh immediately rather than waiting up to a full interval for the first
                // tick, or the control appears inert right after being switched on. Re-check the
                // toggle after the await: if it was switched back off while the refresh was in
                // flight, starting the timer here would leave it running behind an "off" control.
                await RefreshAsync();
                if (AutoRefreshToggle.IsOn)
                    _autoRefreshTimer.Start();
            }
            else
            {
                _autoRefreshTimer.Stop();
            }
        }

        private async void ConnectionsPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Auto-refresh must survive navigation: this page is cached (NavigationCacheMode.Enabled),
            // so its instance lives on after Unloaded stops the timer. Without restarting here a user
            // who turned auto-refresh on, visited Rules, and came back would find the toggle still
            // flipped on but nothing ticking - a silent lie.
            if (AutoRefreshToggle.IsOn)
                _autoRefreshTimer.Start();

            await RefreshAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_busy)
                return;

            SetBusy(true);
            try
            {
                await App.Firewall.RefreshAsync();

                if (!App.Firewall.Connected)
                {
                    ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Status.NotConnected), App.Firewall.LastError ?? string.Empty);
                    _snapshot = new ConnectionsSnapshot();
                }
                else
                {
                    Notice.IsOpen = false;
                    _snapshot = await App.Firewall.GetConnectionsAsync();
                }
            }
            catch (Exception ex)
            {
                // The real client's log/NetStat gathering can throw (e.g. the service dies between
                // the refresh and the gather). That must not look like an all-clear: surface the
                // failure and keep the last good data on screen, where the error InfoBar keeps it
                // honest until the user refreshes again.
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
            }
            finally
            {
                // A throw must not leave the page busy forever, permanently disabling the refresh
                // button and cancelling auto-refresh - same rationale as ShellViewModel.RefreshAsync
                // (shell plan Task 4 ruling).
                SetBusy(false);
            }

            ApplyFilter();
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            var term = FilterBox.Text?.Trim() ?? string.Empty;
            var unknown = Loc.T(LocKeys.Common.Unknown);
            // Match what is displayed, not the raw value: an unnamed row shows as "Unknown" and
            // must be findable by typing exactly that.
            bool Matches(string name) => term.Length == 0
                || (string.IsNullOrEmpty(name) ? unknown : name).Contains(term, StringComparison.CurrentCultureIgnoreCase);

            _connected.Clear();
            foreach (var row in _snapshot.Connected.Where(r => Matches(r.AppName)))
                _connected.Add(ItemFrom(row));

            _open.Clear();
            foreach (var row in _snapshot.Open.Where(r => Matches(r.AppName)))
                _open.Add(ItemFrom(row));

            RebuildBlocked(_snapshot.Blocked.Where(r => Matches(r.AppName)));

            SetHeader(BlockedHeaderText, LocKeys.Connections.SectionBlocked, _blocked.Count);
            SetHeader(ConnectedHeaderText, LocKeys.Connections.SectionConnected, _connected.Count);
            SetHeader(OpenHeaderText, LocKeys.Connections.SectionOpen, _open.Count);

            // Empty states read reassuringly rather than like a failure - an empty Blocked list is
            // a *good* outcome on a firewall. But a filter hiding every row is not the same thing
            // as nothing happening, so it gets its own wording.
            var filtered = Loc.T(LocKeys.Connections.EmptyFiltered);
            SetEmptyState(ConnectedEmpty, _connected.Count, LocKeys.Connections.EmptyConnected, term, filtered);
            SetEmptyState(OpenEmpty, _open.Count, LocKeys.Connections.EmptyOpen, term, filtered);
            SetEmptyState(BlockedEmpty, _blocked.Count, LocKeys.Connections.EmptyBlocked, term, filtered);
        }

        private static void SetEmptyState(TextBlock target, int count, string emptyKey, string term, string filteredText)
        {
            if (count != 0)
            {
                target.Visibility = Visibility.Collapsed;
                return;
            }

            target.Text = term.Length == 0 ? Loc.T(emptyKey) : filteredText;
            target.Visibility = Visibility.Visible;
        }

        private static void SetHeader(TextBlock target, string titleKey, int count)
            => target.Text = Loc.T(titleKey) + " " + Loc.T(LocKeys.Connections.SectionCount, count);

        private void RebuildBlocked(IEnumerable<BlockedRow> rows)
        {
            _blocked.Clear();
            foreach (var row in rows)
            {
                var item = new BlockedListItem
                {
                    AppName = string.IsNullOrEmpty(row.AppName) ? Loc.T(LocKeys.Common.Unknown) : row.AppName,
                    // Security-log listen/bind events have no remote endpoint - render them
                    // without a dangling "→ :0".
                    Detail = string.IsNullOrEmpty(row.RemoteAddress) || row.RemotePort == 0
                        ? $"{row.Protocol} {row.Direction}"
                        : $"{row.Protocol} {row.Direction} → {row.RemoteAddress}:{row.RemotePort}",
                    When = row.Timestamp.ToString("HH:mm:ss"),
                    AppPath = row.AppPath,
                    PackageId = row.PackageId,
                };
                item.AllowRequested += async (_, _) => await AllowAsync(item);
                _blocked.Add(item);
            }
        }

        private async Task AllowAsync(BlockedListItem item)
        {
            // Serialize Allow actions. Until the result dialog is up the page is not blocked, so
            // without this guard a second click fires a concurrent commit - and when both finish,
            // the second ContentDialog.ShowAsync throws (only one dialog can be open per XamlRoot),
            // escaping through this async-void caller as an app crash.
            if (_allowBusy)
                return;

            _allowBusy = true;
            try
            {
                await AllowAsyncCore(item);
            }
            finally
            {
                _allowBusy = false;
            }
        }

        private async Task AllowAsyncCore(BlockedListItem item)
        {
            ExceptionSubject? subject = null;
            if (!string.IsNullOrEmpty(item.PackageId))
                subject = new AppContainerSubject(item.PackageId, item.AppName, string.Empty, string.Empty);
            else if (!string.IsNullOrEmpty(item.AppPath))
                subject = new ExecutableSubject(item.AppPath);

            // Never commit a rule for an app we cannot name (e.g. the process already exited) -
            // an empty ExecutableSubject would be a broken exception that matches nothing, which
            // is exactly the kind of silent no-op a firewall GUI must not produce.
            if (subject is null)
            {
                await ShowAllowResultAsync(Loc.T(LocKeys.Connections.AllowFailedTitle),
                    Loc.T(LocKeys.Connections.AllowFailedUnidentified));
                return;
            }

            MessageType resp;
            try
            {
                resp = await App.Firewall.AllowAsync(subject, new TcpUdpPolicy(true));
            }
            catch (Exception ex)
            {
                await ShowAllowResultAsync(Loc.T(LocKeys.Connections.AllowFailedTitle), ex.Message);
                return;
            }

            if (resp == MessageType.PUT_SETTINGS)
            {
                await ShowAllowResultAsync(Loc.T(LocKeys.Connections.AllowSuccessTitle),
                    Loc.T(LocKeys.Connections.AllowSuccessBody, item.AppName));
                await RefreshAsync();
            }
            else
            {
                var body = resp switch
                {
                    MessageType.RESPONSE_LOCKED => Loc.T(LocKeys.Connections.AllowFailedLockedDetail),
                    MessageType.RESPONSE_STALE_CHANGESET => Loc.T(LocKeys.Connections.AllowFailedStaleDetail),
                    _ => Loc.T(LocKeys.Connections.AllowFailedGenericDetail, resp),
                };
                await ShowAllowResultAsync(Loc.T(LocKeys.Connections.AllowFailedTitle), body);
            }
        }

        private async Task ShowAllowResultAsync(string title, string body)
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
                // Only one ContentDialog can be open per XamlRoot. If another dialog (e.g. the mode
                // chip's Learning confirmation) is up, fall back to the InfoBar rather than crash -
                // the outcome still has to reach the user.
                ShowNotice(InfoBarSeverity.Informational, title, body);
            }
        }

        private static ConnectionListItem ItemFrom(ConnectionRow row) => new()
        {
            AppName = string.IsNullOrEmpty(row.AppName) ? Loc.T(LocKeys.Common.Unknown) : row.AppName,
            Protocol = row.Protocol,
            LocalEndpoint = $"{row.LocalAddress}:{row.LocalPort}",
            RemoteEndpoint = row.RemotePort == 0 ? string.Empty : $"{row.RemoteAddress}:{row.RemotePort}",
            State = row.State,
        };

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
