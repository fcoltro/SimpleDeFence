using SimpleDeFence.Windows.NetStat;
using SimpleDeFence.Windows.Services;
using SimpleDeFence.DatabaseClasses;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Owns the IPC conversation with the service. Pages share one instance so they see the same
    /// cached config/state and the same changeset - the equivalent of the single Controller the
    /// WinForms GUI keeps in GlobalInstances.
    /// </summary>
    internal sealed class FirewallClient : IFirewallClient
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

        public Task<ConnectionsSnapshot> GetConnectionsAsync()
        {
            return Task.Run(() =>
            {
                // Fire the log request first so it overlaps the local NetStat/service-table
                // gathering below, the same overlap ConnectionsForm already relies on.
                var logRequest = _controller.BeginReadFwLog();

                var uwp = new UwpPackageList();
                var servicePids = new ServicePidMap();

                var connected = new List<ConnectionRow>();
                var open = new List<ConnectionRow>();
                CollectTcp(NetStat.GetExtendedTcp4Table(false), uwp, servicePids, connected, open);
                CollectTcp(NetStat.GetExtendedTcp6Table(false), uwp, servicePids, connected, open);
                CollectUdp(NetStat.GetExtendedUdp4Table(false), uwp, servicePids, open);
                CollectUdp(NetStat.GetExtendedUdp6Table(false), uwp, servicePids, open);

                var rawLog = Controller.EndReadFwLog(logRequest.Response);
                var recentBlocked = ConnectionActivity.RecentBlocked(rawLog, DateTime.Now, TimeSpan.FromMinutes(5));
                var blocked = recentBlocked.Select(entry => BlockedRowFrom(entry, uwp, servicePids)).ToList();

                return new ConnectionsSnapshot
                {
                    Blocked = blocked,
                    Connected = connected.OrderBy(r => r.AppName, StringComparer.CurrentCultureIgnoreCase).ToList(),
                    Open = open.OrderBy(r => r.AppName, StringComparer.CurrentCultureIgnoreCase).ToList(),
                };
            });
        }

        public Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy)
            => CommitProfileChangesAsync(profile =>
                profile.AddExceptions(new List<FirewallExceptionV3> { new(subject, policy) }));

        public Task<MessageType> CommitProfileChangesAsync(Action<ServerProfileConfiguration> mutate)
        {
            return Task.Run(() =>
            {
                if (Config is null)
                    return MessageType.RESPONSE_ERROR;

                // Work on a deep copy so the GUI never holds a half-applied state: only a
                // successful PUT replaces the cached config.
                var clone = SerializationHelper.Deserialize<ServerConfiguration>(
                    SerializationHelper.Serialize(Config), new ServerConfiguration());
                mutate(clone.ActiveProfile);

                var resp = _controller.SetServerConfig(clone, _changeset);
                if (resp is TwMessagePutSettings putResp && resp.Type == MessageType.PUT_SETTINGS)
                {
                    // Adopt the changeset/config/state either way, warning or not: they reflect the
                    // server's current truth, so a retry after a warning starts from the right place.
                    _changeset = putResp.Changeset;
                    Config = putResp.Config;
                    if (putResp.State is not null)
                        State = putResp.State;

                    // Warning=true means the service detected our changeset was stale and applied
                    // NOTHING, even though the wire response is still typed PUT_SETTINGS (see
                    // SimpleDeFenceService's PUT_SETTINGS handler). Reporting resp.Type here would
                    // tell the caller a change succeeded when the service silently discarded it.
                    if (putResp.Warning)
                        return MessageType.RESPONSE_STALE_CHANGESET;
                }

                return resp.Type;
            });
        }

        public Task<AppDatabase?> GetAppDatabaseAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "SimpleDeFence", "profiles.json");
                    return File.Exists(path) ? AppDatabase.Load(path) : null;
                }
                catch (Exception)
                {
                    // A missing/unreadable database is a normal state (service not installed,
                    // permissions), not an error - the Special group renders its empty state.
                    return (AppDatabase?)null;
                }
            });
        }

        public Task<IReadOnlyList<ProcessListEntry>> GetRunningProcessesAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var list = new List<ProcessListEntry>();
                    foreach (var p in System.Diagnostics.Process.GetProcesses())
                    {
                        using (p)
                        {
                            var path = ResolvePath(unchecked((uint)p.Id));
                            // No path means we cannot build a rule for it - leave it out rather than
                            // offering a row that would commit a broken exception.
                            if (string.IsNullOrEmpty(path))
                                continue;

                            list.Add(new ProcessListEntry
                            {
                                ProcessId = unchecked((uint)p.Id),
                                Name = p.ProcessName,
                                Path = path,
                            });
                        }
                    }

                    list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                    return (IReadOnlyList<ProcessListEntry>)list;
                }
                catch (Exception)
                {
                    // Process.GetProcesses()/.ProcessName can throw on a process-exit race (the
                    // process disappears between the snapshot and reading its properties). Every
                    // caller of this method is an async void picker entry point with no process-wide
                    // UnhandledException backstop, so an empty list - not a crash - is how this
                    // surfaces: same normal-state handling GetAppDatabaseAsync already gives a
                    // missing/unreadable database.
                    return (IReadOnlyList<ProcessListEntry>)Array.Empty<ProcessListEntry>();
                }
            });
        }

        public Task<IReadOnlyList<WindowListEntry>> GetTopLevelWindowsAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var list = new List<WindowListEntry>();
                    foreach (var w in SimpleDeFence.Windows.TopLevelWindows.EnumerateVisible())
                    {
                        var path = ResolvePath(w.ProcessId);
                        if (string.IsNullOrEmpty(path))
                            continue;

                        list.Add(new WindowListEntry
                        {
                            Title = w.Title,
                            ProcessId = w.ProcessId,
                            ProcessName = Path.GetFileName(path),
                            ProcessPath = path,
                        });
                    }

                    list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
                    return (IReadOnlyList<WindowListEntry>)list;
                }
                catch (Exception)
                {
                    // EnumWindows interop (SimpleDeFence.Windows.TopLevelWindows) can fail for the
                    // same COM/interop reasons GetRunningProcessesAsync's catch above guards against
                    // - see that comment for why an empty list, not a crash, is the right outcome.
                    return (IReadOnlyList<WindowListEntry>)Array.Empty<WindowListEntry>();
                }
            });
        }

        private string ResolvePath(uint pid) => _controller.TryGetProcessPath(pid);

        private void CollectTcp(TcpTable table, UwpPackageList uwp, ServicePidMap servicePids,
            List<ConnectionRow> connected, List<ConnectionRow> open)
        {
            foreach (var row in table)
            {
                var path = ResolvePath(row.ProcessId);
                var info = ProcessInfo.Create(row.ProcessId, path, uwp, servicePids);
                var connectionRow = new ConnectionRow
                {
                    ProcessId = row.ProcessId,
                    AppName = ConnectionActivity.DisplayName(info.Path, info.Package?.Name, info.Services),
                    AppPath = info.Path,
                    Protocol = "TCP",
                    LocalAddress = row.LocalEndPoint.Address.ToString(),
                    LocalPort = row.LocalEndPoint.Port,
                    RemoteAddress = row.RemoteEndPoint.Address.ToString(),
                    RemotePort = row.RemoteEndPoint.Port,
                    State = row.State.ToString(),
                };

                (row.State == TcpState.Listen ? open : connected).Add(connectionRow);
            }
        }

        private void CollectUdp(UdpTable table, UwpPackageList uwp, ServicePidMap servicePids, List<ConnectionRow> open)
        {
            foreach (var row in table)
            {
                var path = ResolvePath(row.ProcessId);
                var info = ProcessInfo.Create(row.ProcessId, path, uwp, servicePids);

                // UDP has no connection state; a bound socket is always "listening" in the sense
                // this screen cares about.
                open.Add(new ConnectionRow
                {
                    ProcessId = row.ProcessId,
                    AppName = ConnectionActivity.DisplayName(info.Path, info.Package?.Name, info.Services),
                    AppPath = info.Path,
                    Protocol = "UDP",
                    LocalAddress = row.LocalEndPoint.Address.ToString(),
                    LocalPort = row.LocalEndPoint.Port,
                    RemoteAddress = string.Empty,
                    RemotePort = 0,
                    State = "Listen",
                });
            }
        }

        private BlockedRow BlockedRowFrom(FirewallLogEntry entry, UwpPackageList uwp, ServicePidMap servicePids)
        {
            var path = entry.AppPath ?? ResolvePath(entry.ProcessId);
            var info = ProcessInfo.Create(entry.ProcessId, path, entry.PackageId, uwp, servicePids);

            return new BlockedRow
            {
                Timestamp = entry.Timestamp,
                ProcessId = entry.ProcessId,
                AppName = ConnectionActivity.DisplayName(info.Path, info.Package?.Name, info.Services),
                AppPath = info.Path,
                PackageId = entry.PackageId,
                Protocol = entry.Protocol.ToString(),
                Direction = entry.Direction.ToString(),
                RemoteAddress = entry.RemoteIp ?? string.Empty,
                RemotePort = entry.RemotePort,
            };
        }
    }
}
