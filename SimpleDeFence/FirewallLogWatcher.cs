using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using SimpleDeFence.Utilities;
using SimpleDeFence.Windows;

namespace SimpleDeFence
{
    internal class FirewallLogWatcher : Disposable
    {
        //private readonly string FIREWALLLOG_PATH = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"LogFiles\Firewall\pfirewall.log");
        private readonly EventLogWatcher LogWatcher;

        public delegate void NewLogEntryDelegate(FirewallLogWatcher sender, FirewallLogEntry entry);
        public event NewLogEntryDelegate? NewLogEntry;

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return; 
            
            if (disposing)
            {
                // Release managed resources

                LogWatcher.Dispose();
            }

            // Release unmanaged resources.
            // Set large fields to null.
            // Call Dispose on your base class.
            //
            // Deliberately does NOT touch the audit policy any more - see AuditPolicy below.
            // The service owns it for its whole lifetime, and tearing it down from here would
            // switch off the Blocked list's data source.

            base.Dispose(disposing);
        }

        ~FirewallLogWatcher() => Dispose(false);

        internal FirewallLogWatcher()
        {
            // Create event notifier
            EventLogQuery evquery = new("Security", PathType.LogName, "*[System[(EventID=5154 or EventID=5155 or EventID=5157 or EventID=5159 or EventID=5156 or EventID=5158)]]");
            LogWatcher = new EventLogWatcher(evquery) { Enabled = false };
            LogWatcher.EventRecordWritten += new EventHandler<EventRecordWrittenEventArgs>(LogWatcher_EventRecordWritten);
        }

        internal bool Enabled
        {
            get 
            {
                return LogWatcher.Enabled;
            }

            set
            {
                // Only the Security-log subscription that feeds auto-learn. It used to switch the
                // system audit policy on and off alongside itself, which conflated two unrelated
                // lifetimes: this watcher is wanted in Learning mode only, while the audit policy
                // is what makes WFP raise the classify-drop events the Blocked list is built from,
                // and that is wanted whenever the service runs. Leaving Learning mode therefore
                // turned off the Blocked list's only data source. AuditPolicy is now the service's
                // to own - see SimpleDeFenceService.Run.
                if (value != LogWatcher.Enabled)
                    LogWatcher.Enabled = value;
            }
        }

        private static FirewallLogEntry ParseLogEntry(EventRecordWrittenEventArgs e)
        {
            var entry = new FirewallLogEntry
            {
                Timestamp = DateTime.Now,
                Event = (EventLogEvent)e.EventRecord.Id
            };

            switch (e.EventRecord.Id)
            {
                case 5154:
                case 5155:
                case 5158:
                case 5159:
                    entry.ProcessId = (uint)(ulong)e.EventRecord.Properties[0].Value;
                    entry.AppPath = (string)e.EventRecord.Properties[1].Value;
                    entry.LocalIp = (string)e.EventRecord.Properties[2].Value;
                    entry.LocalPort = int.Parse((string)e.EventRecord.Properties[3].Value);
                    entry.Protocol = (Protocol)(uint)e.EventRecord.Properties[4].Value;
                    entry.RemoteIp = string.Empty;
                    entry.RemotePort = 0;
                    break;
                case 5156:
                case 5157:
                default:
                    entry.ProcessId = (uint)(ulong)e.EventRecord.Properties[0].Value;
                    entry.AppPath = (string)e.EventRecord.Properties[1].Value;
                    entry.Protocol = (Protocol)(uint)e.EventRecord.Properties[7].Value;
                    switch ((string)e.EventRecord.Properties[2].Value)
                    {
                        case "%%14592":
                            entry.Direction = RuleDirection.In;
                            entry.RemoteIp = (string)e.EventRecord.Properties[3].Value;
                            entry.RemotePort = int.Parse((string)e.EventRecord.Properties[4].Value);
                            entry.LocalIp = (string)e.EventRecord.Properties[5].Value;
                            entry.LocalPort = int.Parse((string)e.EventRecord.Properties[6].Value);
                            break;
                        case "%%14593":
                            entry.Direction = RuleDirection.Out;
                            entry.LocalIp = (string)e.EventRecord.Properties[3].Value;
                            entry.LocalPort = int.Parse((string)e.EventRecord.Properties[4].Value);
                            entry.RemoteIp = (string)e.EventRecord.Properties[5].Value;
                            entry.RemotePort = int.Parse((string)e.EventRecord.Properties[6].Value);
                            break;
                        default:
                            entry.Direction = RuleDirection.Invalid;
                            break;
                    }
                    break;
            }

            // Convert path to Win32 format
            entry.AppPath = PathMapper.Instance.ConvertPathIgnoreErrors(entry.AppPath, PathFormat.Win32);

            // Correct casing of app path
            entry.AppPath = Utils.GetExactPath(entry.AppPath);

            // Replace invalid IP strings with the "unspecified address" IPv6 specifier
            if (string.IsNullOrEmpty(entry.RemoteIp))
                entry.RemoteIp = "::";
            if (string.IsNullOrEmpty(entry.LocalIp))
                entry.LocalIp = "::";

            return entry;
        }

        void LogWatcher_EventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            try
            {
                NewLogEntry?.Invoke(this, ParseLogEntry(e));
            }
            catch { }
            finally
            {
                e.EventRecord?.Dispose();
            }
        }

        private static class NativeMethods
        {
            [Flags]
            internal enum AuditingInformationEnum : uint
            {
                POLICY_AUDIT_EVENT_UNCHANGED = 0,
                POLICY_AUDIT_EVENT_SUCCESS = 1,
                POLICY_AUDIT_EVENT_FAILURE = 2,
                POLICY_AUDIT_EVENT_NONE = 4,
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct AUDIT_POLICY_INFORMATION
            {
                internal Guid AuditSubCategoryGuid;
                internal AuditingInformationEnum AuditingInformation;
                internal Guid AuditCategoryGuid;
            }

            [DllImport("advapi32", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool AuditSetSystemPolicy([In] ref AUDIT_POLICY_INFORMATION pAuditPolicy, uint policyCount);

            [DllImport("advapi32", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            internal static extern bool AuditQuerySystemPolicy([In] ref Guid pSubCategoryGuids, uint policyCount, out IntPtr ppAuditPolicy);

            [DllImport("advapi32")]
            internal static extern void AuditFree(IntPtr buffer);
        }

        private static readonly Guid PACKET_LOGGING_AUDIT_SUBCAT = new("{0CCE9225-69AE-11D9-BED3-505054503030}");
        private static readonly Guid CONNECTION_LOGGING_AUDIT_SUBCAT = new("{0CCE9226-69AE-11D9-BED3-505054503030}");

        private static void AuditSetSystemPolicy(Guid guid, bool success, bool failure)
        {
            var pol = new NativeMethods.AUDIT_POLICY_INFORMATION
            {
                AuditCategoryGuid = guid,
                AuditSubCategoryGuid = guid
            };
            if (success || failure)
            {
                if (success)
                    pol.AuditingInformation |= NativeMethods.AuditingInformationEnum.POLICY_AUDIT_EVENT_SUCCESS;
                if (failure)
                    pol.AuditingInformation |= NativeMethods.AuditingInformationEnum.POLICY_AUDIT_EVENT_FAILURE;
            }
            else
                pol.AuditingInformation = NativeMethods.AuditingInformationEnum.POLICY_AUDIT_EVENT_NONE;

            if (!NativeMethods.AuditSetSystemPolicy(ref pol, 1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>What a subcategory was set to before we touched it, so it can be put back
        /// exactly rather than switched off. Null when it could not be read.</summary>
        internal readonly record struct AuditSetting(bool Success, bool Failure);

        private static AuditSetting? AuditQuerySystemPolicy(Guid guid)
        {
            var subcat = guid;
            if (!NativeMethods.AuditQuerySystemPolicy(ref subcat, 1, out IntPtr buffer))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            if (buffer == IntPtr.Zero)
                return null;

            try
            {
                var pol = Marshal.PtrToStructure<NativeMethods.AUDIT_POLICY_INFORMATION>(buffer);
                return new AuditSetting(
                    (pol.AuditingInformation & NativeMethods.AuditingInformationEnum.POLICY_AUDIT_EVENT_SUCCESS) != 0,
                    (pol.AuditingInformation & NativeMethods.AuditingInformationEnum.POLICY_AUDIT_EVENT_FAILURE) != 0);
            }
            finally
            {
                NativeMethods.AuditFree(buffer);
            }
        }

        /// <summary>
        /// The two Windows audit subcategories that make the Filtering Platform report what it
        /// dropped and what it let through: "Filtering Platform Packet Drop" and "Filtering
        /// Platform Connection".
        ///
        /// These gate the events the whole Blocked section is built from, so they belong to the
        /// service's lifetime rather than to any one firewall mode. They used to be switched on
        /// and off by FirewallLogWatcher.Enabled, which is set from
        /// `LogWatcher.Enabled = (FirewallMode.Learning == newMode)` and nowhere else - so on an
        /// installation that never entered Learning mode they were never enabled at all, and on
        /// one that left Learning mode they were switched back off. Either way the WFP net-event
        /// callback stopped being called, FirewallLogEntries stayed empty, and the Connections
        /// screen's Blocked list was empty for ever - with no error anywhere, because nothing had
        /// failed.
        /// </summary>
        internal static class AuditPolicy
        {
            /// <summary>
            /// What each subcategory was set to before <see cref="Enable"/> changed it.
            ///
            /// Disable used to force both subcategories to "no auditing" unconditionally, having
            /// never read what they were. On a machine where an administrator or a Group Policy had
            /// switched Filtering Platform auditing on for their own reasons, stopping or
            /// uninstalling this service silently turned that off and never put it back - while the
            /// comment at the call site claimed the machine was left as it was found. These are
            /// machine-wide audit settings we are borrowing, not ours to reset.
            ///
            /// Empty when the query failed, in which case Disable leaves the policy alone rather
            /// than guessing: not knowing what it was is a reason to touch nothing, not a reason to
            /// switch it off.
            /// </summary>
            private static readonly Dictionary<Guid, AuditSetting> PreviousSettings = new();

            internal static void Enable() => EnableLogging();
            internal static void Disable() => DisableLogging();

            internal static void RememberPrevious(Guid subcategory, AuditSetting? setting)
            {
                if (setting.HasValue)
                    PreviousSettings[subcategory] = setting.Value;
            }

            internal static bool TryGetPrevious(Guid subcategory, out AuditSetting setting)
                => PreviousSettings.TryGetValue(subcategory, out setting);
        }

        private static readonly Guid[] LoggingAuditSubcategories =
        {
            PACKET_LOGGING_AUDIT_SUBCAT,
            CONNECTION_LOGGING_AUDIT_SUBCAT,
        };

        private static void EnableLogging()
        {
            try
            {
                Privilege.RunWithPrivilege(Privilege.Security, true, delegate (object? state)
                {
                    foreach (var subcat in LoggingAuditSubcategories)
                    {
                        // Read before writing, so Disable has something to put back. A failure to
                        // read is recorded as "unknown" and makes Disable leave this subcategory
                        // alone; enabling still goes ahead, because the Blocked list depends on it.
                        try { AuditPolicy.RememberPrevious(subcat, AuditQuerySystemPolicy(subcat)); }
                        catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_SERVICE); }

                        AuditSetSystemPolicy(subcat, true, true);
                    }
                }, null);
            }
            catch { }
        }

        private static void DisableLogging()
        {
            try
            {
                Privilege.RunWithPrivilege(Privilege.Security, true, delegate (object? state)
                {
                    foreach (var subcat in LoggingAuditSubcategories)
                    {
                        if (!AuditPolicy.TryGetPrevious(subcat, out var previous))
                            continue;

                        AuditSetSystemPolicy(subcat, previous.Success, previous.Failure);
                    }
                }, null);
            }
            catch { }
        }
    }
}
