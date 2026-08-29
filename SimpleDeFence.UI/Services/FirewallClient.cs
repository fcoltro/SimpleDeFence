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

        // Every command below re-reads server state before returning. State is only ever assigned
        // in RefreshAsync, so without this the cached State still describes the server as it was
        // *before* the command: switching mode from the chip left ShellViewModel.Update()
        // recomputing the label from the old Mode, and the chip went on showing the previous mode
        // while the service had genuinely switched (confirmed on the VM - a freshly launched GUI
        // read the new mode immediately). The same staleness applied to Locked/HasPassword after
        // Lock/Unlock/SetPassword.
        //
        // This never showed up against SampleFirewallClient, which mutates its own State object in
        // place and raises Changed itself, so the sample data path was strictly more forgiving than
        // the real one - the same trap as the changeset-conflict path it also cannot reach.
        //
        // Refreshed unconditionally rather than only on the success response: a command that
        // failed part-way can still have moved server state, and re-reading is the only way to
        // find out. The refresh also raises Changed, which is what repaints the tray icon and
        // tooltip - callers reaching IFirewallClient directly (TrayIconService's mode submenu) get
        // that for free instead of each having to remember to refresh.
        private async Task<MessageType> CommandAsync(Func<MessageType> command)
        {
            var response = await Task.Run(command).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            return response;
        }

        public Task<MessageType> SwitchModeAsync(FirewallMode mode)
            => CommandAsync(() => _controller.SwitchFirewallMode(mode));

        public Task<MessageType> LockAsync()
            => CommandAsync(() => _controller.LockServer());

        public Task<MessageType> UnlockAsync(string password)
            => CommandAsync(() => _controller.TryUnlockServer(password));

        public Task<MessageType> SetPasswordAsync(string password)
            => CommandAsync(() => _controller.SetPassphrase(password));

        public Task<ConnectionsSnapshot> GetConnectionsAsync()
        {
            return Task.Run(() =>
            {
                // The firewall log is the one input here that has to come from the service, so it
                // is only asked for when the last refresh could reach it - see ResolvePath for
                // what a request to a service that is not answering costs. Null means "not asked",
                // which the Blocked section below treats the same as an empty log.
                var logRequest = Connected ? _controller.BeginReadFwLog() : null;

                var uwp = new UwpPackageList();

                // Both of these only put better names on rows - a UWP package's display name, a
                // svchost's service names - so neither is allowed to decide whether there are any
                // rows at all. UwpPackageList already falls back to an empty list internally;
                // CreateOrEmpty gives ServicePidMap the same property.
                var servicePids = ServicePidMap.CreateOrEmpty();

                // The three sections are gathered independently, because they have nothing in
                // common but this method. Connected and Open come from GetExtendedTcpTable /
                // GetExtendedUdpTable, which are local calls that do not involve the service at
                // all; Blocked comes from the service's event log over the pipe. Gathered in one
                // unguarded block - as this was - any single failure threw out of Task.Run, and
                // ConnectionsPage.RefreshAsync's catch turned that into an empty snapshot, so a
                // problem reading the firewall log emptied the two lists built from local data
                // too, and vice versa. Whatever could be collected is now returned.
                //
                // Same reasoning, and the same "an empty list is a normal state, not a crash"
                // handling, that GetRunningProcessesAsync and GetTopLevelWindowsAsync below
                // already apply to their own interop.
                var connected = new List<ConnectionRow>();
                var open = new List<ConnectionRow>();
                CollectSafely(() => CollectTcp(NetStat.GetExtendedTcp4Table(false), uwp, servicePids, connected, open), "TCP/IPv4");
                CollectSafely(() => CollectTcp(NetStat.GetExtendedTcp6Table(false), uwp, servicePids, connected, open), "TCP/IPv6");
                CollectSafely(() => CollectUdp(NetStat.GetExtendedUdp4Table(false), uwp, servicePids, open), "UDP/IPv4");
                CollectSafely(() => CollectUdp(NetStat.GetExtendedUdp6Table(false), uwp, servicePids, open), "UDP/IPv6");

                var blocked = new List<BlockedRow>();
                if (logRequest is not null)
                {
                    CollectSafely(() =>
                    {
                        var rawLog = Controller.EndReadFwLog(logRequest.Response);
                        var recentBlocked = ConnectionActivity.RecentBlocked(
                            rawLog, DateTime.Now, ClientSettings.Load().BlockedHistoryWindow);
                        foreach (var entry in recentBlocked)
                            blocked.Add(BlockedRowFrom(entry, uwp, servicePids));
                    }, "Blocked");
                }

                return new ConnectionsSnapshot
                {
                    Blocked = blocked,
                    Connected = connected.OrderBy(r => r.AppName, StringComparer.CurrentCultureIgnoreCase).ToList(),
                    Open = open.OrderBy(r => r.AppName, StringComparer.CurrentCultureIgnoreCase).ToList(),
                };
            });
        }

        public Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy)
            => CommitConfigChangesAsync(config =>
                config.ActiveProfile.AddExceptions(new List<FirewallExceptionV3> { new(subject, policy) }));

        public Task<MessageType> CommitConfigChangesAsync(Action<ServerConfiguration> mutate)
        {
            return Task.Run(() =>
            {
                if (Config is null)
                    return MessageType.RESPONSE_ERROR;

                // Work on a deep copy so the GUI never holds a half-applied state: only a
                // successful PUT replaces the cached config.
                var clone = SerializationHelper.Deserialize<ServerConfiguration>(
                    SerializationHelper.Serialize(Config), new ServerConfiguration());
                mutate(clone);

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

        /// <summary>
        /// The executable behind a pid, asked of the service, or empty when there is no point
        /// asking.
        ///
        /// This is one full pipe round-trip per connection row, and a round-trip to a service that
        /// is not answering is not cheap: PipeClientEndpoint retries once, so it costs two
        /// Connect(1000) timeouts plus the 200 ms sleep between them - about 2.2 seconds - before
        /// returning the empty string it was always going to return. A machine with a few hundred
        /// open sockets would spend minutes on a gather whose path column comes back empty either
        /// way, and the auto-refresh timer would queue up behind it.
        ///
        /// So when the last refresh could not reach the service, do not ask. Rows are still built
        /// and still listed - named by their UWP package or hosted services where those are known,
        /// and shown as "Unknown" where they are not, which is exactly what the name column
        /// already does for a path the service declines to resolve.
        /// </summary>
        private string ResolvePath(uint pid) => Connected ? _controller.TryGetProcessPath(pid) : string.Empty;

        /// <summary>
        /// Runs one section of the connections gather, letting it fail on its own.
        ///
        /// Deliberately catches everything: the callers are P/Invoke into the IP helper API,
        /// per-process token reads, and pipe I/O, none of which has a documented exception set
        /// worth enumerating, and the correct response to any of them is identical - that section
        /// contributes what it managed to collect and the rest of the screen is unaffected.
        /// Swallowing is safe here precisely because it is scoped to one section: the user is not
        /// told a lie, they are shown fewer rows, and the section's own empty state says so.
        /// </summary>
        private static void CollectSafely(Action collect, string section)
        {
            try
            {
                collect();
            }
            catch (Exception e)
            {
                // Traced rather than silently dropped. The screen deliberately carries on with
                // whatever the other sections produced, but a section that keeps coming back
                // empty is otherwise indistinguishable from one that genuinely has nothing in
                // it - which is exactly the confusion that made the original all-or-nothing
                // gather so hard to diagnose. Same channel PipeClientEndpoint traces its own
                // refusals on.
                System.Diagnostics.Debug.WriteLine(
                    $"Connections gather: the {section} section failed and was left as far as it got. {e.GetType().Name}: {e.Message}");
            }
        }

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
