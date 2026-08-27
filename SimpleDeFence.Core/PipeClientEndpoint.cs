using System;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using SimpleDeFence.Utilities;

namespace SimpleDeFence
{
    public class PipeClientEndpoint
    {
        private readonly object SenderSyncRoot = new();
        private readonly string m_PipeName;

        public PipeClientEndpoint(string clientPipeName)
        {
            m_PipeName = clientPipeName;
        }

        private void SendRequest(TwRequest req)
        {
            TwMessage ret = TwMessageComError.Instance;
            lock (SenderSyncRoot)
            {
                // In case of a communication error,
                // retry a small number of times.
                for (int i = 0; i < 2; ++i)
                {
                    var resp = SendRequest(req.Request);
                    if (resp.Type != MessageType.COM_ERROR)
                    {
                        ret = resp;
                        break;
                    }

                    Thread.Sleep(200);
                }
            }

            req.Response = ret;
        }

        private TwMessage SendRequest(TwMessage msg)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream (".", m_PipeName, PipeDirection.InOut, PipeOptions.WriteThrough);
                pipeClient.Connect(1000);
                pipeClient.ReadMode = PipeTransmissionMode.Message;

                // Nothing is written until the far end is known to be the service. The server has
                // always checked the client; the reverse check did not exist, so a local process
                // that got hold of the pipe name received whatever the GUI said next - and the
                // unlock message carries the configuration passphrase in cleartext, which is the
                // one secret that protects everything else. Connecting is not authentication.
                //
                // The server side now holds its instance permanently so the name cannot be stolen
                // while the service runs, but this end must still stand on its own: the service
                // might be stopped, or not yet started, and in that window the name really is free
                // for anyone to take.
                if (!IsServedByLocalSystem(pipeClient))
                    return TwMessageComError.Instance;

                // Send command
                SerializationHelper.SerializeToPipe<TwMessage>(pipeClient, msg);

                // Get response
                return SerializationHelper.DeserializeFromPipe<TwMessage>(pipeClient, 20000, TwMessageComError.Instance);
            }
            catch
            {
                return TwMessageComError.Instance;
            }
        }

        /// <summary>
        /// True only when the connected pipe was created by the service's own account.
        ///
        /// The owner of the pipe object is the account that created it, which for a service running
        /// as LocalSystem is S-1-5-18. Administrators is accepted alongside it because a token
        /// whose owner is set to the Administrators group - the default for an elevated process -
        /// stamps that group as the owner instead; both are accounts an unprivileged attacker
        /// cannot create an object as, which is the property being relied on here.
        ///
        /// Any failure to determine the owner is a refusal, not a pass. Not being able to tell who
        /// is on the other end is exactly the case this exists to catch.
        /// </summary>
        private static bool IsServedByLocalSystem(NamedPipeClientStream pipe)
        {
            // Every refusal is traced. This check gates all traffic to the service, so if it ever
            // says no when it should not, the symptom is the GUI reporting that it cannot reach the
            // firewall - indistinguishable from the service being stopped. Reading the owner needs
            // READ_CONTROL, which the client handle does hold (PipeDirection.InOut opens for
            // GENERIC_READ, and STANDARD_RIGHTS_READ is READ_CONTROL) and which the server's ACL
            // does grant (PipeAccessRights.ReadWrite includes ReadPermissions) - but a trace beats
            // reasoning if this ever misbehaves in the field.
            try
            {
                var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner is null)
                {
                    System.Diagnostics.Debug.WriteLine("Control pipe refused: its owner could not be determined.");
                    return false;
                }

                if (owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
                    || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                {
                    return true;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"Control pipe refused: owned by {owner.Value}, which is neither LocalSystem nor Administrators.");
                return false;
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"Control pipe refused: could not read its owner. {e.Message}");
                return false;
            }
        }

        public TwRequest QueueMessage(TwMessage msg)
        {
            var req = new TwRequest(msg);
            SendRequest(req);
            return req;
        }
    }
}
