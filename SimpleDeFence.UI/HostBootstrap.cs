using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence.Localization;

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

        /// <summary>Hosts PasswordPromptDialog for PromptForPassword below. Derives from App for the
        /// same reason DevelToolApp does (see its comment above): a hand-written `: Application`
        /// subclass has none of the XAML-compiler-generated IXamlMetadataProvider plumbing, and
        /// crashes natively (0xc000027b) the moment any XAML content is constructed - which is
        /// exactly what this class does. Overriding OnLaunched without calling base means App's own
        /// OnLaunched (which builds the Controller GUI's MainWindow, tray icon and hotkeys) never
        /// runs; App's constructor still runs, but its only construction-time side effect is the
        /// `Firewall = new FirewallClient()` static initializer, which opens no pipe until an actual
        /// IPC call is made.
        ///
        /// A ContentDialog needs a XamlRoot, and a bare `new Window()` never gives it one - but
        /// giving the window Content is not enough either. XamlRoot is populated when the element is
        /// actually loaded into the window's content tree, which happens asynchronously: reading
        /// `window.Content.XamlRoot` on the line after `window.Activate()` still returns null, and
        /// ShowAsync() then throws ArgumentException("This element does not have a XamlRoot") -
        /// reproduced on this machine before this comment was written, which is why the dialog is
        /// created from the root Grid's Loaded handler instead of inline in OnLaunched.</summary>
        private sealed class PasswordPromptHostApp : App
        {
            public event Action<string?>? PasswordResolved;

            protected override void OnLaunched(LaunchActivatedEventArgs args)
            {
                // Same reason App.OnLaunched selects a culture before touching any XAML:
                // PasswordPromptDialog's buttons and placeholder are {loc:Loc} bindings resolved
                // during InitializeComponent. Without this the uninstall prompt would always be
                // English (Loc's static initializer defaults to "en"), losing the localization the
                // WinForms PasswordForm had via its satellite resources.
                SelectLanguage(ClientSettings.Load().Language);

                var window = new Window();
                var root = new Grid();
                // Unsubscribed on first fire: Loaded can raise again if the element is ever
                // re-attached, and a second ShowAsync would throw (only one ContentDialog may be
                // open at a time), which the catch below would report as a cancel.
                void OnRootLoaded(object sender, RoutedEventArgs e)
                {
                    root.Loaded -= OnRootLoaded;
                    _ = ShowAsync(window, new PasswordPromptDialog { XamlRoot = root.XamlRoot, FlowDirection = App.UiFlowDirection });
                }
                root.Loaded += OnRootLoaded;
                window.Content = root;
                window.Activate();
            }

            private async Task ShowAsync(Window window, PasswordPromptDialog dialog)
            {
                ContentDialogResult result;
                try
                {
                    result = await dialog.ShowAsync();
                }
                catch (Exception)
                {
                    // Nothing can observe this Task (it is deliberately fire-and-forget - Loaded
                    // handlers cannot be awaited), so an escaping exception would hang the caller
                    // on a window that never resolves. Treat any failure to show as a cancel.
                    PasswordResolved?.Invoke(null);
                    return;
                }

                // The host window is only scaffolding for the dialog's XamlRoot; hide it before
                // resolving so it does not flash empty while the caller gets on with the uninstall.
                window.Close();
                PasswordResolved?.Invoke(result == ContentDialogResult.Primary ? dialog.Password : null);
            }
        }

        /// <summary>Starts a throwaway WinUI Application solely to host PasswordPromptDialog, for
        /// callers with no already-running WinUI shell (SimpleDeFenceDoctor.Uninstall, run from
        /// Program.cs's Uninstall mode - a separate process launch from Controller mode, so there is
        /// no existing Window/XamlRoot to attach to). Returns null if the user cancels.</summary>
        public static string? PromptForPassword()
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            string? result = null;
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                var app = new PasswordPromptHostApp();
                app.PasswordResolved += pw => { result = pw; app.Exit(); };
            });
            return result;
        }
    }
}
