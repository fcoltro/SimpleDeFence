using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleDeFence.DatabaseClasses;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// What the GUI needs from the service. Exists so the screens can run against sample data
    /// while the real client is blocked by AuthAsServer - without it, none of
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
        /// <summary>learningMinutes only matters for a switch to Learning: the service switches
        /// back to Normal by itself after that many minutes. Zero leaves Learning on until the
        /// next switch.</summary>
        Task<MessageType> SwitchModeAsync(FirewallMode mode, int learningMinutes = 0);

        /// <summary>Blocked/Connected/Open, gathered fresh on every call - no caching, this is a
        /// point-in-time view of live network state.</summary>
        Task<ConnectionsSnapshot> GetConnectionsAsync();

        /// <summary>Commits a new exception for the given subject with the given policy, lasting as
        /// long as <paramref name="timer"/> says (permanent unless told otherwise).</summary>
        Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy, AppExceptionTimer timer = AppExceptionTimer.Permanent);

        /// <summary>Locks the configuration server-side. A no-op on the wire (still returns
        /// MessageType.LOCK) when no password is set - PasswordLock's own Locked setter is gated
        /// on HasPassword server-side, so this is inert rather than an error in that case; callers
        /// should disable the "Lock now" action when State.HasPassword is false.</summary>
        Task<MessageType> LockAsync();

        /// <summary>Attempts to unlock the configuration with the given password. Success is
        /// exactly MessageType.UNLOCK; anything else (including a wrong password) is a failure to
        /// show as one.</summary>
        Task<MessageType> UnlockAsync(string password);

        /// <summary>Sets, changes, or clears (empty string) the password protecting the
        /// configuration. Success is exactly MessageType.SET_PASSPHRASE.</summary>
        Task<MessageType> SetPasswordAsync(string password);

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

        /// <summary>"Stop it trying": the services, scheduled tasks and startup entries from the
        /// same publisher as <paramref name="appPath"/>. Null when the service did not answer.
        /// An empty Publisher in the reply means the program's publisher could not be told.</summary>
        Task<TwMessageBackgroundItems?> GetBackgroundItemsAsync(string appPath);

        /// <summary>Switches the listed items on or off. Result is SET_BACKGROUND_ITEMS on
        /// success, with Items the list as it now stands; anything else is a failure to show.</summary>
        Task<(MessageType Result, TwMessageBackgroundItems? Items)> SetBackgroundItemsAsync(string appPath, BackgroundItemChange[] changes);
    }
}
