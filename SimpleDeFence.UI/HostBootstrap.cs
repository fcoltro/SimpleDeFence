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

        /// <summary>Hosts DevelToolWindow under an Application subclass of its own, rather than
        /// directly constructing DevelToolWindow inside RunAsDevelTool's Application.Start callback the
        /// way the task brief's original sample did. That sample crashed hard on launch - reproduced
        /// via SimpleDeFence.exe /develtool: an access violation deep inside Microsoft.UI.Xaml.dll
        /// (exception code 0xc000027b), before OnLaunched ever ran. Root-caused by elimination (three
        /// launches, each changing one variable):
        ///   1. Original brief code (no Application subclass at all, DevelToolWindow constructed
        ///      directly in the Start callback) - crashed.
        ///   2. A hand-written, code-only `: Application` subclass (no XAML file/InitializeComponent of
        ///      its own), merging XamlControlsResources in code and constructing DevelToolWindow in
        ///      OnLaunched - still crashed, identical fault offset.
        ///   3. Same code-only subclass, OnLaunched doing nothing but writing a marker file - the
        ///      marker was never written, so the crash happens constructing the Application subclass
        ///      itself, before OnLaunched fires at all - it is not about DevelToolWindow's XAML,
        ///      TabView, or missing resources specifically.
        /// WinUI 3's XAML compiler generates IXamlMetadataProvider plumbing (GetXamlType etc.) into the
        /// x:Class partial class from its .xaml file; RunAsControllerGui's `_ = new App();` (which
        /// works - confirmed by this same manual verification pass, MainWindow renders its full content
        /// tree) goes through that generated code via App.xaml's InitializeComponent(). A hand-written
        /// Application subclass with no matching .xaml has none of that, and apparently can't safely
        /// construct or load any XAML content in this WindowsAppSDK version - hence the native crash.
        /// Deriving from App itself instead (rather than Application directly) inherits App's working
        /// XAML-backed InitializeComponent()/metadata-provider plumbing for free via normal C# subclass
        /// construction (App's constructor still runs first), while overriding OnLaunched here means
        /// App's own OnLaunched (which unconditionally builds the Controller GUI's MainWindow) never
        /// runs. This still touches nothing in App.xaml/App.xaml.cs - both stay untouched, out of this
        /// task's file-scope as specified.</summary>
        private sealed class DevelToolApp : App
        {
            protected override void OnLaunched(LaunchActivatedEventArgs args)
            {
                var window = new DevelToolWindow();
                window.Activate();
                _ = window.ShowStartupWarningAsync();
            }
        }

        public static void RunAsDevelTool(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new DevelToolApp();
            });
        }
    }
}
