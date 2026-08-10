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
        private readonly bool _locked;

        /// <param name="locked">
        /// Simulates a locked service, which refuses mode changes. Without this the sample client
        /// could only ever succeed, leaving the GUI's failure handling unreachable and therefore
        /// unverifiable until the real client becomes usable.
        /// </param>
        public SampleFirewallClient(bool locked = false) => _locked = locked;

        public ServerConfiguration? Config { get; private set; }
        public ServerState? State { get; private set; }
        public bool Connected { get; private set; }
        public string? LastError { get; private set; }

        public event EventHandler? Changed;

        public Task RefreshAsync()
        {
            Config ??= BuildConfig();
            State ??= new ServerState
            {
                Mode = FirewallMode.Normal,
                Locked = _locked,
                HasPassword = _locked,
            };

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

        public Task<ConnectionsSnapshot> GetConnectionsAsync()
        {
            var snapshot = new ConnectionsSnapshot
            {
                Blocked = new List<BlockedRow>
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
                },
                Connected = new List<ConnectionRow>
                {
                    new()
                    {
                        ProcessId = 5150,
                        AppName = "firefox.exe",
                        AppPath = @"C:\Program Files\Mozilla Firefox\firefox.exe",
                        Protocol = "TCP",
                        LocalAddress = "10.0.0.5",
                        LocalPort = 51234,
                        RemoteAddress = "142.250.72.14",
                        RemotePort = 443,
                        State = "Established",
                    },
                },
                Open = new List<ConnectionRow>
                {
                    new()
                    {
                        ProcessId = 1044,
                        AppName = "DoSvc",
                        AppPath = @"C:\Windows\System32\svchost.exe",
                        Protocol = "UDP",
                        LocalAddress = "0.0.0.0",
                        LocalPort = 5353,
                        RemoteAddress = string.Empty,
                        RemotePort = 0,
                        State = "Listen",
                    },
                },
            };

            return Task.FromResult(snapshot);
        }

        public Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy)
            => CommitProfileChangesAsync(profile =>
                profile.AddExceptions(new List<FirewallExceptionV3> { new(subject, policy) }));

        public Task<MessageType> CommitProfileChangesAsync(Action<ServerProfileConfiguration> mutate)
        {
            if (_locked)
                return Task.FromResult(MessageType.RESPONSE_LOCKED);

            if (Config is null)
                return Task.FromResult(MessageType.RESPONSE_ERROR);

            mutate(Config.ActiveProfile);
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
