# net10 Retarget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retarget `SimpleDeFence.csproj` (the WinForms exe hosting both the service and the controller GUI) from net48 to net10, replacing its `System.Configuration.Install`-based service install/uninstall with direct Win32 calls, and repackage the MSI as a self-contained, win-x64 installer.

**Architecture:** `ServiceControlManager` (already net48/net10 dual-target, pure P/Invoke) gains `CreateService`/`DeleteService`, retiring the entire `Installer/` folder and its `ManagedInstallerClass` calls. `SimpleDeFence.csproj` then hard-cuts to `net10.0-windows10.0.19041.0` (no dual-targeting), with its framework references retargeted to their net10 equivalents. The MSI moves from a fully hand-authored `<Component>` list (viable for net48's ~9 small dependency DLLs) to `heat.exe`-harvested components for the self-contained publish's much larger file set.

**Tech Stack:** C#, .NET 10, WinForms, Win32 P/Invoke (advapi32), WiX Toolset v3, xunit.

**Spec:** `docs/superpowers/specs/2026-08-13-net10-retarget-design.md`

## Global Constraints

- **Hard cutover, no dual-targeting.** `SimpleDeFence.csproj` moves straight to `net10.0-windows10.0.19041.0`. There is no net48 fallback to preserve.
- **No behavior change anywhere in this plan.** Every task either substitutes a mechanism while preserving existing error-handling/logging exactly (install/uninstall), or is a reference-only retarget with no expected source change. The IPC protocol, WFP rule construction, and the service's actual firewall behavior are untouched.
- **Nothing removed from WinForms beyond the `Installer/` folder itself.** The controller GUI, tray icon, hosts-file management, and DevelTool are all still WinForms, all still reachable exactly as today — this plan changes what they run on, not what they do.
- **x64 only.** Self-contained publishing needs a specific `RuntimeIdentifier` per architecture; this plan produces `win-x64` only, matching `SimpleDeFence.UI.csproj`'s own existing choice. The MSI's x86/arm64 variants have nothing to package once net48 is gone — extending self-contained publishing to those architectures is an explicit, named follow-up, not silently dropped.
- **Known environment gaps, both already present before this plan, one expected to be fixed by it:**
  - This machine's Framework MSBuild has no `Microsoft.DotNet.MSBuildSdkResolver`, so it cannot build any SDK-style net48 project — a pre-existing gap, unrelated to this plan.
  - `SimpleDeFence.csproj` currently also fails a plain `dotnet build` (net48) with `MSB3822`/`MSB3823`, a `.resx`/`GenerateResourceUsePreserializedResources` issue specific to net48 — Task 2 is expected to fix this as a side effect of retargeting, not something to work around.
  - The WiX Toolset (`heat.exe`, `candle.exe`, `light.exe`) is **not installed on this machine** — confirmed by searching for it before writing this plan. Task 3 cannot be build-verified in this environment; its steps say so explicitly rather than claim a test that can't run here.
- **Every P/Invoke addition matches the existing style** in `NativeMethods.cs`/`ServiceControlManager.cs`: `SafeServiceHandle` for handle returns, `Win32Exception(Marshal.GetLastWin32Error())` thrown on failure (never swallowed inside the P/Invoke wrapper itself — the existing caller-side try/catch in `SimpleDeFenceDoctor` is the only error-handling layer, unchanged).

---

## Task 1: `SimpleDeFence.Windows.Services` — service creation/deletion, net10-ready `ServiceBase`

**Files:**
- Modify: `SimpleDeFence.Windows.Services/NativeStructs.cs`
- Modify: `SimpleDeFence.Windows.Services/NativeMethods.cs`
- Modify: `SimpleDeFence.Windows.Services/ServiceControlManager.cs`
- Modify: `SimpleDeFence.Windows.Services/ServiceBase.cs`
- Modify: `SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj`

**Interfaces:**
- Consumes: `NativeMethods.OpenSCManager`/`OpenService` (existing), `SafeServiceHandle` (existing, `SimpleDeFence.Windows.Services/SafeHandles.cs`)
- Produces: `ServiceControlManager.CreateService(string serviceName, string displayName, string binaryPath, string[] dependencies)`, `ServiceControlManager.DeleteService(string serviceName)` — Task 2 consumes both.

Why: `ServiceControlManager` already has every access-right value and every supporting method (`SetLoadOrderGroup`, the private `OpenService` helper) it needs except the two Win32 calls that actually create/delete a service registration. `ServiceBase.cs`'s only net10 blocker is one unused attribute.

- [ ] **Step 1: Add the missing `DELETE` access right**

In `SimpleDeFence.Windows.Services/NativeStructs.cs`, find the `ServiceAccessRights` enum (currently ends with `SERVICE_ALL_ACCESS = 0xF01FF`) and add a `DELETE` member — the standard Win32 `DELETE` standard-rights value, needed to open a service handle for deletion:

```csharp
    [Flags]
    public enum ServiceAccessRights : int
    {
        SERVICE_QUERY_CONFIG = 0x0001, // Required to call the QueryServiceConfig and QueryServiceConfig2 functions to query the service configuration. 
        SERVICE_CHANGE_CONFIG = 0x0002, // Required to call the ChangeServiceConfig or ChangeServiceConfig2 function to change the service configuration. Because this grants the caller the right to change the executable file that the system runs, it should be granted only to administrators. 
        SERVICE_QUERY_STATUS = 0x0004, // Required to call the QueryServiceStatusEx function to ask the service control manager about the status of the service. 
        SERVICE_ENUMERATE_DEPENDENTS = 0x0008, // Required to call the EnumDependentServices function to enumerate all the services dependent on the service. 
        SERVICE_START = 0x0010, // Required to call the StartService function to start the service. 
        SERVICE_STOP = 0x0020, // Required to call the ControlService function to stop the service. 
        SERVICE_PAUSE_CONTINUE = 0x0040, // Required to call the ControlService function to pause or continue the service. 
        SERVICE_INTERROGATE = 0x0080, // Required to call the ControlService function to ask the service to report its status immediately. 
        SERVICE_USER_DEFINED_CONTROL = 0x0100, // Required to call the ControlService function to specify a user-defined control code.
        DELETE = 0x00010000, // Required to call the DeleteService function to delete the service.
        SERVICE_ALL_ACCESS = 0xF01FF // Includes STANDARD_RIGHTS_REQUIRED in addition to all access rights in this table. 
    }
```

- [ ] **Step 2: Add the `CreateService`/`DeleteService` P/Invoke declarations**

In `SimpleDeFence.Windows.Services/NativeMethods.cs`, add these two declarations after the existing `OpenService` declaration (matching its exact style — `SafeServiceHandle` return, `CharSet.Unicode`, `SetLastError = true`):

```csharp
        [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeServiceHandle CreateService(
            SafeServiceHandle hSCManager,
            string lpServiceName,
            string lpDisplayName,
            ServiceAccessRights dwDesiredAccess,
            ServiceType dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string? lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string? lpDependencies,
            string? lpServiceStartName,
            string? lpPassword);

        [DllImport("advapi32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteService(SafeServiceHandle hService);
```

`ServiceType` is the existing enum in `NativeStructs.cs` (already used by `ServiceBase.cs` as `ServiceType.SERVICE_TYPE_WIN32_OWN_PROCESS`) — no new type needed.

- [ ] **Step 3: Widen the SCM handle's requested access rights**

In `SimpleDeFence.Windows.Services/ServiceControlManager.cs`, the constructor currently opens the SCM handle with `SC_MANAGER_CONNECT` only — too narrow for `CreateService`, which needs `SC_MANAGER_CREATE_SERVICE` on that same handle (the access-right *value* already exists in `ServiceControlAccessRights`; nothing currently requests it). Every existing caller of `new ServiceControlManager()` (`SimpleDeFenceDoctor.EnsureHealth`, `.EnsureServiceDependencies`) is already admin-gated, so this doesn't change any caller's actual privilege level — it only widens what this class itself asks for. Replace:

```csharp
        public ServiceControlManager()
        {
            // Open the service control manager
            SCManager = NativeMethods.OpenSCManager(
                null,
                null,
                ServiceControlAccessRights.SC_MANAGER_CONNECT);
```

with:

```csharp
        public ServiceControlManager()
        {
            // Open the service control manager
            SCManager = NativeMethods.OpenSCManager(
                null,
                null,
                ServiceControlAccessRights.SC_MANAGER_CONNECT | ServiceControlAccessRights.SC_MANAGER_CREATE_SERVICE);
```

(`ServiceControlAccessRights` is already `[Flags]`, so the `|` combination is valid as-is.)

- [ ] **Step 4: Add `CreateService`**

In `SimpleDeFence.Windows.Services/ServiceControlManager.cs`, add this method after `SetLoadOrderGroup` (it calls `SetLoadOrderGroup` itself, so needs to come after):

```csharp
        /// <summary>
        /// Registers a new Win32 service, LocalSystem account, automatic start. Mirrors what
        /// SimpleDeFenceServiceInstaller (System.Configuration.Install-based, net48-only) used to
        /// do: create the service, then set its load-order group to "NetworkProvider" - both are
        /// needed for the firewall service to start in the right order relative to networking.
        /// </summary>
        public void CreateService(string serviceName, string displayName, string binaryPath, string[] dependencies)
        {
            const uint SERVICE_AUTO_START = 0x00000002;
            const uint SERVICE_ERROR_NORMAL = 0x00000001;

            // CreateService expects a double-null-terminated multi-string for dependencies (each
            // entry separated by one embedded '\0', with an extra trailing '\0' so the automatic
            // terminator .NET's Unicode string marshaling appends becomes the second one). No
            // dependencies means a literal null pointer, not an empty string.
            string? dependenciesMultiString = dependencies.Length == 0
                ? null
                : string.Join('\0', dependencies) + "\0";

            using var service = NativeMethods.CreateService(
                SCManager,
                serviceName,
                displayName,
                ServiceAccessRights.SERVICE_ALL_ACCESS,
                ServiceType.SERVICE_TYPE_WIN32_OWN_PROCESS,
                SERVICE_AUTO_START,
                SERVICE_ERROR_NORMAL,
                binaryPath,
                null,
                IntPtr.Zero,
                dependenciesMultiString,
                null,
                null);

            if (service.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            SetLoadOrderGroup(serviceName, @"NetworkProvider");
        }
```

- [ ] **Step 5: Add `DeleteService`**

Immediately after `CreateService`:

```csharp
        public void DeleteService(string serviceName)
        {
            using var service = OpenService(serviceName, ServiceAccessRights.DELETE);

            if (!NativeMethods.DeleteService(service))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
```

- [ ] **Step 6: Build to verify (both frameworks — this project still dual-targets)**

Run: `dotnet build SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj -f net48 -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors.

Run: `dotnet build SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj -f net10.0-windows10.0.19041.0 -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 7: Remove `ServiceBase.cs`'s dead installer-metadata attribute**

In `SimpleDeFence.Windows.Services/ServiceBase.cs`, this attribute is never read at runtime by this hand-rolled implementation — it references `System.ServiceProcess.ServiceProcessInstaller`, which is `System.Configuration.Install`-derived and has no net10 equivalent, which is why the whole file is currently excluded from the net10 build. Remove the line:

```csharp
    [InstallerType(typeof(System.ServiceProcess.ServiceProcessInstaller))]
    public abstract class ServiceBase : IDisposable
```

becomes:

```csharp
    public abstract class ServiceBase : IDisposable
```

- [ ] **Step 8: Drop the net10 exclusion for `ServiceBase.cs`**

In `SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj`, remove the now-unnecessary compile exclusion and its comment:

```xml
  <ItemGroup Condition="'$(TargetFramework)'!='net48'">
    <!-- Restores ServiceController/ServiceControllerStatus for modern .NET; the framework-only
         System.ServiceProcess assembly reference above doesn't resolve here. -->
    <PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.10" />
    <!-- ServiceBase.cs hosts a Windows Service via the System.Configuration.Install installer
         pipeline, which has no .NET Core/5+ equivalent. Nothing this project's net10 consumers
         need touches it. -->
    <Compile Remove="ServiceBase.cs" />
  </ItemGroup>
```

becomes:

```xml
  <ItemGroup Condition="'$(TargetFramework)'!='net48'">
    <!-- Restores ServiceController/ServiceControllerStatus for modern .NET; the framework-only
         System.ServiceProcess assembly reference above doesn't resolve here. -->
    <PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.10" />
  </ItemGroup>
```

- [ ] **Step 9: Build again to confirm `ServiceBase.cs` now compiles on net10**

Run: `dotnet build SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj -f net10.0-windows10.0.19041.0 -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors. If `[InstallerType(...)]`'s removal alone isn't sufficient (some other net10-incompatible construct surfaces), the build error names it — fix that specific issue and rebuild; do not restore the `<Compile Remove>` guard as a workaround.

- [ ] **Step 10: Commit**

```bash
git add SimpleDeFence.Windows.Services/NativeStructs.cs SimpleDeFence.Windows.Services/NativeMethods.cs SimpleDeFence.Windows.Services/ServiceControlManager.cs SimpleDeFence.Windows.Services/ServiceBase.cs SimpleDeFence.Windows.Services/SimpleDeFence.Windows.Services.csproj
git commit -m "Add ServiceControlManager.CreateService/DeleteService; make ServiceBase.cs net10-clean"
```

## Task 2: Retarget `SimpleDeFence.csproj` to net10

**Files:**
- Modify: `SimpleDeFence/SimpleDeFenceDoctor.cs`
- Delete: `SimpleDeFence/Installer/SimpleDeFenceInstaller.cs`
- Delete: `SimpleDeFence/Installer/SimpleDeFenceServiceInstaller.cs`
- Modify: `SimpleDeFence/SimpleDeFence.csproj`

**Interfaces:**
- Consumes: `ServiceControlManager.CreateService`/`.DeleteService` (Task 1)
- Produces: a net10, self-contained, win-x64 build of `SimpleDeFence.exe` — Task 3 publishes and packages it.

Why: these four files are the only ones referencing `ManagedInstallerClass`/`System.Configuration.Install`/the net48-only framework references, and none of them build-verify independently of each other in this environment (this project can't be built via either toolchain here until it's retargeted) — they land together as one task.

- [ ] **Step 1: Rewrite `EnsureServiceInstalledAndRunning`'s install call**

In `SimpleDeFence/SimpleDeFenceDoctor.cs`, replace:

```csharp
            if (Utils.RunningAsAdmin())
            {
                // Run installers
                try
                {
                    ManagedInstallerClass.InstallHelper(new string[] { "/i", Utils.ExecutablePath });
                }
                catch(Exception e)
                {
                    Utils.LogException(e, logContext);
                }
```

with:

```csharp
            if (Utils.RunningAsAdmin())
            {
                // Run installers
                try
                {
                    using var scm = new ServiceControlManager();
                    scm.CreateService(SimpleDeFenceService.SERVICE_NAME, SimpleDeFenceService.SERVICE_DISPLAY_NAME, Utils.ExecutablePath, SimpleDeFenceService.ServiceDependencies);
                }
                catch(Exception e)
                {
                    Utils.LogException(e, logContext);
                }
```

(The rest of `EnsureServiceInstalledAndRunning` — the `EnsureHealth` call, starting the service — is unchanged.)

- [ ] **Step 2: Rewrite `Uninstall`'s uninstall call**

In the same file, replace:

```csharp
            try
            {
                ManagedInstallerClass.InstallHelper(new string[] { "/u", Utils.ExecutablePath });
            }
            catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_INSTALLER); }

            return 0;
```

with:

```csharp
            try
            {
                using var scm = new ServiceControlManager();
                scm.DeleteService(SimpleDeFenceService.SERVICE_NAME);
            }
            catch (Exception e) { Utils.LogException(e, Utils.LOG_ID_INSTALLER); }

            return 0;
```

- [ ] **Step 3: Remove the now-unused `System.Configuration.Install` import**

At the top of `SimpleDeFence/SimpleDeFenceDoctor.cs`, remove the line:

```csharp
using System.Configuration.Install;
```

`using SimpleDeFence.Windows.Services;` is already present in this file's using block — no new import is needed for `ServiceControlManager`.

- [ ] **Step 4: Delete the `Installer/` folder**

```bash
git rm SimpleDeFence/Installer/SimpleDeFenceInstaller.cs SimpleDeFence/Installer/SimpleDeFenceServiceInstaller.cs
```

Nothing else references either class — `ManagedInstallerClass.InstallHelper` was the only caller, and both call sites were rewritten in Steps 1-2.

- [ ] **Step 5: Retarget the framework and references**

In `SimpleDeFence/SimpleDeFence.csproj`, replace the first `PropertyGroup`'s framework declaration:

```xml
    <TargetFramework>net48</TargetFramework>
```

with:

```xml
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
```

Remove the now-obsolete WinRT-interop platform version line and its comment (superseded by the TFM's own platform version):

```xml
    <!-- Used for WinRT access in .Net 4.8, not needed with .NET 5+ -->
    <TargetPlatformVersion>8.0</TargetPlatformVersion>
```

Add self-contained publishing settings to the same `PropertyGroup`:

```xml
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
```

- [ ] **Step 6: Retarget the reference `ItemGroup`**

Replace:

```xml
  <ItemGroup>
    <Reference Include="System.Configuration.Install" />
    <!-- Required by the late-bound COM call sites (dynamic). Part of the shared framework on
         .NET 5+, so this reference is net48-only. -->
    <Reference Include="Microsoft.CSharp" />
    <Reference Include="System.Management" />
    <Reference Include="System.Security" />
    <Reference Include="System.ServiceProcess" />
    <Reference Include="System.Windows.Forms" />
    
    <!-- Used for WinRT access in .Net 4.8, not needed with .NET 5+ -->
    <Reference Include="Windows.Management" />
    <Reference Include="Windows.ApplicationModel" />
    <Reference Include="System.Runtime" />

  </ItemGroup>
```

with:

```xml
  <ItemGroup>
    <PackageReference Include="System.Management" Version="10.0.0" />
    <PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.10" />
  </ItemGroup>
```

`System.Configuration.Install`, `Microsoft.CSharp`, `System.Security`, `Windows.Management`, `Windows.ApplicationModel`, and `System.Runtime` all drop outright — each is either gone (the first) or already part of the net10 shared framework (the rest), matching the comments already on this block before the change. `System.Windows.Forms` drops too: it's redundant with `<UseWindowsForms>true</UseWindowsForms>` (already set in this file's first `PropertyGroup`, unchanged by this plan), which is the correct, sufficient way to reference WinForms on net10 — a bare `<Reference Include="System.Windows.Forms" />` is net48 GAC syntax and won't resolve under net10.

If `System.Management`'s `10.0.0` package version doesn't resolve, check [nuget.org](https://www.nuget.org/packages/System.Management) for the latest version compatible with `net10.0-windows10.0.19041.0` and use that instead — the exact patch version is not load-bearing, only that `System.Management.ManagementEventWatcher`/`WqlEventQuery` (used by `SimpleDeFenceService.cs`'s `ProcessStartWatcher`) resolve.

- [ ] **Step 7: Build**

Run: `dotnet build SimpleDeFence/SimpleDeFence.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors. This is the first time this exact command has been expected to work in this environment — previously blocked by the net48-specific `.resx` issue noted in this plan's Global Constraints.

If new errors surface beyond what this task's steps anticipated (for example, another file using a namespace this task didn't retarget), fix them in place and note what was needed in the task's completion report — don't silently expand scope without saying so.

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS, same count as before this task. Nothing in this task touches any code `SimpleDeFence.Tests` exercises (it references `SimpleDeFence.Core` only, not `SimpleDeFence` or `SimpleDeFence.Windows.Services`) — this run exists to catch any unexpected cross-project regression, not because a specific new behavior needs covering.

- [ ] **Step 9: Commit**

```bash
git add SimpleDeFence/SimpleDeFenceDoctor.cs SimpleDeFence/SimpleDeFence.csproj
git commit -m "Retarget SimpleDeFence.csproj to net10: install/uninstall via ServiceControlManager, reference retarget, self-contained win-x64"
```

## Task 3: MSI packaging — self-contained publish, harvested dependencies

**Files:**
- Modify: `MsiSetup/MsiSetup.wixproj`
- Modify: `MsiSetup/Product.wxs`
- Create (tool-generated, not hand-written): `MsiSetup/Dependencies.wxs`

**Interfaces:**
- Consumes: the net10, self-contained `SimpleDeFence.csproj` build (Task 2)
- Produces: an installable MSI with no .NET Framework prerequisite

Why: today's `Product.wxs` hand-authors one `<Component>` per DLL — viable for net48's ~9 small NuGet-package dependency DLLs (the rest came from the GAC), completely impractical for a self-contained publish, which this plan confirmed produces several hundred files (a same-shaped self-contained WinUI publish in this repo produced 478 files at 262MB; the WinForms-only, non-WinUI `SimpleDeFence.exe` publish will be smaller — no Windows App SDK/WinUI/CommunityToolkit payload — but still on the order of 100+ runtime files, an order of magnitude beyond what hand-authoring can reasonably track). `heat.exe`, WiX's own directory-harvesting tool, generates that component list automatically from the real publish output instead.

**This task cannot be build-verified in this environment** — the WiX Toolset (`heat.exe`/`candle.exe`/`light.exe`) is not installed on this machine (confirmed by search before this plan was written). Every step below is specified as precisely as the well-documented, stable `heat.exe` CLI allows, but the whole task needs a real pass on a machine with WiX Toolset v3.x installed before it can be trusted. Say so plainly in this task's completion report rather than claim a verification that didn't happen.

- [ ] **Step 1: Publish the retargeted app**

Run: `dotnet publish SimpleDeFence/SimpleDeFence.csproj -c Release -r win-x64 --self-contained true -o SimpleDeFence/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish`

Expected: publish succeeds, producing `SimpleDeFence.exe` (an apphost) plus a large set of runtime/dependency DLLs in the output directory. Note the output path — it's what `Directory` points at in Step 2's harvest command, and Step 4's `PublishDir` variable.

- [ ] **Step 2: Harvest the publish output with `heat.exe`**

Run, from the `MsiSetup` directory (adjust the WiX Toolset install path to match the real machine — this plan cannot know it, since WiX isn't installed here):

```
heat.exe dir "..\SimpleDeFence\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish" -platform x64 -cg PublishedDependencies -gg -scom -sreg -srd -dr INSTALLDIR -var var.PublishDir -out Dependencies.wxs
```

Flag meanings, for whoever has to adjust this: `-cg PublishedDependencies` names the generated `ComponentGroup` (referenced in Step 4); `-gg` generates stable component GUIDs; `-scom -sreg` suppress harvesting COM/registry data (none of these files self-register); `-srd` suppresses generating a wrapper `<Directory>` element for the publish root, since Step 3 nests the harvested files under the existing `INSTALLDIR` directory instead; `-var var.PublishDir` parameterizes the source path via a preprocessor variable rather than baking in an absolute path, so the generated file stays portable across machines/build configs (`PublishDir` is defined as a WiX variable in Step 4).

Expected: `Dependencies.wxs` is created, containing one `<Fragment>` with a `ComponentGroup Id="PublishedDependencies">` and one `<Component>`/`<File>` pair per file in the publish output, each `Source` attribute referencing `$(var.PublishDir)\<relative path>`.

If any flag here doesn't behave as described (this task's author could not run `heat.exe` to confirm), treat the discrepancy as this task's real content and fix it against `heat.exe`'s own `-help` output and the resulting `Dependencies.wxs`'s actual shape — not as a sign to abandon harvesting in favor of hand-authoring hundreds of components.

- [ ] **Step 3: Add `Dependencies.wxs` to the WiX project and define `PublishDir`**

In `MsiSetup/MsiSetup.wixproj`, add `Dependencies.wxs` to the existing `Compile` `ItemGroup`:

```xml
	<ItemGroup>
    <Compile Include="CustomWelcomeDlg.wxs" />
    <Compile Include="Dependencies.wxs" />
    <Compile Include="RemoteWarnDlg.wxs" />
    <Compile Include="Product.wxs" />
    <Compile Include="WixUI_InstallDir_Custom.wxs" />
  </ItemGroup>
```

Add a `PublishDir` preprocessor variable, matching Step 1's output path, to the top-level `PropertyGroup` (the one already defining `Configuration`/`Platform`):

```xml
    <DefineConstants>$(DefineConstants);PublishDir=..\SimpleDeFence\bin\$(Configuration)\net10.0-windows10.0.19041.0\win-x64\publish</DefineConstants>
```

- [ ] **Step 4: Wire the harvested group into `Product.wxs`, remove the old manual dependency list**

In `MsiSetup/Product.wxs`, remove the nine hand-authored dependency `<Component>` blocks (each one a net48-era NuGet polyfill package — `Microsoft.Bcl.AsyncInterfaces`, `System.Buffers`, `System.Memory`, `System.Numerics.Vectors`, `System.Runtime.CompilerServices.Unsafe`, `System.Text.Encodings.Web`, `System.Text.Json`, `System.Threading.Tasks.Extensions`, `System.ValueTuple` — all now part of the net10 BCL, none needed once self-contained):

```xml
          <Component Id="Microsoft_Bcl_AsyncInterfaces_Lib" Guid="{2F512105-6A4B-4383-9A40-083CB0561BE5}" Win64="$(var.Win64)">
            <File Id="Microsoft_Bcl_AsyncInterfaces_dll" Source="Sources\ProgramFiles\SimpleDeFence\Microsoft.Bcl.AsyncInterfaces.dll" Vital="yes" KeyPath="yes" Checksum="yes" Assembly=".net" AssemblyApplication="TinyWallEXE" />
          </Component>
          <Component Id="System_Buffers_Lib" Guid="{C57EA79A-D53D-4C96-8120-C2D030F39D13}" Win64="$(var.Win64)">
            <File Id="System_Buffers_dll" Source="Sources\ProgramFiles\SimpleDeFence\System.Buffers.dll" Vital="yes" KeyPath="yes" Checksum="yes" Assembly=".net" AssemblyApplication="TinyWallEXE" />
          </Component>
          <Component Id="System_Memory_Lib" Guid="{D1BD8A4C-D66C-4EDF-A414-DD81CC18325A}" Win64="$(var.Win64)">
            <File Id="System_Memory_dll" Source="Sources\ProgramFiles\SimpleDeFence\System.Memory.dll" Vital="yes" KeyPath="yes" Checksum="yes" Assembly=".net" AssemblyApplication="TinyWallEXE" />
          </Component>
          <Component Id="System_Numerics_Vectors_Lib" Guid="{A4C268C7-2199-4561-86ED-533A5A7CBE45}" Win64="$(var.Win64)">
            <File Id="System_Numerics_Vectors_dll" Source="Sources\ProgramFiles\SimpleDeFence\System.Numerics.Vectors.dll" Vital="yes" KeyPath="yes" Checksum="yes" Assembly=".net" AssemblyApplication="TinyWallEXE" />
          </Component>
          <Component Id="System_Runtime_CompilerServices_Unsafe_Lib" Guid="{20D4C69C-DBE1-4DF1-9666-029DCE207E93}" Win64="$(var.Win64)">
            <File Id="System_Runtime_CompilerServices_Unsafe_dll" Source="Sources\ProgramFiles\SimpleDeFence\System.Runtime.CompilerServices.Unsafe.dll" Vital="yes" KeyPath="yes" Checksum="yes" Assembly=".net" AssemblyApplication="TinyWallEXE" />
          </Component>
          <Component Id="System_Text_Encodings_Web_Lib" Guid="{FA88A627-F2E3-47BF-A245-79B72D91F448}" Win64="$(var.Win64)">
            <File Id="System_Text_Encodings_Web_dll" Source="Sources\ProgramFiles\SimpleDeFence\System.Text.Encodings.Web.dll" Vital="yes" KeyPath="yes" Checksum="yes" Assembly=".net" AssemblyApplication="TinyWallEXE" />
          </Component>
          <Component Id="System_Text_Json_Lib" Guid="{A95856BD-5B66-4CAE-8870-1EA5CF3CFAD6}" Win64="$(var.Win64)">
            <File Id="System_Text_Json_dll" Source="Sources\ProgramFiles\SimpleDeFence\System.Text.Json.dll" Vital="yes" KeyPath="yes" Checksum="yes" Assembly=".net" AssemblyApplication="TinyWallEXE" />
          </Component>
          <Component Id="System_Threading_Tasks_Extensions_Lib" Guid="{AA5566BE-26F8-483C-8F16-008AE947B1D0}" Win64="$(var.Win64)">
            <File Id="System_Threading_Tasks_Extensions_dll" Source="Sources\ProgramFiles\SimpleDeFence\System.Threading.Tasks.Extensions.dll" Vital="yes" KeyPath="yes" Checksum="yes" Assembly=".net" AssemblyApplication="TinyWallEXE" />
          </Component>
          <Component Id="System_ValueTuple_Lib" Guid="{61BA9FB8-3CF5-462D-8097-0829F724619F}" Win64="$(var.Win64)">
            <File Id="System_ValueTuple_dll" Source="Sources\ProgramFiles\SimpleDeFence\System.ValueTuple.dll" Vital="yes" KeyPath="yes" Checksum="yes" Assembly=".net" AssemblyApplication="TinyWallEXE" />
          </Component>
```

Replace the `Dependencies` `ComponentGroup` definition (which only exists to group-reference the nine components just removed) with a reference to the harvested group instead — replace:

```xml
    <!-- Dependencies groupping -->
    <ComponentGroup Id="Dependencies">
      <ComponentRef Id="Microsoft_Bcl_AsyncInterfaces_Lib"/>
      <ComponentRef Id="System_Buffers_Lib"/>
      <ComponentRef Id="System_Memory_Lib"/>
      <ComponentRef Id="System_Numerics_Vectors_Lib"/>
      <ComponentRef Id="System_Runtime_CompilerServices_Unsafe_Lib"/>
      <ComponentRef Id="System_Text_Encodings_Web_Lib"/>
      <ComponentRef Id="System_Text_Json_Lib"/>
      <ComponentRef Id="System_Threading_Tasks_Extensions_Lib"/>
      <ComponentRef Id="System_ValueTuple_Lib"/>
    </ComponentGroup>
```

with:

```xml
    <!-- Dependencies groupping - harvested from the self-contained publish output by heat.exe
         (see Dependencies.wxs); hand-authoring one <Component> per file stopped being practical
         once the app became self-contained (hundreds of runtime files vs. the ~9 small NuGet
         package DLLs net48's framework-dependent build needed). -->
```

The `<Feature>` element's existing `<ComponentGroupRef Id='Dependencies' />` becomes `<ComponentGroupRef Id='PublishedDependencies' />`, matching the `-cg` name from Step 2's `heat.exe` invocation.

- [ ] **Step 5: Remove the .NET Framework 4.8 prerequisite check**

In `MsiSetup/Product.wxs`, remove the now-false condition (the app no longer needs .NET Framework at all — it's self-contained net10):

```xml
    <!-- .Net Framework detection -->
    <PropertyRef Id="WIX_IS_NETFRAMEWORK_48_OR_LATER_INSTALLED"/>
    <Condition Message='This application requires .NET Framework 4.8 to be installed.'>
      <![CDATA[WIX_IS_NETFRAMEWORK_48_OR_LATER_INSTALLED]]>
    </Condition>
```

- [ ] **Step 6: Update the main executable's file source path if the apphost filename changed**

Self-contained publish still produces `SimpleDeFence.exe` as the apphost (the project's `AssemblyName` is unchanged by this plan), so `Product.wxs`'s existing `MainExecutable` component:

```xml
          <Component Id="MainExecutable" Guid="{422FE697-4CCC-4B6C-B5E6-F4BB54CC1AF3}" Win64="$(var.Win64)">
            <File Id="TinyWallEXE" Source="Sources\ProgramFiles\SimpleDeFence\SimpleDeFence.exe" Vital="yes" KeyPath="yes" Checksum="yes" Assembly=".net" AssemblyApplication="TinyWallEXE" />
          </Component>
```

should not need its `Source` path changed. Confirm this against Step 1's real publish output during verification (Step 7) — if the self-contained apphost's `File` element needs `Assembly=".net"`/`AssemblyApplication` removed (those attributes describe a framework-dependent managed assembly load; a self-contained apphost is a native launcher and may not need them), that's a real, expected finding from Step 7's real pass, not a gap in this plan.

Also note: `Sources\ProgramFiles\SimpleDeFence\SimpleDeFence.exe.config` (the `MainExecutableConfig` component, `App.config`-style) is a net48 convention with no net10 equivalent (`.deps.json`/`.runtimeconfig.json` replace it, and both are already covered by Step 2's harvest of the publish directory) — remove the `MainExecutableConfig` component and its `<ComponentRef Id='MainExecutableConfig' />` in the `<Feature>` block, and delete `MsiSetup/Sources/ProgramFiles/SimpleDeFence/SimpleDeFence.exe.config` from the repo if nothing else references it.

- [ ] **Step 7: Build and verify — on a machine with the WiX Toolset installed**

Run (from `MsiSetup/`): `msbuild MsiSetup.wixproj /p:Configuration=Release /p:Platform=x64`

Expected: the build succeeds, producing `SimpleDeFence_x64.msi`. Install it on a real or virtual Windows machine and confirm: the app installs without any .NET Framework prompt, `SimpleDeFence.exe` launches, and (building on Task 2's own real-machine verification) the service still installs/starts/stops/uninstalls correctly through the newly-packaged binary.

This step cannot run in this environment (no WiX Toolset installed, confirmed before writing this plan) — do not report this task complete without it actually running somewhere that has WiX. If it surfaces `heat.exe`/`Dependencies.wxs` issues Step 2 didn't anticipate correctly, fix them here against the real tool output.

- [ ] **Step 8: Commit**

```bash
git add MsiSetup/MsiSetup.wixproj MsiSetup/Product.wxs MsiSetup/Dependencies.wxs
git rm MsiSetup/Sources/ProgramFiles/SimpleDeFence/SimpleDeFence.exe.config
git commit -m "Repackage MSI for the self-contained net10 build: heat.exe-harvested dependencies, drop the .NET Framework prerequisite"
```
