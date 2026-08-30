using System;
using System.IO.Pipes;
using System.Threading.Tasks;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>
    /// Pins that a reply larger than the pipe's kernel buffer reaches the client whole.
    ///
    /// PipeServerEndpoint wrote the response and then let its finally block disconnect.
    /// Disconnect() discards whatever the client has not read yet, and Flush() only pushes bytes
    /// as far as the 20 KB kernel buffer, so every reply bigger than that was truncated in flight.
    /// Small replies won the race, which is why the control pipe looked healthy; READ_FW_LOG
    /// carries the entire firewall event ring in one message and never did. The Connections
    /// screen's Blocked list was the visible casualty.
    ///
    /// Same hard-timeout discipline as PipeImpersonationOrderTests: a test that pins a blocking
    /// API has to fail fast rather than hang - a deadlock here once ran a CI job to its ceiling.
    /// </summary>
    public class PipeLargeResponseTests
    {
        private const int TimeoutMs = 30_000;

        /// <summary>Comfortably past the 20 KB buffer the service gives its pipe, and about the
        /// size of a real 500-entry firewall log.</summary>
        private const int EntryCount = 500;

        [Fact]
        public void A_response_larger_than_the_pipe_buffer_arrives_intact()
        {
            var scenario = Task.Run(RoundTripLargeLog);

            Assert.True(
                scenario.Wait(TimeoutMs),
                $"The pipe scenario did not finish within {TimeoutMs} ms, which means it deadlocked.");

            var received = scenario.GetAwaiter().GetResult();

            Assert.NotNull(received);
            Assert.Equal(EntryCount, received!.Length);
            // Spot-check the far end of the payload: a truncated message loses the tail first.
            Assert.Equal("10.0.0.1", received[EntryCount - 1].RemoteIp);
        }

        private static FirewallLogEntry[]? RoundTripLargeLog()
        {
            string name = "SimpleDeFenceTest_" + Guid.NewGuid().ToString("N");

            // The service's own pipe settings, buffer sizes included - those are the point here.
            using var server = new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
                PipeOptions.WriteThrough, 2048 * 10, 2048 * 10);

            FirewallLogEntry[]? received = null;

            var client = Task.Run(() =>
            {
                using var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.WriteThrough);
                stream.Connect(TimeoutMs);
                stream.ReadMode = PipeTransmissionMode.Message;

                SerializationHelper.SerializeToPipe<TwMessage>(stream, TwMessageReadFwLog.CreateRequest());

                var resp = SerializationHelper.DeserializeFromPipe<TwMessage>(stream, TimeoutMs, TwMessageComError.Instance);
                received = Controller.EndReadFwLog(resp);
            });

            server.WaitForConnection();
            server.ReadMode = PipeTransmissionMode.Message;

            // Read the request, then answer it exactly as PipeServerEndpoint does.
            SerializationHelper.DeserializeFromPipe<TwMessage>(server, TimeoutMs, TwMessageComError.Instance);
            SerializationHelper.SerializeToPipe<TwMessage>(server, new TwMessageReadFwLog(BuildLog()));

            // The line under test. Without it the disconnect below discards the tail of the reply.
            server.WaitForPipeDrain();

            if (server.IsConnected)
                server.Disconnect();

            Assert.True(client.Wait(TimeoutMs), "The client did not finish reading the response in time.");
            client.GetAwaiter().GetResult();

            return received;
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
                    // The last entry is the one the assertion checks for.
                    RemoteIp = i == EntryCount - 1 ? "10.0.0.1" : "142.250.219." + (i % 255),
                    LocalPort = 50000 + i,
                    RemotePort = 443,
                    AppPath = @"C:\Program Files\Mozilla Firefox\firefox.exe",
                };
            return entries;
        }
    }
}
