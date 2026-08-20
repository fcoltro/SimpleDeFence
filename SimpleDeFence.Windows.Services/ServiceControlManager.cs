using System;
using System.ComponentModel;
using System.ServiceProcess;
using System.Runtime.InteropServices;

namespace SimpleDeFence.Windows.Services
{
    public class ServiceControlManager : IDisposable
    {
        private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;

        private bool disposed;
        private readonly SafeServiceHandle SCManager;

        private SafeServiceHandle OpenService(string serviceName, ServiceAccessRights desiredAccess)
        {
            // Open the service
            var service = NativeMethods.OpenService(
                SCManager,
                serviceName,
                desiredAccess);

            // Verify if the service is opened
            if (service.IsInvalid)
                throw new Win32Exception();

            return service;
        }

        /// <summary>
        /// Opens the service control manager with SC_MANAGER_CONNECT, plus any additional rights
        /// requested through <paramref name="extraRights"/>.
        /// </summary>
        /// <param name="extraRights">
        /// Extra SCM-level access rights. Keep this at the default unless the operation really
        /// needs more: SC_MANAGER_CONNECT alone is granted to non-elevated processes, while asking
        /// for anything beyond it (SC_MANAGER_CREATE_SERVICE in particular) makes OpenSCManager
        /// fail with ERROR_ACCESS_DENIED when the process is not elevated. Operations that act on
        /// a single service (DeleteService, SetStartupMode, GetServicePid, ...) go through
        /// OpenService and carry their own per-service rights, so they do not need anything here.
        /// </param>
        public ServiceControlManager(ServiceControlAccessRights extraRights = default)
        {
            // Open the service control manager
            SCManager = NativeMethods.OpenSCManager(
                null,
                null,
                ServiceControlAccessRights.SC_MANAGER_CONNECT | extraRights);

            // Verify if the SC is opened
            if (SCManager.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }


        /// <summary>
        /// Sets the nominated service to restart on failure.
        /// </summary>
        public void SetRestartOnFailure(string serviceName, bool restartOnFailure)
        {
            const uint delay = 1000;
            const int MAX_ACTIONS = 2;
            int SC_ACTION_SIZE = Marshal.SizeOf<SC_ACTION>();

            // Open the service
            using var service = OpenService(
                serviceName,
                ServiceAccessRights.SERVICE_CHANGE_CONFIG |
                ServiceAccessRights.SERVICE_START);

            using var actionPtr = SafeHGlobalHandle.Alloc(SC_ACTION_SIZE * MAX_ACTIONS);
            int actionCount;
            if (restartOnFailure)
            {
                actionCount = 2;

                // Set up the restart action
                var action1 = new SC_ACTION { Type = SC_ACTION_TYPE.SC_ACTION_RESTART, Delay = delay };
                actionPtr.MarshalFromStruct(action1, 0);

                // Set up the "do nothing" action
                var action2 = new SC_ACTION { Type = SC_ACTION_TYPE.SC_ACTION_NONE, Delay = delay };
                actionPtr.MarshalFromStruct(action2, SC_ACTION_SIZE);
            }
            else
            {
                actionCount = 1;

                // Set up the "do nothing" action
                var action1 = new SC_ACTION { Type = SC_ACTION_TYPE.SC_ACTION_NONE, Delay = delay };
                actionPtr.MarshalFromStruct(action1);
            }

            // Set up the failure actions
            var failureActions = new SERVICE_FAILURE_ACTIONS
            {
                dwResetPeriod = 0,
                cActions = (uint)actionCount,
                lpsaActions = actionPtr.DangerousGetHandle(),
                lpRebootMsg = null,
                lpCommand = null
            };
            using var failureActionsPtr = SafeHGlobalHandle.FromManagedStruct(failureActions);

            // Make the change
            if (!NativeMethods.ChangeServiceConfig2(
                service,
                ServiceConfig2InfoLevel.SERVICE_CONFIG_FAILURE_ACTIONS,
                failureActionsPtr.DangerousGetHandle()))
            {
                var err_code = Marshal.GetLastWin32Error();
                throw new Win32Exception(err_code, $"ChangeServiceConfig2 failed with error {err_code}.");
            }
        }

        public void SetStartupMode(string serviceName, ServiceStartMode mode)
        {
            using var service = OpenService(
                serviceName,
                ServiceAccessRights.SERVICE_CHANGE_CONFIG |
                ServiceAccessRights.SERVICE_QUERY_CONFIG
            );
            var result = NativeMethods.ChangeServiceConfig(
                service,
                SERVICE_NO_CHANGE,
                (uint)mode,
                SERVICE_NO_CHANGE,
                null,
                null,
                IntPtr.Zero,
                null,
                null,
                null,
                null);

            if (result == false)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void SetLoadOrderGroup(string serviceName, string group)
        {
            using var service = OpenService(
                serviceName,
                ServiceAccessRights.SERVICE_CHANGE_CONFIG |
                ServiceAccessRights.SERVICE_QUERY_CONFIG
            );
            var result = NativeMethods.ChangeServiceConfig(
                service,
                SERVICE_NO_CHANGE,
                SERVICE_NO_CHANGE,
                SERVICE_NO_CHANGE,
                null,
                group,
                IntPtr.Zero,
                null,
                null,
                null,
                null);

            if (result == false)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>
        /// Registers a new Win32 service, LocalSystem account, automatic start. Mirrors what
        /// SimpleDeFenceServiceInstaller (System.Configuration.Install-based, net48-only) used to
        /// do: create the service, then set its load-order group to "NetworkProvider" - both are
        /// needed for the firewall service to start in the right order relative to networking.
        /// </summary>
        public void CreateService(string serviceName, string displayName, string binaryPath, string[] dependencies)
        {
            const uint SERVICE_AUTO_START = 0x00000002;
            const uint SERVICE_ERROR_NORMAL = 0x00000001;

            // CreateService expects a double-null-terminated multi-string for dependencies (each
            // entry separated by one embedded '\0', with an extra trailing '\0' so the automatic
            // terminator .NET's Unicode string marshaling appends becomes the second one). No
            // dependencies means a literal null pointer, not an empty string.
            string? dependenciesMultiString = dependencies.Length == 0
                ? null
                : string.Join("\0", dependencies) + "\0";

            // Quote the image path. The default install directory ("C:\Program Files\SimpleDeFence")
            // contains a space, and an unquoted ImagePath on a LocalSystem service is the classic
            // "unquoted service path" weakness (CWE-428). ServiceInstaller.Install(), which this
            // replaced, quoted it too. No caller ever passes an already-quoted path.
            string quotedBinaryPath = "\"" + binaryPath + "\"";

            using var service = NativeMethods.CreateService(
                SCManager,
                serviceName,
                displayName,
                ServiceAccessRights.SERVICE_ALL_ACCESS,
                ServiceType.SERVICE_TYPE_WIN32_OWN_PROCESS,
                SERVICE_AUTO_START,
                SERVICE_ERROR_NORMAL,
                quotedBinaryPath,
                null,
                IntPtr.Zero,
                dependenciesMultiString,
                null,
                null);

            if (service.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            SetLoadOrderGroup(serviceName, @"NetworkProvider");
        }

        public void DeleteService(string serviceName)
        {
            using var service = OpenService(serviceName, ServiceAccessRights.DELETE);

            if (!NativeMethods.DeleteService(service))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public uint GetStartupMode(string serviceName)
        {
            using var service = OpenService(serviceName, ServiceAccessRights.SERVICE_QUERY_CONFIG);

            var result = NativeMethods.QueryServiceConfig(service, IntPtr.Zero, 0, out uint structSize);
            using var buff = SafeHGlobalHandle.Alloc(structSize);

            result = NativeMethods.QueryServiceConfig(service, buff.DangerousGetHandle(), structSize, out structSize);
            if (result == false)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            QUERY_SERVICE_CONFIG query_srv_config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buff.DangerousGetHandle());
            return query_srv_config.dwStartType;
        }

        public uint? GetServicePid(string serviceName)
        {
            using var service = OpenService(serviceName, ServiceAccessRights.SERVICE_QUERY_STATUS);

            var result = NativeMethods.QueryServiceStatusEx(service, ServiceInfoLevel.SC_STATUS_PROCESS_INFO, IntPtr.Zero, 0, out uint structSize);
            using var buff = SafeHGlobalHandle.Alloc(structSize);

            result = NativeMethods.QueryServiceStatusEx(service, ServiceInfoLevel.SC_STATUS_PROCESS_INFO, buff.DangerousGetHandle(), structSize, out structSize);
            if (result == false)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            SERVICE_STATUS_PROCESS query_srv_status = Marshal.PtrToStructure<SERVICE_STATUS_PROCESS>(buff.DangerousGetHandle());

            return query_srv_status.dwCurrentState switch
            {
                ServiceState.Running or
                ServiceState.PausePending or
                ServiceState.Paused or
                ServiceState.ContinuePending => query_srv_status.dwProcessId,
                _ => null,
            };
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
            {
                // Release managed resources

                SCManager.Dispose();
            }

            // Release unmanaged resources.
            // Set large fields to null.
            // Call Dispose on your base class.

            disposed = true;
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
