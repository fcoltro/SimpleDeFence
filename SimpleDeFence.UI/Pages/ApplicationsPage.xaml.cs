using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleDeFence;
using SimpleDeFence.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    /// <summary>One row of the exception list. Mirrors what SettingsForm shows per entry.</summary>
    public sealed class ExceptionRow
    {
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string Policy { get; init; } = string.Empty;
        public string Created { get; init; } = string.Empty;
        public bool IsBlocked { get; init; }

        /// <summary>Blocked entries are tinted, the same signal SettingsForm gives via row colour.</summary>
        // global:: is required - inside SimpleDeFence.UI.Pages a bare "Windows" binds to
        // SimpleDeFence.Windows, not the platform namespace.
        public Brush PolicyBackground => IsBlocked
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x40, 0xE8, 0x11, 0x23))
            : new SolidColorBrush(global::Microsoft.UI.Colors.Transparent);
    }

    public sealed partial class ApplicationsPage : Page
    {
        private readonly ObservableCollection<ExceptionRow> _visible = new();
        private List<ExceptionRow> _all = new();
        private bool _busy;

        public ApplicationsPage()
        {
            InitializeComponent();
            ExceptionList.ItemsSource = _visible;
            Loaded += ApplicationsPage_Loaded;
        }

        private async void ApplicationsPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Reuse whatever the shared client already has; only go to the service if it has
            // nothing cached yet.
            if (App.Firewall.Config is null)
                await RefreshAsync();
            else
                Rebuild();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
            => await RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_busy)
                return;

            SetBusy(true);
            await App.Firewall.RefreshAsync();
            SetBusy(false);

            if (!App.Firewall.Connected)
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Status.NotConnected), App.Firewall.LastError ?? string.Empty);
            else
                Notice.IsOpen = false;

            Rebuild();
        }

        private void Rebuild()
        {
            var profile = App.Firewall.Config?.ActiveProfile;
            _all = profile is null
                ? new List<ExceptionRow>()
                : profile.AppExceptions.Select(RowFrom).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            var profileName = profile?.ProfileName ?? string.Empty;
            SummaryText.Text = App.Firewall.Connected
                ? (_all.Count == 1
                    ? Loc.T(LocKeys.Applications.SummaryOne, profileName)
                    : Loc.T(LocKeys.Applications.Summary, _all.Count, profileName))
                : Loc.T(LocKeys.Status.NotConnected);

            ApplyFilter();
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            var term = FilterBox.Text?.Trim() ?? string.Empty;

            _visible.Clear();
            foreach (var row in _all)
            {
                if (term.Length == 0
                    || row.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                    || row.Detail.Contains(term, StringComparison.CurrentCultureIgnoreCase))
                {
                    _visible.Add(row);
                }
            }
        }

        // The describing itself lives in Core (ExceptionDescriptor) so both GUIs can render an
        // entry identically, and so it can be unit tested without standing up any UI.
        private static ExceptionRow RowFrom(FirewallExceptionV3 ex)
        {
            var d = ExceptionDescriptor.Describe(ex);

            return new ExceptionRow
            {
                Name = d.Name,
                Kind = d.Kind,
                Detail = d.Detail,
                Policy = d.Policy,
                Created = ex.CreationDate.ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture),
                IsBlocked = d.IsBlocked,
            };
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
