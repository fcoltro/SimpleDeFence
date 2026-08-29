using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>
    /// Pins the ordering constraint that broke the control pipe in 0.1.1.
    ///
    /// PipeServerEndpoint authorized the caller by impersonating it (RunAsClient) and only then
    /// read the request. Windows does not allow that order: ImpersonateNamedPipeClient fails until
    /// the server has read from the pipe. Every request therefore threw, every throw was treated as
    /// a refusal, and the service refused 100% of control traffic while still reporting itself as
    /// Running - the GUI could not connect, and the MSI uninstall could not stop the service and
    /// rolled back.
    ///
    /// The two tests are a matched pair: the same call fails before a read and succeeds after one,
    /// which is the causal claim the fix rests on. Both ends run as the same user, so no elevation
    /// is needed.
    /// </summary>
    public class PipeImpersonationOrderTests
    {
        private const int TimeoutMs = 15_000;

        [Fact]
        public void Impersonating_before_reading_the_request_fails()
        {
            RunScenario(server => Assert.Throws<IOException>(() => server.RunAsClient(() => { })));
        }

        [Fact]
        public void Impersonating_after_reading_the_request_succeeds()
        {
            RunScenario(server =>
            {
                var buffer = new byte[64];
                Assert.Equal(4, server.Read(buffer, 0, buffer.Length));

                string? impersonated = null;
                server.RunAsClient(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    impersonated = identity.Name;
                });

                Assert.False(string.IsNullOrEmpty(impersonated));
            });
        }

        /// <summary>
        /// Runs <paramref name="onConnected"/> against a connected pipe, under a hard timeout.
        ///
        /// The timeout is the point. The first version of this file deadlocked and ran a CI job to
        /// the six-hour ceiling; a test that pins a blocking API has to fail fast rather than hang,
        /// so the scenario runs on a worker and a stall becomes an assertion failure.
        /// </summary>
        private static void RunScenario(Action<NamedPipeServerStream> onConnected)
        {
            var scenario = Task.Run(() => Scenario(onConnected));

            Assert.True(
                scenario.Wait(TimeoutMs),
                $"The pipe scenario did not finish within {TimeoutMs} ms, which means it deadlocked.");

            // Surfaces whatever the scenario threw, including the assertions inside onConnected.
            scenario.GetAwaiter().GetResult();
        }

        private static void Scenario(Action<NamedPipeServerStream> onConnected)
        {
            string name = "SimpleDeFenceTest_" + Guid.NewGuid().ToString("N");
            using var release = new ManualResetEventSlim(false);

            // The service's own pipe settings, buffer sizes included - and the buffer sizes are the
            // part that matters here. The constructor overload without them defaults both to 0,
            // which makes every write block until a reader consumes it, and the first test above is
            // built on a server that deliberately does not read. That wedged the client's 4-byte
            // write and deadlocked the scenario. The service passes 2048 * 10 and never sees it.
            using var server = new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
                PipeOptions.WriteThrough, 2048 * 10, 2048 * 10);

            var client = Task.Run(() =>
            {
                using var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.WriteThrough);
                stream.Connect(TimeoutMs);
                stream.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
                stream.Flush();

                // The connection is held open by an event rather than by a pipe read, so the client
                // can never block on the server. That is what turns a server-side failure into a
                // reported test failure instead of a hang.
                release.Wait(TimeoutMs);
            });

            try
            {
                server.WaitForConnection();
                server.ReadMode = PipeTransmissionMode.Message;
                onConnected(server);
            }
            finally
            {
                release.Set();
                client.Wait(TimeoutMs);
            }
        }
    }
}
