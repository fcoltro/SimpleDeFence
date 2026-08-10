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

        private void AutoRefreshToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (AutoRefreshToggle.IsOn)
                _autoRefreshTimer.Start();
            else
                _autoRefreshTimer.Stop();
        }

        private async void ConnectionsPage_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_busy)
                return;

            SetBusy(true);
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

            SetBusy(false);
            Rebuild();
        }

        private void Rebuild()
        {
            ApplyFilter();
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            var term = FilterBox.Text?.Trim() ?? string.Empty;
            bool Matches(string name) => term.Length == 0 || name.Contains(term, StringComparison.CurrentCultureIgnoreCase);

            _connected.Clear();
            foreach (var row in _snapshot.Connected.Where(r => Matches(r.AppName)))
                _connected.Add(ItemFrom(row));

            _open.Clear();
            foreach (var row in _snapshot.Open.Where(r => Matches(r.AppName)))
                _open.Add(ItemFrom(row));

            RebuildBlocked(_snapshot.Blocked.Where(r => Matches(r.AppName)));

            SetHeader(BlockedHeaderText, LocKeys.Connections.SectionBlocked, BlockedCount());
            SetHeader(ConnectedHeaderText, LocKeys.Connections.SectionConnected, _connected.Count);
            SetHeader(OpenHeaderText, LocKeys.Connections.SectionOpen, _open.Count);

            // Empty states read reassuringly rather than like a failure - an empty Blocked list is
            // a *good* outcome on a firewall.
            ConnectedEmpty.Visibility = _connected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            OpenEmpty.Visibility = _open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BlockedEmpty.Visibility = BlockedCount() == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void SetHeader(TextBlock target, string titleKey, int count)
            => target.Text = Loc.T(titleKey) + " " + Loc.T(LocKeys.Connections.SectionCount, count);

        private int BlockedCount() => _blocked.Count;

        private void RebuildBlocked(IEnumerable<BlockedRow> rows)
        {
            _blocked.Clear();
            foreach (var row in rows)
            {
                var item = new BlockedListItem
                {
                    AppName = string.IsNullOrEmpty(row.AppName) ? Loc.T(LocKeys.Common.Unknown) : row.AppName,
                    Detail = $"{row.Protocol} {row.Direction} → {row.RemoteAddress}:{row.RemotePort}",
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
            ExceptionSubject subject = !string.IsNullOrEmpty(item.PackageId)
                ? new AppContainerSubject(item.PackageId, item.AppName, string.Empty, string.Empty)
                : new ExecutableSubject(item.AppPath ?? string.Empty);

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
                    _ => Loc.T(LocKeys.Mode.SwitchFailedGenericDetail, resp),
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
            await dialog.ShowAsync();
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
