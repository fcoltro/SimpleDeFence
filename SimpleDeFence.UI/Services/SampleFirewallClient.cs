using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SimpleDeFence.DatabaseClasses;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Realistic in-memory data, used only when --sample-data is passed. Lets the screens be
    /// built and visually verified before the .NET 10 migration makes the real client usable.
    /// </summary>
    internal sealed class SampleFirewallClient : IFirewallClient
    {
        private bool _locked;
        private readonly ServiceDegradation _degraded;
        private bool _hasPassword;

        /// <param name="locked">
        /// Simulates a locked service, which refuses mode changes. Without this the sample client
        /// could only ever succeed, leaving the GUI's failure handling unreachable and therefore
        /// unverifiable until the real client becomes usable. Implies a password is set, mirroring
        /// PasswordLock.Locked's real getter (locked && HasPassword) - you cannot be locked
        /// without a password.
        /// </param>
        /// <param name="degraded">
        /// Simulates a service that is running but not enforcing everything it was told to. Same
        /// reasoning as <paramref name="locked"/>: the degraded banner is unreachable from a sample
        /// client that always succeeds, and the real thing needs WFP to refuse a filter or a
        /// database to fail to load - neither of which can be arranged on demand.
        /// </param>
        public SampleFirewallClient(bool locked = false, ServiceDegradation degraded = ServiceDegradation.None)
        {
            _locked = locked;
            _hasPassword = locked;
            _degraded = degraded;
        }

        public ServerConfiguration? Config { get; private set; }
        public ServerState? State { get; private set; }
        public bool Connected { get; private set; }
        public string? LastError { get; private set; }

        public event EventHandler? Changed;

        public Task RefreshAsync()
        {
            Config ??= BuildConfig();
            State ??= new ServerState { Mode = FirewallMode.Normal };
            // Re-synced every refresh, not just on first creation, so LockAsync/UnlockAsync/
            // SetPasswordAsync (which mutate the fields, not the State object directly) are
            // reflected the next time a page calls RefreshAsync() - the same "refresh after a
            // commit to see the new truth" convention every other mutation in this app already
            // follows.
            State.Locked = _locked;
            State.HasPassword = _hasPassword;
            State.Degraded = _degraded;

            Connected = true;
            LastError = null;

            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<MessageType> SwitchModeAsync(FirewallMode mode)
        {
            // Mirrors the responses the real client can return, so the GUI's failure branches
            // are reachable while building against sample data.
            if (_locked)
                return Task.FromResult(MessageType.RESPONSE_LOCKED);

            // Never report success for a change that did not happen.
            if (State is null)
                return Task.FromResult(MessageType.RESPONSE_ERROR);

            State.Mode = mode;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(MessageType.MODE_SWITCH);
        }

        public Task<MessageType> LockAsync()
        {
            // LOCK is a privileged command (MessageType > 2047), so it's rejected while locked,
            // matching the real service's behavior in SimpleDeFenceService.cs:1895-1899.
            if (_locked)
                return Task.FromResult(MessageType.RESPONSE_LOCKED);

            // Mirrors the real service: PasswordLock.Locked's setter is a no-op without a
            // password, but the response is still MessageType.LOCK either way - it is the
            // UI's job to disable "Lock now" when State.HasPassword is false, not this method's.
            if (_hasPassword)
                _locked = true;
            return Task.FromResult(MessageType.LOCK);
        }

        public Task<MessageType> UnlockAsync(string password)
        {
            // Sample data has no real password hash to check against - any input unlocks. This
            // still exercises the GUI's success/failure branches faithfully because the *lock*
            // state, not the password check, is what SampleFirewallClient(locked: true) exists to
            // simulate; a wrong-password failure path has no sample-data seam, same limitation
            // GetAppDatabaseAsync's missing-file case already has for other data.
            _locked = false;
            return Task.FromResult(MessageType.UNLOCK);
        }

        public Task<MessageType> SetPasswordAsync(string password)
        {
            // SET_PASSPHRASE is a privileged command (MessageType > 2047), so it's rejected while
            // locked, matching the real service's behavior in SimpleDeFenceService.cs:1895-1899.
            if (_locked)
                return Task.FromResult(MessageType.RESPONSE_LOCKED);

            _hasPassword = !string.IsNullOrEmpty(password);
            if (!_hasPassword)
                // Clearing the password also clears any lock, mirroring PasswordLock.Locked's
                // real getter (locked && HasPassword) - a lock cannot survive its password
                // being removed.
                _locked = false;
            return Task.FromResult(MessageType.SET_PASSPHRASE);
        }

        /// <summary>One app holding several connections plus one holding a single connection, so
        /// both shapes the Connected list renders - collapsed with "Show all", and the lone entry
        /// shown as itself - are visible under --sample-data.</summary>
        private static List<ConnectionRow> BuildSampleConnected()
        {
            var rows = new List<ConnectionRow>();

            var firefoxRemotes = new[] { "142.250.72.14", "142.250.72.36", "34.107.221.82", "151.101.1.140", "151.101.65.140" };
            for (int i = 0; i < firefoxRemotes.Length; ++i)
            {
                rows.Add(new ConnectionRow
                {
                    ProcessId = 5150,
                    AppName = "firefox.exe",
                    AppPath = @"C:\Program Files\Mozilla Firefox\firefox.exe",
                    Protocol = "TCP",
                    LocalAddress = "10.0.0.5",
                    LocalPort = 51234 + i,
                    RemoteAddress = firefoxRemotes[i],
                    RemotePort = 443,
                    State = i == 4 ? "TimeWait" : "Established",
                });
            }

            // Talking to something on the LAN rather than the internet - the case the green
            // marker exists to call out.
            rows.Add(new ConnectionRow
            {
                ProcessId = 2100,
                AppName = "explorer.exe",
                AppPath = @"C:\Windows\explorer.exe",
                Protocol = "TCP",
                LocalAddress = "192.168.1.20",
                LocalPort = 50110,
                RemoteAddress = "192.168.1.10",
                RemotePort = 445,
                State = "Established",
            });

            rows.Add(new ConnectionRow
            {
                ProcessId = 3300,
                AppName = "updater.exe",
                AppPath = @"C:\Program Files\SampleVendor\updater.exe",
                Protocol = "TCP",
                LocalAddress = "10.0.0.5",
                LocalPort = 52001,
                RemoteAddress = "93.184.216.34",
                RemotePort = 80,
                State = "Established",
            });

            return rows;
        }

        /// <summary>svchost listening on a spread of ports - the case that made the Open list
        /// unreadable - alongside a single-port listener.</summary>
        private static List<ConnectionRow> BuildSampleOpen()
        {
            var rows = new List<ConnectionRow>();

            var ports = new[] { 5353, 5355, 3702, 49664, 49667, 49669 };
            for (int i = 0; i < ports.Length; ++i)
            {
                rows.Add(new ConnectionRow
                {
                    ProcessId = 1044,
                    AppName = "DoSvc",
                    AppPath = @"C:\Windows\System32\svchost.exe",
                    Protocol = i % 2 == 0 ? "UDP" : "TCP",
                    LocalAddress = "0.0.0.0",
                    LocalPort = ports[i],
                    RemoteAddress = string.Empty,
                    RemotePort = 0,
                    State = "Listen",
                });
            }

            // Bound to loopback, so it is reachable only from this machine. 0.0.0.0 above is the
            // opposite case and deliberately does not read as local.
            rows.Add(new ConnectionRow
            {
                ProcessId = 7700,
                AppName = "postgres.exe",
                AppPath = @"C:\Program Files\PostgreSQL\postgres.exe",
                Protocol = "TCP",
                LocalAddress = "127.0.0.1",
                LocalPort = 5432,
                RemoteAddress = string.Empty,
                RemotePort = 0,
                State = "Listen",
            });

            rows.Add(new ConnectionRow
            {
                ProcessId = 900,
                AppName = "sshd.exe",
                AppPath = @"C:\Windows\System32\OpenSSH\sshd.exe",
                Protocol = "TCP",
                LocalAddress = "0.0.0.0",
                LocalPort = 22,
                RemoteAddress = string.Empty,
                RemotePort = 0,
                State = "Listen",
            });

            return rows;
        }

        private static List<BlockedRow> BuildSampleBlocked()
        {
            var rows = new List<BlockedRow>
            {
                new()
                {
                    Timestamp = DateTime.Now.AddSeconds(-40),
                    ProcessId = 4242,
                    AppName = "tracker.exe",
                    AppPath = @"C:\Users\sample\AppData\Local\Telemetry\tracker.exe",
                    Protocol = "TCP",
                    Direction = "Out",
                    RemoteAddress = "203.0.113.9",
                    RemotePort = 443,
                },
            };

            rows.Add(new BlockedRow
            {
                Timestamp = DateTime.Now.AddSeconds(-20),
                ProcessId = 6600,
                AppName = "printerhelper.exe",
                AppPath = @"C:\Program Files\SampleVendor\printerhelper.exe",
                Protocol = "UDP",
                Direction = "Out",
                RemoteAddress = "192.168.1.55",
                RemotePort = 161,
            });

            // One service hammering a rotating set of endpoints, the way a real one does.
            var endpoints = new[] { "198.51.100.202", "198.51.100.204", "198.51.100.211", "198.51.100.213" };
            for (int i = 0; i < 11; ++i)
            {
                rows.Add(new BlockedRow
                {
                    Timestamp = DateTime.Now.AddSeconds(-8 * i),
                    ProcessId = 3120,
                    AppName = "backupagent.exe",
                    AppPath = @"C:\Program Files\SampleVendor\backupagent.exe",
                    Protocol = "TCP",
                    Direction = "Out",
                    RemoteAddress = endpoints[i % endpoints.Length],
                    RemotePort = 443,
                });
            }

            return rows;
        }

        public Task<ConnectionsSnapshot> GetConnectionsAsync()
        {
            var snapshot = new ConnectionsSnapshot
            {
                // Two shapes on purpose, because the Blocked list renders them differently: one app
                // that tried once (shown as the attempt itself, no "Show all"), and one that retries
                // against a spread of addresses (collapsed to one row, attempts behind the flyout).
                // The second is what the list looks like in practice - a blocked process does not give
                // up after one try - and with only the single row here, the grouping this fixture is
                // meant to exercise never appeared at all.
                Blocked = BuildSampleBlocked(),
                Connected = BuildSampleConnected(),
                Open = BuildSampleOpen(),
            };

            return Task.FromResult(snapshot);
        }

        public Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy)
            => CommitConfigChangesAsync(config =>
                config.ActiveProfile.AddExceptions(new List<FirewallExceptionV3> { new(subject, policy) }));

        public Task<MessageType> CommitConfigChangesAsync(Action<ServerConfiguration> mutate)
        {
            if (_locked)
                return Task.FromResult(MessageType.RESPONSE_LOCKED);

            if (Config is null)
                return Task.FromResult(MessageType.RESPONSE_ERROR);

            mutate(Config);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(MessageType.PUT_SETTINGS);
        }

        public Task<AppDatabase?> GetAppDatabaseAsync()
        {
            // A small built-in set so the Special group is exercisable on sample data: one
            // recommended, one optional, one hidden (never rendered).
            var db = new AppDatabase(new List<Application>
            {
                MakeSpecial("Windows_Update", recommended: true),
                MakeSpecial("Gaming", recommended: false),
                MakeSpecial("Hidden_Service", recommended: true, hidden: true),
            });
            return Task.FromResult<AppDatabase?>(db);
        }

        private static Application MakeSpecial(string name, bool recommended, bool hidden = false)
        {
            var app = new Application { Name = name };
            app.Flags!["TWUI:SPECIAL"] = null;
            if (recommended) app.Flags["TWUI:RECOMMENDED"] = null;
            if (hidden) app.Flags["TWUI:HIDDEN"] = null;
            return app;
        }

        public Task<IReadOnlyList<ProcessListEntry>> GetRunningProcessesAsync()
        {
            IReadOnlyList<ProcessListEntry> list = new List<ProcessListEntry>
            {
                new() { ProcessId = 5150, Name = "firefox", Path = @"C:\Program Files\Mozilla Firefox\firefox.exe" },
                new() { ProcessId = 4242, Name = "tracker", Path = @"C:\Users\sample\AppData\Local\Telemetry\tracker.exe" },
            };
            return Task.FromResult(list);
        }

        public Task<IReadOnlyList<WindowListEntry>> GetTopLevelWindowsAsync()
        {
            IReadOnlyList<WindowListEntry> list = new List<WindowListEntry>
            {
                new() { Title = "Mozilla Firefox", ProcessId = 5150, ProcessName = "firefox.exe", ProcessPath = @"C:\Program Files\Mozilla Firefox\firefox.exe" },
                new() { Title = "Settings", ProcessId = 1044, ProcessName = "svchost.exe", ProcessPath = @"C:\Windows\System32\svchost.exe" },
            };
            return Task.FromResult(list);
        }

        private static ServerConfiguration BuildConfig()
        {
            var profile = new ServerProfileConfiguration("Default");

            profile.AppExceptions.Add(new FirewallExceptionV3(
                new ExecutableSubject(@"C:\Program Files\Mozilla Firefox\firefox.exe"),
                new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "80,443" }));

            profile.AppExceptions.Add(new FirewallExceptionV3(
                new ExecutableSubject(@"C:\Program Files\Git\git-remote-https.exe"),
                new UnrestrictedPolicy()));

            profile.AppExceptions.Add(new FirewallExceptionV3(
                new ServiceSubject(@"C:\Windows\System32\svchost.exe", "UsoSvc"),
                new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "80,443" }));

            profile.AppExceptions.Add(new FirewallExceptionV3(
                new ServiceSubject(@"C:\Windows\System32\svchost.exe", "DoSvc"),
                new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "80,443" }));

            profile.AppExceptions.Add(new FirewallExceptionV3(
                new ExecutableSubject(@"C:\Users\sample\AppData\Local\Telemetry\tracker.exe"),
                new HardBlockPolicy()));

            profile.AppExceptions.Add(new FirewallExceptionV3(
                GlobalSubject.Instance,
                new TcpUdpPolicy { AllowedLocalUdpListenerPorts = "5353" }));

            var config = new ServerConfiguration();
            config.Profiles.Add(profile);
            config.ActiveProfileName = "Default";
            return config;
        }
    }
}
