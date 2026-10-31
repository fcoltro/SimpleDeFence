using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleDeFence
{
    /// <summary>
    /// Pure transformations over the firewall's raw event log and resolved process identity,
    /// shared by both GUIs. Mirrors filtering the WinForms ConnectionsForm already does ad hoc in
    /// its code-behind, extracted here so it is unit-tested and reusable from the WinUI GUI too.
    /// </summary>
    public static class ConnectionActivity
    {
        /// <summary>
        /// Collapses the Security-log event-ID variants (_LISTEN/_CONNECTION/_PACKET/_LOCAL_BIND)
        /// down to a plain BLOCKED or ALLOWED, so callers only ever need to branch on two outcomes.
        /// </summary>
        public static FirewallLogEntry Collapse(FirewallLogEntry entry) => entry with
        {
            Event = IsBlockedEvent(entry.Event) ? EventLogEvent.BLOCKED : EventLogEvent.ALLOWED,
        };

        private static bool IsBlockedEvent(EventLogEvent e) => e switch
        {
            EventLogEvent.BLOCKED
                or EventLogEvent.BLOCKED_LISTEN
                or EventLogEvent.BLOCKED_CONNECTION
                or EventLogEvent.BLOCKED_PACKET
                or EventLogEvent.BLOCKED_LOCAL_BIND => true,
            _ => false,
        };

        /// <summary>
        /// How far ahead of <c>now</c> an entry may be stamped and still count as recent.
        ///
        /// Entries are stamped by the service, from the WFP event header, and read by the GUI
        /// against its own DateTime.Now, so the two ends can disagree slightly - an event stamped
        /// during the gather, or a clock adjustment landing between the two reads. The window used
        /// to have a hard "not in the future" edge, which the WinForms ConnectionsForm this
        /// replaced never had: it only ever discarded entries that were too OLD. An entry a
        /// fraction of a second ahead is a real block that was silently dropped from the list, and
        /// a dropped block is one the user cannot release - the whole purpose of the section.
        ///
        /// Bounded rather than removed, so a genuinely wrong clock cannot fill the list with
        /// entries dated next week.
        /// </summary>
        private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Whether two entries are the same attempt made again: same app, process, direction,
        /// protocol and remote address, and the same port on the side that names the service.
        ///
        /// The other port is left out on purpose. An outgoing connection gets a new local port
        /// from Windows on every try, so comparing it made each retry of a blocked app its own
        /// row. For an incoming one it is the remote port that changes.
        /// </summary>
        public static bool SameAttempt(FirewallLogEntry a, FirewallLogEntry b)
        {
            if (a.Event != b.Event
                || a.ProcessId != b.ProcessId
                || a.Protocol != b.Protocol
                || a.Direction != b.Direction
                || !string.Equals(a.RemoteIp, b.RemoteIp, StringComparison.Ordinal)
                || !string.Equals(a.AppPath, b.AppPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a.PackageId, b.PackageId, StringComparison.Ordinal))
                return false;

            return a.Direction switch
            {
                RuleDirection.Out => a.RemotePort == b.RemotePort,
                RuleDirection.In => a.LocalPort == b.LocalPort,
                _ => a.RemotePort == b.RemotePort && a.LocalPort == b.LocalPort,
            };
        }

        /// <summary>
        /// The blocked attempts worth showing right now: collapsed to BLOCKED/ALLOWED, restricted to
        /// entries within <paramref name="window"/> of <paramref name="now"/> (allowing for a little
        /// clock skew on the near edge - see <see cref="ClockSkewTolerance"/>), deduplicated
        /// (repeats of the same attempt - see <see cref="SameAttempt"/> - collapse into one row
        /// keeping the latest time and adding up their counts), and newest first.
        /// </summary>
        public static IReadOnlyList<FirewallLogEntry> RecentBlocked(
            IEnumerable<FirewallLogEntry> entries, DateTime now, TimeSpan window)
        {
            var cutoff = now - window;
            var horizon = now + ClockSkewTolerance;
            var deduped = new List<FirewallLogEntry>();

            foreach (var raw in entries)
            {
                if (raw.Timestamp < cutoff || raw.Timestamp > horizon)
                    continue;

                var collapsed = Collapse(raw);
                if (collapsed.Event != EventLogEvent.BLOCKED)
                    continue;

                var existingIndex = deduped.FindIndex(e => SameAttempt(e, collapsed));
                if (existingIndex < 0)
                    deduped.Add(collapsed);
                else
                {
                    var existing = deduped[existingIndex];
                    var newer = collapsed.Timestamp > existing.Timestamp ? collapsed : existing;
                    deduped[existingIndex] = newer with { Count = existing.Count + collapsed.Count };
                }
            }

            return deduped.OrderByDescending(e => e.Timestamp).ToList();
        }

        /// <summary>
        /// What to call a row identifying a process: a UWP package's display name if it has one,
        /// else the hosted Windows service name(s) if any (e.g. "DoSvc, UsoSvc" for a shared
        /// svchost.exe), else the executable's filename, else empty if nothing is known.
        /// </summary>
        public static string DisplayName(string? path, string? packageDisplayName, IReadOnlyCollection<string>? services)
        {
            if (!string.IsNullOrEmpty(packageDisplayName))
                return packageDisplayName!;

            if (services is { Count: > 0 })
                return string.Join(", ", services.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(path))
                return System.IO.Path.GetFileName(path);

            return string.Empty;
        }
    }
}
