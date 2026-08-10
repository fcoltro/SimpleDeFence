using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleDeFence.DatabaseClasses;

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

        /// <summary>
        /// The one commit path: clone the cached config, mutate the clone's active profile, put
        /// it back. Only PUT_SETTINGS means the change took; anything else (locked, changeset
        /// conflict, unrecognised) is a failure the caller must show as one.
        /// </summary>
        Task<MessageType> CommitProfileChangesAsync(Action<ServerProfileConfiguration> mutate);

        /// <summary>The bundled app database (special-exception definitions), or null when the
        /// file is absent/unreadable - a missing database is a normal state, not an error.</summary>
        Task<AppDatabase?> GetAppDatabaseAsync();

        /// <summary>Running processes with resolved paths, for the process picker.</summary>
        Task<IReadOnlyList<ProcessListEntry>> GetRunningProcessesAsync();

        /// <summary>Visible top-level windows, for the window picker.</summary>
        Task<IReadOnlyList<WindowListEntry>> GetTopLevelWindowsAsync();
    }
}
