using Microsoft.UI.Xaml;
using SimpleDeFence.Localization;
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
            // Must run before any XAML is touched: MainWindow's constructor resolves Loc.T for
            // its Title, and every {loc:Loc} markup extension in the tree resolves during
            // InitializeComponent - both need the right culture selected first.
            var langOverride = ArgValue("--lang");
            if (langOverride is not null)
                Loc.SetCulture(langOverride);
            else
                Loc.UseSystemCulture();

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

        /// <summary>Reads "--name value" from the command line, or null if absent.</summary>
        private static string? ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; ++i)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
