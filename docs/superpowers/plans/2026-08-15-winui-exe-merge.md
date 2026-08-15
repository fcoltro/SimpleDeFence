# WinUI exe-merge and WinForms retirement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fold `SimpleDeFence.UI` (WinUI 3) into `SimpleDeFence.exe` as its only GUI, porting the tray icon, DevelTool, "add folder" bulk-exception flow, global hotkeys, and update-checking onto WinUI, then deleting every WinForms screen this makes redundant.

**Architecture:** `SimpleDeFence.csproj` gets a `ProjectReference` to `SimpleDeFence.UI.csproj` (which keeps its own `OutputType=WinExe`/`UseWinUI=true` shape) plus a small bootstrap entry point; `Program.cs`'s mode dispatch calls into WinUI instead of `Application.Run(new SimpleDeFenceController(...))`. Each WinForms-only capability (tray, DevelTool, folder-add, hotkeys, update-checking) gets ported to WinUI as its own task, verified independently via the existing `SimpleDeFence.UI.exe --sample-data` dev harness before the final task wires the merged exe's dispatch and deletes the WinForms code it replaces.

**Tech Stack:** C#, .NET 10, WinForms (being retired), WinUI 3 / Windows App SDK, `H.NotifyIcon.WinUI`, xunit.

**Spec:** `docs/superpowers/specs/2026-08-15-winui-exe-merge-design.md`

## Global Constraints

- **End state: WinUI becomes the only GUI.** No transitional fallback flag. WinForms is deleted once real-machine verification confirms parity (Task 8), not left in the tree as dead code.
- **No behavior change beyond the explicitly-decided ones.** Every port preserves existing behavior exactly, except two named, deliberate fixes bundled into rewrites that already touch those lines: `Thread.Abort()` → proper `CancellationToken` cancellation (Task 6), and the two settings that only ride along with UI ports (Task 7) rather than getting new functionality of their own. One more exception, structural rather than a port: `StartController` is shared code between `Controller` and `SelfHosted` launch modes (`Program.cs`), so Task 1's rewrite switches both to WinUI together — not a separate decision, since `SelfHosted` was always going to end up on WinUI once WinForms is deleted (Task 8).
- **`AuthAsServer` is never touched.** It keeps comparing the running exe's own path to itself; nothing in this plan changes what exe SimpleDeFence ships as.
- **Every WinRT picker uses the existing pattern**: `WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)` + `WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd)`, matching `RulesPage.xaml.cs`'s `AddPickExecutable_Click`.
- **Every `ContentDialog.ShowAsync()` call routes through a "only one dialog per `XamlRoot`" guard**, matching `RulesPage.xaml.cs`'s `TryShowDialogAsync`/`ShowResultAsync` pattern — copy that pattern into any new page that shows dialogs, don't call `ShowAsync()` directly.
- **New localized strings go into `SimpleDeFence.Core/Localization/LocKeys.cs` (typed constants) and `Strings.en.json` (English text).** Other locales are not translated as part of this plan — matching this repo's existing convention of shipping English first (see the partial-coverage satellite `.resx` files already in the WinForms side).
- **Open risk, verified first (Task 1):** whether `Microsoft.UI.Xaml.Application.Start(...)` builds and runs correctly when called from a hand-written `Main` in a project that isn't itself `UseWinUI=true`, referencing a `WinExe`-output library project. If this doesn't work as expected, stop and reconsider Decision 1 of the spec before continuing to any later task.

---

## Task 1: Exe-merge mechanism

**Files:**
- Create: `SimpleDeFence.UI/HostBootstrap.cs`
- Modify: `SimpleDeFence/SimpleDeFence.csproj`
- Modify: `SimpleDeFence/Program.cs`

**Interfaces:**
- Produces: `SimpleDeFence.UI.HostBootstrap.RunAsControllerGui(string[] args)` — every later task that needs the merged exe to launch WinUI's controller shell calls this.

Why: this is the spec's own flagged "open risk" — prove the mechanism works before investing in five more tasks that assume it does.

- [ ] **Step 1: Add the `ProjectReference` and `Microsoft.WindowsAppSDK` package to `SimpleDeFence.csproj`**

In `SimpleDeFence/SimpleDeFence.csproj`, add to the existing `<ItemGroup>` that already has `System.Management`/`System.ServiceProcess.ServiceController`:

```xml
  <ItemGroup>
    <PackageReference Include="System.Management" Version="10.0.0" />
    <PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.10" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.250907003" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SimpleDeFence.UI\SimpleDeFence.UI.csproj" />
  </ItemGroup>
```

Use whatever `Microsoft.WindowsAppSDK` version `SimpleDeFence.UI.csproj` already pins (check its own `PackageReference` before assuming the version above — the two projects must agree, since they'll link into the same process).

- [ ] **Step 2: Write `HostBootstrap.RunAsControllerGui`**

Create `SimpleDeFence.UI/HostBootstrap.cs`:

```csharp
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
```

- [ ] **Step 3: Wire `Program.cs`'s `StartController` to call it**

In `SimpleDeFence/Program.cs`, replace:

```csharp
        private static int StartController(CmdLineArgs opts)
        {
            // Start controller application
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            do
            {
                RestartOnQuit = false;
                System.Windows.Forms.Application.Run(new SimpleDeFenceController(opts));
            } while (RestartOnQuit);
            return 0;
        }
```

with:

```csharp
        private static int StartController(CmdLineArgs opts)
        {
            // WinUI 3 controller shell (see docs/superpowers/specs/2026-08-15-winui-exe-merge-design.md).
            // opts/RestartOnQuit are WinForms-controller-only concepts - App.xaml.cs reads its own
            // command-line args directly (Environment.GetCommandLineArgs()), and WinUI has no
            // equivalent "restart the whole shell" action yet.
            SimpleDeFence.UI.HostBootstrap.RunAsControllerGui(Environment.GetCommandLineArgs());
            return 0;
        }
```

Note: `opts` becomes unused by this method's new body. Leave the parameter in place — an unused-parameter warning here is expected and fine; do not suppress it by deleting the parameter, since `StartUpMode.SelfHosted`'s case block also calls `StartController(opts)`.

**`SelfHosted` mode's runtime behavior changes too, intentionally.** `StartController` is the single method both `Controller` and `SelfHosted` modes call — `SelfHosted` starts an in-process service (`StartService(srv)`) and then calls `StartController(opts)` to give it a GUI, purely for local dev/test convenience (no separately-installed service needed). Once this step lands, `/selfhosted` launches the WinUI shell instead of the WinForms controller too — the same "known, intentional gap until later tasks" (no tray icon yet, etc.) applies there as well. This isn't an oversight: this plan's own end state deletes the WinForms controller outright (Task 8), so `SelfHosted` was always going to end up on WinUI eventually — this step is simply where that happens, as an unavoidable side effect of `StartController` being shared code, not a separate decision. It's also a net gain for this environment specifically: `/selfhosted` is the first way to manually verify the WinUI shell against a real, live, in-process service without a VM (Controller mode alone, launched directly, has no service to connect to here).

- [ ] **Step 4: Build**

Run: `dotnet build SimpleDeFence/SimpleDeFence.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors. If `Application.Start`/`WinRT.ComWrappersSupport` don't resolve, or resolve to a different signature than above, this is the plan's flagged open risk surfacing for real — fix against the actual compiler errors (the exact generic delegate shape `Application.Start` expects can vary slightly by WindowsAppSDK version) and note in the commit message what was needed, rather than treating a mismatch as a reason to abandon Decision 1.

- [ ] **Step 5: Launch and verify manually**

Run `SimpleDeFence.exe` directly (no arguments) from its build output directory, as a normal user (not admin — Controller mode doesn't require elevation).

Expected: the WinUI 3 shell (`MainWindow`, `NavigationView` with Connections/Rules/Settings) appears, showing the same "Not connected" state it shows today when launched via `SimpleDeFence.UI.exe` without `--sample-data` (no `SimpleDeFenceController` service pipe exists to connect to in this dev environment, matching the caveat already on record in ROADMAP.md for the WinUI shell's prior verification).

**Known, intentional gap until later tasks:** no tray icon, no global hotkeys, no "add folder" option, no "Check for updates now" — all still WinForms-only until Tasks 2–7 port them. This is expected, not a regression to chase down.

- [ ] **Step 6: Commit**

```bash
git add SimpleDeFence.UI/HostBootstrap.cs SimpleDeFence/SimpleDeFence.csproj SimpleDeFence/Program.cs
git commit -m "Wire SimpleDeFence.exe's Controller mode to launch the WinUI 3 shell"
```

---

## Task 2: Global hotkey component and `ClientSettings` reconciliation fields

**Files:**
- Create: `SimpleDeFence.UI/Services/WindowHotkeys.cs`
- Modify: `SimpleDeFence.Core/ClientSettings.cs`
- Modify: `SimpleDeFence.Tests/ClientSettingsTests.cs`
- Test: `SimpleDeFence.Tests/ClientSettingsTests.cs` (fields); `WindowHotkeys` itself is manual-only (needs a real window handle and real global keyboard state — not unit-testable, matching `Hotkey.cs`'s own untested status today)

**Interfaces:**
- Consumes: `App.MainWindow` (existing, `App.xaml.cs`)
- Produces: `WindowHotkeys` class — `RegisterHotkey(int id, Windows.System.VirtualKey key, uint modifiers, Action callback)`, `UnregisterHotkey(int id)`, `IDisposable`. Task 3 (tray) consumes both methods for the three whitelist-by-X shortcuts.
- Produces: `ClientSettings.Language` (`string`), `.AskForExceptionDetails` (`bool`), `.EnableGlobalHotkeys` (`bool`). Task 3 reads both booleans immediately (its tray menu/hotkey wiring needs them to exist even before Task 7 gives them a settings UI); Task 7 adds the UI to change them and `App.xaml.cs`'s language-on-launch wiring.

Why: `SimpleDeFence.Windows/Hotkey.cs` relies on `System.Windows.Forms.Application.AddMessageFilter`/`IMessageFilter` to intercept `WM_HOTKEY` — a mechanism specific to WinForms' message loop, with no WinUI equivalent. WinUI's `Microsoft.UI.Xaml.Window` exposes a real Win32 `HWND` (via `WinRT.Interop.WindowNative.GetWindowHandle`), so the same effect is achieved by subclassing that window's `WndProc` directly. The `ClientSettings` fields land here rather than in Task 7 because Task 3's tray code reads them directly — putting the fields in Task 7 (after Task 3) would make Task 3 reference class members that don't exist yet.

- [ ] **Step 1: Write the failing `ClientSettings` tests**

In `SimpleDeFence.Tests/ClientSettingsTests.cs`, add:

```csharp
        [Fact]
        public void Default_language_is_auto()
        {
            Assert.Equal("auto", new ClientSettings().Language);
        }

        [Fact]
        public void Default_ask_for_exception_details_is_false()
        {
            Assert.False(new ClientSettings().AskForExceptionDetails);
        }

        [Fact]
        public void Default_enable_global_hotkeys_is_true()
        {
            Assert.True(new ClientSettings().EnableGlobalHotkeys);
        }

        [Fact]
        public void New_fields_round_trip_through_serialization()
        {
            var original = new ClientSettings { Language = "pt-BR", AskForExceptionDetails = true, EnableGlobalHotkeys = false };
            var bytes = SerializationHelper.Serialize(original);
            var restored = SerializationHelper.Deserialize(bytes, new ClientSettings());

            Assert.Equal("pt-BR", restored.Language);
            Assert.True(restored.AskForExceptionDetails);
            Assert.False(restored.EnableGlobalHotkeys);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj --filter ClientSettingsTests`
Expected: FAIL — `ClientSettings` has no `Language`/`AskForExceptionDetails`/`EnableGlobalHotkeys` members yet.

- [ ] **Step 3: Add the fields**

In `SimpleDeFence.Core/ClientSettings.cs`, add after `UiTheme`:

```csharp
        [DataMember(EmitDefaultValue = false)]
        public string Language { get; set; } = "auto";

        [DataMember(EmitDefaultValue = false)]
        public bool AskForExceptionDetails { get; set; } = false;

        [DataMember(EmitDefaultValue = false)]
        public bool EnableGlobalHotkeys { get; set; } = true;
```

Defaults match `ControllerSettings`' own field defaults exactly (`Language = "auto"`, `AskForExceptionDetails = false`, `EnableGlobalHotkeys = true`), preserving existing out-of-box behavior even without a migration path.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj --filter ClientSettingsTests`
Expected: PASS, all (existing 2 + 4 new).

- [ ] **Step 5: Write `WindowHotkeys`**

Create `SimpleDeFence.UI/Services/WindowHotkeys.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Registers global (system-wide) hotkeys against a WinUI 3 window's HWND, by subclassing its
    /// WndProc to intercept WM_HOTKEY - the WinUI equivalent of what
    /// SimpleDeFence.Windows/Hotkey.cs does via WinForms' Application.AddMessageFilter, which has no
    /// WinUI counterpart. One instance per window; not thread-safe (all calls expected from the UI
    /// thread, matching how RegisterHotKey/UnregisterHotKey are themselves not thread-safe).
    /// </summary>
    public sealed class WindowHotkeys : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int GWLP_WNDPROC = -4;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly IntPtr _hwnd;
        private readonly IntPtr _previousWndProc;
        private readonly WndProcDelegate _newWndProcDelegate; // kept alive: see field comment below
        private readonly Dictionary<int, Action> _callbacks = new();
        private bool _disposed;

        public WindowHotkeys(Window window)
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            // The delegate passed to SetWindowLongPtr must be kept alive for the window's lifetime -
            // if it were a lambda with no field reference, the GC could collect it while native code
            // still holds the function pointer, corrupting the window's message handling.
            _newWndProcDelegate = WndProc;
            var newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_newWndProcDelegate);
            _previousWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, newWndProcPtr);
        }

        public void RegisterHotkey(int id, uint virtualKey, uint modifiers, Action callback)
        {
            if (!RegisterHotKey(_hwnd, id, modifiers, virtualKey))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            _callbacks[id] = callback;
        }

        public void UnregisterHotkey(int id)
        {
            _callbacks.Remove(id);
            UnregisterHotKey(_hwnd, id);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
                callback();

            return CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var id in new List<int>(_callbacks.Keys))
                UnregisterHotKey(_hwnd, id);
            _callbacks.Clear();

            // Restore the original WndProc before this object (and its delegate) can be collected.
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _previousWndProc);
        }
    }
}
```

`fsModifiers`/`vk` match `user32.dll`'s `RegisterHotKey` exactly as `SimpleDeFence.Windows/Hotkey.cs`'s `NativeMethods` already declares them (`MOD_ALT`/`MOD_CONTROL`/`MOD_SHIFT`/`MOD_WIN` bit values, a raw virtual-key code) — Task 3 passes the same modifier/key combinations `SimpleDeFenceController.cs`'s `ApplyControllerSettings`/`SetHotkey` already uses (`Keys.W`/`Keys.E`/`Keys.P` with Ctrl+Alt, per that method's existing call sites), translated from `System.Windows.Forms.Keys` to the raw Win32 virtual-key integer (they're numerically identical for letter keys — `Keys.W == 0x57 == VK_W`).

- [ ] **Step 6: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add SimpleDeFence.UI/Services/WindowHotkeys.cs SimpleDeFence.Core/ClientSettings.cs SimpleDeFence.Tests/ClientSettingsTests.cs
git commit -m "Add WindowHotkeys (WM_HOTKEY interception) and the ClientSettings fields it and the tray need"
```

---

## Task 3: Tray icon + full menu port

**Files:**
- Modify: `SimpleDeFence.UI/SimpleDeFence.UI.csproj`
- Create: `SimpleDeFence.UI/Services/TrayIconService.cs`
- Create: `SimpleDeFence.UI/PasswordPromptDialog.xaml`
- Create: `SimpleDeFence.UI/PasswordPromptDialog.xaml.cs`
- Modify: `SimpleDeFence.UI/App.xaml.cs`
- Modify: `SimpleDeFence.Core/Localization/LocKeys.cs`
- Modify: `SimpleDeFence.Core/Localization/Strings.en.json`
- Modify: `SimpleDeFence.UI/Assets/` (new icon files)

**Interfaces:**
- Consumes: `App.Firewall` (`IFirewallClient`), `App.MainWindow`, `WindowHotkeys` and `ClientSettings.AskForExceptionDetails`/`.EnableGlobalHotkeys` (all Task 2 — the fields exist by this task, just with no settings UI to change them until Task 7).
- Produces: `PasswordPromptDialog` — a reusable `ContentDialog` subclass with a `Password` (`string`) dependency-like property read after `ShowAsync()` returns `ContentDialogResult.Primary`. Task 8 (`SimpleDeFenceDoctor.Uninstall`) consumes this too.

Why: replicates `SimpleDeFenceController.cs`'s `Tray`/`TrayMenu` fields and their ~15 menu items on top of the already-working `IFirewallClient` abstraction, rather than re-deriving WinForms' own IPC-calling internals.

- [ ] **Step 1: Add the `H.NotifyIcon.WinUI` package**

In `SimpleDeFence.UI/SimpleDeFence.UI.csproj`, add to the existing `<ItemGroup>` with `Microsoft.WindowsAppSDK`:

```xml
    <PackageReference Include="H.NotifyIcon.WinUI" Version="2.4.1" />
```

- [ ] **Step 2: Copy the mode-icon assets**

Copy these five files from `SimpleDeFence/Resources/img/` to `SimpleDeFence.UI/Assets/TrayIcons/`, unchanged: `firewall.ico`, `shield_red_small.ico`, `shield_yellow_small.ico`, `shield_grey_small.ico`, `shield_blue_small.ico`. Set each one's `Build Action` to `Content` with `Copy if newer` (matching how `SimpleDeFence.UI/Assets/AppIcon.ico` is already packaged — check its existing `<ItemGroup>` entry in the `.csproj`, if any explicit one exists, and mirror it; WinUI's default globbing usually picks up `Assets/**` automatically without an explicit entry).

- [ ] **Step 3: Add the new localization keys**

In `SimpleDeFence.Core/Localization/LocKeys.cs`, add a new nested class after `Settings`:

```csharp
        public static class Tray
        {
            public const string ModeNormal = "tray.mode.normal";
            public const string ModeBlockAll = "tray.mode.blockAll";
            public const string ModeAllowOutgoing = "tray.mode.allowOutgoing";
            public const string ModeDisabled = "tray.mode.disabled";
            public const string ModeLearning = "tray.mode.learning";
            public const string Manage = "tray.manage";
            public const string Connections = "tray.connections";
            public const string Lock = "tray.lock";
            public const string Elevate = "tray.elevate";
            public const string AllowLocalSubnet = "tray.allowLocalSubnet";
            public const string EnableHostsBlocklist = "tray.enableHostsBlocklist";
            public const string WhitelistByExecutable = "tray.whitelistByExecutable";
            public const string WhitelistByProcess = "tray.whitelistByProcess";
            public const string WhitelistByWindow = "tray.whitelistByWindow";
            public const string Quit = "tray.quit";
            public const string UnlockTitle = "tray.unlock.title";
            public const string UnlockPasswordPlaceholder = "tray.unlock.passwordPlaceholder";
            public const string UnlockButton = "tray.unlock.button";
            public const string UnlockFailedTitle = "tray.unlock.failedTitle";
        }
```

In `SimpleDeFence.Core/Localization/Strings.en.json`, add the matching entries (following the existing flat-key-per-line JSON shape already used for `settings.*`/`rules.*` keys — open the file and match its exact formatting):

```json
  "tray.mode.normal": "Normal",
  "tray.mode.blockAll": "Block All",
  "tray.mode.allowOutgoing": "Allow Outgoing",
  "tray.mode.disabled": "Disabled",
  "tray.mode.learning": "Learning",
  "tray.manage": "Manage...",
  "tray.connections": "Connections...",
  "tray.lock": "Lock",
  "tray.elevate": "Run as Administrator",
  "tray.allowLocalSubnet": "Allow Local Subnet",
  "tray.enableHostsBlocklist": "Enable Hosts Blocklist",
  "tray.whitelistByExecutable": "Whitelist executable...",
  "tray.whitelistByProcess": "Whitelist running process...",
  "tray.whitelistByWindow": "Whitelist by window...",
  "tray.quit": "Quit",
  "tray.unlock.title": "Unlock SimpleDeFence",
  "tray.unlock.passwordPlaceholder": "Password",
  "tray.unlock.button": "Unlock",
  "tray.unlock.failedTitle": "Incorrect password"
```

Run `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj --filter LocKeysTests` to confirm the new keys and JSON entries stay in sync (this test already exists per `LocKeys.cs`'s own header comment — if its exact name differs, find it with `dotnet test --list-tests` first).
Expected: PASS.

- [ ] **Step 4: Write `PasswordPromptDialog`**

Create `SimpleDeFence.UI/PasswordPromptDialog.xaml`:

```xml
<ContentDialog
    x:Class="SimpleDeFence.UI.PasswordPromptDialog"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:loc="using:SimpleDeFence.UI.Services"
    PrimaryButtonText="{loc:Loc Key=tray.unlock.button}"
    CloseButtonText="{loc:Loc Key=common.cancel}"
    DefaultButton="Primary">
    <PasswordBox x:Name="PasswordInput"
                 PlaceholderText="{loc:Loc Key=tray.unlock.passwordPlaceholder}"
                 KeyDown="PasswordInput_KeyDown"/>
</ContentDialog>
```

Create `SimpleDeFence.UI/PasswordPromptDialog.xaml.cs`:

```csharp
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace SimpleDeFence.UI
{
    /// <summary>Shared unlock prompt: used by the tray's "Lock" action (this task) and
    /// SimpleDeFenceDoctor.Uninstall's unlock-before-stop flow (Task 8) - one component, two call
    /// sites, replacing WinForms' PasswordForm for both.</summary>
    public sealed partial class PasswordPromptDialog : ContentDialog
    {
        public string Password => PasswordInput.Password;

        public PasswordPromptDialog()
        {
            InitializeComponent();
        }

        private void PasswordInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
                Hide(); // ContentDialog's own Enter-triggers-primary-button behavior handles the rest
        }
    }
}
```

- [ ] **Step 5: Write `TrayIconService`**

Create `SimpleDeFence.UI/Services/TrayIconService.cs`:

```csharp
using System;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence.Localization;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Owns the tray icon and its context menu, replacing SimpleDeFenceController.cs's Tray/TrayMenu
    /// fields (see the net10 exe-merge design doc, Decision 3). Built entirely on top of the
    /// already-working IFirewallClient abstraction - no WinForms/IPC internals ported here.
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private readonly TaskbarIcon _icon;
        private readonly WindowHotkeys _hotkeys;
        private bool _disposed;

        private const int HOTKEY_EXECUTABLE = 1;
        private const int HOTKEY_PROCESS = 2;
        private const int HOTKEY_WINDOW = 3;
        private const uint MOD_CONTROL = 0x2;
        private const uint MOD_ALT = 0x1;
        private const uint VK_E = 0x45;
        private const uint VK_P = 0x50;
        private const uint VK_W = 0x57;

        public TrayIconService()
        {
            _icon = new TaskbarIcon
            {
                IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appx:///Assets/TrayIcons/firewall.ico")),
                ContextMenuMode = ContextMenuMode.SecondWindow,
            };
            _icon.ContextFlyout = BuildMenu();
            _icon.ForceCreate();

            _hotkeys = new WindowHotkeys(App.MainWindow!);
            ApplyHotkeySetting(ClientSettings.Load().EnableGlobalHotkeys);

            if (App.Firewall != null)
                App.Firewall.Changed += (_, _) => UpdateModeIcon();
            UpdateModeIcon();
        }

        /// <summary>Called from the Settings page when the user flips "Enable global hotkeys" -
        /// registers/unregisters all three at once, matching SimpleDeFenceController's
        /// ApplyControllerSettings/SetHotkey all-or-nothing behavior for this toggle.</summary>
        public void ApplyHotkeySetting(bool enabled)
        {
            if (enabled)
            {
                _hotkeys.RegisterHotkey(HOTKEY_EXECUTABLE, VK_E, MOD_CONTROL | MOD_ALT, () => _ = WhitelistByExecutableAsync());
                _hotkeys.RegisterHotkey(HOTKEY_PROCESS, VK_P, MOD_CONTROL | MOD_ALT, () => _ = WhitelistByProcessAsync());
                _hotkeys.RegisterHotkey(HOTKEY_WINDOW, VK_W, MOD_CONTROL | MOD_ALT, () => _ = WhitelistByWindowAsync());
            }
            else
            {
                _hotkeys.UnregisterHotkey(HOTKEY_EXECUTABLE);
                _hotkeys.UnregisterHotkey(HOTKEY_PROCESS);
                _hotkeys.UnregisterHotkey(HOTKEY_WINDOW);
            }
        }

        private MenuFlyout BuildMenu()
        {
            var menu = new MenuFlyout();

            var modeNormal = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeNormal) };
            modeNormal.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.Normal);
            var modeBlockAll = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeBlockAll) };
            modeBlockAll.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.BlockAll);
            var modeAllowOutgoing = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeAllowOutgoing) };
            modeAllowOutgoing.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.AllowOutgoing);
            var modeDisabled = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeDisabled) };
            modeDisabled.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.Disabled);
            var modeLearning = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.ModeLearning) };
            modeLearning.Click += (_, _) => _ = SwitchModeAsync(FirewallMode.Learning);
            var modeSub = new MenuFlyoutSubItem { Text = Loc.T(LocKeys.Nav.ModeChip) };
            modeSub.Items.Add(modeNormal);
            modeSub.Items.Add(modeBlockAll);
            modeSub.Items.Add(modeAllowOutgoing);
            modeSub.Items.Add(modeDisabled);
            modeSub.Items.Add(modeLearning);
            menu.Items.Add(modeSub);

            var manage = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Manage) };
            manage.Click += (_, _) => ShowAndNavigate(typeof(Pages.RulesPage));
            menu.Items.Add(manage);

            var connections = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Connections) };
            connections.Click += (_, _) => ShowAndNavigate(typeof(Pages.ConnectionsPage));
            menu.Items.Add(connections);

            menu.Items.Add(new MenuFlyoutSeparator());

            var lockItem = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Lock) };
            lockItem.Click += (_, _) => _ = LockAsync();
            menu.Items.Add(lockItem);

            var elevate = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Elevate) };
            elevate.Click += (_, _) => ElevateSelf();
            menu.Items.Add(elevate);

            menu.Items.Add(new MenuFlyoutSeparator());

            var allowLocalSubnet = new ToggleMenuFlyoutItem { Text = Loc.T(LocKeys.Tray.AllowLocalSubnet) };
            allowLocalSubnet.Click += (_, _) => _ = ToggleAllowLocalSubnetAsync(allowLocalSubnet.IsChecked);
            menu.Items.Add(allowLocalSubnet);

            var hostsBlocklist = new ToggleMenuFlyoutItem { Text = Loc.T(LocKeys.Tray.EnableHostsBlocklist) };
            hostsBlocklist.Click += (_, _) => _ = ToggleHostsBlocklistAsync(hostsBlocklist.IsChecked);
            menu.Items.Add(hostsBlocklist);

            menu.Items.Add(new MenuFlyoutSeparator());

            var whitelistExe = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.WhitelistByExecutable) };
            whitelistExe.Click += (_, _) => _ = WhitelistByExecutableAsync();
            menu.Items.Add(whitelistExe);

            var whitelistProc = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.WhitelistByProcess) };
            whitelistProc.Click += (_, _) => _ = WhitelistByProcessAsync();
            menu.Items.Add(whitelistProc);

            var whitelistWin = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.WhitelistByWindow) };
            whitelistWin.Click += (_, _) => _ = WhitelistByWindowAsync();
            menu.Items.Add(whitelistWin);

            menu.Items.Add(new MenuFlyoutSeparator());

            var quit = new MenuFlyoutItem { Text = Loc.T(LocKeys.Tray.Quit) };
            quit.Click += (_, _) => App.Current.Exit();
            menu.Items.Add(quit);

            return menu;
        }

        private void UpdateModeIcon()
        {
            var mode = App.Firewall?.State?.Mode ?? FirewallMode.Unknown;
            var iconFile = mode switch
            {
                FirewallMode.Normal => "firewall.ico",
                FirewallMode.AllowOutgoing => "shield_red_small.ico",
                FirewallMode.BlockAll => "shield_yellow_small.ico",
                FirewallMode.Disabled => "shield_grey_small.ico",
                FirewallMode.Learning => "shield_blue_small.ico",
                _ => "shield_grey_small.ico",
            };
            _icon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri($"ms-appx:///Assets/TrayIcons/{iconFile}"));
            _icon.ToolTipText = "SimpleDeFence";
        }

        private static async System.Threading.Tasks.Task SwitchModeAsync(FirewallMode mode)
        {
            if (App.Firewall != null)
                await App.Firewall.SwitchModeAsync(mode);
        }

        private static async System.Threading.Tasks.Task LockAsync()
        {
            if (App.Firewall != null)
                await App.Firewall.LockAsync();
        }

        private static void ElevateSelf()
        {
            // Matches SimpleDeFenceController.cs's mnuElevate_Click: relaunch self elevated via the
            // existing /install-style ShellExecute pattern in SimpleDeFenceDoctor - Task 8 confirms
            // this call site is unaffected by the WinForms deletion (SimpleDeFenceDoctor stays).
            SimpleDeFence.Utils.StartProcess(SimpleDeFence.Utils.ExecutablePath, string.Empty, true);
        }

        private static async System.Threading.Tasks.Task ToggleAllowLocalSubnetAsync(bool value)
        {
            if (App.Firewall != null)
                await App.Firewall.CommitConfigChangesAsync(c => c.ActiveProfile.AllowLocalSubnet = value);
        }

        private static async System.Threading.Tasks.Task ToggleHostsBlocklistAsync(bool value)
        {
            if (App.Firewall != null)
                await App.Firewall.CommitConfigChangesAsync(c => c.Blocklists.EnableBlocklists = value);
        }

        private static async System.Threading.Tasks.Task WhitelistByExecutableAsync()
        {
            // Task 4 gives RulesPage its own folder/executable pickers; the tray's quick-add shares
            // that same picker+commit logic. Wired here once Task 4 exposes it as a callable helper
            // (Pages.RulesPage.QuickAddExecutableAsync) rather than duplicating the picker code.
            await Pages.RulesPage.QuickAddExecutableAsync(AskForExceptionDetails());
        }

        private static async System.Threading.Tasks.Task WhitelistByProcessAsync()
        {
            await Pages.RulesPage.QuickAddProcessAsync(AskForExceptionDetails());
        }

        private static async System.Threading.Tasks.Task WhitelistByWindowAsync()
        {
            await Pages.RulesPage.QuickAddWindowAsync(AskForExceptionDetails());
        }

        private static bool AskForExceptionDetails() => ClientSettings.Load().AskForExceptionDetails;

        private static void ShowAndNavigate(Type pageType)
        {
            App.MainWindow?.Activate();
            if (App.MainWindow?.Content is Frame frame)
                frame.Navigate(pageType);
            else if (App.MainWindow is MainWindow mw)
                mw.NavigateTo(pageType);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _hotkeys.Dispose();
            _icon.Dispose();
        }
    }
}
```

Two call sites (`ShowAndNavigate`'s `MainWindow.NavigateTo`, `RulesPage.QuickAddExecutableAsync`/`QuickAddProcessAsync`/`QuickAddWindowAsync`) reference members that don't exist yet — `MainWindow`'s actual navigation API and `RulesPage`'s current picker methods are private (per Task 4's grep of `RulesPage.xaml.cs`: `AddPickExecutable_Click` is `private`). Before writing this file for real, open `MainWindow.xaml.cs` to find its actual page-navigation method name (adjust `ShowAndNavigate` to call it, whatever it's actually called), and treat exposing `QuickAddExecutableAsync`/`QuickAddProcessAsync`/`QuickAddWindowAsync` as `internal static` wrappers around each existing private picker method as part of *this* task's Step 5 (a small, mechanical visibility change in `RulesPage.xaml.cs` — extract each handler's body after the picker result into a reusable method, call it from both the existing `Click` handler and this new wrapper).

- [ ] **Step 6: Instantiate the tray icon from `App`**

In `SimpleDeFence.UI/App.xaml.cs`, add a field and instantiate it in `OnLaunched`, after `m_window.Activate()`:

```csharp
        private static TrayIconService? _tray;
```

```csharp
            m_window.Activate();
            _tray = new TrayIconService();
```

- [ ] **Step 7: Manual verification**

Run `SimpleDeFence.UI.exe --sample-data` (the standalone dev harness — no need to touch the merged exe for this).
Expected: a tray icon appears; right-clicking shows the full menu; clicking each mode item, Manage, Connections, Allow Local Subnet, Enable Hosts Blocklist doesn't crash (sample data mode won't actually commit changes, but the menu must open and dispatch without throwing). Confirm Ctrl+Alt+E/P/W don't crash either.

- [ ] **Step 8: Commit**

```bash
git add SimpleDeFence.UI/SimpleDeFence.UI.csproj SimpleDeFence.UI/Services/TrayIconService.cs SimpleDeFence.UI/PasswordPromptDialog.xaml SimpleDeFence.UI/PasswordPromptDialog.xaml.cs SimpleDeFence.UI/App.xaml.cs SimpleDeFence.UI/Assets/TrayIcons SimpleDeFence.Core/Localization/LocKeys.cs SimpleDeFence.Core/Localization/Strings.en.json SimpleDeFence.UI/Pages/RulesPage.xaml.cs
git commit -m "Port the tray icon and its full menu to WinUI (H.NotifyIcon.WinUI)"
```

---

## Task 4: "Add folder" bulk-exception flow

**Files:**
- Modify: `SimpleDeFence.Core/Database/AppDatabase.cs`
- Modify: `SimpleDeFence/DatabaseClasses/AppDatabase.cs`
- Modify: `SimpleDeFence.UI/Pages/RulesPage.xaml`
- Modify: `SimpleDeFence.UI/Pages/RulesPage.xaml.cs`
- Modify: `SimpleDeFence.Core/Localization/LocKeys.cs`
- Modify: `SimpleDeFence.Core/Localization/Strings.en.json`
- Test: `SimpleDeFence.Tests/AppDatabaseTests.cs` (new file)

**Interfaces:**
- Produces: `AppDatabase.GetExceptionsForApp(ExceptionSubject, out Application?)` (Core, public) — Task 3's `TrayIconService` doesn't need this, but the WinForms wrapper (kept until Task 8) and `SimpleDeFenceService.cs` both do.

Why: closes the exact gap the earlier ROADMAP entry flagged ("smarter 'Allow this app' suggestions ported from `AppDatabase.GetExceptionsForApp`") — spec Decision 4.

- [ ] **Step 1: Write the failing test for the Core method**

Create `SimpleDeFence.Tests/AppDatabaseTests.cs`:

```csharp
using System.Collections.Generic;
using SimpleDeFence.DatabaseClasses;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class AppDatabaseTests
    {
        [Fact]
        public void GetExceptionsForApp_returns_single_allow_all_exception_for_unrecognized_executable()
        {
            var db = new AppDatabase(); // empty KnownApplications
            var subject = new ExecutableSubject(@"C:\Games\SomeGame\game.exe");

            var exceptions = db.GetExceptionsForApp(subject, out var app);

            Assert.Null(app);
            var exception = Assert.Single(exceptions);
            Assert.Equal(subject, exception.Subject);
            Assert.IsType<TcpUdpPolicy>(exception.Policy);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj --filter GetExceptionsForApp_returns_single_allow_all_exception_for_unrecognized_executable`
Expected: FAIL — `AppDatabase` has no member `GetExceptionsForApp` yet (compile error, which counts as a failing test here).

- [ ] **Step 3: Move `TryGetApp`/`GetExceptionsForApp`'s prompt-free logic to Core**

In `SimpleDeFence.Core/Database/AppDatabase.cs`, add (after `FastSearchMachineForKnownApps`, before `GetJsonTypeInfo`):

```csharp
        internal Application? TryGetApp(ExecutableSubject fromSubject, out FirewallExceptionV3? fwex, bool matchSpecial)
        {
            foreach (var app in KnownApplications)
            {
                if (!matchSpecial && app.HasFlag("TWUI:Special"))
                    continue;

                foreach (var id in app.Components)
                {
                    if (id.DoesExecutableSatisfy(fromSubject))
                    {
                        fwex = id.InstantiateException(fromSubject);
                        return app;
                    }
                }
            }

            fwex = null;
            return null;
        }

        /// <summary>The prompt-free half of what was SimpleDeFence/DatabaseClasses/AppDatabase.cs's
        /// GetExceptionsForApp(subject, guiPrompt, out app) - moved here because this half has no
        /// WinForms dependency (unlike the guiPrompt=true path, which shows a
        /// Microsoft.Samples.TaskDialog prompt and stays in that WinForms-only partial as a thin
        /// wrapper around this method).</summary>
        public List<FirewallExceptionV3> GetExceptionsForApp(ExceptionSubject fromSubject, out Application? app)
        {
            app = null;
            var exceptions = new List<FirewallExceptionV3>();

            if (fromSubject is AppContainerSubject)
            {
                exceptions.Add(new FirewallExceptionV3(fromSubject, new TcpUdpPolicy(true)));
                return exceptions;
            }
            else if (fromSubject is ExecutableSubject exeSubject)
            {
                app = TryGetApp(exeSubject, out _, false);
                if (app == null)
                {
                    exceptions.Add(new FirewallExceptionV3(exeSubject, new TcpUdpPolicy(true)));
                    return exceptions;
                }

                string? pathHint = System.IO.Path.GetDirectoryName(exeSubject.ExecutablePath);
                foreach (SubjectIdentity id in app.Components)
                {
                    List<ExceptionSubject> foundSubjects = id.SearchForFile(pathHint);
                    foreach (ExceptionSubject subject in foundSubjects)
                    {
                        var tmp = id.InstantiateException(subject);
                        if (fromSubject.Equals(subject))
                            exceptions.Insert(0, tmp);
                        else
                            exceptions.Add(tmp);
                    }
                }
            }
            else
            {
                throw new NotImplementedException();
            }

            return exceptions;
        }
```

Add `using System;` to this file's existing `using` block if not already present (needed for `NotImplementedException`).

- [ ] **Step 4: Rewrite the WinForms-only partial as a thin wrapper**

In `SimpleDeFence/DatabaseClasses/AppDatabase.cs`, replace the entire `TryGetApp` method and the body of `GetExceptionsForApp` — keep the method signature exactly as-is (every existing WinForms call site depends on it unchanged), replacing:

```csharp
        internal Application? TryGetApp(ExecutableSubject fromSubject, out FirewallExceptionV3? fwex, bool matchSpecial)
        {
            foreach (var app in KnownApplications)
            {
                if (!matchSpecial && app.HasFlag("TWUI:Special"))
                    continue;

                foreach (var id in app.Components)
                {
                    if (id.DoesExecutableSatisfy(fromSubject))
                    {
                        fwex = id.InstantiateException(fromSubject);
                        return app;
                    }
                }
            }

            fwex = null;
            return null;
        }

        internal List<FirewallExceptionV3> GetExceptionsForApp(ExceptionSubject fromSubject, bool guiPrompt, out Application? app)
        {
            app = null;
            var exceptions = new List<FirewallExceptionV3>();

            if (fromSubject is AppContainerSubject)
            {
                exceptions.Add(new FirewallExceptionV3(fromSubject, new TcpUdpPolicy(true)));
                return exceptions;
            }
            else if (fromSubject is ExecutableSubject exeSubject)
            {
                app = TryGetApp(exeSubject, out FirewallExceptionV3? _, false);
                if (app == null)
                {
                    exceptions.Add(new FirewallExceptionV3(exeSubject, new TcpUdpPolicy(true)));
                    return exceptions;
                }

                string pathHint = System.IO.Path.GetDirectoryName(exeSubject.ExecutablePath);
                foreach (SubjectIdentity id in app.Components)
                {
                    List<ExceptionSubject> foundSubjects = id.SearchForFile(pathHint);
                    foreach (ExceptionSubject subject in foundSubjects)
                    {
                        var tmp = id.InstantiateException(subject);
                        if (fromSubject.Equals(subject))
                            exceptions.Insert(0, tmp);
                        else
                            exceptions.Add(tmp);
                    }
                }

                if ((exceptions.Count > 1) && guiPrompt)
                {
```

with:

```csharp
        internal List<FirewallExceptionV3> GetExceptionsForApp(ExceptionSubject fromSubject, bool guiPrompt, out Application? app)
        {
            var exceptions = GetExceptionsForApp(fromSubject, out app);

            if ((exceptions.Count > 1) && guiPrompt && app is not null && fromSubject is ExecutableSubject exeSubject)
            {
```

Everything from the original `if ((exceptions.Count > 1) && guiPrompt)` block's body onward (the `TaskDialog` construction and its `switch` on button IDs, through the final `return exceptions;`) stays exactly as it was — only the two method signatures and the code that built `exceptions`/`app` before that `if` are replaced. Leave the closing braces matching (removing one extra `else { throw new NotImplementedException(); }` block along with the deleted `TryGetApp` call and the deleted `else if`/`else` structure — the new one-line body already produced the equivalent `exceptions`/`app` via the Core call, so the old `else`/final `else { throw }` become unreachable and should be deleted, not kept dead).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj --filter GetExceptionsForApp_returns_single_allow_all_exception_for_unrecognized_executable`
Expected: PASS.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS, 58/58 (53 original + Task 2's 4 new `ClientSettings` tests + this task's 1 new test).

- [ ] **Step 7: Build the WinForms side to confirm the wrapper still compiles**

Run: `dotnet build SimpleDeFence/SimpleDeFence.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors — confirms every existing WinForms call site (`SimpleDeFenceController.cs`, `ApplicationExceptionForm.cs`, `SettingsForm.cs`, `SimpleDeFenceService.cs`) still resolves against the unchanged wrapper signature.

- [ ] **Step 8: Add the folder-add localization keys**

In `SimpleDeFence.Core/Localization/LocKeys.cs`, add to the existing `Rules` nested class (find it — it already has `DetailApplySuccess` etc. from earlier in the file):

```csharp
            public const string AddPickFolder = "rules.addPickFolder";
```

In `SimpleDeFence.Core/Localization/Strings.en.json`:

```json
  "rules.addPickFolder": "Pick folder...",
```

- [ ] **Step 9: Add the "Add folder" option to Rules' Add button**

In `SimpleDeFence.UI/Pages/RulesPage.xaml`, add a fifth `MenuFlyoutItem` to the existing `SplitButton.Flyout`:

```xml
                        <MenuFlyoutItem Text="{loc:Loc Key=rules.addPickUwp}" Click="AddPickUwp_Click"/>
                        <MenuFlyoutItem Text="{loc:Loc Key=rules.addPickFolder}" Click="AddPickFolder_Click"/>
```

(inserting the new line immediately after the existing `addPickUwp` item, before the flyout's closing tag.)

- [ ] **Step 10: Write `AddPickFolder_Click`**

In `SimpleDeFence.UI/Pages/RulesPage.xaml.cs`, add near the other `AddPick*_Click` handlers:

```csharp
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
```

This reuses `CommitAsync`/`ShowResultAsync`/`FailureDetail` exactly as `CommitAddAsync` already does — no new commit-path plumbing.

- [ ] **Step 11: Extract the quick-add helpers Task 3's `TrayIconService` needs**

In the same file, change `AddPickExecutable_Click`, `AddPickProcess_Click` (find it near `AddPickWindow_Click`), and `AddPickWindow_Click` (find it near line 378's `AddPickUwp_Click`) to delegate to new `internal static` methods that Task 3 already references as `RulesPage.QuickAddExecutableAsync`/`QuickAddProcessAsync`/`QuickAddWindowAsync`. For each handler, extract everything from after the picker result is obtained (the `await CommitAddAsync(...)` call and whatever precedes it that isn't picker-specific UI) into a method taking an `askForDetails` parameter — since none of the existing three currently branch on "ask for details" (they always commit immediately with the database default), the `askForDetails` parameter is new: when `true`, show a confirmation dialog with the resolved exceptions list before committing (matching WinForms' `ApplicationExceptionForm` edit-before-commit behavior); when `false` (today's only behavior), commit immediately as before. Keep the exact shape of this decision self-contained to this step — do not change `CommitAddAsync`'s own signature, only wrap it.

Because this step's exact extraction depends on each handler's current full body (only partially quoted earlier in this plan), read `RulesPage.xaml.cs`'s `AddPickProcess_Click` and `AddPickWindow_Click` in full before writing the extraction, and match the same `_addBusy`/`try`/`finally` shape `AddPickExecutable_Click` already uses.

- [ ] **Step 12: Manual verification**

Run `SimpleDeFence.UI.exe --sample-data`, open Rules, click the Add button's "Pick folder..." option, choose a folder with a handful of `.exe` files (e.g. `C:\Windows\System32` for a quick smoke test — expect many results).
Expected: no crash; a result dialog reports success or a clear failure reason (sample-data mode may report failure since there's no real service to commit against — that's expected, matching every other Add flow's behavior in sample-data mode).

- [ ] **Step 13: Commit**

```bash
git add SimpleDeFence.Core/Database/AppDatabase.cs SimpleDeFence/DatabaseClasses/AppDatabase.cs SimpleDeFence.UI/Pages/RulesPage.xaml SimpleDeFence.UI/Pages/RulesPage.xaml.cs SimpleDeFence.Core/Localization/LocKeys.cs SimpleDeFence.Core/Localization/Strings.en.json SimpleDeFence.Tests/AppDatabaseTests.cs
git commit -m "Port folder-based bulk-exception add to Rules; move GetExceptionsForApp's prompt-free path to Core"
```

---

## Task 5: DevelTool port

**Files:**
- Create: `SimpleDeFence.UI/DevelToolWindow.xaml`
- Create: `SimpleDeFence.UI/DevelToolWindow.xaml.cs`
- Modify: `SimpleDeFence.UI/HostBootstrap.cs`
- Modify: `SimpleDeFence/Program.cs`

**Interfaces:**
- Produces: `SimpleDeFence.UI.HostBootstrap.RunAsDevelTool(string[] args)`.

Why: mechanical port per spec Decision 5 — every handler calls the same existing backend classes unchanged; only the UI shell changes. This tool is never end-user-facing (its own constructor already shows a "not for end-users" warning), so it does not need NavigationView/localization polish — a single `Window` with a `Pivot`-style tabbed layout mirroring today's sections is sufficient, and its strings stay English-only (no `LocKeys` entries), matching this plan's own localization scope decision.

- [ ] **Step 1: Write `DevelToolWindow.xaml`**

Create `SimpleDeFence.UI/DevelToolWindow.xaml` with five `PivotItem`-equivalent sections. WinUI 3 has no `Pivot` control (that's UWP-only); use a `NavigationView` in `LeftCompact` mode or a plain `TabView`. Use `TabView` — simpler for an internal tool with a fixed, small tab count:

```xml
<Window
    x:Class="SimpleDeFence.UI.DevelToolWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <TabView x:Name="Tabs" IsAddTabButtonVisible="False">
        <TabViewItem Header="Associations" IsClosable="False">
            <StackPanel Padding="12" Spacing="8">
                <TextBox x:Name="AssocExePath" PlaceholderText="Executable path"/>
                <Button Content="Browse..." Click="AssocBrowse_Click"/>
                <Button Content="Create" Click="AssocCreate_Click"/>
                <TextBox x:Name="AssocResult" AcceptsReturn="True" TextWrapping="Wrap" Height="200" IsReadOnly="True"/>
            </StackPanel>
        </TabViewItem>
        <TabViewItem Header="Collections" IsClosable="False">
            <StackPanel Padding="12" Spacing="8">
                <TextBox x:Name="DbFolderPath" PlaceholderText="Input database folder"/>
                <Button Content="Browse..." Click="ProfileFolderBrowse_Click"/>
                <TextBox x:Name="AssocOutputPath" PlaceholderText="Output folder"/>
                <Button Content="Browse..." Click="AssocOutputBrowse_Click"/>
                <Button Content="Create collections" Click="CollectionsCreate_Click"/>
            </StackPanel>
        </TabViewItem>
        <TabViewItem Header="Update package" IsClosable="False">
            <StackPanel Padding="12" Spacing="8">
                <TextBox x:Name="UpdateInstallerProjectDir" PlaceholderText="Installer project directory"/>
                <Button Content="Browse..." Click="UpdateInstallerBrowse_Click"/>
                <TextBox x:Name="UpdateOutput" PlaceholderText="Output folder"/>
                <Button Content="Browse..." Click="UpdateOutputBrowse_Click"/>
                <TextBox x:Name="UpdateURL" PlaceholderText="Update base URL"/>
                <Button Content="Create update package" Click="UpdateCreate_Click"/>
            </StackPanel>
        </TabViewItem>
        <TabViewItem Header="Resx optimizer" IsClosable="False">
            <StackPanel Padding="12" Spacing="8">
                <Button Content="Add primaries..." Click="AddPrimaries_Click"/>
                <Button Content="Clear" Click="Clear_Click"/>
                <ListView x:Name="ListPrimaries" SelectionChanged="ListPrimaries_SelectionChanged" Height="150"/>
                <ListView x:Name="ListSatellites" Height="150"/>
                <TextBox x:Name="OptimizeOutputPath" PlaceholderText="Output folder"/>
                <Button Content="Optimize" Click="Optimize_Click"/>
            </StackPanel>
        </TabViewItem>
        <TabViewItem Header="Batch sign" IsClosable="False">
            <StackPanel Padding="12" Spacing="8">
                <TextBox x:Name="CertPath" PlaceholderText="Certificate path"/>
                <Button Content="Browse..." Click="CertBrowse_Click"/>
                <TextBox x:Name="SignDirPath" PlaceholderText="Directory to sign"/>
                <Button Content="Browse..." Click="SignDirBrowse_Click"/>
                <TextBox x:Name="SigntoolPath" PlaceholderText="signtool.exe path"/>
                <Button Content="Browse..." Click="SigntoolBrowse_Click"/>
                <TextBox x:Name="TimestampServer" PlaceholderText="Timestamp server URL"/>
                <Button x:Name="BatchSignButton" Content="Sign" Click="BatchSign_Click"/>
            </StackPanel>
        </TabViewItem>
    </TabView>
</Window>
```

- [ ] **Step 2: Write `DevelToolWindow.xaml.cs`**

Create `SimpleDeFence.UI/DevelToolWindow.xaml.cs`, porting each WinForms handler's body verbatim except for the picker calls (WinRT `FileOpenPicker`/`FolderPicker` instead of `OpenFileDialog`/`FolderBrowserDialog`, matching `RulesPage.xaml.cs`'s picker pattern) and message boxes (`ContentDialog` instead of `MessageBox.Show`):

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
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

        public async System.Threading.Tasks.Task ShowStartupWarningAsync()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Warning: Not for users!",
                Content = "This tool is not meant for end-users. Only use this tool when instructed to do so by the application developer.",
                CloseButtonText = "OK",
            };
            await dialog.ShowAsync();
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
                    Utils.CompressDeflate(src_filepath, dst_filepath);
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

        private static Dictionary<string, ResXDataNode> ReadResXFile(string filePath)
        {
            var resxContents = new Dictionary<string, ResXDataNode>();
            using var resxReader = new ResXResourceReader(filePath);
            resxReader.UseResXDataNodes = true;
            var dict = resxReader.GetEnumerator();
            while (dict.MoveNext())
            {
                var node = (ResXDataNode)dict.Value!;
                resxContents.Add(node.Name, node);
            }
            return resxContents;
        }

        private void Optimize_Click(object sender, RoutedEventArgs e)
        {
            ITypeResolutionService? trs = null;

            foreach (var pair in _resXInputs)
            {
                var primary = ReadResXFile(pair.Key);

                foreach (var satellitePath in pair.Value)
                {
                    string primaryText;
                    using (var sr = new StreamReader(satellitePath, Encoding.UTF8))
                        primaryText = sr.ReadToEnd();
                    primaryText = primaryText.Replace(", Version=2.0.0.0,", ", Version=4.0.0.0,");
                    using (var sw = new StreamWriter(satellitePath, false, Encoding.UTF8))
                        sw.Write(primaryText);

                    var satellite = ReadResXFile(satellitePath);
                    var newSatellite = new Dictionary<string, ResXDataNode>();

                    foreach (var primaryEntry in primary)
                    {
                        if (!satellite.TryGetValue(primaryEntry.Key, out var satelliteItem))
                            continue;

                        if (satelliteItem.Name.Contains('.'))
                        {
                            if (!satelliteItem.Name.EndsWith(".Text") &&
                                !satelliteItem.Name.EndsWith(".Title") &&
                                !satelliteItem.Name.EndsWith(".Filter") &&
                                !satelliteItem.Name.EndsWith(".AccessibleName"))
                                continue;
                        }

                        if (Equals(satelliteItem.GetValue(trs), primaryEntry.Value.GetValue(trs)))
                            continue;

                        newSatellite.Add(satelliteItem.Name, satelliteItem);
                    }

                    string outPath = Path.Combine(OptimizeOutputPath.Text, Path.GetFileName(satellitePath));
                    using var resxWriter = new ResXResourceWriter(outPath);
                    foreach (var kv in newSatellite)
                        resxWriter.AddResource(kv.Value);
                    resxWriter.Generate();
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
            using (Process p = Utils.StartProcess(SigntoolPath.Text, signParams, false))
            {
                p.WaitForExit();
                signSuccess = p.ExitCode == 0;
            }

            await ShowMessageAsync("Signing result", signSuccess ? "Files successfully signed." : "Failed to sign files.");
        }
    }
}
```

- [ ] **Step 3: Wire the bootstrap and `Program.cs`**

In `SimpleDeFence.UI/HostBootstrap.cs`, add:

```csharp
        public static void RunAsDevelTool(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                var window = new DevelToolWindow();
                window.Activate();
                _ = window.ShowStartupWarningAsync();
            });
        }
```

In `SimpleDeFence/Program.cs`, replace:

```csharp
        private static int StartDevelTool()
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            System.Windows.Forms.Application.Run(new DevelToolForm());
            return 0;
        }
```

with:

```csharp
        private static int StartDevelTool()
        {
            SimpleDeFence.UI.HostBootstrap.RunAsDevelTool(Environment.GetCommandLineArgs());
            return 0;
        }
```

- [ ] **Step 4: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo` then `dotnet build SimpleDeFence/SimpleDeFence.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors, both.

- [ ] **Step 5: Manual verification**

Run `SimpleDeFence.exe /develtool` (from the merged exe, now that Task 1 wired its build; this specific mode isn't gated behind Controller-mode's tray/hotkey dependencies, so it's safe to verify from the real merged exe here).
Expected: the warning dialog appears, then the `DevelToolWindow` with five tabs. Spot-check one handler per tab that doesn't need real external files (e.g. `Clear_Click`) doesn't crash.

- [ ] **Step 6: Commit**

```bash
git add SimpleDeFence.UI/DevelToolWindow.xaml SimpleDeFence.UI/DevelToolWindow.xaml.cs SimpleDeFence.UI/HostBootstrap.cs SimpleDeFence/Program.cs
git commit -m "Port DevelTool to WinUI 3"
```

---

## Task 6: Updater / update-checking port

**Files:**
- Create: `SimpleDeFence.Core/UpdateChecker.cs`
- Create: `SimpleDeFence.UI/Services/Updater.cs`
- Modify: `SimpleDeFence.UI/Pages/SettingsPage.xaml`
- Modify: `SimpleDeFence.UI/Pages/SettingsPage.xaml.cs`
- Modify: `SimpleDeFence.Core/Localization/LocKeys.cs`
- Modify: `SimpleDeFence.Core/Localization/Strings.en.json`

**Interfaces:**
- Produces: `SimpleDeFence.Core.UpdateChecker.GetDescriptorAsync(CancellationToken)`.

Why: splits `SimpleDeFence/UpdateChecker.cs` along its existing internal seam per spec Decision 6 — the descriptor-fetch is framework-agnostic, the interactive flow is genuinely WinForms-coupled and gets rebuilt on `ContentDialog`/`HttpClient`, fixing the dormant `Thread.Abort()` bug as part of the same rewrite.

- [ ] **Step 1: Move the descriptor fetch to Core**

Create `SimpleDeFence.Core/UpdateChecker.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDeFence
{
    public static class UpdateChecker
    {
        private const int UPDATER_VERSION = 7;
        private const string URL_UPDATE_DESCRIPTOR = @"https://raw.githubusercontent.com/fcoltro/SimpleDeFence/refs/heads/main/updates/UpdVer{0}/update.json";

        public static async Task<UpdateDescriptor> GetDescriptorAsync(CancellationToken cancellationToken)
        {
            var url = string.Format(CultureInfo.InvariantCulture, URL_UPDATE_DESCRIPTOR, UPDATER_VERSION);
            var productVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("TW-Version", productVersion);

            using var response = await httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken);

            var descriptor = SerializationHelper.Deserialize(System.Text.Encoding.UTF8.GetBytes(json), new UpdateDescriptor());
            if (descriptor.MagicWord != "SimpleDeFence Update Descriptor")
                throw new ApplicationException("Bad update descriptor file.");

            return descriptor;
        }
    }
}
```

Check `SerializationHelper`'s exact deserialize-from-bytes overload name before using it (`SimpleDeFence.Core/SerializationHelper.cs` — `WinForms/UpdateChecker.GetDescriptor` used `DeserializeFromFile`, reading straight from a temp file; this version reads the HTTP body into memory instead, since there's no reason to round-trip through a temp file with `HttpClient` — use whichever `SerializationHelper` overload takes a `byte[]`/`string`, or fall back to writing to `Path.GetTempFileName()` and calling `DeserializeFromFile` if no in-memory overload exists, matching the original's approach exactly).

- [ ] **Step 2: Write the WinUI `Updater`**

Create `SimpleDeFence.UI/Services/Updater.cs`:

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
                Title = Loc.T(LocKeys.Settings.UpdatesCheckingTitle),
                Content = new ProgressRing { IsActive = true },
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
            };

            UpdateDescriptor descriptor;
            var checkTask = UpdateChecker.GetDescriptorAsync(cts.Token);

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
            var tmpFile = Path.GetTempFileName() + ".msi";
            using var cts = new CancellationTokenSource();
            using var httpClient = new HttpClient();

            var progressDialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = Loc.T(LocKeys.Settings.UpdatesDownloading),
                Content = new ProgressRing { IsActive = true },
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
            };

            var downloadTask = DownloadFileAsync(httpClient, mainModule.UpdateURL, tmpFile, cts.Token);
            _ = TryShowDialogAsync(progressDialog);
            var completed = await Task.WhenAny(downloadTask, WaitForCloseAsync(progressDialog));
            if (completed != downloadTask)
            {
                cts.Cancel();
                return;
            }

            progressDialog.Hide();

            try
            {
                await downloadTask;
            }
            catch (Exception ex)
            {
                await ShowMessageAsync(xamlRoot, Loc.T(LocKeys.Settings.UpdatesCheckFailedTitle), ex.Message);
                return;
            }

            Utils.StartProcess(tmpFile, string.Empty, false, false);
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
            var dialog = new ContentDialog { XamlRoot = xamlRoot, Title = title, Content = message, CloseButtonText = Loc.T(LocKeys.Common.Ok) };
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
    }
}
```

Verify `UpdateDescriptor.GetModule`/`MODULE_NAME_MAINBIN` and `UpdateModule.ComponentVersion`/`UpdateURL` match `SimpleDeFence.Core/ServerState.cs`'s actual member names before relying on them as written — this plan describes them from the WinForms `Updater.CheckAppVersion`/`DownloadUpdate` call sites, not a direct read of `ServerState.cs` itself.

- [ ] **Step 3: Add the localization keys**

In `SimpleDeFence.Core/Localization/LocKeys.cs`, add to the existing `Settings` class:

```csharp
            public const string UpdatesCheckNow = "settings.updates.checkNow";
            public const string UpdatesCheckingTitle = "settings.updates.checkingTitle";
            public const string UpdatesCheckFailedTitle = "settings.updates.checkFailedTitle";
            public const string UpdatesNoneAvailable = "settings.updates.noneAvailable";
            public const string UpdatesAvailable = "settings.updates.available";
            public const string UpdatesDownloading = "settings.updates.downloading";
```

In `SimpleDeFence.Core/Localization/Strings.en.json`:

```json
  "settings.updates.checkNow": "Check for updates now",
  "settings.updates.checkingTitle": "Checking for updates...",
  "settings.updates.checkFailedTitle": "Update check failed",
  "settings.updates.noneAvailable": "You're running the latest version.",
  "settings.updates.available": "Version {0} is available. Download and install it now?",
  "settings.updates.downloading": "Downloading update..."
```

- [ ] **Step 4: Wire "Check for updates now" into `SettingsPage`**

In `SimpleDeFence.UI/Pages/SettingsPage.xaml`, find the Updates section (`SectionUpdates`, near the existing `AutoCheckDescription` toggle) and add a button after it:

```xml
                    <Button Content="{loc:Loc Key=settings.updates.checkNow}" Click="CheckForUpdatesNow_Click"/>
```

In `SimpleDeFence.UI/Pages/SettingsPage.xaml.cs`, add:

```csharp
        private async void CheckForUpdatesNow_Click(object sender, RoutedEventArgs e)
        {
            await Services.Updater.CheckForUpdatesAsync(Content.XamlRoot);
        }
```

- [ ] **Step 5: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors. Fix any member-name mismatches surfaced against `ServerState.cs`'s real `UpdateDescriptor`/`UpdateModule` shape (flagged as unverified in Step 2) here.

- [ ] **Step 6: Manual verification**

Run `SimpleDeFence.UI.exe --sample-data`, open Settings, click "Check for updates now."
Expected: the progress dialog appears, then either a real network result (this repo's actual `update.json` at the URL above) or a clean failure dialog if unreachable in this environment — either is acceptable proof the flow doesn't crash; a real version-comparison outcome can't be forced without controlling the remote descriptor.

- [ ] **Step 7: Commit**

```bash
git add SimpleDeFence.Core/UpdateChecker.cs SimpleDeFence.UI/Services/Updater.cs SimpleDeFence.UI/Pages/SettingsPage.xaml SimpleDeFence.UI/Pages/SettingsPage.xaml.cs SimpleDeFence.Core/Localization/LocKeys.cs SimpleDeFence.Core/Localization/Strings.en.json
git commit -m "Port update-checking to WinUI: descriptor fetch to Core, interactive flow to ContentDialog/HttpClient"
```

---

## Task 7: `ClientSettings` Settings UI

**Files:**
- Modify: `SimpleDeFence.UI/Pages/SettingsPage.xaml`
- Modify: `SimpleDeFence.UI/Pages/SettingsPage.xaml.cs`
- Modify: `SimpleDeFence.UI/App.xaml.cs`
- Modify: `SimpleDeFence.Core/Localization/LocKeys.cs`
- Modify: `SimpleDeFence.Core/Localization/Strings.en.json`

**Interfaces:**
- Consumes: `ClientSettings.Language`/`.AskForExceptionDetails`/`.EnableGlobalHotkeys` (Task 2, already added — this task only adds UI to change them), `TrayIconService.ApplyHotkeySetting` (Task 3).

Why: per spec Decision 7 — the fields exist since Task 2, but with no UI they're permanently stuck at their defaults, which is a silent regression for anyone who had them set differently in WinForms. This task closes that gap.

- [ ] **Step 1: Add the localization keys**

In `SimpleDeFence.Core/Localization/LocKeys.cs`, add to the existing `Settings.General*` group:

```csharp
            public const string GeneralLanguage = "settings.general.language";
            public const string GeneralLanguageDescription = "settings.general.languageDescription";
            public const string GeneralAskForExceptionDetails = "settings.general.askForExceptionDetails";
            public const string GeneralAskForExceptionDetailsDescription = "settings.general.askForExceptionDetailsDescription";
            public const string GeneralEnableHotkeys = "settings.general.enableHotkeys";
            public const string GeneralEnableHotkeysDescription = "settings.general.enableHotkeysDescription";
```

In `SimpleDeFence.Core/Localization/Strings.en.json`:

```json
  "settings.general.language": "Language",
  "settings.general.languageDescription": "Overrides the system language for SimpleDeFence's own UI.",
  "settings.general.askForExceptionDetails": "Ask for details when adding a single exception",
  "settings.general.askForExceptionDetailsDescription": "Show an editable confirmation before adding one exception via the tray's quick-add shortcuts. Bulk adds (e.g. a whole folder) never prompt.",
  "settings.general.enableHotkeys": "Enable global keyboard shortcuts",
  "settings.general.enableHotkeysDescription": "Ctrl+Alt+E/P/W instantly whitelist an executable, process, or window from anywhere."
```

- [ ] **Step 2: Add the Settings UI**

In `SimpleDeFence.UI/Pages/SettingsPage.xaml`, in the General section (near the existing theme picker), add:

```xml
                    <ComboBox x:Name="LanguageCombo" Header="{loc:Loc Key=settings.general.language}"
                              SelectionChanged="LanguageCombo_SelectionChanged">
                        <ComboBoxItem Content="Auto" Tag="auto"/>
                        <ComboBoxItem Content="English" Tag="en"/>
                        <ComboBoxItem Content="Português (Brasil)" Tag="pt-BR"/>
                    </ComboBox>
                    <ToggleSwitch x:Name="AskForExceptionDetailsToggle"
                                  Header="{loc:Loc Key=settings.general.askForExceptionDetails}"
                                  OffContent="{loc:Loc Key=settings.general.askForExceptionDetailsDescription}"
                                  Toggled="AskForExceptionDetailsToggle_Toggled"/>
                    <ToggleSwitch x:Name="EnableHotkeysToggle"
                                  Header="{loc:Loc Key=settings.general.enableHotkeys}"
                                  OffContent="{loc:Loc Key=settings.general.enableHotkeysDescription}"
                                  Toggled="EnableHotkeysToggle_Toggled"/>
```

(The two language choices listed — `en`/`pt-BR` — match the two `Strings.*.json` files that exist today; extend the list if more are added later.)

In `SimpleDeFence.UI/Pages/SettingsPage.xaml.cs`, find where the page loads local (non-server) settings — likely near `LockHostsFileToggle.IsOn = config.LockHostsFile;` in a seeding method that reads `ClientSettings` — and add:

```csharp
            var clientSettings = ClientSettings.Load();
            _seeding = true;
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if ((string)item.Tag == clientSettings.Language)
                {
                    LanguageCombo.SelectedItem = item;
                    break;
                }
            }
            AskForExceptionDetailsToggle.IsOn = clientSettings.AskForExceptionDetails;
            EnableHotkeysToggle.IsOn = clientSettings.EnableGlobalHotkeys;
            _seeding = false;
```

(match this page's existing `_seeding`/`_committing` guard convention — copy the exact field names already used elsewhere in this file rather than introducing new ones.) Add the three handlers:

```csharp
        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_seeding || LanguageCombo.SelectedItem is not ComboBoxItem item)
                return;

            var language = (string)item.Tag;
            var settings = ClientSettings.Load();
            settings.Language = language;
            settings.Save();

            if (language == "auto")
                Loc.UseSystemCulture();
            else
                Loc.SetCulture(language);
        }

        private void AskForExceptionDetailsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding)
                return;
            var settings = ClientSettings.Load();
            settings.AskForExceptionDetails = AskForExceptionDetailsToggle.IsOn;
            settings.Save();
        }

        private void EnableHotkeysToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding)
                return;
            var settings = ClientSettings.Load();
            settings.EnableGlobalHotkeys = EnableHotkeysToggle.IsOn;
            settings.Save();
            (Application.Current as App)?.NotifyHotkeySettingChanged(settings.EnableGlobalHotkeys);
        }
```

- [ ] **Step 3: Apply the persisted language at startup**

In `SimpleDeFence.UI/App.xaml.cs`'s `OnLaunched`, the existing `--lang` command-line handling takes priority (unchanged); add the persisted-setting fallback between the `--lang` check and the default `Loc.UseSystemCulture()` call:

```csharp
            var langOverride = ArgValue("--lang");
            if (langOverride is not null)
                Loc.SetCulture(langOverride);
            else
            {
                var savedLanguage = ClientSettings.Load().Language;
                if (savedLanguage != "auto")
                    Loc.SetCulture(savedLanguage);
                else
                    Loc.UseSystemCulture();
            }
```

- [ ] **Step 4: Wire the hotkey-toggle notification into `TrayIconService`**

In `SimpleDeFence.UI/App.xaml.cs`, add a method the Settings page's toggle handler (Step 6) calls:

```csharp
        internal void NotifyHotkeySettingChanged(bool enabled) => _tray?.ApplyHotkeySetting(enabled);
```

- [ ] **Step 5: Build and run the full test suite**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors.

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS, 58/58 (57 after Task 2's 4 new `ClientSettings` tests + Task 4's 1 new `AppDatabase` test — this task adds no new automated tests, UI-only, so it's a regression check).

- [ ] **Step 6: Manual verification**

Run `SimpleDeFence.UI.exe --sample-data`, open Settings, change the language dropdown, confirm the UI's own labels (not server-dependent content) re-render in the chosen language without a restart. Toggle "Enable global keyboard shortcuts" off, confirm (from the merged exe, per Task 3's own verification) Ctrl+Alt+E/P/W no longer trigger; toggle back on, confirm they do again.

- [ ] **Step 7: Commit**

```bash
git add SimpleDeFence.UI/Pages/SettingsPage.xaml SimpleDeFence.UI/Pages/SettingsPage.xaml.cs SimpleDeFence.UI/App.xaml.cs SimpleDeFence.Core/Localization/LocKeys.cs SimpleDeFence.Core/Localization/Strings.en.json
git commit -m "Add Settings UI for Language, AskForExceptionDetails, and EnableGlobalHotkeys"
```

---

## Task 8: Final cutover — delete WinForms

**Files:**
- Modify: `SimpleDeFence/SimpleDeFenceDoctor.cs`
- Modify: `SimpleDeFence/SimpleDeFence.csproj`
- Delete: `SimpleDeFence/SimpleDeFenceController.cs`, `.Designer.cs`, `MainForm.*.resx` (all 18)
- Delete: `SimpleDeFence/SettingsForm.cs`, `.Designer.cs`, `SettingsForm.*.resx` (all locales)
- Delete: `SimpleDeFence/DevelToolForm.cs`, `.Designer.cs`, `DevelToolForm.resx`
- Delete: `SimpleDeFence/ApplicationExceptionForm.cs`, `.Designer.cs`, `ApplicationExceptionForm.*.resx` (all locales)
- Delete: `SimpleDeFence/AppFinderForm.cs`, `.Designer.cs`, `AppFinderForm.*.resx` (all locales)
- Delete: `SimpleDeFence/PasswordForm.cs`, `.Designer.cs`, `PasswordForm.*.resx` (all locales)
- Delete: `SimpleDeFence/ConnectionsForm.cs`, `.Designer.cs`, `ConnectionsForm.*.resx` (all locales)
- Delete: `SimpleDeFence/Processes.cs`, `.Designer.cs`, `Processes.*.resx` (all locales)
- Delete: `SimpleDeFence/Services.cs`, `.Designer.cs`, `Services.*.resx` (all locales)
- Delete: `SimpleDeFence/UwpPackagesForm.cs`, `.Designer.cs`, `UwpPackagesForm.*.resx` (all locales)
- Modify: `SimpleDeFence/Settings.cs`

Why: everything on this list is unreachable once Tasks 1–7 land — confirmed by this plan's own spec (Cross-cutting section). This is the only task that deletes anything; every prior task was purely additive.

- [ ] **Step 1: Replace `SimpleDeFenceDoctor.Uninstall()`'s `PasswordForm` usage**

In `SimpleDeFence/SimpleDeFenceDoctor.cs`, `Uninstall()` currently does:

```csharp
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
```

`ShowDialog()` here works today without any `Application.Run()` already active (WinForms modal dialogs pump their own nested loop) — WinUI's `ContentDialog.ShowAsync()` has no equivalent; it needs an active `Window`/`XamlRoot` to attach to. Replace the whole block with a call into a new small bootstrap that starts a minimal WinUI `Application` just long enough to show `PasswordPromptDialog` (Task 3) and return its result:

```csharp
                    while (twController.IsServerLocked)
                    {
                        string? password = SimpleDeFence.UI.HostBootstrap.PromptForPassword();
                        if (password is null)
                            return -1;

                        twController.TryUnlockServer(password);
                    }
```

In `SimpleDeFence.UI/HostBootstrap.cs`, add:

```csharp
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
```

This references a new minimal `Application` subclass, `PasswordPromptHostApp` — add it to the bottom of the same `HostBootstrap.cs` file (or a new small file if preferred; keep it out of `App.xaml.cs`, since the real `App` is the full controller shell and must not be reused for this one-dialog purpose):

```csharp
    /// <summary>A minimal WinUI Application whose only job is hosting one PasswordPromptDialog and
    /// then exiting - see HostBootstrap.PromptForPassword.</summary>
    internal sealed class PasswordPromptHostApp : Application
    {
        public event Action<string?>? PasswordResolved;

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var window = new Window();
            window.Activate();

            var dialog = new PasswordPromptDialog { XamlRoot = window.Content?.XamlRoot };
            _ = ShowAsync(dialog);
        }

        private async void ShowAsync(PasswordPromptDialog dialog)
        {
            var result = await dialog.ShowAsync();
            PasswordResolved?.Invoke(result == ContentDialogResult.Primary ? dialog.Password : null);
        }
    }
```

`PasswordPromptDialog` needs a `Window` with `Content` set before `XamlRoot` resolves — a bare `new Window()` has no `Content` by default; set a trivial one (`window.Content = new Grid();`) before reading `window.Content?.XamlRoot`, or the dialog's `XamlRoot` will be null and `ShowAsync()` will throw. Verify this exact sequencing against the actual `Window`/`ContentDialog` API during Step 3's build+manual test — WinUI's exact requirements here are easy to get subtly wrong from memory alone.

- [ ] **Step 2: Delete the WinForms files**

```bash
git rm SimpleDeFence/SimpleDeFenceController.cs SimpleDeFence/SimpleDeFenceController.Designer.cs SimpleDeFence/MainForm.*.resx
git rm SimpleDeFence/SettingsForm.cs SimpleDeFence/SettingsForm.Designer.cs SimpleDeFence/SettingsForm.resx SimpleDeFence/SettingsForm.*.resx
git rm SimpleDeFence/DevelToolForm.cs SimpleDeFence/DevelToolForm.Designer.cs SimpleDeFence/DevelToolForm.resx
git rm SimpleDeFence/ApplicationExceptionForm.cs SimpleDeFence/ApplicationExceptionForm.Designer.cs SimpleDeFence/ApplicationExceptionForm.resx SimpleDeFence/ApplicationExceptionForm.*.resx
git rm SimpleDeFence/AppFinderForm.cs SimpleDeFence/AppFinderForm.Designer.cs SimpleDeFence/AppFinderForm.resx SimpleDeFence/AppFinderForm.*.resx
git rm SimpleDeFence/PasswordForm.cs SimpleDeFence/PasswordForm.Designer.cs SimpleDeFence/PasswordForm.resx SimpleDeFence/PasswordForm.*.resx
git rm SimpleDeFence/ConnectionsForm.cs SimpleDeFence/ConnectionsForm.Designer.cs SimpleDeFence/ConnectionsForm.resx SimpleDeFence/ConnectionsForm.*.resx
git rm SimpleDeFence/Processes.cs SimpleDeFence/Processes.Designer.cs SimpleDeFence/Processes.resx SimpleDeFence/Processes.*.resx
git rm SimpleDeFence/Services.cs SimpleDeFence/Services.Designer.cs SimpleDeFence/Services.resx SimpleDeFence/Services.*.resx
git rm SimpleDeFence/UwpPackagesForm.cs SimpleDeFence/UwpPackagesForm.Designer.cs SimpleDeFence/UwpPackagesForm.resx SimpleDeFence/UwpPackagesForm.*.resx
```

If any glob above matches zero files (a given form might not have every locale's satellite resx), `git rm` errors on that specific pattern — re-run without the non-matching glob rather than treating it as a real failure.

- [ ] **Step 3: Delete `ControllerSettings` and clean up `Settings.cs`**

In `SimpleDeFence/Settings.cs`, delete the entire `ControllerSettings` class (from `[DataContract(...)] public sealed class ControllerSettings` through its closing brace, including the `[OnDeserialized]` method and `UserDataPath`/`FilePath`/`Save`/`Load`/`GetJsonTypeInfo` members).

In the same file, in `ConfigContainer`, delete the `Controller` field and both constructors' references to it:

```csharp
        [DataMember(EmitDefaultValue = false)]
        public ControllerSettings Controller;
```

and update both constructors to drop the `client`/`Controller` assignment (adjust the second constructor's parameter list too — check every call site of `new ConfigContainer(...)` across the remaining codebase before removing the parameter, since a positional-arg mismatch is a silent bug, not a compile error, if any caller still passes two arguments).

In `ActiveConfig`, delete:

```csharp
        [AllowNull]
        internal static ControllerSettings Controller = null;
```

- [ ] **Step 4: Remove the now-dead `EmbeddedResource`/`Compile` entries from `SimpleDeFence.csproj`**

In `SimpleDeFence/SimpleDeFence.csproj`, remove every `<EmbeddedResource Update="...">`, `<Compile Update="...">`, and `<Compile Include="...">` entry (there are dozens, one per deleted `.cs`/`.Designer.cs`/`.resx` file) referencing any file deleted in Step 2. This project doesn't glob its own `SimpleDeFence/*.cs` — check whether it does (open the file and look for a bare `<Compile Include="*.cs">`-style glob at the top level, distinct from the `../SimpleDeFence.Windows/*.cs`-style cross-project globs already known from the net10-retarget work) before assuming every deleted file needs an explicit removal; if it globs its own directory, only the explicit `<EmbeddedResource Update>`/`<Compile Update>` metadata entries (Designer/resx wiring) need removing, not `<Compile Include>` for the `.cs` files themselves.

- [ ] **Step 5: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors.

Run: `dotnet build SimpleDeFence/SimpleDeFence.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors. Any remaining reference to a deleted type (missed call site from Step 3's `ConfigContainer`/`ActiveConfig` cleanup, or a stray `<Compile Include>` missed in Step 4) surfaces here — fix in place.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS, 58/58 — nothing in this task touches anything the test suite exercises directly, so this is a regression check, not new coverage.

- [ ] **Step 7: Real-machine verification checklist**

None of this task's actual risk is caught by the build or the test suite — every item below needs a real Windows session (VM or physical machine), matching the verification standard every prior WinUI phase in this repo already used:

1. Install the retargeted MSI (or run the self-contained publish directly) and confirm `SimpleDeFence.exe` launches the WinUI shell by default with no WinForms window ever appearing.
2. Confirm the tray icon appears, shows the correct mode icon, and every menu item (mode switching, Manage, Connections, Lock, Elevate, Allow Local Subnet, Enable Hosts Blocklist, all three whitelist quick-adds, Quit) works against a real running service.
3. Confirm Ctrl+Alt+E/P/W trigger the whitelist quick-adds from outside the app (global, not just when focused).
4. Confirm `/develtool` launches and at least one real end-to-end DevelTool flow (e.g. Associations → Create) produces correct output against real files.
5. Confirm "Check for updates now" completes against the real update descriptor URL.
6. Confirm Rules' "Pick folder..." option against a real multi-exe folder produces the expected exceptions once committed to a real service.
7. Confirm `/uninstall` against a locked, password-protected install: the new `PasswordPromptDialog`-via-`PasswordPromptHostApp` flow appears, accepts the correct password, and the uninstall proceeds; a wrong password is rejected without crashing.
8. Confirm the Language picker actually changes the WinUI shell's displayed strings, and persists across a relaunch.

Do not report this task complete without this checklist actually run somewhere with a real Windows session — say so plainly if it hasn't happened yet, matching this repo's own established convention (see the net10-retarget plan's MSI task) rather than claiming a verification that didn't happen.

- [ ] **Step 8: Commit**

```bash
git add SimpleDeFence/SimpleDeFenceDoctor.cs SimpleDeFence/SimpleDeFence.csproj SimpleDeFence/Settings.cs SimpleDeFence.UI/HostBootstrap.cs
git commit -m "Delete WinForms: SimpleDeFence.exe now launches WinUI 3 only"
```
