using System;
using System.Collections.Generic;
using SimpleDeFence.Utilities;

namespace SimpleDeFence
{
    public sealed class Controller
    {
        private readonly PipeClientEndpoint Endpoint;

        public Controller(string serverEndpoint)
        {
            Endpoint = new PipeClientEndpoint(serverEndpoint);
        }

        public MessageType GetServerConfig(out ServerConfiguration? serverConfig, out ServerState? serverState, ref Guid clientChangeset)
        {
            // Detect if server settings have changed in comparison to ours and download
            // settings only if we need them. Settings are "version numbered" using the "changeset"
            // property. We send our changeset number to the service and if it differs from his,
            // the service will send back the settings.

            serverConfig = null;
            serverState = null;

            var resp = Endpoint.QueueMessage(TwMessageGetSettings.CreateRequest(clientChangeset)).Response;
            if (resp.Type == MessageType.GET_SETTINGS)
            {
                var respArgs = (TwMessageGetSettings)resp;
                if (respArgs.Changeset != clientChangeset)
                {
                    clientChangeset = respArgs.Changeset;
                    serverConfig = respArgs.Config;
                    serverState = respArgs.State;
                }
            }

            return resp.Type;
        }

        public TwMessage SetServerConfig(ServerConfiguration serverConfig, Guid clientChangeset)
        {
            return Endpoint.QueueMessage(TwMessagePutSettings.CreateRequest(clientChangeset, serverConfig)).Response;
        }

        public TwRequest BeginReadFwLog()
        {
            return Endpoint.QueueMessage(TwMessageReadFwLog.CreateRequest());
        }

        /// <summary>
        /// The firewall log from a READ_FW_LOG reply, or null when the service did not return one.
        ///
        /// Null rather than an empty array, because the difference is the whole meaning of the
        /// Blocked list. This used to answer both cases with Array.Empty and a TODO asking whether
        /// the user should be told; the Connections screen therefore rendered "nothing blocked" -
        /// the reassuring reading - whenever the log could not be fetched at all. A response that
        /// times out, or a pipe error, looked exactly like a quiet firewall.
        ///
        /// That is not theoretical: the reply carries the entire event ring as one pipe message,
        /// and a large enough ring stops arriving within the read timeout, silently and for ever.
        /// Callers must tell the two apart.
        /// </summary>
        public static FirewallLogEntry[]? EndReadFwLog(TwMessage twResp)
        {
            if (twResp is TwMessageReadFwLog fwLog)
                return fwLog.Entries;

            return null;
        }

        public MessageType SwitchFirewallMode(FirewallMode mode)
        {
            return Endpoint.QueueMessage(TwMessageModeSwitch.CreateRequest(mode)).Response.Type;
        }

        public MessageType RequestServerStop()
        {
            return Endpoint.QueueMessage(TwMessageSimple.CreateRequest(MessageType.STOP_SERVICE)).Response.Type;
        }

        public bool IsServerLocked
        {
            get
            {
                var resp = Endpoint.QueueMessage(TwMessageIsLocked.CreateRequest()).Response;
                if (resp is TwMessageIsLocked isLockedResp)
                    return isLockedResp.LockedStatus;
                else
                    return false;
            }
        }

        public MessageType SetPassphrase(string pwd)
        {
            return Endpoint.QueueMessage(TwMessageSetPassword.CreateRequest(pwd)).Response.Type;
        }

        public MessageType TryUnlockServer(string pwd)
        {
            return Endpoint.QueueMessage(TwMessageUnlock.CreateRequest(pwd)).Response.Type;
        }

        public MessageType LockServer()
        {
            return Endpoint.QueueMessage(TwMessageSimple.CreateRequest(MessageType.LOCK)).Response.Type;
        }

        public string TryGetProcessPath(uint pid)
        {
            var resp = Endpoint.QueueMessage(TwMessageGetProcessPath.CreateRequest(pid)).Response;
            if (resp.Type == MessageType.GET_PROCESS_PATH)
            {
                var respArgs = (TwMessageGetProcessPath)resp;
                return respArgs.Path;
            }
            else
                return string.Empty;
        }
    }
}
