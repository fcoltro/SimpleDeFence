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

    public sealed partial class ConnectionsPage : Page
    {
        private ConnectionsSnapshot _snapshot = new();
        private readonly ObservableCollection<ConnectionListItem> _connected = new();
        private readonly ObservableCollection<ConnectionListItem> _open = new();
        private bool _busy;

        public ConnectionsPage()
        {
            InitializeComponent();
            // Keeps this page instance (and therefore each Expander's IsExpanded) alive across
            // navigating to Rules and back, instead of Frame recreating it - the closest match to
            // the spec's "collapsible, remembered state" without persisting to disk.
            NavigationCacheMode = NavigationCacheMode.Enabled;
            ConnectedList.ItemsSource = _connected;
            OpenList.ItemsSource = _open;
            Loaded += ConnectionsPage_Loaded;
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

        // Overridden by Task 5, which adds the Blocked list and its Allow action. Left as a
        // count-only stub here so this task is independently verifiable.
        private int BlockedCount() => _snapshot.Blocked.Count;
        private void RebuildBlocked(IEnumerable<BlockedRow> rows) { }

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
