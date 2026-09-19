using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>
    /// Pins that a client which never reads its reply cannot park the server indefinitely.
    ///
    /// The service has exactly one pipe worker thread, so whatever blocks it blocks every client.
    /// Waiting for the client to take the reply is necessary - see PipeLargeResponseTests for why
    /// dropping that wait truncated every large response - but the wait has to be bounded, or the
    /// fix for a stuck GUI becomes a new way to produce one.
    ///
    /// Both halves of the delivery block, which is the part worth pinning. WaitForPipeDrain is the
    /// obvious one. Write is the one that is easy to miss: the pipe's kernel buffer is 20 KB, so a
    /// reply larger than that cannot be handed over at all until the client starts reading. This
    /// test uses a reply comfortably past that buffer, so it fails if only the drain were bounded.
    ///
    /// Same hard-timeout discipline as the other pipe tests: a test that pins blocking APIs has to
    /// fail fast rather than hang - a deadlock here once ran a CI job to its ceiling.
    /// </summary>
    public class PipeUnreadResponseTests
    {
        /// <summary>Outer bound. Generous against the delivery bound below, so a failure reads as
        /// "the delivery was not bounded" rather than as flakiness.</summary>
        private const int TimeoutMs = 30_000;

        /// <summary>What the server allows the client before giving up on it. Short, so the test
        /// costs a second rather than the production ten.</summary>
        private const int DeliveryTimeoutMs = 1_000;

        /// <summary>Well past the pipe's 20 KB buffer, so the write itself cannot complete while
        /// the client is not reading.</summary>
        private const int EntryCount = 500;

        [Fact]
        public void A_client_that_never_reads_does_not_park_the_server()
        {
            var scenario = Task.Run(DeliverToSilentClient);

            Assert.True(
                scenario.Wait(TimeoutMs),
                $"The server did not give up on a non-reading client within {TimeoutMs} ms, "
                + "which means the response delivery is unbounded and one client can wedge the pipe.");

            var elapsedMs = scenario.GetAwaiter().GetResult();

            // It must actually have waited - a delivery that returns instantly would be the old
            // truncation bug, not a bounded wait.
            Assert.True(elapsedMs >= DeliveryTimeoutMs,
                $"Delivery returned after {elapsedMs} ms, before the {DeliveryTimeoutMs} ms bound; "
                + "the server is not waiting for the client to read at all.");
        }

        /// <summary>Runs one request/response against a client that connects, asks, and then never
        /// reads. Returns how long the bounded delivery took.</summary>
        private static long DeliverToSilentClient()
        {
            string name = "SimpleDeFenceTest_" + Guid.NewGuid().ToString("N");

            // The service's own pipe settings, buffer sizes included - those are the point here.
            using var server = new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
                PipeOptions.WriteThrough, 2048 * 10, 2048 * 10);

            using var releaseClient = new ManualResetEventSlim(false);

            var client = Task.Run(() =>
            {
                using var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.WriteThrough);
                stream.Connect(TimeoutMs);
                stream.ReadMode = PipeTransmissionMode.Message;

                SerializationHelper.SerializeToPipe<TwMessage>(stream, TwMessageReadFwLog.CreateRequest());

                // The whole point: ask, then never read the answer. Held open until the server has
                // finished with it, so the server is timing out on a live connection rather than
                // on one that closed under it.
                releaseClient.Wait(TimeoutMs);
            });

            server.WaitForConnection();
            server.ReadMode = PipeTransmissionMode.Message;
            SerializationHelper.DeserializeFromPipe<TwMessage>(server, TimeoutMs, TwMessageComError.Instance);

            var stopwatch = Stopwatch.StartNew();
            SendResponseBounded(server, new TwMessageReadFwLog(BuildLog()));
            stopwatch.Stop();

            releaseClient.Set();

            if (server.IsConnected)
                server.Disconnect();

            Assert.True(client.Wait(TimeoutMs), "The client task did not finish in time.");
            client.GetAwaiter().GetResult();

            return stopwatch.ElapsedMilliseconds;
        }

        /// <summary>Mirrors PipeServerEndpoint.SendResponse, the same way the other pipe tests
        /// mirror the server loop rather than reaching into the service assembly.</summary>
        private static void SendResponseBounded(NamedPipeServerStream pipeServer, TwMessage resp)
        {
            var delivery = Task.Run(() =>
            {
                try
                {
                    SerializationHelper.SerializeToPipe(pipeServer, resp);
                    pipeServer.WaitForPipeDrain();
                }
                catch
                {
                    // Disconnected under us, or the client went away mid-write.
                }
            });

            delivery.Wait(DeliveryTimeoutMs);
        }

        private static FirewallLogEntry[] BuildLog()
        {
            var entries = new FirewallLogEntry[EntryCount];
            for (int i = 0; i < EntryCount; i++)
                entries[i] = new FirewallLogEntry
                {
                    Timestamp = DateTime.Now,
                    Event = EventLogEvent.BLOCKED,
                    Protocol = Protocol.TCP,
                    Direction = RuleDirection.Out,
                    LocalIp = "192.168.0.66",
                    RemoteIp = "142.250.219." + (i % 255),
                    LocalPort = 50000 + i,
                    RemotePort = 443,
                    AppPath = @"C:\Program Files\Mozilla Firefox\firefox.exe",
                };
            return entries;
        }
    }
}
