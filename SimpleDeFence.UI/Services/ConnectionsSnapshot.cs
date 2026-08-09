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

    /// <summary>Everything the Connections screen renders in one refresh.</summary>
    internal sealed class ConnectionsSnapshot
    {
        public IReadOnlyList<BlockedRow> Blocked { get; init; } = Array.Empty<BlockedRow>();
        public IReadOnlyList<ConnectionRow> Connected { get; init; } = Array.Empty<ConnectionRow>();
        public IReadOnlyList<ConnectionRow> Open { get; init; } = Array.Empty<ConnectionRow>();
    }
}
