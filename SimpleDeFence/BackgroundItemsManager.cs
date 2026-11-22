using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;
using SimpleDeFence.Windows.Services;

namespace SimpleDeFence
{
    /// <summary>
    /// What is left of "Stop it trying" (1.1.0), which switched off the services, scheduled
    /// tasks and startup entries of a blocked program's publisher. The feature was removed in the
    /// next release because it did not work well enough. With its screen gone, nobody could switch
    /// those items back on from the app, so on start the service turns back on everything that
    /// feature switched off, once, and then forgets it.
    ///
    /// It only ever undoes what SimpleDeFence itself did: the list is the background-items.txt
    /// file the feature kept, with each service's previous start type.
    /// </summary>
    internal static class BackgroundItemsManager
    {
        private static readonly string StateFilePath = Path.Combine(Utils.AppDataPath, "background-items.txt");

        private static int s_restoring;

        /// <summary>True while something is still waiting to be switched back on.</summary>
        internal static bool HasPending => File.Exists(StateFilePath);

        /// <summary>
        /// Switches back on every item "Stop it trying" switched off. Never throws, and does
        /// nothing if another call is already at it.
        ///
        /// An item that cannot be switched on yet stays in the file and is tried again later (the
        /// service retries every ten minutes while the file exists). This runs at boot, before
        /// anyone signs in: a user's own startup entries live in their registry hive, which is
        /// not loaded until they sign in, and Task Scheduler may not be up yet either. Dropping
        /// those on the first failure turned them off for good. Only an item that is really gone
        /// (its service or task was uninstalled, or its user account deleted) is dropped.
        /// </summary>
        internal static void RestoreAll()
        {
            if (Interlocked.Exchange(ref s_restoring, 1) == 1)
                return;
            try
            {
                var state = LoadState();
                foreach (var key in state.Keys.ToList())
                {
                    int bar = key.IndexOf('|');
                    if (bar <= 0 || !Enum.TryParse<BackgroundItemKind>(key.Substring(0, bar), out var kind))
                    {
                        Forget(key);
                        continue;
                    }
                    var id = key.Substring(bar + 1);
                    try
                    {
                        switch (kind)
                        {
                            case BackgroundItemKind.Service: SetService(id, true); break;
                            case BackgroundItemKind.ScheduledTask: SetTask(id, true); break;
                            case BackgroundItemKind.Startup: SetStartupEntry(id, true); break;
                        }
                        Utils.Log($"Stop it trying was removed: switched {kind} \"{id}\" back on.", Utils.LOG_ID_SERVICE);
                    }
                    catch (Exception e) when (IsGone(kind, id, e))
                    {
                        Utils.Log($"Stop it trying was removed: {kind} \"{id}\" no longer exists; dropping it.", Utils.LOG_ID_SERVICE);
                        Forget(key);
                    }
                    catch (Exception)
                    {
                        // Not yet: the user is not signed in, or Task Scheduler is not up. Left in
                        // the file for the next try.
                    }
                }

                if (File.Exists(StateFilePath) && LoadState().Count == 0)
                    File.Delete(StateFilePath);
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
            }
            finally
            {
                Volatile.Write(ref s_restoring, 0);
            }
        }

        private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
        private const int HR_FILE_NOT_FOUND = unchecked((int)0x80070002);
        private const int HR_PATH_NOT_FOUND = unchecked((int)0x80070003);

        /// <summary>Whether a failure to switch an item back on means it no longer exists, rather
        /// than that it cannot be reached yet.</summary>
        private static bool IsGone(BackgroundItemKind kind, string id, Exception e)
        {
            switch (kind)
            {
                case BackgroundItemKind.Service:
                    return e is System.ComponentModel.Win32Exception w && w.NativeErrorCode == ERROR_SERVICE_DOES_NOT_EXIST;
                case BackgroundItemKind.ScheduledTask:
                    return e is System.Runtime.InteropServices.COMException c && (c.HResult == HR_FILE_NOT_FOUND || c.HResult == HR_PATH_NOT_FOUND);
                case BackgroundItemKind.Startup:
                    // "HKU\<sid>\...": gone only when that account has no profile on this PC any
                    // more. A profile that exists but is not loaded just means nobody signed in.
                    if (!id.StartsWith(@"HKU\", StringComparison.OrdinalIgnoreCase))
                        return false;
                    var rest = id.Substring(4);
                    int slash = rest.IndexOf('\\');
                    var sid = slash > 0 ? rest.Substring(0, slash) : rest;
                    using (var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                    using (var profile = profiles?.OpenSubKey(sid))
                        return profiles is not null && profile is null;
                default:
                    return true;
            }
        }

        private static void Forget(string key)
        {
            var state = LoadState();
            if (state.Remove(key))
                SaveState(state);
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
    }
}
