using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Owns the IPC conversation with the service. Pages share one instance so they see the same
    /// cached config/state and the same changeset - the equivalent of the single Controller the
    /// WinForms GUI keeps in GlobalInstances.
    /// </summary>
    internal sealed class FirewallClient
    {
        // Same pipe name the WinForms controller uses (GlobalInstances.cs) - this talks to the
        // existing, unchanged C# service over its current IPC protocol.
        private const string PipeName = "SimpleDeFenceController";

        private readonly Controller _controller = new(PipeName);
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Guid _changeset = Guid.Empty;

        public ServerConfiguration? Config { get; private set; }
        public ServerState? State { get; private set; }
        public bool Connected { get; private set; }
        public string? LastError { get; private set; }

        /// <summary>Raised after every refresh so open pages can redraw.</summary>
        public event EventHandler? Changed;

        public async Task RefreshAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                // The pipe call blocks, so keep it off the UI thread.
                var result = await Task.Run(() =>
                {
                    var changeset = _changeset;
                    var type = _controller.GetServerConfig(out var config, out var state, ref changeset);
                    return (Type: type, Changeset: changeset, Config: config, State: state);
                }).ConfigureAwait(true);

                _changeset = result.Changeset;

                // Config and state only come back when the changeset moved. Nulls on an otherwise
                // good response mean "nothing changed", so keep what we already had.
                if (result.Config is not null)
                    Config = result.Config;
                if (result.State is not null)
                    State = result.State;

                Connected = result.Type == MessageType.GET_SETTINGS;
                LastError = Connected
                    ? null
                    : "Could not reach the SimpleDeFence service. Is it installed and running?";
            }
            catch (Exception ex)
            {
                Connected = false;
                LastError = ex.Message;
            }
            finally
            {
                _gate.Release();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<MessageType> SwitchModeAsync(FirewallMode mode)
            => Task.Run(() => _controller.SwitchFirewallMode(mode));
    }
}
