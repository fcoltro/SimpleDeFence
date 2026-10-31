using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence
{
    /// <summary>Where "Reduce Windows telemetry" stands, as the Settings page shows it.</summary>
    public enum TelemetryStatus
    {
        /// <summary>Never turned on, or undone.</summary>
        Off,

        /// <summary>Turned on, and every change is still in place.</summary>
        On,

        /// <summary>Turned on, but something is back the way it was - a feature update turned a
        /// task back on, or a step was refused. Applying again puts it right.</summary>
        Partial,
    }

    /// <summary>
    /// The machine as the mitigation sees it. The service implements it against the SCM, Task
    /// Scheduler and the registry; tests implement it with dictionaries.
    /// </summary>
    public interface ITelemetrySystem
    {
        /// <summary>The service's start type (2 automatic, 3 manual, 4 disabled), or null when the
        /// service is not installed on this edition.</summary>
        uint? GetServiceStartMode(string service);
        void SetServiceStartMode(string service, uint startMode);
        void StopService(string service);

        /// <summary>Null when the task does not exist on this machine.</summary>
        bool? GetTaskEnabled(string taskPath);
        void SetTaskEnabled(string taskPath, bool enabled);

        /// <summary>A DWORD under HKEY_LOCAL_MACHINE, or null when the value is not there.</summary>
        int? GetPolicyValue(string key, string name);

        /// <summary>Null deletes the value.</summary>
        void SetPolicyValue(string key, string name, int? value);
    }

    /// <summary>A DWORD under HKEY_LOCAL_MACHINE and the value the mitigation sets it to.</summary>
    public sealed record TelemetryPolicyValue(string Key, string Name, int Value)
    {
        /// <summary>How the value is filed in <see cref="TelemetryBackup.Policies"/>.</summary>
        public string BackupKey => Key + "|" + Name;
    }

    /// <summary>
    /// What everything looked like before the mitigation touched it, so Undo can put it back.
    /// Written to disk before the first change is made: a mitigation that stops half way must
    /// still be undoable. Only items that exist on this machine are listed.
    /// </summary>
    public class TelemetryBackup : ISerializable<TelemetryBackup>
    {
        /// <summary>Service name to its original start type.</summary>
        public Dictionary<string, uint> Services = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Task path to whether it was enabled.</summary>
        public Dictionary<string, bool> Tasks = new(StringComparer.OrdinalIgnoreCase);

        /// <summary><see cref="TelemetryPolicyValue.BackupKey"/> to the original value, null
        /// when the value did not exist (Undo deletes it again).</summary>
        public Dictionary<string, int?> Policies = new(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty => (Services.Count == 0) && (Tasks.Count == 0) && (Policies.Count == 0);

        public JsonTypeInfo<TelemetryBackup> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.TelemetryBackup;
        }
    }

    /// <summary>
    /// "Reduce Windows telemetry": switches off the services, scheduled tasks and policy settings
    /// that collect and send diagnostic data, and (in the service's rule assembly) blocks the
    /// programs that do the sending.
    ///
    /// Nothing is deleted. The files are owned by TrustedInstaller, Windows Update and sfc put
    /// them back, and a missing system file can stop a cumulative update from installing. Turning
    /// them off reaches the same result and can be undone exactly.
    ///
    /// Everything here uses the switches Windows itself provides, so a later "Undo" or a Windows
    /// repair leaves the system in a state Windows knows about.
    /// </summary>
    public static class TelemetryMitigation
    {
        public const uint SERVICE_DISABLED = 4;

        /// <summary>Connected User Experiences and Telemetry, and the WAP push router it uses.</summary>
        public static readonly IReadOnlyList<string> Services = new[]
        {
            "DiagTrack",
            "dmwappushservice",
        };

        public static readonly IReadOnlyList<string> Tasks = new[]
        {
            @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            @"\Microsoft\Windows\Autochk\Proxy",
            @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
            @"\Microsoft\Windows\Device Information\Device",
            @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
            @"\Microsoft\Windows\Feedback\Siuf\DmClient",
            @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
        };

        private const string DataCollection = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";

        /// <summary>Machine-wide Group Policy values, the same ones gpedit.msc writes.</summary>
        public static readonly IReadOnlyList<TelemetryPolicyValue> Policies = new[]
        {
            // Diagnostic data at the lowest level the edition allows. Enterprise and Education
            // honour 0 (off); Home and Pro treat it as 1 (Required).
            new TelemetryPolicyValue(DataCollection, "AllowTelemetry", 0),
            new TelemetryPolicyValue(DataCollection, "DoNotShowFeedbackNotifications", 1),
            new TelemetryPolicyValue(DataCollection, "LimitDiagnosticLogCollection", 1),
            new TelemetryPolicyValue(DataCollection, "LimitDumpCollection", 1),
            new TelemetryPolicyValue(DataCollection, "DisableOneSettingsDownloads", 1),
            new TelemetryPolicyValue(@"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1),
            new TelemetryPolicyValue(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0),
            new TelemetryPolicyValue(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
            new TelemetryPolicyValue(@"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
            new TelemetryPolicyValue(@"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "AITEnable", 0),
            new TelemetryPolicyValue(@"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableInventory", 1),
            new TelemetryPolicyValue(@"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0),
        };

        /// <summary>Programs in System32 that collect and upload telemetry. The service blocks
        /// them, and the telemetry services above, while the mitigation is on - so data already
        /// queued cannot leave even if Windows turns something back on.</summary>
        public static readonly IReadOnlyList<string> BlockedExecutables = new[]
        {
            "CompatTelRunner.exe",
            "DeviceCensus.exe",
        };

        /// <summary>
        /// Records the current state of everything the mitigation is about to change. On a
        /// second apply the existing backup is kept as it is and only items it does not list yet
        /// are added: reading them again would record the mitigation's own values as the
        /// "originals", and Undo would then restore nothing.
        /// </summary>
        public static TelemetryBackup CaptureOriginals(ITelemetrySystem sys, TelemetryBackup? existing)
        {
            var backup = existing ?? new TelemetryBackup();

            foreach (var service in Services)
            {
                if (!backup.Services.ContainsKey(service) && Try(() => sys.GetServiceStartMode(service)) is uint mode)
                    backup.Services[service] = mode;
            }

            foreach (var task in Tasks)
            {
                if (!backup.Tasks.ContainsKey(task) && Try(() => sys.GetTaskEnabled(task)) is bool enabled)
                    backup.Tasks[task] = enabled;
            }

            foreach (var policy in Policies)
            {
                if (!backup.Policies.ContainsKey(policy.BackupKey))
                    backup.Policies[policy.BackupKey] = Try(() => sys.GetPolicyValue(policy.Key, policy.Name));
            }

            return backup;
        }

        /// <summary>Makes every change for the items <paramref name="backup"/> lists. Keeps going
        /// past a refusal; each one is described in <paramref name="failures"/>.</summary>
        public static void Apply(ITelemetrySystem sys, TelemetryBackup backup, List<string> failures)
        {
            foreach (var service in backup.Services.Keys)
            {
                Do(failures, $"service {service}", () =>
                {
                    sys.SetServiceStartMode(service, SERVICE_DISABLED);
                    sys.StopService(service);
                });
            }

            foreach (var task in backup.Tasks.Keys)
                Do(failures, $"task {task}", () => sys.SetTaskEnabled(task, false));

            foreach (var policy in Policies)
            {
                if (backup.Policies.ContainsKey(policy.BackupKey))
                    Do(failures, $"policy {policy.BackupKey}", () => sys.SetPolicyValue(policy.Key, policy.Name, policy.Value));
            }
        }

        /// <summary>
        /// Puts back what <paramref name="backup"/> recorded, removing each item from it once
        /// restored. Whatever is left afterwards could not be restored; the caller keeps that much
        /// so Undo can be tried again, and deletes the backup when nothing is left.
        /// </summary>
        public static void Undo(ITelemetrySystem sys, TelemetryBackup backup, List<string> failures)
        {
            foreach (var pair in new List<KeyValuePair<string, uint>>(backup.Services))
            {
                // Started again only by its own trigger or the next boot, the same as any service
                // whose start type changes; starting it here would be a surprise.
                if (Do(failures, $"service {pair.Key}", () => sys.SetServiceStartMode(pair.Key, pair.Value)))
                    backup.Services.Remove(pair.Key);
            }

            foreach (var pair in new List<KeyValuePair<string, bool>>(backup.Tasks))
            {
                if (Do(failures, $"task {pair.Key}", () => sys.SetTaskEnabled(pair.Key, pair.Value)))
                    backup.Tasks.Remove(pair.Key);
            }

            foreach (var policy in Policies)
            {
                if (backup.Policies.TryGetValue(policy.BackupKey, out int? original)
                    && Do(failures, $"policy {policy.BackupKey}", () => sys.SetPolicyValue(policy.Key, policy.Name, original)))
                {
                    backup.Policies.Remove(policy.BackupKey);
                }
            }
        }

        /// <summary>On when every item the backup lists is still the way the mitigation set it.
        /// A check that throws counts as not in place.</summary>
        public static TelemetryStatus StatusOf(ITelemetrySystem sys, TelemetryBackup? backup)
        {
            if (backup is null)
                return TelemetryStatus.Off;

            foreach (var service in backup.Services.Keys)
            {
                if (Try(() => sys.GetServiceStartMode(service)) is uint mode && mode != SERVICE_DISABLED)
                    return TelemetryStatus.Partial;
            }

            foreach (var task in backup.Tasks.Keys)
            {
                // A task that has gone altogether is not sending anything either.
                if (Try(() => sys.GetTaskEnabled(task)) == true)
                    return TelemetryStatus.Partial;
            }

            foreach (var policy in Policies)
            {
                if (backup.Policies.ContainsKey(policy.BackupKey) && Try(() => sys.GetPolicyValue(policy.Key, policy.Name)) != policy.Value)
                    return TelemetryStatus.Partial;
            }

            return TelemetryStatus.On;
        }

        private static T? Try<T>(Func<T?> read) where T : struct
        {
            try { return read(); }
            catch { return null; }
        }

        private static bool Do(List<string> failures, string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                failures.Add($"{what}: {e.Message}");
                return false;
            }
        }
    }
}
