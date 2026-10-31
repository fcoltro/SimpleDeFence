using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SimpleDeFence;
using SimpleDeFence.Localization;
using SimpleDeFence.Utilities;
using SimpleDeFence.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    /// <summary>One connection or listening port inside a group's "Show all" dialog.</summary>
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

        /// <summary>Full path of the binary behind this group, already used as the grouping key in
        /// RebuildConnections but not previously carried onto the item.</summary>
        public string? AppPath { get; init; }

        /// <summary>Hover text for the row: the app's full path. The name column is capped at 260px
        /// and ellipsed, so on any real install it shows a bare file name and nothing about which
        /// copy of it this is - two binaries can present the same name, which is exactly why the
        /// grouping is keyed on the path rather than the name. Null rather than empty when there is
        /// no path to show: ToolTipService treats null as "no tooltip", where an empty string still
        /// pops an empty box.</summary>
        public string? TooltipText => string.IsNullOrEmpty(AppPath) ? null : AppPath;

        public string FlyoutHeader { get; init; } = string.Empty;
        public IReadOnlyList<ConnectionLine> Lines { get; init; } = Array.Empty<ConnectionLine>();

        public bool HasMultiple => Lines.Count > 1;
        public Visibility ShowAllVisibility => HasMultiple ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The distinct remote addresses in the group, for the details dialog's lookup.
        /// Empty for listening ports, which have no remote end.</summary>
        public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();

        public event EventHandler? ShowAllRequested;
        public void ShowAllButton_Click(object sender, RoutedEventArgs e) => ShowAllRequested?.Invoke(this, EventArgs.Empty);

        public string TruncationNote { get; init; } = string.Empty;
        public Visibility TruncationVisibility => TruncationNote.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>See BlockedListItem.IsLocal. For a listening port this means bound to loopback
        /// rather than to every interface - 0.0.0.0 is the exposed case and is not local.</summary>
        public bool IsLocal { get; init; }
        public Visibility LocalVisibility => IsLocal ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoteVisibility => IsLocal ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A single blocked attempt, shown in a group's "Show all" dialog.</summary>
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
        /// <summary>Cap on what the dialog lists. A loop can rack up thousands of attempts, and
        /// nobody reads past the recent ones - the count in the summary still reports the true
        /// total.</summary>
        public const int MaxListedAttempts = 50;

        public string AppName { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string When { get; init; } = string.Empty;
        public string? AppPath { get; init; }
        public string? PackageId { get; init; }

        /// <summary>Muted from the right-click menu; only listed while "Show muted" is on.</summary>
        public bool IsMuted { get; init; }

        /// <summary>The key this row's attempts were grouped under, carried rather than recomputed.
        /// Recomputing it from this object would not be safe: AppName here is substituted with
        /// "Unknown" when the log had no name, so a key derived from it could differ from the one
        /// the group was actually built with.</summary>
        public string SubjectKey { get; init; } = string.Empty;

        /// <summary>Hover text for the row - see ConnectionListItem.TooltipText for why the row
        /// needs one at all. Falls back to the package identity for a store app, which has no
        /// executable path of its own and would otherwise be the one kind of row that could never
        /// tell you what it actually is.</summary>
        public string? TooltipText
            => !string.IsNullOrEmpty(AppPath) ? AppPath
             : !string.IsNullOrEmpty(PackageId) ? PackageId
             : null;

        public IReadOnlyList<BlockedAttempt> Attempts { get; init; } = Array.Empty<BlockedAttempt>();

        /// <summary>Only a group with something behind it offers "Show all" as a button; a single attempt
        /// already says everything it has on the row.</summary>
        public bool HasMultipleAttempts => Attempts.Count > 1;
        public Visibility ShowAllVisibility => HasMultipleAttempts ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The distinct addresses the group was aimed at, most recent first.</summary>
        public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();

        public event EventHandler? ShowAllRequested;
        public void ShowAllButton_Click(object sender, RoutedEventArgs e) => ShowAllRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>Set when the group holds more attempts than the dialog lists.</summary>
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
            FilterRequested += ApplyRequestedFilter;
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

        // Set by a click on an activity notification, so the page opens on the app it was about.
        // Static because the click can come before the page exists: Loaded picks it up then, and
        // the event covers the cached page that is already there.
        private static string? s_requestedFilter;
        private static event Action? FilterRequested;

        internal static void RequestFilter(string text)
        {
            s_requestedFilter = text;
            FilterRequested?.Invoke();
        }

        private void ApplyRequestedFilter()
        {
            if (s_requestedFilter is not string text)
                return;

            s_requestedFilter = null;
            FilterBox.Text = text;   // TextChanged applies it
        }

        private async void ConnectionsPage_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyRequestedFilter();

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

                // Gathered either way. Only the Blocked section is the service's to answer for -
                // it is built from the firewall's event log, which arrives over the pipe. Connected
                // and Open come from GetExtendedTcpTable/GetExtendedUdpTable, which are local calls
                // that work whether or not the service can be reached.
                //
                // This used to replace the snapshot with an empty one whenever the service was
                // unreachable, which threw away two lists that had never needed it and left the
                // whole screen blank behind an error - the reading being "SimpleDeFence can see
                // nothing", when what was true is "SimpleDeFence cannot tell you what it blocked".
                // The notice still says the service is unreachable, so the empty Blocked list is
                // not mistaken for an all-clear.
                if (!App.Firewall.Connected)
                    ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Status.NotConnected), App.Firewall.LastError ?? string.Empty);
                else
                    Notice.IsOpen = false;

                _snapshot = await App.Firewall.GetConnectionsAsync();
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

            // Inside a guard of its own. This used to run after the try/catch, where a throw from
            // the rebuild escaped into the async void handlers that call this and was reported
            // nowhere - and RebuildBlocked clears its collection before it repopulates it, so the
            // visible result was a section that emptied itself with no error to explain it.
            try
            {
                ApplyFilter();
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
            }
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

            // "Nothing blocked" is the reassuring reading, and it must only ever be said about a
            // log we actually read. When the log did not arrive, the section says so instead -
            // the distinction the Blocked list exists to make, on the screen whose whole job is
            // letting the user release what was blocked.
            SetEmptyState(BlockedEmpty, _blocked.Count,
                _snapshot.BlockedUnavailable ? LocKeys.Connections.BlockedUnavailable : LocKeys.Connections.EmptyBlocked,
                term, filtered);
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
        /// <summary>
        /// Identifies an app across the Blocked list and the allow-suppression map below. Both
        /// callers use this one method so a row can never be grouped under one key and suppressed
        /// under another.
        /// </summary>
        private static string SubjectKeyOf(string? appPath, string? packageId, string? appName)
            => !string.IsNullOrEmpty(appPath) ? "path:" + appPath!.ToUpperInvariant()
             : !string.IsNullOrEmpty(packageId) ? "pkg:" + packageId!.ToUpperInvariant()
             : "name:" + (appName ?? string.Empty).ToUpperInvariant();

        /// <summary>
        /// When each app was allowed from this page, and therefore how far back its blocked history
        /// should stop being shown.
        ///
        /// The Blocked list is a rolling five-minute window over the firewall log, so allowing an
        /// app did nothing to the attempts it had already made: the row sat there afterwards,
        /// looking exactly like it had before, for as long as it took those entries to age out. For
        /// an app that had been retrying in a loop - which is the usual reason it is in this list at
        /// all - that reads as the Allow having silently failed, while Rules shows the new rule
        /// sitting there perfectly.
        ///
        /// Only entries older than the moment the rule was written are hidden. A block recorded
        /// *after* it is real and stays: an app allowed on the local network only will keep being
        /// blocked on its way to the internet, and that is exactly the thing the user needs to see
        /// rather than something to tidy away.
        /// </summary>
        private readonly Dictionary<string, DateTime> _allowedAt = new();

        /// <summary>Whether muted apps are shown in the Blocked list for now, so they can be
        /// unmuted. Not remembered: the list goes back to hiding them next time.</summary>
        private bool _showMuted;

        private void RebuildBlocked(IEnumerable<BlockedRow> rows)
        {
            _blocked.Clear();
            var settings = ClientSettings.Load();
            int mutedCount = 0;

            var groups = rows
                .Where(r => !SuppressedByAllow(r))
                .GroupBy(r => SubjectKeyOf(r.AppPath, r.PackageId, r.AppName));

            foreach (var group in groups)
            {
                var first = group.First();
                bool muted = settings.IsMuted(first.AppPath, first.PackageId);
                if (muted)
                {
                    ++mutedCount;
                    if (!_showMuted)
                        continue;
                }

                // Newest first, so both the row's timestamp and the dialog lead with what just
                // happened rather than whatever the log happened to hand us first.
                var ordered = group.OrderByDescending(r => r.Timestamp).ToList();
                var newest = ordered[0];

                var addresses = ordered
                    .Where(r => !string.IsNullOrEmpty(r.RemoteAddress))
                    .Select(r => r.RemoteAddress!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                int addressCount = addresses.Count;

                // Each row can stand for many tries, because the service merges repeats of the same
                // attempt; the summary counts tries, not rows.
                int attemptCount = ordered.Sum(r => Math.Max(1, r.Count));

                // One attempt reads better as the attempt itself than as "1 attempt"; several
                // report how many, and how many places they were aimed at - the number that says
                // whether this is one stubborn connection or a scan.
                string detail = attemptCount == 1
                    ? DetailFor(newest)
                    : addressCount > 1
                        ? Loc.T(LocKeys.Connections.BlockedAttempts, attemptCount, addressCount)
                        : Loc.T(LocKeys.Connections.BlockedAttemptsOneAddress, attemptCount);

                var listed = ordered
                    .Take(BlockedListItem.MaxListedAttempts)
                    .Select(r => new BlockedAttempt
                    {
                        Detail = r.Count > 1 ? $"{DetailFor(r)}  ×{r.Count}" : DetailFor(r),
                        When = r.Timestamp.ToString("HH:mm:ss"),
                    })
                    .ToList();

                var item = new BlockedListItem
                {
                    SubjectKey = group.Key,
                    AppName = string.IsNullOrEmpty(newest.AppName) ? Loc.T(LocKeys.Common.Unknown) : newest.AppName,
                    Protocol = CommonProtocol(ordered.Select(r => r.Protocol)),
                    // Green only when the whole group stayed local. One attempt out to the internet
                    // among ten local ones is the one worth noticing, so any of those wins.
                    IsLocal = ordered.All(r => IpAddrMask.IsLocalNetwork(r.RemoteAddress)),
                    Detail = detail,
                    When = newest.Timestamp.ToString("HH:mm:ss"),
                    AppPath = newest.AppPath,
                    PackageId = newest.PackageId,
                    IsMuted = muted,
                    Attempts = listed,
                    Addresses = addresses,
                    TruncationNote = ordered.Count > listed.Count
                        ? Loc.T(LocKeys.Connections.ListTruncated, listed.Count)
                        : string.Empty,
                };
                item.AllowRequested += async (_, _) => await AllowAsync(item);
                item.ShowAllRequested += async (_, _) => await ShowDetailsAsync(DetailsOf(item), lookUpNow: false);
                _blocked.Add(item);
            }

            if (mutedCount == 0)
                _showMuted = false;
            MutedToggle.Visibility = mutedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            MutedToggle.Content = _showMuted
                ? Loc.T(LocKeys.Connections.MutedHide)
                : Loc.T(LocKeys.Connections.MutedShow, mutedCount);
        }

        private void MutedToggle_Click(object sender, RoutedEventArgs e)
        {
            _showMuted = !_showMuted;
            ApplyFilter();
        }

        /// <summary>"Mute this app": still blocked, but out of the Blocked list and notifications.
        /// For a program that retries for ever and has nothing left to switch off.</summary>
        private void SetMuted(BlockedListItem item, bool mute)
        {
            var settings = ClientSettings.Load();
            if (!settings.SetMuted(item.AppPath, item.PackageId, mute))
                return;
            settings.Save();
            App.NotifyActivitySettingsChanged(settings);
            ApplyFilter();
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

            // Ask before writing. This button used to commit TcpUdpPolicy(unrestricted: true) the
            // instant it was clicked - every TCP and UDP port, inbound as well as outbound, with no
            // local-network restriction - and only then put up a dialog, which named the app but was
            // reporting a rule that already existed. The widest policy the app can grant should not
            // be the one a single click applies silently, and the Rules page already offers exactly
            // these choices for the same rule after the fact; this just moves them to the point of
            // decision.
            //
            // A program with a signature Windows verifies can also be allowed by its publisher,
            // which is read first, off the UI thread: verifying hashes the whole file.
            string? publisher = null;
            if (subject is ExecutableSubject exe)
            {
                try { publisher = await Task.Run(() => exe.CertValid ? exe.CertSubject : null); }
                catch { publisher = null; }
            }

            if (await AskAllowPolicyAsync(item, publisher) is not { } choice)
                return;
            var (policy, timer, trustPublisher) = choice;

            MessageType resp;
            try
            {
                resp = trustPublisher is null
                    ? await App.Firewall.AllowAsync(subject, policy, timer)
                    : await App.Firewall.CommitConfigChangesAsync(config =>
                    {
                        var trusted = config.ActiveProfile.TrustedPublishers;
                        if (!trusted.Contains(trustPublisher!, StringComparer.OrdinalIgnoreCase))
                            trusted.Add(trustPublisher!);
                    });
            }
            catch (Exception ex)
            {
                await ShowAllowResultAsync(Loc.T(LocKeys.Connections.AllowFailedTitle), ex.Message);
                return;
            }

            if (resp == MessageType.PUT_SETTINGS)
            {
                // "{0} can now connect" is only true of the two allowing policies. Picking Blocked
                // in the dialog above also writes a rule and also succeeds, and telling the user it
                // had let the app through would be the one report a firewall must never get wrong -
                // so that case borrows the Rules page's neutral wording instead.
                var body = trustPublisher is not null
                    ? Loc.T(LocKeys.Connections.AllowPublisherSuccessBody, PublisherTrust.DisplayName(trustPublisher))
                    : policy is HardBlockPolicy
                    ? Loc.T(LocKeys.Rules.DetailApplySuccess)
                    : Loc.T(LocKeys.Connections.AllowSuccessBody, item.AppName);

                // Before the refresh, so the rebuild it triggers already hides the attempts this
                // rule was written in response to. Recorded whichever policy was chosen: picking
                // Blocked is also a decision about this app, and leaving its backlog on the list
                // afterwards would be the same "did that do anything?" as allowing it.
                MarkAllowed(item);

                await ShowAllowResultAsync(Loc.T(LocKeys.Connections.AllowSuccessTitle), body);
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

        /// <summary>
        /// Puts the policy choice in front of the user before anything is committed, and returns
        /// what they picked - or null if they cancelled, in which case nothing is written.
        ///
        /// The three options are the ones that need no further input, and each builds exactly the
        /// policy the Rules detail pane's radio button of the same name builds (see
        /// RulesPage.BuildPolicyFromEditor), so the two screens cannot come to mean different things
        /// by the same word. The port-list policy is deliberately not offered here: it needs four
        /// text fields to be worth anything, and a rule that specific belongs on the Rules page
        /// rather than in a dialog raised from a blocked-connection row.
        ///
        /// Unrestricted is preselected, so clicking straight through still allows the app. Note that
        /// it is not identical to what the button used to commit without asking - that was
        /// TcpUdpPolicy(unrestricted: true), every TCP and UDP port, where UnrestrictedPolicy also
        /// covers the other IP protocols. The wider grant is the deliberate one to preselect, now
        /// that it is named on screen and confirmed rather than applied behind the click.
        /// </summary>
        /// <remarks>publisher is the program's verified certificate subject, or null. With one, a
        /// fourth option trusts that publisher instead of writing a rule for this path, and the
        /// result carries it as TrustPublisher; Policy and Timer are then unused.</remarks>
        private async Task<(ExceptionPolicy Policy, AppExceptionTimer Timer, string? TrustPublisher)?> AskAllowPolicyAsync(BlockedListItem item, string? publisher)
        {
            var unrestricted = new RadioButton
            {
                Content = Loc.T(LocKeys.Policy.Unrestricted),
                IsChecked = true,
                GroupName = "ConnectionsAllowPolicy",
            };
            var unrestrictedLan = new RadioButton
            {
                Content = Loc.T(LocKeys.Policy.UnrestrictedLan),
                GroupName = "ConnectionsAllowPolicy",
            };
            var blocked = new RadioButton
            {
                Content = Loc.T(LocKeys.Policy.Blocked),
                GroupName = "ConnectionsAllowPolicy",
            };

            var panel = new StackPanel { Spacing = 4, MinWidth = 320 };
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T(LocKeys.Connections.AllowDialogBody, item.AppName),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            // The path, verbatim and selectable, for the same reason the row now carries it as a
            // tooltip: the name alone does not say which copy of a binary this is, and this dialog
            // is the last point at which that can still be checked before a rule is written.
            if (item.TooltipText is { Length: > 0 } identity)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = identity,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Opacity = 0.8,
                    Margin = new Thickness(0, 0, 0, 12),
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                });
            }

            panel.Children.Add(unrestricted);
            panel.Children.Add(unrestrictedLan);
            panel.Children.Add(blocked);

            RadioButton? byPublisher = null;
            if (publisher is not null)
            {
                byPublisher = new RadioButton
                {
                    Content = Loc.T(LocKeys.Connections.AllowByPublisher, PublisherTrust.DisplayName(publisher)),
                    GroupName = "ConnectionsAllowPolicy",
                };
                ToolTipService.SetToolTip(byPublisher, publisher);
                panel.Children.Add(byPublisher);
            }

            // How long the rule lasts. Permanent is preselected, which is what this dialog wrote
            // before it asked; the shorter ones are for the installer or one-off tool that needs
            // the network once, and should not keep it.
            var duration = new ComboBox
            {
                Header = Loc.T(LocKeys.Duration.Label),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 12, 0, 0),
            };
            foreach (var timer in AppExceptionTimers.Offered)
                duration.Items.Add(AppExceptionTimers.Label(timer));
            duration.SelectedIndex = 0;
            panel.Children.Add(duration);

            // A publisher is trusted until it is removed on the Rules page; there is no duration
            // to choose for it.
            if (byPublisher is not null)
            {
                byPublisher.Checked += (_, _) => duration.IsEnabled = false;
                byPublisher.Unchecked += (_, _) => duration.IsEnabled = true;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                FlowDirection = App.UiFlowDirection,
                RequestedTheme = App.UiElementTheme,
                Title = Loc.T(LocKeys.Connections.AllowDialogTitle),
                Content = panel,
                PrimaryButtonText = Loc.T(LocKeys.Rules.DetailApply),
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
                DefaultButton = ContentDialogButton.Primary,
            };

            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                // Only one ContentDialog can be open per XamlRoot. Unlike the result dialog, there
                // is no falling back to the InfoBar here - this dialog *is* the consent, so if it
                // cannot be shown the rule must not be written.
                ShowNotice(InfoBarSeverity.Informational,
                    Loc.T(LocKeys.Connections.AllowDialogTitle),
                    Loc.T(LocKeys.Common.AnotherDialogOpenDetail));
                return null;
            }

            if (result != ContentDialogResult.Primary)
                return null;

            var chosenTimer = duration.SelectedIndex >= 0 && duration.SelectedIndex < AppExceptionTimers.Offered.Count
                ? AppExceptionTimers.Offered[duration.SelectedIndex]
                : AppExceptionTimer.Permanent;

            if (byPublisher?.IsChecked == true)
                return (new UnrestrictedPolicy(), AppExceptionTimer.Permanent, publisher);

            if (blocked.IsChecked == true)
                return (new HardBlockPolicy(), chosenTimer, null);

            return (new UnrestrictedPolicy { LocalNetworkOnly = unrestrictedLan.IsChecked == true }, chosenTimer, null);
        }

        private async Task ShowAllowResultAsync(string title, string body)
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
        private void RebuildConnections(
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

                var item = new ConnectionListItem
                {
                    AppName = string.IsNullOrEmpty(first.AppName) ? Loc.T(LocKeys.Common.Unknown) : first.AppName,
                    // Every row in the group shares this by construction - it is the grouping key.
                    AppPath = first.AppPath,
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
                    Addresses = remote
                        ? ordered.Where(r => r.RemotePort != 0 && !string.IsNullOrEmpty(r.RemoteAddress))
                                 .Select(r => r.RemoteAddress)
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList()
                        : Array.Empty<string>(),
                    TruncationNote = ordered.Count > listed.Count
                        ? Loc.T(LocKeys.Connections.ListTruncated, listed.Count)
                        : string.Empty,
                };
                item.ShowAllRequested += async (_, _) => await ShowDetailsAsync(DetailsOf(item), lookUpNow: false);
                target.Add(item);
            }
        }

        /// <summary>True for a blocked attempt that happened before this app was allowed, and so
        /// describes a state that no longer exists. See _allowedAt.</summary>
        private bool SuppressedByAllow(BlockedRow row)
            => _allowedAt.TryGetValue(SubjectKeyOf(row.AppPath, row.PackageId, row.AppName), out var allowedAt)
               && row.Timestamp <= allowedAt;

        /// <summary>Records that an app was just allowed, so its earlier attempts stop being listed.
        ///
        /// Keys are dropped once they are older than the window the Blocked list covers, with a
        /// margin: past that point every entry they could suppress has aged out of the log anyway,
        /// and keeping them would slowly turn a page that is open for days into a leak.</summary>
        private void MarkAllowed(BlockedListItem item)
        {
            var now = DateTime.Now;
            _allowedAt[item.SubjectKey] = now;

            var stale = _allowedAt
                .Where(kv => now - kv.Value > BlockedHistoryWindow + TimeSpan.FromMinutes(1))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in stale)
                _allowedAt.Remove(key);
        }

        /// <summary>How far back the Blocked list reaches, used here only to decide when a
        /// suppression key can be forgotten. Read from the same setting the gather uses rather
        /// than restated as a constant: the two were separate literals that a comment asked
        /// readers to keep in step, which is the kind of pairing that silently drifts.</summary>
        private static TimeSpan BlockedHistoryWindow => ClientSettings.Load().BlockedHistoryWindow;

        /// <summary>Everything the details dialog shows, gathered from either kind of row so the
        /// dialog is written once. Blocked is set only for a Blocked row, which is the one kind the
        /// dialog can offer Allow for.</summary>
        private sealed record RowDetails(
            string AppName,
            string? Identity,
            string? AppPath,
            string Header,
            IReadOnlyList<(string Text, string When)> Lines,
            string TruncationNote,
            IReadOnlyList<string> Addresses,
            BlockedListItem? Blocked);

        private static RowDetails DetailsOf(BlockedListItem item)
            => new(item.AppName, item.TooltipText, item.AppPath, Loc.T(LocKeys.Connections.BlockedAttemptsHeader),
                   item.Attempts.Select(a => (a.Detail, a.When)).ToList(), item.TruncationNote, item.Addresses, item);

        private static RowDetails DetailsOf(ConnectionListItem item)
            => new(item.AppName, item.TooltipText, item.AppPath, item.FlyoutHeader,
                   item.Lines.Select(l => (l.Endpoint, l.Trailing)).ToList(), item.TruncationNote, item.Addresses, null);

        private bool _detailsBusy;

        /// <summary>
        /// "Show all", reached from the row's button or its right-click menu: every line behind the
        /// row, and below them the addresses involved with a button that looks each one up.
        ///
        /// This used to be a flyout. It is a dialog now because the lookup needs room to say what
        /// it found under each address, and because a flyout closes on the first click outside it
        /// - which, while a lookup was still filling in, threw the answers away.
        ///
        /// Nothing is looked up until the button is clicked (or the menu's "Look up addresses",
        /// which is the same click made in advance): see IpLookup for why that matters here.
        /// </summary>
        private async Task ShowDetailsAsync(RowDetails details, bool lookUpNow)
        {
            if (_detailsBusy)
                return;

            _detailsBusy = true;
            var cancel = new CancellationTokenSource();
            ContentDialogResult result;
            try
            {
                var caption = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextBlockStyle"];
                var strong = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"];
                var panel = new StackPanel { Spacing = 8, MinWidth = 380, MaxWidth = 540 };

                if (details.Identity is { Length: > 0 } identity)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = identity,
                        Style = caption,
                        Opacity = 0.8,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    });
                }

                panel.Children.Add(new TextBlock { Text = details.Header, Style = caption, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) });

                var lines = new StackPanel();
                foreach (var (text, when) in details.Lines)
                {
                    var line = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 3, 0, 3) };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    line.Children.Add(new TextBlock { Text = text, Style = caption, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, IsTextSelectionEnabled = true });
                    var trailing = new TextBlock { Text = when, Style = caption, Opacity = 0.6, TextWrapping = TextWrapping.NoWrap };
                    Grid.SetColumn(trailing, 1);
                    line.Children.Add(trailing);
                    lines.Children.Add(line);
                }
                panel.Children.Add(new ScrollViewer { Content = lines, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

                if (details.TruncationNote.Length > 0)
                    panel.Children.Add(new TextBlock { Text = details.TruncationNote, Style = caption, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

                Func<Task>? lookUp = null;
                if (details.Addresses.Count > 0)
                {
                    panel.Children.Add(new TextBlock { Text = Loc.T(LocKeys.Connections.DetailsAddresses), Style = strong, Margin = new Thickness(0, 8, 0, 0) });

                    var button = new Button { Content = Loc.T(LocKeys.Connections.MenuLookUp) };
                    var ring = new ProgressRing { IsActive = false, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
                    var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    buttonRow.Children.Add(button);
                    buttonRow.Children.Add(ring);
                    panel.Children.Add(buttonRow);
                    panel.Children.Add(new TextBlock { Text = Loc.T(LocKeys.Connections.DetailsLookUpNote), Style = caption, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

                    var shown = details.Addresses.Take(IpLookup.MaxPerRequest).ToList();
                    var rows = new List<(string Address, TextBlock Info)>();
                    var addressList = new StackPanel { Spacing = 6 };
                    foreach (var address in shown)
                    {
                        var entry = new StackPanel();
                        entry.Children.Add(new TextBlock { Text = address, Style = caption, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsTextSelectionEnabled = true });
                        var info = new TextBlock { Style = caption, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
                        entry.Children.Add(info);
                        addressList.Children.Add(entry);
                        rows.Add((address, info));
                    }
                    panel.Children.Add(new ScrollViewer { Content = addressList, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

                    if (details.Addresses.Count > shown.Count)
                        panel.Children.Add(new TextBlock { Text = Loc.T(LocKeys.Connections.DetailsLookUpLimit, shown.Count), Style = caption, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

                    lookUp = async () =>
                    {
                        button.IsEnabled = false;
                        ring.IsActive = true;
                        // A few at a time: twenty requests at once to a free service is the kind
                        // of burst that gets an address rate-limited, and the user is reading the
                        // first answers while the rest arrive anyway.
                        using var gate = new SemaphoreSlim(4);
                        await Task.WhenAll(rows.Select(async row =>
                        {
                            await gate.WaitAsync();
                            try
                            {
                                IpDetails? found = null;
                                try { found = await IpLookup.LookUpAsync(row.Address, cancel.Token); }
                                catch { found = null; }
                                row.Info.Text = Describe(found);
                                row.Info.Visibility = Visibility.Visible;
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }));
                        ring.IsActive = false;
                        button.IsEnabled = true;
                    };
                    button.Click += async (_, _) => await lookUp();
                }

                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    FlowDirection = App.UiFlowDirection,
                    RequestedTheme = App.UiElementTheme,
                    Title = details.AppName,
                    Content = panel,
                    CloseButtonText = Loc.T(LocKeys.Common.Ok),
                    DefaultButton = ContentDialogButton.Close,
                };
                if (details.Blocked is not null)
                    dialog.PrimaryButtonText = Loc.T(LocKeys.Connections.Allow);
                if (lookUpNow && lookUp is not null)
                    dialog.Opened += async (_, _) => await lookUp();

                try
                {
                    result = await dialog.ShowAsync();
                }
                catch (InvalidOperationException)
                {
                    ShowNotice(InfoBarSeverity.Informational, details.AppName, Loc.T(LocKeys.Common.AnotherDialogOpenDetail));
                    return;
                }
                finally
                {
                    // Answers still on their way have nowhere to go once the dialog is gone.
                    cancel.Cancel();
                }
            }
            finally
            {
                _detailsBusy = false;
            }

            // After the dialog has closed: Allow raises a dialog of its own, and only one can be up.
            if (result == ContentDialogResult.Primary && details.Blocked is not null)
                await AllowAsync(details.Blocked);
        }

        /// <summary>The lines shown under an address once it has been looked up.</summary>
        private static string Describe(IpDetails? found)
        {
            if (found is null)
                return Loc.T(LocKeys.Connections.DetailsNotFound);
            if (found.IsLocal)
                return Loc.T(LocKeys.Connections.DetailsLocal);
            if (!found.FoundAnything)
                return Loc.T(LocKeys.Connections.DetailsNotFound);

            var parts = new List<string>();
            if (found.HostName is not null)
                parts.Add(Loc.T(LocKeys.Connections.DetailsHost, found.HostName));
            if (found.Organization is not null)
                parts.Add(Loc.T(LocKeys.Connections.DetailsOwner, found.Organization));
            // The network name is usually the owner in capitals; only worth a line when it says
            // something the owner did not.
            if (found.Network is not null
                && (found.Organization is null || !found.Organization.Contains(found.Network, StringComparison.OrdinalIgnoreCase)))
                parts.Add(Loc.T(LocKeys.Connections.DetailsNetwork, found.Network));
            if (found.Country is not null)
                parts.Add(Loc.T(LocKeys.Connections.DetailsCountry, found.Country));
            return string.Join(Environment.NewLine, parts);
        }

        /// <summary>
        /// Right click (or the Menu key, or Shift+F10) on any row of the three lists. The menu
        /// carries what the row already offers - details, Allow - plus the small things that used
        /// to need a trip elsewhere: copying an address or a path, opening the program's folder,
        /// and narrowing the page to this one app.
        ///
        /// The row is found from the container rather than from DataContext on whatever element
        /// was hit, because a keyboard request arrives from the focused ListViewItem itself and a
        /// pointer one from somewhere inside its template; walking up to the container covers both.
        /// </summary>
        private void Row_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            if (sender is not ListView list)
                return;

            ListViewItem? container = null;
            for (var node = args.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, list); node = VisualTreeHelper.GetParent(node))
            {
                if (node is ListViewItem found)
                {
                    container = found;
                    break;
                }
            }

            var details = container is null ? null : list.ItemFromContainer(container) switch
            {
                BlockedListItem blocked => DetailsOf(blocked),
                ConnectionListItem connection => DetailsOf(connection),
                _ => null,
            };
            if (container is null || details is null)
                return;

            var menu = BuildRowMenu(details);
            App.ApplyShellStyling(menu);
            if (args.TryGetPosition(container, out var point))
                menu.ShowAt(container, new FlyoutShowOptions { Position = point });
            else
                menu.ShowAt(container);
            args.Handled = true;
        }

        private MenuFlyout BuildRowMenu(RowDetails details)
        {
            var menu = new MenuFlyout();
            void Add(string key, string glyph, Action action)
            {
                var item = new MenuFlyoutItem { Text = Loc.T(key), Icon = new FontIcon { Glyph = glyph } };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }

            Add(LocKeys.Connections.MenuDetails, "", async () => await ShowDetailsAsync(details, lookUpNow: false));
            if (details.Addresses.Any(a => !IpAddrMask.IsLocalNetwork(a)))
                Add(LocKeys.Connections.MenuLookUp, "", async () => await ShowDetailsAsync(details, lookUpNow: true));
            if (details.Blocked is { } blocked)
                Add(LocKeys.Connections.Allow, "", async () => await AllowAsync(blocked));
            if (details.AppPath is { Length: > 0 } stopPath)
                Add(LocKeys.Connections.StopMenu, "", async () => await StopTryingAsync(details.AppName, stopPath));
            if (details.Blocked is { } mutable && ClientSettings.MuteKey(mutable.AppPath, mutable.PackageId) is not null)
            {
                if (mutable.IsMuted)
                    Add(LocKeys.Connections.MenuUnmute, "\uE767", () => SetMuted(mutable, false));
                else
                    Add(LocKeys.Connections.MenuMute, "\uE74F", () => SetMuted(mutable, true));
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            Add(LocKeys.Connections.MenuFilterApp, "", () => FilterBox.Text = details.AppName);
            if (details.Addresses.Count > 0)
                Add(LocKeys.Connections.MenuCopyAddresses, "", () => CopyText(string.Join(Environment.NewLine, details.Addresses)));
            if (details.AppPath is { Length: > 0 } path)
            {
                Add(LocKeys.Connections.MenuCopyPath, "", () => CopyText(path));
                Add(LocKeys.Connections.MenuOpenLocation, "", () => OpenFileLocation(path));
            }

            return menu;
        }

        private static void CopyText(string text)
        {
            try
            {
                var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(text);
                global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }
            catch (Exception)
            {
                // Another program holding the clipboard open makes this throw; a copy that did not
                // happen is obvious on the next paste and not worth a dialog.
            }
        }

        /// <summary>Opens the program's folder in Explorer with the file selected. A path that no
        /// longer exists - an uninstalled app, a temp-folder installer that cleaned up after
        /// itself - says so instead of opening Explorer somewhere unrelated.</summary>
        private void OpenFileLocation(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                else
                    ShowNotice(InfoBarSeverity.Warning, Loc.T(LocKeys.Connections.MenuOpenLocation), Loc.T(LocKeys.Connections.OpenLocationMissing));
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Warning, Loc.T(LocKeys.Connections.MenuOpenLocation), ex.Message);
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
