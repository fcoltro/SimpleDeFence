using Microsoft.UI.Xaml;
using SimpleDeFence.UI.Services;
using System;

namespace SimpleDeFence.UI
{
    public partial class App : Application
    {
        private Window? m_window;

        // Shared by every page so they agree on connection state and the config changeset.
        // The real client is the default in every configuration; sample data is opt-in only,
        // so fabricated firewall state can never be shown to a user by accident.
        internal static IFirewallClient Firewall { get; private set; } = new FirewallClient();

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // --sample-locked also implies sample data; it simulates a locked service so the
            // GUI's refusal handling can be exercised.
            bool locked = HasSwitch("--sample-locked");
            if (locked || HasSwitch("--sample-data"))
                Firewall = new SampleFirewallClient(locked);

            m_window = new MainWindow();
            m_window.Activate();
        }

        private static bool HasSwitch(string name)
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
