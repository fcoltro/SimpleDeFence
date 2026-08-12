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
        /// The one commit path: clone the cached config, mutate the whole clone, put it back. A
        /// returned type of PUT_SETTINGS alone is NOT sufficient to mean the change took - the
        /// service can reply PUT_SETTINGS while having applied nothing when the caller's
        /// changeset was stale (TwMessagePutSettings.Warning). Implementations translate that
        /// case to MessageType.RESPONSE_STALE_CHANGESET instead, so "PUT_SETTINGS and only
        /// PUT_SETTINGS" is the caller's complete success check; every other value (including
        /// RESPONSE_STALE_CHANGESET, locked, or unrecognised) is a failure to show as one.
        /// Callers that only need the active profile (the common case) write
        /// `config => mutate(config.ActiveProfile)`.
        /// </summary>
        Task<MessageType> CommitConfigChangesAsync(Action<ServerConfiguration> mutate);

        /// <summary>The bundled app database (special-exception definitions), or null when the
        /// file is absent/unreadable - a missing database is a normal state, not an error.</summary>
        Task<AppDatabase?> GetAppDatabaseAsync();

        /// <summary>Running processes with resolved paths, for the process picker.</summary>
        Task<IReadOnlyList<ProcessListEntry>> GetRunningProcessesAsync();

        /// <summary>Visible top-level windows, for the window picker.</summary>
        Task<IReadOnlyList<WindowListEntry>> GetTopLevelWindowsAsync();
    }
}
