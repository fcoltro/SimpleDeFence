using System;
using SimpleDeFence;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>
    /// "The log said nothing was blocked" and "the log never arrived" are different answers, and
    /// the Connections screen words one of them reassuringly. Conflating them is what let a
    /// firewall that was blocking everything report an empty Blocked list with no error.
    /// </summary>
    public class ReadFwLogResponseTests
    {
        private static FirewallLogEntry Entry() => new()
        {
            Timestamp = DateTime.Now,
            Event = EventLogEvent.BLOCKED,
            Protocol = Protocol.TCP,
            Direction = RuleDirection.Out,
            LocalIp = "10.0.0.5",
            RemoteIp = "1.2.3.4",
            LocalPort = 51000,
            RemotePort = 443,
            AppPath = @"C:\app.exe",
        };

        [Fact]
        public void A_communication_error_is_not_an_empty_log()
        {
            // The reply the pipe client synthesises when the request failed - a timeout reading a
            // large response, or a refused connection. Returning an empty array here is what made
            // the failure indistinguishable from a quiet firewall.
            Assert.Null(Controller.EndReadFwLog(TwMessageComError.Instance));
        }

        [Fact]
        public void An_unrelated_reply_is_not_an_empty_log()
        {
            Assert.Null(Controller.EndReadFwLog(TwMessageError.Instance));
        }

        [Fact]
        public void A_log_reply_returns_its_entries()
        {
            var resp = new TwMessageReadFwLog(new[] { Entry() });

            var log = Controller.EndReadFwLog(resp);

            Assert.NotNull(log);
            Assert.Single(log!);
        }

        [Fact]
        public void A_genuinely_empty_log_is_distinguishable_from_a_failure()
        {
            var resp = new TwMessageReadFwLog(Array.Empty<FirewallLogEntry>());

            var log = Controller.EndReadFwLog(resp);

            // Empty, but present - the firewall really did report nothing blocked.
            Assert.NotNull(log);
            Assert.Empty(log!);
        }
    }
}
