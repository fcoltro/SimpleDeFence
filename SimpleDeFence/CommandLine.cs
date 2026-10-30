using System;
using System.IO;
using System.Runtime.InteropServices;
using SimpleDeFence.DatabaseClasses;

namespace SimpleDeFence
{
    /// <summary>
    /// Commands that do one thing and exit, for scripts, shortcuts and scheduled tasks:
    ///
    ///   SimpleDeFence.exe /mode normal | blockall | allowoutgoing | disabled
    ///   SimpleDeFence.exe /mode learning [/minutes N]
    ///   SimpleDeFence.exe /allow "C:\path\to\app.exe"
    ///
    /// They go to the running service over the same pipe as the window, so the window's rules
    /// apply: the process has to be elevated (app.manifest asks for that), and a password-locked
    /// configuration refuses them. /allow writes what "Add application" on the Rules page writes,
    /// including the app database's companion programs for a recognised app.
    ///
    /// Exit codes: 0 done, 1 refused or failed, 2 a command line that could not be understood.
    /// Messages are English only. They go to the console the command was typed in, when there is
    /// one; this is a GUI-subsystem executable, so it has to attach to that console itself.
    /// </summary>
    internal static class CommandLine
    {
        private const int Done = 0;
        private const int Failed = 1;
        private const int Usage = 2;

        private const string PipeName = "SimpleDeFenceController";

        internal static bool IsCommand(string[] args)
            => IndexOf(args, "/mode") >= 0 || IndexOf(args, "/allow") >= 0;

        internal static int Run(string[] args)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);

            try
            {
                int mode = IndexOf(args, "/mode");
                if (mode >= 0)
                    return SwitchMode(ValueAfter(args, mode), args);

                return Allow(ValueAfter(args, IndexOf(args, "/allow")));
            }
            catch (Exception e)
            {
                // Most often the service is not running or not reachable: the pipe connect throws.
                Console.Error.WriteLine("SimpleDeFence: " + e.Message);
                return Failed;
            }
        }

        private static int SwitchMode(string? name, string[] args)
        {
            FirewallMode? mode = name?.ToLowerInvariant() switch
            {
                "normal" => FirewallMode.Normal,
                "blockall" => FirewallMode.BlockAll,
                "allowoutgoing" => FirewallMode.AllowOutgoing,
                "disabled" => FirewallMode.Disabled,
                "learning" => FirewallMode.Learning,
                _ => null,
            };
            if (mode is not FirewallMode target)
                return UsageError("/mode needs one of: normal, blockall, allowoutgoing, disabled, learning.");

            int minutes = 0;
            int minutesAt = IndexOf(args, "/minutes");
            if (minutesAt >= 0)
            {
                if (target != FirewallMode.Learning)
                    return UsageError("/minutes only applies to /mode learning.");
                if (!int.TryParse(ValueAfter(args, minutesAt), out minutes) || minutes < 1 || minutes > 24 * 60)
                    return UsageError("/minutes needs a number of minutes from 1 to 1440.");
            }

            var controller = new Controller(PipeName);
            var resp = controller.SwitchFirewallMode(target, minutes);
            if (resp != MessageType.MODE_SWITCH)
                return Refused(resp, "The mode was not changed.");

            Console.WriteLine($"SimpleDeFence: mode is now {FirewallModes.LabelFor(target)}.");
            return Done;
        }

        private static int Allow(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return UsageError("/allow needs the path of a program.");

            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                return UsageError($"{path} does not exist.");

            var controller = new Controller(PipeName);
            var changeset = Guid.Empty;
            var resp = controller.GetServerConfig(out var config, out var state, ref changeset);
            if (resp != MessageType.GET_SETTINGS || config is null)
                return Refused(resp, "No rule was added.");
            if (state?.Locked == true)
                return Refused(MessageType.RESPONSE_LOCKED, "No rule was added.");

            // A missing or unreadable database is a normal state, as it is for the Rules page: the
            // program is then allowed on its own, with nothing added for companions.
            AppDatabase db;
            try { db = AppDatabase.Load(); }
            catch { db = new AppDatabase(); }

            var exceptions = db.GetExceptionsForApp(new ExecutableSubject(path), out var app);
            config.ActiveProfile.AddExceptions(exceptions);

            var put = controller.SetServerConfig(config, changeset);
            if (put.Type != MessageType.PUT_SETTINGS)
                return Refused(put.Type, "No rule was added.");

            // PUT_SETTINGS with Warning set means the service saw a stale changeset and applied
            // nothing - see IFirewallClient.CommitConfigChangesAsync. It is not a success.
            if (put is TwMessagePutSettings { Warning: true })
                return Refused(MessageType.RESPONSE_STALE_CHANGESET, "No rule was added; try again.");

            Console.WriteLine(app is null
                ? $"SimpleDeFence: allowed {path}."
                : $"SimpleDeFence: allowed {path} as {app.Name}.");
            return Done;
        }

        private static int Refused(MessageType resp, string consequence)
        {
            string reason = resp switch
            {
                MessageType.RESPONSE_LOCKED => "the configuration is locked. Unlock it in the SimpleDeFence window first.",
                MessageType.COM_ERROR => "the service could not be reached.",
                _ => $"the service answered {resp}.",
            };
            Console.Error.WriteLine($"SimpleDeFence: {consequence} Reason: {reason}");
            return Failed;
        }

        private static int UsageError(string message)
        {
            Console.Error.WriteLine("SimpleDeFence: " + message);
            Console.Error.WriteLine("Usage: SimpleDeFence.exe /mode normal|blockall|allowoutgoing|disabled");
            Console.Error.WriteLine("       SimpleDeFence.exe /mode learning [/minutes N]");
            Console.Error.WriteLine("       SimpleDeFence.exe /allow \"C:\\path\\to\\app.exe\"");
            return Usage;
        }

        private static int IndexOf(string[] args, string name)
            => Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        private static string? ValueAfter(string[] args, int index)
            => index >= 0 && index + 1 < args.Length ? args[index + 1] : null;

        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
    }
}
