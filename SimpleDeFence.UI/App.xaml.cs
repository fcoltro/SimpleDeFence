using Microsoft.UI.Xaml;
using SimpleDeFence.UI.Services;

namespace SimpleDeFence.UI
{
    public partial class App : Application
    {
        private Window? m_window;

        // Shared by every page so they agree on connection state and the config changeset.
        internal static IFirewallClient Firewall { get; } = new FirewallClient();

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Activate();
        }
    }
}
