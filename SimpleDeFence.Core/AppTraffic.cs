using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace SimpleDeFence
{
    /// <summary>
    /// How much one app has sent and received since the service started counting. One row of the
    /// Connections page's "Data used" section.
    /// </summary>
    public sealed record AppTraffic
    {
        /// <summary>The executable, or "System" for the kernel, or empty when the process was gone
        /// before its path could be read.</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>The Windows services hosted by the process, for a shared svchost.exe. Kept apart
        /// per set of services, or every service on the machine would add up into one
        /// "svchost.exe" row nobody could act on.</summary>
        public string[] Services { get; init; } = Array.Empty<string>();

        public long Sent { get; init; }
        public long Received { get; init; }
    }

    /// <summary>
    /// Adds up network bytes per app from a stream of (process id, bytes, direction) events.
    ///
    /// The events name a process id, and a process id is only good for as long as the process
    /// lives: Windows hands it to the next process soon after. So bytes are first counted per
    /// process, and <see cref="Sweep"/> looks each live process up again. When it has exited, or
    /// its id now belongs to a different program, its bytes move into a per-app total and the id is
    /// forgotten. Totals survive the app restarting; a browser that has been closed and opened ten
    /// times today is still one row.
    ///
    /// Thread safety: <see cref="Record"/> runs on the trace's own thread, many thousands of times a
    /// second under load, so it takes no lock. Sweep and Snapshot may run from any thread and
    /// serialize on a lock of their own.
    /// </summary>
    public sealed class TrafficLedger
    {
        private sealed class ProcessCounter
        {
            public readonly string Path;
            public string[]? Services;
            public long Sent;
            public long Received;

            public ProcessCounter(string path) => Path = path;
        }

        private readonly Func<uint, string> _pathOf;
        private readonly Func<uint, IReadOnlyCollection<string>?> _servicesOf;
        private readonly ConcurrentDictionary<uint, ProcessCounter> _live = new();
        private readonly Dictionary<string, AppTraffic> _finished = new(StringComparer.Ordinal);
        private readonly object _sweepLock = new();

        /// <param name="pathOf">The executable behind a process id, or empty when it cannot be
        /// read (most often because the process has exited).</param>
        /// <param name="servicesOf">The services hosted in a process. Only asked for processes
        /// whose executable is svchost.exe, and only once per process.</param>
        public TrafficLedger(Func<uint, string> pathOf, Func<uint, IReadOnlyCollection<string>?> servicesOf)
        {
            _pathOf = pathOf;
            _servicesOf = servicesOf;
            Since = DateTime.Now;
        }

        /// <summary>When counting started. The totals cover the time since then and nothing
        /// before it.</summary>
        public DateTime Since { get; }

        public void Record(uint pid, long bytes, bool sent)
        {
            if (bytes <= 0)
                return;

            // The path is read the first time a process is seen, while it is most likely still
            // running. A short-lived process could otherwise be gone by the time anyone asks.
            if (!_live.TryGetValue(pid, out var counter))
                counter = _live.GetOrAdd(pid, id => new ProcessCounter(_pathOf(id) ?? string.Empty));

            if (sent)
                Interlocked.Add(ref counter.Sent, bytes);
            else
                Interlocked.Add(ref counter.Received, bytes);
        }

        /// <summary>
        /// Moves the bytes of every process that has exited, or whose id now names a different
        /// program, into its app's total. Bytes a new process sends between reusing an id and the
        /// next sweep are counted for the old one; the service sweeps every few seconds to keep
        /// that window small.
        /// </summary>
        public void Sweep()
        {
            lock (_sweepLock)
            {
                foreach (var (pid, counter) in _live)
                {
                    var current = _pathOf(pid) ?? string.Empty;
                    if (current.Length != 0 && string.Equals(current, counter.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        // Still the same program. A svchost's services are filled in here rather
                        // than in Record, because listing services is far too slow for the trace
                        // thread. An empty answer is asked again next time: the service may only
                        // just have started.
                        if (counter.Services is null)
                        {
                            var services = IsServiceHost(counter.Path) ? Sorted(_servicesOf(pid)) : Array.Empty<string>();
                            if (services.Length != 0 || !IsServiceHost(counter.Path))
                                counter.Services = services;
                        }
                        continue;
                    }

                    if (!_live.TryRemove(new KeyValuePair<uint, ProcessCounter>(pid, counter)))
                        continue;

                    AddTo(_finished, ToTraffic(counter));
                }
            }
        }

        /// <summary>Every app's totals, finished processes and running ones together.</summary>
        public AppTraffic[] Snapshot()
        {
            lock (_sweepLock)
            {
                Sweep();

                var totals = new Dictionary<string, AppTraffic>(_finished, StringComparer.Ordinal);
                foreach (var counter in _live.Values)
                    AddTo(totals, ToTraffic(counter));

                return totals.Values
                    .Where(t => t.Sent + t.Received > 0)
                    .OrderByDescending(t => t.Sent + t.Received)
                    .ToArray();
            }
        }

        private static AppTraffic ToTraffic(ProcessCounter counter) => new()
        {
            Path = counter.Path,
            Services = counter.Services ?? Array.Empty<string>(),
            Sent = Interlocked.Read(ref counter.Sent),
            Received = Interlocked.Read(ref counter.Received),
        };

        private static void AddTo(Dictionary<string, AppTraffic> totals, AppTraffic traffic)
        {
            var key = KeyOf(traffic);
            totals[key] = totals.TryGetValue(key, out var existing)
                ? existing with { Sent = existing.Sent + traffic.Sent, Received = existing.Received + traffic.Received }
                : traffic;
        }

        /// <summary>One row per program, and for a shared service host one row per set of
        /// services it hosts.</summary>
        public static string KeyOf(AppTraffic traffic)
            => traffic.Path.ToUpperInvariant() + "|" + string.Join(",", traffic.Services).ToUpperInvariant();

        private static bool IsServiceHost(string path)
            => string.Equals(System.IO.Path.GetFileName(path), "svchost.exe", StringComparison.OrdinalIgnoreCase);

        private static string[] Sorted(IReadOnlyCollection<string>? services)
            => services is null || services.Count == 0
                ? Array.Empty<string>()
                : services.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static class TrafficFormat
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        /// <summary>A byte count the way Explorer writes one: 1024 to the step, one decimal below
        /// 100 and none above, so a column of them stays short.</summary>
        public static string Bytes(long bytes, CultureInfo? culture = null)
        {
            culture ??= CultureInfo.CurrentCulture;
            if (bytes < 1024)
                return Math.Max(bytes, 0).ToString(culture) + " B";

            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            // Rounding can carry 1023.96 KB up to "1024 KB"; step up a unit instead.
            if (Math.Round(value, value < 100 ? 1 : 0) >= 1024 && unit < Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString(value < 100 ? "0.#" : "0", culture) + " " + Units[unit];
        }
    }
}
