using System;
using System.Threading.Tasks;

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
