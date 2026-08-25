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

        private ProcessInfo(uint pid, string path, UwpPackageList.Package? package, HashSet<string> services)
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
