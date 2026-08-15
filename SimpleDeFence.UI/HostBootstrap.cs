using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SimpleDeFence.UI
{
    /// <summary>
    /// Entry points for hosting SimpleDeFence.UI's WinUI 3 app from a process whose own Main isn't
    /// itself UseWinUI - specifically SimpleDeFence.exe (see the net10 exe-merge design doc). This
    /// mirrors what the WindowsAppSDK SDK would auto-generate as Main if this project had none of
    /// its own - SimpleDeFence.UI.csproj still gets that auto-generated Main for its own standalone
    /// --sample-data launch; this is the same bootstrap, callable from elsewhere.
    /// </summary>
    public static class HostBootstrap
    {
        public static void RunAsControllerGui(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
    }
}
