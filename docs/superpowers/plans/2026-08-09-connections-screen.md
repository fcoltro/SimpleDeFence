# Connections Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the WinUI 3 Connections screen — the landing destination showing Blocked / Connected / Open sections with an inline "Allow this app" action — per Phase 3 of `docs/superpowers/specs/2026-08-08-winui-gui-modernization-design.md`.

**Architecture:** Blocked-attempt data comes from the existing `READ_FW_LOG` pipe RPC (already implemented server-side); Connected/Open data is gathered locally in the GUI process via `SimpleDeFence.Windows.NetStat`, exactly as the WinForms `ConnectionsForm` already does today — no server changes needed. Process/service identity resolution (`ProcessInfo`, `ServicePidMap`) moves from the WinForms-only `SimpleDeFence` project into the shared, already-multi-targeted `SimpleDeFence.Windows`/`SimpleDeFence.Windows.Services` projects so the WinUI app can reach it too. Pure log-filtering logic (dedup, time-window, event-collapsing) goes into `SimpleDeFence.Core` as unit-tested functions, mirroring `ExceptionDescriptor`/`FirewallModeInfo`.

**Tech Stack:** C#, WinUI 3 / Windows App SDK 2.3.1, .NET 10 (`net10.0-windows10.0.19041.0`), xunit.

## Global Constraints

- Target framework for `SimpleDeFence.UI` and `SimpleDeFence.Tests` is `net10.0-windows10.0.19041.0`; `SimpleDeFence.Core` multi-targets `net48;net10.0-windows10.0.19041.0`.
- **Anything added to `SimpleDeFence.Core` must compile under net48**, because `SimpleDeFence.csproj` glob-compiles Core's sources into the net48 WinForms app. No `net10`-only APIs there.
- **Status is never signalled by colour alone** — always colour *plus* icon *plus* word (carries over from the shell plan; applies to the "Blocked" tint here too).
- **The real client is the default in every configuration.** Sample data is only reachable via the `--sample-data` command-line switch.
- **All IPC calls stay off the UI thread.**
- **Never let an unrecognised response look like success** — an action that did not take must not appear to have taken. Applies to the new `AllowAsync` exception commit exactly as it already applies to mode switching.
- **Nothing is removed from the WinForms app.** It stays buildable throughout, including `ConnectionsForm` itself (untouched — it keeps working until its own retirement plan).
- Build the net48 app with `-t:Restore` and `-t:Build` as **separate** MSBuild invocations (see ROADMAP.md).
- Localization: every new user-visible string needs a `LocKeys` constant plus matching entries in **both** `SimpleDeFence.Core/Localization/Strings.en.json` and `Strings.pt-BR.json` — `LocTests` fails the build otherwise (key-parity is enforced, not optional).
- `SimpleDeFence.Tests` runs single-threaded (`DisableTestParallelization`, added in the prior plan) because of `Loc`'s process-wide static culture state — new tests don't need to guard against this themselves, it's already handled at the assembly level.

## Deliberate scope decisions (read before objecting to a "gap")

1. **"Allow this app" uses a fixed unrestricted policy (`new TcpUdpPolicy(true)`), not the WinForms `AppDatabase` smart-lookup.** `AppDatabase.GetExceptionsForApp` (the mechanism that picks a sensible default policy for known apps) lives in the WinForms-only `SimpleDeFence` project and is itself a large, app-specific database — porting it is out of scope for this plan. `TcpUdpPolicy(true)` is not invented for this purpose: it's the same value the codebase already uses as `FirewallExceptionV3.Default`'s policy (`SimpleDeFence.Core/FirewallException.cs:24`). A future plan can add the smarter default.
2. **Section headers are not sticky while scrolling.** The design spec asks for sticky headers; WinUI 3's `Expander` (used here for the collapsible sections) doesn't support that natively, and a custom sticky-header `ScrollViewer` is a meaningfully separate piece of work. Counts stay visible on collapsed headers either way (the core ask), which is what makes the "collapse to save space, don't lose track of what's hidden" requirement actually work.
3. **No column-sort.** `ConnectionsForm` has clickable column-sort; this plan ships fixed sort order (blocked: newest first; connected/open: by app name) to keep scope bounded. Sortable columns can follow.

## File Structure

**Created:**
- `SimpleDeFence.Core/ConnectionActivity.cs` — pure log-filtering and display-name functions (net48-safe)
- `SimpleDeFence.Tests/ConnectionActivityTests.cs` — tests for the above
- `SimpleDeFence.UI/Services/ConnectionsSnapshot.cs` — `ConnectionRow`, `BlockedRow`, `ConnectionsSnapshot` DTOs
- `SimpleDeFence.UI/Pages/ConnectionsPage.xaml` / `.xaml.cs` — the new landing screen

**Modified:**
- `SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj` — multi-target net48;net10
- `SimpleDeFence.Windows.Services/ServiceControlManager.cs` — drop dead CAS attributes blocking net10
- `SimpleDeFence.Windows.Services/ProcessInfo.cs` — new file (moved from `SimpleDeFence/ProcessInfo.cs`; lives alongside `ServicePidMap`, not in `SimpleDeFence.Windows` — see Task 1 Step 1's note on why)
- `SimpleDeFence.Windows.Services/ServicePidMap.cs` — new file (moved from `SimpleDeFence/ServicePidMap.cs`)
- `SimpleDeFence/Processes.cs` — adjust the one call site that used the removed `ProcessInfo` overload
- `SimpleDeFence.UI/Services/IFirewallClient.cs` — add `GetConnectionsAsync()`, `AllowAsync(...)`
- `SimpleDeFence.UI/Services/FirewallClient.cs` — implement both
- `SimpleDeFence.UI/Services/SampleFirewallClient.cs` — fabricate sample blocked/connected/open rows
- `SimpleDeFence.UI/SimpleDeFence.UI.csproj` — add `SimpleDeFence.Windows.Services` project reference
- `SimpleDeFence.UI/MainWindow.xaml` / `.xaml.cs` — Connections becomes the landing nav destination
- `SimpleDeFence.Core/Localization/LocKeys.cs`, `Strings.en.json`, `Strings.pt-BR.json` — new `connections.*` keys

**Deleted:**
- `SimpleDeFence/ProcessInfo.cs`, `SimpleDeFence/ServicePidMap.cs` (moved, see above — WinForms still gets them via the existing glob include of `../SimpleDeFence.Windows.Services/*.cs`, so no `.csproj` change needed on the WinForms side; four call sites do need a new `using`, see Task 1 Step 6)

---

### Task 1: Portable process/service identity

Moves `ProcessInfo` and `ServicePidMap` out of the WinForms-only `SimpleDeFence` project into the already-shared `SimpleDeFence.Windows.Services` project, and multi-targets the latter to net10, so the WinUI app can resolve "which app owns this connection" the same way `ConnectionsForm` does today. No behaviour change for WinForms — its build already glob-includes that target folder (`SimpleDeFence.csproj` line 59), so it keeps compiling the same source, just from its new location.

**Files:**
- Create: `SimpleDeFence.Windows.Services/ProcessInfo.cs`, `SimpleDeFence.Windows.Services/ServicePidMap.cs`
- Delete: `SimpleDeFence/ProcessInfo.cs`, `SimpleDeFence/ServicePidMap.cs`
- Modify: `SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj`, `SimpleDeFence.Windows.Services/ServiceControlManager.cs`, `SimpleDeFence/Processes.cs`, `SimpleDeFence/ConnectionsForm.cs`, `SimpleDeFence/SimpleDeFenceController.cs`, `SimpleDeFence/ApplicationExceptionForm.cs`

**Interfaces:**
- Consumes: `UwpPackageList` (`SimpleDeFence.Core/UwpPackageList.cs`, namespace `SimpleDeFence`), `ServiceControlManager` (existing, same project)
- Produces: `SimpleDeFence.Windows.Services.ProcessInfo` (`Pid`, `Path`, `Package`, `Services`, two `Create` overloads), `SimpleDeFence.Windows.Services.ServicePidMap` (ctor, `GetServicesInPid(uint)`)

- [ ] **Step 1: Move `ProcessInfo`, dropping the WinForms-only overload**

The current `SimpleDeFence/ProcessInfo.cs` has three `Create` overloads; only two are portable (the third calls `Utils.GetPathOfProcessUseTwService(pid, GlobalInstances.Controller)`, both WinForms-app-local statics). Grep confirms exactly one call site uses that overload — `SimpleDeFence/Processes.cs:101` — everything else (including `ConnectionsForm.cs`, the file this plan's screen replaces) already uses the two portable ones.

**`ProcessInfo` goes into `SimpleDeFence.Windows.Services`, not `SimpleDeFence.Windows`, and this is not a free choice — building the project graph the other way round doesn't compile.** `ProcessInfo` needs `UwpPackageList`, which lives in `SimpleDeFence.Core`. `SimpleDeFence.Core.csproj` already has `<ProjectReference Include="..\SimpleDeFence.Windows\SimpleDeFence.Windows.csproj" />` (for `SafeSidHandle`, which `UwpPackageList`'s WinRT P/Invoke needs). So `SimpleDeFence.Windows` → `SimpleDeFence.Core` would close a cycle: `Core → Windows → Core`. `SimpleDeFence.Windows.Services` is downstream of both (it already references `Windows`) and neither `Core` nor `Windows` reference it back, so `Windows.Services → Core` is the only direction that doesn't cycle. `ServicePidMap` already lives here, so this also keeps the two types that are always used together in the same project.

Delete `SimpleDeFence/ProcessInfo.cs`. Create `SimpleDeFence.Windows.Services/ProcessInfo.cs`:

```csharp
using SimpleDeFence;
using System.Collections.Generic;

namespace SimpleDeFence.Windows.Services
{
    public class ProcessInfo
    {
        public uint Pid;
        public string Path;
        public UwpPackageList.Package? Package;
        public HashSet<string> Services;

        private ProcessInfo(uint pid, string path, UwpPackageList.Package? package, HashSet<string> services)
        {
            Pid = pid;
            Path = path;
            Package = package;
            Services = services;
        }

        public static ProcessInfo Create(uint pid, string path, UwpPackageList uwp, ServicePidMap servicePids)
        {
            return new ProcessInfo(
                pid,
                path,
                uwp.FindPackageForProcess(pid),
                servicePids.GetServicesInPid(pid)
            );
        }
        public static ProcessInfo Create(uint pid, string path, string? packageId, UwpPackageList uwp, ServicePidMap servicePids)
        {
            return new ProcessInfo(
                pid,
                path,
                uwp.FindPackage(packageId),
                servicePids.GetServicesInPid(pid)
            );
        }
    }
}
```

(`ServicePidMap` needs no `using` here — it's in the same namespace, added in Step 2.)

- [ ] **Step 2: Move `ServicePidMap`**

Delete `SimpleDeFence/ServicePidMap.cs`. Create `SimpleDeFence.Windows.Services/ServicePidMap.cs` (identical body, new namespace):

```csharp
using System;
using System.Collections.Generic;
using System.ServiceProcess;

namespace SimpleDeFence.Windows.Services
{
    public class ServicePidMap
    {
        private readonly Dictionary<uint, HashSet<string>> Cache = new();

        public ServicePidMap()
        {
            using var scm = new ServiceControlManager();
            var services = ServiceController.GetServices();
            try
            {
                foreach (var service in services)
                {
                    if (service.Status != ServiceControllerStatus.Running)
                        continue;

                    uint pid = scm.GetServicePid(service.ServiceName) ?? 0;
                    if (pid != 0)
                    {
                        if (!Cache.ContainsKey(pid))
                            Cache.Add(pid, new HashSet<string>());
                        Cache[pid].Add(service.ServiceName);
                    }
                }
            }
            finally
            {
                foreach (var service in services)
                    service.Dispose();
            }
        }

        public HashSet<string> GetServicesInPid(uint pid)
        {
            if (Cache.TryGetValue(pid, out HashSet<string> set))
                return new HashSet<string>(set);
            else
                return new HashSet<string>();
        }
    }
}
```

(Same file as before — it already lived next to `ServiceControlManager`'s *usage*, it just wasn't in the same *project*. No logic changes.)

- [ ] **Step 3: Fix the one broken WinForms call site**

`SimpleDeFence/Processes.cs:101` used the removed `ProcessInfo.Create(pid, uwp, servicePids)` overload (the one that resolved the path itself via `GlobalInstances.Controller`). Replace it with the portable 4-arg overload, resolving the path inline the same way that overload used to:

```csharp
var e = ProcessInfo.Create(pid, packageList, service_pids);
```
becomes:
```csharp
var e = ProcessInfo.Create(pid, Utils.GetPathOfProcessUseTwService(pid, GlobalInstances.Controller), packageList, service_pids);
```

- [ ] **Step 4: Multi-target `SimpleDeFence.Windows.Services` to net10**

`ServiceControlManager.cs` needs `System.ServiceProcess.ServiceController` on net10 — `System.ServiceProcess` (the net48 framework `<Reference>`) doesn't resolve there. `ServiceBase.cs` (a *different* file in this project, unrelated to anything this task needs) uses `[InstallerType(typeof(System.ServiceProcess.ServiceProcessInstaller))]`, which depends on the `System.Configuration.Install` installer pipeline — a piece of the full-framework installer model with no .NET Core/5+ equivalent at all, so it's excluded from the net10 leg rather than ported (the actual Windows Service host stays net48-only until the .NET 10 service migration in ROADMAP.md).

Replace `SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<TargetFrameworks>net48;net10.0-windows10.0.19041.0</TargetFrameworks>
		<PlatformTarget>AnyCPU</PlatformTarget>
		<LangVersion>9.0</LangVersion>
		<Nullable>enable</Nullable>
		<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
		<GenerateSerializationAssemblies>Off</GenerateSerializationAssemblies>
		<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
		<RootNamespace>SimpleDeFence.Windows.Services</RootNamespace>
		<EnableNETAnalyzers>true</EnableNETAnalyzers>
	</PropertyGroup>
	<PropertyGroup>
		<Product>SimpleDeFence.Windows.Services</Product>
		<AssemblyTitle>SimpleDeFence.Windows.Services</AssemblyTitle>
		<Company>Károly Pados</Company>
		<Copyright>Copyright © 2021 Károly Pados</Copyright>
		<Version>1.0.0</Version>
	</PropertyGroup>
	<ItemGroup Condition="'$(TargetFramework)'=='net48'">
		<PackageReference Include="Nullable" Version="1.3.0">
			<PrivateAssets>all</PrivateAssets>
			<IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
		</PackageReference>
		<Reference Include="System.ServiceProcess" />
	</ItemGroup>
	<ItemGroup Condition="'$(TargetFramework)'!='net48'">
		<!-- Restores ServiceController/ServiceControllerStatus for modern .NET; the framework-only
		     System.ServiceProcess assembly reference above doesn't resolve here. -->
		<PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.10" />
		<!-- ServiceBase.cs hosts a Windows Service via the System.Configuration.Install installer
		     pipeline, which has no .NET Core/5+ equivalent. Nothing this project's net10 consumers
		     need touches it. -->
		<Compile Remove="ServiceBase.cs" />
	</ItemGroup>
	<ItemGroup>
		<ProjectReference Include="..\SimpleDeFence.Windows\SimpleDeFence.Windows.csproj" />
		<!-- For UwpPackageList (ProcessInfo.cs, Step 1). Core itself references Windows (for
		     SafeSidHandle), so this direction only - Windows/Core must never reference
		     Windows.Services back, or the project graph cycles. -->
		<ProjectReference Include="..\SimpleDeFence.Core\SimpleDeFence.Core.csproj" />
	</ItemGroup>
</Project>
```

- [ ] **Step 5: Drop the CAS attributes blocking net10 compilation**

`ServiceControlManager.cs` has `[SecurityPermission(SecurityAction.LinkDemand, UnmanagedCode = true)]` on 7 methods/the constructor, from `System.Security.Permissions` — a CAS (Code Access Security) mechanism that was already inert under .NET Framework's default full-trust hosting and doesn't exist in the BCL on .NET Core/5+ at all. Remove the `using System.Security.Permissions;` line and every occurrence of `[SecurityPermission(SecurityAction.LinkDemand, UnmanagedCode = true)]` (there are 7, all byte-for-byte identical — use a find-all-occurrences replace, not a one-by-one edit).

- [ ] **Step 6: Point the WinForms call sites at the new namespace**

`ProcessInfo` and `ServicePidMap` moved from namespace `SimpleDeFence` (same namespace as every WinForms form, needing no `using`) to `SimpleDeFence.Windows.Services`. Four WinForms files reference `ProcessInfo` by its bare name and need the new `using` added (grep confirms these are the only four: `grep -rln "ProcessInfo\b" SimpleDeFence/*.cs`):

In `SimpleDeFence/ConnectionsForm.cs`, after the existing `using SimpleDeFence.Windows.NetStat;`:
```csharp
using SimpleDeFence.Windows.Services;
```

In `SimpleDeFence/Processes.cs`, after the existing `using SimpleDeFence.Windows;`:
```csharp
using SimpleDeFence.Windows.Services;
```

In `SimpleDeFence/SimpleDeFenceController.cs`, after the existing `using SimpleDeFence.Windows;`:
```csharp
using SimpleDeFence.Windows.Services;
```

In `SimpleDeFence/ApplicationExceptionForm.cs`, after the existing `using SimpleDeFence.Windows;`:
```csharp
using SimpleDeFence.Windows.Services;
```

- [ ] **Step 7: Build both net48 and net10 legs**

```bash
dotnet build SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj -c Debug -v:minimal -nologo
```
Expected: `Build succeeded` for both `net48` and `net10.0-windows10.0.19041.0` targets, 0 errors.

```bash
MSB="/c/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/MSBuild/Current/Bin/MSBuild.exe"
export MSBuildSDKsPath="C:\Program Files\dotnet\sdk\10.0.302\Sdks" MSBuildEnableWorkloadResolver=false
"$MSB" SimpleDeFence/SimpleDeFence.csproj -t:Restore -v:quiet -nologo
"$MSB" SimpleDeFence/SimpleDeFence.csproj -t:Build -p:Configuration=Debug -v:minimal -nologo
```
Expected: `Build succeeded`, 0 errors — confirms the WinForms app still compiles `ProcessInfo`/`ServicePidMap` from their new glob-included location, with the Step 3 and Step 6 fixes in place.

- [ ] **Step 8: Commit**

```bash
git add SimpleDeFence.Windows.Services/ProcessInfo.cs SimpleDeFence.Windows.Services/ServicePidMap.cs SimpleDeFence.Windows.Services/ServiceControlManager.cs SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj SimpleDeFence/ProcessInfo.cs SimpleDeFence/ServicePidMap.cs SimpleDeFence/Processes.cs SimpleDeFence/ConnectionsForm.cs SimpleDeFence/SimpleDeFenceController.cs SimpleDeFence/ApplicationExceptionForm.cs
git commit -m "Move process/service identity to Windows.Services, multi-target net10"
```

---

### Task 2: Core connection-activity pure functions

**Files:**
- Create: `SimpleDeFence.Core/ConnectionActivity.cs`
- Test: `SimpleDeFence.Tests/ConnectionActivityTests.cs`

**Interfaces:**
- Consumes: `FirewallLogEntry`, `EventLogEvent` (`SimpleDeFence.Core/FirewallLogEntry.cs`, already exist)
- Produces: `SimpleDeFence.ConnectionActivity` with `static FirewallLogEntry Collapse(FirewallLogEntry)`, `static IReadOnlyList<FirewallLogEntry> RecentBlocked(IEnumerable<FirewallLogEntry>, DateTime now, TimeSpan window)`, `static string DisplayName(string? path, string? packageDisplayName, IReadOnlyCollection<string>? services)`

- [ ] **Step 1: Write the failing tests**

Create `SimpleDeFence.Tests/ConnectionActivityTests.cs`:

```csharp
using SimpleDeFence;
using System;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class ConnectionActivityTests
    {
        private static FirewallLogEntry Entry(EventLogEvent ev, DateTime ts, uint pid = 100, int remotePort = 443, string remoteIp = "1.2.3.4") => new()
        {
            Timestamp = ts,
            Event = ev,
            ProcessId = pid,
            Protocol = Protocol.TCP,
            Direction = RuleDirection.Out,
            LocalIp = "10.0.0.5",
            RemoteIp = remoteIp,
            LocalPort = 51000,
            RemotePort = remotePort,
            AppPath = @"C:\app.exe",
        };

        [Fact]
        public void Collapse_maps_every_blocked_variant_to_BLOCKED()
        {
            var now = DateTime.Now;
            Assert.Equal(EventLogEvent.BLOCKED, ConnectionActivity.Collapse(Entry(EventLogEvent.BLOCKED_CONNECTION, now)).Event);
            Assert.Equal(EventLogEvent.BLOCKED, ConnectionActivity.Collapse(Entry(EventLogEvent.BLOCKED_LISTEN, now)).Event);
            Assert.Equal(EventLogEvent.BLOCKED, ConnectionActivity.Collapse(Entry(EventLogEvent.BLOCKED_PACKET, now)).Event);
            Assert.Equal(EventLogEvent.BLOCKED, ConnectionActivity.Collapse(Entry(EventLogEvent.BLOCKED_LOCAL_BIND, now)).Event);
        }

        [Fact]
        public void Collapse_maps_every_allowed_variant_to_ALLOWED()
        {
            var now = DateTime.Now;
            Assert.Equal(EventLogEvent.ALLOWED, ConnectionActivity.Collapse(Entry(EventLogEvent.ALLOWED_CONNECTION, now)).Event);
            Assert.Equal(EventLogEvent.ALLOWED, ConnectionActivity.Collapse(Entry(EventLogEvent.ALLOWED_LISTEN, now)).Event);
            Assert.Equal(EventLogEvent.ALLOWED, ConnectionActivity.Collapse(Entry(EventLogEvent.ALLOWED_LOCAL_BIND, now)).Event);
        }

        [Fact]
        public void RecentBlocked_excludes_entries_outside_the_window()
        {
            var now = DateTime.Now;
            var entries = new[]
            {
                Entry(EventLogEvent.BLOCKED, now.AddMinutes(-2)),
                Entry(EventLogEvent.BLOCKED, now.AddMinutes(-10)),
            };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Single(result);
            Assert.Equal(now.AddMinutes(-2), result[0].Timestamp);
        }

        [Fact]
        public void RecentBlocked_excludes_allowed_entries()
        {
            var now = DateTime.Now;
            var entries = new[] { Entry(EventLogEvent.ALLOWED_CONNECTION, now) };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Empty(result);
        }

        [Fact]
        public void RecentBlocked_deduplicates_repeated_attempts_keeping_the_latest_timestamp()
        {
            var now = DateTime.Now;
            var entries = new[]
            {
                Entry(EventLogEvent.BLOCKED_CONNECTION, now.AddMinutes(-3), pid: 200, remotePort: 443, remoteIp: "1.1.1.1"),
                Entry(EventLogEvent.BLOCKED_CONNECTION, now.AddMinutes(-1), pid: 200, remotePort: 443, remoteIp: "1.1.1.1"),
            };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Single(result);
            Assert.Equal(now.AddMinutes(-1), result[0].Timestamp);
        }

        [Fact]
        public void RecentBlocked_keeps_distinct_ports_as_separate_rows()
        {
            var now = DateTime.Now;
            var entries = new[]
            {
                Entry(EventLogEvent.BLOCKED_CONNECTION, now, pid: 200, remotePort: 443),
                Entry(EventLogEvent.BLOCKED_CONNECTION, now, pid: 200, remotePort: 8080),
            };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void RecentBlocked_orders_newest_first()
        {
            var now = DateTime.Now;
            var entries = new[]
            {
                Entry(EventLogEvent.BLOCKED, now.AddMinutes(-4), pid: 1),
                Entry(EventLogEvent.BLOCKED, now.AddMinutes(-1), pid: 2),
                Entry(EventLogEvent.BLOCKED, now.AddMinutes(-2), pid: 3),
            };

            var result = ConnectionActivity.RecentBlocked(entries, now, TimeSpan.FromMinutes(5));

            Assert.Equal(new uint[] { 2, 3, 1 }, new[] { result[0].ProcessId, result[1].ProcessId, result[2].ProcessId });
        }

        [Fact]
        public void DisplayName_prefers_package_then_services_then_executable_filename()
        {
            Assert.Equal("Contoso App", ConnectionActivity.DisplayName(@"C:\app.exe", "Contoso App", new[] { "SomeSvc" }));
            Assert.Equal("DoSvc, UsoSvc", ConnectionActivity.DisplayName(@"C:\Windows\System32\svchost.exe", null, new[] { "DoSvc", "UsoSvc" }));
            Assert.Equal("app.exe", ConnectionActivity.DisplayName(@"C:\Program Files\app.exe", null, null));
        }

        [Fact]
        public void DisplayName_returns_empty_when_nothing_is_known()
        {
            Assert.Equal(string.Empty, ConnectionActivity.DisplayName(null, null, null));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: FAIL — `The type or namespace name 'ConnectionActivity' could not be found`

- [ ] **Step 3: Write the implementation**

Create `SimpleDeFence.Core/ConnectionActivity.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleDeFence
{
    /// <summary>
    /// Pure transformations over the firewall's raw event log and resolved process identity,
    /// shared by both GUIs. Mirrors filtering the WinForms ConnectionsForm already does ad hoc in
    /// its code-behind, extracted here so it is unit-tested and reusable from the WinUI GUI too.
    /// </summary>
    public static class ConnectionActivity
    {
        /// <summary>
        /// Collapses the Security-log event-ID variants (_LISTEN/_CONNECTION/_PACKET/_LOCAL_BIND)
        /// down to a plain BLOCKED or ALLOWED, so callers only ever need to branch on two outcomes.
        /// </summary>
        public static FirewallLogEntry Collapse(FirewallLogEntry entry) => entry with
        {
            Event = IsBlockedEvent(entry.Event) ? EventLogEvent.BLOCKED : EventLogEvent.ALLOWED,
        };

        private static bool IsBlockedEvent(EventLogEvent e) => e switch
        {
            EventLogEvent.BLOCKED
                or EventLogEvent.BLOCKED_LISTEN
                or EventLogEvent.BLOCKED_CONNECTION
                or EventLogEvent.BLOCKED_PACKET
                or EventLogEvent.BLOCKED_LOCAL_BIND => true,
            _ => false,
        };

        /// <summary>
        /// The blocked attempts worth showing right now: collapsed to BLOCKED/ALLOWED, restricted to
        /// entries within <paramref name="window"/> of <paramref name="now"/>, deduplicated (repeated
        /// attempts matching on everything but timestamp collapse into one row keeping the latest
        /// time), and newest first.
        /// </summary>
        public static IReadOnlyList<FirewallLogEntry> RecentBlocked(
            IEnumerable<FirewallLogEntry> entries, DateTime now, TimeSpan window)
        {
            var cutoff = now - window;
            var deduped = new List<FirewallLogEntry>();

            foreach (var raw in entries)
            {
                if (raw.Timestamp < cutoff || raw.Timestamp > now)
                    continue;

                var collapsed = Collapse(raw);
                if (collapsed.Event != EventLogEvent.BLOCKED)
                    continue;

                var existingIndex = deduped.FindIndex(e => e.Equals(collapsed, includeTimestamp: false));
                if (existingIndex < 0)
                    deduped.Add(collapsed);
                else if (collapsed.Timestamp > deduped[existingIndex].Timestamp)
                    deduped[existingIndex] = collapsed;
            }

            return deduped.OrderByDescending(e => e.Timestamp).ToList();
        }

        /// <summary>
        /// What to call a row identifying a process: a UWP package's display name if it has one,
        /// else the hosted Windows service name(s) if any (e.g. "DoSvc, UsoSvc" for a shared
        /// svchost.exe), else the executable's filename, else empty if nothing is known.
        /// </summary>
        public static string DisplayName(string? path, string? packageDisplayName, IReadOnlyCollection<string>? services)
        {
            if (!string.IsNullOrEmpty(packageDisplayName))
                return packageDisplayName!;

            if (services is { Count: > 0 })
                return string.Join(", ", services.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(path))
                return System.IO.Path.GetFileName(path);

            return string.Empty;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS — all tests green

- [ ] **Step 5: Verify the net48 WinForms app still compiles**

```bash
MSB="/c/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/MSBuild/Current/Bin/MSBuild.exe"
export MSBuildSDKsPath="C:\Program Files\dotnet\sdk\10.0.302\Sdks" MSBuildEnableWorkloadResolver=false
"$MSB" SimpleDeFence/SimpleDeFence.csproj -t:Restore -v:quiet -nologo
"$MSB" SimpleDeFence/SimpleDeFence.csproj -t:Build -p:Configuration=Debug -v:minimal -nologo
```
Expected: `Build succeeded`, 0 errors

- [ ] **Step 6: Commit**

```bash
git add SimpleDeFence.Core/ConnectionActivity.cs SimpleDeFence.Tests/ConnectionActivityTests.cs
git commit -m "Add Core connection-activity filtering, shared by both GUIs"
```

---

### Task 3: Extend IFirewallClient with connection data and the allow-exception commit

**Files:**
- Create: `SimpleDeFence.UI/Services/ConnectionsSnapshot.cs`
- Modify: `SimpleDeFence.UI/Services/IFirewallClient.cs`, `SimpleDeFence.UI/Services/FirewallClient.cs`, `SimpleDeFence.UI/Services/SampleFirewallClient.cs`, `SimpleDeFence.UI/SimpleDeFence.UI.csproj`

**Interfaces:**
- Consumes: `Controller.BeginReadFwLog()`/`EndReadFwLog()`/`TryGetProcessPath()` (Core), `ConnectionActivity` (Task 2), `SimpleDeFence.Windows.NetStat.NetStat`/`TcpRow`/`UdpRow`, `SimpleDeFence.Windows.Services.ProcessInfo`/`ServicePidMap`, `UwpPackageList` (all Task 1/existing)
- Produces: `SimpleDeFence.UI.Services.ConnectionRow`, `BlockedRow`, `ConnectionsSnapshot`; `IFirewallClient.GetConnectionsAsync()`, `IFirewallClient.AllowAsync(ExceptionSubject, ExceptionPolicy)`

- [ ] **Step 1: Add the DTOs**

Create `SimpleDeFence.UI/Services/ConnectionsSnapshot.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace SimpleDeFence.UI.Services
{
    /// <summary>One row in the Connected or Open section: a live TCP/UDP endpoint.</summary>
    internal sealed class ConnectionRow
    {
        public uint ProcessId { get; init; }
        public string AppName { get; init; } = string.Empty;
        public string AppPath { get; init; } = string.Empty;
        public string Protocol { get; init; } = string.Empty;
        public string LocalAddress { get; init; } = string.Empty;
        public int LocalPort { get; init; }
        public string RemoteAddress { get; init; } = string.Empty;
        public int RemotePort { get; init; }
        public string State { get; init; } = string.Empty;
    }

    /// <summary>One row in the Blocked section - enough to build an "Allow this app" exception.</summary>
    internal sealed class BlockedRow
    {
        public DateTime Timestamp { get; init; }
        public uint ProcessId { get; init; }
        public string AppName { get; init; } = string.Empty;
        public string? AppPath { get; init; }
        public string? PackageId { get; init; }
        public string Protocol { get; init; } = string.Empty;
        public string Direction { get; init; } = string.Empty;
        public string RemoteAddress { get; init; } = string.Empty;
        public int RemotePort { get; init; }
    }

    /// <summary>Everything the Connections screen renders in one refresh.</summary>
    internal sealed class ConnectionsSnapshot
    {
        public IReadOnlyList<BlockedRow> Blocked { get; init; } = Array.Empty<BlockedRow>();
        public IReadOnlyList<ConnectionRow> Connected { get; init; } = Array.Empty<ConnectionRow>();
        public IReadOnlyList<ConnectionRow> Open { get; init; } = Array.Empty<ConnectionRow>();
    }
}
```

- [ ] **Step 2: Extend the interface**

In `SimpleDeFence.UI/Services/IFirewallClient.cs`, add two members inside the interface body (after `SwitchModeAsync`):

```csharp
        /// <summary>Blocked/Connected/Open, gathered fresh on every call - no caching, this is a
        /// point-in-time view of live network state.</summary>
        Task<ConnectionsSnapshot> GetConnectionsAsync();

        /// <summary>Commits a new exception for the given subject with the given policy.</summary>
        Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy);
```

- [ ] **Step 3: Reference `SimpleDeFence.Windows.Services` from the UI project**

In `SimpleDeFence.UI/SimpleDeFence.UI.csproj`, add the project reference (this transitively brings in `SimpleDeFence.Windows` too, since `Windows.Services` already references it):

```xml
  <ItemGroup>
    <ProjectReference Include="..\SimpleDeFence.Core\SimpleDeFence.Core.csproj" />
    <ProjectReference Include="..\SimpleDeFence.Windows.Services\SimpleDeFence.Windows.Services.csproj" />
  </ItemGroup>
```

- [ ] **Step 4: Implement the real client**

In `SimpleDeFence.UI/Services/FirewallClient.cs`, add `using System.Collections.Generic;` and `using SimpleDeFence.Windows.NetStat;` and `using SimpleDeFence.Windows.Services;` to the top of the file (`ProcessInfo` and `ServicePidMap` both live in `Windows.Services` per Task 1's correction — no separate `SimpleDeFence.Windows` using is needed), then add these members (after `SwitchModeAsync`):

```csharp
        public Task<ConnectionsSnapshot> GetConnectionsAsync()
        {
            return Task.Run(() =>
            {
                // Fire the log request first so it overlaps the local NetStat/service-table
                // gathering below, the same overlap ConnectionsForm already relies on.
                var logRequest = _controller.BeginReadFwLog();

                var uwp = new UwpPackageList();
                var servicePids = new ServicePidMap();

                var connected = new List<ConnectionRow>();
                var open = new List<ConnectionRow>();
                CollectTcp(NetStat.GetExtendedTcp4Table(false), uwp, servicePids, connected, open);
                CollectTcp(NetStat.GetExtendedTcp6Table(false), uwp, servicePids, connected, open);
                CollectUdp(NetStat.GetExtendedUdp4Table(false), uwp, servicePids, open);
                CollectUdp(NetStat.GetExtendedUdp6Table(false), uwp, servicePids, open);

                var rawLog = Controller.EndReadFwLog(logRequest.Response);
                var recentBlocked = ConnectionActivity.RecentBlocked(rawLog, DateTime.Now, TimeSpan.FromMinutes(5));
                var blocked = recentBlocked.Select(entry => BlockedRowFrom(entry, uwp, servicePids)).ToList();

                return new ConnectionsSnapshot
                {
                    Blocked = blocked,
                    Connected = connected.OrderBy(r => r.AppName, StringComparer.CurrentCultureIgnoreCase).ToList(),
                    Open = open.OrderBy(r => r.AppName, StringComparer.CurrentCultureIgnoreCase).ToList(),
                };
            });
        }

        public Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy)
        {
            return Task.Run(() =>
            {
                if (Config is null)
                    return MessageType.RESPONSE_ERROR;

                var clone = SerializationHelper.Deserialize(SerializationHelper.Serialize(Config), Config);
                clone.ActiveProfile.AddExceptions(new List<FirewallExceptionV3> { new(subject, policy) });

                var resp = _controller.SetServerConfig(clone, _changeset);
                if (resp is TwMessagePutSettings putResp && resp.Type == MessageType.PUT_SETTINGS)
                {
                    _changeset = putResp.Changeset;
                    Config = putResp.Config;
                    if (putResp.State is not null)
                        State = putResp.State;
                }

                return resp.Type;
            });
        }

        private string ResolvePath(uint pid) => _controller.TryGetProcessPath(pid);

        private void CollectTcp(TcpTable table, UwpPackageList uwp, ServicePidMap servicePids,
            List<ConnectionRow> connected, List<ConnectionRow> open)
        {
            foreach (var row in table)
            {
                var path = ResolvePath(row.ProcessId);
                var info = ProcessInfo.Create(row.ProcessId, path, uwp, servicePids);
                var connectionRow = new ConnectionRow
                {
                    ProcessId = row.ProcessId,
                    AppName = ConnectionActivity.DisplayName(info.Path, info.Package?.Name, info.Services),
                    AppPath = info.Path,
                    Protocol = "TCP",
                    LocalAddress = row.LocalEndPoint.Address.ToString(),
                    LocalPort = row.LocalEndPoint.Port,
                    RemoteAddress = row.RemoteEndPoint.Address.ToString(),
                    RemotePort = row.RemoteEndPoint.Port,
                    State = row.State.ToString(),
                };

                (row.State == TcpState.Listen ? open : connected).Add(connectionRow);
            }
        }

        private void CollectUdp(UdpTable table, UwpPackageList uwp, ServicePidMap servicePids, List<ConnectionRow> open)
        {
            foreach (var row in table)
            {
                var path = ResolvePath(row.ProcessId);
                var info = ProcessInfo.Create(row.ProcessId, path, uwp, servicePids);

                // UDP has no connection state; a bound socket is always "listening" in the sense
                // this screen cares about.
                open.Add(new ConnectionRow
                {
                    ProcessId = row.ProcessId,
                    AppName = ConnectionActivity.DisplayName(info.Path, info.Package?.Name, info.Services),
                    AppPath = info.Path,
                    Protocol = "UDP",
                    LocalAddress = row.LocalEndPoint.Address.ToString(),
                    LocalPort = row.LocalEndPoint.Port,
                    RemoteAddress = string.Empty,
                    RemotePort = 0,
                    State = "Listen",
                });
            }
        }

        private BlockedRow BlockedRowFrom(FirewallLogEntry entry, UwpPackageList uwp, ServicePidMap servicePids)
        {
            var path = entry.AppPath ?? ResolvePath(entry.ProcessId);
            var info = ProcessInfo.Create(entry.ProcessId, path, entry.PackageId, uwp, servicePids);

            return new BlockedRow
            {
                Timestamp = entry.Timestamp,
                ProcessId = entry.ProcessId,
                AppName = ConnectionActivity.DisplayName(info.Path, info.Package?.Name, info.Services),
                AppPath = info.Path,
                PackageId = entry.PackageId,
                Protocol = entry.Protocol.ToString(),
                Direction = entry.Direction.ToString(),
                RemoteAddress = entry.RemoteIp ?? string.Empty,
                RemotePort = entry.RemotePort,
            };
        }
```

Add `using System;`, `using System.Linq;`, and `using System.Net.NetworkInformation;` (for `TcpState`) to the top of the file alongside the ones already there.

- [ ] **Step 5: Implement the sample client**

In `SimpleDeFence.UI/Services/SampleFirewallClient.cs`, add:

```csharp
        public Task<ConnectionsSnapshot> GetConnectionsAsync()
        {
            var snapshot = new ConnectionsSnapshot
            {
                Blocked = new List<BlockedRow>
                {
                    new()
                    {
                        Timestamp = DateTime.Now.AddSeconds(-40),
                        ProcessId = 4242,
                        AppName = "tracker.exe",
                        AppPath = @"C:\Users\sample\AppData\Local\Telemetry\tracker.exe",
                        Protocol = "TCP",
                        Direction = "Out",
                        RemoteAddress = "203.0.113.9",
                        RemotePort = 443,
                    },
                },
                Connected = new List<ConnectionRow>
                {
                    new()
                    {
                        ProcessId = 5150,
                        AppName = "firefox.exe",
                        AppPath = @"C:\Program Files\Mozilla Firefox\firefox.exe",
                        Protocol = "TCP",
                        LocalAddress = "10.0.0.5",
                        LocalPort = 51234,
                        RemoteAddress = "142.250.72.14",
                        RemotePort = 443,
                        State = "Established",
                    },
                },
                Open = new List<ConnectionRow>
                {
                    new()
                    {
                        ProcessId = 1044,
                        AppName = "DoSvc",
                        AppPath = @"C:\Windows\System32\svchost.exe",
                        Protocol = "UDP",
                        LocalAddress = "0.0.0.0",
                        LocalPort = 5353,
                        RemoteAddress = string.Empty,
                        RemotePort = 0,
                        State = "Listen",
                    },
                },
            };

            return Task.FromResult(snapshot);
        }

        public Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy)
        {
            if (_locked)
                return Task.FromResult(MessageType.RESPONSE_LOCKED);

            if (Config is null)
                return Task.FromResult(MessageType.RESPONSE_ERROR);

            Config.ActiveProfile.AddExceptions(new List<FirewallExceptionV3> { new(subject, policy) });
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(MessageType.PUT_SETTINGS);
        }
```

Add `using System.Collections.Generic;` to the top of the file.

- [ ] **Step 6: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors

- [ ] **Step 7: Verify the net48 WinForms app still compiles**

```bash
MSB="/c/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/MSBuild/Current/Bin/MSBuild.exe"
export MSBuildSDKsPath="C:\Program Files\dotnet\sdk\10.0.302\Sdks" MSBuildEnableWorkloadResolver=false
"$MSB" SimpleDeFence/SimpleDeFence.csproj -t:Restore -v:quiet -nologo
"$MSB" SimpleDeFence/SimpleDeFence.csproj -t:Build -p:Configuration=Debug -v:minimal -nologo
```
Expected: `Build succeeded`, 0 errors (this task doesn't touch anything WinForms glob-compiles, so this is a regression check, not expected to find anything)

- [ ] **Step 8: Commit**

```bash
git add SimpleDeFence.UI/Services/ SimpleDeFence.UI/SimpleDeFence.UI.csproj
git commit -m "Extend IFirewallClient with connection data and the allow-exception commit"
```

---

### Task 4: ConnectionsPage — three collapsible sections, read-only

Builds the page shell: three `Expander` sections (Blocked / Connected / Open) with live counts in their headers, empty/loading/error states, no interactivity beyond expand/collapse yet. Deliberately not wired into navigation yet (Task 6) so it can be verified in isolation first.

**Files:**
- Create: `SimpleDeFence.UI/Pages/ConnectionsPage.xaml`, `SimpleDeFence.UI/Pages/ConnectionsPage.xaml.cs`
- Modify: `SimpleDeFence.Core/Localization/LocKeys.cs`, `Strings.en.json`, `Strings.pt-BR.json`

**Interfaces:**
- Consumes: `App.Firewall.GetConnectionsAsync()` (Task 3), `ConnectionRow`/`BlockedRow` (Task 3)
- Produces: `SimpleDeFence.UI.Pages.ConnectionsPage`, `ConnectionsPage.ConnectionListItem` (view row for Connected/Open), `ConnectionsPage.BlockedListItem` (view row for Blocked, Task 5 adds the Allow button binding)

- [ ] **Step 1: Add localization keys**

In `SimpleDeFence.Core/Localization/LocKeys.cs`, add a new nested class after `Applications`:

```csharp
        public static class Connections
        {
            public const string Title = "connections.title";
            public const string SectionBlocked = "connections.section.blocked";
            public const string SectionConnected = "connections.section.connected";
            public const string SectionOpen = "connections.section.open";
            public const string SectionCount = "connections.section.count";
            public const string EmptyBlocked = "connections.empty.blocked";
            public const string EmptyConnected = "connections.empty.connected";
            public const string EmptyOpen = "connections.empty.open";
            public const string FilterPlaceholder = "connections.filterPlaceholder";
            public const string AutoRefresh = "connections.autoRefresh";
            public const string Allow = "connections.allow";
            public const string AllowSuccessTitle = "connections.allowSuccess.title";
            public const string AllowSuccessBody = "connections.allowSuccess.body";
            public const string AllowFailedTitle = "connections.allowFailed.title";
        }
```

In `SimpleDeFence.Core/Localization/Strings.en.json`, add after the `"applications"` block (keep the trailing comma on `"applications"`'s closing brace):

```json
  "connections": {
    "title": "Connections",
    "section": {
      "blocked": "Blocked",
      "connected": "Connected",
      "open": "Open",
      "count": "({0})"
    },
    "empty": {
      "blocked": "Nothing blocked in the last 5 minutes.",
      "connected": "No established connections.",
      "open": "No listening ports."
    },
    "filterPlaceholder": "Filter by app name",
    "autoRefresh": "Auto-refresh",
    "allow": "Allow this app",
    "allowSuccess": {
      "title": "Exception added",
      "body": "{0} can now connect."
    },
    "allowFailed": {
      "title": "Could not add the exception"
    }
  },

```

In `SimpleDeFence.Core/Localization/Strings.pt-BR.json`, add the matching Portuguese block in the same position:

```json
  "connections": {
    "title": "Conexões",
    "section": {
      "blocked": "Bloqueadas",
      "connected": "Conectadas",
      "open": "Abertas",
      "count": "({0})"
    },
    "empty": {
      "blocked": "Nada bloqueado nos últimos 5 minutos.",
      "connected": "Nenhuma conexão estabelecida.",
      "open": "Nenhuma porta aberta."
    },
    "filterPlaceholder": "Filtrar por nome do aplicativo",
    "autoRefresh": "Atualização automática",
    "allow": "Permitir este aplicativo",
    "allowSuccess": {
      "title": "Exceção adicionada",
      "body": "{0} agora pode se conectar."
    },
    "allowFailed": {
      "title": "Não foi possível adicionar a exceção"
    }
  },

```

- [ ] **Step 2: Run the localization tests to verify key parity**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj --filter FullyQualifiedName~LocTests`
Expected: PASS — confirms the new `LocKeys.Connections.*` constants match both JSON files exactly (this is `LocTests`' whole purpose; a mismatch here means a typo in one of the three places just edited).

- [ ] **Step 3: Write the page code-behind**

Create `SimpleDeFence.UI/Pages/ConnectionsPage.xaml.cs`:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SimpleDeFence;
using SimpleDeFence.Localization;
using SimpleDeFence.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    /// <summary>Connected/Open row as shown in a list.</summary>
    public sealed class ConnectionListItem
    {
        public string AppName { get; init; } = string.Empty;
        public string Protocol { get; init; } = string.Empty;
        public string LocalEndpoint { get; init; } = string.Empty;
        public string RemoteEndpoint { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
    }

    public sealed partial class ConnectionsPage : Page
    {
        private ConnectionsSnapshot _snapshot = new();
        private readonly ObservableCollection<ConnectionListItem> _connected = new();
        private readonly ObservableCollection<ConnectionListItem> _open = new();
        private bool _busy;

        public ConnectionsPage()
        {
            InitializeComponent();
            // Keeps this page instance (and therefore each Expander's IsExpanded) alive across
            // navigating to Rules and back, instead of Frame recreating it - the closest match to
            // the spec's "collapsible, remembered state" without persisting to disk.
            NavigationCacheMode = NavigationCacheMode.Enabled;
            ConnectedList.ItemsSource = _connected;
            OpenList.ItemsSource = _open;
            Loaded += ConnectionsPage_Loaded;
        }

        private async void ConnectionsPage_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_busy)
                return;

            SetBusy(true);
            await App.Firewall.RefreshAsync();

            if (!App.Firewall.Connected)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Status.NotConnected), App.Firewall.LastError ?? string.Empty);
                _snapshot = new ConnectionsSnapshot();
            }
            else
            {
                Notice.IsOpen = false;
                _snapshot = await App.Firewall.GetConnectionsAsync();
            }

            SetBusy(false);
            Rebuild();
        }

        private void Rebuild()
        {
            ApplyFilter();
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            var term = FilterBox.Text?.Trim() ?? string.Empty;
            bool Matches(string name) => term.Length == 0 || name.Contains(term, StringComparison.CurrentCultureIgnoreCase);

            _connected.Clear();
            foreach (var row in _snapshot.Connected.Where(r => Matches(r.AppName)))
                _connected.Add(ItemFrom(row));

            _open.Clear();
            foreach (var row in _snapshot.Open.Where(r => Matches(r.AppName)))
                _open.Add(ItemFrom(row));

            RebuildBlocked(_snapshot.Blocked.Where(r => Matches(r.AppName)));

            SetHeader(BlockedHeaderText, LocKeys.Connections.SectionBlocked, BlockedCount());
            SetHeader(ConnectedHeaderText, LocKeys.Connections.SectionConnected, _connected.Count);
            SetHeader(OpenHeaderText, LocKeys.Connections.SectionOpen, _open.Count);

            // Empty states read reassuringly rather than like a failure - an empty Blocked list is
            // a *good* outcome on a firewall.
            ConnectedEmpty.Visibility = _connected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            OpenEmpty.Visibility = _open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BlockedEmpty.Visibility = BlockedCount() == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void SetHeader(TextBlock target, string titleKey, int count)
            => target.Text = Loc.T(titleKey) + " " + Loc.T(LocKeys.Connections.SectionCount, count);

        // Overridden by Task 5, which adds the Blocked list and its Allow action. Left as a
        // count-only stub here so this task is independently verifiable.
        private int BlockedCount() => _snapshot.Blocked.Count;
        private void RebuildBlocked(IEnumerable<BlockedRow> rows) { }

        private static ConnectionListItem ItemFrom(ConnectionRow row) => new()
        {
            AppName = string.IsNullOrEmpty(row.AppName) ? Loc.T(LocKeys.Common.Unknown) : row.AppName,
            Protocol = row.Protocol,
            LocalEndpoint = $"{row.LocalAddress}:{row.LocalPort}",
            RemoteEndpoint = row.RemotePort == 0 ? string.Empty : $"{row.RemoteAddress}:{row.RemotePort}",
            State = row.State,
        };

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
```

- [ ] **Step 4: Write the page XAML**

Create `SimpleDeFence.UI/Pages/ConnectionsPage.xaml`:

```xml
<Page
    x:Class="SimpleDeFence.UI.Pages.ConnectionsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:SimpleDeFence.UI.Pages"
    xmlns:loc="using:SimpleDeFence.UI.Localization">

    <Grid Padding="28" RowSpacing="14">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="{loc:Loc Key=connections.title}" Style="{StaticResource TitleTextBlockStyle}"/>

        <InfoBar Grid.Row="1" x:Name="Notice" IsOpen="False" IsClosable="True"/>

        <Grid Grid.Row="2" ColumnSpacing="12">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBox Grid.Column="0" x:Name="FilterBox" PlaceholderText="{loc:Loc Key=connections.filterPlaceholder}"
                     TextChanged="FilterBox_TextChanged" MaxWidth="360" HorizontalAlignment="Left"/>
            <Button Grid.Column="1" x:Name="RefreshButton" Content="{loc:Loc Key=common.refresh}" Click="RefreshButton_Click"/>
            <ProgressRing Grid.Column="2" x:Name="Busy" IsActive="False" Width="20" Height="20" VerticalAlignment="Center"/>
        </Grid>

        <ScrollViewer Grid.Row="3">
            <StackPanel Spacing="12">
                <Expander x:Name="BlockedExpander" IsExpanded="True" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
                    <Expander.Header>
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <FontIcon FontSize="16" Glyph="&#xE785;"/>
                            <TextBlock x:Name="BlockedHeaderText" Style="{StaticResource SubtitleTextBlockStyle}"/>
                        </StackPanel>
                    </Expander.Header>
                    <StackPanel>
                        <TextBlock x:Name="BlockedEmpty" Text="{loc:Loc Key=connections.empty.blocked}"
                                   Style="{StaticResource BodyTextBlockStyle}" Opacity="0.7" Margin="0,8" Visibility="Collapsed"/>
                        <ListView x:Name="BlockedList" SelectionMode="None"/>
                    </StackPanel>
                </Expander>

                <Expander x:Name="ConnectedExpander" IsExpanded="True" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
                    <Expander.Header>
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <FontIcon FontSize="16" Glyph="&#xE8AB;"/>
                            <TextBlock x:Name="ConnectedHeaderText" Style="{StaticResource SubtitleTextBlockStyle}"/>
                        </StackPanel>
                    </Expander.Header>
                    <StackPanel>
                        <TextBlock x:Name="ConnectedEmpty" Text="{loc:Loc Key=connections.empty.connected}"
                                   Style="{StaticResource BodyTextBlockStyle}" Opacity="0.7" Margin="0,8" Visibility="Collapsed"/>
                        <ListView x:Name="ConnectedList" SelectionMode="None">
                        <ListView.ItemTemplate>
                            <DataTemplate x:DataType="local:ConnectionListItem">
                                <Grid Padding="0,6" ColumnSpacing="14">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="220"/>
                                        <ColumnDefinition Width="60"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Grid.Column="0" Text="{x:Bind AppName}" Style="{StaticResource BodyStrongTextBlockStyle}" TextTrimming="CharacterEllipsis"/>
                                    <TextBlock Grid.Column="1" Text="{x:Bind Protocol}" Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.8"/>
                                    <TextBlock Grid.Column="2" Text="{x:Bind RemoteEndpoint}" Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.8" TextTrimming="CharacterEllipsis"/>
                                    <TextBlock Grid.Column="3" Text="{x:Bind State}" Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.8"/>
                                </Grid>
                            </DataTemplate>
                        </ListView.ItemTemplate>
                        </ListView>
                    </StackPanel>
                </Expander>

                <Expander x:Name="OpenExpander" IsExpanded="True" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
                    <Expander.Header>
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <FontIcon FontSize="16" Glyph="&#xE703;"/>
                            <TextBlock x:Name="OpenHeaderText" Style="{StaticResource SubtitleTextBlockStyle}"/>
                        </StackPanel>
                    </Expander.Header>
                    <StackPanel>
                        <TextBlock x:Name="OpenEmpty" Text="{loc:Loc Key=connections.empty.open}"
                                   Style="{StaticResource BodyTextBlockStyle}" Opacity="0.7" Margin="0,8" Visibility="Collapsed"/>
                        <ListView x:Name="OpenList" SelectionMode="None">
                        <ListView.ItemTemplate>
                            <DataTemplate x:DataType="local:ConnectionListItem">
                                <Grid Padding="0,6" ColumnSpacing="14">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="220"/>
                                        <ColumnDefinition Width="60"/>
                                        <ColumnDefinition Width="*"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock Grid.Column="0" Text="{x:Bind AppName}" Style="{StaticResource BodyStrongTextBlockStyle}" TextTrimming="CharacterEllipsis"/>
                                    <TextBlock Grid.Column="1" Text="{x:Bind Protocol}" Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.8"/>
                                    <TextBlock Grid.Column="2" Text="{x:Bind LocalEndpoint}" Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.8" TextTrimming="CharacterEllipsis"/>
                                </Grid>
                            </DataTemplate>
                        </ListView.ItemTemplate>
                        </ListView>
                    </StackPanel>
                </Expander>
            </StackPanel>
        </ScrollViewer>
    </Grid>
</Page>
```

Note `BlockedList` deliberately has no `ItemTemplate` yet — Task 5 adds `BlockedListItem` and its template together with the Allow button, since they're the same piece of work.

- [ ] **Step 5: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors

- [ ] **Step 6: Verify by running against sample data**

```bash
SimpleDeFence.UI/bin/Debug/net10.0-windows10.0.19041.0/win-x64/SimpleDeFence.UI.exe --sample-data
```

`ConnectionsPage` isn't reachable from the nav yet (Task 6). Verify it directly by temporarily changing `ContentFrame.Navigate(typeof(ApplicationsPage))` to `ContentFrame.Navigate(typeof(ConnectionsPage))` in `MainWindow.xaml.cs`'s constructor, running, and confirming: three sections render, headers show live counts, Connected shows the sample firefox.exe row, Open shows the sample DoSvc row, filtering by "firefox" narrows to just that row. Then **revert the temporary change** (Task 6 does this properly, wired into the nav).

- [ ] **Step 7: Commit**

```bash
git add SimpleDeFence.UI/Pages/ConnectionsPage.xaml SimpleDeFence.UI/Pages/ConnectionsPage.xaml.cs SimpleDeFence.Core/Localization/
git commit -m "Add ConnectionsPage shell with Connected/Open sections"
```

---

### Task 5: Blocked section and inline "Allow this app"

**Files:**
- Modify: `SimpleDeFence.UI/Pages/ConnectionsPage.xaml`, `SimpleDeFence.UI/Pages/ConnectionsPage.xaml.cs`

**Interfaces:**
- Consumes: `App.Firewall.AllowAsync(ExceptionSubject, ExceptionPolicy)` (Task 3), `BlockedRow` (Task 3)
- Produces: `ConnectionsPage.BlockedListItem` (`AppName`, `Detail`, `When`, `AllowCommand`-equivalent via `Click`)

- [ ] **Step 1: Add the Blocked row view model with its Allow action**

In `SimpleDeFence.UI/Pages/ConnectionsPage.xaml.cs`, add a new class above `ConnectionsPage` (next to `ConnectionListItem`):

```csharp
    /// <summary>Blocked row as shown in a list, carrying what "Allow this app" needs to act on.</summary>
    public sealed class BlockedListItem
    {
        public string AppName { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string When { get; init; } = string.Empty;
        public string? AppPath { get; init; }
        public string? PackageId { get; init; }

        public event EventHandler? AllowRequested;
        public void RequestAllow() => AllowRequested?.Invoke(this, EventArgs.Empty);
    }
```

- [ ] **Step 2: Replace the Blocked stub with the real implementation**

Replace the `BlockedCount`/`RebuildBlocked` stub pair from Task 4 with:

```csharp
        private readonly ObservableCollection<BlockedListItem> _blocked = new();

        private int BlockedCount() => _blocked.Count;

        private void RebuildBlocked(IEnumerable<BlockedRow> rows)
        {
            _blocked.Clear();
            foreach (var row in rows)
            {
                var item = new BlockedListItem
                {
                    AppName = string.IsNullOrEmpty(row.AppName) ? Loc.T(LocKeys.Common.Unknown) : row.AppName,
                    Detail = $"{row.Protocol} {row.Direction} \u2192 {row.RemoteAddress}:{row.RemotePort}",
                    When = row.Timestamp.ToString("HH:mm:ss"),
                    AppPath = row.AppPath,
                    PackageId = row.PackageId,
                };
                item.AllowRequested += async (_, _) => await AllowAsync(item);
                _blocked.Add(item);
            }
        }
```

(Delete the old one-line stub versions of both members and the `using System.Collections.Generic;` `RebuildBlocked` parameter type is unaffected — it still takes `IEnumerable<BlockedRow>`.)

In the constructor, wire up the list's items source next to the other two:

```csharp
            BlockedList.ItemsSource = _blocked;
```

- [ ] **Step 3: Add the Allow handler**

Add this method to `ConnectionsPage`:

```csharp
        private async Task AllowAsync(BlockedListItem item)
        {
            ExceptionSubject subject = !string.IsNullOrEmpty(item.PackageId)
                ? new AppContainerSubject(item.PackageId, item.AppName, string.Empty, string.Empty)
                : new ExecutableSubject(item.AppPath ?? string.Empty);

            MessageType resp;
            try
            {
                resp = await App.Firewall.AllowAsync(subject, new TcpUdpPolicy(true));
            }
            catch (Exception ex)
            {
                await ShowAllowResultAsync(Loc.T(LocKeys.Connections.AllowFailedTitle), ex.Message);
                return;
            }

            if (resp == MessageType.PUT_SETTINGS)
            {
                await ShowAllowResultAsync(Loc.T(LocKeys.Connections.AllowSuccessTitle),
                    Loc.T(LocKeys.Connections.AllowSuccessBody, item.AppName));
                await RefreshAsync();
            }
            else
            {
                var body = resp switch
                {
                    MessageType.RESPONSE_LOCKED => Loc.T(LocKeys.Status.LockedDetail),
                    _ => Loc.T(LocKeys.Mode.SwitchFailedGenericDetail, resp),
                };
                await ShowAllowResultAsync(Loc.T(LocKeys.Connections.AllowFailedTitle), body);
            }
        }

        private async Task ShowAllowResultAsync(string title, string body)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = body,
                CloseButtonText = Loc.T(LocKeys.Common.Ok),
            };
            await dialog.ShowAsync();
        }
```

Note the `AppContainerSubject(item.PackageId, item.AppName, string.Empty, string.Empty)` constructor call: `AppContainerSubject`'s 4-arg constructor is `(string sid, string displayName, string publisher, string publisherId)` (`SimpleDeFence.Core/ExceptionSubject.cs:409`). A blocked log entry only carries a `PackageId`, not the full package metadata `UwpPackageList.Package` would have — publisher/publisherId are left blank here rather than invented. This is honest given what the log actually reports, not a shortcut; a `sid`-only match is exactly what `ExceptionSubject.Equals` compares on for this type (`ExceptionSubject.cs:427`).

Add `using System.Threading.Tasks;` if not already present (it is, from Task 4).

- [ ] **Step 4: Add the item template to the XAML**

In `SimpleDeFence.UI/Pages/ConnectionsPage.xaml`, replace `<ListView x:Name="BlockedList" SelectionMode="None"/>` with:

```xml
                    <ListView x:Name="BlockedList" SelectionMode="None">
                        <ListView.ItemTemplate>
                            <DataTemplate x:DataType="local:BlockedListItem">
                                <Grid Padding="0,6" ColumnSpacing="14"
                                      Background="{ThemeResource SystemFillColorCriticalBackgroundBrush}">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="Auto"/>
                                        <ColumnDefinition Width="220"/>
                                        <ColumnDefinition Width="*"/>
                                        <ColumnDefinition Width="Auto"/>
                                        <ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <FontIcon Grid.Column="0" FontSize="14" Glyph="&#xE785;" VerticalAlignment="Center"
                                              Foreground="{ThemeResource SystemFillColorCriticalBrush}"/>
                                    <TextBlock Grid.Column="1" Text="{x:Bind AppName}" Style="{StaticResource BodyStrongTextBlockStyle}" TextTrimming="CharacterEllipsis"/>
                                    <TextBlock Grid.Column="2" Text="{x:Bind Detail}" Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.8" TextTrimming="CharacterEllipsis"/>
                                    <TextBlock Grid.Column="3" Text="{x:Bind When}" Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.6" VerticalAlignment="Center"/>
                                    <Button Grid.Column="4" Content="{loc:Loc Key=connections.allow}"
                                            Click="{x:Bind AllowButton_Click}"/>
                                </Grid>
                            </DataTemplate>
                        </ListView.ItemTemplate>
                    </ListView>
```

`x:Bind` to an event needs a matching-signature method, not a lambda, so `BlockedListItem` needs one more member — add it next to `RequestAllow`:

```csharp
        public void AllowButton_Click(object sender, RoutedEventArgs e) => RequestAllow();
```

(This means `BlockedListItem` needs `using Microsoft.UI.Xaml;` for `RoutedEventArgs` - add it to the top of `ConnectionsPage.xaml.cs`; it's already imported there from Task 4.)

Every status/action row here also carries an icon and text alongside the red background - colour is never the only signal, matching the Global Constraint carried over from the shell plan.

- [ ] **Step 5: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors

- [ ] **Step 6: Verify by running against sample data**

Same temporary-navigate trick as Task 4 Step 6 (`ContentFrame.Navigate(typeof(ConnectionsPage))`), then:
1. Blocked section shows the sample tracker.exe attempt, with a red-tinted row, a blocked icon, and an "Allow this app" button — colour, icon, and word together.
2. Click "Allow this app" — a confirmation dialog appears ("Exception added..."), and the row disappears from Blocked on the refresh that follows (since `--sample-data`'s `SampleFirewallClient.AllowAsync` mutates `Config` but the *log* entry itself doesn't change — verify at minimum that the dialog shows success and no exception is thrown; the row persisting in the *log* view is expected, since a real blocked-log entry is history, not live state, and the WinForms `ConnectionsForm` has this same property).
3. Run with `--sample-locked` and confirm clicking Allow shows the locked failure dialog instead.

Revert the temporary navigation change afterward (Task 6 does this properly).

- [ ] **Step 7: Commit**

```bash
git add SimpleDeFence.UI/Pages/ConnectionsPage.xaml SimpleDeFence.UI/Pages/ConnectionsPage.xaml.cs
git commit -m "Add Blocked section with inline Allow-this-app action"
```

---

### Task 6: Auto-refresh, and nav integration

Wires `ConnectionsPage` into the shell as the landing destination, and adds the auto-refresh toggle the design spec calls for. Search and empty/loading/error states are already done (Tasks 4-5); this task is what makes the screen actually reachable.

**Files:**
- Modify: `SimpleDeFence.UI/Pages/ConnectionsPage.xaml`, `SimpleDeFence.UI/Pages/ConnectionsPage.xaml.cs`, `SimpleDeFence.UI/MainWindow.xaml`, `SimpleDeFence.UI/MainWindow.xaml.cs`, `SimpleDeFence.Core/Localization/LocKeys.cs` (already has `AutoRefresh`, added Task 4), `Strings.en.json`/`Strings.pt-BR.json` (same)

**Interfaces:**
- Consumes: `ConnectionsPage` (Tasks 4-5)
- Produces: `MainWindow` now navigates to `ConnectionsPage` first; nav has two destinations

- [ ] **Step 1: Add the auto-refresh toggle**

In `SimpleDeFence.UI/Pages/ConnectionsPage.xaml`, add a `ToggleSwitch` to the filter row (Grid.Row="2"), between the filter box and the refresh button — change that Grid's column definitions and add the control:

```xml
        <Grid Grid.Row="2" ColumnSpacing="12">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBox Grid.Column="0" x:Name="FilterBox" PlaceholderText="{loc:Loc Key=connections.filterPlaceholder}"
                     TextChanged="FilterBox_TextChanged" MaxWidth="360" HorizontalAlignment="Left"/>
            <ToggleSwitch Grid.Column="1" x:Name="AutoRefreshToggle" OnContent="{loc:Loc Key=connections.autoRefresh}"
                          OffContent="{loc:Loc Key=connections.autoRefresh}" Toggled="AutoRefreshToggle_Toggled"/>
            <Button Grid.Column="2" x:Name="RefreshButton" Content="{loc:Loc Key=common.refresh}" Click="RefreshButton_Click"/>
            <ProgressRing Grid.Column="3" x:Name="Busy" IsActive="False" Width="20" Height="20" VerticalAlignment="Center"/>
        </Grid>
```

In `ConnectionsPage.xaml.cs`, add the timer and its handlers:

```csharp
        private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };

        // (in the constructor, after Loaded += ConnectionsPage_Loaded;)
        _autoRefreshTimer.Tick += async (_, _) => await RefreshAsync();
        Unloaded += (_, _) => _autoRefreshTimer.Stop();

        private void AutoRefreshToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (AutoRefreshToggle.IsOn)
                _autoRefreshTimer.Start();
            else
                _autoRefreshTimer.Stop();
        }
```

`DispatcherTimer` is `Microsoft.UI.Xaml.DispatcherTimer` - already covered by the existing `using Microsoft.UI.Xaml;`.

- [ ] **Step 2: Add Connections as a nav destination, landing first**

In `SimpleDeFence.UI/MainWindow.xaml`, replace the `NavigationView.MenuItems` block:

```xml
            <NavigationView.MenuItems>
                <NavigationViewItem Content="{loc:Loc Key=connections.title}" Tag="connections" IsSelected="True">
                    <NavigationViewItem.Icon>
                        <FontIcon Glyph="&#xE774;"/>
                    </NavigationViewItem.Icon>
                </NavigationViewItem>
                <NavigationViewItem Content="{loc:Loc Key=nav.rules}" Tag="rules">
                    <NavigationViewItem.Icon>
                        <FontIcon Glyph="&#xE71D;"/>
                    </NavigationViewItem.Icon>
                </NavigationViewItem>
            </NavigationView.MenuItems>
```

- [ ] **Step 3: Route by tag and land on Connections**

In `SimpleDeFence.UI/MainWindow.xaml.cs`, add `using SimpleDeFence.UI.Pages;` (already present) and update the constructor and selection handler:

```csharp
            ContentFrame.Navigate(typeof(ConnectionsPage));
```
(replaces `ContentFrame.Navigate(typeof(ApplicationsPage));`)

```csharp
        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item)
                return;

            var targetType = (string)item.Tag switch
            {
                "connections" => typeof(ConnectionsPage),
                "rules" => typeof(ApplicationsPage),
                _ => typeof(ConnectionsPage),
            };

            if (ContentFrame.CurrentSourcePageType != targetType)
                ContentFrame.Navigate(targetType, null, new EntranceNavigationTransitionInfo());
        }
```

(This replaces the single-destination stub from the shell plan - the comment there already said "Rules is currently the only destination... at which point this maps item.Tag to a page type," which is exactly what's happening now.)

- [ ] **Step 4: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors

- [ ] **Step 5: Run and verify the whole screen end-to-end**

```bash
SimpleDeFence.UI/bin/Debug/net10.0-windows10.0.19041.0/win-x64/SimpleDeFence.UI.exe --sample-data
```

Verify:
1. App launches directly onto Connections (not Rules) - the nav pane's first item is selected and highlighted.
2. Clicking "Rules" in the nav still shows the Applications page unchanged; clicking back to "Connections" returns to this screen without re-fetching unnecessarily.
3. Turn on auto-refresh, wait >5s, confirm the header counts still read correctly (no exceptions, no UI freeze - this is the concrete proof IPC/NetStat gathering isn't blocking the UI thread).
4. Run without `--sample-data`: Connections shows "Not connected" (InfoBar), same as Applications does today - the real client stays the default.
5. Run `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj` - full suite, all green, including the new `ConnectionActivityTests` and unaffected `LocTests`.

- [ ] **Step 6: Verify the net48 WinForms app still compiles**

```bash
MSB="/c/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/MSBuild/Current/Bin/MSBuild.exe"
export MSBuildSDKsPath="C:\Program Files\dotnet\sdk\10.0.302\Sdks" MSBuildEnableWorkloadResolver=false
"$MSB" SimpleDeFence/SimpleDeFence.csproj -t:Restore -v:quiet -nologo
"$MSB" SimpleDeFence/SimpleDeFence.csproj -t:Build -p:Configuration=Debug -v:minimal -nologo
```
Expected: `Build succeeded`, 0 errors

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Wire Connections into the shell as the landing destination"
```

---

## Done when

- `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj` passes (including new `ConnectionActivityTests`, and `LocTests` with the new `connections.*` keys).
- The net48 WinForms app still builds after every task.
- `SimpleDeFence.Windows.Services` builds clean on both `net48` and `net10.0-windows10.0.19041.0`.
- The app launches onto Connections by default; Blocked/Connected/Open populate from `--sample-data`; "Allow this app" on a blocked row commits an exception and reports success/failure honestly; auto-refresh runs without blocking the UI thread; the real client remains the default with no `--sample-data`.

## Next plans

1. **Rules** — rework the Applications page: detail pane, add/pick flows, multi-select.
2. **Settings** — `SettingsCard` groups (adds the `CommunityToolkit.WinUI.Controls.SettingsControls` dependency).
3. **Smarter "Allow this app"** — port (or reimplement) `AppDatabase`-style known-app policy suggestions instead of always defaulting to unrestricted.
4. **Sticky section headers / sortable columns** — the two scope decisions deferred at the top of this plan.
