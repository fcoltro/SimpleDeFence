using Microsoft.UI.Xaml;
using SimpleDeFence;
using System;
using System.Threading.Tasks;

namespace SimpleDeFence.UI
{
    public sealed partial class MainWindow : Window
    {
        // Same pipe name the WinForms controller uses (GlobalInstances.cs) - this shell talks
        // to the existing, unchanged C# service over its current IPC protocol.
        private readonly Controller _controller = new("SimpleDeFenceController");
        private Guid _clientChangeset = Guid.Empty;
        private FirewallMode _lastMode = FirewallMode.Unknown;
        private bool _lastLocked;

        public MainWindow()
        {
            InitializeComponent();
            Title = "SimpleDeFence";
            _ = RefreshStatusAsync();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshStatusAsync();
        }

        private async Task RefreshStatusAsync()
        {
            StatusText.Text = "Connecting...";

            var respType = await Task.Run(() =>
            {
                var changeset = _clientChangeset;
                var result = _controller.GetServerConfig(out _, out var state, ref changeset);
                _clientChangeset = changeset;
                if (state != null)
                {
                    _lastMode = state.Mode;
                    _lastLocked = state.Locked;
                }
                return result;
            });

            if (respType != MessageType.GET_SETTINGS)
            {
                StatusText.Text = "Not connected";
                DetailText.Text = "Could not reach the SimpleDeFence service. Is it installed and running?";
                return;
            }

            StatusText.Text = $"Connected — mode: {_lastMode}";
            DetailText.Text = _lastLocked ? "Configuration is locked." : "Configuration is unlocked.";
        }
    }
}
