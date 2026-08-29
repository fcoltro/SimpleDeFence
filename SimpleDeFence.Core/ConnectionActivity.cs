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
        /// The blocked attempts worth showing right now: collapsed to BLOCKED/ALLOWED, restricted to
        /// entries within <paramref name="window"/> of <paramref name="now"/> (allowing for a little
        /// clock skew on the near edge - see <see cref="ClockSkewTolerance"/>), deduplicated
        /// (repeated attempts matching on everything but timestamp collapse into one row keeping the
        /// latest time), and newest first.
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

                var existingIndex = deduped.FindIndex(e => e.Equals(collapsed, includeTimestamp: false));
                if (existingIndex < 0)
                    deduped.Add(collapsed);
                else if (collapsed.Timestamp > deduped[existingIndex].Timestamp)
                    deduped[existingIndex] = collapsed;
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
