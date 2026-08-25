using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using SimpleDeFence.Windows;
using SimpleDeFence.Windows.Services;
using SimpleDeFence.Windows.WFP;
using SimpleDeFence.Windows.WFP.Interop;

namespace SimpleDeFence
{
    internal static class SimpleDeFenceDoctor
    {
        private static readonly string CONTROLLER_START_TASKSCH_NAME = "SimpleDeFence Controller";

        internal static bool IsServiceRunning(string logContext, bool installing)
        {
#if !DEBUG
            try
            {
                using var sc = new ServiceController(SimpleDeFenceService.SERVICE_NAME);
                return (sc.Status == ServiceControllerStatus.Running) || (sc.Status == ServiceControllerStatus.StartPending);
            }
            catch(Exception e)
            {
                if (!installing) Utils.LogException(e, logContext);
                return false;
            }
#else
            return true;
#endif
        }

        internal static bool IsServiceStopped()
        {
#if !DEBUG
            try
            {
                using var sc = new ServiceController(SimpleDeFenceService.SERVICE_NAME);
                return (sc.Status == ServiceControllerStatus.Stopped);
            }
            catch
            {
                return false;
            }
#else
            return true;
#endif
        }

        internal static bool EnsureServiceInstalledAndRunning(string logContext, bool installing)
        {
            if (SimpleDeFenceDoctor.IsServiceRunning(logContext, installing))
                return true;

            if (Utils.RunningAsAdmin())
            {
                // Run installers
                try
                {
                    // The only place that needs SC_MANAGER_CREATE_SERVICE. It is denied to
                    // non-elevated processes, but we are inside the RunningAsAdmin() branch here.
                    using var scm = new ServiceControlManager(ServiceControlAccessRights.SC_MANAGER_CREATE_SERVICE);
                    scm.CreateService(SimpleDeFenceService.SERVICE_NAME, SimpleDeFenceService.SERVICE_DISPLAY_NAME, Utils.ExecutablePath, SimpleDeFenceService.ServiceDependencies);
                }
                catch(Exception e)
                {
                    Utils.LogException(e, logContext);
                }

                // Ensure dependencies
                SimpleDeFenceDoctor.EnsureHealth(logContext);

                // Start service
                try
                {
                    using var sc = new ServiceController(SimpleDeFenceService.SERVICE_NAME);
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, System.TimeSpan.FromSeconds(15));
                    }
                }
                catch (Exception e)
                {
                    Utils.LogException(e, logContext);
                    return false;
                }
            }
            else
            {
                // We are not running as admin.
                try
                {
                    using Process p = Utils.StartProcess(Utils.ExecutablePath, "/install", true);
                    p.WaitForExit();
                    return (p.ExitCode == 0);
                }
                catch (Exception e)
                {
                    Utils.LogException(e, logContext);
                    return false;
                }
            }

            return true;
        }

        /// <summary>True when this process has a desktop a human could actually answer a modal on.
        /// Deferred MSI custom actions run as LocalSystem in session 0, where Session 0 Isolation
        /// puts any window we open on a desktop nobody can see.</summary>
        private static bool HasInteractiveDesktop()
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                return self.SessionId != 0;
            }
            catch (Exception e)
            {
                // Cannot tell - keep asking rather than tear the firewall down unprompted.
                Utils.LogException(e, Utils.LOG_ID_INSTALLER);
                return true;
            }
        }

        internal static int Uninstall()
        {
            // Everything below that puts something on screen first has to know whether there is a
            // screen. Product.wxs runs UninstallCustom - and InstallCustomRollback - as deferred,
            // non-impersonated actions, so we are LocalSystem in session 0: Session 0 Isolation
            // draws any modal we open on a desktop no user can reach, and because those actions are
            // Return='check', msiexec then waits on us forever. That is a hung Add/Remove Programs
            // uninstall, and killing the process makes Windows Installer roll the whole thing back.
            //
            // Arriving here without an interactive desktop already implies consent - msiexec only
            // runs the action after an elevated, user-initiated uninstall - so the confirmation is
            // skipped there rather than asked into a void.
            bool interactive = HasInteractiveDesktop();

            if (interactive)
            {
                // MessageBoxW directly rather than WinForms. This prompt runs under /uninstall, where
                // there is no WinUI window to parent a ContentDialog to and no message loop, so a
                // native modal is the right shape - and it was the only thing left pulling
                // System.Windows.Forms into this executable.
                //
                // The invisible owner Form this used to create existed purely to make the box topmost
                // (per the CodeProject article it cited); MB_TOPMOST and MB_SETFOREGROUND say that
                // directly, which is what the whole dance was emulating.
                const uint MB_YESNO = 0x00000004;
                const uint MB_ICONEXCLAMATION = 0x00000030;
                const uint MB_SETFOREGROUND = 0x00010000;
                const uint MB_TOPMOST = 0x00040000;
                const int IDYES = 6;

                int answer = Utils.SafeNativeMethods.MessageBoxW(
                    IntPtr.Zero,
                    Resources.Messages.DidYouInitiateTheUninstall,
                    Resources.Messages.SimpleDeFence,
                    MB_YESNO | MB_ICONEXCLAMATION | MB_SETFOREGROUND | MB_TOPMOST);

                if (answer != IDYES)
                    return -1;
            }
            else
            {
                Utils.Log("No interactive desktop (session 0); proceeding without the uninstall confirmation.", Utils.LOG_ID_INSTALLER);
            }

            // Stop service
            try
            {
                if (SimpleDeFenceDoctor.IsServiceRunning(Utils.LOG_ID_INSTALLER, false))
                {
                    var twController = new Controller("SimpleDeFenceController");

                    // Unlock server
                    while (twController.IsServerLocked)
                    {
                        if (!interactive)
                        {
                            // PromptForPassword starts a WinUI Application, which in session 0 is the
                            // same trap as the confirmation above. Refusing beats hanging: an unattended
                            // uninstall of a locked server should fail, and say why.
                            Utils.Log("Server is password-locked and there is no interactive desktop to ask on; aborting uninstall. Unlock SimpleDeFence first, then uninstall.", Utils.LOG_ID_INSTALLER);
                            return -1;
                        }

                        string? password = SimpleDeFence.UI.HostBootstrap.PromptForPassword();
                        if (password is null)
                            return -1;

                        twController.TryUnlockServer(password);
                    }

                    // Stop server
                    twController.RequestServerStop();
                    DateTime startTs = DateTime.Now;
                    while (!IsServiceStopped() && ((DateTime.Now - startTs) < TimeSpan.FromSeconds(15)))
                        System.Threading.Thread.Sleep(200);
                    if (!IsServiceStopped())
                    {
                        Utils.Log("Failed to stop service during uninstall.", Utils.LOG_ID_INSTALLER);
                        return -1;
                    }
                }
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_INSTALLER);
                return -1;
            }

            // Terminate remaining SimpleDeFence processes (e.g. controller)
            {
                using var ownProc = Process.GetCurrentProcess();
                int ownPid = ownProc.Id;
                Process[] procs = Process.GetProcesses();
                try
                {
                    foreach (Process p in procs)
                    {
                        try
                        {
                            if (p.ProcessName.Contains("SimpleDeFence") && (p.Id != ownPid))
                            {
                                ProcessManager.TerminateProcess(p, 2000);
                            }
                        }
                        catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_INSTALLER); }
                    }
                }
                finally
                {
                    foreach (var p in procs)
                        p.Dispose();
                }
            }

            try
            {
                // Remove persistent WFP objects
                using var WfpEngine = new Engine("SimpleDeFence Uninstall Session", "", FWPM_SESSION_FLAGS.None, 5000);
                using var trx = WfpEngine.BeginTransaction();
                SimpleDeFenceServer.DeleteWfpObjects(WfpEngine, true);
                trx.Commit();
            }
            catch (Exception e)
            {
                Utils.LogException(e, Utils.LOG_ID_INSTALLER);
                return -1;
            }


            try
            {
                // Disable automatic start of controller
                dynamic taskService = TaskScheduler.ConnectedService();
                taskService.GetFolder(@"\").DeleteTask(CONTROLLER_START_TASKSCH_NAME, 0);
            }
            catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_INSTALLER); }

            try
            {
                // Put back the user's original hosts file
                using HostsFileManager hosts = new();
                hosts.DisableHostsFile();
            }
            catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_INSTALLER); }

            try
            {
                using var scm = new ServiceControlManager();
                scm.DeleteService(SimpleDeFenceService.SERVICE_NAME);
            }
            catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_INSTALLER); }

            return 0;
        }

        /// <summary>
        /// Locks down the service's data directory.
        ///
        /// It sits under ProgramData and was created by plain Directory.CreateDirectory, so it
        /// inherited that folder's default rights: Users may create files, and CREATOR OWNER hands
        /// whoever creates one full control of it. Existing files written by the service are safe -
        /// Users only have read on those - but several of the files that matter are written lazily,
        /// long after install: the config, its key, and hosts.orig. A standard user who creates one
        /// of those first owns it.
        ///
        /// That is not theoretical for two of them. hosts.orig is copied over the system hosts file
        /// when the blocklist is switched off, so owning it is a machine-wide DNS redirect. And
        /// machine-scope DPAPI is available to every user, so a user can write a validly wrapped
        /// config.key and then a config authenticated under it - which the service would load as
        /// genuine.
        ///
        /// Run on every service start rather than only at install, so an installation that has
        /// already been tampered with is repaired rather than merely not made worse.
        /// </summary>
        internal static void HardenAppDataDirectory(string logContext)
        {
            try
            {
                var root = new DirectoryInfo(Utils.AppDataPath);
                if (!root.Exists)
                    return;

                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                const InheritanceFlags Inherit = InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;

                var acl = new DirectorySecurity();
                // true, false: stop inheriting, and do not keep a copy of what was inherited - the
                // Users create rights and the CREATOR OWNER grant are exactly what must not survive.
                acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
                root.SetAccessControl(acl);

                // The new inherited set does not displace an explicit entry already on a child, and
                // an explicit full-control entry is exactly what a pre-created file carries. Strip
                // them so the directory's rules are the only ones in play.
                foreach (var child in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                    ClearExplicitAccessRules(child, logContext);

                // One exception, carved as narrowly as it can be: the tray icon and GUI run as the
                // signed-in user and write their log here. The directory is created by the service
                // so it cannot be replaced by a junction, and write access to it buys an attacker
                // nothing better than noisy log files.
                var logs = new DirectoryInfo(Path.Combine(root.FullName, "logs"));
                if (!logs.Exists)
                    logs.Create();

                var logAcl = logs.GetAccessControl();
                logAcl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, Inherit, PropagationFlags.None, AccessControlType.Allow));
                logs.SetAccessControl(logAcl);
            }
            catch (Exception e)
            {
                Utils.Log("Could not apply the intended permissions to the data directory. For details see the next log entry.", logContext);
                Utils.LogException(e, logContext);
            }
        }

        /// <summary>Removes every non-inherited rule from one file or directory, so that what the
        /// parent grants is all that applies to it.</summary>
        private static void ClearExplicitAccessRules(FileSystemInfo item, string logContext)
        {
            try
            {
                if (item is DirectoryInfo dir)
                {
                    var acl = dir.GetAccessControl();
                    if (!RemoveExplicitRules(acl))
                        return;
                    dir.SetAccessControl(acl);
                }
                else if (item is FileInfo file)
                {
                    var acl = file.GetAccessControl();
                    if (!RemoveExplicitRules(acl))
                        return;
                    file.SetAccessControl(acl);
                }
            }
            catch (Exception e)
            {
                // One unreadable child must not stop the rest of the directory being repaired.
                Utils.Log($"Could not reset permissions on \"{item.FullName}\": {e.Message}", logContext);
            }
        }

        private static bool RemoveExplicitRules(FileSystemSecurity acl)
        {
            acl.SetAccessRuleProtection(false, false);

            bool changed = false;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                acl.RemoveAccessRuleSpecific(rule);
                changed = true;
            }

            return changed;
        }

        internal static void EnsureHealth(string logContext)
        {
            // Before anything else: the files every other check depends on are only as trustworthy
            // as the directory holding them.
            HardenAppDataDirectory(logContext);

            // Ensure that SimpleDeFence's dependencies can be started
            try
            {
                EnsureServiceDependencies();
            }
            catch (InvalidOperationException e)
            {
                if (!Utils.IsSystemShuttingDown())
                    Utils.LogException(e, logContext);
            }
            catch (Exception e)
            {
                Utils.LogException(e, logContext);
            }

            // Ensure that SimpleDeFence itself can be started
            try
            {
                using var scm = new ServiceControlManager();
                scm.SetStartupMode(SimpleDeFenceService.SERVICE_NAME, ServiceStartMode.Automatic);
                scm.SetRestartOnFailure(SimpleDeFenceService.SERVICE_NAME, true);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                const int E_FAIL = -2147467259;
                if (!(Utils.IsSystemShuttingDown() && (e.ErrorCode == E_FAIL)))
                    Utils.LogException(e, logContext);
            }
            catch (Exception e)
            {
                Utils.LogException(e, logContext);
            }

            // Ensure that controller will be started for users
            try
            {
                const string INTERACTIVE_GROUP_SID = "S-1-5-4";
                const int TASK_CREATE_OR_UPDATE = 6;
                dynamic taskService = TaskScheduler.ConnectedService();
                dynamic td = taskService.NewTask(0);
                td.RegistrationInfo.Author = "SimpleDeFence, Károly Pados";
                td.RegistrationInfo.Description = "This task starts the SimpleDeFence tray icon when a user is logged in.";
                td.Settings.Enabled = true;
                td.Principal.GroupId = INTERACTIVE_GROUP_SID;
                td.Principal.LogonType = TaskLogonType.InteractiveToken;
                td.Principal.RunLevel = TaskRunLevel.Highest;
                td.Settings.Compatibility = TaskCompatibility.V2;
                td.Settings.Enabled = true;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.Hidden = false;
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.ExecutionTimeLimit = "PT0S";
                td.Settings.MultipleInstances = TaskInstancesPolicy.Parallel;
                td.Triggers.Create(TaskTriggerType2.Logon);
                dynamic act = td.Actions.Create(TaskActionType.Exec);
                act.Path = Utils.ExecutablePath;
                // RegisterTaskDefinition takes a trailing optional sddl parameter; late binding
                // does not fill optionals in, so it is passed explicitly.
                taskService.GetFolder(@"\").RegisterTaskDefinition(CONTROLLER_START_TASKSCH_NAME, td, TASK_CREATE_OR_UPDATE, null, null, TaskLogonType.InteractiveToken, null);
            }
            catch (System.Runtime.InteropServices.COMException e)
            {
                if (!Utils.IsSystemShuttingDown())
                    Utils.LogException(e, logContext);
            }
            catch (Exception e)
            {
                Utils.LogException(e, logContext);
            }
        }

        private static void EnsureServiceDependencies()
        {
            // First, do a recursive scan of all service dependencies
            var deps = new HashSet<string>();
            foreach (var srv in SimpleDeFenceService.ServiceDependencies)
            {
                using var sc = new ServiceController(srv);
                ScanServiceDependencies(sc, deps);
            }

            // Enable services we need
            using var scm = new ServiceControlManager();
            foreach (string srv in deps)
            {
                if (scm.GetStartupMode(srv) == (uint)ServiceStartMode.Disabled)
                    scm.SetStartupMode(srv, ServiceStartMode.Manual);
            }
        }

        private static void ScanServiceDependencies(ServiceController srv, HashSet<string> allDeps)
        {
            if (allDeps.Contains(srv.ServiceName))
                return;

            allDeps.Add(srv.ServiceName);

            ServiceController[] ServicesDependedOn = srv.ServicesDependedOn;
            foreach (ServiceController depOn in ServicesDependedOn)
            {
                ScanServiceDependencies(depOn, allDeps);
            }
        }
    }
}
