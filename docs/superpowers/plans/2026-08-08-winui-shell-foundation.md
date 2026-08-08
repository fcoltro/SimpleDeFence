# WinUI Shell & Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the ad-hoc two-page WinUI shell with the designed three-destination shell — persistent mode chip, Mica Alt, custom title bar — running on a swappable data source so every screen is runnable and verifiable before the .NET 10 migration lands.

**Architecture:** Display logic moves into `SimpleDeFence.Core` as pure, unit-tested functions. The UI talks to an `IFirewallClient` interface with two implementations: the real pipe client, and a sample-data client selected by `--sample-data`. The shell is a `NavigationView` whose pane footer holds an always-visible mode chip, replacing the Status page entirely.

**Tech Stack:** C#, WinUI 3 / Windows App SDK 2.3.1, .NET 10 (`net10.0-windows10.0.19041.0`), xunit.

## Global Constraints

- Target framework for `SimpleDeFence.UI` and `SimpleDeFence.Tests` is `net10.0-windows10.0.19041.0`; `SimpleDeFence.Core` multi-targets `net48;net10.0-windows10.0.19041.0`.
- **Anything added to `SimpleDeFence.Core` must compile under net48**, because `SimpleDeFence.csproj` glob-compiles Core's sources into the net48 WinForms app. No `net10`-only APIs there.
- **Status is never signalled by colour alone** — always colour *plus* icon *plus* word.
- Status colours are **roles bound to WinUI system semantic brushes**, not hex literals.
- **The real client is the default in every configuration.** Sample data is only reachable via the `--sample-data` command-line switch.
- **All IPC calls stay off the UI thread.**
- **Never let an unrecognised response look like success** — an action that did not take must not appear to have taken.
- The WinUI GUI is English-only; the WinForms GUI keeps its localized resx. No localization work here.
- **Nothing is removed from the WinForms app.** It stays buildable throughout.
- Build the net48 app with `-t:Restore` and `-t:Build` as **separate** MSBuild invocations (see ROADMAP.md).

## Scope

This plan covers spec phases **1 (Foundation)** and **2 (Shell)** only. The Connections screen, Rules rework and Settings are separate plans. What ships at the end of this plan: an app that launches, renders the new shell with a working mode chip, navigates between destinations, and can be run on realistic sample data for visual verification.

## Known limitation (deliberate, not an oversight)

`SimpleDeFence.Tests` cannot reference `SimpleDeFence.UI`, because it is a WinUI executable and referencing it drags the WinUI runtime into the test host. So **view models are not unit tested in this plan** — only the pure Core functions are. Making view models testable requires extracting them into a separate non-WinUI class library; that is a follow-up, recorded here rather than silently dropped. View-model behaviour in this plan is verified by running the app.

## File Structure

**Created:**
- `SimpleDeFence.Core/FirewallModeInfo.cs` — pure mode→display mapping (net48-safe)
- `SimpleDeFence.Tests/FirewallModeInfoTests.cs` — tests for the above
- `SimpleDeFence.UI/Services/IFirewallClient.cs` — the client abstraction
- `SimpleDeFence.UI/Services/SampleFirewallClient.cs` — sample-data implementation
- `SimpleDeFence.UI/ViewModels/ObservableObject.cs` — `INotifyPropertyChanged` base
- `SimpleDeFence.UI/ViewModels/ShellViewModel.cs` — mode chip + connection state
- `SimpleDeFence.UI/Themes/StatusResources.xaml` — status brushes and glyphs

**Modified:**
- `SimpleDeFence.UI/Services/FirewallClient.cs` — implement `IFirewallClient`
- `SimpleDeFence.UI/App.xaml` — merge `StatusResources.xaml`
- `SimpleDeFence.UI/App.xaml.cs` — client selection via `--sample-data`
- `SimpleDeFence.UI/MainWindow.xaml` / `.xaml.cs` — the new shell
- `SimpleDeFence.UI/Pages/ApplicationsPage.xaml.cs` — consume `IFirewallClient`

**Deleted:**
- `SimpleDeFence.UI/Pages/StatusPage.xaml` and `.xaml.cs` — its mode switching moves into the shell's mode chip

---

### Task 1: Mode display mapping in Core

Moves the mode labels/descriptions currently hardcoded in `StatusPage` into Core, so the shell and any future screen share one definition and it can be unit tested. Labels match the WinForms `Messages.resx` wording so both GUIs name modes identically.

**Files:**
- Create: `SimpleDeFence.Core/FirewallModeInfo.cs`
- Test: `SimpleDeFence.Tests/FirewallModeInfoTests.cs`

**Interfaces:**
- Consumes: `FirewallMode` (already in `SimpleDeFence.Core/ServerConfiguration.cs`)
- Produces: `SimpleDeFence.FirewallModeInfo` (properties `Mode`, `Label`, `Description`) and `SimpleDeFence.FirewallModes` with `IReadOnlyList<FirewallModeInfo> Selectable`, `string LabelFor(FirewallMode)`, `string DescriptionFor(FirewallMode)`, `int IndexOf(FirewallMode)`

- [ ] **Step 1: Write the failing test**

Create `SimpleDeFence.Tests/FirewallModeInfoTests.cs`:

```csharp
using SimpleDeFence;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class FirewallModeInfoTests
    {
        [Fact]
        public void Selectable_lists_the_five_user_choosable_modes_in_order()
        {
            var modes = new System.Collections.Generic.List<FirewallMode>();
            foreach (var m in FirewallModes.Selectable)
                modes.Add(m.Mode);

            Assert.Equal(
                new[]
                {
                    FirewallMode.Normal,
                    FirewallMode.BlockAll,
                    FirewallMode.AllowOutgoing,
                    FirewallMode.Disabled,
                    FirewallMode.Learning,
                },
                modes);
        }

        [Theory]
        [InlineData(FirewallMode.Normal, "Normal")]
        [InlineData(FirewallMode.BlockAll, "Block all")]
        [InlineData(FirewallMode.AllowOutgoing, "Allow outgoing")]
        [InlineData(FirewallMode.Disabled, "Disabled")]
        [InlineData(FirewallMode.Learning, "Autolearn")]
        public void Labels_match_the_WinForms_wording(FirewallMode mode, string expected)
        {
            Assert.Equal(expected, FirewallModes.LabelFor(mode));
        }

        [Fact]
        public void Unknown_is_labelled_but_not_selectable()
        {
            Assert.Equal("Unknown", FirewallModes.LabelFor(FirewallMode.Unknown));
            Assert.Equal(-1, FirewallModes.IndexOf(FirewallMode.Unknown));
        }

        [Fact]
        public void IndexOf_matches_the_selectable_order()
        {
            Assert.Equal(0, FirewallModes.IndexOf(FirewallMode.Normal));
            Assert.Equal(4, FirewallModes.IndexOf(FirewallMode.Learning));
        }

        [Fact]
        public void Every_selectable_mode_has_a_non_empty_description()
        {
            foreach (var m in FirewallModes.Selectable)
                Assert.False(string.IsNullOrWhiteSpace(m.Description));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: FAIL — `The name 'FirewallModes' does not exist in the current context`

- [ ] **Step 3: Write the implementation**

Create `SimpleDeFence.Core/FirewallModeInfo.cs`:

```csharp
using System.Collections.Generic;

namespace SimpleDeFence
{
    /// <summary>How a single firewall mode is presented to the user.</summary>
    public sealed class FirewallModeInfo
    {
        public FirewallMode Mode { get; }
        public string Label { get; }
        public string Description { get; }

        public FirewallModeInfo(FirewallMode mode, string label, string description)
        {
            Mode = mode;
            Label = label;
            Description = description;
        }
    }

    /// <summary>
    /// The user-selectable firewall modes and their wording. Labels match the WinForms GUI's
    /// Messages.resx so both GUIs name modes identically while they run side by side.
    /// Lives in Core so it is shared and unit-testable without a UI.
    /// </summary>
    public static class FirewallModes
    {
        private static readonly FirewallModeInfo[] _selectable =
        {
            new FirewallModeInfo(FirewallMode.Normal, "Normal",
                "The firewall is operating as recommended."),
            new FirewallModeInfo(FirewallMode.BlockAll, "Block all",
                "The firewall is blocking all incoming and outgoing traffic."),
            new FirewallModeInfo(FirewallMode.AllowOutgoing, "Allow outgoing",
                "The firewall allows outgoing connections."),
            new FirewallModeInfo(FirewallMode.Disabled, "Disabled",
                "The firewall is disabled."),
            new FirewallModeInfo(FirewallMode.Learning, "Autolearn",
                "The firewall is learning while letting all traffic through."),
        };

        public static IReadOnlyList<FirewallModeInfo> Selectable => _selectable;

        /// <summary>Index into <see cref="Selectable"/>, or -1 for modes the user cannot pick.</summary>
        public static int IndexOf(FirewallMode mode)
        {
            for (int i = 0; i < _selectable.Length; ++i)
            {
                if (_selectable[i].Mode == mode)
                    return i;
            }
            return -1;
        }

        public static string LabelFor(FirewallMode mode)
        {
            int i = IndexOf(mode);
            return i >= 0 ? _selectable[i].Label : "Unknown";
        }

        public static string DescriptionFor(FirewallMode mode)
        {
            int i = IndexOf(mode);
            return i >= 0 ? _selectable[i].Description : string.Empty;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS — all tests green

- [ ] **Step 5: Verify the net48 WinForms app still compiles**

This file is glob-compiled into the net48 app, so it must not use net10-only APIs.

```bash
MSB="/c/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/MSBuild/Current/Bin/MSBuild.exe"
export MSBuildSDKsPath="C:\Program Files\dotnet\sdk\10.0.302\Sdks" MSBuildEnableWorkloadResolver=false
"$MSB" SimpleDeFence/SimpleDeFence.csproj -t:Restore -v:quiet -nologo
"$MSB" SimpleDeFence/SimpleDeFence.csproj -t:Build -p:Configuration=Debug -v:minimal -nologo
```
Expected: `Build succeeded`, 0 errors

- [ ] **Step 6: Commit**

```bash
git add SimpleDeFence.Core/FirewallModeInfo.cs SimpleDeFence.Tests/FirewallModeInfoTests.cs
git commit -m "Move firewall mode display wording into Core and cover it"
```

---

### Task 2: IFirewallClient abstraction

Extracts an interface from the existing client so a sample-data implementation can stand in. No behaviour changes.

**Files:**
- Create: `SimpleDeFence.UI/Services/IFirewallClient.cs`
- Modify: `SimpleDeFence.UI/Services/FirewallClient.cs`
- Modify: `SimpleDeFence.UI/App.xaml.cs`

`ApplicationsPage` needs **no change**: it only touches `Config`, `Connected`, `LastError` and
`RefreshAsync()`, all of which are on the interface.

**Interfaces:**
- Consumes: `FirewallClient` (existing), `ServerConfiguration`, `ServerState`, `MessageType`, `FirewallMode`
- Produces: `SimpleDeFence.UI.Services.IFirewallClient` with members `Config`, `State`, `Connected`, `LastError`, `Changed`, `RefreshAsync()`, `SwitchModeAsync(FirewallMode)`

- [ ] **Step 1: Create the interface**

Create `SimpleDeFence.UI/Services/IFirewallClient.cs`:

```csharp
using System;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// What the GUI needs from the service. Exists so the screens can run against sample data
    /// while the real client is blocked by AuthAsServer (see ROADMAP.md) - without it, none of
    /// these screens could be run or visually verified while being built.
    /// </summary>
    internal interface IFirewallClient
    {
        ServerConfiguration? Config { get; }
        ServerState? State { get; }
        bool Connected { get; }
        string? LastError { get; }

        /// <summary>Raised after every refresh so open pages can redraw.</summary>
        event EventHandler? Changed;

        Task RefreshAsync();
        Task<MessageType> SwitchModeAsync(FirewallMode mode);
    }
}
```

- [ ] **Step 2: Make FirewallClient implement it**

In `SimpleDeFence.UI/Services/FirewallClient.cs`, change the class declaration line:

```csharp
    internal sealed class FirewallClient : IFirewallClient
```

No other change is needed — the existing members already match the interface.

- [ ] **Step 3: Point App at the interface**

In `SimpleDeFence.UI/App.xaml.cs`, change the property type:

```csharp
        // Shared by every page so they agree on connection state and the config changeset.
        internal static IFirewallClient Firewall { get; } = new FirewallClient();
```

- [ ] **Step 4: Build to verify nothing broke**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors

- [ ] **Step 5: Commit**

```bash
git add SimpleDeFence.UI/Services/IFirewallClient.cs SimpleDeFence.UI/Services/FirewallClient.cs SimpleDeFence.UI/App.xaml.cs
git commit -m "Extract IFirewallClient so screens can run on sample data"
```

---

### Task 3: Sample data client

**Files:**
- Create: `SimpleDeFence.UI/Services/SampleFirewallClient.cs`
- Modify: `SimpleDeFence.UI/App.xaml.cs`

**Interfaces:**
- Consumes: `IFirewallClient` (Task 2)
- Produces: `SimpleDeFence.UI.Services.SampleFirewallClient`; `App.Firewall` becomes settable-once at startup

- [ ] **Step 1: Write the sample client**

Create `SimpleDeFence.UI/Services/SampleFirewallClient.cs`:

```csharp
using System;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Realistic in-memory data, used only when --sample-data is passed. Lets the screens be
    /// built and visually verified before the .NET 10 migration makes the real client usable.
    /// </summary>
    internal sealed class SampleFirewallClient : IFirewallClient
    {
        public ServerConfiguration? Config { get; private set; }
        public ServerState? State { get; private set; }
        public bool Connected { get; private set; }
        public string? LastError { get; private set; }

        public event EventHandler? Changed;

        public Task RefreshAsync()
        {
            Config ??= BuildConfig();
            State ??= new ServerState
            {
                Mode = FirewallMode.Normal,
                Locked = false,
                HasPassword = false,
            };

            Connected = true;
            LastError = null;

            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<MessageType> SwitchModeAsync(FirewallMode mode)
        {
            if (State is not null)
                State.Mode = mode;

            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(MessageType.MODE_SWITCH);
        }

        private static ServerConfiguration BuildConfig()
        {
            var profile = new ServerProfileConfiguration("Default");

            profile.AppExceptions.Add(new FirewallExceptionV3(
                new ExecutableSubject(@"C:\Program Files\Mozilla Firefox\firefox.exe"),
                new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "80,443" }));

            profile.AppExceptions.Add(new FirewallExceptionV3(
                new ExecutableSubject(@"C:\Program Files\Git\git-remote-https.exe"),
                new UnrestrictedPolicy()));

            profile.AppExceptions.Add(new FirewallExceptionV3(
                new ServiceSubject(@"C:\Windows\System32\svchost.exe", "UsoSvc"),
                new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "80,443" }));

            profile.AppExceptions.Add(new FirewallExceptionV3(
                new ServiceSubject(@"C:\Windows\System32\svchost.exe", "DoSvc"),
                new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "80,443" }));

            profile.AppExceptions.Add(new FirewallExceptionV3(
                new ExecutableSubject(@"C:\Users\sample\AppData\Local\Telemetry\tracker.exe"),
                new HardBlockPolicy()));

            profile.AppExceptions.Add(new FirewallExceptionV3(
                GlobalSubject.Instance,
                new TcpUdpPolicy { AllowedLocalUdpListenerPorts = "5353" }));

            var config = new ServerConfiguration();
            config.Profiles.Add(profile);
            config.ActiveProfileName = "Default";
            return config;
        }
    }
}
```

- [ ] **Step 2: Select the client from the command line**

Replace the whole of `SimpleDeFence.UI/App.xaml.cs` with:

```csharp
using Microsoft.UI.Xaml;
using SimpleDeFence.UI.Services;
using System;

namespace SimpleDeFence.UI
{
    public partial class App : Application
    {
        private Window? m_window;

        // Shared by every page so they agree on connection state and the config changeset.
        // The real client is the default in every configuration; sample data is opt-in only,
        // so fabricated firewall state can never be shown to a user by accident.
        internal static IFirewallClient Firewall { get; private set; } = new FirewallClient();

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (UseSampleData())
                Firewall = new SampleFirewallClient();

            m_window = new MainWindow();
            m_window.Activate();
        }

        private static bool UseSampleData()
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, "--sample-data", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors

- [ ] **Step 4: Verify sample data actually loads**

```bash
SimpleDeFence.UI/bin/Debug/net10.0-windows10.0.19041.0/win-x64/SimpleDeFence.UI.exe --sample-data
```
Expected: the window opens and the Applications page lists **6 exceptions** (firefox.exe, git-remote-https.exe, UsoSvc, DoSvc, tracker.exe, All applications) instead of "Not connected". Then close it and run **without** the switch — it must show "Not connected" again, proving the real client is still the default.

- [ ] **Step 5: Commit**

```bash
git add SimpleDeFence.UI/Services/SampleFirewallClient.cs SimpleDeFence.UI/App.xaml.cs
git commit -m "Add opt-in sample data client so screens are runnable before migration"
```

---

### Task 4: Observable base and shell view model

**Files:**
- Create: `SimpleDeFence.UI/ViewModels/ObservableObject.cs`
- Create: `SimpleDeFence.UI/ViewModels/ShellViewModel.cs`

**Interfaces:**
- Consumes: `IFirewallClient` (Task 2), `FirewallModes` (Task 1)
- Produces: `SimpleDeFence.UI.ViewModels.ObservableObject` (protected `Set<T>`, `OnPropertyChanged`); `SimpleDeFence.UI.ViewModels.ShellViewModel` with `ModeLabel`, `ModeGlyph`, `ModeStateKey`, `IsConnected`, `IsLocked`, `StatusLine`, `Task RefreshAsync()`, `Task<MessageType> SwitchModeAsync(FirewallMode)`

- [ ] **Step 1: Write the observable base**

Create `SimpleDeFence.UI/ViewModels/ObservableObject.cs`:

```csharp
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SimpleDeFence.UI.ViewModels
{
    internal abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Assigns and raises PropertyChanged only when the value actually changed.</summary>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
```

- [ ] **Step 2: Write the shell view model**

Create `SimpleDeFence.UI/ViewModels/ShellViewModel.cs`:

```csharp
using SimpleDeFence.UI.Services;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.ViewModels
{
    /// <summary>
    /// Backs the always-visible mode chip. Status is ambient rather than a destination, so this
    /// lives on the shell instead of a Status page.
    /// </summary>
    internal sealed class ShellViewModel : ObservableObject
    {
        private readonly IFirewallClient _client;

        private string _modeLabel = "Connecting...";
        private string _modeGlyph = "\uE9CE";      // Segoe Fluent: Unknown
        private string _modeStateKey = "Neutral";
        private string _statusLine = string.Empty;
        private bool _isConnected;
        private bool _isLocked;
        private bool _busy;

        public ShellViewModel(IFirewallClient client) => _client = client;

        public string ModeLabel { get => _modeLabel; private set => Set(ref _modeLabel, value); }
        public string ModeGlyph { get => _modeGlyph; private set => Set(ref _modeGlyph, value); }
        public string ModeStateKey { get => _modeStateKey; private set => Set(ref _modeStateKey, value); }
        public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }
        public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
        public bool IsLocked { get => _isLocked; private set => Set(ref _isLocked, value); }
        public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }

        /// <summary>True only when the user may actually change the mode.</summary>
        public bool CanSwitchMode => IsConnected && !IsLocked && !IsBusy;

        public FirewallMode CurrentMode => _client.State?.Mode ?? FirewallMode.Unknown;

        public async Task RefreshAsync()
        {
            IsBusy = true;
            await _client.RefreshAsync();
            IsBusy = false;
            Update();
        }

        public async Task<MessageType> SwitchModeAsync(FirewallMode mode)
        {
            IsBusy = true;
            MessageType resp;
            try
            {
                resp = await _client.SwitchModeAsync(mode);
            }
            finally
            {
                IsBusy = false;
            }

            Update();
            return resp;
        }

        private void Update()
        {
            IsConnected = _client.Connected;
            IsLocked = _client.State?.Locked ?? false;

            var mode = CurrentMode;

            if (!IsConnected)
            {
                ModeLabel = "Not connected";
                ModeGlyph = "\uE8CD";       // Segoe Fluent: Error
                ModeStateKey = "Neutral";
                StatusLine = _client.LastError ?? string.Empty;
            }
            else
            {
                ModeLabel = FirewallModes.LabelFor(mode);
                ModeGlyph = GlyphFor(mode);
                ModeStateKey = StateKeyFor(mode);
                StatusLine = IsLocked
                    ? "Configuration is locked"
                    : FirewallModes.DescriptionFor(mode);
            }

            OnPropertyChanged(nameof(CurrentMode));
            OnPropertyChanged(nameof(CanSwitchMode));
        }

        // Segoe Fluent Icons glyphs. Paired with a colour AND a word everywhere they are used,
        // so status never depends on colour alone.
        private static string GlyphFor(FirewallMode mode) => mode switch
        {
            FirewallMode.Normal => "\uE72E",          // Lock (protected)
            FirewallMode.BlockAll => "\uE785",        // BlockContact (locked down)
            FirewallMode.AllowOutgoing => "\uE8AB",   // Forward (relaxed)
            FirewallMode.Disabled => "\uE7BA",        // Warning (inactive)
            FirewallMode.Learning => "\uE9CE",        // Info (transient)
            _ => "\uE9CE",
        };

        private static string StateKeyFor(FirewallMode mode) => mode switch
        {
            FirewallMode.Normal => "Success",
            FirewallMode.BlockAll => "Information",
            FirewallMode.AllowOutgoing => "Caution",
            FirewallMode.Disabled => "Neutral",
            FirewallMode.Learning => "AccentAlt",
            _ => "Neutral",
        };
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors

- [ ] **Step 4: Commit**

```bash
git add SimpleDeFence.UI/ViewModels/
git commit -m "Add observable base and shell view model for the mode chip"
```

---

### Task 5: Status resource dictionary

**Files:**
- Create: `SimpleDeFence.UI/Themes/StatusResources.xaml`
- Modify: `SimpleDeFence.UI/App.xaml`

**Interfaces:**
- Produces: brush resources `StatusSuccessBrush`, `StatusCautionBrush`, `StatusInformationBrush`, `StatusNeutralBrush`, `StatusAccentAltBrush`, and a `ModeStateToBrushConverter` keyed `ModeStateToBrush`

- [ ] **Step 1: Create the resource dictionary**

Create `SimpleDeFence.UI/Themes/StatusResources.xaml`:

```xml
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:SimpleDeFence.UI.Themes">

    <!-- Status colours are roles bound to the WinUI system semantic brushes rather than hex
         literals, so they track the OS theme and high-contrast modes automatically. -->
    <SolidColorBrush x:Key="StatusSuccessBrush"     Color="{ThemeResource SystemFillColorSuccess}"/>
    <SolidColorBrush x:Key="StatusCautionBrush"     Color="{ThemeResource SystemFillColorCaution}"/>
    <SolidColorBrush x:Key="StatusInformationBrush" Color="{ThemeResource SystemFillColorAttention}"/>
    <SolidColorBrush x:Key="StatusNeutralBrush"     Color="{ThemeResource SystemFillColorNeutral}"/>
    <SolidColorBrush x:Key="StatusAccentAltBrush"   Color="{ThemeResource SystemAccentColorLight2}"/>

    <local:ModeStateToBrushConverter x:Key="ModeStateToBrush"/>
</ResourceDictionary>
```

- [ ] **Step 2: Create the converter**

Create `SimpleDeFence.UI/Themes/ModeStateToBrushConverter.cs`:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace SimpleDeFence.UI.Themes
{
    /// <summary>Maps ShellViewModel.ModeStateKey to one of the status brushes.</summary>
    public sealed class ModeStateToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var key = value as string ?? "Neutral";
            var resourceKey = key switch
            {
                "Success" => "StatusSuccessBrush",
                "Caution" => "StatusCautionBrush",
                "Information" => "StatusInformationBrush",
                "AccentAlt" => "StatusAccentAltBrush",
                _ => "StatusNeutralBrush",
            };

            if (Application.Current.Resources.TryGetValue(resourceKey, out var brush))
                return brush;

            return new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
```

- [ ] **Step 3: Merge the dictionary into App.xaml**

Replace `SimpleDeFence.UI/App.xaml` with:

```xml
<Application
    x:Class="SimpleDeFence.UI.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:Microsoft.UI.Xaml.Controls">

    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <!-- Brings in the WinUI control templates and theme resources. Without this every
                     {StaticResource} lookup fails and the app dies during XAML parse with
                     0x802B000A. -->
                <controls:XamlControlsResources/>
                <ResourceDictionary Source="ms-appx:///Themes/StatusResources.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 4: Build and run to verify XAML parses**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`

Then launch the app. **A XAML resource mistake does not fail the build — it crashes at startup**, exactly as the missing `XamlControlsResources` did:
```bash
SimpleDeFence.UI/bin/Debug/net10.0-windows10.0.19041.0/win-x64/SimpleDeFence.UI.exe
```
Expected: window opens and stays open. If it exits immediately with `0xC000027B`, the resource dictionary path or a brush key is wrong.

- [ ] **Step 5: Commit**

```bash
git add SimpleDeFence.UI/Themes/ SimpleDeFence.UI/App.xaml
git commit -m "Add status colour roles bound to system semantic brushes"
```

---

### Task 6: The shell

Replaces the two-destination shell with the designed one and removes the Status page, whose mode switching moves into the always-visible chip.

**Deviation from the spec, deliberate:** the spec's IA has three destinations (Connections / Rules / Settings), but only Rules exists at the end of this plan. The nav therefore ships with **one** entry, and the other two are added by their own plans. Shipping nav entries that lead to empty placeholder pages would look broken and would make the shell impossible to verify honestly.

**Files:**
- Modify: `SimpleDeFence.UI/MainWindow.xaml`
- Modify: `SimpleDeFence.UI/MainWindow.xaml.cs`
- Delete: `SimpleDeFence.UI/Pages/StatusPage.xaml`, `SimpleDeFence.UI/Pages/StatusPage.xaml.cs`

**Interfaces:**
- Consumes: `ShellViewModel` (Task 4), `ModeStateToBrush` (Task 5), `FirewallModes` (Task 1), `ApplicationsPage` (existing)
- Produces: the shell; later plans add `ConnectionsPage` and `SettingsPage` destinations

- [ ] **Step 1: Write the shell XAML**

Replace `SimpleDeFence.UI/MainWindow.xaml` with:

```xml
<Window
    x:Class="SimpleDeFence.UI.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Window.SystemBackdrop>
        <MicaBackdrop Kind="BaseAlt"/>
    </Window.SystemBackdrop>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Drag region for the extended title bar. -->
        <Grid x:Name="AppTitleBar" Grid.Row="0" Height="40" Padding="16,0"
              ColumnSpacing="12" HorizontalAlignment="Stretch">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="SimpleDeFence" VerticalAlignment="Center"
                       Style="{StaticResource CaptionTextBlockStyle}"/>
        </Grid>

        <NavigationView x:Name="Nav" Grid.Row="1"
                        IsBackButtonVisible="Collapsed"
                        IsSettingsVisible="False"
                        PaneDisplayMode="Auto"
                        IsPaneToggleButtonVisible="False"
                        OpenPaneLength="220"
                        SelectionChanged="Nav_SelectionChanged">

            <NavigationView.MenuItems>
                <NavigationViewItem Content="Rules" Tag="rules" IsSelected="True">
                    <NavigationViewItem.Icon>
                        <FontIcon Glyph="&#xE71D;"/>
                    </NavigationViewItem.Icon>
                </NavigationViewItem>
            </NavigationView.MenuItems>

            <!-- Mode chip: status is ambient, not a destination. Colour + icon + word, never
                 colour alone. -->
            <NavigationView.PaneFooter>
                <Button x:Name="ModeChip" Margin="12,8,12,16" Padding="12,8"
                        HorizontalAlignment="Stretch" HorizontalContentAlignment="Left"
                        Background="{ThemeResource LayerFillColorDefaultBrush}"
                        Click="ModeChip_Click"
                        AutomationProperties.Name="Firewall mode">
                    <StackPanel Orientation="Horizontal" Spacing="10">
                        <Ellipse Width="10" Height="10" VerticalAlignment="Center"
                                 Fill="{x:Bind Shell.ModeStateKey, Mode=OneWay,
                                        Converter={StaticResource ModeStateToBrush}}"/>
                        <FontIcon FontSize="14" VerticalAlignment="Center"
                                  Glyph="{x:Bind Shell.ModeGlyph, Mode=OneWay}"/>
                        <StackPanel>
                            <TextBlock Text="{x:Bind Shell.ModeLabel, Mode=OneWay}"
                                       Style="{StaticResource BodyStrongTextBlockStyle}"/>
                            <TextBlock Text="{x:Bind Shell.StatusLine, Mode=OneWay}"
                                       Style="{StaticResource CaptionTextBlockStyle}"
                                       Opacity="0.7" TextWrapping="Wrap" MaxWidth="150"/>
                        </StackPanel>
                    </StackPanel>
                </Button>
            </NavigationView.PaneFooter>

            <Frame x:Name="ContentFrame"/>
        </NavigationView>
    </Grid>
</Window>
```

- [ ] **Step 2: Write the shell code-behind**

Replace `SimpleDeFence.UI/MainWindow.xaml.cs` with:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using SimpleDeFence.UI.Pages;
using SimpleDeFence.UI.ViewModels;
using System;

namespace SimpleDeFence.UI
{
    public sealed partial class MainWindow : Window
    {
        internal ShellViewModel Shell { get; }

        public MainWindow()
        {
            Shell = new ShellViewModel(App.Firewall);
            InitializeComponent();

            Title = "SimpleDeFence";

            // Run the nav pane to the top edge - the standard Windows 11 app shape.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            ContentFrame.Navigate(typeof(ApplicationsPage));
            _ = Shell.RefreshAsync();
        }

        // Only the destinations that actually exist are listed. Connections and Settings are
        // added by their own plans; a nav entry pointing at an empty placeholder page would be
        // worse than no entry at all.
        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item)
                return;

            Type page = item.Tag as string switch
            {
                "rules" => typeof(ApplicationsPage),
                _ => typeof(ApplicationsPage),
            };

            if (ContentFrame.CurrentSourcePageType != page)
                ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        }

        private async void ModeChip_Click(object sender, RoutedEventArgs e)
        {
            await Shell.RefreshAsync();

            if (!Shell.CanSwitchMode)
            {
                await ShowMessageAsync(
                    Shell.IsConnected ? "Configuration is locked" : "Not connected",
                    Shell.IsConnected
                        ? "Unlock the configuration before changing the mode."
                        : "Could not reach the SimpleDeFence service. Is it installed and running?");
                return;
            }

            var menu = new MenuFlyout();
            foreach (var info in FirewallModes.Selectable)
            {
                var mode = info.Mode;
                var item = new MenuFlyoutItem { Text = info.Label };
                item.Click += async (_, _) => await ApplyModeAsync(mode);
                menu.Items.Add(item);
            }

            menu.ShowAt(ModeChip);
        }

        private async System.Threading.Tasks.Task ApplyModeAsync(FirewallMode mode)
        {
            if (mode == Shell.CurrentMode)
                return;

            // Learning lets all traffic through, so it keeps the confirmation the WinForms GUI shows.
            if (mode == FirewallMode.Learning && !await ConfirmLearningModeAsync())
                return;

            MessageType resp;
            try
            {
                resp = await Shell.SwitchModeAsync(mode);
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Could not reach the service", ex.Message);
                return;
            }

            // Anything other than MODE_SWITCH is a failure. An unrecognised response must not
            // look like success - on a firewall, a mode change that did not take must not
            // appear to have taken.
            if (resp != MessageType.MODE_SWITCH)
            {
                var (title, body) = resp switch
                {
                    MessageType.RESPONSE_LOCKED => ("SimpleDeFence is currently locked",
                        "Unlock the configuration before changing the mode."),
                    MessageType.COM_ERROR => ("Communication with the service failed",
                        "The mode was not changed."),
                    _ => ("Operation failed", $"The service returned {resp}. The mode was not changed."),
                };
                await ShowMessageAsync(title, body);
            }
        }

        private async System.Threading.Tasks.Task<bool> ConfirmLearningModeAsync()
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Start automatic learning?",
                Content = "In automatic learning mode SimpleDeFence allows all traffic and remembers "
                        + "which applications used the network, then adds exceptions for them when you "
                        + "leave the mode. Rules cannot be learned for Special Exceptions.\n\n"
                        + "Only use this on a system you are confident is free of malware.",
                PrimaryButtonText = "Enter learning mode",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async System.Threading.Tasks.Task ShowMessageAsync(string title, string body)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = body,
                CloseButtonText = "OK",
            };
            await dialog.ShowAsync();
        }
    }
}
```

- [ ] **Step 3: Delete the Status page**

```bash
git rm SimpleDeFence.UI/Pages/StatusPage.xaml SimpleDeFence.UI/Pages/StatusPage.xaml.cs
```

- [ ] **Step 4: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors

- [ ] **Step 5: Run and verify the shell**

```bash
SimpleDeFence.UI/bin/Debug/net10.0-windows10.0.19041.0/win-x64/SimpleDeFence.UI.exe --sample-data
```

Verify by eye and by keyboard:
1. Window opens and **stays open** for at least 10 seconds (no `0xC000027B`).
2. Title bar is extended — the nav pane reaches the top edge, "SimpleDeFence" sits top-left.
3. Mode chip in the pane footer reads **Normal** with a green dot, a lock glyph, and the description underneath — colour *and* icon *and* word.
4. Clicking the chip opens a flyout listing all five modes; choosing **Block all** updates the chip to "Block all" with the informational colour.
5. Choosing **Autolearn** shows the confirmation dialog first; cancelling leaves the mode unchanged.
6. Narrow the window below ~640px: the nav pane collapses to overlay mode.
7. Tab through the window: the chip is reachable by keyboard and announces as "Firewall mode".

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Replace Status page with designed shell and always-visible mode chip"
```

---

## Done when

- `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj` passes.
- The net48 WinForms app still builds (Task 1 Step 5).
- The app launches with the new shell, mode switching works from the chip, and `--sample-data` populates the Applications list while the default run still reports "Not connected".

## Next plans

1. **Connections** — the landing screen: Blocked / Connected / Open sections with inline "Allow this app". Adds `GetLogAsync` and connection enumeration to `IFirewallClient`, and the activity description mapping into Core.
2. **Rules** — rework the Applications page: detail pane, add/pick flows, multi-select.
3. **Settings** — `SettingsCard` groups (adds the `CommunityToolkit.WinUI.Controls.SettingsControls` dependency).
