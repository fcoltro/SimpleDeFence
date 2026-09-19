using SimpleDeFence;
using System.Collections.Generic;

namespace SimpleDeFence.Windows.Services
{
    public class ProcessInfo
    {
        public uint Pid;
        public string Path;
        public UwpPackageList.Package? Package;
        public HashSet<string> Services;

        /// <summary>Public so a caller that has already resolved these - for instance one
        /// memoizing the expensive per-pid lookups across many rows - can build the record without
        /// going through the Create overloads and repeating that work.</summary>
        public ProcessInfo(uint pid, string path, UwpPackageList.Package? package, HashSet<string> services)
        {
            Pid = pid;
            Path = path;
            Package = package;
            Services = services;
        }

        public static ProcessInfo Create(uint pid, string path, UwpPackageList uwp, ServicePidMap servicePids)
        {
            return new ProcessInfo(
                pid,
                path,
                uwp.FindPackageForProcess(pid),
                servicePids.GetServicesInPid(pid)
            );
        }
        public static ProcessInfo Create(uint pid, string path, string? packageId, UwpPackageList uwp, ServicePidMap servicePids)
        {
            return new ProcessInfo(
                pid,
                path,
                uwp.FindPackage(packageId),
                servicePids.GetServicesInPid(pid)
            );
        }
    }
}
