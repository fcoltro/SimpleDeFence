using SimpleDeFence;
using System;
using System.Linq;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class ConnectionActivityTests
    {
        private static FirewallLogEntry Entry(EventLogEvent ev, DateTime ts, uint pid = 100, int remotePort = 443, string remoteIp = "1.2.3.4") => new()
        {
            Timestamp = ts,
            Event = ev,
            ProcessId = pid,
            Protocol = Protocol.TCP,
            Direction = RuleDirection.Out,
            LocalIp = "10.0.0.5",
            RemoteIp = remoteIp,
            LocalPort = 51000,
            RemotePort = remotePort,
            AppPath = @"C:\app.exe",
        };

        [Fact]
        public void Collapse_maps_every_blocked_variant_to_BLOCKED()
        {
            var now = DateTime.Now;
            Assert.Equal(EventLogEvent.BLOCKED, ConnectionActivity.Collapse(Entry(EventLogEvent.BLOCKED_CONNECTION, now)).Event);
            Assert.Equal(EventLogEvent.BLOCKED, ConnectionActivity.Collapse(Entry(EventLogEvent.BLOCKED_LISTEN, now)).Event);
            Assert.Equal(EventLogEvent.BLOCKED, ConnectionActivity.Collapse(Entry(EventLogEvent.BLOCKED_PACKET, now)).Event);
            Assert.Equal(EventLogEvent.BLOCKED, ConnectionActivity.Collapse(Entry(EventLogEvent.BLOCKED_LOCAL_BIND, now)).Event);
        }

        [Fact]
        public void Collapse_maps_every_allowed_variant_to_ALLOWED()
        {
            var now = DateTime.Now;
            Assert.Equal(EventLogEvent.ALLOWED, ConnectionActivity.Collapse(Entry(EventLogEvent.ALLOWED_CONNECTION, now)).Event);
            Assert.Equal(EventLogEvent.ALLOWED, ConnectionActivity.Collapse(Entry(EventLogEvent.ALLOWED_LISTEN, now)).Event);
            Assert.Equal(EventLogEvent.ALLOWED, ConnectionActivity.Collapse(Entry(EventLogEvent.ALLOWED_LOCAL_BIND, now)).Event);
        }

        [Fact]
        public void RecentBlocked_excludes_entries_outside_the_window()
        {
            var now = DateTime.Now;
            var entries = new[]
            {
                Entry(EventLogEvent.BLOCKED, now.AddMinutes(-2)),
                Entry(EventLogEvent.BLOCKED, now.AddMinutes(-10)),
            };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Single(result);
            Assert.Equal(now.AddMinutes(-2), result[0].Timestamp);
        }

        [Fact]
        public void RecentBlocked_excludes_allowed_entries()
        {
            var now = DateTime.Now;
            var entries = new[] { Entry(EventLogEvent.ALLOWED_CONNECTION, now) };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Empty(result);
        }

        [Fact]
        public void RecentBlocked_deduplicates_repeated_attempts_keeping_the_latest_timestamp()
        {
            var now = DateTime.Now;
            var entries = new[]
            {
                Entry(EventLogEvent.BLOCKED_CONNECTION, now.AddMinutes(-3), pid: 200, remotePort: 443, remoteIp: "1.1.1.1"),
                Entry(EventLogEvent.BLOCKED_CONNECTION, now.AddMinutes(-1), pid: 200, remotePort: 443, remoteIp: "1.1.1.1"),
            };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Single(result);
            Assert.Equal(now.AddMinutes(-1), result[0].Timestamp);
        }

        [Fact]
        public void RecentBlocked_keeps_distinct_ports_as_separate_rows()
        {
            var now = DateTime.Now;
            var entries = new[]
            {
                Entry(EventLogEvent.BLOCKED_CONNECTION, now, pid: 200, remotePort: 443),
                Entry(EventLogEvent.BLOCKED_CONNECTION, now, pid: 200, remotePort: 8080),
            };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void RecentBlocked_orders_newest_first()
        {
            var now = DateTime.Now;
            var entries = new[]
            {
                Entry(EventLogEvent.BLOCKED, now.AddMinutes(-4), pid: 1),
                Entry(EventLogEvent.BLOCKED, now.AddMinutes(-1), pid: 2),
                Entry(EventLogEvent.BLOCKED, now.AddMinutes(-2), pid: 3),
            };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Equal(new uint[] { 2, 3, 1 }, new[] { result[0].ProcessId, result[1].ProcessId, result[2].ProcessId });
        }

        [Fact]
        public void DisplayName_prefers_package_then_services_then_executable_filename()
        {
            Assert.Equal("Contoso App", ConnectionActivity.DisplayName(@"C:\app.exe", "Contoso App", new[] { "SomeSvc" }));
            Assert.Equal("DoSvc, UsoSvc", ConnectionActivity.DisplayName(@"C:\Windows\System32\svchost.exe", null, new[] { "DoSvc", "UsoSvc" }));
            Assert.Equal("app.exe", ConnectionActivity.DisplayName(@"C:\Program Files\app.exe", null, null));
        }

        [Fact]
        public void RecentBlocked_keeps_an_entry_stamped_slightly_ahead_of_now()
        {
            // The service stamps entries from the WFP event header and the GUI compares them
            // against its own clock, so an entry can land a moment "in the future". It is still a
            // real block, and a block that is not listed is one the user cannot release.
            var now = DateTime.Now;
            var entries = new[] { Entry(EventLogEvent.BLOCKED_CONNECTION, now.AddSeconds(2)) };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Single(result);
        }

        [Fact]
        public void RecentBlocked_still_discards_an_entry_from_a_badly_wrong_clock()
        {
            var now = DateTime.Now;
            var entries = new[] { Entry(EventLogEvent.BLOCKED_CONNECTION, now.AddDays(7)) };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Empty(result);
        }

        [Fact]
        public void RecentBlocked_merges_retries_that_differ_only_in_local_port_and_adds_up_counts()
        {
            // Windows hands an outgoing connection a fresh local port on every try, so a blocked
            // app retrying in a loop must still come out as one row.
            var now = DateTime.Now;
            var entries = new[]
            {
                Entry(EventLogEvent.BLOCKED_CONNECTION, now.AddMinutes(-2)) with { LocalPort = 50001, Count = 40 },
                Entry(EventLogEvent.BLOCKED_CONNECTION, now.AddMinutes(-1)) with { LocalPort = 50002 },
            };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Single(result);
            Assert.Equal(41, result[0].Count);
            Assert.Equal(now.AddMinutes(-1), result[0].Timestamp);
        }

        [Fact]
        public void SameAttempt_compares_the_local_port_for_incoming_attempts()
        {
            var now = DateTime.Now;
            var a = Entry(EventLogEvent.BLOCKED, now) with { Direction = RuleDirection.In, LocalPort = 445, RemotePort = 60001 };

            Assert.True(ConnectionActivity.SameAttempt(a, a with { RemotePort = 60002 }));
            Assert.False(ConnectionActivity.SameAttempt(a, a with { LocalPort = 3389 }));
        }

        [Fact]
        public void BlockedLog_merges_a_repeat_into_one_entry_and_moves_it_to_the_newest_end()
        {
            var now = DateTime.Now;
            var log = new BlockedLog(10);
            log.Add(Entry(EventLogEvent.BLOCKED, now.AddSeconds(-30), remoteIp: "1.1.1.1") with { LocalPort = 50001 });
            log.Add(Entry(EventLogEvent.BLOCKED, now.AddSeconds(-20), remoteIp: "2.2.2.2"));
            log.Add(Entry(EventLogEvent.BLOCKED, now, remoteIp: "1.1.1.1") with { LocalPort = 50002 });

            var entries = log.ToArray();
            Assert.Equal(2, entries.Length);
            Assert.Equal("2.2.2.2", entries[0].RemoteIp);
            Assert.Equal("1.1.1.1", entries[1].RemoteIp);
            Assert.Equal(2, entries[1].Count);
            Assert.Equal(now, entries[1].Timestamp);
        }

        [Fact]
        public void BlockedLog_does_not_let_one_noisy_app_evict_the_others()
        {
            var now = DateTime.Now;
            var log = new BlockedLog(3);
            log.Add(Entry(EventLogEvent.BLOCKED, now, remoteIp: "9.9.9.9", pid: 7));
            for (int i = 0; i < 1000; ++i)
                log.Add(Entry(EventLogEvent.BLOCKED, now.AddMilliseconds(i), remoteIp: "1.1.1.1") with { LocalPort = 50000 + i });

            Assert.Equal(2, log.Count);
            Assert.Contains(log, e => e.ProcessId == 7);
            Assert.Contains(log, e => e.RemoteIp == "1.1.1.1" && e.Count == 1000);
        }

        [Fact]
        public void BlockedLog_starts_a_new_entry_after_a_long_quiet_gap()
        {
            var now = DateTime.Now;
            var log = new BlockedLog(10);
            log.Add(Entry(EventLogEvent.BLOCKED, now - BlockedLog.MergeWindow - TimeSpan.FromMinutes(1)));
            log.Add(Entry(EventLogEvent.BLOCKED, now));

            Assert.Equal(2, log.Count);
            Assert.All(log, e => Assert.Equal(1, e.Count));
        }

        [Fact]
        public void BlockedLog_evicts_the_oldest_entry_when_full()
        {
            var now = DateTime.Now;
            var log = new BlockedLog(2);
            log.Add(Entry(EventLogEvent.BLOCKED, now, remotePort: 1));
            log.Add(Entry(EventLogEvent.BLOCKED, now, remotePort: 2));
            log.Add(Entry(EventLogEvent.BLOCKED, now, remotePort: 3));

            Assert.Equal(new[] { 2, 3 }, log.Select(e => e.RemotePort).ToArray());
        }

        [Fact]
        public void DisplayName_returns_empty_when_nothing_is_known()
        {
            Assert.Equal(string.Empty, ConnectionActivity.DisplayName(null, null, null));
        }
    }
}
