using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimpleDeFence;
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
                ShowNotice(InfoBarSeverity.Error, "Not connected", App.Firewall.LastError ?? string.Empty);
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

            SummaryText.Text = App.Firewall.Connected
                ? $"{_all.Count} exception{(_all.Count == 1 ? "" : "s")} in profile \"{profile?.ProfileName}\""
                : "Not connected";

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

        // Mirrors SettingsForm.ListItemFromAppException so both GUIs describe an entry the same way.
        private static ExceptionRow RowFrom(FirewallExceptionV3 ex)
        {
            string name, kind, detail;

            // Switch on SubjectType, not the runtime type: ServiceSubject derives from
            // ExecutableSubject, so a type-pattern switch silently matches services as
            // executables. SettingsForm switches on SubjectType for the same reason.
            switch (ex.Subject.SubjectType)
            {
                case SubjectType.Executable:
                {
                    var exe = (ExecutableSubject)ex.Subject;
                    name = exe.ExecutableName;
                    kind = "Executable";
                    detail = exe.ExecutablePath;
                    break;
                }

                case SubjectType.Service:
                {
                    var svc = (ServiceSubject)ex.Subject;
                    name = svc.ServiceName;
                    kind = "Service";
                    detail = svc.ExecutablePath;
                    break;
                }

                case SubjectType.AppContainer:
                {
                    var uwp = (AppContainerSubject)ex.Subject;
                    name = uwp.DisplayName;
                    kind = "UWP Package";
                    detail = $"{uwp.PublisherId}, {uwp.Publisher}";
                    break;
                }

                case SubjectType.Global:
                    name = "All applications";
                    kind = "Global";
                    detail = string.Empty;
                    break;

                default:
                    name = ex.Subject.ToString() ?? "Unknown";
                    kind = "Unknown";
                    detail = string.Empty;
                    break;
            }

            return new ExceptionRow
            {
                Name = name,
                Kind = kind,
                Detail = detail,
                Policy = DescribePolicy(ex.Policy),
                Created = ex.CreationDate.ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture),
                IsBlocked = ex.Policy.PolicyType == PolicyType.HardBlock,
            };
        }

        private static string DescribePolicy(ExceptionPolicy policy) => policy switch
        {
            HardBlockPolicy => "Blocked",
            UnrestrictedPolicy u => u.LocalNetworkOnly ? "Unrestricted (LAN only)" : "Unrestricted",
            TcpUdpPolicy t => DescribeTcpUdp(t),
            RuleListPolicy r => $"{r.Rules.Count} custom rule{(r.Rules.Count == 1 ? "" : "s")}",
            _ => "Unknown",
        };

        private static string DescribeTcpUdp(TcpUdpPolicy p)
        {
            var parts = new List<string>();
            Add(parts, "TCP out", p.AllowedRemoteTcpConnectPorts);
            Add(parts, "UDP out", p.AllowedRemoteUdpConnectPorts);
            Add(parts, "TCP in", p.AllowedLocalTcpListenerPorts);
            Add(parts, "UDP in", p.AllowedLocalUdpListenerPorts);

            if (parts.Count == 0)
                return "No ports allowed";

            var text = string.Join(", ", parts);
            return p.LocalNetworkOnly ? text + " (LAN only)" : text;

            static void Add(List<string> into, string label, string? ports)
            {
                if (string.IsNullOrEmpty(ports))
                    return;
                into.Add($"{label} {(ports == "*" ? "all" : ports)}");
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
