using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimpleDeFence;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class TrafficLedgerTests
    {
        /// <summary>A pretend process table: what each process id points at right now.</summary>
        private sealed class Processes
        {
            public readonly Dictionary<uint, string> Paths = new();
            public readonly Dictionary<uint, string[]> Services = new();
            public int ServiceLookups;

            public TrafficLedger Ledger() => new(
                pid => Paths.TryGetValue(pid, out var p) ? p : string.Empty,
                pid => { ServiceLookups++; return Services.TryGetValue(pid, out var s) ? s : null; });
        }

        private const string Browser = @"C:\Apps\browser.exe";
        private const string Editor = @"C:\Apps\editor.exe";
        private const string SvcHost = @"C:\Windows\System32\svchost.exe";

        [Fact]
        public void Bytes_add_up_per_app_and_direction()
        {
            var p = new Processes();
            p.Paths[10] = Browser;
            var ledger = p.Ledger();

            ledger.Record(10, 100, sent: true);
            ledger.Record(10, 50, sent: true);
            ledger.Record(10, 1000, sent: false);

            var row = Assert.Single(ledger.Snapshot());
            Assert.Equal(Browser, row.Path);
            Assert.Equal(150, row.Sent);
            Assert.Equal(1000, row.Received);
        }

        [Fact]
        public void An_app_that_restarts_stays_one_row()
        {
            var p = new Processes();
            p.Paths[10] = Browser;
            var ledger = p.Ledger();
            ledger.Record(10, 100, sent: false);

            // Closed, and opened again under a new id.
            p.Paths.Remove(10);
            ledger.Sweep();
            p.Paths[20] = Browser;
            ledger.Record(20, 200, sent: false);

            var row = Assert.Single(ledger.Snapshot());
            Assert.Equal(300, row.Received);
        }

        [Fact]
        public void A_reused_process_id_is_not_charged_to_the_old_app()
        {
            var p = new Processes();
            p.Paths[10] = Browser;
            var ledger = p.Ledger();
            ledger.Record(10, 100, sent: true);

            // The browser exits and Windows gives its id to the editor.
            p.Paths[10] = Editor;
            ledger.Sweep();
            ledger.Record(10, 7, sent: true);

            var rows = ledger.Snapshot();
            Assert.Equal(100, rows.Single(r => r.Path == Browser).Sent);
            Assert.Equal(7, rows.Single(r => r.Path == Editor).Sent);
        }

        [Fact]
        public void Paths_match_without_regard_to_case()
        {
            var p = new Processes();
            p.Paths[10] = Browser;
            var ledger = p.Ledger();
            ledger.Record(10, 100, sent: true);

            p.Paths[10] = Browser.ToUpperInvariant();
            ledger.Sweep();
            ledger.Record(10, 1, sent: true);

            Assert.Equal(101, Assert.Single(ledger.Snapshot()).Sent);
        }

        [Fact]
        public void Service_hosts_are_kept_apart_by_the_services_they_run()
        {
            var p = new Processes();
            p.Paths[30] = SvcHost;
            p.Paths[31] = SvcHost;
            p.Services[30] = new[] { "UsoSvc", "DoSvc" };
            p.Services[31] = new[] { "Dnscache" };
            var ledger = p.Ledger();
            ledger.Record(30, 500, sent: false);
            ledger.Record(31, 5, sent: false);

            var rows = ledger.Snapshot();
            Assert.Equal(2, rows.Length);
            Assert.Equal(new[] { "DoSvc", "UsoSvc" }, rows[0].Services);
            Assert.Equal(new[] { "Dnscache" }, rows[1].Services);
        }

        [Fact]
        public void Services_are_only_looked_up_for_service_hosts()
        {
            var p = new Processes();
            p.Paths[10] = Browser;
            var ledger = p.Ledger();
            ledger.Record(10, 1, sent: true);

            ledger.Sweep();
            ledger.Sweep();

            Assert.Equal(0, p.ServiceLookups);
            Assert.Empty(Assert.Single(ledger.Snapshot()).Services);
        }

        [Fact]
        public void Rows_come_most_data_first_and_silent_apps_are_left_out()
        {
            var p = new Processes();
            p.Paths[10] = Browser;
            p.Paths[11] = Editor;
            var ledger = p.Ledger();
            ledger.Record(10, 10, sent: true);
            ledger.Record(11, 10_000, sent: false);
            ledger.Record(12, 0, sent: false);

            var rows = ledger.Snapshot();
            Assert.Equal(new[] { Editor, Browser }, rows.Select(r => r.Path));
        }

        [Fact]
        public void A_process_gone_before_it_was_looked_up_still_counts()
        {
            var p = new Processes();
            var ledger = p.Ledger();
            ledger.Record(99, 42, sent: true);

            var row = Assert.Single(ledger.Snapshot());
            Assert.Equal(string.Empty, row.Path);
            Assert.Equal(42, row.Sent);
        }

        [Fact]
        public void The_reply_survives_the_pipe()
        {
            var since = new DateTime(2026, 11, 22, 9, 14, 0);
            var reply = TwMessageGetTraffic.CreateRequest().CreateResponse(true, since, new[]
            {
                new AppTraffic { Path = SvcHost, Services = new[] { "DoSvc" }, Sent = 1, Received = 5_000_000_000 },
            });

            var bytes = SerializationHelper.Serialize<TwMessage>(reply);
            var back = Assert.IsType<TwMessageGetTraffic>(SerializationHelper.Deserialize<TwMessage>(bytes, TwMessageError.Instance));

            Assert.True(back.Available);
            Assert.Equal(since, back.Since);
            var app = Assert.Single(back.Apps);
            Assert.Equal(SvcHost, app.Path);
            Assert.Equal(new[] { "DoSvc" }, app.Services);
            Assert.Equal(5_000_000_000, app.Received);
        }

        [Theory]
        [InlineData(0, "0 B")]
        [InlineData(1023, "1023 B")]
        [InlineData(1024, "1 KB")]
        [InlineData(1536, "1.5 KB")]
        [InlineData(150 * 1024, "150 KB")]
        [InlineData(1024 * 1024 - 1, "1 MB")]
        [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
        public void Bytes_are_written_short(long bytes, string expected)
        {
            Assert.Equal(expected, TrafficFormat.Bytes(bytes, CultureInfo.InvariantCulture));
        }
    }
}
