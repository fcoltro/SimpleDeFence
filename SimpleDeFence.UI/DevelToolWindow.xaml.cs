using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SimpleDeFence.Utilities;
using System.Linq;
using System.Resources;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence.DatabaseClasses;

namespace SimpleDeFence.UI
{
    /// <summary>Internal, never-user-facing batch/build tool - WinUI port of
    /// SimpleDeFence/DevelToolForm.cs. Every handler calls the same backend classes the WinForms
    /// version did; only the shell (Window+TabView instead of Form) and pickers/dialogs changed.
    /// </summary>
    public sealed partial class DevelToolWindow : Window
    {
        private static readonly string[] SIGNING_FILE_PATTERNS = { "*.dll", "*.exe", "*.msi" };
        private readonly List<KeyValuePair<string, string[]>> _resXInputs = new();

        public DevelToolWindow()
        {
            InitializeComponent();
        }

        /// <summary>Called right after Activate() (see HostBootstrap.RunAsDevelTool). Content.XamlRoot
        /// is still null at that exact point - Activate() only requests that the window be shown, it
        /// does not block for the first layout pass that actually connects the visual tree - so
        /// reading Content.XamlRoot synchronously here (as the task brief's original sample did) hands
        /// ContentDialog a null XamlRoot; ShowAsync() then fails, and because the caller awaits this
        /// with a discarded `_ = ...` (there is nothing else to await it before the window is up), the
        /// failure is a silently unobserved Task, not a crash - reproduced by launching
        /// SimpleDeFence.exe /develtool and finding no ContentDialog anywhere in the UI Automation tree
        /// even seconds after startup, window otherwise rendering fine. Awaiting the root element's own
        /// Loaded event first (which fires once the visual tree is actually connected and XamlRoot is
        /// valid) is the same signal RulesPage's constructor uses to defer its own first load past
        /// construction.</summary>
        public async System.Threading.Tasks.Task ShowStartupWarningAsync()
        {
            if (Content is FrameworkElement { XamlRoot: null } root)
            {
                var loaded = new System.Threading.Tasks.TaskCompletionSource();
                void OnLoaded(object sender, RoutedEventArgs e)
                {
                    root.Loaded -= OnLoaded;
                    loaded.TrySetResult();
                }
                root.Loaded += OnLoaded;
                await loaded.Task;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Warning: Not for users!",
                Content = "This tool is not meant for end-users. Only use this tool when instructed to do so by the application developer.",
                CloseButtonText = "OK",
            };
            await TryShowDialogAsync(dialog);
        }

        private async System.Threading.Tasks.Task<string?> PickFileAsync(string extensionFilter = "*")
        {
            var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(extensionFilter);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }

        private async System.Threading.Tasks.Task<string?> PickFolderAsync()
        {
            var picker = new global::Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }

        private async System.Threading.Tasks.Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = title, Content = message, CloseButtonText = "OK" };
            await TryShowDialogAsync(dialog);
        }

        /// <summary>Per this plan's Global Constraints: WinUI allows only one open ContentDialog per
        /// XamlRoot at a time; a second ShowAsync() while one is already open throws
        /// InvalidOperationException. Matches RulesPage.xaml.cs's TryShowDialogAsync pattern.</summary>
        private async System.Threading.Tasks.Task<ContentDialogResult> TryShowDialogAsync(ContentDialog dialog)
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

        private async void AssocBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickFileAsync();
            if (path is not null)
                AssocExePath.Text = path;
        }

        private async void AssocCreate_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(AssocExePath.Text))
            {
                var exe = new ExecutableSubject(AssocExePath.Text);
                var id = new DatabaseClasses.SubjectIdentity(exe) { AllowedSha1 = new List<string> { exe.HashSha1 } };
                if (exe.IsSigned && exe.CertValid)
                {
                    id.CertificateSubjects = new List<string>();
                    if (exe.CertSubject is not null)
                        id.CertificateSubjects.Add(exe.CertSubject);
                }
                var utf8bytes = SerializationHelper.Serialize(id);
                AssocResult.Text = Encoding.UTF8.GetString(utf8bytes);
            }
            else
            {
                await ShowMessageAsync("File not found", "No such file.");
            }
        }

        private async void ProfileFolderBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickFolderAsync();
            if (path is not null)
                DbFolderPath.Text = path;
        }

        private async void AssocOutputBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickFolderAsync();
            if (path is not null)
                AssocOutputPath.Text = path;
        }

        private async void CollectionsCreate_Click(object sender, RoutedEventArgs e)
        {
            string outputPath = Path.Combine(AssocOutputPath.Text, "profiles.json");
            string inputPath = DbFolderPath.Text;
            if (!Directory.Exists(inputPath))
            {
                await ShowMessageAsync("Directory not found", "Input database folder not found.");
                return;
            }

            var defAppInst = new DatabaseClasses.Application();
            var files = Directory.GetFiles(inputPath, "*.json", SearchOption.AllDirectories);
            var db = new AppDatabase();
            foreach (string fpath in files)
            {
                if (fpath.Equals(outputPath, StringComparison.CurrentCultureIgnoreCase))
                    continue;

                try
                {
                    var loadedAppInst = SerializationHelper.DeserializeFromFile(fpath, defAppInst);
                    if (string.IsNullOrEmpty(loadedAppInst.Name))
                    {
                        await ShowMessageAsync("Error", $"No app name provided in profile:\n{fpath}.\n\nProfile creation aborted.");
                        return;
                    }
                    db.KnownApplications.Add(loadedAppInst);
                }
                catch
                {
                    await ShowMessageAsync("Error", $"Unloadable profile:\n{fpath}.\n\nProfile creation aborted.");
                    return;
                }
            }

            db.Save(outputPath);
            await ShowMessageAsync("Success", "Creation of collections finished.");
        }

        private async void UpdateInstallerBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickFileAsync();
            if (path is not null)
                UpdateInstallerProjectDir.Text = path;
        }

        private async void UpdateOutputBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickFolderAsync();
            if (path is not null)
                UpdateOutput.Text = path;
        }

        private async void UpdateCreate_Click(object sender, RoutedEventArgs e)
        {
            const string DB_OUT_NAME = "database.def";
            const string HOSTS_OUT_NAME = "hosts.def";
            const string DESCRIPTOR_NAME = "update.json";
            const string DESCRIPTOR_TEMPLATE_NAME = "update_template.json";
            const string MSI_FILENAME_X86 = "SimpleDeFence_x86.msi";
            const string MSI_FILENAME_ARM64 = "SimpleDeFence_arm64.msi";

            string projectDir = UpdateInstallerProjectDir.Text;
            string msiX86Path = Path.Combine(projectDir, @"bin\Release\" + MSI_FILENAME_X86);
            string msiArm64Path = Path.Combine(projectDir, @"bin\Release\" + MSI_FILENAME_ARM64);
            string hostsPath = Path.Combine(projectDir, @"Sources\CommonAppData\SimpleDeFence\hosts.bck");
            string profilesPath = Path.Combine(projectDir, @"Sources\CommonAppData\SimpleDeFence\profiles.json");
            string twAssemblyPath = Path.Combine(projectDir, @"Sources\ProgramFiles\SimpleDeFence\SimpleDeFence.exe");

            UpdateModule prepare_module(string component_id, string src_filepath, string dst_filename, string version, bool compress)
            {
                if (!File.Exists(src_filepath))
                    throw new FileNotFoundException($"File\n\n{src_filepath}\n\nnot found.");

                string dst_filepath = Path.Combine(UpdateOutput.Text, dst_filename);
                if (compress)
                    CompressDeflate(src_filepath, dst_filepath);
                else
                    File.Copy(src_filepath, dst_filepath, true);

                return new UpdateModule
                {
                    Component = component_id,
                    ComponentVersion = version,
                    DownloadHash = Hasher.HashFile(src_filepath),
                    UpdateURL = UpdateURL.Text + dst_filename
                };
            }

            try
            {
                if (!File.Exists(twAssemblyPath))
                    throw new FileNotFoundException(string.Empty, twAssemblyPath);
                if (!Directory.Exists(UpdateOutput.Text))
                    throw new FileNotFoundException(string.Empty, UpdateOutput.Text);

                var version_info = FileVersionInfo.GetVersionInfo(twAssemblyPath).ProductVersion!.Trim();
                var timestamp = DateTime.UtcNow.ToString("O");
                var update = new UpdateDescriptor
                {
                    Modules = new UpdateModule[4]
                    {
                        prepare_module("SimpleDeFence_x86", msiX86Path, MSI_FILENAME_X86, version_info, false),
                        prepare_module("SimpleDeFence_arm64", msiArm64Path, MSI_FILENAME_ARM64, version_info, false),
                        prepare_module("Database", profilesPath, DB_OUT_NAME, timestamp, true),
                        prepare_module("HostsFile", hostsPath, HOSTS_OUT_NAME, timestamp, true)
                    }
                };

                SerializationHelper.SerializeToFile(update, Path.Combine(UpdateOutput.Text, DESCRIPTOR_NAME));
                update.Modules[3].DownloadHash = "[HOSTS_SHA256_PLACEHOLDER]";
                SerializationHelper.SerializeToFile(update, Path.Combine(UpdateOutput.Text, DESCRIPTOR_TEMPLATE_NAME));
            }
            catch (FileNotFoundException ex)
            {
                await ShowMessageAsync("Error", $"File or directory\n\n{ex?.FileName ?? "null"}\n\nnot found.");
                return;
            }

            await ShowMessageAsync("Success", "Update created.");
        }

        /// <summary>Straight copy of SimpleDeFence.Utils.CompressDeflate's body - that internal
        /// helper lives in the SimpleDeFence (WinForms) assembly, which SimpleDeFence.UI has no
        /// project reference to (see HostBootstrap.cs's own doc comment: the reference direction is
        /// SimpleDeFence -> SimpleDeFence.UI, not the other way - a back-reference here would cycle
        /// the project graph), and Utils itself is `internal` besides. Deliberately not moved to
        /// SimpleDeFence.Core: the rest of Utils is WinForms/System.Drawing-only and out of scope for
        /// this task.</summary>
        private static void CompressDeflate(string inputFile, string outputFile)
        {
            using var inFile = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
            using var outFile = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
            using var compressedOutFile = new System.IO.Compression.DeflateStream(outFile, System.IO.Compression.CompressionMode.Compress, true);

            byte[] buffer = new byte[4096];
            int numRead;
            while ((numRead = inFile.Read(buffer, 0, buffer.Length)) != 0)
            {
                compressedOutFile.Write(buffer, 0, numRead);
            }
        }

        /// <summary>Straight copy of SimpleDeFence.Utils.StartProcess's body - same reachability
        /// reason as CompressDeflate above.</summary>
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

        private static int CountOccurence(string haystack, char needle) => haystack.Count(c => c == needle);

        private async void AddPrimaries_Click(object sender, RoutedEventArgs e)
        {
            var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".resx");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0)
                return;

            foreach (var file in files)
            {
                string primary = file.Path;
                if (CountOccurence(Path.GetFileName(primary), '.') != 1)
                    continue;

                string? dir = Path.GetDirectoryName(primary);
                string primaryBase = Path.GetFileNameWithoutExtension(primary);
                string[] satellites = Directory.GetFiles(dir!, primaryBase + ".*.resx", SearchOption.TopDirectoryOnly);
                _resXInputs.Add(new KeyValuePair<string, string[]>(primary, satellites));
            }

            ListPrimaries.Items.Clear();
            foreach (var pair in _resXInputs)
                ListPrimaries.Items.Add(Path.GetFileName(pair.Key));
        }

        private void ListPrimaries_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ListSatellites.Items.Clear();
            if (ListPrimaries.SelectedIndex < 0)
                return;

            var pair = _resXInputs[ListPrimaries.SelectedIndex];
            foreach (var sat in pair.Value)
                ListSatellites.Items.Add(Path.GetFileName(sat));
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            ListPrimaries.Items.Clear();
            ListSatellites.Items.Clear();
            _resXInputs.Clear();
        }


        /// <summary>Strips from each satellite .resx the entries that add nothing: those whose
        /// translation is identical to the primary, and the designer-generated property keys that
        /// are not localizable text.
        ///
        /// Reads and writes through ResxFile (plain XML) rather than ResXResourceReader/Writer.
        /// Those were the last use of System.Windows.Forms anywhere in this project. Two things
        /// went with them: ITypeResolutionService, which was only ever passed as null, and a
        /// string-replace that rewrote ", Version=2.0.0.0," to "4.0.0.0" in each satellite before
        /// reading it - a workaround for the reader refusing type names from the .NET 2.0-era
        /// assembly, which cannot arise when the entries are read as XML.</summary>
        private void Optimize_Click(object sender, RoutedEventArgs e)
        {
            foreach (var pair in _resXInputs)
            {
                var primary = ResxFile.Read(pair.Key);

                foreach (var satellitePath in pair.Value)
                {
                    var satellite = ResxFile.Read(satellitePath);
                    var kept = new List<KeyValuePair<string, string>>();

                    foreach (var primaryEntry in primary)
                    {
                        if (!satellite.TryGetValue(primaryEntry.Key, out var satelliteValue))
                            continue;
                        if (satelliteValue is null)
                            continue; // not a plain string - left alone rather than rewritten

                        if (primaryEntry.Key.Contains('.'))
                        {
                            if (!primaryEntry.Key.EndsWith(".Text", StringComparison.Ordinal) &&
                                !primaryEntry.Key.EndsWith(".Title", StringComparison.Ordinal) &&
                                !primaryEntry.Key.EndsWith(".Filter", StringComparison.Ordinal) &&
                                !primaryEntry.Key.EndsWith(".AccessibleName", StringComparison.Ordinal))
                                continue;
                        }

                        if (string.Equals(satelliteValue, primaryEntry.Value, StringComparison.Ordinal))
                            continue; // untranslated - the primary already says this

                        kept.Add(new KeyValuePair<string, string>(primaryEntry.Key, satelliteValue));
                    }

                    var outPath = Path.Combine(OptimizeOutputPath.Text, Path.GetFileName(satellitePath));
                    ResxFile.Write(outPath, kept);
                }
            }
        }

        private async void CertBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickFileAsync();
            if (path is not null)
                CertPath.Text = path;
        }

        private async void SignDirBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickFolderAsync();
            if (path is not null)
                SignDirPath.Text = path;
        }

        private async void SigntoolBrowse_Click(object sender, RoutedEventArgs e)
        {
            var path = await PickFileAsync(".exe");
            if (path is not null)
                SigntoolPath.Text = path;
        }

        private async void BatchSign_Click(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(SignDirPath.Text))
            {
                await ShowMessageAsync("Error", "Signing directory is invalid!");
                return;
            }
            if (!File.Exists(SigntoolPath.Text))
            {
                await ShowMessageAsync("Error", "Signtool.exe not found!");
                return;
            }

            BatchSignButton.IsEnabled = false;
            await SignFilesAsync(SignDirPath.Text, SIGNING_FILE_PATTERNS);
            BatchSignButton.IsEnabled = true;
        }

        private async System.Threading.Tasks.Task SignFilesAsync(string dirPath, string[] filePatterns)
        {
            var filesToSign = new List<string>();
            foreach (var pattern in filePatterns)
            {
                foreach (var filePath in Directory.GetFiles(dirPath, pattern, SearchOption.AllDirectories))
                {
                    var signedStatus = SimpleDeFence.Windows.WinTrust.VerifyFileAuthenticode(filePath);
                    if (signedStatus == Windows.WinTrust.VerifyResult.SIGNATURE_MISSING)
                    {
                        filesToSign.Add("\"" + filePath + "\"");
                    }
                    else if (signedStatus == Windows.WinTrust.VerifyResult.SIGNATURE_INVALID)
                    {
                        await ShowMessageAsync("Signing result", $"File \"{filePath}\" has pre-existing INVALID certificate. Signing will be aborted for all files.");
                        return;
                    }
                }
            }

            if (filesToSign.Count == 0)
            {
                await ShowMessageAsync("Signing result", "No files to sign, or all files are already signed.");
                return;
            }

            string signParams = string.Format(
                "sign /d SimpleDeFence /du \"https://github.com/fcoltro/SimpleDeFence\" /n \"{0}\" /tr \"{1}\" /td sha256 /fd sha256 /v {2}",
                CertPath.Text, TimestampServer.Text, string.Join(" ", filesToSign));

            bool signSuccess;
            using (Process p = StartProcess(SigntoolPath.Text, signParams, false))
            {
                p.WaitForExit();
                signSuccess = p.ExitCode == 0;
            }

            await ShowMessageAsync("Signing result", signSuccess ? "Files successfully signed." : "Failed to sign files.");
        }
    }
}
