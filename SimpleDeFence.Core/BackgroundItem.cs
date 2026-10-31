using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleDeFence
{
    public enum BackgroundItemKind
    {
        Service,
        ScheduledTask,
        Startup,
    }

    /// <summary>
    /// One thing that starts a program in the background without being asked: a Windows service,
    /// a scheduled task or a startup entry. "Stop it trying" lists these for one publisher, so a
    /// blocked app's updaters, sync agents and analytics stop running - and stop retrying the
    /// connection they will never get.
    /// </summary>
    public sealed record BackgroundItem
    {
        public BackgroundItemKind Kind { get; init; }

        /// <summary>What the service needs to find the item again: the service name, the task's
        /// full path, or the startup entry as "registry key|value name".</summary>
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;

        /// <summary>False when it is switched off, by us or by anyone else.</summary>
        public bool Enabled { get; init; }

        /// <summary>A licence checker the publisher's apps may refuse to open without.</summary>
        public bool NeededToOpen { get; init; }

        /// <summary>Switched off by "Stop it trying", so "Switch all back on" may restore it.
        /// Things that were already off before SimpleDeFence touched them stay off.</summary>
        public bool SwitchedOffHere { get; init; }
    }

    /// <summary>One change asked for from the "Stop it trying" dialog.</summary>
    public sealed record BackgroundItemChange
    {
        public BackgroundItemKind Kind { get; init; }
        public string Id { get; init; } = string.Empty;
        public bool Enable { get; init; }
    }

    /// <summary>
    /// The rules behind "Stop it trying", kept free of Windows calls so they can be tested. The
    /// service supplies the services, tasks, startup entries and signatures.
    /// </summary>
    public static class BackgroundItemRules
    {
        /// <summary>
        /// Licence checkers. Switching these off can stop the publisher's apps from opening at
        /// all, so the dialog marks them and leaves them on unless they are ticked by hand.
        /// Matched on the executable's file name.
        /// </summary>
        private static readonly HashSet<string> LicenceCheckers = new(StringComparer.OrdinalIgnoreCase)
        {
            "AdskLicensingService.exe",
            "AdskLicensingAgent.exe",
            "FNPLicensingService.exe",
            "FNPLicensingService64.exe",
            "lmgrd.exe",
            "AdobeIPCBroker.exe",
        };

        public static bool NeededToOpen(string path)
            => !string.IsNullOrEmpty(path) && LicenceCheckers.Contains(System.IO.Path.GetFileName(path));

        /// <summary>
        /// Whether a publisher's background items may be listed at all. Windows' own parts are
        /// never offered: switching off a Microsoft-signed service from a firewall's right-click
        /// menu is how a machine stops booting properly.
        /// </summary>
        public static bool MayQuiet(string? publisher)
            => !string.IsNullOrWhiteSpace(publisher)
               && publisher!.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) < 0;

        /// <summary>Same publisher: the certificate names (see PublisherTrust.DisplayName) match.</summary>
        public static bool SamePublisher(string? a, string? b)
            => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The executable a command line starts: a service's ImagePath, a startup entry's command
        /// or a task's action. Handles quoted paths, unquoted paths with arguments after ".exe",
        /// the "\??\" prefix and %Variables%. Empty when there is no executable to find.
        /// </summary>
        public static string ExecutableOf(string? commandLine, Func<string, string>? expand = null)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
                return string.Empty;

            var s = commandLine!.Trim();
            if (expand is not null)
                s = expand(s);

            string exe;
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = s.IndexOf('"', 1);
                exe = end < 0 ? s.Substring(1) : s.Substring(1, end - 1);
            }
            else
            {
                int dotExe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                exe = dotExe < 0 ? s.Split(' ')[0] : s.Substring(0, dotExe + 4);
            }

            if (exe.StartsWith(@"\??\", StringComparison.Ordinal))
                exe = exe.Substring(4);
            if (exe.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe.Substring(12));

            return exe.Trim();
        }

        /// <summary>
        /// Whether a StartupApproved value says the entry is switched off. This is where Task
        /// Manager's Startup tab records its own on/off switch: an odd first byte is off. No value
        /// means nobody switched it, which is on.
        /// </summary>
        public static bool StartupApprovedIsOff(byte[]? value)
            => value is { Length: > 0 } && (value[0] & 1) == 1;

        /// <summary>The StartupApproved value that switches an entry on or off, the same 12 bytes
        /// Task Manager writes: a flag, three zero bytes, then when it was switched off.</summary>
        public static byte[] StartupApprovedValue(bool enable, DateTime nowUtc)
        {
            var value = new byte[12];
            value[0] = enable ? (byte)0x02 : (byte)0x03;
            if (!enable)
                BitConverter.GetBytes(nowUtc.ToFileTimeUtc()).CopyTo(value, 4);
            return value;
        }

        /// <summary>
        /// The publisher behind a program whose own signature fails, read from the other programs
        /// in its folder: the name at least two of them are validly signed with, and more of them
        /// than any other name. A single signed file proves little, since suites bundle libraries
        /// signed by other companies, and a tie names no one. Empty when no name qualifies.
        /// Publishers whose items are never offered (see MayQuiet) do not count.
        /// </summary>
        public static string PublisherFromNeighbours(IEnumerable<string?> neighbourPublishers)
        {
            var counts = neighbourPublishers
                .Where(MayQuiet)
                .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count)
                .ToList();
            if (counts.Count == 0 || counts[0].Count < 2)
                return string.Empty;
            if (counts.Count > 1 && counts[1].Count == counts[0].Count)
                return string.Empty;
            return counts[0].Name;
        }

        /// <summary>A program inside the Windows folder is a part of Windows, whoever signed it.</summary>
        public static bool IsInWindowsFolder(string path, string windowsDirectory)
            => IsUnder(path, windowsDirectory);

        /// <summary>Whether path is inside folder, at any depth.</summary>
        public static bool IsUnder(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder))
                return false;
            var prefix = folder.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static readonly string[] BroadInProfile = { "Desktop", "Documents", "Downloads", "Music", "Pictures", "Videos", "AppData" };
        private static readonly string[] BroadInAppData = { @"AppData\Local", @"AppData\LocalLow", @"AppData\Roaming", @"AppData\Local\Programs", @"AppData\Local\Temp" };

        /// <summary>
        /// Whether a folder is too wide to stand for one program in "Stop it trying": a drive's
        /// root, one of the shared folders (Program Files, ProgramData, the Windows folder...) or
        /// anything above them, a user's profile, or a well-known folder inside it like Downloads.
        /// An unsigned program in such a place would otherwise take every other program there
        /// with it.
        /// </summary>
        public static bool FolderTooBroad(string? folder, IEnumerable<string> sharedFolders, string usersRoot)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return true;
            var f = folder!.TrimEnd('\\', '/');
            if (f.Length <= 3)   // "C:" or "C:\"
                return true;

            foreach (var shared in sharedFolders)
            {
                if (string.IsNullOrEmpty(shared))
                    continue;
                var s = shared.TrimEnd('\\', '/');
                if (string.Equals(f, s, StringComparison.OrdinalIgnoreCase) || IsUnder(s, f))
                    return true;
            }

            if (!string.IsNullOrEmpty(usersRoot))
            {
                var users = usersRoot.TrimEnd('\\', '/');
                if (string.Equals(f, users, StringComparison.OrdinalIgnoreCase) || IsUnder(users, f))
                    return true;
                if (IsUnder(f, users))
                {
                    // Users\<name>\...: the profile itself and its well-known folders are too wide.
                    var parts = f.Substring(users.Length + 1).Split('\\', '/');
                    if (parts.Length == 1)
                        return true;
                    var inProfile = string.Join("\\", parts, 1, parts.Length - 1);
                    if (parts.Length == 2 && BroadInProfile.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
                        return true;
                    if (BroadInAppData.Contains(inProfile, StringComparer.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }
    }
}
