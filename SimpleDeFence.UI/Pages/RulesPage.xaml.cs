using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence;
using SimpleDeFence.DatabaseClasses;
using SimpleDeFence.Localization;
using SimpleDeFence.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    /// <summary>One selectable application-rule row.</summary>
    public sealed class RuleListItem
    {
        public RuleRow Row { get; init; } = null!;
        public string Name => Row.Name;
        public string Kind => Row.Kind;
        public string Detail => Row.Detail;
        public string Policy => Row.Policy;
        public bool IsBlocked => Row.IsBlocked;

        /// <summary>Blocked entries are tinted, the same signal ExceptionRow/SettingsForm give via
        /// row colour.</summary>
        // global:: is required - inside SimpleDeFence.UI.Pages a bare "Windows" binds to
        // SimpleDeFence.Windows, not the platform namespace.
        public global::Microsoft.UI.Xaml.Media.Brush RowBackground => IsBlocked
            ? new global::Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0x40, 0xE8, 0x11, 0x23))
            : new global::Microsoft.UI.Xaml.Media.SolidColorBrush(global::Microsoft.UI.Colors.Transparent);
    }

    /// <summary>One special-exception row with its toggle.</summary>
    public sealed class SpecialListItem
    {
        public RuleRow Row { get; init; } = null!;
        public string Name => Row.Name;
        public bool IsOn
        {
            get => Row.Enabled;
            set => ToggleRequested?.Invoke(this, value);
        }

        public event EventHandler<bool>? ToggleRequested;
    }

    public sealed partial class RulesPage : Page
    {
        private readonly ObservableCollection<RuleListItem> _apps = new();
        private readonly ObservableCollection<SpecialListItem> _specialRecommended = new();
        private readonly ObservableCollection<SpecialListItem> _specialOptional = new();
        private IReadOnlyList<RuleRow> _rows = Array.Empty<RuleRow>();
        private bool _busy;
        private bool _committing;

        /// <summary>Guards the Add split button's four pickers for the part _committing does not
        /// cover: the picker dialog is up but no commit has started yet (CommitAsync only flips
        /// _committing once a picker actually has something to commit). Prevents a rapid
        /// double-invoke from opening two overlapping picker dialogs (only one ContentDialog can be
        /// open per XamlRoot) - same rationale as ConnectionsPage's _allowBusy. Once a pick reaches
        /// the commit stage, CommitAddAsync routes through the shared CommitAsync helper (like
        /// Remove/Apply/Toggle), so _committing serializes that part; UpdateAddButtonEnabled gates
        /// the Add button on both flags together, and UpdateRemoveButton/UpdateApplyButtonEnabled
        /// gate Remove/Apply on _committing, so no two of Remove/Apply/Toggle/Add can commit at
        /// once.</summary>
        private bool _addBusy;

        /// <summary>What _addBusy is for the page's own Add split button, this is for the tray
        /// menu's three "Whitelist ..." items and their Ctrl+Alt+E/P/W hotkeys (see QuickAddAsync
        /// below). Deliberately a separate, static flag rather than a reuse of _addBusy/_committing:
        /// the tray can fire a quick-add with no RulesPage instance in existence at all, so there is
        /// no instance field to guard with and no Add button to grey out - only the "do not open two
        /// picker dialogs on one XamlRoot" part of _addBusy's job carries over.</summary>
        private static bool _quickAddBusy;

        /// <summary>The single selected application row the detail pane is currently editing, or
        /// null when zero/multiple rows are selected (the pane is collapsed in that case).</summary>
        private RuleListItem? _selectedDetailItem;

        public RulesPage()
        {
            InitializeComponent();
            // Keeps this page instance (and its filter/expander state) alive across navigating to
            // Connections and back, instead of Frame recreating it - same rationale as
            // ConnectionsPage.
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
            AppsList.ItemsSource = _apps;
            SpecialRecommendedList.ItemsSource = _specialRecommended;
            SpecialOptionalList.ItemsSource = _specialOptional;
            Loaded += async (_, _) => await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (_busy)
                return;

            SetBusy(true);
            try
            {
                await App.Firewall.RefreshAsync();

                if (!App.Firewall.Connected || App.Firewall.Config is null)
                {
                    ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Status.NotConnected),
                        App.Firewall.LastError ?? string.Empty);
                    _rows = Array.Empty<RuleRow>();
                }
                else
                {
                    Notice.IsOpen = false;
                    var db = await App.Firewall.GetAppDatabaseAsync();
                    _rows = RuleListBuilder.Build(App.Firewall.Config.ActiveProfile, db ?? new AppDatabase());
                }
            }
            catch (Exception ex)
            {
                // Never let a throw mid-refresh look like a clean, empty screen - surface it and
                // keep whatever rows were already loaded on screen.
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
            }
            finally
            {
                // A throw must not leave the page busy forever, permanently disabling Refresh.
                SetBusy(false);
            }

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var term = FilterBox.Text?.Trim() ?? string.Empty;
            var filtered = RuleListBuilder.Filter(_rows, term);

            _apps.Clear();
            foreach (var row in filtered.Where(r => r.Group == RuleGroup.Applications))
                _apps.Add(new RuleListItem { Row = row });

            RebuildSpecial(_specialRecommended, filtered, RuleGroup.SpecialRecommended);
            RebuildSpecial(_specialOptional, filtered, RuleGroup.SpecialOptional);

            AppsHeader.Text = Loc.T(LocKeys.Rules.SectionApplications)
                + " " + Loc.T(LocKeys.Connections.SectionCount, _apps.Count);
            SpecialRecommendedHeader.Text = Loc.T(LocKeys.Rules.SectionSpecialRecommended)
                + " " + Loc.T(LocKeys.Connections.SectionCount, _specialRecommended.Count);
            SpecialOptionalHeader.Text = Loc.T(LocKeys.Rules.SectionSpecialOptional)
                + " " + Loc.T(LocKeys.Connections.SectionCount, _specialOptional.Count);

            SetEmpty(AppsEmpty, _apps.Count, LocKeys.Rules.EmptyApplications, term);
            var specialCount = _specialRecommended.Count + _specialOptional.Count;
            SetEmpty(SpecialEmpty, specialCount, LocKeys.Rules.EmptySpecial, term);

            UpdateRemoveButton();
        }

        private void RebuildSpecial(ObservableCollection<SpecialListItem> target,
            IReadOnlyList<RuleRow> rows, RuleGroup group)
        {
            target.Clear();
            foreach (var row in rows.Where(r => r.Group == group))
            {
                var item = new SpecialListItem { Row = row };
                // The ToggleSwitch fires this synchronously from inside its own Toggled handling
                // (via the x:Bind TwoWay setter), still on that control's call stack. Against the
                // sample client every awaited step below completes synchronously (Task.FromResult),
                // so without deferring, ContentDialog.ShowAsync() and the collection rebuild in
                // RefreshAsync would run nested inside the ToggleSwitch's own event dispatch -
                // WinUI does not tolerate that reentrancy and crashes with an access violation
                // (reproduced: Microsoft.UI.Xaml.dll, 0xc0000005). Enqueueing via DispatcherQueue
                // lets the ToggleSwitch's own call stack unwind first.
                item.ToggleRequested += (_, enabled) => DispatcherQueue.TryEnqueue(() => _ = ToggleSpecialAsync(item, enabled));
                target.Add(item);
            }
        }

        private static void SetEmpty(TextBlock target, int count, string emptyKey, string term)
        {
            if (count != 0)
            {
                target.Visibility = Visibility.Collapsed;
                return;
            }

            target.Text = term.Length == 0 ? Loc.T(emptyKey) : Loc.T(LocKeys.Rules.EmptyFiltered);
            target.Visibility = Visibility.Visible;
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateRemoveButton();
            UpdateDetailPane();
        }

        private void UpdateRemoveButton()
        {
            var count = AppsList.SelectedItems.Count;
            RemoveButton.IsEnabled = count > 0 && !_committing;
            RemoveButton.Content = count > 0
                ? Loc.T(LocKeys.Rules.Remove) + " " + Loc.T(LocKeys.Connections.SectionCount, count)
                : Loc.T(LocKeys.Rules.Remove);
            UpdateAddButtonEnabled();
        }

        /// <summary>Shows/hides the detail pane based on selection count, and seeds it from the
        /// single selected row's policy. Only ever touches local UI state - never commits or shows
        /// a dialog, so it is safe to call from a selection-changed handler.</summary>
        private void UpdateDetailPane()
        {
            var selected = AppsList.SelectedItems.Cast<RuleListItem>().ToList();
            if (selected.Count != 1)
            {
                _selectedDetailItem = null;
                DetailPane.Visibility = Visibility.Collapsed;
                return;
            }

            _selectedDetailItem = selected[0];
            DetailPane.Visibility = Visibility.Visible;
            SeedDetailPane(_selectedDetailItem);
        }

        private void SeedDetailPane(RuleListItem item)
        {
            var row = item.Row;
            DetailName.Text = row.Name;
            DetailKind.Text = row.Kind;
            DetailSubjectDetail.Text = row.Detail;
            DetailSubjectDetail.Visibility = string.IsNullOrEmpty(row.Detail) ? Visibility.Collapsed : Visibility.Visible;

            var policy = row.Exception!.Policy;
            // Not policy.PolicyType == PolicyType.RuleList: that enum is a hand-maintained,
            // per-subclass property (see SimpleDeFence.Core/ExceptionPolicy.cs), so it is only as
            // trustworthy as whoever wrote the newest subclass's override. IsEditablePolicy below
            // instead pattern-matches the same real subclass shapes the switch itself uses, so
            // "editable" and "recognised by the switch" can never drift apart - refuse-by-default
            // (readOnly) for anything neither of them recognises, exactly like a RuleListPolicy row,
            // rather than risk BuildPolicyFromEditor's TcpUdpPolicy fallback silently submitting an
            // unrelated policy on Apply.
            bool readOnly = !IsEditablePolicy(policy);
            DetailReadOnlyNote.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
            DetailEditorFields.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;

            if (!readOnly)
            {
                // Reset before seeding so a preset that leaves a field blank (e.g. Blocked) does not
                // keep a value carried over from the previously selected row.
                TcpOutBox.Text = string.Empty;
                UdpOutBox.Text = string.Empty;
                TcpInBox.Text = string.Empty;
                UdpInBox.Text = string.Empty;
                LanOnlyCheck.IsChecked = false;

                // Pattern-matched against the actual ExceptionPolicy subclass shapes in
                // SimpleDeFence.Core/ExceptionPolicy.cs - not the PolicyType enum - so this stays
                // correct if a subclass ever changes without a matching PolicyType edit. readOnly
                // above already guarantees policy is one of these three cases, so there is nothing
                // left for a default branch to do here.
                switch (policy)
                {
                    case HardBlockPolicy:
                        PresetBlocked.IsChecked = true;
                        break;
                    case UnrestrictedPolicy { LocalNetworkOnly: true }:
                        PresetUnrestrictedLan.IsChecked = true;
                        break;
                    case UnrestrictedPolicy:
                        PresetUnrestricted.IsChecked = true;
                        break;
                    case TcpUdpPolicy tcpUdp:
                        PresetTcpUdp.IsChecked = true;
                        TcpOutBox.Text = tcpUdp.AllowedRemoteTcpConnectPorts ?? string.Empty;
                        UdpOutBox.Text = tcpUdp.AllowedRemoteUdpConnectPorts ?? string.Empty;
                        TcpInBox.Text = tcpUdp.AllowedLocalTcpListenerPorts ?? string.Empty;
                        UdpInBox.Text = tcpUdp.AllowedLocalUdpListenerPorts ?? string.Empty;
                        LanOnlyCheck.IsChecked = tcpUdp.LocalNetworkOnly;
                        break;
                }
            }

            UpdateTcpUdpFieldsEnabled();
            UpdateApplyButtonEnabled();
        }

        /// <summary>True for exactly the ExceptionPolicy subclasses SeedDetailPane/
        /// BuildPolicyFromEditor know how to edit. A RuleListPolicy row, or any future non-sealed
        /// subclass neither of them recognises, must read as false here - shared by SeedDetailPane
        /// (read-only note/editor visibility) and UpdateApplyButtonEnabled (Apply's enabled state)
        /// so the two can never disagree about whether a row is editable.</summary>
        private static bool IsEditablePolicy(ExceptionPolicy policy) => policy switch
        {
            HardBlockPolicy => true,
            UnrestrictedPolicy => true,
            TcpUdpPolicy => true,
            _ => false,
        };

        /// <summary>Local UI state only (which fields are enabled) - fires from a RadioButton's own
        /// Checked event, so per the Task 4 reentrancy fix it must never commit or show a dialog.</summary>
        private void PresetRadio_Checked(object sender, RoutedEventArgs e) => UpdateTcpUdpFieldsEnabled();

        private void UpdateTcpUdpFieldsEnabled()
        {
            // StackPanel (unlike its Control children) has no IsEnabled of its own to cascade, so
            // each field is toggled individually.
            bool enabled = PresetTcpUdp.IsChecked == true;
            TcpOutBox.IsEnabled = enabled;
            UdpOutBox.IsEnabled = enabled;
            TcpInBox.IsEnabled = enabled;
            UdpInBox.IsEnabled = enabled;
            LanOnlyCheck.IsEnabled = enabled;
        }

        private void UpdateApplyButtonEnabled()
        {
            var editable = _selectedDetailItem is not null && IsEditablePolicy(_selectedDetailItem.Row.Exception!.Policy);
            ApplyButton.IsEnabled = editable && !_committing;
            UpdateAddButtonEnabled();
        }

        /// <summary>Add SplitButton is disabled for the same two reasons Remove/Apply are: a commit
        /// (of any of the four Add pickers, or of Remove/Apply/Toggle) is in flight (_committing), or
        /// an Add picker's own dialog is up and hasn't reached the commit stage yet (_addBusy, which
        /// _committing does not cover since it is only set once CommitAsync is actually invoked).
        /// Called from both UpdateRemoveButton/UpdateApplyButtonEnabled (which already run on every
        /// _committing transition) and directly wherever _addBusy changes.</summary>
        private void UpdateAddButtonEnabled() => AddButton.IsEnabled = !_committing && !_addBusy;

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        /// <summary>MenuFlyoutItem.Click - a plain top-level Click, the same safe shape
        /// RemoveButton_Click/ApplyButton_Click already use, so committing and showing a result
        /// dialog directly from here is fine per the Task 4 reentrancy rule.</summary>
        private async void AddPickExecutable_Click(object sender, RoutedEventArgs e)
        {
            if (_addBusy)
                return;

            _addBusy = true;
            UpdateAddButtonEnabled();
            try
            {
                global::Windows.Storage.StorageFile? file;
                try
                {
                    file = await PickExecutableFileAsync();
                }
                catch (Exception ex)
                {
                    // PickSingleFileAsync is an awaited WinRT async operation on an async void
                    // handler with no process-wide UnhandledException backstop - an exception here
                    // (COM/interop failure) must be caught, not left to terminate the process.
                    await ShowResultAsync(Loc.T(LocKeys.Connections.AllowFailedTitle), ex.Message);
                    return;
                }

                if (file is null)
                    return; // Cancelled (or no MainWindow yet) - not an error, no dialog, no notice.

                if (ClientSettings.Load().AskForExceptionDetails && !await ConfirmQuickAddAsync(Content.XamlRoot, file.Name))
                    return;

                await CommitAddAsync(new ExecutableSubject(file.Path), file.Name);
            }
            finally
            {
                _addBusy = false;
                UpdateAddButtonEnabled();
            }
        }

        /// <summary>Shared FileOpenPicker construction and invocation for both this page's own
        /// "Executable file..." picker (above) and the tray/hotkey quick-add's executable picker
        /// (QuickPickExecutableAsync below) - one .exe-filtered picker, wired once, instead of the
        /// two independent copies this task originally shipped with (brief's Step 5 called out
        /// exactly this merge as part of the task, the same way Process/Window's pickers already
        /// share PickFromListAsync).
        ///
        /// "Windows" is ambiguous inside SimpleDeFence.UI.Pages - it resolves to the sibling
        /// SimpleDeFence.Windows project/namespace before the platform namespace, so the picker type
        /// needs the same global:: qualification RuleListItem.RowBackground already uses for the
        /// same reason. Callers, not this method, decide how to report a PickSingleFileAsync
        /// exception - the page's own Click handler shows an InfoBar-backed result dialog, the
        /// tray/hotkey path already has QuickAddAsync's own outer try/catch - so this method lets
        /// that exception propagate rather than swallowing it.</summary>
        private static async Task<global::Windows.Storage.StorageFile?> PickExecutableFileAsync()
        {
            if (App.MainWindow is null)
                return null;

            var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            return await picker.PickSingleFileAsync();
        }

        /// <summary>Same safe shape as AddPickExecutable_Click above.</summary>
        private async void AddPickUwp_Click(object sender, RoutedEventArgs e)
        {
            if (_addBusy)
                return;

            _addBusy = true;
            UpdateAddButtonEnabled();
            try
            {
                await PickUwpAsync();
            }
            finally
            {
                _addBusy = false;
                UpdateAddButtonEnabled();
            }
        }

        /// <summary>Same safe shape as AddPickExecutable_Click/AddPickUwp_Click above.</summary>
        private async void AddPickProcess_Click(object sender, RoutedEventArgs e)
        {
            if (_addBusy)
                return;

            _addBusy = true;
            UpdateAddButtonEnabled();
            try
            {
                await PickProcessAsync();
            }
            finally
            {
                _addBusy = false;
                UpdateAddButtonEnabled();
            }
        }

        /// <summary>Same safe shape as AddPickExecutable_Click/AddPickUwp_Click above.</summary>
        private async void AddPickWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_addBusy)
                return;

            _addBusy = true;
            UpdateAddButtonEnabled();
            try
            {
                await PickWindowAsync();
            }
            finally
            {
                _addBusy = false;
                UpdateAddButtonEnabled();
            }
        }

        /// <summary>Same safe shape as AddPickExecutable_Click. Recursively collects every
        /// .exe/.dll under the picked folder and adds each as an exception using the app
        /// database's default recommendation - deliberately no per-file prompt, matching WinForms'
        /// SettingsForm.btnAppAddFolder_Click, since a folder can match dozens of files (e.g. a game
        /// install directory).</summary>
        private async void AddPickFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_addBusy)
                return;

            _addBusy = true;
            UpdateAddButtonEnabled();
            try
            {
                var picker = new global::Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");

                if (App.MainWindow is null)
                    return;

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                global::Windows.Storage.StorageFolder? folder;
                try
                {
                    folder = await picker.PickSingleFolderAsync();
                }
                catch (Exception ex)
                {
                    await ShowResultAsync(Loc.T(LocKeys.Connections.AllowFailedTitle), ex.Message);
                    return;
                }

                if (folder is null)
                    return; // Cancelled.

                var files = new List<string>();
                CollectExeAndDllFiles(folder.Path, files);
                if (files.Count == 0)
                    return;

                var db = await App.Firewall.GetAppDatabaseAsync();
                var toAdd = new List<FirewallExceptionV3>();
                foreach (var file in files)
                {
                    try
                    {
                        toAdd.AddRange((db ?? new AppDatabase()).GetExceptionsForApp(new ExecutableSubject(file), out _));
                    }
                    catch { } // Matches WinForms' own per-file catch-and-skip in btnAppAddFolder_Click.
                }

                if (toAdd.Count == 0)
                    return;

                var resp = await CommitAsync(profile => profile.AddExceptions(toAdd));
                if (resp == MessageType.PUT_SETTINGS)
                {
                    await ShowResultAsync(Loc.T(LocKeys.Connections.AllowSuccessTitle),
                        Loc.T(LocKeys.Connections.AllowSuccessBody, folder.Name));
                    await RefreshAsync();
                }
                else
                {
                    await ShowResultAsync(Loc.T(LocKeys.Connections.AllowFailedTitle), FailureDetail(resp,
                        LocKeys.Connections.AllowFailedLockedDetail, LocKeys.Connections.AllowFailedStaleDetail,
                        LocKeys.Connections.AllowFailedGenericDetail));
                }
            }
            finally
            {
                _addBusy = false;
                UpdateAddButtonEnabled();
            }
        }

        /// <summary>Pure file-system recursion, ported unchanged from WinForms'
        /// SettingsForm.CollectExeAndDllFiles - it never had a WinForms dependency, only a WinForms
        /// home.</summary>
        private static void CollectExeAndDllFiles(string path, List<string> results)
        {
            try
            {
                results.AddRange(System.IO.Directory.GetFiles(path, "*.exe", System.IO.SearchOption.TopDirectoryOnly));
                results.AddRange(System.IO.Directory.GetFiles(path, "*.dll", System.IO.SearchOption.TopDirectoryOnly));

                foreach (string dir in System.IO.Directory.GetDirectories(path))
                    CollectExeAndDllFiles(dir, results);
            }
            catch
            {
                // Matches WinForms' own behavior: an inaccessible subdirectory is skipped, not fatal
                // to the whole scan.
            }
        }

        /// <summary>Builds and shows the UWP package picker dialog, then (if a package was picked)
        /// commits it before returning. UwpPackageList enumerates the real OS package catalog
        /// (Windows.Management.Deployment.PackageManager) regardless of --sample-data - there is no
        /// sample-data seam for it, unlike the rest of this page's data. The package list is
        /// materialized once up front; UwpPackageList caches internally per-instance, but there is
        /// no reason to re-query the OS every time the filter box changes.
        ///
        /// The commit happens here, after ShowAsync() returns, rather than inline inside
        /// ListView.ItemClick: ItemClick fires as its own separate, un-awaited dispatcher
        /// continuation, decoupled from whatever awaited the ShowAsync() call that it completes by
        /// calling dialog.Hide(). Committing from inside ItemClick would let CommitAddAsync (and its
        /// result dialog) run entirely after PickUwpAsync had already returned control to
        /// AddPickUwp_Click, which resets _addBusy = false as soon as its single `await
        /// PickUwpAsync()` completes - leaving _addBusy false for the whole commit and defeating the
        /// reentrancy guard. Capturing the pick and awaiting the commit here, after ShowAsync()
        /// returns, keeps it inside PickUwpAsync's own await chain so _addBusy covers the full
        /// operation, symmetric with AddPickExecutable_Click and matching the ConnectionsPage
        /// _allowBusy precedent.</summary>
        private async Task PickUwpAsync()
        {
            // UwpPackageList.ToList() enumerates the OS package catalog (PackageManager.
            // FindPackagesForUser plus a per-package SID-derivation P/Invoke) - on a real machine
            // that is 150-300+ packages, so materializing it must happen off the UI thread like
            // the other three pickers' data sources already do (GetRunningProcessesAsync,
            // GetTopLevelWindowsAsync, and the awaited FileOpenPicker call), or the window freezes
            // with no busy indicator for however long the enumeration takes.
            var allPackages = await Task.Run(() => new UwpPackageList().ToList());

            var listView = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true,
                MaxHeight = 360,
            };

            // Shown instead of a silently-blank list box when nothing matches - including on a
            // genuine enumeration failure: UwpPackageList swallows that into an empty Packages list
            // (e.g. the AppXSVC service disabled), which without this looked identical to "you typed
            // a filter that matched nothing" or "there is nothing to pick from".
            var emptyText = new TextBlock
            {
                Text = Loc.T(LocKeys.Rules.PickEmpty),
                Opacity = 0.7,
                Margin = new Thickness(4),
                Visibility = Visibility.Collapsed,
            };

            void Populate(string term)
            {
                listView.Items.Clear();
                foreach (var package in allPackages)
                {
                    // The Package struct exposes Name and PublisherId but no ready-made "family
                    // name" property; Name + "_" + PublisherId is the real Windows Package Family
                    // Name convention, built from the two fields that do exist.
                    var familyName = $"{package.Name}_{package.PublisherId}";
                    if (term.Length > 0
                        && package.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) < 0
                        && familyName.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) < 0)
                        continue;

                    var row = new StackPanel { Tag = package, Padding = new Thickness(4) };
                    row.Children.Add(new TextBlock { Text = package.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                    row.Children.Add(new TextBlock { Text = familyName, FontSize = 12, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis });
                    listView.Items.Add(row);
                }

                emptyText.Visibility = listView.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            Populate(string.Empty);

            var filterBox = new TextBox { PlaceholderText = Loc.T(LocKeys.Rules.FilterPlaceholder) };
            filterBox.TextChanged += (_, _) => Populate(filterBox.Text?.Trim() ?? string.Empty);

            var panel = new StackPanel { Spacing = 8, Width = 420 };
            panel.Children.Add(filterBox);
            panel.Children.Add(listView);
            panel.Children.Add(emptyText);

            var pickTitle = Loc.T(LocKeys.Rules.PickUwpTitle);
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = pickTitle,
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
                Content = panel,
            };

            // ItemClick only records the pick and hides the dialog - it must not commit itself (see
            // the method doc above for why). Hide() completes the await TryShowDialogAsync(...)
            // below, which is where the commit actually happens: only one ContentDialog can be open
            // per XamlRoot, so CommitAddAsync's own result dialog would otherwise fail to show while
            // the picker is still up.
            UwpPackageList.Package? picked = null;
            listView.ItemClick += (_, args) =>
            {
                var row = (FrameworkElement)args.ClickedItem;
                picked = (UwpPackageList.Package)row.Tag;
                dialog.Hide();
            };

            await TryShowDialogAsync(dialog, pickTitle, Loc.T(LocKeys.Common.AnotherDialogOpenDetail));

            if (picked is { } package)
                await CommitAddAsync(new AppContainerSubject(package), package.Name);
        }

        /// <summary>Builds and shows the running-process picker dialog via the shared
        /// PickFromListAsync helper below, then (if a process was picked) commits it before
        /// returning. Same commit-after-ShowAsync-returns shape as PickUwpAsync, for the same
        /// _addBusy-lifecycle reason: AddPickProcess_Click's single `await PickProcessAsync()` must
        /// stay pending until the commit (and its result dialog) are done, not just until the picker
        /// dialog closes.</summary>
        private async Task PickProcessAsync()
        {
            var processes = await App.Firewall.GetRunningProcessesAsync();

            var picked = await PickFromListAsync(
                Content.XamlRoot,
                Loc.T(LocKeys.Rules.PickProcessTitle),
                processes,
                (process, term) =>
                    process.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || process.Path.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0,
                process => (process.Name, process.Path),
                ShowDialogBlockedNotice);

            if (picked is { } process)
            {
                if (ClientSettings.Load().AskForExceptionDetails && !await ConfirmQuickAddAsync(Content.XamlRoot, process.Name))
                    return;

                await CommitAddAsync(new ExecutableSubject(process.Path), process.Name);
            }
        }

        /// <summary>Same shape as PickProcessAsync above, for visible top-level windows. The window's
        /// title (not its process name) is the display name in the result dialog/refreshed row -
        /// it's the identifying detail the user actually picked from the list.</summary>
        private async Task PickWindowAsync()
        {
            var windows = await App.Firewall.GetTopLevelWindowsAsync();

            var picked = await PickFromListAsync(
                Content.XamlRoot,
                Loc.T(LocKeys.Rules.PickWindowTitle),
                windows,
                (window, term) =>
                    window.Title.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || window.ProcessName.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0,
                window => (window.Title, window.ProcessName),
                ShowDialogBlockedNotice);

            if (picked is { } window)
            {
                if (ClientSettings.Load().AskForExceptionDetails && !await ConfirmQuickAddAsync(Content.XamlRoot, window.Title))
                    return;

                await CommitAddAsync(new ExecutableSubject(window.ProcessPath), window.Title);
            }
        }

        /// <summary>Shared filterable-list-in-a-dialog shape for the process and window pickers
        /// above, factored out because those two are structurally identical (a reference-type row
        /// DTO, a two-field substring filter, a two-line row) once PickUwpAsync's own row type is set
        /// aside. PickUwpAsync is deliberately NOT rebuilt on top of this: UwpPackageList.Package is a
        /// struct, and an unconstrained "T? picked" can't distinguish "nothing picked" from
        /// "default(Package)" the way it can for a reference type - forcing it in would either need
        /// an awkward extra bool or silently mishandle Cancel, so the `where T : class` constraint
        /// here is intentional, not an oversight.
        ///
        /// Same commit-free ItemClick shape PickUwpAsync established (and Task 6's fix corrected):
        /// ItemClick only records the pick and calls Hide() - never commits - because ItemClick fires
        /// as its own separate, un-awaited dispatcher continuation. The caller commits after this
        /// method returns, still inside its own await chain, so whichever *_Click handler set
        /// _addBusy keeps it true for the full pick-then-commit operation.
        ///
        /// Static, with the XamlRoot and the "another dialog is already open" reporter passed in,
        /// because the tray's quick-add (QuickAddProcessAsync/QuickAddWindowAsync below) shows the
        /// very same pickers with no RulesPage instance in existence - so there is no
        /// Content.XamlRoot and no Notice InfoBar to reach for. The page's own two call sites pass
        /// both and keep their previous behaviour exactly; the tray passes null for the reporter,
        /// where a blocked dialog simply means nothing was picked and nothing is added.</summary>
        private static async Task<T?> PickFromListAsync<T>(XamlRoot xamlRoot, string title,
            IReadOnlyList<T> items, Func<T, string, bool> matches,
            Func<T, (string Primary, string Secondary)> toRow, Action<string, string>? onDialogBlocked)
            where T : class
        {
            var listView = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true,
                MaxHeight = 360,
            };

            // Shown instead of a silently-blank list box when nothing matches - see PickUwpAsync's
            // identical emptyText for why this matters beyond just an empty filter result.
            var emptyText = new TextBlock
            {
                Text = Loc.T(LocKeys.Rules.PickEmpty),
                Opacity = 0.7,
                Margin = new Thickness(4),
                Visibility = Visibility.Collapsed,
            };

            void Populate(string term)
            {
                listView.Items.Clear();
                foreach (var item in items)
                {
                    if (term.Length > 0 && !matches(item, term))
                        continue;

                    var (primary, secondary) = toRow(item);
                    var row = new StackPanel { Tag = item, Padding = new Thickness(4) };
                    row.Children.Add(new TextBlock { Text = primary, TextTrimming = TextTrimming.CharacterEllipsis });
                    row.Children.Add(new TextBlock { Text = secondary, FontSize = 12, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis });
                    listView.Items.Add(row);
                }

                emptyText.Visibility = listView.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            Populate(string.Empty);

            var filterBox = new TextBox { PlaceholderText = Loc.T(LocKeys.Rules.FilterPlaceholder) };
            filterBox.TextChanged += (_, _) => Populate(filterBox.Text?.Trim() ?? string.Empty);

            var panel = new StackPanel { Spacing = 8, Width = 420 };
            panel.Children.Add(filterBox);
            panel.Children.Add(listView);
            panel.Children.Add(emptyText);

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
                Content = panel,
            };

            T? picked = null;
            listView.ItemClick += (_, args) =>
            {
                var row = (FrameworkElement)args.ClickedItem;
                picked = (T)row.Tag;
                dialog.Hide();
            };

            if (await ShowDialogOrNullAsync(dialog) is null)
                onDialogBlocked?.Invoke(title, Loc.T(LocKeys.Common.AnotherDialogOpenDetail));

            return picked;
        }

        /// <summary>Shared commit path for all four Add pickers - identical operation to Connections'
        /// "Allow this app", so it reuses the same loc keys and reporting pattern. Routed through the
        /// page's own CommitAsync (rather than calling App.Firewall.AllowAsync directly, which is
        /// what AllowAsync itself does under the hood) so Add serializes with Remove/Apply/Toggle
        /// through the single shared _committing guard instead of being a fifth, independent commit
        /// path that could race them - this also gets the shared exception -> InfoBar handling
        /// CommitAsync already gives Remove/Apply/Toggle, so no bespoke try/catch is needed here.</summary>
        private async Task CommitAddAsync(ExceptionSubject subject, string displayName)
        {
            var resp = await CommitAsync(profile =>
                profile.AddExceptions(new List<FirewallExceptionV3> { new(subject, new TcpUdpPolicy(true)) }));

            if (resp == MessageType.PUT_SETTINGS)
            {
                await ShowResultAsync(Loc.T(LocKeys.Connections.AllowSuccessTitle),
                    Loc.T(LocKeys.Connections.AllowSuccessBody, displayName));
                await RefreshAsync();
            }
            else
            {
                await ShowResultAsync(Loc.T(LocKeys.Connections.AllowFailedTitle), FailureDetail(resp,
                    LocKeys.Connections.AllowFailedLockedDetail, LocKeys.Connections.AllowFailedStaleDetail,
                    LocKeys.Connections.AllowFailedGenericDetail));
            }
        }

        /// <summary>Tray menu "Whitelist executable..." (and its Ctrl+Alt+E hotkey). Shows the same
        /// .exe picker AddPickExecutable_Click does, then commits.</summary>
        internal static Task QuickAddExecutableAsync(bool askForDetails)
            => QuickAddAsync(askForDetails, QuickPickExecutableAsync);

        /// <summary>Tray menu "Whitelist running process..." (Ctrl+Alt+P).</summary>
        internal static Task QuickAddProcessAsync(bool askForDetails)
            => QuickAddAsync(askForDetails, QuickPickProcessAsync);

        /// <summary>Tray menu "Whitelist by window..." (Ctrl+Alt+W).</summary>
        internal static Task QuickAddWindowAsync(bool askForDetails)
            => QuickAddAsync(askForDetails, QuickPickWindowAsync);

        /// <summary>
        /// Shared pick-then-commit path for the three tray/hotkey quick-adds above - the same
        /// operation CommitAddAsync performs for the page's own Add pickers, and it builds the very
        /// same ExecutableSubject + TcpUdpPolicy(true) exception, but deliberately NOT routed
        /// through the page: there may be no RulesPage instance on screen at all when the tray fires
        /// one of these, so there is no _committing flag to serialize with, no button to grey out,
        /// and above all nothing to RefreshAsync() afterwards. A quick-add that lands while the page
        /// IS open therefore shows up on it at the next refresh, not instantly.
        ///
        /// Committing outside the page's _committing guard means a quick-add can collide with a
        /// commit the open page started. That collision is safe rather than silent: the service
        /// detects the stale changeset and applies nothing, which CommitConfigChangesAsync reports
        /// as RESPONSE_STALE_CHANGESET and the result dialog below shows as a failure.
        ///
        /// The whole body is wrapped because every entry point into it is fire-and-forget - a
        /// MenuFlyoutItem.Click lambda or, for the hotkeys, WindowHotkeys' WndProc callback. An
        /// escaping exception there is either an unobserved Task or a throw inside a native window
        /// procedure, neither of which reaches a user as anything but a crash or a silence.
        /// </summary>
        private static async Task QuickAddAsync(bool askForDetails,
            Func<XamlRoot, Task<(ExceptionSubject Subject, string DisplayName)?>> pick)
        {
            if (_quickAddBusy)
                return;

            // Before the window's content is loaded there is no XamlRoot to host the picker or the
            // result dialog, so there is no honest way to run this at all.
            var xamlRoot = App.MainWindow?.Content?.XamlRoot;
            if (xamlRoot is null)
                return;

            _quickAddBusy = true;
            try
            {
                if (await pick(xamlRoot) is not { } picked)
                    return; // Cancelled or blocked - not an error, no dialog.

                var (subject, displayName) = picked;

                // askForDetails is ClientSettings.AskForExceptionDetails. WinForms opens a full
                // policy editor here; this is deliberately just a confirmation naming what was
                // resolved, since the policy it would edit is the same unrestricted-TCP/UDP one the
                // page's own Add pickers commit without asking.
                if (askForDetails && !await ConfirmQuickAddAsync(xamlRoot, displayName))
                    return;

                var resp = await App.Firewall.CommitConfigChangesAsync(config =>
                    config.ActiveProfile.AddExceptions(
                        new List<FirewallExceptionV3> { new(subject, new TcpUdpPolicy(true)) }));

                if (resp == MessageType.PUT_SETTINGS)
                {
                    await ShowQuickAddResultAsync(xamlRoot, Loc.T(LocKeys.Connections.AllowSuccessTitle),
                        Loc.T(LocKeys.Connections.AllowSuccessBody, displayName));
                }
                else
                {
                    await ShowQuickAddResultAsync(xamlRoot, Loc.T(LocKeys.Connections.AllowFailedTitle),
                        FailureDetail(resp, LocKeys.Connections.AllowFailedLockedDetail,
                            LocKeys.Connections.AllowFailedStaleDetail,
                            LocKeys.Connections.AllowFailedGenericDetail));
                }
            }
            catch (Exception ex)
            {
                await ShowQuickAddResultAsync(xamlRoot, Loc.T(LocKeys.Connections.AllowFailedTitle), ex.Message);
            }
            finally
            {
                _quickAddBusy = false;
            }
        }

        /// <summary>Quick-add's executable picker - goes through the same PickExecutableFileAsync
        /// helper AddPickExecutable_Click uses, so there is exactly one FileOpenPicker wiring in
        /// this file, not two. Ignores the XamlRoot the shared delegate shape hands it: a picker
        /// needs the owner HWND PickExecutableFileAsync already looks up from App.MainWindow, not a
        /// XamlRoot.</summary>
        private static async Task<(ExceptionSubject Subject, string DisplayName)?> QuickPickExecutableAsync(XamlRoot xamlRoot)
        {
            var file = await PickExecutableFileAsync();
            return file is null ? null : (new ExecutableSubject(file.Path), file.Name);
        }

        /// <summary>Quick-add's running-process picker - the same list, filter and row shape
        /// PickProcessAsync shows, through the same shared PickFromListAsync helper.</summary>
        private static async Task<(ExceptionSubject Subject, string DisplayName)?> QuickPickProcessAsync(XamlRoot xamlRoot)
        {
            var processes = await App.Firewall.GetRunningProcessesAsync();

            var picked = await PickFromListAsync(
                xamlRoot,
                Loc.T(LocKeys.Rules.PickProcessTitle),
                processes,
                (process, term) =>
                    process.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || process.Path.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0,
                process => (process.Name, process.Path),
                onDialogBlocked: null);

            return picked is null ? null : (new ExecutableSubject(picked.Path), picked.Name);
        }

        /// <summary>Quick-add's top-level-window picker - same as PickWindowAsync, including using
        /// the window's title (not its process name) as the display name, since that is the
        /// identifying detail the user actually picked from the list.</summary>
        private static async Task<(ExceptionSubject Subject, string DisplayName)?> QuickPickWindowAsync(XamlRoot xamlRoot)
        {
            var windows = await App.Firewall.GetTopLevelWindowsAsync();

            var picked = await PickFromListAsync(
                xamlRoot,
                Loc.T(LocKeys.Rules.PickWindowTitle),
                windows,
                (window, term) =>
                    window.Title.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || window.ProcessName.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0,
                window => (window.Title, window.ProcessName),
                onDialogBlocked: null);

            return picked is null ? null : (new ExecutableSubject(picked.ProcessPath), picked.Title);
        }

        /// <summary>The "ask for exception details" confirmation. A blocked dialog is never a
        /// confirmation, so the add safely does not happen - the same refuse-by-default choice
        /// RemoveButton_Click's confirm makes.</summary>
        private static async Task<bool> ConfirmQuickAddAsync(XamlRoot xamlRoot, string displayName)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = Loc.T(LocKeys.Tray.QuickAddConfirmTitle),
                Content = Loc.T(LocKeys.Tray.QuickAddConfirmBody, displayName),
                PrimaryButtonText = Loc.T(LocKeys.Tray.QuickAddConfirmAdd),
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
                DefaultButton = ContentDialogButton.Close,
            };

            return await ShowDialogOrNullAsync(dialog) == ContentDialogResult.Primary;
        }

        /// <summary>ShowResultAsync's counterpart for the quick-add path: same dialog, attached to
        /// the window's XamlRoot instead of a page's, and with no InfoBar to degrade to.</summary>
        private static async Task ShowQuickAddResultAsync(XamlRoot xamlRoot, string title, string body)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = body,
                CloseButtonText = Loc.T(LocKeys.Common.Ok),
            };

            await ShowDialogOrNullAsync(dialog);
        }

        private async void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = AppsList.SelectedItems.Cast<RuleListItem>().ToList();
            if (selected.Count == 0 || _committing)
                return;

            var confirmTitle = Loc.T(LocKeys.Rules.RemoveConfirmTitle);
            var confirmBody = Loc.T(LocKeys.Rules.RemoveConfirmBody, selected.Count);
            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = confirmTitle,
                Content = confirmBody,
                PrimaryButtonText = Loc.T(LocKeys.Rules.Remove),
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
                DefaultButton = ContentDialogButton.Close,
            };
            // If another dialog is already up (single-dialog-per-XamlRoot), the fallback InfoBar
            // shows the same confirm text this dialog would have, and ContentDialogResult.None
            // (never Primary) means Remove safely does not proceed without an actual confirmation.
            if (await TryShowDialogAsync(confirm, confirmTitle, confirmBody) != ContentDialogResult.Primary)
                return;

            var ids = selected.Select(s => s.Row.Exception!.Id).ToList();
            var resp = await CommitAsync(profile => RuleEdit.RemoveExceptions(profile, ids));

            if (resp == MessageType.PUT_SETTINGS)
            {
                await ShowResultAsync(Loc.T(LocKeys.Rules.RemoveSuccessTitle),
                    Loc.T(LocKeys.Rules.RemoveSuccessBody, ids.Count));
                await RefreshAsync();
            }
            else
            {
                await ShowResultAsync(Loc.T(LocKeys.Rules.RemoveFailedTitle), FailureDetail(resp,
                    LocKeys.Rules.RemoveFailedLockedDetail, LocKeys.Rules.RemoveFailedStaleDetail,
                    LocKeys.Rules.RemoveFailedGenericDetail));
            }
        }

        /// <summary>
        /// Plain Button.Click handler - the same safe shape RemoveButton_Click already uses. This is
        /// the only place in the detail pane allowed to call CommitAsync/show a dialog; the
        /// RadioButton/CheckBox handlers above only ever touch local UI state, per the Task 4
        /// reentrancy fix (firing a ContentDialog synchronously from inside another control's own
        /// event dispatch corrupts WinUI against the sample client's synchronously-completing tasks).
        /// </summary>
        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing || _selectedDetailItem is null)
                return;

            var original = _selectedDetailItem.Row.Exception!;
            if (!IsEditablePolicy(original.Policy))
                return; // Apply is disabled for this row; guard in case that ever drifts.

            // Captured now, not read back off _selectedDetailItem after the commit: RefreshAsync
            // below rebuilds _apps, which resets AppsList's selection and (via
            // AppsList_SelectionChanged -> UpdateDetailPane) nulls _selectedDetailItem out from
            // under this handler.
            var rowName = _selectedDetailItem.Row.Name;
            var newPolicy = BuildPolicyFromEditor();

            FirewallExceptionV3 edited;
            try
            {
                edited = RuleEdit.ApplyPreset(original, newPolicy);
            }
            catch (InvalidOperationException ex)
            {
                // Unreachable given the guard above, but a thrown exception must not look like
                // nothing happened.
                await ShowResultAsync(Loc.T(LocKeys.Rules.DetailApplyFailedTitle), ex.Message);
                return;
            }

            var resp = await CommitAsync(profile =>
                profile.AddExceptions(new List<FirewallExceptionV3> { edited }));

            if (resp == MessageType.PUT_SETTINGS)
            {
                // Refresh first: RefreshAsync's own success path unconditionally sets
                // Notice.IsOpen = false, so setting the confirmation before refreshing would just
                // have it wiped out again before it was ever seen. A lightweight, non-blocking
                // InfoBar (unlike Remove's dialog) is enough here - Apply is a frequent, low-risk
                // action on a single row, and the refreshed row already shows the new policy text.
                // Success (Success severity) and failure (Error severity, from
                // ShowResultAsync/ShowNotice elsewhere) are always paired with distinct
                // title/message text, never signalled by colour alone.
                await RefreshAsync();
                ShowNotice(InfoBarSeverity.Success, Loc.T(LocKeys.Rules.DetailApplySuccess), rowName);
            }
            else
            {
                await ShowResultAsync(Loc.T(LocKeys.Rules.DetailApplyFailedTitle), FailureDetail(resp,
                    LocKeys.Rules.DetailApplyFailedLockedDetail, LocKeys.Rules.DetailApplyFailedStaleDetail,
                    LocKeys.Rules.DetailApplyFailedGenericDetail));
            }
        }

        /// <summary>Reads the preset editor's current state into the matching ExceptionPolicy
        /// subclass. TCP/UDP is the fallback branch: it is the only preset besides the mutually
        /// exclusive radios above, and exactly one of the four is always checked once a row has been
        /// seeded (SeedDetailPane always sets one).</summary>
        private ExceptionPolicy BuildPolicyFromEditor()
        {
            if (PresetBlocked.IsChecked == true)
                return new HardBlockPolicy();

            if (PresetUnrestricted.IsChecked == true)
                return new UnrestrictedPolicy { LocalNetworkOnly = false };

            if (PresetUnrestrictedLan.IsChecked == true)
                return new UnrestrictedPolicy { LocalNetworkOnly = true };

            return new TcpUdpPolicy
            {
                AllowedRemoteTcpConnectPorts = NormalizePorts(TcpOutBox.Text),
                AllowedRemoteUdpConnectPorts = NormalizePorts(UdpOutBox.Text),
                AllowedLocalTcpListenerPorts = NormalizePorts(TcpInBox.Text),
                AllowedLocalUdpListenerPorts = NormalizePorts(UdpInBox.Text),
                LocalNetworkOnly = LanOnlyCheck.IsChecked == true,
            };
        }

        private static string? NormalizePorts(string text)
        {
            var trimmed = text?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        private async Task ToggleSpecialAsync(SpecialListItem item, bool enabled)
        {
            // Serializes with Remove and with other toggles: only one commit in flight at a time.
            // The ToggleSwitch already shows the new value (its IsOn setter fired this handler via
            // the TwoWay binding) - RefreshAsync below is what reconciles it back to the truth,
            // whichever way the commit went.
            if (_committing)
                return;

            var id = item.Row.SpecialId!;
            var resp = await CommitAsync(profile => RuleEdit.SetSpecialEnabled(profile, id, enabled));

            if (resp == MessageType.PUT_SETTINGS)
            {
                await RefreshAsync();
            }
            else
            {
                // The toggle already flipped visually; revert it to the truth.
                await RefreshAsync();
                await ShowResultAsync(Loc.T(LocKeys.Rules.SpecialToggleFailedTitle), FailureDetail(resp,
                    LocKeys.Rules.SpecialToggleFailedLockedDetail, LocKeys.Rules.SpecialToggleFailedStaleDetail,
                    LocKeys.Rules.SpecialToggleFailedGenericDetail));
            }
        }

        /// <summary>One serialized commit path; concurrent commits are refused, never raced.</summary>
        private async Task<MessageType> CommitAsync(Action<ServerProfileConfiguration> mutate)
        {
            _committing = true;
            UpdateRemoveButton();
            UpdateApplyButtonEnabled();
            try
            {
                return await App.Firewall.CommitConfigChangesAsync(config => mutate(config.ActiveProfile));
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
                return MessageType.COM_ERROR;
            }
            finally
            {
                _committing = false;
                UpdateRemoveButton();
                UpdateApplyButtonEnabled();
            }
        }

        private static string FailureDetail(MessageType resp, string lockedKey, string staleKey, string genericKey) => resp switch
        {
            MessageType.RESPONSE_LOCKED => Loc.T(lockedKey),
            MessageType.RESPONSE_STALE_CHANGESET => Loc.T(staleKey),
            _ => Loc.T(genericKey, resp),
        };

        private Task ShowResultAsync(string title, string body)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = body,
                CloseButtonText = Loc.T(LocKeys.Common.Ok),
            };

            return TryShowDialogAsync(dialog, title, body);
        }

        /// <summary>The single ContentDialog.ShowAsync() call in this file: WinUI allows only one
        /// open ContentDialog per XamlRoot at a time, and a second ShowAsync() while one is already
        /// open throws InvalidOperationException instead of queuing or showing anything. Nothing
        /// here has a process-wide UnhandledException handler backstopping its async void entry
        /// points (deliberately - see the final-review fix wave), so an unguarded throw is a hard
        /// crash, not a swallowed error.
        ///
        /// Returns null - never a ContentDialogResult - when the dialog could not be shown at all,
        /// so a caller can tell that apart from a genuine None (the user pressed Close). Both
        /// wrappers below refuse-by-default on null: no confirmation, no pick, no commit.</summary>
        private static async Task<ContentDialogResult?> ShowDialogOrNullAsync(ContentDialog dialog)
        {
            try
            {
                return await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>The page-instance wrapper around ShowDialogOrNullAsync: adds the InfoBar
        /// fallback (same Informational-severity pattern ShowResultAsync originated), a degraded but
        /// still honest way for the outcome to reach the user instead of nothing happening at
        /// all.</summary>
        private async Task<ContentDialogResult> TryShowDialogAsync(ContentDialog dialog, string fallbackTitle, string fallbackMessage)
        {
            var result = await ShowDialogOrNullAsync(dialog);
            if (result is null)
            {
                ShowNotice(InfoBarSeverity.Informational, fallbackTitle, fallbackMessage);
                return ContentDialogResult.None;
            }

            return result.Value;
        }

        /// <summary>Passed as PickFromListAsync's onDialogBlocked when this page shows the picker
        /// itself, so the static helper can still reach this instance's InfoBar.</summary>
        private void ShowDialogBlockedNotice(string title, string message)
            => ShowNotice(InfoBarSeverity.Informational, title, message);

        private void SetBusy(bool busy)
        {
            _busy = busy;
            Busy.IsActive = busy;
            RefreshButton.IsEnabled = !busy;
        }

        private void ShowNotice(InfoBarSeverity severity, string title, string message)
        {
            Notice.Severity = severity;
            Notice.Title = title;
            Notice.Message = message;
            Notice.IsOpen = true;
        }
    }
}
