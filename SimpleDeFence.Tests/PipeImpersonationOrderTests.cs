using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Tasks;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>
    /// Pins the ordering constraint that broke the control pipe in 0.1.1.
    ///
    /// PipeServerEndpoint authorized the caller by impersonating it (RunAsClient) and then read the
    /// request. Windows does not allow that order: ImpersonateNamedPipeClient fails until the server
    /// has read from the pipe, so every request threw, every throw was treated as a refusal, and the
    /// service refused 100% of control traffic while still reporting itself as Running.
    ///
    /// These tests exercise the same API the endpoint uses, so they fail if the order is ever
    /// reversed again. Both ends run as the same user, so no elevation is needed.
    /// </summary>
    public class PipeImpersonationOrderTests
    {
        private static string NewPipeName() => "SimpleDeFenceTest_" + Guid.NewGuid().ToString("N");

        /// <summary>Runs a client that writes one message, against a server that hands the connected
        /// stream to <paramref name="onConnected"/>.</summary>
        private static void WithConnectedPipe(Action<NamedPipeServerStream> onConnected)
        {
            string name = NewPipeName();

            using var server = new NamedPipeServerStream(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.WriteThrough);

            var clientTask = Task.Run(() =>
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.WriteThrough);
                client.Connect(10_000);
                client.ReadMode = PipeTransmissionMode.Message;
                var payload = new byte[] { 1, 2, 3, 4 };
                client.Write(payload, 0, payload.Length);
                client.Flush();
                // Hold the connection open until the server is done inspecting it.
                client.ReadByte();
            });

            server.WaitForConnection();
            server.ReadMode = PipeTransmissionMode.Message;

            try
            {
                onConnected(server);
            }
            finally
            {
                try { server.WriteByte(0); } catch { }
                try { clientTask.Wait(10_000); } catch { }
            }
        }

        [Fact]
        public void Impersonating_before_reading_the_request_fails()
        {
            WithConnectedPipe(server =>
            {
                // This is exactly what the broken endpoint did: authorize first, read afterwards.
                var ex = Assert.Throws<IOException>(() => server.RunAsClient(() => { }));
                Assert.Contains("until data has been read", ex.Message, StringComparison.OrdinalIgnoreCase);
            });
        }

        [Fact]
        public void Impersonating_after_reading_the_request_succeeds()
        {
            WithConnectedPipe(server =>
            {
                var buffer = new byte[64];
                int read = server.Read(buffer, 0, buffer.Length);
                Assert.Equal(4, read);

                string? impersonated = null;
                server.RunAsClient(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    impersonated = identity.Name;
                });

                Assert.False(string.IsNullOrEmpty(impersonated));
            });
        }
    }
}
