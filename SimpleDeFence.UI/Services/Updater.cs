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
                RequestedTheme = App.UiElementTheme,
                Title = Loc.T(LocKeys.Settings.UpdatesCheckingTitle),
                Content = new ProgressRing { IsActive = true },
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
            };

            UpdateDescriptor descriptor;
            var checkTask = UpdateDescriptorFetcher.GetDescriptorAsync(cts.Token);

            var progressClosed = WaitForCloseAsync(progressDialog);
            _ = TryShowDialogAsync(progressDialog);
            var completed = await Task.WhenAny(checkTask, progressClosed);
            if (completed != checkTask)
            {
                cts.Cancel();
                return; // User cancelled.
            }

            // Hide() only starts the close. Showing the next dialog before this one has finished
            // closing throws, which TryShowDialogAsync turns into "None" - so the result or error
            // that follows was silently dropped.
            progressDialog.Hide();
            await progressClosed;

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

            // TryParse, not new Version(...). ComponentVersion arrives over the network - the
            // descriptor is fetched from raw.githubusercontent.com - and new Version throws on
            // anything that is not strictly numeric: an empty string, a typo, or a perfectly
            // reasonable-looking "0.1.1-beta". This parse sits outside the try/catch that guards
            // the fetch above, so a malformed descriptor did not degrade the update check, it threw
            // out of it. Treating unparseable as "nothing on offer" fails the way the rest of this
            // path already fails: quietly, leaving the user on the build they have.
            //
            // The three ways there is nothing to offer are one branch: no module published for
            // this architecture, a version that cannot be read, and a version no newer than what
            // is already installed. Written this way the compiler can also see that updateModule
            // is not null past this point, which is what actually guarantees the download below
            // has something to download - rather than a "!" asserting it.
            if (updateModule is null
                || !Version.TryParse(updateModule.ComponentVersion, out var newVersion)
                || newVersion <= oldVersion)
            {
                await ShowMessageAsync(xamlRoot, Loc.T(LocKeys.Settings.UpdatesCheckNow), Loc.T(LocKeys.Settings.UpdatesNoneAvailable));
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = xamlRoot,
                FlowDirection = App.UiFlowDirection,
                RequestedTheme = App.UiElementTheme,
                Title = Loc.T(LocKeys.Settings.UpdatesCheckNow),
                // newVersion, not the raw ComponentVersion string: this is the value that was
                // actually parsed and compared, so the number offered to the user cannot differ
                // from the one the decision was made on - and it is non-null, which the descriptor
                // field is not.
                Content = Loc.T(LocKeys.Settings.UpdatesAvailable, newVersion.ToString()),
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
                RequestedTheme = App.UiElementTheme,
                Title = Loc.T(LocKeys.Settings.UpdatesDownloading),
                Content = new ProgressRing { IsActive = true },
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
            };

            if (!UpdateUrlPolicy.IsAllowed(mainModule.UpdateURL))
            {
                await ShowMessageAsync(xamlRoot,
                    Loc.T(LocKeys.Settings.UpdatesCheckFailedTitle),
                    Loc.T(LocKeys.Settings.UpdatesVerificationFailed));
                return;
            }

            var downloadTask = DownloadFileAsync(httpClient, mainModule.UpdateURL!, tmpFile, cts.Token);
            var progressClosed = WaitForCloseAsync(progressDialog);
            _ = TryShowDialogAsync(progressDialog);
            var completed = await Task.WhenAny(downloadTask, progressClosed);
            if (completed != downloadTask)
            {
                cts.Cancel();

                // The download still holds the file open until it observes the cancellation, and
                // deleting before then fails silently and leaves a partial MSI in %TEMP%.
                try { await downloadTask; } catch { }
                TryDelete(tmpFile);
                return;
            }

            progressDialog.Hide();
            await progressClosed;

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
            // Verified and launched through ONE open handle, held across both. Hashing by path and
            // then starting by path checks one set of bytes and runs whatever is at that name a
            // moment later: any process running as this user can swap the file in between, and
            // since installing an MSI raises a UAC prompt the user is already expecting, that turns
            // same-user code execution into administrator. FileShare.Read lets the installer read
            // the file while denying every writer and - the part that closes the hole - every
            // delete, which is what a replace needs.
            //
            // The handle is deliberately not disposed on the success path. There is no moment at
            // which it is safe to let go: the installer opens the file some time after the user
            // accepts the elevation prompt, and releasing the lock before then reopens the window
            // this exists to close. One handle held until the process exits is the cost, and this
            // process is about to be replaced by the very installer it just launched.
            FileStream? verified = null;
            try
            {
                verified = new FileStream(tmpFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex)
            {
                TryDelete(tmpFile);
                await ShowMessageAsync(xamlRoot, Loc.T(LocKeys.Settings.UpdatesCheckFailedTitle), ex.Message);
                return;
            }

            if (!Hasher.StreamMatchesHash(verified, mainModule.DownloadHash))
            {
                verified.Dispose();
                TryDelete(tmpFile);
                await ShowMessageAsync(xamlRoot,
                    Loc.T(LocKeys.Settings.UpdatesCheckFailedTitle),
                    Loc.T(LocKeys.Settings.UpdatesVerificationFailed));
                return;
            }

            // Authenticode, checked while the handle above still pins the bytes. The hash only
            // proves the file is the one the descriptor names, and the descriptor is unsigned, so
            // a signature is the only thing here that says who built it. A signature that is
            // present but does not verify is always refused - that is a tampered or re-signed
            // file. An unsigned package is accepted only while releases are not signed yet; flip
            // RequireSignedInstaller once they are.
            var signature = SimpleDeFence.Windows.WinTrust.VerifyFileAuthenticode(tmpFile);
            if (signature == SimpleDeFence.Windows.WinTrust.VerifyResult.SIGNATURE_INVALID
                || (RequireSignedInstaller && signature != SimpleDeFence.Windows.WinTrust.VerifyResult.SIGNATURE_VALID))
            {
                verified.Dispose();
                TryDelete(tmpFile);
                await ShowMessageAsync(xamlRoot,
                    Loc.T(LocKeys.Settings.UpdatesCheckFailedTitle),
                    Loc.T(LocKeys.Settings.UpdatesVerificationFailed));
                return;
            }

            try
            {
                StartInstaller(tmpFile);

                // Rooted for the life of the process. A local would be eligible for collection the
                // moment this method returns, and the finalizer closing it would release the lock
                // while msiexec - which opens the file by path, after the user answers its UAC
                // prompt - has not read it yet.
                HeldInstallerHandle = verified;
            }
            catch (Exception ex)
            {
                verified.Dispose();
                TryDelete(tmpFile);
                await ShowMessageAsync(xamlRoot, Loc.T(LocKeys.Settings.UpdatesCheckFailedTitle), ex.Message);
            }
        }

        /// <summary>True once releases are Authenticode-signed. While false, an unsigned installer
        /// is accepted (one with a broken signature never is).</summary>
        private static readonly bool RequireSignedInstaller = false;

        /// <summary>Keeps the verified installer locked against writers and deletes until this
        /// process exits. See the end of <see cref="DownloadAndInstallAsync"/>.</summary>
        private static FileStream? HeldInstallerHandle;

        /// <summary>
        /// Runs the downloaded package through msiexec explicitly.
        ///
        /// This used to be StartProcess(tmpFile, ...) with asAdmin false, which leaves
        /// UseShellExecute at its .NET default of false - and Process.Start with UseShellExecute
        /// false needs an executable image. An .msi is a document, so that call throws
        /// Win32Exception rather than installing anything, and the exception escaped into an async
        /// void caller.
        ///
        /// Naming msiexec under SystemRoot rather than setting UseShellExecute true also takes the
        /// .msi file association out of the picture. That association is per-user and writable by
        /// the user, so a shell-executed launch of a file we just went to the trouble of hashing
        /// would hand control to whatever HKCU says .msi means. msiexec raises its own elevation
        /// prompt, so nothing is lost by not asking for one here.
        /// </summary>
        private static void StartInstaller(string msiPath)
        {
            var msiexec = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

            Process.Start(new ProcessStartInfo(msiexec)
            {
                ArgumentList = { "/i", msiPath },
                UseShellExecute = false,
            })!.Dispose();
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
            var dialog = new ContentDialog { XamlRoot = xamlRoot, FlowDirection = App.UiFlowDirection, RequestedTheme = App.UiElementTheme, Title = title, Content = message, CloseButtonText = Loc.T(LocKeys.Common.Ok) };
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
