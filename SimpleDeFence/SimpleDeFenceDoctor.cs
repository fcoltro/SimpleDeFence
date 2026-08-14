using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        internal static int Uninstall()
        {
            using (var frm = new System.Windows.Forms.Form())
            {
                // See http://www.codeproject.com/Articles/18612/TopMost-MessageBox
                // for an explanation as for why this is needed.
                frm.Size = new System.Drawing.Size(1, 1);
                frm.ShowInTaskbar = false;
                frm.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                System.Drawing.Rectangle rect = System.Windows.Forms.SystemInformation.VirtualScreen;
                frm.Location = new System.Drawing.Point(rect.Bottom + 10, rect.Right + 10);
                frm.Show();
                frm.Focus();
                frm.BringToFront(); 
                frm.TopMost = true;

                if (System.Windows.Forms.MessageBox.Show(frm,
                    Resources.Messages.DidYouInitiateTheUninstall,
                    Resources.Messages.SimpleDeFence,
                    System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Exclamation) != System.Windows.Forms.DialogResult.Yes)
                {
                    return -1;
                }
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
                        using var pf = new PasswordForm();
                        pf.BringToFront();
                        pf.Activate();
                        if (pf.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        {
                            twController.TryUnlockServer(pf.PassHash);
                        }
                        else
                            return -1;
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

        internal static void EnsureHealth(string logContext)
        {
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
