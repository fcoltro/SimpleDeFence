using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence.Localization;

namespace SimpleDeFence.UI.Services
{
    /// <summary>WinUI port of SimpleDeFence/UpdateChecker.cs's Updater class. Fixes a dormant bug
    /// in the port: the WinForms version's cancel path called Thread.Abort(), which throws
    /// PlatformNotSupportedException on net10 (never actually exercised there since it only
    /// triggers on cancel-during-check). This version uses a real CancellationToken instead.
    /// </summary>
    public static class Updater
    {
        public static async Task CheckForUpdatesAsync(XamlRoot xamlRoot)
        {
            using var cts = new CancellationTokenSource();

            var progressDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                FlowDirection = App.UiFlowDirection,
                Title = Loc.T(LocKeys.Settings.UpdatesCheckingTitle),
                Content = new ProgressRing { IsActive = true },
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
            };

            UpdateDescriptor descriptor;
            var checkTask = UpdateDescriptorFetcher.GetDescriptorAsync(cts.Token);

            _ = TryShowDialogAsync(progressDialog);
            var completed = await Task.WhenAny(checkTask, WaitForCloseAsync(progressDialog));
            if (completed != checkTask)
            {
                cts.Cancel();
                return; // User cancelled.
            }

            progressDialog.Hide();

            try
            {
                descriptor = await checkTask;
            }
            catch (Exception ex)
            {
                await ShowMessageAsync(xamlRoot, Loc.T(LocKeys.Settings.UpdatesCheckFailedTitle), ex.Message);
                return;
            }

            await CheckAppVersionAsync(xamlRoot, descriptor);
        }

        private static Task WaitForCloseAsync(ContentDialog dialog)
        {
            var tcs = new TaskCompletionSource();
            dialog.Closed += (_, _) => tcs.TrySetResult();
            return tcs.Task;
        }

        private static async Task CheckAppVersionAsync(XamlRoot xamlRoot, UpdateDescriptor descriptor)
        {
            var updateModule = descriptor.GetModule(UpdateDescriptor.MODULE_NAME_MAINBIN);
            var oldVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
            var newVersion = new Version(updateModule?.ComponentVersion ?? oldVersion.ToString());

            if (newVersion <= oldVersion)
            {
                await ShowMessageAsync(xamlRoot, Loc.T(LocKeys.Settings.UpdatesCheckNow), Loc.T(LocKeys.Settings.UpdatesNoneAvailable));
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = xamlRoot,
                FlowDirection = App.UiFlowDirection,
                Title = Loc.T(LocKeys.Settings.UpdatesCheckNow),
                Content = Loc.T(LocKeys.Settings.UpdatesAvailable, updateModule!.ComponentVersion),
                PrimaryButtonText = Loc.T(LocKeys.Common.Ok),
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
            };
            if (await TryShowDialogAsync(confirm) != ContentDialogResult.Primary)
                return;

            await DownloadAndInstallAsync(xamlRoot, updateModule);
        }

        private static async Task DownloadAndInstallAsync(XamlRoot xamlRoot, UpdateModule mainModule)
        {
            // Not Path.GetTempFileName() + ".msi": GetTempFileName creates a file and returns its
            // path, so appending an extension names a different one and orphans the first. This
            // path is deleted again on every route out of here except a successful launch, where
            // the installer process is reading from it.
            var tmpFile = Path.Combine(Path.GetTempPath(), $"SimpleDeFence-update-{Guid.NewGuid():N}.msi");
            using var cts = new CancellationTokenSource();
            using var httpClient = new HttpClient();

            var progressDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                FlowDirection = App.UiFlowDirection,
                Title = Loc.T(LocKeys.Settings.UpdatesDownloading),
                Content = new ProgressRing { IsActive = true },
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
            };

            var downloadTask = DownloadFileAsync(httpClient, mainModule.UpdateURL!, tmpFile, cts.Token);
            _ = TryShowDialogAsync(progressDialog);
            var completed = await Task.WhenAny(downloadTask, WaitForCloseAsync(progressDialog));
            if (completed != downloadTask)
            {
                cts.Cancel();
                TryDelete(tmpFile);
                return;
            }

            progressDialog.Hide();

            try
            {
                await downloadTask;
            }
            catch (Exception ex)
            {
                TryDelete(tmpFile);
                await ShowMessageAsync(xamlRoot, Loc.T(LocKeys.Settings.UpdatesCheckFailedTitle), ex.Message);
                return;
            }

            // Nothing downloaded from the network gets executed until it matches the checksum the
            // descriptor published. This used to StartProcess the file the moment it arrived,
            // without reading DownloadHash at all - while the service's own updater
            // (SimpleDeFenceService.GetCompressedUpdate) had always verified before installing.
            // The unattended path checked and the path that runs an installer for the user did not,
            // which was exactly the wrong way round.
            //
            // An absent hash is a refusal, not a pass: a descriptor that does not say what the
            // payload should be cannot authorise running it.
            if (!Hasher.FileMatchesHash(tmpFile, mainModule.DownloadHash))
            {
                TryDelete(tmpFile);
                await ShowMessageAsync(xamlRoot,
                    Loc.T(LocKeys.Settings.UpdatesCheckFailedTitle),
                    Loc.T(LocKeys.Settings.UpdatesVerificationFailed));
                return;
            }

            StartProcess(tmpFile, string.Empty, false, false);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best effort; %TEMP% is swept by the OS */ }
        }

        private static async Task DownloadFileAsync(HttpClient client, string url, string destination, CancellationToken ct)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var dest = File.Create(destination);
            await source.CopyToAsync(dest, ct);
        }

        private static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
        {
            var dialog = new ContentDialog { XamlRoot = xamlRoot, FlowDirection = App.UiFlowDirection, Title = title, Content = message, CloseButtonText = Loc.T(LocKeys.Common.Ok) };
            await TryShowDialogAsync(dialog);
        }

        /// <summary>Per this plan's Global Constraints: WinUI allows only one open ContentDialog per
        /// XamlRoot at a time; a second ShowAsync() while one is already open throws
        /// InvalidOperationException. Matches RulesPage.xaml.cs's TryShowDialogAsync pattern.</summary>
        private static async Task<ContentDialogResult> TryShowDialogAsync(ContentDialog dialog)
        {
            try
            {
                return await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                return ContentDialogResult.None;
            }
        }

        /// <summary>Straight copy of SimpleDeFence.Utils.StartProcess's body - that internal static
        /// class lives only in the WinForms SimpleDeFence project and SimpleDeFence.UI has no
        /// reference path to it (direct or transitive), same reachability problem
        /// DevelToolWindow.xaml.cs's own copy of this method (and of CompressDeflate) already hit
        /// and solved the same way in Task 5 of this plan.</summary>
        private static Process StartProcess(string path, string args, bool asAdmin, bool hideWindow = false)
        {
            var psi = new ProcessStartInfo(path, args) { WorkingDirectory = Path.GetDirectoryName(path) };
            if (asAdmin)
            {
                psi.Verb = "runas";
                psi.UseShellExecute = true;
            }
            if (hideWindow)
                psi.WindowStyle = ProcessWindowStyle.Hidden;

            return Process.Start(psi)!;
        }
    }
}
