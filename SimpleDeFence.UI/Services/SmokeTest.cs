using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence.UI.Pages;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// The UI smoke test CI runs: <c>SimpleDeFence.exe --smoke-test rules --smoke-out result.txt</c>.
    /// Always against <see cref="SampleFirewallClient"/>, never the real service.
    ///
    /// It opens the Rules page, waits for its rows, then leaves it alone for a while and checks
    /// that nothing happened. A page left alone must not save settings and must not rebuild its
    /// lists. The Rules page once did both forever (a special-rule switch echoed its starting value
    /// back as a user toggle, which saved, which refreshed, which made new switches that echoed
    /// again), and unit tests could not see it because it only happens with real XAML controls.
    ///
    /// The result is written to the --smoke-out file and given as the exit code: 0 passed,
    /// 1 failed. A crash or a hang shows up as the process dying or not exiting, which the
    /// workflow treats as a failure too.
    /// </summary>
    internal static class SmokeTest
    {
        /// <summary>How long the page is left alone. Long enough for a loop to go round many
        /// times - the original one went round several times a second.</summary>
        private static readonly TimeSpan IdleTime = TimeSpan.FromSeconds(10);

        private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

        /// <summary>A special rule the sample data has switched on, so a switch starts On - the
        /// case that started the loop.</summary>
        private const string EnabledSpecialRule = SampleFirewallClient.EnabledSpecialRule;

        private static string? _outPath;
        private static readonly List<string> _log = new();

        public static async void Start(string page, string? outPath, SampleFirewallClient client)
        {
            _outPath = outPath;
            Application.Current.UnhandledException += (_, e) =>
                Finish(false, "Unhandled exception: " + e.Exception);

            try
            {
                switch (page.ToLowerInvariant())
                {
                    case "rules":
                        Finish(await RunRulesAsync(client), null);
                        break;
                    default:
                        Finish(false, $"Unknown smoke test '{page}'. Known: rules.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Finish(false, "Smoke test threw: " + ex);
            }
        }

        private static async Task<bool> RunRulesAsync(SampleFirewallClient client)
        {
            if (App.MainWindow is not SimpleDeFence.UI.MainWindow window)
                return Fail("No main window.");

            var frame = await WaitForAsync(
                () => (window.Content as FrameworkElement)?.FindName("ContentFrame") as Frame,
                "the window's content frame");
            if (frame is null)
                return false;

            window.NavigateTo(typeof(RulesPage));

            var page = await WaitForAsync(
                () => frame.Content is RulesPage p && p.IsLoaded ? p : null,
                "the Rules page to load");
            if (page is null)
                return false;

            // Found by name rather than reached into: the page's own code is not changed for this.
            var apps = page.FindName("AppsList") as ListView;
            var special = page.FindName("SpecialRecommendedList") as ListView;
            if (apps is null || special is null)
                return Fail("Rules page has no AppsList or SpecialRecommendedList.");

            if (await WaitForAsync(() => apps.Items.Count > 0 && special.Items.Count > 0 ? apps : null,
                    "the Rules page to show its rows") is null)
                return false;

            Log($"Rules page loaded: {apps.Items.Count} app rules, {special.Items.Count} recommended special rules.");

            // Every refresh of the page clears and refills the app list, so counting clears counts
            // refreshes, without touching the page.
            int rebuilds = 0;
            if (apps.ItemsSource is INotifyCollectionChanged source)
                source.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Reset)
                        ++rebuilds;
                };

            int commitsBefore = client.CommitCount;
            await Task.Delay(IdleTime);
            int commitsDuring = client.CommitCount - commitsBefore;

            Log($"Left alone for {IdleTime.TotalSeconds:0} s: {client.CommitCount} saves since launch "
                + $"({commitsDuring} while idle), {rebuilds} list rebuilds while idle.");

            bool ok = true;
            // Nothing was clicked, so nothing may have been saved - not even once at load.
            if (client.CommitCount != 0)
                ok = Fail($"The page saved settings {client.CommitCount} times without being touched.");
            // One rebuild is tolerated: a Loaded that lands late.
            if (rebuilds > 1)
                ok = Fail($"The page rebuilt its lists {rebuilds} times without being touched.");
            if (client.Config?.ActiveProfile.HasSpecialException(EnabledSpecialRule) != true)
                ok = Fail($"The special rule {EnabledSpecialRule} was switched off without being touched.");
            if (apps.Items.Count == 0)
                ok = Fail("The app rules list is empty after idling.");

            return ok;
        }

        /// <summary>Polls on the UI thread until <paramref name="probe"/> returns something, or
        /// gives up and fails after <see cref="LoadTimeout"/>.</summary>
        private static async Task<T?> WaitForAsync<T>(Func<T?> probe, string what) where T : class
        {
            var deadline = DateTime.UtcNow + LoadTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (probe() is { } found)
                    return found;
                await Task.Delay(250);
            }
            Fail($"Timed out after {LoadTimeout.TotalSeconds:0} s waiting for {what}.");
            return null;
        }

        private static bool Fail(string message)
        {
            Log("FAIL: " + message);
            return false;
        }

        private static void Log(string message) => _log.Add(message);

        private static void Finish(bool passed, string? message)
        {
            if (message is not null)
                Log(message);
            Log(passed ? "PASS" : "FAILED");

            if (_outPath is not null)
            {
                try { File.WriteAllLines(_outPath, _log); }
                catch { /* The exit code still says how it went. */ }
            }

            Environment.Exit(passed ? 0 : 1);
        }
    }
}
