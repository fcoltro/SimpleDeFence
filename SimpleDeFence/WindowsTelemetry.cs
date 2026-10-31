using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;
using SimpleDeFence.Windows.Services;

namespace SimpleDeFence
{
    /// <summary>
    /// <see cref="ITelemetrySystem"/> against the real machine. Runs inside the service, so as
    /// LocalSystem: enough to change the telemetry services, the Microsoft task folders and the
    /// HKLM policy keys.
    /// </summary>
    internal sealed class WindowsTelemetrySystem : ITelemetrySystem
    {
        private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

        // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) / (ERROR_PATH_NOT_FOUND): no such task or folder.
        private const int HR_FILE_NOT_FOUND = unchecked((int)0x80070002);
        private const int HR_PATH_NOT_FOUND = unchecked((int)0x80070003);

        public uint? GetServiceStartMode(string service)
        {
            try
            {
                using var scm = new ServiceControlManager();
                return scm.GetStartupMode(service);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == ERROR_SERVICE_DOES_NOT_EXIST)
            {
                return null;
            }
        }

        public void SetServiceStartMode(string service, uint startMode)
        {
            using var scm = new ServiceControlManager();
            scm.SetStartupMode(service, (ServiceStartMode)startMode);
        }

        public void StopService(string service)
        {
            using var sc = new ServiceController(service);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                return;

            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        }

        public bool? GetTaskEnabled(string taskPath)
        {
            try
            {
                return (bool)OpenTask(taskPath).Enabled;
            }
            catch (System.Runtime.InteropServices.COMException e) when (e.HResult is HR_FILE_NOT_FOUND or HR_PATH_NOT_FOUND)
            {
                return null;
            }
        }

        public void SetTaskEnabled(string taskPath, bool enabled)
        {
            OpenTask(taskPath).Enabled = enabled;
        }

        public int? GetPolicyValue(string key, string name)
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(name) is int value ? value : null;
        }

        public void SetPolicyValue(string key, string name, int? value)
        {
            if (value is int v)
            {
                using var k = Registry.LocalMachine.CreateSubKey(key, writable: true);
                k.SetValue(name, v, RegistryValueKind.DWord);
            }
            else
            {
                using var k = Registry.LocalMachine.OpenSubKey(key, writable: true);
                k?.DeleteValue(name, throwOnMissingValue: false);
            }
        }

        private static dynamic OpenTask(string taskPath)
        {
            int split = taskPath.LastIndexOf('\\');
            string folder = (split <= 0) ? @"\" : taskPath.Substring(0, split);
            string name = taskPath.Substring(split + 1);

            dynamic taskService = TaskScheduler.ConnectedService();
            return taskService.GetFolder(folder).GetTask(name);
        }
    }

    /// <summary>
    /// The service's end of "Reduce Windows telemetry": where the backup lives, what the Settings
    /// page is told, and which firewall rules go with it. Only ever used from the service's worker
    /// thread, like the rest of the state ProcessCmd touches.
    /// </summary>
    internal sealed class TelemetryMitigationHost
    {
        private static readonly string BackupPath = Path.Combine(Utils.AppDataPath, "telemetry-backup.json");

        private readonly ITelemetrySystem Machine = new WindowsTelemetrySystem();

        /// <summary>The firewall half follows the backup file: while there is something to undo,
        /// the telemetry programs stay blocked.</summary>
        public bool BlockRulesWanted => File.Exists(BackupPath);

        public TelemetryStatus Status { get; private set; } = TelemetryStatus.Off;

        /// <summary>Re-reads the machine.</summary>
        public void Refresh()
        {
            Status = TelemetryMitigation.StatusOf(Machine, LoadBackup());
        }

        /// <summary>False when anything was refused; what was, is in the service log.</summary>
        public bool Apply()
        {
            var backup = TelemetryMitigation.CaptureOriginals(Machine, LoadBackup());

            // Saved before the first change, or a failure half way would leave changes that
            // nothing knows how to undo.
            SerializationHelper.SerializeToFile(backup, BackupPath);

            var failures = new List<string>();
            TelemetryMitigation.Apply(Machine, backup, failures);
            Log("Turned on Windows telemetry reduction", failures);
            Refresh();
            return failures.Count == 0;
        }

        public bool Undo()
        {
            var backup = LoadBackup();
            if (backup is null)
            {
                Refresh();
                return true;
            }

            var failures = new List<string>();
            TelemetryMitigation.Undo(Machine, backup, failures);

            // Whatever could not be put back stays listed, so Undo can be pressed again.
            if (backup.IsEmpty)
                File.Delete(BackupPath);
            else
                SerializationHelper.SerializeToFile(backup, BackupPath);

            Log("Undid Windows telemetry reduction", failures);
            Refresh();
            return failures.Count == 0;
        }

        /// <summary>Blocks for the telemetry services and programs, at blocklist weight so no
        /// allow rule - including one the user added - lets them through.</summary>
        public void AddBlockRules(List<RuleDef> rules, Guid modeId)
        {
            if (!BlockRulesWanted)
                return;

            string system32 = Environment.SystemDirectory;
            string svchost = Path.Combine(system32, "svchost.exe");

            foreach (var service in TelemetryMitigation.Services)
            {
                rules.Add(new RuleDef(modeId, "Windows telemetry", new ServiceSubject(svchost, service),
                    RuleAction.Block, RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.Blocklist));
            }

            foreach (var exe in TelemetryMitigation.BlockedExecutables)
            {
                rules.Add(new RuleDef(modeId, "Windows telemetry", new ExecutableSubject(Path.Combine(system32, exe)),
                    RuleAction.Block, RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.Blocklist));
            }
        }

        private static TelemetryBackup? LoadBackup()
        {
            if (!File.Exists(BackupPath))
                return null;

            try
            {
                return SerializationHelper.Deserialize(File.ReadAllBytes(BackupPath), new TelemetryBackup());
            }
            catch (Exception e)
            {
                // Kept on disk: it may still be readable by hand, and deleting it would throw away
                // the only record of what the machine looked like.
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                return new TelemetryBackup();
            }
        }

        private static void Log(string what, List<string> failures)
        {
            if (failures.Count == 0)
                Utils.Log(what + ".", Utils.LOG_ID_SERVICE);
            else
                Utils.Log(what + ", with failures:" + Environment.NewLine + string.Join(Environment.NewLine, failures), Utils.LOG_ID_SERVICE);
        }
    }
}
