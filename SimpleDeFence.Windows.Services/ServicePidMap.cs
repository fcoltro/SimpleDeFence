using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ServiceProcess;

namespace SimpleDeFence.Windows.Services
{
    public class ServicePidMap
    {
        private readonly Dictionary<uint, HashSet<string>> Cache = new();

        /// <summary>
        /// Builds the pid-to-service-names map, skipping every service it cannot read rather than
        /// failing as a whole.
        ///
        /// ServiceController.GetServices() is a snapshot. By the time this loop reaches an entry
        /// the service may have stopped and been removed - OpenService then fails and
        /// GetServicePid throws Win32Exception("The specified service does not exist as an
        /// installed service") - or it may be one whose DACL does not grant SERVICE_QUERY_STATUS
        /// to the caller, which throws the same way with ERROR_ACCESS_DENIED. Reading .Status can
        /// throw InvalidOperationException for the same disappearing-service reason.
        ///
        /// None of that used to be caught anywhere. The exception left this constructor, and the
        /// caller that matters - FirewallClient.GetConnectionsAsync, which builds this map before
        /// it gathers anything - had no handler either, so it propagated to
        /// ConnectionsPage.RefreshAsync's catch. That handler keeps the previous snapshot and
        /// shows an error, which on the first refresh after launch means an empty snapshot: the
        /// Blocked, Connected AND Open sections all render as "nothing here", including the two
        /// that are built from a purely local API and had nothing to do with the failure. One
        /// service stopping at the wrong moment blanked the entire Connections screen.
        ///
        /// A service that cannot be queried is simply a service this map does not know about,
        /// which is already how GetServicesInPid answers for any pid it has not seen. Callers get
        /// a connection row named after its executable instead of its service - a worse name, not
        /// a missing row.
        /// </summary>
        public ServicePidMap()
        {
            using var scm = new ServiceControlManager();
            var services = ServiceController.GetServices();
            try
            {
                foreach (var service in services)
                {
                    string serviceName;
                    try
                    {
                        if (service.Status != ServiceControllerStatus.Running)
                            continue;

                        serviceName = service.ServiceName;
                    }
                    catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                    {
                        // Gone between the snapshot and now.
                        continue;
                    }

                    uint pid;
                    try
                    {
                        pid = scm.GetServicePid(serviceName) ?? 0;
                    }
                    catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                    {
                        // Gone, or not ours to query.
                        continue;
                    }

                    if (pid != 0)
                    {
                        if (!Cache.ContainsKey(pid))
                            Cache.Add(pid, new HashSet<string>());
                        Cache[pid].Add(serviceName);
                    }
                }
            }
            finally
            {
                foreach (var service in services)
                    service.Dispose();
            }
        }

        /// <summary>Used only by <see cref="CreateOrEmpty"/>, for the map that knows nothing.</summary>
        private ServicePidMap(bool _)
        {
        }

        /// <summary>
        /// The map, or one that knows about no services at all when it cannot be built.
        ///
        /// The constructor tolerates individual services failing, but opening the service control
        /// manager or enumerating it at all can still fail outright, and for callers that only
        /// want this to put nicer names on rows there is nothing useful to do with that exception.
        /// Naming a svchost.exe row "svchost.exe" instead of "DoSvc, UsoSvc" is a smaller loss
        /// than showing no rows.
        /// </summary>
        public static ServicePidMap CreateOrEmpty()
        {
            try
            {
                return new ServicePidMap();
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                return new ServicePidMap(false);
            }
        }

        public HashSet<string> GetServicesInPid(uint pid)
        {
            if (Cache.TryGetValue(pid, out HashSet<string> set))
                return new HashSet<string>(set);
            else
                return new HashSet<string>();
        }
    }
}
