using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using Microsoft.Win32;
using SimpleDeFence.Windows.Services;

namespace SimpleDeFence
{
    /// <summary>
    /// "Stop it trying": finds the services, scheduled tasks and startup entries signed by the same
    /// publisher as a blocked program, and switches them off or back on.
    ///
    /// A blocked app is refused at once, and big suites keep their updaters, sync agents and
    /// analytics retrying that refusal for ever. Blocking cannot make them stop; not starting them
    /// does. Everything here is reversible: a service gets back the start type it had (remembered
    /// in a file next to the configuration), a task is re-enabled, and a startup entry is switched
    /// with the same StartupApproved flag Task Manager's Startup tab uses.
    ///
    /// Never offers anything inside the Windows folder or signed by Microsoft - see
    /// BackgroundItemRules.MayQuiet.
    /// </summary>
    internal sealed class BackgroundItemsManager
    {
        private static readonly string StateFilePath = Path.Combine(Utils.AppDataPath, "background-items.txt");

        private readonly Func<string, (bool Valid, string? Subject)> SignatureOf;
        private readonly string WindowsDirectory;

        internal BackgroundItemsManager(Func<string, (bool Valid, string? Subject)> signatureOf, string windowsDirectory)
        {
            SignatureOf = signatureOf;
            WindowsDirectory = windowsDirectory;
        }

        /// <summary>The certificate name of the program's publisher, or empty when it has no valid
        /// signature or is one whose items are never offered.</summary>
        internal string PublisherOf(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return string.Empty;
            var (valid, subject) = SignatureOf(path);
            if (!valid || string.IsNullOrEmpty(subject))
                return string.Empty;
            var publisher = PublisherTrust.DisplayName(subject!);
            return BackgroundItemRules.MayQuiet(publisher) ? publisher : string.Empty;
        }

        /// <summary>
        /// Like PublisherOf, but when the program's own signature fails - it was changed after it
        /// was signed, or never signed - the publisher is read from the signed programs and
        /// libraries in the same folder (see BackgroundItemRules.PublisherFromNeighbours).
        /// FromFolder tells the dialog to say so. Every item is still checked against that
        /// publisher's signature one by one, so nothing unsigned is ever switched off this way.
        /// </summary>
        internal (string Publisher, bool FromFolder) PublisherNear(string path)
        {
            var own = PublisherOf(path);
            if (own.Length > 0)
                return (own, false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return (string.Empty, false);

            // A Microsoft-signed program is refused outright, not looked around: its folder may
            // well hold someone else's files, but its items are never offered.
            var (valid, subject) = SignatureOf(path);
            if (valid && !string.IsNullOrEmpty(subject))
                return (string.Empty, false);

            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder) || BackgroundItemRules.IsInWindowsFolder(path, WindowsDirectory))
                return (string.Empty, false);

            var publisher = BackgroundItemRules.PublisherFromNeighbours(NeighbourPublishers(folder!, path));
            return publisher.Length > 0 ? (publisher, true) : (string.Empty, false);
        }

        /// <summary>The publishers of up to NeighbourLimit other programs and libraries in the
        /// folder, smallest first: checking a signature hashes the whole file, and the answer has
        /// to arrive well inside the pipe's 20-second wait.</summary>
        private List<string?> NeighbourPublishers(string folder, string except)
        {
            const int NeighbourLimit = 12;
            const long MaxSize = 64L * 1024 * 1024;

            List<FileInfo> files = new();
            try
            {
                files = new DirectoryInfo(folder).EnumerateFiles()
                    .Where(f => f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                             || f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Length <= MaxSize
                             && !string.Equals(f.FullName, except, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Length)
                    .Take(NeighbourLimit)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Unreadable folder: no neighbours, so no publisher.
            }

            var publishers = new List<string?>(files.Count);
            foreach (var f in files)
            {
                var (valid, subject) = SignatureOf(f.FullName);
                publishers.Add(valid && !string.IsNullOrEmpty(subject) ? PublisherTrust.DisplayName(subject!) : null);
            }
            return publishers;
        }

        /// <summary>
        /// The folder "Stop it trying" may fall back to for a program with no publisher: its own,
        /// or empty when that is a place many programs share (see BackgroundItemRules.FolderTooBroad),
        /// inside the Windows folder, or when the program is signed by a publisher whose items are
        /// never offered.
        /// </summary>
        internal string OwnFolderOf(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)
                || BackgroundItemRules.IsInWindowsFolder(path, WindowsDirectory))
                return string.Empty;

            var (valid, subject) = SignatureOf(path);
            if (valid && !string.IsNullOrEmpty(subject))
                return string.Empty;

            var folder = Path.GetDirectoryName(path) ?? string.Empty;
            var usersRoot = Path.GetDirectoryName(Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty) ?? string.Empty;
            var shared = new[]
            {
                WindowsDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Path.GetTempPath(),
            };
            return BackgroundItemRules.FolderTooBroad(folder, shared, usersRoot) ? string.Empty : folder;
        }

        /// <summary>The items whose program is validly signed by this publisher.</summary>
        internal BackgroundItem[] List(string publisher)
            => BackgroundItemRules.MayQuiet(publisher)
                ? ListMatching(path => FromPublisher(path, publisher))
                : Array.Empty<BackgroundItem>();

        /// <summary>
        /// The items whose program runs from this folder or below it, for a blocked program with
        /// no publisher to go by. Weaker than a signature, so the dialog says so; the folder must
        /// pass BackgroundItemRules.FolderTooBroad first, and Windows' own and Microsoft-signed
        /// programs are still never offered.
        /// </summary>
        internal BackgroundItem[] ListInFolder(string folder)
            => ListMatching(path => InFolder(path, folder));

        private BackgroundItem[] ListMatching(Func<string, bool> belongs)
        {
            var items = new List<BackgroundItem>();

            try { items.AddRange(Services(belongs)); }
            catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }
            try { items.AddRange(Tasks(belongs)); }
            catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }
            try { items.AddRange(StartupEntries(belongs)); }
            catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }

            // Only what we switched off - and is still off - can be put back by "Switch all back
            // on". Something its own installer or the user had switched off stays as they left it.
            var state = LoadState();
            for (int i = 0; i < items.Count; ++i)
            {
                if (!items[i].Enabled && state.ContainsKey(StateKey(items[i].Kind, items[i].Id)))
                    items[i] = items[i] with { SwitchedOffHere = true };
            }

            return items
                .OrderBy(i => i.Kind)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Applies the changes to items of this publisher only. Each change is checked against a
        /// fresh listing first, so a request cannot name some other program's service and have it
        /// switched off. Returns false if any change failed; the others still went through.
        /// </summary>
        internal bool Apply(string publisher, IEnumerable<BackgroundItemChange> changes)
            => Apply(List(publisher), changes);

        /// <summary>Apply, for the items ListInFolder finds.</summary>
        internal bool ApplyInFolder(string folder, IEnumerable<BackgroundItemChange> changes)
            => Apply(ListInFolder(folder), changes);

        private bool Apply(BackgroundItem[] current, IEnumerable<BackgroundItemChange> changes)
        {
            bool ok = true;
            var stopPaths = new List<string>();

            foreach (var change in changes)
            {
                var item = current.FirstOrDefault(i => i.Kind == change.Kind && string.Equals(i.Id, change.Id, StringComparison.OrdinalIgnoreCase));
                if (item is null || item.Enabled == change.Enable)
                    continue;

                try
                {
                    switch (item.Kind)
                    {
                        case BackgroundItemKind.Service: SetService(item.Id, change.Enable); break;
                        case BackgroundItemKind.ScheduledTask: SetTask(item.Id, change.Enable); break;
                        case BackgroundItemKind.Startup: SetStartupEntry(item.Id, change.Enable); break;
                    }
                    Utils.Log($"Stop it trying: {(change.Enable ? "switched on" : "switched off")} {item.Kind} \"{item.Name}\" ({item.Path}).", Utils.LOG_ID_SERVICE);

                    if (!change.Enable && !string.IsNullOrEmpty(item.Path))
                        stopPaths.Add(item.Path);
                }
                catch (Exception e)
                {
                    ok = false;
                    Utils.LogException(e, Utils.LOG_ID_SERVICE);
                }
            }

            // Switching off only stops the next start. What is already running keeps retrying
            // until it exits, so close it now - this is the part the user actually notices.
            if (stopPaths.Count > 0)
                CloseRunning(stopPaths);

            return ok;
        }

        // ----------------------------------------------------------------- services

        private List<BackgroundItem> Services(Func<string, bool> belongs)
        {
            var items = new List<BackgroundItem>();
            using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var sc in ServiceController.GetServices())
            {
                using (sc)
                {
                    // One service we may not read must not hide the rest.
                    try
                    {
                        string imagePath;
                        using (var key = servicesKey?.OpenSubKey(sc.ServiceName))
                            imagePath = key?.GetValue("ImagePath") as string ?? string.Empty;

                        var path = BackgroundItemRules.ExecutableOf(imagePath, Environment.ExpandEnvironmentVariables);
                        if (!belongs(path))
                            continue;

                        items.Add(new BackgroundItem
                        {
                            Kind = BackgroundItemKind.Service,
                            Id = sc.ServiceName,
                            Name = sc.DisplayName,
                            Path = path,
                            Enabled = sc.StartType != ServiceStartMode.Disabled,
                            NeededToOpen = BackgroundItemRules.NeededToOpen(path),
                        });
                    }
                    catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }
                }
            }
            return items;
        }

        private static void SetService(string name, bool enable)
        {
            var state = LoadState();
            var key = StateKey(BackgroundItemKind.Service, name);
            using var scm = new ServiceControlManager();

            if (enable)
            {
                var mode = state.TryGetValue(key, out var previous) && Enum.TryParse<ServiceStartMode>(previous, out var parsed)
                    ? parsed
                    : ServiceStartMode.Manual;
                scm.SetStartupMode(name, mode);
                state.Remove(key);
                SaveState(state);

                if (mode == ServiceStartMode.Automatic)
                {
                    try { using var sc = new ServiceController(name); sc.Start(); }
                    catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }
                }
            }
            else
            {
                using (var sc = new ServiceController(name))
                {
                    if (!state.ContainsKey(key))
                    {
                        state[key] = sc.StartType.ToString();
                        SaveState(state);
                    }
                    scm.SetStartupMode(name, ServiceStartMode.Disabled);

                    try
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped && sc.CanStop)
                            sc.Stop();
                    }
                    catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }
                }
            }
        }

        /// <summary>Records that we switched a task or startup entry off, or forgets it when it is
        /// switched back on - so "Switch all back on" only undoes what SimpleDeFence did.</summary>
        private static void RememberSwitchedOff(BackgroundItemKind kind, string id, bool off)
        {
            var state = LoadState();
            var key = StateKey(kind, id);
            if (off)
                state[key] = "Off";
            else if (!state.Remove(key))
                return;
            SaveState(state);
        }

        private static string StateKey(BackgroundItemKind kind, string id) => $"{kind}|{id}";

        /// <summary>
        /// What SimpleDeFence switched off, one "Kind|Id=value" per line: for a service, the start
        /// type it had before; for anything else, "Off". Kept out of the configuration so a reset
        /// of the configuration cannot lose it, and in the service's own data directory, which
        /// only SYSTEM and Administrators can write.
        /// </summary>
        private static Dictionary<string, string> LoadState()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(StateFilePath))
                    return result;
                foreach (var line in File.ReadAllLines(StateFilePath))
                {
                    // The value never holds '=', an Id may (a startup entry's value name).
                    int eq = line.LastIndexOf('=');
                    if (eq > 0)
                        result[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }
            return result;
        }

        private static void SaveState(Dictionary<string, string> state)
        {
            File.WriteAllLines(StateFilePath, state.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        // ----------------------------------------------------------------- scheduled tasks

        private IEnumerable<BackgroundItem> Tasks(Func<string, bool> belongs)
        {
            var items = new List<BackgroundItem>();
            dynamic svc = TaskScheduler.ConnectedService();
            WalkTasks(svc.GetFolder(@"\"), belongs, items);
            return items;
        }

        private void WalkTasks(dynamic folder, Func<string, bool> belongs, List<BackgroundItem> items)
        {
            // Windows' own tasks live under \Microsoft and are never offered; skipping the folder
            // also skips most of the work.
            if (string.Equals((string)folder.Path, @"\Microsoft", StringComparison.OrdinalIgnoreCase))
                return;

            const int TASK_ENUM_HIDDEN = 1;
            // Indexed (1-based) rather than foreach: enumerating a COM collection through dynamic
            // has not always been supported on .NET, its default indexer has.
            dynamic tasks = folder.GetTasks(TASK_ENUM_HIDDEN);
            int taskCount = tasks.Count;
            for (int t = 1; t <= taskCount; ++t)
            {
                try
                {
                    dynamic task = tasks[t];
                    string? path = null;
                    dynamic actions = task.Definition.Actions;
                    int actionCount = actions.Count;
                    for (int a = 1; a <= actionCount; ++a)
                    {
                        dynamic action = actions[a];
                        if ((int)action.Type != TaskActionType.Exec)
                            continue;
                        path = BackgroundItemRules.ExecutableOf((string)action.Path, Environment.ExpandEnvironmentVariables);
                        break;
                    }

                    if (path is null || !belongs(path))
                        continue;

                    items.Add(new BackgroundItem
                    {
                        Kind = BackgroundItemKind.ScheduledTask,
                        Id = (string)task.Path,
                        Name = (string)task.Name,
                        Path = path,
                        Enabled = (bool)task.Enabled,
                        NeededToOpen = BackgroundItemRules.NeededToOpen(path),
                    });
                }
                catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }
            }

            dynamic folders = folder.GetFolders(0);
            int folderCount = folders.Count;
            for (int f = 1; f <= folderCount; ++f)
                WalkTasks(folders[f], belongs, items);
        }

        private static void SetTask(string taskPath, bool enable)
        {
            dynamic svc = TaskScheduler.ConnectedService();
            dynamic task = svc.GetFolder(@"\").GetTask(taskPath);
            task.Enabled = enable;
            RememberSwitchedOff(BackgroundItemKind.ScheduledTask, taskPath, !enable);
            if (!enable)
            {
                try { task.Stop(0); }
                catch { }
            }
        }

        // ----------------------------------------------------------------- startup entries

        /// <summary>A Run key and the StartupApproved key that switches its entries on and off.</summary>
        private sealed record RunLocation(RegistryKey Root, string RootName, string RunKey, string ApprovedKey);

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        /// <summary>The machine's Run keys (64- and 32-bit) and those of every signed-in user. The
        /// service runs as SYSTEM, so "the current user" would only ever be SYSTEM itself.</summary>
        private static IEnumerable<RunLocation> RunLocations()
        {
            var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            yield return new RunLocation(hklm, "HKLM", RunKey, ApprovedRun);
            yield return new RunLocation(hklm, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", ApprovedRun + "32");

            var hku = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            foreach (var sid in hku.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return new RunLocation(hku, "HKU", sid + "\\" + RunKey, sid + "\\" + ApprovedRun);
            }
        }

        private List<BackgroundItem> StartupEntries(Func<string, bool> belongs)
        {
            var items = new List<BackgroundItem>();
            foreach (var loc in RunLocations())
            {
                try
                {
                    using var run = loc.Root.OpenSubKey(loc.RunKey);
                    if (run is null)
                        continue;
                    using var approved = loc.Root.OpenSubKey(loc.ApprovedKey);

                    foreach (var valueName in run.GetValueNames())
                    {
                        var path = BackgroundItemRules.ExecutableOf(run.GetValue(valueName) as string, Environment.ExpandEnvironmentVariables);
                        if (!belongs(path))
                            continue;

                        items.Add(new BackgroundItem
                        {
                            Kind = BackgroundItemKind.Startup,
                            Id = $"{loc.RootName}\\{loc.RunKey}|{valueName}",
                            Name = valueName,
                            Path = path,
                            Enabled = !BackgroundItemRules.StartupApprovedIsOff(approved?.GetValue(valueName) as byte[]),
                            NeededToOpen = BackgroundItemRules.NeededToOpen(path),
                        });
                    }
                }
                catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }
            }
            return items;
        }

        private static void SetStartupEntry(string id, bool enable)
        {
            foreach (var loc in RunLocations())
            {
                var prefix = $"{loc.RootName}\\{loc.RunKey}|";
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var valueName = id.Substring(prefix.Length);
                using var approved = loc.Root.CreateSubKey(loc.ApprovedKey);
                approved.SetValue(valueName, BackgroundItemRules.StartupApprovedValue(enable, DateTime.UtcNow), RegistryValueKind.Binary);
                RememberSwitchedOff(BackgroundItemKind.Startup, id, !enable);
                return;
            }
            throw new ArgumentException($"Unknown startup entry \"{id}\".");
        }

        // ----------------------------------------------------------------- helpers

        private bool FromPublisher(string path, string publisher)
        {
            if (string.IsNullOrEmpty(path)
                || BackgroundItemRules.IsInWindowsFolder(path, WindowsDirectory)
                || !File.Exists(path))
                return false;

            var (valid, subject) = SignatureOf(path);
            return valid
                && !string.IsNullOrEmpty(subject)
                && BackgroundItemRules.SamePublisher(PublisherTrust.DisplayName(subject!), publisher);
        }

        private bool InFolder(string path, string folder)
        {
            if (string.IsNullOrEmpty(path)
                || BackgroundItemRules.IsInWindowsFolder(path, WindowsDirectory)
                || !BackgroundItemRules.IsUnder(path, folder)
                || !File.Exists(path))
                return false;

            // Anything validly signed by a publisher whose items are never offered - Microsoft -
            // stays out, wherever it was installed.
            var (valid, subject) = SignatureOf(path);
            return !(valid && !string.IsNullOrEmpty(subject)
                     && !BackgroundItemRules.MayQuiet(PublisherTrust.DisplayName(subject!)));
        }

        private static void CloseRunning(List<string> paths)
        {
            var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id <= 4)
                            continue;
                        var path = Utils.GetPathOfProcess((uint)p.Id);
                        if (!string.IsNullOrEmpty(path) && wanted.Contains(path))
                            p.Kill();
                    }
                    catch
                    {
                        // Already gone, or protected; it will not start again next time either way.
                    }
                }
            }
        }
    }
}
