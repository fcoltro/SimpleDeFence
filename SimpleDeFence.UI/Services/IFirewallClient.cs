using System;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// What the GUI needs from the service. Exists so the screens can run against sample data
    /// while the real client is blocked by AuthAsServer (see ROADMAP.md) - without it, none of
    /// these screens could be run or visually verified while being built.
    /// </summary>
    internal interface IFirewallClient
    {
        ServerConfiguration? Config { get; }
        ServerState? State { get; }
        bool Connected { get; }
        string? LastError { get; }

        /// <summary>Raised after every refresh so open pages can redraw.</summary>
        event EventHandler? Changed;

        Task RefreshAsync();
        Task<MessageType> SwitchModeAsync(FirewallMode mode);

        /// <summary>Blocked/Connected/Open, gathered fresh on every call - no caching, this is a
        /// point-in-time view of live network state.</summary>
        Task<ConnectionsSnapshot> GetConnectionsAsync();

        /// <summary>Commits a new exception for the given subject with the given policy.</summary>
        Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy);
    }
}
