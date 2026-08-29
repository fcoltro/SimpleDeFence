using System;
using System.Collections.Generic;

namespace SimpleDeFence.UI.Services
{
    /// <summary>One row in the Connected or Open section: a live TCP/UDP endpoint.</summary>
    internal sealed class ConnectionRow
    {
        public uint ProcessId { get; init; }
        public string AppName { get; init; } = string.Empty;
        public string AppPath { get; init; } = string.Empty;
        public string Protocol { get; init; } = string.Empty;
        public string LocalAddress { get; init; } = string.Empty;
        public int LocalPort { get; init; }
        public string RemoteAddress { get; init; } = string.Empty;
        public int RemotePort { get; init; }
        public string State { get; init; } = string.Empty;
    }

    /// <summary>One row in the Blocked section - enough to build an "Allow this app" exception.</summary>
    internal sealed class BlockedRow
    {
        public DateTime Timestamp { get; init; }
        public uint ProcessId { get; init; }
        public string AppName { get; init; } = string.Empty;
        public string? AppPath { get; init; }
        public string? PackageId { get; init; }
        public string Protocol { get; init; } = string.Empty;
        public string Direction { get; init; } = string.Empty;
        public string RemoteAddress { get; init; } = string.Empty;
        public int RemotePort { get; init; }
    }

    /// <summary>One row of the process picker: a running process with its resolved path.</summary>
    internal sealed class ProcessListEntry
    {
        public uint ProcessId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
    }

    /// <summary>One row of the window picker: a visible top-level window and its process.</summary>
    internal sealed class WindowListEntry
    {
        public string Title { get; init; } = string.Empty;
        public uint ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string ProcessPath { get; init; } = string.Empty;
    }

    /// <summary>Everything the Connections screen renders in one refresh.</summary>
    internal sealed class ConnectionsSnapshot
    {
        public IReadOnlyList<BlockedRow> Blocked { get; init; } = Array.Empty<BlockedRow>();

        /// <summary>
        /// True when the firewall log could not be read, so <see cref="Blocked"/> being empty says
        /// nothing about whether anything was blocked.
        ///
        /// An empty Blocked list is the reassuring outcome on a firewall, and the screen words it
        /// that way. It must therefore never be shown for a log that simply did not arrive, which
        /// is what happened whenever the READ_FW_LOG reply failed: the failure was indistinguishable
        /// from a quiet firewall, on a screen whose entire purpose is to let the user release what
        /// was blocked.
        /// </summary>
        public bool BlockedUnavailable { get; init; }
        public IReadOnlyList<ConnectionRow> Connected { get; init; } = Array.Empty<ConnectionRow>();
        public IReadOnlyList<ConnectionRow> Open { get; init; } = Array.Empty<ConnectionRow>();
    }
}
