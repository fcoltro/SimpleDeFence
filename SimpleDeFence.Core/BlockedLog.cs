using System;
using System.Collections;
using System.Collections.Generic;

namespace SimpleDeFence
{
    /// <summary>
    /// The service's log of recent blocked attempts, oldest first, holding at most
    /// <see cref="Capacity"/> entries.
    ///
    /// A blocked app is refused at once and many simply try again, some many times a second.
    /// When each try was its own entry, one such app filled the whole log within seconds and every
    /// other block fell out of the Blocked list. A repeat of the same attempt (see
    /// <see cref="ConnectionActivity.SameAttempt"/>) is now merged into the entry it repeats: the
    /// count goes up, the time moves to the latest try, and the entry moves to the newest end so it
    /// is not the next one evicted.
    ///
    /// Not thread-safe; the service takes a lock around it. Entries are replaced rather than
    /// changed in place, so a copy taken under that lock stays valid after it is released.
    /// </summary>
    public sealed class BlockedLog : IEnumerable<FirewallLogEntry>
    {
        /// <summary>A repeat this long after the last try starts a new entry instead. Kept short
        /// because the Blocked list shows only the last few minutes and filters on an entry's
        /// latest time: a longer window would let a count include tries from before it.</summary>
        public static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(1);

        private readonly List<FirewallLogEntry> _entries = new();

        public BlockedLog(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
        }

        public int Capacity { get; }

        public int Count => _entries.Count;

        public void Add(FirewallLogEntry entry)
        {
            // Newest first, because the entry being repeated is almost always a recent one.
            for (int i = _entries.Count - 1; i >= 0; --i)
            {
                var existing = _entries[i];
                if (!ConnectionActivity.SameAttempt(existing, entry))
                    continue;

                if (entry.Timestamp - existing.Timestamp > MergeWindow)
                    break;

                _entries.RemoveAt(i);
                var latest = entry.Timestamp >= existing.Timestamp ? entry : existing;
                _entries.Add(latest with { Count = existing.Count + entry.Count });
                return;
            }

            if (_entries.Count == Capacity)
                _entries.RemoveAt(0);
            _entries.Add(entry);
        }

        public IEnumerator<FirewallLogEntry> GetEnumerator() => _entries.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
