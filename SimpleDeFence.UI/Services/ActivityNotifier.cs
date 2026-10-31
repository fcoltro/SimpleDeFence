using Microsoft.UI.Dispatching;
using SimpleDeFence.Localization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Watches the same snapshot the Connections page shows and tells the user, from the tray,
    /// when an app is newly blocked from connecting out or newly accepting incoming connections.
    /// ActivityAlerts decides what counts as new; this only gathers, words and shows.
    ///
    /// Independent of any page, like ConnectionLogService, because the moment it matters is when
    /// the window is closed and a program has just failed to connect for no reason the user can
    /// see. It polls rather than being told: the service pushes nothing to clients.
    /// </summary>
    internal sealed class ActivityNotifier : IDisposable
    {
        // Slower than the Connections page's refresh: a few seconds' delay costs nothing here, and
        // each gather is a netstat plus a firewall-log read.
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

        private readonly DispatcherQueueTimer _timer;
        private readonly ActivityAlerts _alerts = new();
        private readonly string _windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // One notification per tick, as the shell shows one at a time; the rest wait their turn.
        private readonly Queue<(string Title, string Message, string? AppName)> _pending = new();

        private ClientSettings _settings = new();
        private bool _polling;
        private bool _wasConnected;
        private bool _disposed;

        public ActivityNotifier(DispatcherQueue dispatcher)
        {
            _timer = dispatcher.CreateTimer();
            _timer.Interval = Interval;
            _timer.Tick += async (_, _) => await PollAsync();
            ApplySettings(ClientSettings.Load());
        }

        public void ApplySettings(ClientSettings settings)
        {
            if (_disposed)
                return;

            _settings = settings;
            _timer.Stop();
            _pending.Clear();

            // Off means off: nothing gathered, and nothing remembered, so turning it back on starts
            // from a fresh baseline instead of announcing everything that happened in between.
            _alerts.Reset();

            if (settings.NotifyWhenBlocked || settings.NotifyWhenListening)
                _timer.Start();
        }

        private async System.Threading.Tasks.Task PollAsync()
        {
            if (_polling || _disposed)
                return;

            _polling = true;
            try
            {
                if (_pending.Count > 0)
                {
                    Show(_pending.Dequeue());
                    return;
                }

                // Nothing else refreshes the client while the window is closed to the tray, and
                // both the connection state and the mode below are read from it. A refresh with an
                // unchanged changeset is one small round trip.
                await App.Firewall.RefreshAsync();

                if (!App.Firewall.Connected)
                {
                    // Whatever is blocked by the time the service is back is a new baseline.
                    if (_wasConnected)
                        _alerts.Reset();
                    _wasConnected = false;
                    return;
                }
                _wasConnected = true;

                var snapshot = await App.Firewall.GetConnectionsAsync();

                var blocked = snapshot.Blocked
                    .Where(r => r.Direction == nameof(RuleDirection.Out))
                    .Select(r => Sight(r.AppPath, r.PackageId, r.AppName));
                var listening = snapshot.Open
                    .Select(r => Sight(r.AppPath, null, r.AppName));

                var (newlyBlocked, newListeners) = _alerts.Observe(
                    blocked.OfType<ActivityAlerts.Sighting>(),
                    listening.OfType<ActivityAlerts.Sighting>());

                // Blocked notices only in Normal. In Block all everything is blocked on purpose,
                // and in the other modes outgoing connections are not blocked at all.
                if (_settings.NotifyWhenBlocked && App.Firewall.State?.Mode == FirewallMode.Normal && newlyBlocked.Count > 0)
                    _pending.Enqueue(Describe(newlyBlocked, LocKeys.Notifications.BlockedOneTitle, LocKeys.Notifications.BlockedManyTitle, LocKeys.Notifications.BlockedBody));

                if (_settings.NotifyWhenListening && newListeners.Count > 0)
                    _pending.Enqueue(Describe(newListeners, LocKeys.Notifications.ListeningOneTitle, LocKeys.Notifications.ListeningManyTitle, LocKeys.Notifications.ListeningBody));

                if (_pending.Count > 0)
                    Show(_pending.Dequeue());
            }
            catch (Exception ex)
            {
                // A notification is a courtesy. A gather that fails is tried again next tick.
                System.Diagnostics.Debug.WriteLine($"Activity check failed: {ex.Message}");
            }
            finally
            {
                _polling = false;
            }
        }

        private ActivityAlerts.Sighting? Sight(string? path, string? packageId, string name)
            => ActivityAlerts.KeyFor(path, packageId, _windowsDirectory) is string key
                ? new ActivityAlerts.Sighting(key, name)
                : null;

        /// <summary>Names the app when there is one, and counts the rest when there are more, so
        /// a burst at login is one notification rather than a stack of them.</summary>
        private static (string Title, string Message, string? AppName) Describe(
            IReadOnlyList<ActivityAlerts.Sighting> apps, string oneTitle, string manyTitle, string body)
        {
            var first = apps[0].Name;
            return apps.Count == 1
                ? (Loc.T(oneTitle, first), Loc.T(body), first)
                : (Loc.T(manyTitle, first, apps.Count - 1), Loc.T(body), null);
        }

        private static void Show((string Title, string Message, string? AppName) notice)
            => App.Tray?.ShowActivityNotification(notice.Title, notice.Message, notice.AppName);

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Stop();
        }
    }
}
