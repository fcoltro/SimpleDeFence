using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SimpleDeFence;
using SimpleDeFence.Localization;
using SimpleDeFence.Utilities;
using SimpleDeFence.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    /// <summary>One connection or listening port inside a group's "Show all" flyout.</summary>
    public sealed class ConnectionLine
    {
        public string Endpoint { get; init; } = string.Empty;
        public string Trailing { get; init; } = string.Empty;
    }

    /// <summary>One app's connections (or listening ports), collapsed into a single row - the same
    /// treatment BlockedListItem gets, and for the same reason: a browser holds dozens of
    /// connections at once and listed one per row it buries everything else.</summary>
    public sealed class ConnectionListItem
    {
        public const int MaxListedLines = 50;

        public string AppName { get; init; } = string.Empty;
        /// <summary>Blank when a group mixes protocols - naming one of them would be a lie.</summary>
        public string Protocol { get; init; } = string.Empty;
        public string LocalEndpoint { get; init; } = string.Empty;
        public string RemoteEndpoint { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;

        public string FlyoutHeader { get; init; } = string.Empty;
        public IReadOnlyList<ConnectionLine> Lines { get; init; } = Array.Empty<ConnectionLine>();

        public bool HasMultiple => Lines.Count > 1;
        public Visibility ShowAllVisibility => HasMultiple ? Visibility.Visible : Visibility.Collapsed;

        public string TruncationNote { get; init; } = string.Empty;
        public Visibility TruncationVisibility => TruncationNote.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>See BlockedListItem.IsLocal. For a listening port this means bound to loopback
        /// rather than to every interface - 0.0.0.0 is the exposed case and is not local.</summary>
        public bool IsLocal { get; init; }
        public Visibility LocalVisibility => IsLocal ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoteVisibility => IsLocal ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A single blocked attempt, shown in a group's "Show all" flyout.</summary>
    public sealed class BlockedAttempt
    {
        public string Detail { get; init; } = string.Empty;
        public string When { get; init; } = string.Empty;
    }

    /// <summary>One app's blocked attempts, collapsed into a single row.
    ///
    /// A blocked app retries constantly and each retry used to be its own row - one process could
    /// fill the whole section with near-identical lines differing only in the last octet, burying
    /// every other app. The rows are grouped per app instead, and the individual attempts move
    /// behind "Show all".</summary>
    public sealed class BlockedListItem
    {
        /// <summary>Cap on what the flyout lists. A loop can rack up thousands of attempts, and
        /// nobody reads past the recent ones - the count in the summary still reports the true
        /// total.</summary>
        public const int MaxListedAttempts = 50;

        public string AppName { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string When { get; init; } = string.Empty;
        public string? AppPath { get; init; }
        public string? PackageId { get; init; }

        public IReadOnlyList<BlockedAttempt> Attempts { get; init; } = Array.Empty<BlockedAttempt>();

        /// <summary>Only a group with something behind it offers the flyout; a single attempt
        /// already says everything it has on the row.</summary>
        public bool HasMultipleAttempts => Attempts.Count > 1;
        public Visibility ShowAllVisibility => HasMultipleAttempts ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Set when the group holds more attempts than the flyout lists.</summary>
        public string TruncationNote { get; init; } = string.Empty;
        public Visibility TruncationVisibility => TruncationNote.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Protocol on its own, so this column lines up with the one the Connected and Open
        /// lists already had. It used to be glued to the front of Detail, which left the three
        /// sections disagreeing about where each column starts.</summary>
        public string Protocol { get; init; } = string.Empty;

        /// <summary>True when every address in the group stays on the local network. Drives the
        /// green marker: traffic that never left the network is a different thing from traffic
        /// headed for the internet, and worth telling apart at a glance.</summary>
        public bool IsLocal { get; init; }
        public Visibility LocalVisibility => IsLocal ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoteVisibility => IsLocal ? Visibility.Collapsed : Visibility.Visible;

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
        // Interval comes from ClientSettings, not a constant: this page polls - nothing pushes
        // connection changes to it - so how stale the list is allowed to get is the user's call.
        // Re-read in ConnectionsPage_Loaded as well as here, because this page is cached
        // (NavigationCacheMode.Enabled) and so its constructor does not run again after the user
        // changes the value on the Settings page and navigates back.
        private readonly DispatcherTimer _autoRefreshTimer = new();
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
            _autoRefreshTimer.Interval = ClientSettings.Load().ConnectionsAutoRefreshInterval;
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
            _autoRefreshTimer.Interval = ClientSettings.Load().ConnectionsAutoRefreshInterval;

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

            RebuildConnections(_connected, _snapshot.Connected.Where(r => Matches(r.AppName)), remote: true);
            RebuildConnections(_open, _snapshot.Open.Where(r => Matches(r.AppName)), remote: false);

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

        /// <summary>Security-log listen/bind events carry no remote endpoint, so they render
        /// without a dangling "→ :0".</summary>
        /// <summary>The part of a blocked attempt that is not the protocol; the protocol has a
        /// column of its own now.</summary>
        private static string DetailFor(BlockedRow row)
            => string.IsNullOrEmpty(row.RemoteAddress) || row.RemotePort == 0
                ? row.Direction ?? string.Empty
                : $"{row.Direction} → {row.RemoteAddress}:{row.RemotePort}";

        /// <summary>Protocol shared by every row in a group, or blank when they differ - naming one
        /// protocol for a mixed group would be wrong.</summary>
        private static string CommonProtocol(IEnumerable<string?> protocols)
        {
            var distinct = protocols.Select(x => x ?? string.Empty)
                                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return distinct.Count == 1 ? distinct[0] : string.Empty;
        }

        /// <summary>Groups rows by the app they came from, so one app retrying against a dozen
        /// addresses is one row rather than a dozen.
        ///
        /// Grouped on AppPath (or PackageId), never on the display name: two different binaries can
        /// present the same name, and merging those would put one app's attempts behind another's
        /// "Allow this app" button. Rows with neither - the ones Allow already refuses as
        /// unidentified - are keyed by name so they still collapse, but only among themselves.</summary>
        private void RebuildBlocked(IEnumerable<BlockedRow> rows)
        {
            _blocked.Clear();

            var groups = rows.GroupBy(r =>
                !string.IsNullOrEmpty(r.AppPath) ? "path:" + r.AppPath!.ToUpperInvariant()
                : !string.IsNullOrEmpty(r.PackageId) ? "pkg:" + r.PackageId!.ToUpperInvariant()
                : "name:" + (r.AppName ?? string.Empty).ToUpperInvariant());

            foreach (var group in groups)
            {
                // Newest first, so both the row's timestamp and the flyout lead with what just
                // happened rather than whatever the log happened to hand us first.
                var ordered = group.OrderByDescending(r => r.Timestamp).ToList();
                var newest = ordered[0];

                int addressCount = ordered
                    .Where(r => !string.IsNullOrEmpty(r.RemoteAddress))
                    .Select(r => r.RemoteAddress!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                // One attempt reads better as the attempt itself than as "1 attempt"; several
                // report how many, and how many places they were aimed at - the number that says
                // whether this is one stubborn connection or a scan.
                string detail = ordered.Count == 1
                    ? DetailFor(newest)
                    : addressCount > 1
                        ? Loc.T(LocKeys.Connections.BlockedAttempts, ordered.Count, addressCount)
                        : Loc.T(LocKeys.Connections.BlockedAttemptsOneAddress, ordered.Count);

                var listed = ordered
                    .Take(BlockedListItem.MaxListedAttempts)
                    .Select(r => new BlockedAttempt { Detail = DetailFor(r), When = r.Timestamp.ToString("HH:mm:ss") })
                    .ToList();

                var item = new BlockedListItem
                {
                    AppName = string.IsNullOrEmpty(newest.AppName) ? Loc.T(LocKeys.Common.Unknown) : newest.AppName,
                    Protocol = CommonProtocol(ordered.Select(r => r.Protocol)),
                    // Green only when the whole group stayed local. One attempt out to the internet
                    // among ten local ones is the one worth noticing, so any of those wins.
                    IsLocal = ordered.All(r => IpAddrMask.IsLocalNetwork(r.RemoteAddress)),
                    Detail = detail,
                    When = newest.Timestamp.ToString("HH:mm:ss"),
                    AppPath = newest.AppPath,
                    PackageId = newest.PackageId,
                    Attempts = listed,
                    TruncationNote = ordered.Count > listed.Count
                        ? Loc.T(LocKeys.Connections.ListTruncated, listed.Count)
                        : string.Empty,
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
                FlowDirection = App.UiFlowDirection,
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

        private static string EndpointOf(ConnectionRow row, bool remote)
            => remote
                ? (row.RemotePort == 0 ? string.Empty : $"{row.RemoteAddress}:{row.RemotePort}")
                : $"{row.LocalAddress}:{row.LocalPort}";

        /// <summary>Collapses rows to one per app, the same shape the Blocked list uses. A browser
        /// holds dozens of connections at once, and one row each buried every other app on the page.
        ///
        /// Keyed on AppPath (or PackageId) rather than the display name, for the reason the Blocked
        /// list documents: two binaries can present the same name and must not be merged.</summary>
        private static void RebuildConnections(
            ObservableCollection<ConnectionListItem> target, IEnumerable<ConnectionRow> rows, bool remote)
        {
            target.Clear();

            // ConnectionRow carries no PackageId - only BlockedRow does - so the fallback here is
            // the display name rather than the package identity.
            var groups = rows.GroupBy(r =>
                !string.IsNullOrEmpty(r.AppPath) ? "path:" + r.AppPath!.ToUpperInvariant()
                : "name:" + (r.AppName ?? string.Empty).ToUpperInvariant());

            foreach (var group in groups)
            {
                var ordered = group.OrderBy(r => EndpointOf(r, remote), StringComparer.OrdinalIgnoreCase).ToList();
                var first = ordered[0];

                // Naming one protocol for a group that mixes them would be wrong, so a mixed group
                // names none.
                string protocol = CommonProtocol(ordered.Select(r => r.Protocol));

                bool single = ordered.Count == 1;
                string summary = single
                    ? EndpointOf(first, remote)
                    : Loc.T(remote ? LocKeys.Connections.ConnectedSummary : LocKeys.Connections.OpenSummary, ordered.Count);

                var listed = ordered
                    .Take(ConnectionListItem.MaxListedLines)
                    .Select(r => new ConnectionLine
                    {
                        Endpoint = EndpointOf(r, remote),
                        // The per-line protocol still shows, because the row above may have had to
                        // drop it for a mixed group.
                        Trailing = remote ? $"{r.Protocol} {r.State}".Trim() : (r.Protocol ?? string.Empty),
                    })
                    .ToList();

                target.Add(new ConnectionListItem
                {
                    AppName = string.IsNullOrEmpty(first.AppName) ? Loc.T(LocKeys.Common.Unknown) : first.AppName,
                    Protocol = protocol,
                    // For Connected this asks where the traffic is going; for Open, whether the
                    // socket is confined to this machine or listening on every interface.
                    IsLocal = ordered.All(r => IpAddrMask.IsLocalNetwork(
                        remote ? r.RemoteAddress : r.LocalAddress)),
                    LocalEndpoint = remote ? string.Empty : summary,
                    RemoteEndpoint = remote ? summary : string.Empty,
                    State = single && remote ? first.State : string.Empty,
                    FlyoutHeader = Loc.T(remote ? LocKeys.Connections.ConnectedHeader : LocKeys.Connections.OpenHeader),
                    Lines = listed,
                    TruncationNote = ordered.Count > listed.Count
                        ? Loc.T(LocKeys.Connections.ListTruncated, listed.Count)
                        : string.Empty,
                });
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
