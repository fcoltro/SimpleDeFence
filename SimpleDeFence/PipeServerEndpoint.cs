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
            // Administrators and SYSTEM only.
            //
            // This used to admit Authenticated Users, which is every account that can log on -
            // standard users, remote desktop sessions, and any service running as NETWORK SERVICE
            // or a compromised worker process. The only thing standing between such a caller and
            // MODE_SWITCH, PUT_SETTINGS or STOP_SERVICE was AuthAsServer's image-path comparison,
            // which every copy of the GUI satisfies by construction because the GUI and the service
            // are the same executable.
            //
            // Narrowing it is possible because the shipped executable's app.manifest requests
            // highestAvailable: an administrator's copy of the GUI runs elevated and holds
            // Administrators in its token, so it still connects. (It said requireAdministrator when
            // this was written; that was relaxed later, and highestAvailable is what ships - see
            // SimpleDeFence/app.manifest, which is the manifest that reaches the binary, and
            // Elevation.cs for what the GUI does when it finds itself without the group.) A
            // standard user's copy therefore cannot reach the service at all, which is deliberate
            // and is what Elevation.IsElevated exists to explain to them.
            //
            // SYSTEM is listed explicitly because a DACL without it would lock the service out of
            // its own pipe - PipeServerEndpoint.Dispose connects a dummy client to wake the
            // worker, and that client is this process.
            //
            // A non-elevated caller is now refused by the operating system before a single byte is
            // read, which is a stronger boundary than any check this code could make afterwards.
            var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            PipeSecurity ps = new();
            ps.AddAccessRule(new PipeAccessRule(adminsSid, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(systemSid, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));

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

                        // The request is read BEFORE the caller is authorized, because Windows
                        // requires that order. ImpersonateNamedPipeClient - which is how the
                        // Administrators check in AuthAsServer establishes who is calling - fails
                        // with "Unable to impersonate using a named pipe until data has been read
                        // from that pipe" until the server has read from the connection.
                        //
                        // Authorizing first therefore threw on every single request, and
                        // CallerIsAdministrator treats a failure to impersonate as a refusal, so the
                        // service refused 100% of control traffic while still reporting itself as
                        // Running to the SCM. The GUI showed "cannot connect - is the service
                        // installed and running?", and the MSI uninstall could not stop the service
                        // and rolled back mid-progress.
                        //
                        // Reading first is safe. The DACL already restricts connections to
                        // Administrators and SYSTEM, the deserializer is bounded by a timeout and
                        // yields TwMessageComError on anything malformed, and authorization still
                        // gates everything that acts on the message: an unauthorized caller's
                        // request is never dispatched to m_RcvCallback and never gets a response.
                        var req = SerializationHelper.DeserializeFromPipe<TwMessage>(pipeServer, 3000, TwMessageComError.Instance);

                        if (AuthAsServer(pipeServer))
                        {
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

        /// <summary>
        /// Decides whether the connected client may issue commands.
        ///
        /// Two independent conditions, both required. The image-path comparison was here already;
        /// on its own it was never an authorization decision, because the GUI and the service are
        /// the same executable since the WinUI merge - so "the caller runs the same file we do" is
        /// satisfied by any copy of the shipped GUI, launched by anyone. It is kept as a cheap way
        /// to turn away an unrelated process, not as the boundary.
        ///
        /// The boundary is the second condition: impersonate the caller and require the
        /// Administrators role. That is deliberately evaluated against the caller's *effective*
        /// token, so an administrator running unelevated - whose Administrators membership is
        /// present but deny-only - is refused exactly like a standard user. The pipe's DACL already
        /// rejects both before they get this far; this is the check that does not depend on the
        /// DACL having been applied as intended.
        ///
        /// A failure to impersonate is a refusal. Not being able to establish who is calling is the
        /// case this exists to catch.
        /// </summary>
        private static bool AuthAsServer(NamedPipeServerStream stream)
        {
#if !DEBUG
            if (!Utils.SafeNativeMethods.GetNamedPipeClientProcessId(stream.SafePipeHandle.DangerousGetHandle(), out ulong clientPid))
                return false;

            string clientFilePath = Utils.GetPathOfProcess((uint)clientPid);
            if (!clientFilePath.Equals(SimpleDeFence.Windows.ProcessManager.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return false;

            return CallerIsAdministrator(stream);
#else
            return true;
#endif
        }

        private static bool CallerIsAdministrator(NamedPipeServerStream stream)
        {
            try
            {
                bool isAdmin = false;

                // RunAsClient impersonates the connected client on this thread for the duration of
                // the callback. The service runs as LocalSystem, which holds SeImpersonatePrivilege,
                // so this is available to it.
                stream.RunAsClient(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                });

                if (!isAdmin)
                    Utils.Log("Refused a control request from a caller that is not running elevated.", Utils.LOG_ID_SERVICE);

                return isAdmin;
            }
            catch (Exception e)
            {
                Utils.Log("Refused a control request: the caller could not be impersonated. "
                    + "For details see the next log entry.", Utils.LOG_ID_SERVICE);
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                return false;
            }
        }
    }
}
