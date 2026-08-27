using System;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using SimpleDeFence.Utilities;

namespace SimpleDeFence
{
    internal delegate TwMessage PipeDataReceived(TwMessage req);

    internal class PipeServerEndpoint : Disposable
    {
        private readonly Thread m_PipeWorkerThread;
        private readonly PipeDataReceived m_RcvCallback;
        private readonly string m_PipeName;

        private bool m_Run = true;

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            m_Run = false;

            // Create a dummy connection so that worker thread gets out of the infinite WaitForConnection()
            //
            // Guarded, because this can legitimately fail and must not throw out of Dispose during
            // service shutdown. The single instance is now permanent rather than recreated per
            // request, so if the worker happens to be mid-request when this runs, the instance is
            // busy and Connect times out instead of connecting. The Join below still bounds the
            // wait, and the worker leaves on its own once m_Run is false.
            try
            {
                using var npcs = new NamedPipeClientStream(m_PipeName);
                npcs.Connect(500);
            }
            catch
            {
            }

            if (disposing)
            {
                // Release managed resources
                m_PipeWorkerThread.Join(TimeSpan.FromMilliseconds(1000));
            }

            // Release unmanaged resources.
            // Set large fields to null.
            // Call Dispose on your base class.
            base.Dispose(disposing);
        }

        internal PipeServerEndpoint(PipeDataReceived recvCallback, string serverPipeName)
        {
            m_RcvCallback = recvCallback;
            m_PipeName = serverPipeName;

            m_PipeWorkerThread = new Thread(new ThreadStart(PipeServerWorker))
            {
                Name = "ServerPipeWorker",
                IsBackground = true
            };
            m_PipeWorkerThread.Start();
        }

        private void PipeServerWorker()
        {
            // Allow authenticated users access to the pipe
            SecurityIdentifier AuthenticatedSID = new(WellKnownSidType.AuthenticatedUserSid, null);
            PipeAccessRule par = new(AuthenticatedSID, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow);
            PipeSecurity ps = new();
            ps.AddAccessRule(par);

            // Created ONCE, before the loop, and kept for the life of the service.
            //
            // This used to be created inside the loop under a `using`, with a single allowed
            // instance - so the pipe was destroyed and rebuilt for every request, and between the
            // dispose at the bottom of one iteration and the create at the top of the next, the
            // name \\.\pipe\SimpleDeFenceController belonged to nobody. Any local process could
            // claim it in that gap, and the 200 ms sleep on the error path - which ran with the
            // instance already disposed - widened the gap from microseconds to something trivially
            // winnable by a polling loop. Whoever won it received the GUI's next message, and the
            // unlock message carries the configuration passphrase in cleartext.
            //
            // FirstPipeInstance is the other half: it makes creation fail outright if the name is
            // already taken, rather than quietly joining someone else's pipe as a second instance.
            // If that happens at startup, something is already squatting the name and the only safe
            // thing to do is refuse to serve rather than compete for connections.
            NamedPipeServerStream pipeServer;
            try
            {
                pipeServer = NamedPipeServerStreamAcl.Create(
                    m_PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
                    PipeOptions.WriteThrough | PipeOptions.FirstPipeInstance,
                    2048 * 10, 2048 * 10, ps);
            }
            catch (Exception e)
            {
                Utils.Log($"Could not create the control pipe '{m_PipeName}'. Another process is already holding that name, "
                    + "so no client requests will be served. For details see the next log entry.", Utils.LOG_ID_SERVICE);
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                return;
            }

            using (pipeServer)
            {
                while (m_Run)
                {
                    try
                    {
                        pipeServer.WaitForConnection();
                        pipeServer.ReadMode = PipeTransmissionMode.Message;

                        if (AuthAsServer(pipeServer))
                        {
                            var req = SerializationHelper.DeserializeFromPipe<TwMessage>(pipeServer, 3000, TwMessageComError.Instance);
                            var resp = m_RcvCallback(req);
                            SerializationHelper.SerializeToPipe(pipeServer, resp);
                        }
                    }
                    catch
                    {
                        // No sleep here any more. It existed to space out retries of the create
                        // above, which no longer happens - and because the instance now survives
                        // the error, sleeping would only delay the next legitimate client while
                        // holding the name we already own.
                    }
                    finally
                    {
                        // Back to listening on the same instance, without ever releasing the name.
                        try
                        {
                            if (pipeServer.IsConnected)
                                pipeServer.Disconnect();
                        }
                        catch
                        {
                        }
                    }
                } //while
            }
        }

        private static bool AuthAsServer(PipeStream stream)
        {
#if !DEBUG
            if (!Utils.SafeNativeMethods.GetNamedPipeClientProcessId(stream.SafePipeHandle.DangerousGetHandle(), out ulong clientPid))
                return false;

            string clientFilePath = Utils.GetPathOfProcess((uint)clientPid);

            return clientFilePath.Equals(SimpleDeFence.Windows.ProcessManager.ExecutablePath, StringComparison.OrdinalIgnoreCase);
#else
            return true;
#endif
        }
    }
}
