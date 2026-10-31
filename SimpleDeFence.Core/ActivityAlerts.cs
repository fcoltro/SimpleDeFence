using System;
using System.Collections.Generic;
using System.IO;

namespace SimpleDeFence
{
    /// <summary>
    /// Decides which of the apps in a connections snapshot are worth a notification: one that was
    /// just blocked from connecting out, or one that has just started accepting incoming
    /// connections. Kept free of UI types so the rules can be tested; the tray service feeds it.
    ///
    /// Each app is announced once per session, and nothing is announced for what was already
    /// there when watching began, so starting the app or reconnecting to the service never
    /// produces a burst. Windows' own components are left out: they are blocked or listening by
    /// design, and the Rules page's special exceptions are where they are allowed.
    /// </summary>
    public sealed class ActivityAlerts
    {
        /// <summary>One app in a snapshot. Key identifies it across snapshots (its path or package),
        /// Name is what the notification calls it.</summary>
        public readonly record struct Sighting(string Key, string Name);

        private readonly HashSet<string> _blockedSeen = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _listenersSeen = new(StringComparer.OrdinalIgnoreCase);
        private bool _baselined;

        /// <summary>Records a snapshot and returns the apps in it not seen before. The first call
        /// only records, and returns nothing.</summary>
        public (IReadOnlyList<Sighting> NewlyBlocked, IReadOnlyList<Sighting> NewListeners) Observe(
            IEnumerable<Sighting> blocked, IEnumerable<Sighting> listening)
        {
            var newlyBlocked = Collect(blocked, _blockedSeen);
            var newListeners = Collect(listening, _listenersSeen);

            if (!_baselined)
            {
                _baselined = true;
                return (Array.Empty<Sighting>(), Array.Empty<Sighting>());
            }

            return (newlyBlocked, newListeners);
        }

        /// <summary>Starts over, for when the service connection was lost: what is blocked by
        /// then is a new baseline, not news.</summary>
        public void Reset()
        {
            _blockedSeen.Clear();
            _listenersSeen.Clear();
            _baselined = false;
        }

        private static List<Sighting> Collect(IEnumerable<Sighting> rows, HashSet<string> seen)
        {
            var fresh = new List<Sighting>();
            foreach (var row in rows)
            {
                if (seen.Add(row.Key))
                    fresh.Add(row);
            }
            return fresh;
        }

        /// <summary>
        /// The key an app is tracked by, or null when it should never be announced: nothing to
        /// name it by, or a program inside the Windows folder. A package is keyed by its id, a
        /// program by its full path, so two copies of one program are two apps, as they are on
        /// the Rules page.
        /// </summary>
        public static string? KeyFor(string? path, string? packageId, string windowsDirectory)
        {
            if (!string.IsNullOrEmpty(packageId))
                return "pkg:" + packageId;

            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
                return null;

            var windows = windowsDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!string.IsNullOrEmpty(windowsDirectory) && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                return null;

            return "exe:" + path;
        }
    }
}
