using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Net;
using System.Net.NetworkInformation;
using System.Management;
using System.Threading;
using SimpleDeFence.Windows;
using SimpleDeFence.Windows.Services;
using SimpleDeFence.Windows.WFP;
using SimpleDeFence.Windows.WFP.Interop;
using SimpleDeFence.Utilities;

namespace SimpleDeFence
{
    /// <summary>
    /// Thrown when WFP refuses a rule whose job is to deny traffic. Its own type because the
    /// caller has to treat it differently from any other failure: the transaction must not be
    /// committed, since committing a rule set whose default-deny is missing is how a firewall ends
    /// up permissive while reporting success.
    /// </summary>
    public sealed class BlockRuleInstallException : Exception
    {
        public BlockRuleInstallException(int refusedCount)
            : base($"WFP refused {refusedCount} block rule(s); the assembled rule set was not applied.")
        { }
    }

    public sealed class SimpleDeFenceServer : IDisposable
    {
        private static readonly Guid TINYWALL_PROVIDER_KEY = new("{69E15520-9A9F-409E-AA6A-2F009D6B7295}");

        private readonly BlockingCollection<TwRequest> Q = new(32);
        private readonly PipeServerEndpoint ServerPipe;
        private readonly Timer MinuteTimer;

        /// <summary>
        /// The firewall's recent event ring, and the only thing the Connections screen's Blocked
        /// list is built from.
        ///
        /// The depth is bounded by what READ_FW_LOG can actually deliver, not by how much history
        /// would be nice to have. The whole ring is serialised into a single pipe message on every
        /// read, as indented JSON, and the client reads it back in 4 KB chunks with a one-second
        /// timeout on each chunk after the first. Measured: 500 entries is ~176 KB and 42 chunks;
        /// 5000 is ~1.7 MB and 429. Raising it to 5000 pushed the response past what that read
        /// reliably completes while the service is busy logging, and the failure is silent -
        /// Controller.EndReadFwLog turns any non-ReadFwLog reply into an empty array, which the
        /// Connections screen renders as "nothing blocked". The symptom was a Blocked list that
        /// worked for the first few seconds after the service started, while the ring was still
        /// nearly empty, and was empty for ever afterwards. Do not raise this without also fixing
        /// how the log is transported.
        ///
        /// Only drops are kept, which is what makes 500 go further than it used to. Every
        /// consumer of this ring - GetFwLog, READ_FW_LOG, ConnectionActivity.RecentBlocked -
        /// discards the allowed events anyway, so carrying them only spent the depth that the
        /// blocked entries needed: on an ordinary network the inbound broadcast and multicast
        /// traffic that EventMatchAnyKeywords asks for arrives constantly, and it was evicting the
        /// outbound application blocks the user opened the screen to find.
        /// </summary>
        private readonly CircularBuffer<FirewallLogEntry> FirewallLogEntries = new(500);
        private readonly FileLocker FileLocker = new();
        private readonly HostsFileManager HostsFileManager = new();
        private DateTime LastControllerCommandTime = DateTime.Now;
        private DateTime LastRuleReloadTime = DateTime.Now;

        // Context needed for learning mode
        private readonly FirewallLogWatcher LogWatcher = new();
        private readonly List<FirewallExceptionV3> LearningNewExceptions = new();

        // Context for auto rule inheritance
        private readonly object InheritanceGuard = new();
        private readonly HashSet<string> UserSubjectExes = new(StringComparer.OrdinalIgnoreCase);        // All executables with pre-configured rules.
        private readonly Dictionary<string, List<FirewallExceptionV3>> ChildInheritance = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> ChildInheritedSubjectExes = new(StringComparer.OrdinalIgnoreCase);   // Executables that have been already auto-whitelisted due to inheritance
        private readonly ThreadThrottler FirewallThreadThrottler = new(Thread.CurrentThread, ThreadPriority.Highest, false);
        private StringBuilder? ProcessStartWatcher_Sbuilder;

        private bool RunService = false;
        private bool DisplayCurrentlyOn = true;
        private readonly ServerState VisibleState = new();

        private readonly Engine WfpEngine = new("SimpleDeFence Session", "", FWPM_SESSION_FLAGS.None, 5000);
        private readonly ManagementEventWatcher ProcessStartWatcher = new(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        private readonly EventMerger RuleReloadEventMerger = new(1000);

        private HashSet<IpAddrMask> LocalSubnetAddreses = new();
        private HashSet<IpAddrMask> GatewayAddresses = new();
        private HashSet<IpAddrMask> DnsAddresses = new();
        private readonly FilterConditionList LocalSubnetFilterConditions = new();
        private readonly FilterConditionList GatewayFilterConditions = new();
        private readonly FilterConditionList DnsFilterConditions = new();

        private List<RuleDef> AssembleActiveRules(List<RuleDef> rawSocketExceptions)
        {
            using var timer = new HierarchicalStopwatch("AssembleActiveRules()");
            var rules = new List<RuleDef>();
            var ModeId = Guid.NewGuid();

            // Do we want to let local traffic through?
            if (ActiveConfig.Service.ActiveProfile.AllowLocalSubnet)
            {
                var def = new RuleDef(ModeId, "Allow local subnet", GlobalSubject.Instance, RuleAction.Allow, RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.DefaultPermit)
                {
                    RemoteAddresses = RuleDef.LOCALSUBNET_ID
                };
                rules.Add(def);
            }

            // Do we want to block known malware ports?
            if (ActiveConfig.Service.Blocklists.EnableBlocklists && ActiveConfig.Service.Blocklists.EnablePortBlocklist)
            {
                var exceptions = new List<FirewallExceptionV3>();
                exceptions.AddRange(CollectExceptionsForAppByName("Malware Ports"));
                foreach (var ex in exceptions)
                {
                    ex.RegenerateId();
                    GetRulesForException(ex, rules, rawSocketExceptions, (ulong)FilterWeights.DefaultPermit, (ulong)FilterWeights.Blocklist);
                }
            }

            // Rules specific to the selected firewall mode. The decision itself lives in
            // SimpleDeFence.Core/ModeRules.cs so it can be exercised without constructing a
            // service - "every mode that is not deliberately open still denies by default" is the
            // invariant the whole firewall rests on, and it had no test because it was buried in
            // here. All that is left at this level is saying out loud when it fell through.
            if (ModeRules.IsUnrecognised(VisibleState.Mode))
                Utils.Log($"Assembling rules for unrecognised firewall mode {(int)VisibleState.Mode}; blocking by default.", Utils.LOG_ID_SERVICE);

            rules.AddRange(ModeRules.For(VisibleState.Mode, ModeId, out bool needUserRules));

            if (needUserRules)
            {
                // Initialize the collection with our own binary
                var UserExceptions = new List<FirewallExceptionV3>
                {
                    new(
                        new ExecutableSubject(ProcessManager.ExecutablePath),
                        new TcpUdpPolicy()
                        {
                            AllowedRemoteTcpConnectPorts = "443"
                        }
                    )
                };

                // Collect all applications exceptions
                UserExceptions.AddRange(ActiveConfig.Service.ActiveProfile.AppExceptions);

                // Collect all special exceptions
                ActiveConfig.Service.ActiveProfile.SpecialExceptions.Remove("SimpleDeFence");    // TODO: Deprecated: Needed due to old configs. Remove in future version.
                foreach (string appName in ActiveConfig.Service.ActiveProfile.SpecialExceptions)
                    UserExceptions.AddRange(CollectExceptionsForAppByName(appName));

                // Convert exceptions to rules
                foreach (FirewallExceptionV3 ex in UserExceptions)
                {
                    if (ex.Subject is ExecutableSubject exe)
                    {
                        string exePath = exe.ExecutablePath;
                        UserSubjectExes.Add(exePath);
                        if (ex.ChildProcessesInherit)
                        {
                            // We might have multiple rules with the same exePath, so we maintain a list of exceptions
                            if (!ChildInheritance.ContainsKey(exePath))
                                ChildInheritance.Add(exePath, new List<FirewallExceptionV3>());
                            ChildInheritance[exePath].Add(ex);
                        }
                    }

                    GetRulesForException(ex, rules, rawSocketExceptions, (ulong)FilterWeights.UserPermit, (ulong)FilterWeights.UserBlock);
                }

                if (ChildInheritance.Count != 0)
                {
                    timer.NewSubTask("Rule inheritance processing");

                    var sbuilder = new StringBuilder(1024);
                    var procTree = new Dictionary<uint, ProcessSnapshotEntry>();
                    foreach (var p in ProcessManager.CreateToolhelp32SnapshotExtended())
                        procTree.Add(p.ProcessId, p);

                    // This list will hold parents that we already checked for a process.
                    // Used to avoid inf. loop when parent-PID info is unreliable.
                    var pidsChecked = new HashSet<uint>();

                    foreach (var pair in procTree)
                    {
                        pidsChecked.Clear();

                        string procPath = pair.Value.ImagePath;

                        // Skip if we have no path
                        if (string.IsNullOrEmpty(procPath))
                            continue;

                        // Skip if we have a user-defined rule for this path
                        if (UserSubjectExes.Contains(procPath))
                            continue;

                        // Start walking up the process tree
                        for (var parentEntry = procTree[pair.Key]; ;)
                        {
                            long childCreationTime = parentEntry.CreationTime;
                            if (procTree.TryGetValue(parentEntry.ParentProcessId, out var val))
                                parentEntry = val;
                            else
                                // We reached top of process tree (with non-existing parent)
                                break;

                            // Check if what we have is really the parent, or just a reused PID
                            if (parentEntry.CreationTime > childCreationTime)
                                // We reached the top of the process tree (with non-existing parent)
                                break;

                            if (parentEntry.ProcessId == 0)
                                // We reached top of process tree (with idle process)
                                break;

                            if (pidsChecked.Contains(parentEntry.ProcessId))
                                // We've been here before, damn it. Avoid looping eternally...
                                break;

                            pidsChecked.Add(parentEntry.ProcessId);

                            if (string.IsNullOrEmpty(parentEntry.ImagePath))
                                // We cannot get the path, so let's skip this parent
                                continue;

                            if (ChildInheritedSubjectExes.TryGetValue(procPath, out var childVal))
                            { 
                                if (childVal.Contains(parentEntry.ImagePath))
                                    // We have already processed this parent-child combination
                                    break;
                            }

                            if (ChildInheritance.TryGetValue(parentEntry.ImagePath, out List<FirewallExceptionV3> exList))
                            {
                                var subj = new ExecutableSubject(procPath);
                                foreach (var userEx in exList)
                                    GetRulesForException(new FirewallExceptionV3(subj, userEx.Policy), rules, rawSocketExceptions, (ulong)FilterWeights.UserPermit, (ulong)FilterWeights.UserBlock);

                                if (!ChildInheritedSubjectExes.ContainsKey(procPath))
                                    ChildInheritedSubjectExes.Add(procPath, new HashSet<string>());
                                ChildInheritedSubjectExes[procPath].Add(parentEntry.ImagePath);
                                break;
                            }
                        }
                    }
                }   // if (ChildInheritance ...
            }

            // Convert all paths to kernel-format
            foreach (var r in rules)
            {
                if (r.Application is not null)
                    r.Application = PathMapper.Instance.ConvertPathIgnoreErrors(r.Application, PathFormat.NativeNt);
            }

            bool displayBlockActive = ActiveConfig.Service.ActiveProfile.DisplayOffBlock && !DisplayCurrentlyOn;
            if (displayBlockActive)
            {
                // Modify all allow-rules to only allow local subnet
                foreach (var r in rules)
                {
                    if (r.Action == RuleAction.Allow)
                    {
                        r.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                    }
                }
            }

            return rules;
        }

        private void InstallRules(List<RuleDef> rules, List<RuleDef> rawSocketExceptions, bool useTransaction)
        {
            Transaction? trx = useTransaction ? WfpEngine.BeginTransaction() : null;
            try
            {
                // Every failure here used to be swallowed, so a rule WFP had refused looked exactly
                // like one it had taken. What the two counters buy is the distinction that matters,
                // and it is narrower than "block versus permit": a user hard-block or a blocklist
                // entry that fails leaves one application unblocked, while the mode's own deny
                // failing leaves nothing stopping anything. Only the second is worth throwing the
                // whole rule set away for - the first would let one bad exception take the
                // default deny down with it.
                int refusedDefaultDenies = 0;
                int refusedOthers = 0;

                // Add new rules
                foreach (RuleDef r in rules)
                {
                    bool installed;
                    try
                    {
                        installed = ConstructFilter(r);
                    }
                    catch (Exception e)
                    {
                        installed = false;
                        Utils.Log($"Could not build the {r.Action} rule \"{r.Name}\": {e.Message}", Utils.LOG_ID_SERVICE);
                        Utils.LogException(e, Utils.LOG_ID_SERVICE);
                    }

                    if (!installed)
                    {
                        if (r.IsModeDefault && r.Action == RuleAction.Block)
                            ++refusedDefaultDenies;
                        else
                            ++refusedOthers;
                    }
                }

                if (refusedDefaultDenies > 0)
                {
                    // Abandoning the transaction leaves whatever was committed last still in force,
                    // which is the conservative end of the two outcomes available here. Committing
                    // would publish a rule set that is missing the deny everything else is measured
                    // against.
                    Utils.Log($"Not applying the rule set: {refusedDefaultDenies} default-deny rule(s) were refused by WFP.", Utils.LOG_ID_SERVICE);
                    throw new BlockRuleInstallException(refusedDefaultDenies);
                }

                if (refusedOthers > 0)
                {
                    Utils.Log($"{refusedOthers} rule(s) were refused by WFP; the applied rule set is incomplete.", Utils.LOG_ID_SERVICE);
                    VisibleState.Degraded |= ServiceDegradation.RulesIncomplete;
                }

                // Built-in protections
                if (VisibleState.Mode != FirewallMode.Disabled)
                {
                    InstallRawSocketPermits(rawSocketExceptions);
                    InstallWsl2Filters(ActiveConfig.Service.ActiveProfile.HasSpecialException("WSL_2"));
                }

                trx?.Commit();
            }
            finally
            {
                trx?.Dispose();
            }

        }

        private void InstallFirewallRules()
        {
            using var timer = new HierarchicalStopwatch("InstallFirewallRules()");
            LastRuleReloadTime = DateTime.Now;
            FiltersRefusedThisInstall = 0;
            VisibleState.Degraded &= ~ServiceDegradation.RulesIncomplete;
            PathMapper.Instance.RebuildCache();

            var rules = new List<RuleDef>();
            var rawSocketExceptions = new List<RuleDef>();
            lock (InheritanceGuard)
            {
                UserSubjectExes.Clear();
                ChildInheritance.Clear();
                ChildInheritedSubjectExes.Clear();
                rules.AddRange(AssembleActiveRules(rawSocketExceptions));

                try
                {
                    if (ChildInheritance.Count > 0)
                        ProcessStartWatcher.Start();
                    else
                        ProcessStartWatcher.Stop();
                }
                catch
                {
                    // TODO: Add nonce-flag and log only if it has not been logged already
                    // Utils.Log("WMI error. Subprocess monitoring will be disabled.", Utils.LOG_ID_SERVICE);
                }
            }

            timer.NewSubTask("WFP transaction acquire");
            using Transaction trx = WfpEngine.BeginTransaction();
            timer.NewSubTask("WFP preparation");
            // Remove all existing WFP objects
            DeleteWfpObjects(WfpEngine, true);

            // Install provider
            var provider = new FWPM_PROVIDER0();
            provider.displayData.name = "fcoltro";
            provider.displayData.description = "SimpleDeFence Provider";
            provider.serviceName = SimpleDeFenceService.SERVICE_NAME;
            provider.flags = FWPM_PROVIDER_FLAGS.FWPM_PROVIDER_FLAG_PERSISTENT;
            provider.providerKey = TINYWALL_PROVIDER_KEY;
            var providerKey = WfpEngine.RegisterProvider(ref provider);
            Debug.Assert(TINYWALL_PROVIDER_KEY == providerKey);

            // Install sublayers
            var layerKeys = (LayerKeyEnum[])Enum.GetValues(typeof(LayerKeyEnum));
            foreach (var layer in layerKeys)
            {
                var slKey = GetSublayerKey(layer);
                using var wfpSublayer = new Sublayer($"SimpleDeFence Sublayer for {layer}");
                wfpSublayer.Weight = ushort.MaxValue >> 4;
                wfpSublayer.SublayerKey = slKey;
                wfpSublayer.ProviderKey = TINYWALL_PROVIDER_KEY;
                wfpSublayer.Flags = FWPM_SUBLAYER_FLAGS.FWPM_SUBLAYER_FLAG_PERSISTENT;
                WfpEngine.RegisterSublayer(wfpSublayer);
            }

            // Add standard protections
            if (VisibleState.Mode != FirewallMode.Disabled)
            {
                InstallPortScanProtection();
                InstallRawSocketBlocks();
            }

            timer.NewSubTask("Installing rules");
            InstallRules(rules, rawSocketExceptions, false);

            timer.NewSubTask("WFP transaction commit");
            trx.Commit();

            // The built-in protections - raw socket blocks, port scan protection, the WSL2 filters -
            // do not go through the rule loop's block/permit accounting, so their refusals are
            // caught here instead. They do not abandon the install: unlike the default deny, each
            // one narrows an already-blocking firewall rather than being the thing that blocks.
            if (FiltersRefusedThisInstall > 0)
            {
                Utils.Log($"{FiltersRefusedThisInstall} filter(s) were refused by WFP during this install.", Utils.LOG_ID_SERVICE);
                VisibleState.Degraded |= ServiceDegradation.RulesIncomplete;
            }
        }

        private enum LayerKeyEnum
        {
            FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6,
            FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4,
            FWPM_LAYER_INBOUND_ICMP_ERROR_V6,
            FWPM_LAYER_INBOUND_ICMP_ERROR_V4,
            FWPM_LAYER_ALE_AUTH_CONNECT_V6,
            FWPM_LAYER_ALE_AUTH_CONNECT_V4,
            FWPM_LAYER_ALE_AUTH_LISTEN_V6,
            FWPM_LAYER_ALE_AUTH_LISTEN_V4,
            FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6,
            FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,
            FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD,
            FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD,
            FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6,
            FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4,
        }

        private static Guid GetSublayerKey(LayerKeyEnum layer)
        {
            var wellKnownLayerKey = layer switch
            {
                LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6 => WfpSublayerKeys.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6,
                LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4 => WfpSublayerKeys.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4,
                LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6 => WfpSublayerKeys.FWPM_LAYER_INBOUND_ICMP_ERROR_V6,
                LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4 => WfpSublayerKeys.FWPM_LAYER_INBOUND_ICMP_ERROR_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_CONNECT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_LISTEN_V6 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_LISTEN_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_LISTEN_V4 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_LISTEN_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 => WfpSublayerKeys.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,
                LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD => WfpSublayerKeys.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD,
                LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD => WfpSublayerKeys.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD,
                LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6 => WfpSublayerKeys.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4 => WfpSublayerKeys.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4,
                _ => throw new ArgumentException("Invalid or not support layerEnum."),
            };

            // WfpSublayerKeys holds Microsoft's well-known layer GUIDs, reused here as a
            // convenient per-layer unique sublayer key. Forked builds share this source code,
            // so XOR in our own provider key to keep each product's registered sublayers
            // distinct - otherwise two installs of this codebase under different product
            // identities (e.g. this fork alongside upstream TinyWall) would fight over the
            // same sublayer keys if both ran on the same machine.
            byte[] a = wellKnownLayerKey.ToByteArray();
            byte[] b = TINYWALL_PROVIDER_KEY.ToByteArray();
            for (int i = 0; i < a.Length; i++)
                a[i] ^= b[i];
            return new Guid(a);
        }

        private static Guid GetLayerKey(LayerKeyEnum layer)
        {
            return layer switch
            {
                LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6 => LayerKeys.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6,
                LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4 => LayerKeys.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4,
                LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6 => LayerKeys.FWPM_LAYER_INBOUND_ICMP_ERROR_V6,
                LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4 => LayerKeys.FWPM_LAYER_INBOUND_ICMP_ERROR_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6 => LayerKeys.FWPM_LAYER_ALE_AUTH_CONNECT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4 => LayerKeys.FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_LISTEN_V6 => LayerKeys.FWPM_LAYER_ALE_AUTH_LISTEN_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_LISTEN_V4 => LayerKeys.FWPM_LAYER_ALE_AUTH_LISTEN_V4,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 => LayerKeys.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 => LayerKeys.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,
                LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD => LayerKeys.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD,
                LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD => LayerKeys.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD,
                LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6 => LayerKeys.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6,
                LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4 => LayerKeys.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4,
                _ => throw new ArgumentException("Invalid or not support layerEnum."),
            };
        }

        /// <summary>
        /// Registers one filter twice - once persistent, once boot-time - and says whether both
        /// took. This used to swallow everything, which meant a filter WFP had refused was
        /// indistinguishable from one it had accepted; for a block rule that is the difference
        /// between a firewall and a decoration.
        ///
        /// The two registrations are reported separately because a half-installed filter is its own
        /// state: persistent-only is enforced while the machine runs and absent between boot and
        /// service start, which is precisely the window a boot-time filter exists to cover.
        /// </summary>
        private bool InstallWfpFilter(Filter f)
        {
            bool ok = true;

            try
            {
                f.FilterKey = Guid.NewGuid();
                f.Flags = FilterFlags.FWPM_FILTER_FLAG_PERSISTENT;
                WfpEngine.RegisterFilter(f);
            }
            catch (Exception e)
            {
                ok = false;
                ++FiltersRefusedThisInstall;
                Utils.Log($"WFP refused the persistent filter \"{f.DisplayName}\": {e.Message}", Utils.LOG_ID_SERVICE);
            }

            try
            {
                f.FilterKey = Guid.NewGuid();
                f.Flags = FilterFlags.FWPM_FILTER_FLAG_BOOTTIME;
                WfpEngine.RegisterFilter(f);
            }
            catch (Exception e)
            {
                ok = false;
                ++FiltersRefusedThisInstall;
                Utils.Log($"WFP refused the boot-time filter \"{f.DisplayName}\": {e.Message}", Utils.LOG_ID_SERVICE);
            }

            return ok;
        }

        /// <summary>Filters WFP turned down since the current install began. Reset by
        /// InstallFirewallRules(), read by it again once everything has been offered.</summary>
        private int FiltersRefusedThisInstall;

        private bool ConstructFilter(RuleDef r, LayerKeyEnum layer)
        {
            // Local helper methods

            bool addCommonIpFilterCondition(IpFilterCondition cond, FilterConditionList coll)
            {
                if (cond.IsIPv6 == LayerIsV6Stack(layer))
                {
                    coll.Add(cond);
                    return true;
                }
                return false;
            }
            bool addIpFilterCondition(IpAddrMask peerAddr, RemoteOrLocal peerType, FilterConditionList coll)
            {
                if (peerAddr.IsIPv6 == LayerIsV6Stack(layer))
                {
                    coll.Add(new IpFilterCondition(peerAddr.Address, (byte)peerAddr.PrefixLen, peerType));
                    return true;
                }
                return false;
            }
            (ushort, ushort) parseUInt16Range(ReadOnlySpan<char> str)
            {
                if (-1 != str.IndexOf('-'))
                {
                    ReadOnlySpan<char> min, max;
                    using (var enumerator = ReadOnlySpanExtension.Split(str, '-'))
                    {
                        enumerator.MoveNext(); min = enumerator.Current;
                        enumerator.MoveNext(); max = enumerator.Current;
                    }
                    return (min.DecimalToUInt16(), max.DecimalToUInt16());
                }
                else
                {
                    var port = str.DecimalToUInt16();
                    return (port, port);
                }
            }

            // ---------------------------------------

            using var conditions = new FilterConditionList();

            // Application identity
            if (!string.IsNullOrEmpty(r.AppContainerSid))
            {
                System.Diagnostics.Debug.Assert(!r.AppContainerSid.Equals("*"));

                // Skip filter if OS is not supported
                if (!SimpleDeFence.Windows.VersionInfo.Win81OrNewer)
                    return true;   // deliberately not applicable to this layer, not a failure

                if (!LayerIsIcmpError(layer))
                    conditions.Add(new PackageIdFilterCondition(r.AppContainerSid));
                else
                    return true;   // deliberately not applicable to this layer, not a failure
            }
            else
            {
                if (!string.IsNullOrEmpty(r.ServiceName))
                {
                    System.Diagnostics.Debug.Assert(!r.ServiceName.Equals("*"));
                    if (!LayerIsIcmpError(layer))
                        conditions.Add(new ServiceNameFilterCondition(r.ServiceName));
                    else
                        return true;   // deliberately not applicable to this layer, not a failure
                }

                if (!string.IsNullOrEmpty(r.Application))
                {
                    System.Diagnostics.Debug.Assert(!r.Application.Equals("*"));

                    if (!LayerIsIcmpError(layer))
                        conditions.Add(new AppIdFilterCondition(r.Application, false, true));
                    else
                        return true;   // deliberately not applicable to this layer, not a failure
                }
            }

            // IP address
            if (!string.IsNullOrEmpty(r.RemoteAddresses))
            {
                System.Diagnostics.Debug.Assert(!r.RemoteAddresses.Equals("*"));

                bool validAddressFound = false;
                foreach (var ipStr in r.RemoteAddresses.AsSpan().Split(',', SpanSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        if (ipStr.Equals(RuleDef.LOCALSUBNET_ID, StringComparison.Ordinal))
                        {
                            foreach (var filter in LocalSubnetFilterConditions)
                                validAddressFound |= addCommonIpFilterCondition((IpFilterCondition)filter, conditions);
                        }
                        else if (ipStr.Equals("DefaultGateway", StringComparison.Ordinal))
                        {
                            foreach (var filter in GatewayFilterConditions)
                                validAddressFound |= addCommonIpFilterCondition((IpFilterCondition)filter, conditions);
                        }
                        else if (ipStr.Equals("DNS", StringComparison.Ordinal))
                        {
                            foreach (var filter in DnsFilterConditions)
                                validAddressFound |= addCommonIpFilterCondition((IpFilterCondition)filter, conditions);
                        }
                        else
                        {
                            validAddressFound |= addIpFilterCondition(IpAddrMask.Parse(ipStr), RemoteOrLocal.Remote, conditions);
                        }
                    }
                    catch {
                        // Ignore failed IP condition and process next one
                    }
                }

                if (!validAddressFound)
                {
                    // Break. We don't want to add this filter to this layer.
                    return true;   // deliberately not applicable to this layer, not a failure
                }
            }

            // We never want to affect loopback traffic
            conditions.Add(new FlagsFilterCondition(ConditionFlags.FWP_CONDITION_FLAG_IS_LOOPBACK, FieldMatchType.FWP_MATCH_FLAGS_NONE_SET));

            // Protocol
            if (r.Protocol != Protocol.Any)
            {
                if (LayerIsAleAuthConnect(layer) || LayerIsAleAuthRecvAccept(layer))
                {
                    if (r.Protocol == Protocol.TcpUdp)
                    {
                        conditions.Add(new ProtocolFilterCondition((byte)Protocol.TCP));
                        conditions.Add(new ProtocolFilterCondition((byte)Protocol.UDP));
                    }
                    else
                        conditions.Add(new ProtocolFilterCondition((byte)r.Protocol));
                }
            }

            // Ports
            if (!string.IsNullOrEmpty(r.LocalPorts))
            {
                System.Diagnostics.Debug.Assert(!r.LocalPorts.Equals("*"));
                foreach (var p in r.LocalPorts.AsSpan().Split(',', SpanSplitOptions.RemoveEmptyEntries))
                {
                    (var minPort, var maxPort) = parseUInt16Range(p);
                    conditions.Add(new PortFilterCondition(minPort, maxPort, RemoteOrLocal.Local));
                }
            }
            if (!string.IsNullOrEmpty(r.RemotePorts))
            {
                System.Diagnostics.Debug.Assert(!r.RemotePorts.Equals("*"));
                foreach (var p in r.RemotePorts.AsSpan().Split(',', SpanSplitOptions.RemoveEmptyEntries))
                {
                    (var minPort, var maxPort) = parseUInt16Range(p);
                    conditions.Add(new PortFilterCondition(minPort, maxPort, RemoteOrLocal.Remote));
                }
            }

            // ICMP
            if (!string.IsNullOrEmpty(r.IcmpTypesAndCodes))
            {
                System.Diagnostics.Debug.Assert(!r.IcmpTypesAndCodes.Equals("*"));
                foreach (var e in r.IcmpTypesAndCodes.AsSpan().Split(',', SpanSplitOptions.RemoveEmptyEntries))
                {
                    using var tc = ReadOnlySpanExtension.Split(e, ':');
                    tc.MoveNext(); var icmpType = tc.Current;

                    if (LayerIsIcmpError(layer))
                    {
                        // ICMP Type
                        if ((icmpType.Length != 0) && icmpType.TryDecimalToUInt16(out ushort icmpTypeVal))
                            conditions.Add(new IcmpErrorTypeFilterCondition(icmpTypeVal));

                        // ICMP Code
                        if (tc.MoveNext())
                        {
                            var icmpCode = tc.Current;
                            if ((icmpCode.Length != 0) && !icmpCode.Equals("*", StringComparison.Ordinal) && icmpCode.TryDecimalToUInt16(out ushort icmpCodeVal))
                                conditions.Add(new IcmpErrorCodeFilterCondition(icmpCodeVal));
                        }
                    }
                    else
                    {
                        // ICMP Type - note different condition key
                        if ((icmpType.Length != 0) && icmpType.TryDecimalToUInt16(out ushort icmpTypeVal))
                            conditions.Add(new IcmpTypeFilterCondition(icmpTypeVal));

                        // Matching on ICMP Code not possible
                    }
                }
            }

            // Create and install filter
            using var f = new Filter(
                r.ExceptionId.ToString(),
                r.Name,
                TINYWALL_PROVIDER_KEY,
                (r.Action == RuleAction.Allow) ? FilterActions.FWP_ACTION_PERMIT : FilterActions.FWP_ACTION_BLOCK,
                r.Weight,
                conditions
            );
            f.LayerKey = GetLayerKey(layer);
            f.SublayerKey = GetSublayerKey(layer);

            return InstallWfpFilter(f);
        }

        private void InstallRawSocketBlocks()
        {
            InstallRawSocketBlocks(LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4);
            InstallRawSocketBlocks(LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6);
        }

        private void InstallRawSocketBlocks(LayerKeyEnum layer)
        {
            using var f = new Filter(
                "Raw socket block",
                string.Empty,
                TINYWALL_PROVIDER_KEY,
                FilterActions.FWP_ACTION_BLOCK,
                (ulong)FilterWeights.RawSocketBlock
            );
            f.LayerKey = GetLayerKey(layer);
            f.SublayerKey = GetSublayerKey(layer);
            f.Conditions.Add(new FlagsFilterCondition(ConditionFlags.FWP_CONDITION_FLAG_IS_RAW_ENDPOINT, FieldMatchType.FWP_MATCH_FLAGS_ANY_SET));

            InstallWfpFilter(f);
        }

        private void InstallWsl2Filters(bool permit)
        {
            const string ifAlias = "vEthernet (WSL)";
            try
            {
                if (LocalInterfaceCondition.InterfaceAliasExists(ifAlias))
                {
                    InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4);
                    InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6);
                    InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4);
                    InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6);
                    InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4);
                    InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6);
                    InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4);
                    InstallWsl2Filters(permit, ifAlias, LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6);
                }
            }
            catch { }
        }

        private void InstallWsl2Filters(bool permit, string ifAlias, LayerKeyEnum layer)
        {
            FilterActions action = permit ? FilterActions.FWP_ACTION_PERMIT : FilterActions.FWP_ACTION_BLOCK;
            ulong weight = (ulong)(permit ? FilterWeights.UserPermit : FilterWeights.UserBlock);

            using var f = new Filter(
                "Allow WSL2",
                string.Empty,
                TINYWALL_PROVIDER_KEY,
                action,
                weight
            );
            f.LayerKey = GetLayerKey(layer);
            f.SublayerKey = GetSublayerKey(layer);
            f.Conditions.Add(new LocalInterfaceCondition(ifAlias));

            InstallWfpFilter(f);
        }

        private void InstallRawSocketPermits(List<RuleDef> rawSocketExceptions)
        {
            InstallRawSocketPermits(rawSocketExceptions, LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4);
            InstallRawSocketPermits(rawSocketExceptions, LayerKeyEnum.FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6);
        }

        private void InstallRawSocketPermits(List<RuleDef> rawSocketExceptions, LayerKeyEnum layer)
        {
            foreach (var subj in rawSocketExceptions)
            {
                try
                {
                    using var conditions = new FilterConditionList();
                    if (!string.IsNullOrEmpty(subj.Application))
                        conditions.Add(new AppIdFilterCondition(subj.Application, false, true));
                    if (!string.IsNullOrEmpty(subj.ServiceName))
                        conditions.Add(new ServiceNameFilterCondition(subj.ServiceName));
                    if (conditions.Count == 0)
                        continue;

                    using var f = new Filter(
                        "Raw socket permit",
                        string.Empty,
                        TINYWALL_PROVIDER_KEY,
                        FilterActions.FWP_ACTION_PERMIT,
                        (ulong)FilterWeights.RawSocketPermit,
                        conditions
                    );
                    f.LayerKey = GetLayerKey(layer);
                    f.SublayerKey = GetSublayerKey(layer);

                    InstallWfpFilter(f);
                }
                catch { }
            }
        }

        private void InstallPortScanProtection()
        {
            InstallPortScanProtection(LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD, BuiltinCallouts.FWPM_CALLOUT_WFP_TRANSPORT_LAYER_V4_SILENT_DROP);
            InstallPortScanProtection(LayerKeyEnum.FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD, BuiltinCallouts.FWPM_CALLOUT_WFP_TRANSPORT_LAYER_V6_SILENT_DROP);
        }

        private void InstallPortScanProtection(LayerKeyEnum layer, Guid callout)
        {
            using var f = new Filter(
                "Port Scanning Protection",
                string.Empty,
                TINYWALL_PROVIDER_KEY,
                FilterActions.FWP_ACTION_CALLOUT_TERMINATING,
                (ulong)FilterWeights.Blocklist
            );
            f.LayerKey = GetLayerKey(layer);
            f.SublayerKey = GetSublayerKey(layer);
            f.CalloutKey = callout;

            // Don't affect loopback traffic
            f.Conditions.Add(new FlagsFilterCondition(ConditionFlags.FWP_CONDITION_FLAG_IS_LOOPBACK | ConditionFlags.FWP_CONDITION_FLAG_IS_IPSEC_SECURED, FieldMatchType.FWP_MATCH_FLAGS_NONE_SET));

            InstallWfpFilter(f);
        }

        private static bool LayerIsAleAuthConnect(LayerKeyEnum layer)
        {
            return
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4) ||
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6);
        }

        private static bool LayerIsAleAuthRecvAccept(LayerKeyEnum layer)
        {
            return
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4);
        }

        private static bool LayerIsIcmpError(LayerKeyEnum layer)
        {
            return
                (layer == LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4) ||
                (layer == LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4);
        }

        private static bool LayerIsV6Stack(LayerKeyEnum layer)
        {
            return
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_ALE_AUTH_LISTEN_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6) ||
                (layer == LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6);
        }

        /// <summary>Installs one rule across every layer it applies to. False if any of them was
        /// refused - a rule that reached only some of its layers is not the rule that was asked
        /// for, and for a block rule that is a hole rather than a detail.</summary>
        private bool ConstructFilter(RuleDef r)
        {
            // Also, relevant info:
            // https://networkengineering.stackexchange.com/questions/58903/how-to-handle-icmp-in-ipv6-or-icmpv6-in-ipv4

            bool ok = true;

            if ((r.Direction & RuleDirection.Out) != 0)
            {
                ok &= ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V6);
                ok &= ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_CONNECT_V4);

                if ((r.Protocol == Protocol.Any) || (r.Protocol == Protocol.ICMPv6))
                    ok &= ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6);
                if ((r.Protocol == Protocol.Any) || (r.Protocol == Protocol.ICMPv4))
                    ok &= ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4);
            }
            if ((r.Direction & RuleDirection.In) != 0)
            {
                ok &= ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6);
                ok &= ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4);

                if ((r.Protocol == Protocol.Any) || (r.Protocol == Protocol.ICMPv6))
                    ok &= ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V6);
                if ((r.Protocol == Protocol.Any) || (r.Protocol == Protocol.ICMPv4))
                    ok &= ConstructFilter(r, LayerKeyEnum.FWPM_LAYER_INBOUND_ICMP_ERROR_V4);
            }

            return ok;
        }

        private static List<FirewallExceptionV3> CollectExceptionsForAppByName(string name)
        {
            var exceptions = new List<FirewallExceptionV3>();

            try
            {
                // Retrieve database entry for appName
                DatabaseClasses.Application? app = GlobalInstances.AppDatabase.GetApplicationByName(name);
                if (app is null)
                    return exceptions;

                // Create rules
                foreach (DatabaseClasses.SubjectIdentity id in app.Components)
                {
                    try
                    {
                        List<ExceptionSubject> foundSubjects = id.SearchForFile();
                        foreach (var subject in foundSubjects)
                        {
                            exceptions.Add(id.InstantiateException(subject));
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return exceptions;
        }

        private static void GetRulesForException(FirewallExceptionV3 ex, List<RuleDef> results, List<RuleDef> rawSocketExceptions, ulong permitWeight, ulong blockWeight)
        {
            if (ex.Id == Guid.Empty)
            {
                // Do not let the service crash if a rule cannot be constructed 
#if DEBUG
                throw new InvalidOperationException("Firewall exception specification must have an ID.");
#else
                ex.RegenerateId();
                GlobalInstances.ServerChangeset = Guid.NewGuid();
#endif
            }

            switch (ex.Policy.PolicyType)
            {
                case PolicyType.HardBlock:
                    {
                        var def = new RuleDef(ex.Id, "Block", ex.Subject, RuleAction.Block, RuleDirection.InOut, Protocol.Any, blockWeight);
                        results.Add(def);
                        break;
                    }
                case PolicyType.Unrestricted:
                    {
                        var pol = (UnrestrictedPolicy)ex.Policy;

                        var def = new RuleDef(ex.Id, "Full access", ex.Subject, RuleAction.Allow, RuleDirection.InOut, Protocol.Any, permitWeight);
                        if (pol.LocalNetworkOnly)
                            def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                        results.Add(def);

                        // Make exception for promiscuous mode
                        rawSocketExceptions?.Add(def);

                        break;
                    }
                case PolicyType.TcpUdpOnly:
                    {
                        var pol = (TcpUdpPolicy)ex.Policy;

                        // Incoming
                        if (!string.IsNullOrEmpty(pol.AllowedLocalTcpListenerPorts) && (pol.AllowedLocalTcpListenerPorts == pol.AllowedLocalUdpListenerPorts))
                        {
                            var def = new RuleDef(ex.Id, "TCP/UDP Listen Ports", ex.Subject, RuleAction.Allow, RuleDirection.In, Protocol.TcpUdp, permitWeight);
                            if (!string.Equals(pol.AllowedLocalTcpListenerPorts, "*"))
                                def.LocalPorts = pol.AllowedLocalTcpListenerPorts;
                            if (pol.LocalNetworkOnly)
                                def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                            results.Add(def);
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(pol.AllowedLocalTcpListenerPorts))
                            {
                                var def = new RuleDef(ex.Id, "TCP Listen Ports", ex.Subject, RuleAction.Allow, RuleDirection.In, Protocol.TCP, permitWeight);
                                if (!string.Equals(pol.AllowedLocalTcpListenerPorts, "*"))
                                    def.LocalPorts = pol.AllowedLocalTcpListenerPorts;
                                if (pol.LocalNetworkOnly)
                                    def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                                results.Add(def);
                            }
                            if (!string.IsNullOrEmpty(pol.AllowedLocalUdpListenerPorts))
                            {
                                var def = new RuleDef(ex.Id, "UDP Listen Ports", ex.Subject, RuleAction.Allow, RuleDirection.In, Protocol.UDP, permitWeight);
                                if (!string.Equals(pol.AllowedLocalUdpListenerPorts, "*"))
                                    def.LocalPorts = pol.AllowedLocalUdpListenerPorts;
                                if (pol.LocalNetworkOnly)
                                    def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                                results.Add(def);
                            }
                        }

                        // Outgoing
                        if (!string.IsNullOrEmpty(pol.AllowedRemoteTcpConnectPorts) && (pol.AllowedRemoteTcpConnectPorts == pol.AllowedRemoteUdpConnectPorts))
                        {
                            var def = new RuleDef(ex.Id, "TCP/UDP Outbound Ports", ex.Subject, RuleAction.Allow, RuleDirection.Out, Protocol.TcpUdp, permitWeight);
                            if (!string.Equals(pol.AllowedRemoteTcpConnectPorts, "*"))
                                def.RemotePorts = pol.AllowedRemoteTcpConnectPorts;
                            if (pol.LocalNetworkOnly)
                                def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                            results.Add(def);
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(pol.AllowedRemoteTcpConnectPorts))
                            {
                                var def = new RuleDef(ex.Id, "TCP Outbound Ports", ex.Subject, RuleAction.Allow, RuleDirection.Out, Protocol.TCP, permitWeight);
                                if (!string.Equals(pol.AllowedRemoteTcpConnectPorts, "*"))
                                    def.RemotePorts = pol.AllowedRemoteTcpConnectPorts;
                                if (pol.LocalNetworkOnly)
                                    def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                                results.Add(def);
                            }
                            if (!string.IsNullOrEmpty(pol.AllowedRemoteUdpConnectPorts))
                            {
                                var def = new RuleDef(ex.Id, "UDP Outbound Ports", ex.Subject, RuleAction.Allow, RuleDirection.Out, Protocol.UDP, permitWeight);
                                if (!string.Equals(pol.AllowedRemoteUdpConnectPorts, "*"))
                                    def.RemotePorts = pol.AllowedRemoteUdpConnectPorts;
                                if (pol.LocalNetworkOnly)
                                    def.RemoteAddresses = RuleDef.LOCALSUBNET_ID;
                                results.Add(def);
                            }
                        }
                        break;
                    }
                case PolicyType.RuleList:
                    {
                        // The RuleDefs returned can get modified by the caller.
                        // To avoid changing the original templates we return copies of rules.

                        var pol = (RuleListPolicy)ex.Policy;
                        foreach (var rule in pol.Rules)
                        {
                            var ruleCopy = rule.ShallowCopy();
                            ruleCopy.SetSubject(ex.Subject);
                            ruleCopy.ExceptionId = ex.Id;
                            ruleCopy.Weight = (rule.Action == RuleAction.Allow) ? permitWeight : blockWeight;
                            results.Add(ruleCopy);
                        }
                        break;
                    }
            }
        }

        private static string ConfigSavePath
        {
            get
            {
                return Path.Combine(Utils.AppDataPath, "config");
            }
        }

        private static ServerConfiguration LoadServerConfig(out ConfigLoadOutcome outcome)
        {
            outcome = ConfigLoadOutcome.Missing;
            try
            {
                var loaded = ServerConfiguration.Load(ConfigSavePath, out outcome);

                // Load() does not throw when there is no config file. It goes through
                // SerializationHelper.DeserializeFromEncryptedFile, which returns the default
                // instance it was handed the moment the file cannot be read - and that instance
                // is a ServerConfiguration with an empty ActiveProfileName, which every
                // ActiveProfile access rejects with InvalidOperationException.
                //
                // So a config naming no profile has to be treated as no config at all. Without
                // this check a first install sailed straight past the fallback below, leaving the
                // service running but throwing on everything it was asked to do - the GUI only
                // ever reported "Not connected", because no command it sent could succeed.
                if (!string.IsNullOrEmpty(loaded.ActiveProfileName))
                    return loaded;
            }
            catch (Exception e)
            {
                // Reading can throw rather than fall back to defaults - the pre-3.0 XML reader
                // does exactly that for a file it cannot make sense of. A file that is present and
                // unusable is still a configuration this service is failing to honour, so it is
                // reported as one instead of being swallowed here.
                outcome = File.Exists(ConfigSavePath) ? ConfigLoadOutcome.Unreadable : ConfigLoadOutcome.Missing;
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
            }

            // No config on disk, or nothing usable in it: prepare a default instead
            var ret = new ServerConfiguration { ActiveProfileName = Resources.Messages.Default };

            // Allow recommended exceptions
            DatabaseClasses.AppDatabase db = GlobalInstances.AppDatabase;
            foreach (DatabaseClasses.Application app in db.KnownApplications)
            {
                if (app.HasFlag("TWUI:Special") && app.HasFlag("TWUI:Recommended"))
                {
                    ret.ActiveProfile.SpecialExceptions.Add(app.Name);
                }
            }

            return ret;
        }

        /// <summary>
        /// Turns the reason a config load ended up on defaults into something outside this method
        /// can see. Missing is a first run and says nothing; the other three all mean a file is
        /// sitting in AppData that the service declined to trust, and the firewall it then built
        /// is the default one rather than the user's. That case used to be indistinguishable from
        /// a clean first start - no log line, no flag, a UI reporting the mode as if the
        /// configuration behind it were the configured one.
        /// </summary>
        private void ReportConfigOutcome(ConfigLoadOutcome outcome)
        {
            switch (outcome)
            {
                case ConfigLoadOutcome.Unreadable:
                    Utils.Log("The configuration file is present but could not be read. Running on default settings; the file has been left alone.", Utils.LOG_ID_SERVICE);
                    break;
                case ConfigLoadOutcome.Unauthenticated:
                    Utils.Log("The configuration file failed its authentication check - it was altered, truncated, or written under a different key. Running on default settings; the file has been left alone.", Utils.LOG_ID_SERVICE);
                    break;
                case ConfigLoadOutcome.DowngradeRefused:
                    Utils.Log("The configuration file is in the superseded format, which this installation has already migrated away from, so it was refused as a downgrade. Running on default settings; the file has been left alone.", Utils.LOG_ID_SERVICE);
                    break;
                default:
                    VisibleState.Degraded &= ~ServiceDegradation.ConfigurationUnreadable;
                    return;
            }

            VisibleState.Degraded |= ServiceDegradation.ConfigurationUnreadable;
        }

        // This method completely reinitializes the firewall.
        /// <summary>
        /// Runs InitFirewall and records whether it got through. Failure used to fall into the
        /// worker loop's catch, which logged it and carried on: the service then ran, answered
        /// GET_SETTINGS with its StartupMode, and enforced whatever WFP was still holding from the
        /// previous session - filters are persistent, so they survive a restart - while nothing
        /// anywhere could express "running, but not on your configuration".
        /// </summary>
        private bool TryInitFirewall()
        {
            try
            {
                InitFirewall();
                VisibleState.Degraded &= ~ServiceDegradation.InitializationFailed;
                return true;
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                VisibleState.Degraded |= ServiceDegradation.InitializationFailed;

                // Move the changeset so connected clients actually receive the degraded state:
                // GET_SETTINGS only answers with VisibleState when the changeset differs.
                GlobalInstances.ServerChangeset = Guid.NewGuid();
                return false;
            }
        }

        private void InitFirewall()
        {
            using var timer = new HierarchicalStopwatch("InitFirewall()");

            if (LoadDatabase())
                VisibleState.Degraded &= ~ServiceDegradation.AppDatabaseUnavailable;
            else
                VisibleState.Degraded |= ServiceDegradation.AppDatabaseUnavailable;

            ActiveConfig.Service = LoadServerConfig(out var configOutcome);
            ReportConfigOutcome(configOutcome);
            VisibleState.Mode = ActiveConfig.Service.StartupMode;
            GlobalInstances.ServerChangeset = Guid.NewGuid();

            if (CommitLearnedRules() || PruneExpiredRules())
                ActiveConfig.Service.Save(ConfigSavePath);

            ReapplySettings();
            InstallFirewallRules();
        }


        // This method reapplies all firewall settings.
        private void ReapplySettings()
        {
            using var timer = new HierarchicalStopwatch("ReapplySettings()");
            HostsFileManager.EnableProtection = ActiveConfig.Service.LockHostsFile;

            bool wantHostsBlocklist = ActiveConfig.Service.Blocklists.EnableBlocklists
                && ActiveConfig.Service.Blocklists.EnableHostsBlocklist;

            if (wantHostsBlocklist)
            {
                // The result was discarded here, and EnableHostsFile() returned false either way,
                // so a blocklist that never got installed looked exactly like one that did.
                if (HostsFileManager.EnableHostsFile())
                {
                    VisibleState.Degraded &= ~ServiceDegradation.HostsBlocklistUnavailable;
                }
                else
                {
                    Utils.Log("The hosts blocklist is enabled in the configuration but is not installed.", Utils.LOG_ID_SERVICE);
                    VisibleState.Degraded |= ServiceDegradation.HostsBlocklistUnavailable;
                }
            }
            else
            {
                HostsFileManager.DisableHostsFile();
                VisibleState.Degraded &= ~ServiceDegradation.HostsBlocklistUnavailable;
            }
        }

        /// <summary>Loads the application database, falling back to an empty one. False when the
        /// fallback was taken - every blocklist and every named application profile resolves
        /// through this, so an empty database quietly turns "blocklists on" in the configuration
        /// into no rules whatsoever, which the settings page went on describing as enabled.</summary>
        private static bool LoadDatabase()
        {
            using var timer = new HierarchicalStopwatch("LoadDatabase()");

            try
            {
                GlobalInstances.AppDatabase = DatabaseClasses.AppDatabase.Load();
                return true;
            }
            catch (Exception e)
            {
                Utils.Log("The application database could not be loaded. Blocklists and application "
                    + "profiles will produce no rules until it is available again.", Utils.LOG_ID_SERVICE);
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
                GlobalInstances.AppDatabase = new DatabaseClasses.AppDatabase();
                return false;
            }
        }

        private DateTime? LastUpdateCheck_ = null;
        private const string LastUpdateCheck_FILENAME = "updatecheck";
        private DateTime LastUpdateCheck
        {
            get
            {
                if (!LastUpdateCheck_.HasValue)
                {
                    try
                    {
                        string filePath = Path.Combine(Utils.AppDataPath, LastUpdateCheck_FILENAME);
                        if (File.Exists(filePath))
                        {
                            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using var sr = new StreamReader(fs, Encoding.UTF8);
                            LastUpdateCheck_ = DateTime.Parse(sr.ReadLine());
                        }
                    }
                    catch { }
                }

                if (!LastUpdateCheck_.HasValue)
                    LastUpdateCheck_ = DateTime.MinValue;
                if (LastUpdateCheck_.Value > DateTime.Now)
                    LastUpdateCheck_ = DateTime.MinValue;

                return LastUpdateCheck_.Value;
            }

            set
            {
                LastUpdateCheck_ = value;

                try
                {
                    string filePath = Path.Combine(Utils.AppDataPath, LastUpdateCheck_FILENAME);
                    using var afu = new AtomicFileUpdater(filePath);
                    using (var fs = new FileStream(afu.TemporaryFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        using var sw = new StreamWriter(fs, Encoding.UTF8);
                        sw.WriteLine(value.ToString("O"));
                    }
                    afu.Commit();
                }
                catch { }
            }
        }

        private void UpdaterMethod()
        {
            // This is an automatic update check in the background.
            // If we fail (for whatever reason, no internet, server down etc.), do it silently.
            UpdateDescriptor? update = null;
            try { update = UpdateChecker.GetDescriptor(); }
            catch { return; }
            if (update is null)
                return;

            VisibleState.Update = update;
            GlobalInstances.ServerChangeset = Guid.NewGuid();

            try
            {
                var hostsUpdate = update.GetModule(UpdateDescriptor.MODULE_NAME_HOSTS);
                if (hostsUpdate is not null)
                {
                    if (!string.Equals(hostsUpdate.DownloadHash, HostsFileManager.GetHostsHash(), StringComparison.OrdinalIgnoreCase))
                        GetCompressedUpdate(hostsUpdate, HostsUpdateInstall);
                }

                var databaseUpdate = update.GetModule(UpdateDescriptor.MODULE_NAME_DATABASE);
                if (databaseUpdate is not null)
                {
                    if (!string.Equals(databaseUpdate.DownloadHash, Hasher.HashFile(DatabaseClasses.AppDatabase.DBPath), StringComparison.OrdinalIgnoreCase))
                        GetCompressedUpdate(databaseUpdate, DatabaseUpdateInstall);
                }
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_SERVICE);
            }
        }

        private static void GetCompressedUpdate(UpdateModule module, WaitCallback installMethod)
        {
            string tmpCompressedPath = Path.GetTempFileName();
            string tmpFile = Path.GetTempFileName();
            try
            {
                HttpFileDownloader.DownloadFile(module.UpdateURL, tmpCompressedPath);
                Utils.DecompressDeflate(tmpCompressedPath, tmpFile);

                if (Hasher.HashFile(tmpFile).Equals(module.DownloadHash, StringComparison.OrdinalIgnoreCase))
                {
#if !DEBUG  // don't install anything during debug
                    installMethod(tmpFile);
#endif
                }
            }
            catch { }
            finally
            {
                try
                {
                    File.Delete(tmpCompressedPath);
                }
                catch { }

                try
                {
                    File.Delete(tmpFile);
                }
                catch { }
            }
        }

        private void HostsUpdateInstall(object file)
        {
            string tmpHostsPath = (string)file;
            HostsFileManager.UpdateHostsFile(tmpHostsPath);

            if (ActiveConfig.Service.Blocklists.EnableBlocklists
                && ActiveConfig.Service.Blocklists.EnableHostsBlocklist)
            {
                HostsFileManager.EnableHostsFile();
            }
        }
        private void DatabaseUpdateInstall(object file)
        {
            string tmpFilePath = (string)file;

            FileLocker.Unlock(DatabaseClasses.AppDatabase.DBPath);
            using (var afu = new AtomicFileUpdater(DatabaseClasses.AppDatabase.DBPath))
            {
                File.Copy(tmpFilePath, afu.TemporaryFilePath, true);
                afu.Commit();
            }
            FileLocker.Lock(DatabaseClasses.AppDatabase.DBPath, FileAccess.Read, FileShare.Read);
            NotifyController(MessageType.DATABASE_UPDATED);
            Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.REINIT)));
        }

        private void NotifyController(MessageType msg)
        {
            VisibleState.ClientNotifs.Add(msg);
            GlobalInstances.ServerChangeset = Guid.NewGuid();
        }

        internal void TimerCallback(Object state)
        {
            Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.MINUTE_TIMER)));
        }

        private List<FirewallLogEntry> GetFwLog()
        {
            var entries = new List<FirewallLogEntry>();
            lock (FirewallLogEntries)
            {
                entries.AddRange(FirewallLogEntries);
            }
            return entries;
        }

        private bool CommitLearnedRules()
        {
            bool config_changed = false;

            lock (LearningNewExceptions)
            {
                if (LearningNewExceptions.Count > 0)
                {
                    GlobalInstances.ServerChangeset = Guid.NewGuid();
                    ActiveConfig.Service.ActiveProfile.AddExceptions(LearningNewExceptions);
                    LearningNewExceptions.Clear();
                    config_changed = true;
                }
            }

            return config_changed;
        }

        private static bool HasSystemRebooted()
        {
            try
            {
                const string ATOM_NAME = "SimpleDeFence-NoMachineReboot";
                bool rebooted = !GlobalAtomTable.Exists(ATOM_NAME);
                if (rebooted)
                    GlobalAtomTable.Add(ATOM_NAME);
                return rebooted;
            }
            catch
            {
                return true;
            }
        }

        private static bool PruneExpiredRules()
        {
            bool system_rebooted = HasSystemRebooted();
            bool config_changed = false;

            List<FirewallExceptionV3> exs = ActiveConfig.Service.ActiveProfile.AppExceptions;
            for (int i = exs.Count - 1; i >= 0; --i)
            {
                // Timer values above zero are the number of minutes to stay active

                if (system_rebooted && (exs[i].Timer == AppExceptionTimer.Until_Reboot))
                {
                    exs.RemoveAt(i);
                    config_changed = true;
                }
                else if (((int)exs[i].Timer > 0) && (exs[i].CreationDate.AddMinutes((double)exs[i].Timer) <= DateTime.Now))
                {
                    exs.RemoveAt(i);
                    config_changed = true;
                }
            }

            if (config_changed)
            {
                GlobalInstances.ServerChangeset = Guid.NewGuid();
                ActiveConfig.Service.ActiveProfile.AppExceptions = exs;
            }

            return config_changed;
        }

        private TwMessage ProcessCmd(TwMessage req)
        {
            switch (req.Type)
            {
                case MessageType.READ_FW_LOG:
                    {
                        var args = (TwMessageReadFwLog)req;
                        return args.CreateResponse(GetFwLog().ToArray());
                    }
                case MessageType.IS_LOCKED:
                    {
                        var args = (TwMessageIsLocked)req;
                        return args.CreateResponse(PasswordLock.Locked);
                    }
                case MessageType.MODE_SWITCH:
                    {
                        var args = (TwMessageModeSwitch)req;
                        FirewallMode newMode = args.Mode;

                        // The mode was taken on trust, and nothing downstream validates it: a value
                        // outside the five real modes matches no case in AssembleActiveRules, so no
                        // default rule gets assembled and the firewall comes back up permissive.
                        // Refused here so it cannot reach that switch, and the running mode is left
                        // alone.
                        //
                        // Reaching this from outside is harder than the pipe ACL suggests. The ACL
                        // does admit Authenticated Users, but PipeServerEndpoint.AuthAsServer then
                        // compares the connecting process's image path against our own and drops
                        // anything else, so a foreign process gets no further (verified: a probe from
                        // powershell.exe is disconnected without a reply). This is defence in depth
                        // behind that check, not the only thing standing in front of it.
                        //
                        // It still earns its place. FirewallMode.Unknown is a defined value (100) and
                        // is what ServerState.Mode starts out as, so the value is one field copy away
                        // from being sent by something that does pass the path check - and a firewall
                        // that answers an unexpected mode by not blocking is the wrong failure.
                        if (!FirewallModes.IsOperatingMode(newMode))
                        {
                            Utils.Log($"Refused a switch to unrecognised firewall mode {(int)newMode}; staying in {VisibleState.Mode}.", Utils.LOG_ID_SERVICE);
                            return TwMessageError.Instance;
                        }

                        try
                        {
                            LogWatcher.Enabled = (FirewallMode.Learning == newMode);
                        }
                        catch (Exception e)
                        {
                            Utils.Log("Cannot enter auto-learn mode. Is the 'eventlog' service running? For details see next log entry.", Utils.LOG_ID_SERVICE);
                            Utils.LogException(e, Utils.LOG_ID_SERVICE);
                            return TwMessageError.Instance;
                        }

                        bool save_needed = CommitLearnedRules();

                        // Everything below this point is undone if the install fails. PUT_SETTINGS
                        // learned this the hard way and its case says why at length: the state that
                        // matters is what WFP is actually enforcing, and InstallFirewallRules() does
                        // its work inside a transaction, so a throw before Commit() leaves the
                        // *previous* rules in force. Reporting the new mode anyway - which is what
                        // this case used to do - tells the user their firewall is in a mode it is
                        // not in, and for someone switching to Block All to contain something, that
                        // is the worst possible lie to tell.
                        var previousMode = VisibleState.Mode;
                        var previousStartupMode = ActiveConfig.Service.StartupMode;
                        var previousChangeset = GlobalInstances.ServerChangeset;

                        VisibleState.Mode = newMode;

                        // GET_SETTINGS only sends VisibleState back when the client's changeset
                        // differs from ours, so mutating VisibleState without moving the changeset
                        // makes the new mode invisible to every client that is already connected:
                        // it asks, we answer "nothing changed", and it keeps showing the old mode
                        // until it is restarted. Reproduced on the VM - switching mode from the
                        // chip really did switch the service, while the running GUI went on
                        // displaying the previous mode indefinitely and a freshly launched GUI
                        // (changeset Guid.Empty, so always different) showed the new one at once.
                        GlobalInstances.ServerChangeset = Guid.NewGuid();
                        if ((ActiveConfig.Service.StartupMode != VisibleState.Mode) &&
                            (VisibleState.Mode != FirewallMode.Disabled) &&
                            (VisibleState.Mode != FirewallMode.Learning) )
                        {
                            ActiveConfig.Service.StartupMode = VisibleState.Mode;
                            save_needed = true;
                        }
                        if (save_needed)
                            ActiveConfig.Service.Save(ConfigSavePath);

                        try
                        {
                            InstallFirewallRules();
                        }
                        catch (Exception e)
                        {
                            Utils.LogException(e, Utils.LOG_ID_SERVICE);

                            VisibleState.Mode = previousMode;
                            ActiveConfig.Service.StartupMode = previousStartupMode;
                            GlobalInstances.ServerChangeset = previousChangeset;

                            // Only worth rewriting if we changed it: on the common path save_needed
                            // is false and the file on disk still describes the mode WFP is running.
                            if (save_needed)
                            {
                                try
                                {
                                    ActiveConfig.Service.Save(ConfigSavePath);
                                }
                                catch (Exception saveError)
                                {
                                    // Disk now disagrees with memory and with WFP, and the next
                                    // start would come up in a mode that just failed to install.
                                    // Nothing further can be done here, but it must not be silent.
                                    Utils.LogException(saveError, Utils.LOG_ID_SERVICE);
                                }
                            }

                            // Auto-learn was switched on before any of this; put it back too, or the
                            // log watcher runs for a mode the service is no longer in.
                            try { LogWatcher.Enabled = (FirewallMode.Learning == previousMode); }
                            catch (Exception watcherError) { Utils.LogException(watcherError, Utils.LOG_ID_SERVICE); }

                            return TwMessageError.Instance;
                        }

                        return args.CreateResponse(VisibleState.Mode);
                    }
                case MessageType.PUT_SETTINGS:
                    {
                        var args = (TwMessagePutSettings)req;

                        bool warning = (args.Changeset != GlobalInstances.ServerChangeset);
                        if (!warning)
                        {
                            var previousConfig = ActiveConfig.Service;
                            var previousChangeset = GlobalInstances.ServerChangeset;
                            try
                            {
                                GlobalInstances.ServerChangeset = Guid.NewGuid();
                                ActiveConfig.Service = args.Config;
                                ActiveConfig.Service.Save(ConfigSavePath);
                                ReapplySettings();
                                InstallFirewallRules();
                            }
                            catch (Exception e)
                            {
                                // This used to log and fall through to the success response below.
                                // InstallFirewallRules() is what actually programs WFP, so a throw
                                // here told the user their firewall rules were saved while they were
                                // not in effect.
                                //
                                // Not reported through `warning`: that flag means one specific thing
                                // the client acts on - the changeset was stale and nothing was applied
                                // - and FirewallClient turns it into RESPONSE_STALE_CHANGESET, sending
                                // the user to retry a conflict that never happened. An outright error
                                // is the honest answer, and Run() already maps that for every other
                                // command.
                                Utils.LogException(e, Utils.LOG_ID_SERVICE);

                                // The live filters need no undo: InstallFirewallRules() does its work
                                // inside a WFP transaction, so throwing before Commit() abandons it and
                                // leaves the previously committed set in place. What can diverge is our
                                // own state - the new config is already in memory and may already be on
                                // disk - so put both back to what WFP is still enforcing.
                                ActiveConfig.Service = previousConfig;
                                GlobalInstances.ServerChangeset = previousChangeset;
                                try
                                {
                                    previousConfig.Save(ConfigSavePath);
                                }
                                catch (Exception saveError)
                                {
                                    // Disk now disagrees with memory and with WFP, and the next service
                                    // start would load the config that just failed to apply. Nothing
                                    // further can be done from here, but it must not be silent.
                                    Utils.LogException(saveError, Utils.LOG_ID_SERVICE);
                                }

                                return TwMessageError.Instance;
                            }
                        }
                        VisibleState.HasPassword = PasswordLock.HasPassword;
                        VisibleState.Locked = PasswordLock.Locked;
                        return args.CreateResponse(GlobalInstances.ServerChangeset, ActiveConfig.Service, VisibleState, warning);
                    }
                case MessageType.ADD_TEMPORARY_EXCEPTION:
                    {
                        var rules = new List<RuleDef>();
                        var rawSocketExceptions = new List<RuleDef>();
                        var args = (TwMessageAddTempException)req;

                        // try/finally because the release used to be an ordinary last statement:
                        // anything thrown above it - a malformed exception reaching
                        // GetRulesForException, say - skipped it, and the worker thread stayed at
                        // ThreadPriority.Highest for the life of the service. The matching Request()
                        // is in ProcessStartWatcher_EventArrived, which raised the priority so this
                        // message would be handled promptly; that bargain has to be settled on
                        // every path out.
                        try
                        {
                            foreach (var ex in args.Exceptions)
                            {
                                GetRulesForException(ex, rules, rawSocketExceptions, (ulong)FilterWeights.UserPermit, (ulong)FilterWeights.UserBlock);
                            }

                            InstallRules(rules, rawSocketExceptions, true);
                        }
                        finally
                        {
                            lock (FirewallThreadThrottler.SynchRoot) { FirewallThreadThrottler.Release(); }
                        }

                        return args.CreateResponse();
                    }
                case MessageType.GET_SETTINGS:
                    {
                        var args = (TwMessageGetSettings)req;

                        // If our changeset is different from the client's, send new settings
                        if (args.Changeset != GlobalInstances.ServerChangeset)
                        {
                            VisibleState.HasPassword = PasswordLock.HasPassword;
                            VisibleState.Locked = PasswordLock.Locked;

                            var ret = args.CreateResponse(GlobalInstances.ServerChangeset, ActiveConfig.Service, Utils.DeepClone(VisibleState));
                            VisibleState.ClientNotifs.Clear();
                            return ret;
                        }
                        else
                        {
                            // Our changeset is the same, so do not send settings again
                            return args.CreateResponse(GlobalInstances.ServerChangeset);
                        }
                    }
                case MessageType.REINIT:
                    {
                        var args = (TwMessageSimple)req;
                        return TryInitFirewall() ? args.CreateResponse() : TwMessageError.Instance;
                    }
                case MessageType.RELOAD_WFP_FILTERS:
                    {
                        var args = (TwMessageSimple)req;
                        InstallFirewallRules();
                        return args.CreateResponse();
                    }
                case MessageType.UNLOCK:
                    {
                        var args = (TwMessageUnlock)req;
                        bool success = PasswordLock.Unlock(args.Password);
                        if (success)
                        {
                            // Migrate a password still stored under the old PBKDF2-HMAC-SHA1 scheme
                            // now that we have both the plaintext and a verified match. The write
                            // has to be bracketed by the FileLocker the same way SET_PASSPHRASE
                            // does it - this service keeps the password file open for reading, so
                            // an unbracketed write just throws and the upgrade silently never
                            // happens (which is exactly what it did before this bracket existed).
                            if (PasswordLock.StoredHashNeedsUpgrade)
                            {
                                FileLocker.Unlock(PasswordLock.PasswordFilePath);
                                try
                                {
                                    PasswordLock.UpgradeStoredHash(args.Password);
                                }
                                catch (Exception e)
                                {
                                    // A failed rewrite must not turn a valid unlock into a refusal;
                                    // the existing record still verifies.
                                    Utils.LogException(e, Utils.LOG_ID_SERVICE);
                                }
                                finally
                                {
                                    FileLocker.Lock(PasswordLock.PasswordFilePath, FileAccess.Read, FileShare.Read);
                                }
                            }

                            // Same reason as MODE_SWITCH above: GET_SETTINGS recomputes
                            // VisibleState.Locked from PasswordLock, but only actually sends it
                            // when the changeset moved.
                            GlobalInstances.ServerChangeset = Guid.NewGuid();
                            return args.CreateResponse();
                        }
                        else
                            return TwMessageError.Instance;
                    }
                case MessageType.LOCK:
                    {
                        var args = (TwMessageSimple)req;
                        PasswordLock.Locked = true;
                        // Unconditional, even though PasswordLock's setter is inert without a
                        // password: the cost of moving the changeset when nothing changed is one
                        // redundant settings response, whereas not moving it when something did
                        // leaves every connected client showing an unlocked firewall as locked,
                        // or the reverse.
                        GlobalInstances.ServerChangeset = Guid.NewGuid();
                        return args.CreateResponse();
                    }
                case MessageType.GET_PROCESS_PATH:
                    {
                        var args = (TwMessageGetProcessPath)req;
                        string path = Utils.GetPathOfProcess(args.Pid);
                        if (string.IsNullOrEmpty(path))
                            return TwMessageError.Instance;
                        else
                            return args.CreateResponse(path);
                    }
                case MessageType.SET_PASSPHRASE:
                    {
                        var args = (TwMessageSetPassword)req;
                        FileLocker.Unlock(PasswordLock.PasswordFilePath);
                        try
                        {
                            PasswordLock.SetPass(args.Password);
                            GlobalInstances.ServerChangeset = Guid.NewGuid();
                            return args.CreateResponse();
                        }
                        catch
                        {
                            return TwMessageError.Instance;
                        }
                        finally
                        {
                            FileLocker.Lock(PasswordLock.PasswordFilePath, FileAccess.Read, FileShare.Read);
                        }
                    }
                case MessageType.STOP_SERVICE:
                    {
                        var args = (TwMessageSimple)req;
                        RunService = false;
                        return args.CreateResponse();
                    }
                case MessageType.MINUTE_TIMER:
                    {
                        var args = (TwMessageSimple)req;
                        bool save_needed = false;
                        bool rule_reload_needed = false;

                        // Event collection might have been disabled by external process or user after we started up,
                        // so re-enable it if that is the case.
                        if (!WfpEngine.CollectNetEvents)
                            WfpEngine.CollectNetEvents = true;

                        // Startup did not get through earlier, so the rules in force are not the
                        // configured ones. Retried on the timer rather than waiting for a client
                        // command that may never arrive - a service with no GUI running would
                        // otherwise stay degraded until the next reboot. Called directly instead of
                        // queued: this is the worker thread, and the queue it would post to is
                        // bounded and blocks when full.
                        if ((VisibleState.Degraded & ServiceDegradation.InitializationFailed) != 0)
                        {
                            Utils.Log("Retrying initialization after an earlier failure.", Utils.LOG_ID_SERVICE);
                            TryInitFirewall();
                        }

                        // Check for inactivity and lock if necessary
                        if (DateTime.Now - LastControllerCommandTime > TimeSpan.FromMinutes(10))
                        {
                            Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.LOCK)));
                        }

                        if (PruneExpiredRules())
                        {
                            save_needed = true;
                            rule_reload_needed = true;
                        }

                        // Periodically reload all rules.
                        // This is needed to clear out temprary rules added due to child-process rule inheritance.
                        if (DateTime.Now - LastRuleReloadTime > TimeSpan.FromMinutes(30))
                        {
                            rule_reload_needed = true;
                        }

                        if (save_needed)
                        {
                            ActiveConfig.Service.Save(ConfigSavePath);
                        }
                        if (rule_reload_needed)
                        {
                            InstallFirewallRules();
                        }

                        // Check for updates once every 2 days
                        if (ActiveConfig.Service.AutoUpdateCheck && (DateTime.Now - LastUpdateCheck >= TimeSpan.FromDays(2)))
                        {
                            LastUpdateCheck = DateTime.Now;
                            UpdaterMethod();
                        }

                        return args.CreateResponse();
                    }
                case MessageType.REENUMERATE_ADDRESSES:
                    {
                        var args = (TwMessageSimple)req;
                        if (ReenumerateAdresses())  // returns true if anything changed
                            InstallFirewallRules();
                        return args.CreateResponse();
                    }
                case MessageType.DISPLAY_POWER_EVENT:
                    {
                        var args = (TwMessageDisplayPowerEvent)req;
                        if (args.PowerOn != DisplayCurrentlyOn)
                        {
                            DisplayCurrentlyOn = args.PowerOn;
                            InstallFirewallRules();
                        }
                        return args.CreateResponse(args.PowerOn);
                    }
                default:
                    {
                        return TwMessageError.Instance;
                    }
            }
        }

        private bool ReenumerateAdresses()
        {
            using var timer = new HierarchicalStopwatch("NIC enumeration");
            var newLocalSubnetAddreses = new HashSet<IpAddrMask>();
            // Use direct P/Invoke to GetAdaptersAddresses instead of
            // NetworkInterface.GetAllNetworkInterfaces() to avoid native memory leak
            // in iphlpapi!GetPerAdapterInfo -> DNSAPI!Dns_AllocZero (~15KB per call).
            if (!NetworkAdapterEnumerator.EnumerateActiveAdapters(
                out var unicastList, out var newGatewayAddresses, out var newDnsAddresses))
            {
                return false;
            }

            foreach (var entry in unicastList)
            {
                if (entry.IsLoopback || entry.IsLinkLocal)
                    continue;

                newLocalSubnetAddreses.Add(entry.Subnet);
            }

            newLocalSubnetAddreses.Add(new IpAddrMask(IPAddress.Parse("255.255.255.255")));
            newLocalSubnetAddreses.Add(IpAddrMask.LinkLocal);
            newLocalSubnetAddreses.Add(IpAddrMask.IPv6LinkLocal);
            newLocalSubnetAddreses.Add(IpAddrMask.LinkLocalMulticast);
            newLocalSubnetAddreses.Add(IpAddrMask.AdminScopedMulticast);
            newLocalSubnetAddreses.Add(IpAddrMask.IPv6LinkLocalMulticast);

            bool ipConfigurationChanged =
                !LocalSubnetAddreses.SetEquals(newLocalSubnetAddreses) ||
                !GatewayAddresses.SetEquals(newGatewayAddresses) ||
                !DnsAddresses.SetEquals(newDnsAddresses);

            if (ipConfigurationChanged)
            {
                LocalSubnetAddreses = newLocalSubnetAddreses;
                GatewayAddresses = newGatewayAddresses;
                DnsAddresses = newDnsAddresses;

                LocalSubnetFilterConditions.Clear();
                GatewayFilterConditions.Clear();
                DnsFilterConditions.Clear();

                foreach (var addr in LocalSubnetAddreses)
                    LocalSubnetFilterConditions.Add(new IpFilterCondition(addr.Address, (byte)addr.PrefixLen, RemoteOrLocal.Remote));
                foreach (var addr in GatewayAddresses)
                    GatewayFilterConditions.Add(new IpFilterCondition(addr.Address, (byte)addr.PrefixLen, RemoteOrLocal.Remote));
                foreach (var addr in DnsAddresses)
                    DnsFilterConditions.Add(new IpFilterCondition(addr.Address, (byte)addr.PrefixLen, RemoteOrLocal.Remote));
            }

            return ipConfigurationChanged;
        }

        internal static void DeleteWfpObjects(Engine wfp, bool removeLayersAndProvider)
        {
            // WARNING! This method is super-slow if not executed inside a WFP transaction!
            using var timer = new HierarchicalStopwatch("DeleteWfpObjects()");
            var layerKeys = (LayerKeyEnum[])Enum.GetValues(typeof(LayerKeyEnum));
            foreach (var layer in layerKeys)
            {
                Guid layerKey = GetLayerKey(layer);
                Guid subLayerKey = GetSublayerKey(layer);

                // Remove filters in the sublayer
                foreach (var filterKey in wfp.EnumerateFilterKeys(TINYWALL_PROVIDER_KEY, layerKey))
                    wfp.UnregisterFilter(filterKey);

                // Remove sublayer
                if (removeLayersAndProvider)
                    try { wfp.UnregisterSublayer(subLayerKey); } catch { }
            }

            // Remove provider
            if (removeLayersAndProvider)
                try { wfp.UnregisterProvider(TINYWALL_PROVIDER_KEY); } catch { }
        }

        public SimpleDeFenceServer()
        {
            // Make sure the very-first command is a REINIT
            Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.REINIT)));

            // Fire up file protections as soon as possible
            FileLocker.Lock(DatabaseClasses.AppDatabase.DBPath, FileAccess.Read, FileShare.Read);
            FileLocker.Lock(PasswordLock.PasswordFilePath, FileAccess.Read, FileShare.Read);

            // Lock configuration if we have a password
            if (PasswordLock.HasPassword)
                PasswordLock.Locked = true;

            LogWatcher.NewLogEntry += (sender, entry) => AutoLearnLogEntry(entry);
            MinuteTimer = new Timer(new TimerCallback(TimerCallback), null, Timeout.Infinite, Timeout.Infinite);

            // Discover network configuration
            ReenumerateAdresses();

            // Fire up pipe
            ServerPipe = new PipeServerEndpoint(new PipeDataReceived(PipeServerDataReceived), "SimpleDeFenceController");
        }

        // Entry point for thread that actually issues commands to Windows Firewall.
        // Only one thread (this one) is allowed to issue them.
        public void Run(ServiceBase service)
        {
            using var timer = new HierarchicalStopwatch("Service Run()");
            using var WinDefFirewall = new WindowsFirewall();
            using var NetworkInterfaceWatcher = new IpInterfaceWatcher();
            using var DisplayOffSubscription = SafeHandlePowerSettingNotification.Create(service.ServiceHandle, PowerSetting.GUID_CONSOLE_DISPLAY_STATE, DeviceNotifFlags.DEVICE_NOTIFY_SERVICE_HANDLE);
            using var DeviceNotification = SafeHandleDeviceNotification.Create(service.ServiceHandle, DeviceInterfaceClass.GUID_DEVINTERFACE_VOLUME, DeviceNotifFlags.DEVICE_NOTIFY_SERVICE_HANDLE);
            using var MountPointsWatcher = new RegistryWatcher(@"HKEY_LOCAL_MACHINE\SYSTEM\MountedDevices", true);

            // Enabling net-event collection on the engine is only half of what makes the Filtering
            // Platform report a drop: the matching Windows audit subcategories have to be on as
            // well, or the callback below is simply never called and FirewallLogEntries stays
            // empty. That was the state on any installation that had not been put into Learning
            // mode, because FirewallLogWatcher.Enabled - set from the mode switch and nowhere else
            // - was what used to turn them on, and it turned them back off on the way out.
            //
            // Paired with the collection option and released the same way, so the machine is left
            // as it was found when the service stops.
            WfpEngine.CollectNetEvents = true;
            using var NetEventCollection = new CallbackOnDispose(() => { try { WfpEngine.CollectNetEvents = false; } catch { } });
            FirewallLogWatcher.AuditPolicy.Enable();
            using var NetEventAuditing = new CallbackOnDispose(() => { try { FirewallLogWatcher.AuditPolicy.Disable(); } catch { } });
            WfpEngine.EventMatchAnyKeywords = InboundEventMatchKeyword.FWPM_NET_EVENT_KEYWORD_INBOUND_BCAST | InboundEventMatchKeyword.FWPM_NET_EVENT_KEYWORD_INBOUND_MCAST;
            using var WfpEvent = WfpEngine.SubscribeNetEvent(WfpNetEventCallback);

            ProcessStartWatcher.EventArrived += ProcessStartWatcher_EventArrived;
            NetworkInterfaceWatcher.InterfaceChanged += (sender, args) =>
            {
                Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.REENUMERATE_ADDRESSES)));
            };
            RuleReloadEventMerger.Event += (sender, args) =>
            {
                Q.Add(new TwRequest(TwMessageSimple.CreateRequest(MessageType.RELOAD_WFP_FILTERS)));
            };
            MountPointsWatcher.RegistryChanged += (sender, args) =>
            {
                RuleReloadEventMerger.Pulse();
            };
            MountPointsWatcher.Enabled = true;
            service.FinishStateChange();
#if !DEBUG
            // Basic software health checks
            SimpleDeFenceDoctor.EnsureHealth(Utils.LOG_ID_SERVICE);
#endif

            MinuteTimer.Change(60000, 60000);
            RunService = true;
            while (RunService)
            {
                timer.NewSubTask("Message wait");
                var req = Q.Take();

                timer.NewSubTask($"Message {req.Request.Type}");
                try
                {
                    req.Response = ProcessCmd(req.Request);
                }
                catch (Exception e)
                {
                    Utils.LogException(e, Utils.LOG_ID_SERVICE);
                    req.Response = TwMessageError.Instance;
                }
            }
        }

        private void ProcessStartWatcher_EventArrived(object sender, EventArrivedEventArgs e)
        {
            try
            {
                using var throttler = new ThreadThrottler(Thread.CurrentThread, ThreadPriority.Highest, true);
                uint pid = (uint)(e.NewEvent["ProcessID"]);
                string path = ProcessManager.GetProcessPath(pid, ref ProcessStartWatcher_Sbuilder);

                // Skip if we have no path
                if (string.IsNullOrEmpty(path))
                    return;

                List<FirewallExceptionV3>? newExceptions = null;

                lock (InheritanceGuard)
                {
                    // Skip if we have a user-defined rule for this path
                    if (UserSubjectExes.Contains(path))
                        return;

                    // This list will hold parents that we already checked for a process.
                    // Used to avoid infinite loop when parent-PID info is unreliable.
                    var pidsChecked = new HashSet<uint>();

                    // Start walking up the process tree
                    for (var parentPid = pid; ;)
                    {
                        if (!ProcessManager.GetParentProcess(parentPid, ref parentPid))
                            // We reached the top of the process tree (with non-existent parent)
                            break;

                        if (parentPid == 0)
                            // We reached top of process tree (with idle process)
                            break;

                        if (pidsChecked.Contains(parentPid))
                            // We've been here before, damn it. Avoid looping eternally...
                            break;

                        pidsChecked.Add(parentPid);

                        string parentPath = ProcessManager.GetProcessPath(parentPid, ref ProcessStartWatcher_Sbuilder);
                        if (string.IsNullOrEmpty(parentPath))
                            continue;

                        // Skip if we have already processed this parent-child combination
                        if (ChildInheritedSubjectExes.TryGetValue(path, out var childVar))
                        {
                            if (childVar.Contains(parentPath))
                                break;
                        }

                        if (ChildInheritance.TryGetValue(parentPath, out List<FirewallExceptionV3> exList))
                        {
                            newExceptions ??= new List<FirewallExceptionV3>();

                            foreach (var userEx in exList)
                                newExceptions.Add(new FirewallExceptionV3(new ExecutableSubject(path), userEx.Policy));

                            if (!ChildInheritedSubjectExes.ContainsKey(path))
                                ChildInheritedSubjectExes.Add(path, new HashSet<string>());
                            ChildInheritedSubjectExes[path].Add(parentPath);
                            break;
                        }
                    }
                }

                if (newExceptions != null)
                {
                    lock (FirewallThreadThrottler.SynchRoot) { FirewallThreadThrottler.Request(); }
                    Q.Add(new TwRequest(TwMessageAddTempException.CreateRequest(newExceptions.ToArray())));
                }
            }
            finally
            {
                e.NewEvent.Dispose();
            }
        }

        private void WfpNetEventCallback(NetEventData data)
        {
            EventLogEvent eventType;
            if (data.EventType == FWPM_NET_EVENT_TYPE.FWPM_NET_EVENT_TYPE_CLASSIFY_DROP)
                eventType = EventLogEvent.BLOCKED;
            else if (data.EventType == FWPM_NET_EVENT_TYPE.FWPM_NET_EVENT_TYPE_CLASSIFY_ALLOW)
                eventType = EventLogEvent.ALLOWED;
            else
                return;

            var entry = new FirewallLogEntry
            {
                Timestamp = data.timeStamp,
                Event = eventType,
                PackageId = data.packageId,
                RemoteIp = data.remoteAddr?.ToString(),
                LocalIp = data.localAddr?.ToString()
            };

            if (!string.IsNullOrEmpty(data.appId))
                entry.AppPath = PathMapper.Instance.ConvertPathIgnoreErrors(data.appId, PathFormat.Win32);
            else
                entry.AppPath = "System";
            if (data.remotePort.HasValue)
                entry.RemotePort = data.remotePort.Value;
            if (data.direction.HasValue)
                entry.Direction = data.direction == FwpmDirection.FWP_DIRECTION_OUT ? RuleDirection.Out : RuleDirection.In;
            if (data.ipProtocol.HasValue)
                entry.Protocol = (Protocol)data.ipProtocol;
            if (data.localPort.HasValue)
                entry.LocalPort = data.localPort.Value;

            // Replace invalid IP strings with the "unspecified address" IPv6 specifier
            if (string.IsNullOrEmpty(entry.RemoteIp))
                entry.RemoteIp = "::";
            if (string.IsNullOrEmpty(entry.LocalIp))
                entry.LocalIp = "::";

            // Drops only - see FirewallLogEntries. An allowed event is discarded by every reader
            // of this ring, so keeping it would only evict a blocked one that somebody is going to
            // go looking for.
            if (eventType == EventLogEvent.BLOCKED)
            {
                lock (FirewallLogEntries)
                {
                    FirewallLogEntries.Enqueue(entry);
                }
            }
        }

        private void AutoLearnLogEntry(FirewallLogEntry entry)
        {
            if (  // IPv4
                ((string.Equals(entry.RemoteIp, "127.0.0.1", StringComparison.Ordinal)
                && string.Equals(entry.LocalIp, "127.0.0.1", StringComparison.Ordinal)))
               || // IPv6
                ((string.Equals(entry.RemoteIp, "::1", StringComparison.Ordinal)
                && string.Equals(entry.LocalIp, "::1", StringComparison.Ordinal)))
               )
            {
                // Ignore communication within local machine
                return;
            }

            // Certain things we don't want to whitelist.
            //
            // The svchost test is on the file name, not on the whole path. It used to compare
            // entry.AppPath - a full Win32 path by this point, ConvertPathIgnoreErrors then
            // GetExactPath in FirewallLogWatcher.ParseLogEntry - against the bare string
            // "svchost.exe", which is never equal to "C:\Windows\System32\svchost.exe". The guard
            // has therefore never fired, and the one binary it exists to keep out is the one that
            // hosts most of Windows' services: a single Learning-mode session whitelisted svchost
            // with AppDatabase's fallback policy, TcpUdpPolicy(unrestricted: true) - every TCP and
            // UDP port, inbound and outbound - and CommitLearnedRules made it permanent. Every
            // service sharing that host, RemoteRegistry and WinRM included, inherited it.
            //
            // "System" is left comparing the whole value on purpose: for kernel traffic AppPath is
            // literally the string "System", not a path, so there is no file name to take.
            var appFileName = string.IsNullOrEmpty(entry.AppPath)
                ? string.Empty
                : System.IO.Path.GetFileName(entry.AppPath);

            if (string.IsNullOrEmpty(entry.AppPath)
                || string.Equals(entry.AppPath, "System", StringComparison.InvariantCultureIgnoreCase)
                || string.Equals(appFileName, "svchost.exe", StringComparison.InvariantCultureIgnoreCase)
                )
                return;

            var newSubject = new ExecutableSubject(entry.AppPath);

            lock (LearningNewExceptions)
            {
                for (int j = 0; j < LearningNewExceptions.Count; ++j)
                {
                    if (LearningNewExceptions[j].Subject.Equals(newSubject))
                        // Already in LearningNewExceptions, nothing to do
                        return;
                }

                var exceptions = GlobalInstances.AppDatabase.GetExceptionsForApp(newSubject, false, out _);
                LearningNewExceptions.AddRange(exceptions);
            }
        }

        // Entry point for thread that listens to commands from the controller application.
        private TwMessage PipeServerDataReceived(TwMessage reqMsg)
        {
            if (((int)reqMsg.Type > 2047) && PasswordLock.Locked)
            {
                // Notify that we need to be unlocked first
                return TwMessageLocked.Instance;
            }
            if (((int)reqMsg.Type > 4095))
            {
                // We cannot receive this from the client
                return TwMessageError.Instance;
            }
            else
            {
                LastControllerCommandTime = DateTime.Now;

                // Process and wait for response
                var req = new TwRequest(reqMsg);
                Q.Add(req);

                // Send response back to pipe
                return req.Response;
            }
        }

        public void RequestStop()
        {
            var req = new TwRequest(TwMessageSimple.CreateRequest(MessageType.STOP_SERVICE));
            Q.Add(req);
            req.WaitResponse();
        }

        public void DisplayPowerEvent(bool turnOn)
        {
            Q.Add(new TwRequest(TwMessageDisplayPowerEvent.CreateRequest(turnOn)));
        }

        public void MountedVolumesChangedEvent()
        {
            RuleReloadEventMerger.Pulse();
        }

        public void Dispose()
        {
            using var timer = new HierarchicalStopwatch("SimpleDeFenceService.Dispose()");
            ServerPipe?.Dispose();
            ProcessStartWatcher.EventArrived -= ProcessStartWatcher_EventArrived;
            try { ProcessStartWatcher.Stop(); } catch { }
            ProcessStartWatcher.Dispose();

            if (MinuteTimer != null)
            {
                using WaitHandle wh = new AutoResetEvent(false);
                MinuteTimer.Dispose(wh);
                wh.WaitOne();
            }

            if (CommitLearnedRules())
                ActiveConfig.Service.Save(ConfigSavePath);

            RuleReloadEventMerger.Dispose();
            LocalSubnetFilterConditions.Dispose();
            GatewayFilterConditions.Dispose();
            DnsFilterConditions.Dispose();
            LogWatcher.Dispose();
            HostsFileManager.Dispose();
            FileLocker.UnlockAll();

            FirewallThreadThrottler?.Dispose();
            Q.Dispose();
            WfpEngine.Dispose();

#if !DEBUG
            // Basic software health checks
            SimpleDeFenceDoctor.EnsureHealth(Utils.LOG_ID_SERVICE);
#else
                using (var wfp = new Engine("SimpleDeFence Cleanup Session", "", FWPM_SESSION_FLAGS.None, 5000))
                using (var trx = wfp.BeginTransaction())
                {
                    DeleteWfpObjects(wfp, true);
                    trx.Commit();
                }
#endif
            PathMapper.Instance.Dispose();
        }
    }


    internal sealed class SimpleDeFenceService : ServiceBase
    {
        internal readonly static string[] ServiceDependencies = new string[]
        {
            "Schedule",
            "Winmgmt",
            "BFE"
        };

        internal const string SERVICE_NAME = "SimpleDeFence";
        internal const string SERVICE_DISPLAY_NAME = "SimpleDeFence Service";

        private SimpleDeFenceServer? Server;
        private Thread? FirewallWorkerThread;
#if !DEBUG
        private bool IsComputerShuttingDown;
#endif
        internal SimpleDeFenceService()
            : base()
        {
            this.AcceptedControls = ServiceAcceptedControl.SERVICE_ACCEPT_SHUTDOWN;
            this.AcceptedControls |= ServiceAcceptedControl.SERVICE_ACCEPT_POWEREVENT;
#if DEBUG
            this.AcceptedControls |= ServiceAcceptedControl.SERVICE_ACCEPT_STOP;
#endif
        }

        public override string ServiceName
        {
            get { return SERVICE_NAME; }
        }

        private void FirewallWorkerMethod()
        {
            try
            {
                using (Server = new SimpleDeFenceServer())
                {
                    Server.Run(this);
                }
            }
            finally
            {
#if !DEBUG
                Thread.MemoryBarrier();
                if (!IsComputerShuttingDown)    // cannot set service state if a shutdown is already in progress
                {
                    SetServiceStateReached(ServiceState.Stopped);
                }
                Process.GetCurrentProcess().Kill();
#endif
            }
        }

        // Entry point for Windows service.
        protected override void OnStart(string[] args)
        {
            // Initialization on a new thread prevents stalling the SCM
            FirewallWorkerThread = new Thread(new ThreadStart(FirewallWorkerMethod)) { Name = "ServiceMain" };
            FirewallWorkerThread.Start();
        }

        private void StopServer()
        {
            Thread.MemoryBarrier();
            Server?.RequestStop();
            FirewallWorkerThread?.Join(10000);
            FinishStateChange();
        }

        // Executed when service is stopped manually.
        protected override void OnStop()
        {
            StopServer();
        }

        // Executed on computer shutdown.
        protected override void OnShutdown()
        {
#if !DEBUG
            IsComputerShuttingDown = true;
#endif
            StartStateChange(ServiceState.StopPending);
        }

        protected override void OnDeviceEvent(DeviceEventData data)
        {
            if ((data.Event == DeviceEventType.DeviceArrival) || (data.Event == DeviceEventType.DeviceRemoveComplete))
            {
                bool pathMapperRebuildNeeded = false;

                if (data.DeviceType == DeviceBroadcastHdrDevType.DBT_DEVTYP_DEVICEINTERFACE)
                {
                    if (data.Class == DeviceInterfaceClass.GUID_DEVINTERFACE_VOLUME)
                    {
                        pathMapperRebuildNeeded = true;
                    }
                }
                else if (data.DeviceType == DeviceBroadcastHdrDevType.DBT_DEVTYP_VOLUME)
                {
                    pathMapperRebuildNeeded = true;
                }

                if (pathMapperRebuildNeeded)
                {
                    Server?.MountedVolumesChangedEvent();
                }
            }
        }

        protected override void OnPowerEvent(PowerEventData data)
        {
            if (data.Event == PowerEventType.PowerSettingChange)
            {
                if (data.Setting == PowerSetting.GUID_CONSOLE_DISPLAY_STATE)
                {
                    if (data.PayloadInt == 0)
                        Server?.DisplayPowerEvent(false);
                    else if (data.PayloadInt == 1)
                        Server?.DisplayPowerEvent(true);
                    else
                    {
                        // Dimming event... ignore
                    }
                }
            }
        }
    }
}
