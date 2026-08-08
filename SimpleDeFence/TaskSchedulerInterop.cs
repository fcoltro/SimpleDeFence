using System;

namespace SimpleDeFence
{
    // Constants from the Task Scheduler 2.0 COM API (taskschd.h), previously supplied by the
    // TaskScheduler <COMReference>. See WindowsFirewallInterop.cs for why that reference had to
    // go and why the call sites bind late.

    internal static class TaskLogonType
    {
        public const int InteractiveToken = 3;
    }

    internal static class TaskRunLevel
    {
        public const int Highest = 1;
    }

    internal static class TaskCompatibility
    {
        public const int V2 = 2;
    }

    internal static class TaskInstancesPolicy
    {
        public const int Parallel = 0;
    }

    internal static class TaskTriggerType2
    {
        public const int Logon = 9;
    }

    internal static class TaskActionType
    {
        public const int Exec = 0;
    }

    internal static class TaskScheduler
    {
        /// <summary>Creates and connects a Task Scheduler service object.</summary>
        public static dynamic ConnectedService()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            dynamic svc = Activator.CreateInstance(t);
            svc.Connect();
            return svc;
        }
    }
}
