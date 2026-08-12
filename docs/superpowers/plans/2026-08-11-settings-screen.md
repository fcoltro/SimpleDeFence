# Settings Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the (currently unreachable) `"settings"` nav destination with a real WinUI Settings screen covering all 7 groups the modernization design doc named — General, Protection, Blocklists, Security, Updates, Maintenance, About — completing the three-destination information architecture (Connections, Rules, Settings) that Phase 2 set out to build.

**Architecture:** `IFirewallClient.CommitProfileChangesAsync(Action<ServerProfileConfiguration>)` generalizes to `CommitConfigChangesAsync(Action<ServerConfiguration>)` so Settings can reach `ServerConfiguration`-level fields (`Blocklists`, `AutoUpdateCheck`, `LockHostsFile`), not just the active profile. Three new `IFirewallClient` members (`LockAsync`, `UnlockAsync`, `SetPasswordAsync`) thinly wrap `SimpleDeFence.Core.Controller` methods that already exist. Two new Core-only types (`ClientSettings`, `ConfigExport`) carry WinUI's local theme preference and the Import/Export file format, deliberately kept separate from WinForms' `ControllerSettings`/`ConfigContainer` (see plan-wide note below). `SettingsPage` uses `SettingsCard`/`SettingsExpander` from the WinUI Community Toolkit, following the exact commit/failure/reentrancy patterns `RulesPage`/`ConnectionsPage` already established.

**Tech Stack:** C#, WinUI 3 / Windows App SDK 2.3.1, .NET 10 (`net10.0-windows10.0.19041.0`), xunit, `CommunityToolkit.WinUI.Controls.SettingsControls` (new dependency).

## Plan-wide note: why this plan does NOT touch `SimpleDeFence/Settings.cs`

Unlike the Rules plan's Task 1 (which split `AppDatabase` between Core and WinForms), this plan does not split or modify `ControllerSettings`/`ConfigContainer` (`SimpleDeFence/Settings.cs`) at all. `ControllerSettings`' WinForms-only fields (`ConnFormWindowState`, `ProcessesFormWindowState`, etc.) are typed with `System.Windows.Forms.FormWindowState`/`System.Drawing.Point`/`Size`; neither `SimpleDeFence.Core` nor `SimpleDeFence.UI` has WinForms enabled (`UseWindowsForms` is absent from both `.csproj` files — verified before this plan was written), so sharing that class would either drag a WinForms dependency into Core, or — if WinUI wrote only a subset of fields back to the same `ControllerConfig` file WinForms uses — silently discard whatever WinForms-only preferences were already there (a partial-schema writer overwrites the whole file). `SimpleDeFence/AppSerializationContext.cs`'s own doc comment already states the applicable principle: "Local-only types (client settings) that never cross the IPC wire... don't need to live in SimpleDeFence.Core alongside the protocol/config types shared with the WinUI 3 GUI." `ClientSettings` (Task 2) is WinUI's own separate local file for exactly this reason. `AppSerializationContext.cs` and `SimpleDeFence/Settings.cs` are not touched by any task in this plan.

## Global Constraints

Carried over from the Connections/Rules plans, plus new ones this plan introduces:

- Target framework for `SimpleDeFence.UI` and `SimpleDeFence.Tests` is `net10.0-windows10.0.19041.0`; `SimpleDeFence.Core` multi-targets `net48;net10.0-windows10.0.19041.0`.
- **Anything added to `SimpleDeFence.Core` must compile under net48**, because `SimpleDeFence.csproj` glob-compiles Core's top-level sources (`../SimpleDeFence.Core/*.cs`) into the net48 WinForms app. No `net10`-only APIs there.
- **Status is never signalled by colour alone** — always colour *plus* icon *plus* word.
- **The real client is the default in every configuration.** Sample data is only reachable via the `--sample-data` / `--sample-locked` command-line switches.
- **All IPC calls stay off the UI thread.**
- **Never let an unrecognised response look like success.** For `CommitConfigChangesAsync`, only `MessageType.PUT_SETTINGS` (with `Warning` false, already translated to `RESPONSE_STALE_CHANGESET` inside the client) reads as success. For `LockAsync`/`UnlockAsync`/`SetPasswordAsync`, exactly one `MessageType` value each (`LOCK`, `UNLOCK`, `SET_PASSWORD` respectively) reads as success — anything else, including an unrecognised value, is a failure to show as one.
- **Commits are immediate — no OK/Cancel batching**, matching Rules' scope decision 5. Every toggle/field in this plan commits on its own change, not on a page-level Save button.
- **Reentrancy rule, restated because this plan has many more commit-on-change controls than Rules did:** a `ContentDialog` must never be shown, and a commit must never be started, synchronously from inside another WinUI control's own internal event dispatch (a `Toggled`/`Checked`/`Unchecked` callback that isn't a plain top-level `Click`). Rules Task 4 hit and fixed a real WinUI access-violation crash from this exact class of bug (a `ToggleSwitch`'s `x:Bind TwoWay` setter firing a commit+dialog chain synchronously, which against the sample client's synchronously-completing tasks reached `ContentDialog.ShowAsync()` nested inside the `ToggleSwitch`'s own call stack). Every immediate-commit toggle in this plan (Protection, Blocklists, Updates, and Security's Lock-hosts-file) defers its commit via `DispatcherQueue.TryEnqueue(...)`, the same fix Rules applied.
- Every new user-visible string needs a `LocKeys` constant plus matching entries in **both** `SimpleDeFence.Core/Localization/Strings.en.json` and `Strings.pt-BR.json` — `LocTests` fails the build otherwise.
- `SimpleDeFence.Tests` runs single-threaded (`DisableTestParallelization`) because of `Loc`'s process-wide static culture state.
- Build the net48 app with `-t:Restore` and `-t:Build` as **separate** MSBuild invocations (see ROADMAP.md).
- **Nothing is removed from the WinForms app.** `SettingsForm` stays untouched and buildable, exactly as `ApplicationsPage` stayed available until Rules replaced it — this plan does not delete anything from `SimpleDeFence`.

## Deliberate scope decisions (read before objecting to a "gap")

1. **`Language`, `EnableGlobalHotkeys`, and `AskForExceptionDetails` are not ported.** All three have no real behavior in WinUI today: `Language` because the WinUI GUI has no localized strings yet (explicitly out of scope in the modernization design doc); `EnableGlobalHotkeys` because no global-hotkey capture exists anywhere in `SimpleDeFence.UI`; `AskForExceptionDetails` because Rules'/Connections' Allow flows always commit a fixed default policy with no per-add detail-prompt branch to gate. Shipping inert toggles for these would violate the "no inert entries" principle Rules' own Add flyout already established.
2. **"Check for updates now" (the manual action) is deferred**, not the `AutoUpdateCheck` toggle. The manual check needs `Updater`/`UpdateChecker` (`SimpleDeFence/UpdateChecker.cs`), which is WinForms-only and looks like a real subsystem (HTTP calls, version comparison, MSI install flow) — a follow-up plan, not part of this one.
3. **A quicker Lock/Unlock surface in the mode chip is out of scope.** The mode chip already shows an honest "Locked" status; this plan does not touch `MainWindow.xaml`/`.xaml.cs` beyond adding the Settings nav entry.
4. **Reconciling `ControllerSettings` (WinForms) and `ClientSettings` (WinUI) is deferred** to the eventual net10 exe-merge migration (ROADMAP.md) — see the plan-wide note above.
5. **Import replaces the whole server config, not a targeted field.** `ConfigExport`'s `Service` is applied wholesale (every field on the clone overwritten from the imported document), matching what "import a settings file" means to a user — not merged field-by-field with whatever was already active.

## File Structure

**Created:**
- `SimpleDeFence.Core/ClientSettings.cs` — WinUI's local theme preference (`UiTheme`) + its own `Load`/`Save`, separate from `ControllerSettings`
- `SimpleDeFence.Core/ConfigExport.cs` — `{ Service, Controller }` DTO for `.tws` Import/Export, cross-compatible with WinForms' `ConfigContainer` shape without reusing its type name
- `SimpleDeFence.Tests/ClientSettingsTests.cs`
- `SimpleDeFence.Tests/ConfigExportTests.cs`
- `SimpleDeFence.UI/Pages/SettingsPage.xaml` / `.xaml.cs`

**Modified:**
- `SimpleDeFence.UI/Services/IFirewallClient.cs` — `CommitProfileChangesAsync` → `CommitConfigChangesAsync`; add `LockAsync`/`UnlockAsync`/`SetPasswordAsync`
- `SimpleDeFence.UI/Services/FirewallClient.cs` / `SampleFirewallClient.cs` — same rename/addition
- `SimpleDeFence.UI/Pages/RulesPage.xaml.cs` — one-line adaptation to the renamed client method
- `SimpleDeFence.UI/MainWindow.xaml` / `.xaml.cs` — add the `"settings"` `NavigationViewItem` and nav-switch case
- `SimpleDeFence.UI/App.xaml.cs` — apply the persisted theme at launch
- `SimpleDeFence.UI/SimpleDeFence.UI.csproj` — add the `CommunityToolkit.WinUI.Controls.SettingsControls` package reference
- `SimpleDeFence.Core/SerializationHelper.cs` — source-gen context gains `ClientSettings`/`ConfigExport`
- `SimpleDeFence.Core/Localization/LocKeys.cs`, `Strings.en.json`, `Strings.pt-BR.json` — new `settings.*` keys, added per task

## Task 1: Generalize the commit primitive

**Files:**
- Modify: `SimpleDeFence.UI/Services/IFirewallClient.cs`, `FirewallClient.cs`, `SampleFirewallClient.cs`, `SimpleDeFence.UI/Pages/RulesPage.xaml.cs`

**Interfaces:**
- Consumes: `Controller.SetServerConfig` (Core, unchanged), `SerializationHelper` clone pattern (existing)
- Produces: `Task<MessageType> CommitConfigChangesAsync(Action<ServerConfiguration> mutate)` — replaces `CommitProfileChangesAsync`. `AllowAsync` is reimplemented on top of it, same as before.

Why: Rules' `CommitProfileChangesAsync(Action<ServerProfileConfiguration>)` only ever reaches `Config.ActiveProfile`. Settings needs to mutate `ServerConfiguration`-level fields (`Blocklists`, `AutoUpdateCheck`, `LockHostsFile`) that are siblings of `ActiveProfile`, not inside it. Rather than add a second commit method — which would contradict Rules Task 3's explicit "one method, one failure contract" rationale — this widens the existing one. `RulesPage`'s own `CommitAsync(Action<ServerProfileConfiguration>)` wrapper keeps its narrower signature (Rules never needs config-level mutations) and adapts internally with one line; `ConnectionsPage` needs no changes at all (it only calls `AllowAsync`, never `CommitProfileChangesAsync` directly).

- [ ] **Step 1: Update the interface**

In `SimpleDeFence.UI/Services/IFirewallClient.cs`, replace:

```csharp
        /// <summary>
        /// The one commit path: clone the cached config, mutate the clone's active profile, put
        /// it back. A returned type of PUT_SETTINGS alone is NOT sufficient to mean the change
        /// took - the service can reply PUT_SETTINGS while having applied nothing when the
        /// caller's changeset was stale (TwMessagePutSettings.Warning). Implementations translate
        /// that case to MessageType.RESPONSE_STALE_CHANGESET instead, so "PUT_SETTINGS and only
        /// PUT_SETTINGS" is the caller's complete success check; every other value (including
        /// RESPONSE_STALE_CHANGESET, locked, or unrecognised) is a failure to show as one.
        /// </summary>
        Task<MessageType> CommitProfileChangesAsync(Action<ServerProfileConfiguration> mutate);
```

with:

```csharp
        /// <summary>
        /// The one commit path: clone the cached config, mutate the whole clone, put it back. A
        /// returned type of PUT_SETTINGS alone is NOT sufficient to mean the change took - the
        /// service can reply PUT_SETTINGS while having applied nothing when the caller's
        /// changeset was stale (TwMessagePutSettings.Warning). Implementations translate that
        /// case to MessageType.RESPONSE_STALE_CHANGESET instead, so "PUT_SETTINGS and only
        /// PUT_SETTINGS" is the caller's complete success check; every other value (including
        /// RESPONSE_STALE_CHANGESET, locked, or unrecognised) is a failure to show as one.
        /// Callers that only need the active profile (the common case) write
        /// `config => mutate(config.ActiveProfile)`.
        /// </summary>
        Task<MessageType> CommitConfigChangesAsync(Action<ServerConfiguration> mutate);
```

- [ ] **Step 2: Update the real client**

In `SimpleDeFence.UI/Services/FirewallClient.cs`, replace:

```csharp
        public Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy)
            => CommitProfileChangesAsync(profile =>
                profile.AddExceptions(new List<FirewallExceptionV3> { new(subject, policy) }));

        public Task<MessageType> CommitProfileChangesAsync(Action<ServerProfileConfiguration> mutate)
        {
            return Task.Run(() =>
            {
                if (Config is null)
                    return MessageType.RESPONSE_ERROR;

                // Work on a deep copy so the GUI never holds a half-applied state: only a
                // successful PUT replaces the cached config.
                var clone = SerializationHelper.Deserialize<ServerConfiguration>(
                    SerializationHelper.Serialize(Config), new ServerConfiguration());
                mutate(clone.ActiveProfile);
```

with:

```csharp
        public Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy)
            => CommitConfigChangesAsync(config =>
                config.ActiveProfile.AddExceptions(new List<FirewallExceptionV3> { new(subject, policy) }));

        public Task<MessageType> CommitConfigChangesAsync(Action<ServerConfiguration> mutate)
        {
            return Task.Run(() =>
            {
                if (Config is null)
                    return MessageType.RESPONSE_ERROR;

                // Work on a deep copy so the GUI never holds a half-applied state: only a
                // successful PUT replaces the cached config.
                var clone = SerializationHelper.Deserialize<ServerConfiguration>(
                    SerializationHelper.Serialize(Config), new ServerConfiguration());
                mutate(clone);
```

(The rest of the method body — `_controller.SetServerConfig(clone, _changeset)` through the `Warning`/`RESPONSE_STALE_CHANGESET` handling — is unchanged.)

- [ ] **Step 3: Update the sample client**

In `SimpleDeFence.UI/Services/SampleFirewallClient.cs`, replace:

```csharp
        public Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy)
            => CommitProfileChangesAsync(profile =>
                profile.AddExceptions(new List<FirewallExceptionV3> { new(subject, policy) }));

        public Task<MessageType> CommitProfileChangesAsync(Action<ServerProfileConfiguration> mutate)
        {
            if (_locked)
                return Task.FromResult(MessageType.RESPONSE_LOCKED);

            if (Config is null)
                return Task.FromResult(MessageType.RESPONSE_ERROR);

            mutate(Config.ActiveProfile);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(MessageType.PUT_SETTINGS);
        }
```

with:

```csharp
        public Task<MessageType> AllowAsync(ExceptionSubject subject, ExceptionPolicy policy)
            => CommitConfigChangesAsync(config =>
                config.ActiveProfile.AddExceptions(new List<FirewallExceptionV3> { new(subject, policy) }));

        public Task<MessageType> CommitConfigChangesAsync(Action<ServerConfiguration> mutate)
        {
            if (_locked)
                return Task.FromResult(MessageType.RESPONSE_LOCKED);

            if (Config is null)
                return Task.FromResult(MessageType.RESPONSE_ERROR);

            mutate(Config);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(MessageType.PUT_SETTINGS);
        }
```

- [ ] **Step 4: Adapt `RulesPage`'s own commit wrapper**

In `SimpleDeFence.UI/Pages/RulesPage.xaml.cs`, inside the existing `CommitAsync(Action<ServerProfileConfiguration> mutate)` method, replace the single line:

```csharp
                return await App.Firewall.CommitProfileChangesAsync(mutate);
```

with:

```csharp
                return await App.Firewall.CommitConfigChangesAsync(config => mutate(config.ActiveProfile));
```

`RulesPage.CommitAsync`'s own signature (`Action<ServerProfileConfiguration>`) is unchanged — Rules never needs config-level mutations, so its four call sites (`RemoveButton_Click`, `ApplyButton_Click`, `ToggleSpecialAsync`, `CommitAddAsync`) need no changes at all.

- [ ] **Step 5: Build**

`dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors. (`ConnectionsPage.xaml.cs` is untouched and should not appear in the diff — it only calls `AllowAsync`.)

- [ ] **Step 6: Run the existing test suite**

`dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS, same count as before this task (this is a rename plus a signature widening with no behavior change for existing callers — no new tests are needed for this task).

- [ ] **Step 7: Verify against sample data**

`SimpleDeFence.UI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\SimpleDeFence.UI.exe --sample-data`
Navigate to Rules: Remove a rule, Apply a policy change, toggle a Special exception, Add via the UWP picker. All four should still succeed exactly as before (the rename is invisible from the UI). Then relaunch with `--sample-locked` and confirm Remove/Apply/Toggle still show the honest locked failure.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Generalize CommitProfileChangesAsync to CommitConfigChangesAsync"
```

## Task 2: Core settings types — `ClientSettings` and `ConfigExport`

**Files:**
- Create: `SimpleDeFence.Core/ClientSettings.cs`, `SimpleDeFence.Core/ConfigExport.cs`
- Test: `SimpleDeFence.Tests/ClientSettingsTests.cs`, `SimpleDeFence.Tests/ConfigExportTests.cs`
- Modify: `SimpleDeFence.Core/SerializationHelper.cs`

**Interfaces:**
- Consumes: `ISerializable<T>`, `SerializationHelper.Serialize`/`Deserialize`/`SerializeToFile`/`DeserializeFromFile` (existing, unchanged), `ServerConfiguration` (existing)
- Produces: `SimpleDeFence.ClientSettings` (`UiTheme`, `Load()`, `Save()`), `SimpleDeFence.ConfigExport` (`Service`, `Controller`)

Why: see the plan-wide note above for why these are new, separate types rather than a share of WinForms' `ControllerSettings`/`ConfigContainer`. Both are plain data, net48-safe (no `System.Windows.Forms`/`System.Drawing` types), living at `SimpleDeFence.Core/*.cs` top level so the WinForms app's existing glob-include (`../SimpleDeFence.Core/*.cs`) picks them up automatically with no `.csproj` changes on that side.

- [ ] **Step 1: Write the failing tests**

Create `SimpleDeFence.Tests/ClientSettingsTests.cs`:

```csharp
using SimpleDeFence;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class ClientSettingsTests
    {
        [Fact]
        public void Default_theme_is_auto()
        {
            Assert.Equal("auto", new ClientSettings().UiTheme);
        }

        [Fact]
        public void UiTheme_round_trips_through_serialization()
        {
            var original = new ClientSettings { UiTheme = "dark" };
            var bytes = SerializationHelper.Serialize(original);
            var restored = SerializationHelper.Deserialize(bytes, new ClientSettings());

            Assert.Equal("dark", restored.UiTheme);
        }
    }
}
```

Create `SimpleDeFence.Tests/ConfigExportTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using SimpleDeFence;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class ConfigExportTests
    {
        [Fact]
        public void Service_and_controller_round_trip_through_serialization()
        {
            var original = new ConfigExport
            {
                Service = new ServerConfiguration { LockHostsFile = false, AutoUpdateCheck = false },
                Controller = new ClientSettings { UiTheme = "light" },
            };

            var bytes = SerializationHelper.Serialize(original);
            var restored = SerializationHelper.Deserialize(bytes, new ConfigExport());

            Assert.False(restored.Service.LockHostsFile);
            Assert.False(restored.Service.AutoUpdateCheck);
            Assert.Equal("light", restored.Controller.UiTheme);
        }

        [Fact]
        public void Deserializes_a_config_container_shaped_payload_ignoring_unknown_controller_fields()
        {
            // Mirrors the real shape SimpleDeFence.ConfigContainer (WinForms) serializes to:
            // {"Service": {...}, "Controller": {...}}, where Controller carries many WinForms-only
            // fields (window geometry, Language, EnableGlobalHotkeys, SettingsTabIndex) ConfigExport
            // does not know about. This is the cross-compatibility claim the Settings design doc's
            // decision 5 depends on: a .tws file WinForms exports must not throw when WinUI imports
            // it, even though WinUI's Controller type only recognizes a subset of its fields.
            var json = """
            {
              "Service": { "ConfigVersion": 1, "LockHostsFile": true, "AutoUpdateCheck": true, "Profiles": [] },
              "Controller": { "Language": "en", "UiTheme": "dark", "EnableGlobalHotkeys": true, "ConnFormWindowState": 0, "SettingsTabIndex": 2 }
            }
            """;

            var bytes = Encoding.UTF8.GetBytes(json);
            var restored = JsonSerializer.Deserialize(bytes, SourceGenerationContext.Default.ConfigExport);

            Assert.NotNull(restored);
            Assert.True(restored!.Service.LockHostsFile);
            Assert.Equal("dark", restored.Controller.UiTheme);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: FAIL to compile — `ClientSettings`/`ConfigExport`/`SourceGenerationContext.Default.ConfigExport` do not exist yet. (`SourceGenerationContext` is `internal`, but `SimpleDeFence.Tests` compiles against `SimpleDeFence.Core`'s source via the project reference, not just its public surface, so the `internal` type is visible within the same assembly's test project — this matches how existing Core tests already reach internal Core types.)

- [ ] **Step 3: Write `ClientSettings`**

Create `SimpleDeFence.Core/ClientSettings.cs`:

```csharp
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence
{
    /// <summary>
    /// WinUI's own local, per-user preferences - kept deliberately separate from
    /// SimpleDeFence.Settings.ControllerSettings (the WinForms GUI's equivalent). See the Settings
    /// plan's "why this plan does NOT touch SimpleDeFence/Settings.cs" note for why: sharing that
    /// class would either drag a System.Windows.Forms/System.Drawing dependency into Core for
    /// fields WinUI has no use for, or - if WinUI wrote only a subset of fields back to the same
    /// file WinForms uses - silently discard whatever WinForms-only preferences were already
    /// there. Reconciling the two is deferred to the eventual net10 exe-merge migration.
    /// </summary>
    [DataContract(Namespace = "SimpleDeFence")]
    public sealed class ClientSettings : ISerializable<ClientSettings>
    {
        [DataMember(EmitDefaultValue = false)]
        public string UiTheme { get; set; } = "auto";

        private static string FilePath
        {
            get
            {
#if DEBUG
                var dir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()!.Location)!;
#else
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleDeFence");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
#endif
                return Path.Combine(dir, "UIConfig");
            }
        }

        public static ClientSettings Load()
        {
            try
            {
                return SerializationHelper.DeserializeFromFile(FilePath, new ClientSettings());
            }
            catch
            {
                // First run (no file yet) or a corrupt/unreadable file are both a normal state,
                // not an error - the same defensive convention ControllerSettings.Load() already
                // uses. A fresh default instance (theme "auto") is a safe, honest fallback.
                return new ClientSettings();
            }
        }

        public void Save()
        {
            try
            {
                SerializationHelper.SerializeToFile(this, FilePath);
            }
            catch
            {
                // Best-effort persistence, matching ControllerSettings.Save()'s existing
                // convention - a failed save (e.g. AppData briefly locked) must not crash the
                // Settings page or block the in-memory theme change from applying.
            }
        }

        public JsonTypeInfo<ClientSettings> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.ClientSettings;
        }
    }
}
```

- [ ] **Step 4: Write `ConfigExport`**

Create `SimpleDeFence.Core/ConfigExport.cs`:

```csharp
using System.Runtime.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence
{
    /// <summary>
    /// The Import/Export (.tws) wire shape for the WinUI GUI: {Service, Controller}, matching
    /// SimpleDeFence.ConfigContainer's (WinForms) shape field-for-field so files exported by
    /// either GUI import cleanly into the other. A distinct type, not a reuse of ConfigContainer:
    /// SimpleDeFence.Core's sources are glob-compiled into the WinForms project, and a Core type
    /// literally named ConfigContainer would collide with the existing WinForms-only class of that
    /// name in the same namespace.
    /// </summary>
    [DataContract(Namespace = "SimpleDeFence")]
    public sealed class ConfigExport : ISerializable<ConfigExport>
    {
        [DataMember(EmitDefaultValue = false)]
        public ServerConfiguration Service { get; set; } = new ServerConfiguration();

        [DataMember(EmitDefaultValue = false)]
        public ClientSettings Controller { get; set; } = new ClientSettings();

        public JsonTypeInfo<ConfigExport> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.ConfigExport;
        }
    }
}
```

- [ ] **Step 5: Register both types in the source-gen context**

In `SimpleDeFence.Core/SerializationHelper.cs`, add two entries to the existing `SourceGenerationContext` class's attribute list (alongside the existing `[JsonSerializable(typeof(ServerConfiguration))]` etc.):

```csharp
    [JsonSerializable(typeof(ClientSettings))]
    [JsonSerializable(typeof(ConfigExport))]
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS — all four new tests green, existing suite unaffected.

- [ ] **Step 7: Verify net48, then commit**

Core is glob-compiled into the net48 app; verify via Framework MSBuild (separate `-t:Restore`/`-t:Build` invocations — see ROADMAP.md for the exact command this project uses on a bare BuildTools install). Expected: `Build succeeded`, 0 errors — confirms `ClientSettings`/`ConfigExport` use no net10-only APIs.

```bash
git add -A
git commit -m "Add Core ClientSettings and ConfigExport types"
```

## Task 3: Lock, unlock, and password on `IFirewallClient`

**Files:**
- Modify: `SimpleDeFence.UI/Services/IFirewallClient.cs`, `FirewallClient.cs`, `SampleFirewallClient.cs`

**Interfaces:**
- Consumes: `SimpleDeFence.Core.Controller.LockServer()`, `.TryUnlockServer(string)`, `.SetPassphrase(string)` (all pre-existing in Core, already used by the WinForms controller)
- Produces: `Task<MessageType> LockAsync()`, `Task<MessageType> UnlockAsync(string password)`, `Task<MessageType> SetPasswordAsync(string password)`

Why: these three server actions already exist as one-line `Controller` methods (verified in `SimpleDeFence.Core/Controller.cs`) — no new protocol work, just the same thin-wrapper-over-`Task.Run` pattern `SwitchModeAsync` already uses. Lock state itself needs no new plumbing to read: `IFirewallClient.State.HasPassword`/`.Locked` already exist and are already populated by every `RefreshAsync()`.

- [ ] **Step 1: Extend the interface**

In `SimpleDeFence.UI/Services/IFirewallClient.cs`, add after `AllowAsync`:

```csharp
        /// <summary>Locks the configuration server-side. A no-op on the wire (still returns
        /// MessageType.LOCK) when no password is set - PasswordLock's own Locked setter is gated
        /// on HasPassword server-side, so this is inert rather than an error in that case; callers
        /// should disable the "Lock now" action when State.HasPassword is false.</summary>
        Task<MessageType> LockAsync();

        /// <summary>Attempts to unlock the configuration with the given password. Success is
        /// exactly MessageType.UNLOCK; anything else (including a wrong password) is a failure to
        /// show as one.</summary>
        Task<MessageType> UnlockAsync(string password);

        /// <summary>Sets, changes, or clears (empty string) the password protecting the
        /// configuration. Success is exactly MessageType.SET_PASSWORD.</summary>
        Task<MessageType> SetPasswordAsync(string password);
```

- [ ] **Step 2: Implement the real client**

In `SimpleDeFence.UI/Services/FirewallClient.cs`, add after `SwitchModeAsync`:

```csharp
        public Task<MessageType> LockAsync()
            => Task.Run(() => _controller.LockServer());

        public Task<MessageType> UnlockAsync(string password)
            => Task.Run(() => _controller.TryUnlockServer(password));

        public Task<MessageType> SetPasswordAsync(string password)
            => Task.Run(() => _controller.SetPassphrase(password));
```

- [ ] **Step 3: Implement the sample client**

In `SimpleDeFence.UI/Services/SampleFirewallClient.cs`, the constructor and `_locked` field become mutable (Lock/Unlock/SetPassword need to change lock state at runtime, not just read a fixed startup flag). Replace:

```csharp
        private readonly bool _locked;

        /// <param name="locked">
        /// Simulates a locked service, which refuses mode changes. Without this the sample client
        /// could only ever succeed, leaving the GUI's failure handling unreachable and therefore
        /// unverifiable until the real client becomes usable.
        /// </param>
        public SampleFirewallClient(bool locked = false) => _locked = locked;
```

with:

```csharp
        private bool _locked;
        private bool _hasPassword;

        /// <param name="locked">
        /// Simulates a locked service, which refuses mode changes. Without this the sample client
        /// could only ever succeed, leaving the GUI's failure handling unreachable and therefore
        /// unverifiable until the real client becomes usable. Implies a password is set, mirroring
        /// PasswordLock.Locked's real getter (locked && HasPassword) - you cannot be locked
        /// without a password.
        /// </param>
        public SampleFirewallClient(bool locked = false)
        {
            _locked = locked;
            _hasPassword = locked;
        }
```

Then, in `RefreshAsync`, replace:

```csharp
            Config ??= BuildConfig();
            State ??= new ServerState
            {
                Mode = FirewallMode.Normal,
                Locked = _locked,
                HasPassword = _locked,
            };
```

with:

```csharp
            Config ??= BuildConfig();
            State ??= new ServerState { Mode = FirewallMode.Normal };
            // Re-synced every refresh, not just on first creation, so LockAsync/UnlockAsync/
            // SetPasswordAsync (which mutate the fields, not the State object directly) are
            // reflected the next time a page calls RefreshAsync() - the same "refresh after a
            // commit to see the new truth" convention every other mutation in this app already
            // follows.
            State.Locked = _locked;
            State.HasPassword = _hasPassword;
```

Then add, after the new `SwitchModeAsync` block:

```csharp
        public Task<MessageType> LockAsync()
        {
            // Mirrors the real service: PasswordLock.Locked's setter is a no-op without a
            // password, but the response is still MessageType.LOCK either way - it is the
            // UI's job to disable "Lock now" when State.HasPassword is false, not this method's.
            if (_hasPassword)
                _locked = true;
            return Task.FromResult(MessageType.LOCK);
        }

        public Task<MessageType> UnlockAsync(string password)
        {
            // Sample data has no real password hash to check against - any input unlocks. This
            // still exercises the GUI's success/failure branches faithfully because the *lock*
            // state, not the password check, is what SampleFirewallClient(locked: true) exists to
            // simulate; a wrong-password failure path has no sample-data seam, same limitation
            // GetAppDatabaseAsync's missing-file case already has for other data.
            _locked = false;
            return Task.FromResult(MessageType.UNLOCK);
        }

        public Task<MessageType> SetPasswordAsync(string password)
        {
            _hasPassword = !string.IsNullOrEmpty(password);
            if (!_hasPassword)
                // Clearing the password also clears any lock, mirroring PasswordLock.Locked's
                // real getter (locked && HasPassword) - a lock cannot survive its password
                // being removed.
                _locked = false;
            return Task.FromResult(MessageType.SET_PASSWORD);
        }
```

- [ ] **Step 4: Build**

`dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo` — the page does not consume these members yet, so this proves the client surface compiles. Expected: 0 errors.

- [ ] **Step 5: Run the existing test suite**

`dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS, unaffected (this task only touches `SimpleDeFence.UI`, which `SimpleDeFence.Tests` does not reference).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add lock/unlock/password to the firewall client"
```

## Task 4: `SettingsPage` shell, nav wiring, and the General group

**Files:**
- Create: `SimpleDeFence.UI/Pages/SettingsPage.xaml`, `.xaml.cs`
- Modify: `SimpleDeFence.UI/MainWindow.xaml`, `.xaml.cs`, `App.xaml.cs`, `SimpleDeFence.UI.csproj`, `LocKeys.cs` + both JSONs

**Interfaces:**
- Consumes: `App.Firewall` (`IFirewallClient`), `ClientSettings.Load`/`.Save` (Task 2)
- Produces: the Settings screen's page shell, toolbar, and its first working group (General/theme); later tasks add the remaining six groups to the same page

Layout: title, `InfoBar`, a `Refresh` button + busy ring toolbar (matching Rules'/Connections' pattern), then a `ScrollViewer` containing a `StackPanel` of `SettingsExpander`/`SettingsCard` groups in the design doc's order (General, Protection, Blocklists, Security, Updates, Maintenance, About) — this task builds the page and General; Tasks 5-8 add the other six groups' XAML and code-behind to the same files.

- [ ] **Step 1: Add the `CommunityToolkit.WinUI.Controls.SettingsControls` package**

In `SimpleDeFence.UI/SimpleDeFence.UI.csproj`, add to the existing `PackageReference` `ItemGroup`:

```xml
    <PackageReference Include="CommunityToolkit.WinUI.Controls.SettingsControls" Version="8.1.240916" />
```

- [ ] **Step 2: Add the localization keys for the shell and General group**

In `SimpleDeFence.Core/Localization/LocKeys.cs`, add a new nested class after `Rules`:

```csharp
        public static class Settings
        {
            public const string Title = "settings.title";
            public const string SectionGeneral = "settings.section.general";
            public const string SectionProtection = "settings.section.protection";
            public const string SectionBlocklists = "settings.section.blocklists";
            public const string SectionSecurity = "settings.section.security";
            public const string SectionUpdates = "settings.section.updates";
            public const string SectionMaintenance = "settings.section.maintenance";
            public const string SectionAbout = "settings.section.about";

            public const string CommitFailedTitle = "settings.commitFailed.title";
            public const string CommitFailedLockedDetail = "settings.commitFailed.lockedDetail";
            public const string CommitFailedStaleDetail = "settings.commitFailed.staleDetail";
            public const string CommitFailedGenericDetail = "settings.commitFailed.genericDetail";

            public const string GeneralTheme = "settings.general.theme";
            public const string GeneralThemeDescription = "settings.general.themeDescription";
            public const string GeneralThemeAuto = "settings.general.themeAuto";
            public const string GeneralThemeLight = "settings.general.themeLight";
            public const string GeneralThemeDark = "settings.general.themeDark";
        }
```

Also add the nav entry key alongside the existing `Nav` class:

```csharp
        public static class Nav
        {
            public const string Rules = "nav.rules";
            public const string Settings = "nav.settings";
            public const string ModeChip = "nav.modeChip";
        }
```

In `SimpleDeFence.Core/Localization/Strings.en.json`, add `"settings": "Settings"` to the `"nav"` object (making it `"rules": "Rules", "settings": "Settings", "modeChip": "Firewall mode"`), and a new top-level `"settings"` section after `"rules"`:

```json
  "settings": {
    "title": "Settings",
    "section": {
      "general": "General",
      "protection": "Protection",
      "blocklists": "Blocklists",
      "security": "Security",
      "updates": "Updates",
      "maintenance": "Maintenance",
      "about": "About"
    },
    "commitFailed": {
      "title": "Could not save the setting",
      "lockedDetail": "Unlock the configuration before changing settings.",
      "staleDetail": "The configuration changed elsewhere just now; the setting was not changed. Please try again.",
      "genericDetail": "The service returned {0}. The setting was not changed."
    },
    "general": {
      "theme": "App theme",
      "themeDescription": "Applies immediately.",
      "themeAuto": "Use system setting",
      "themeLight": "Light",
      "themeDark": "Dark"
    }
  }
```

In `SimpleDeFence.Core/Localization/Strings.pt-BR.json`, add the matching entries: `"settings": "Configurações"` in `"nav"`, and:

```json
  "settings": {
    "title": "Configurações",
    "section": {
      "general": "Geral",
      "protection": "Proteção",
      "blocklists": "Listas de bloqueio",
      "security": "Segurança",
      "updates": "Atualizações",
      "maintenance": "Manutenção",
      "about": "Sobre"
    },
    "commitFailed": {
      "title": "Não foi possível salvar a configuração",
      "lockedDetail": "Desbloqueie a configuração antes de alterar as configurações.",
      "staleDetail": "A configuração foi alterada em outro lugar agora há pouco; a configuração não foi alterada. Tente novamente.",
      "genericDetail": "O serviço retornou {0}. A configuração não foi alterada."
    },
    "general": {
      "theme": "Tema do aplicativo",
      "themeDescription": "Aplica-se imediatamente.",
      "themeAuto": "Usar configuração do sistema",
      "themeLight": "Claro",
      "themeDark": "Escuro"
    }
  }
```

- [ ] **Step 3: Wire the nav entry**

In `SimpleDeFence.UI/MainWindow.xaml`, add a third `NavigationViewItem` after the Rules one:

```xml
                <NavigationViewItem Content="{loc:Loc Key=nav.settings}" Tag="settings">
                    <NavigationViewItem.Icon>
                        <FontIcon Glyph="&#xE713;"/>
                    </NavigationViewItem.Icon>
                </NavigationViewItem>
```

In `SimpleDeFence.UI/MainWindow.xaml.cs`, replace the `Nav_SelectionChanged` switch:

```csharp
            // Settings arrives in its own plan; until then Connections and Rules are the only
            // two destinations.
            var targetType = (string)item.Tag switch
            {
                "connections" => typeof(ConnectionsPage),
                "rules" => typeof(RulesPage),
                _ => typeof(ConnectionsPage),
            };
```

with:

```csharp
            var targetType = (string)item.Tag switch
            {
                "connections" => typeof(ConnectionsPage),
                "rules" => typeof(RulesPage),
                "settings" => typeof(SettingsPage),
                _ => typeof(ConnectionsPage),
            };
```

- [ ] **Step 4: Apply the persisted theme at launch**

In `SimpleDeFence.UI/App.xaml.cs`, add a using for `Microsoft.UI.Xaml` (already implicitly available via `Window`) and `SimpleDeFence` (for `ClientSettings`), then replace:

```csharp
            m_window = new MainWindow();
            MainWindow = m_window;
            m_window.Activate();
```

with:

```csharp
            m_window = new MainWindow();
            MainWindow = m_window;
            ApplyTheme(ClientSettings.Load().UiTheme);
            m_window.Activate();
```

and add these two members to the `App` class (after `MainWindow`):

```csharp
        /// <summary>Applies a persisted "auto"/"light"/"dark" theme string to the window's root
        /// element. Called at launch (this file) and immediately on change from the Settings page
        /// (Task 4's General group), so both share one mapping from the stored string to
        /// ElementTheme.</summary>
        internal static void ApplyTheme(string uiTheme)
        {
            if (MainWindow?.Content is not Microsoft.UI.Xaml.FrameworkElement root)
                return;

            root.RequestedTheme = uiTheme switch
            {
                "light" => Microsoft.UI.Xaml.ElementTheme.Light,
                "dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
                _ => Microsoft.UI.Xaml.ElementTheme.Default,
            };
        }
```

- [ ] **Step 5: Write the page code-behind**

Create `SimpleDeFence.UI/Pages/SettingsPage.xaml.cs`:

```csharp
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimpleDeFence.Localization;
using System;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private bool _busy;
        private bool _committing;

        /// <summary>Guards every Toggled/SelectionChanged handler against firing its own commit
        /// while SeedControls() is programmatically setting IsOn/SelectedIndex from the just-
        /// refreshed config - without this, seeding a ToggleSwitch's IsOn fires Toggled exactly as
        /// a user click would, which would recommit the value that is only being re-synced, not
        /// changed. Same rationale as SettingsForm.LoadingSettings (the WinForms equivalent this
        /// page replaces), which every one of its ItemCheck handlers checks first for the same
        /// reason.</summary>
        private bool _seeding;

        private ClientSettings _clientSettings = new();

        public SettingsPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
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
                }
                else
                {
                    Notice.IsOpen = false;
                    _seeding = true;
                    try
                    {
                        SeedGeneral();
                    }
                    finally
                    {
                        _seeding = false;
                    }
                }
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SeedGeneral()
        {
            _clientSettings = ClientSettings.Load();
            ThemeCombo.SelectedIndex = _clientSettings.UiTheme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };
        }

        /// <summary>ComboBox.SelectionChanged is a plain top-level event, not nested inside
        /// another control's own dispatch, so committing directly here is safe per the Task 4
        /// (Rules) reentrancy rule - but the change is local-only (no server IPC), so there is
        /// nothing to defer regardless.</summary>
        private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_seeding || ThemeCombo.SelectedItem is not ComboBoxItem item)
                return;

            var theme = (string)item.Tag;
            if (theme == _clientSettings.UiTheme)
                return;

            _clientSettings.UiTheme = theme;
            _clientSettings.Save();
            App.ApplyTheme(theme);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        /// <summary>One serialized commit path for every server-side setting in this page,
        /// mirroring RulesPage.CommitAsync's shape exactly (same _committing guard, same
        /// exception -> InfoBar handling).</summary>
        private async Task<MessageType> CommitAsync(Action<ServerConfiguration> mutate)
        {
            _committing = true;
            UpdateControlsEnabled();
            try
            {
                return await App.Firewall.CommitConfigChangesAsync(mutate);
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
                return MessageType.COM_ERROR;
            }
            finally
            {
                _committing = false;
                UpdateControlsEnabled();
            }
        }

        /// <summary>Placeholder for now (only General exists, and it has nothing to disable while
        /// committing since it never reaches CommitAsync); Tasks 5-8 extend this to disable their
        /// own groups' controls while _committing is true, the same pattern
        /// RulesPage.UpdateRemoveButton/UpdateApplyButtonEnabled/UpdateAddButtonEnabled use.</summary>
        private void UpdateControlsEnabled()
        {
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

        /// <summary>Every ContentDialog.ShowAsync() call site in this page routes through here -
        /// same rationale as RulesPage.TryShowDialogAsync (only one ContentDialog can be open per
        /// XamlRoot; this page has no process-wide UnhandledException backstop).</summary>
        private async Task<ContentDialogResult> TryShowDialogAsync(ContentDialog dialog, string fallbackTitle, string fallbackMessage)
        {
            try
            {
                return await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                ShowNotice(InfoBarSeverity.Informational, fallbackTitle, fallbackMessage);
                return ContentDialogResult.None;
            }
        }

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

- [ ] **Step 6: Write the page XAML**

Create `SimpleDeFence.UI/Pages/SettingsPage.xaml`:

```xml
<Page
    x:Class="SimpleDeFence.UI.Pages.SettingsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:SimpleDeFence.UI.Pages"
    xmlns:loc="using:SimpleDeFence.UI.Localization"
    xmlns:controls="using:CommunityToolkit.WinUI.Controls">

    <Grid Padding="28" RowSpacing="14">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="{loc:Loc Key=settings.title}" Style="{StaticResource TitleTextBlockStyle}"/>

        <InfoBar Grid.Row="1" x:Name="Notice" IsOpen="False" IsClosable="True"/>

        <Grid Grid.Row="2" ColumnSpacing="12">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <Button Grid.Column="1" x:Name="RefreshButton" Content="{loc:Loc Key=common.refresh}" Click="RefreshButton_Click"/>
            <ProgressRing Grid.Column="2" x:Name="Busy" IsActive="False" Width="20" Height="20" VerticalAlignment="Center"/>
        </Grid>

        <ScrollViewer Grid.Row="3">
            <StackPanel Spacing="12" MaxWidth="720" HorizontalAlignment="Stretch">

                <controls:SettingsExpander Header="{loc:Loc Key=settings.section.general}" IsExpanded="True">
                    <controls:SettingsExpander.HeaderIcon>
                        <FontIcon Glyph="&#xE713;"/>
                    </controls:SettingsExpander.HeaderIcon>
                    <controls:SettingsExpander.Items>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.general.theme}"
                                                Description="{loc:Loc Key=settings.general.themeDescription}">
                            <ComboBox x:Name="ThemeCombo" SelectionChanged="ThemeCombo_SelectionChanged" MinWidth="160">
                                <ComboBoxItem Content="{loc:Loc Key=settings.general.themeAuto}" Tag="auto"/>
                                <ComboBoxItem Content="{loc:Loc Key=settings.general.themeLight}" Tag="light"/>
                                <ComboBoxItem Content="{loc:Loc Key=settings.general.themeDark}" Tag="dark"/>
                            </ComboBox>
                        </controls:SettingsCard>
                    </controls:SettingsExpander.Items>
                </controls:SettingsExpander>

                <!-- Protection, Blocklists, Security, Updates, Maintenance, and About groups are
                     added here by Tasks 5-8. -->

            </StackPanel>
        </ScrollViewer>
    </Grid>
</Page>
```

- [ ] **Step 7: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors. If the `CommunityToolkit.WinUI.Controls.SettingsControls` package version in Step 1 does not resolve, check [nuget.org](https://www.nuget.org/packages/CommunityToolkit.WinUI.Controls.SettingsControls) for the latest 8.x release compatible with `Microsoft.WindowsAppSDK 2.3.1` and use that version instead — the exact patch version is not load-bearing, only that it provides `SettingsExpander`/`SettingsCard`.

- [ ] **Step 8: Run the test suite**

`dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS, including `LocTests`' key-parity check across the new `settings.*`/`nav.settings` keys in `LocKeys.cs` and both JSON files.

- [ ] **Step 9: Verify against sample data**

`SimpleDeFence.UI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\SimpleDeFence.UI.exe --sample-data`
Navigate to Settings via the new nav item. Confirm: the General group renders with the theme ComboBox; changing it (Auto → Light → Dark) visibly re-themes the window immediately; relaunching the app shows the previously chosen theme already selected (persisted to `UIConfig` next to the exe in a Debug build). Confirm Connections/Rules still work and the nav item order is Connections, Rules, Settings.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "Add SettingsPage shell, nav wiring, and the General group"
```

## Task 5: Protection and Blocklists groups

**Files:**
- Modify: `SimpleDeFence.UI/Pages/SettingsPage.xaml`, `.xaml.cs`, `LocKeys.cs` + both JSONs

**Interfaces:**
- Consumes: `CommitAsync` (Task 4), `ServerConfiguration.ActiveProfile.AllowLocalSubnet`/`.DisplayOffBlock`, `ServerConfiguration.Blocklists.EnableBlocklists`/`.EnableHostsBlocklist`/`.EnablePortBlocklist` (Core, existing)
- Produces: two more working `SettingsExpander` groups

Both groups are plain immediate-commit toggles reaching different parts of `ServerConfiguration` — Protection into `ActiveProfile`, Blocklists into the top-level `Blocklists` object — proving `CommitConfigChangesAsync`'s generalization from Task 1 actually gets used for both shapes.

- [ ] **Step 1: Add the localization keys**

In `LocKeys.cs`, extend the `Settings` class:

```csharp
            public const string ProtectionAllowLocalSubnet = "settings.protection.allowLocalSubnet";
            public const string ProtectionAllowLocalSubnetDescription = "settings.protection.allowLocalSubnetDescription";
            public const string ProtectionDisplayOffBlock = "settings.protection.displayOffBlock";
            public const string ProtectionDisplayOffBlockDescription = "settings.protection.displayOffBlockDescription";

            public const string BlocklistsEnable = "settings.blocklists.enable";
            public const string BlocklistsEnableDescription = "settings.blocklists.enableDescription";
            public const string BlocklistsHosts = "settings.blocklists.hosts";
            public const string BlocklistsPorts = "settings.blocklists.ports";
```

In `Strings.en.json`, extend the `"settings"` object (after `"general"`):

```json
    "protection": {
      "allowLocalSubnet": "Allow local network",
      "allowLocalSubnetDescription": "Always allow traffic to and from other devices on your local network.",
      "displayOffBlock": "Block network when display is off",
      "displayOffBlockDescription": "Blocks all network traffic while the screen is off."
    },
    "blocklists": {
      "enable": "Enable blocklists",
      "enableDescription": "Blocks traffic to known-malicious hosts and ports.",
      "hosts": "Block malicious hosts",
      "ports": "Block malicious ports"
    }
```

In `Strings.pt-BR.json`, extend `"settings"` with the matching Portuguese entries:

```json
    "protection": {
      "allowLocalSubnet": "Permitir rede local",
      "allowLocalSubnetDescription": "Sempre permitir tráfego de e para outros dispositivos na sua rede local.",
      "displayOffBlock": "Bloquear rede quando a tela estiver desligada",
      "displayOffBlockDescription": "Bloqueia todo o tráfego de rede enquanto a tela estiver desligada."
    },
    "blocklists": {
      "enable": "Ativar listas de bloqueio",
      "enableDescription": "Bloqueia tráfego para hosts e portas conhecidos como maliciosos.",
      "hosts": "Bloquear hosts maliciosos",
      "ports": "Bloquear portas maliciosas"
    }
```

- [ ] **Step 2: Add the XAML**

In `SettingsPage.xaml`, replace the placeholder comment left by Task 4 (`<!-- Protection, Blocklists, ... -->`) with:

```xml
                <controls:SettingsExpander Header="{loc:Loc Key=settings.section.protection}" IsExpanded="True">
                    <controls:SettingsExpander.HeaderIcon>
                        <FontIcon Glyph="&#xE72E;"/>
                    </controls:SettingsExpander.HeaderIcon>
                    <controls:SettingsExpander.Items>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.protection.allowLocalSubnet}"
                                                Description="{loc:Loc Key=settings.protection.allowLocalSubnetDescription}">
                            <ToggleSwitch x:Name="AllowLocalSubnetToggle" Toggled="AllowLocalSubnetToggle_Toggled"/>
                        </controls:SettingsCard>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.protection.displayOffBlock}"
                                                Description="{loc:Loc Key=settings.protection.displayOffBlockDescription}">
                            <ToggleSwitch x:Name="DisplayOffBlockToggle" Toggled="DisplayOffBlockToggle_Toggled"/>
                        </controls:SettingsCard>
                    </controls:SettingsExpander.Items>
                </controls:SettingsExpander>

                <controls:SettingsExpander Header="{loc:Loc Key=settings.section.blocklists}" IsExpanded="True">
                    <controls:SettingsExpander.HeaderIcon>
                        <FontIcon Glyph="&#xE783;"/>
                    </controls:SettingsExpander.HeaderIcon>
                    <controls:SettingsExpander.Items>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.blocklists.enable}"
                                                Description="{loc:Loc Key=settings.blocklists.enableDescription}">
                            <ToggleSwitch x:Name="EnableBlocklistsToggle" Toggled="EnableBlocklistsToggle_Toggled"/>
                        </controls:SettingsCard>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.blocklists.hosts}">
                            <ToggleSwitch x:Name="EnableHostsBlocklistToggle" Toggled="EnableHostsBlocklistToggle_Toggled"/>
                        </controls:SettingsCard>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.blocklists.ports}">
                            <ToggleSwitch x:Name="EnablePortBlocklistToggle" Toggled="EnablePortBlocklistToggle_Toggled"/>
                        </controls:SettingsCard>
                    </controls:SettingsExpander.Items>
                </controls:SettingsExpander>

                <!-- Security, Updates, Maintenance, and About groups are added here by Tasks 6-8. -->
```

- [ ] **Step 3: Wire the code-behind**

In `SettingsPage.xaml.cs`, extend `SeedGeneral` (rename it `SeedControls` since it now seeds more than General) and add the six new `Toggled` handlers. Replace:

```csharp
        private void SeedGeneral()
        {
            _clientSettings = ClientSettings.Load();
            ThemeCombo.SelectedIndex = _clientSettings.UiTheme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };
        }
```

with:

```csharp
        private void SeedControls()
        {
            _clientSettings = ClientSettings.Load();
            ThemeCombo.SelectedIndex = _clientSettings.UiTheme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };

            var config = App.Firewall.Config!;
            AllowLocalSubnetToggle.IsOn = config.ActiveProfile.AllowLocalSubnet;
            DisplayOffBlockToggle.IsOn = config.ActiveProfile.DisplayOffBlock;

            EnableBlocklistsToggle.IsOn = config.Blocklists.EnableBlocklists;
            EnableHostsBlocklistToggle.IsOn = config.Blocklists.EnableHostsBlocklist;
            EnablePortBlocklistToggle.IsOn = config.Blocklists.EnablePortBlocklist;
            UpdateBlocklistSubTogglesEnabled();
        }

        /// <summary>Hosts/Ports blocklist toggles are only meaningful while the master toggle is
        /// on - same disabled-when-master-off relationship WinForms' chkEnableBlocklists_
        /// CheckedChanged already has between chkEnableBlocklists and chkHostsBlocklist/
        /// chkBlockMalwarePorts.</summary>
        private void UpdateBlocklistSubTogglesEnabled()
        {
            var enabled = EnableBlocklistsToggle.IsOn;
            EnableHostsBlocklistToggle.IsEnabled = enabled;
            EnablePortBlocklistToggle.IsEnabled = enabled;
        }
```

Update the call site in `RefreshAsync` (`SeedGeneral();` → `SeedControls();`).

Add the six toggle handlers (after `ThemeCombo_SelectionChanged`):

```csharp
        /// <summary>ToggleSwitch fires Toggled synchronously from inside its own event dispatch -
        /// deferring via DispatcherQueue.TryEnqueue before committing is the same fix Rules Task 4
        /// applied for the identical reentrancy hazard (see this plan's Global Constraints).</summary>
        private void AllowLocalSubnetToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // _seeding: ignore the Toggled fired by SeedControls programmatically setting IsOn to
            // the just-refreshed value - that is a re-sync, not a user change. _committing: refuse
            // a second commit while one is already in flight, the same guard
            // RulesPage.ToggleSpecialAsync uses, rather than the narrower _busy (which only guards
            // Refresh) this handler used before self-review caught the gap.
            if (_seeding || _committing) return;
            var value = AllowLocalSubnetToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.ActiveProfile.AllowLocalSubnet = value));
        }

        private void DisplayOffBlockToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = DisplayOffBlockToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.ActiveProfile.DisplayOffBlock = value));
        }

        private void EnableBlocklistsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = EnableBlocklistsToggle.IsOn;
            UpdateBlocklistSubTogglesEnabled();
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.Blocklists.EnableBlocklists = value));
        }

        private void EnableHostsBlocklistToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = EnableHostsBlocklistToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.Blocklists.EnableHostsBlocklist = value));
        }

        private void EnablePortBlocklistToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = EnablePortBlocklistToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.Blocklists.EnablePortBlocklist = value));
        }

        /// <summary>Shared by every immediate-commit toggle in this page (Protection, Blocklists,
        /// and later Updates/Security's Lock-hosts-file): commits, then refreshes either way so
        /// every toggle's visual state reconciles back to the server's truth - the same pattern
        /// RulesPage.ToggleSpecialAsync uses for the identical reason.</summary>
        private async Task CommitToggleAsync(Action<ServerConfiguration> mutate)
        {
            var resp = await CommitAsync(mutate);
            await RefreshAsync();

            if (resp != MessageType.PUT_SETTINGS)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.CommitFailedTitle), FailureDetail(resp,
                    LocKeys.Settings.CommitFailedLockedDetail, LocKeys.Settings.CommitFailedStaleDetail,
                    LocKeys.Settings.CommitFailedGenericDetail));
            }
        }
```

Add `using System.Threading.Tasks;` if not already present (it is, from Task 4).

- [ ] **Step 4: Build**

`dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo` — 0 errors expected.

- [ ] **Step 5: Run the test suite**

`dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj` — PASS, including `LocTests` for the 8 new keys.

- [ ] **Step 6: Verify against sample data**

Launch with `--sample-data`, navigate to Settings. Toggle Allow local network and Block-when-display-off — both persist across a Refresh. Toggle Enable blocklists off — Hosts/Ports sub-toggles grey out; toggle it back on — they re-enable, keeping their prior on/off state. Toggle Hosts/Ports individually while the master is on. Confirm no crash on rapid toggling (the reentrancy fix holds). Relaunch with `--sample-locked`: toggling any of the five shows the honest locked failure and the toggle visually reverts.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add Settings Protection and Blocklists groups"
```

## Task 6: Security group

**Files:**
- Modify: `SimpleDeFence.UI/Pages/SettingsPage.xaml`, `.xaml.cs`, `LocKeys.cs` + both JSONs

**Interfaces:**
- Consumes: `CommitAsync`/`CommitToggleAsync` (Tasks 4-5), `LockAsync`/`UnlockAsync`/`SetPasswordAsync` (Task 3), `ServerConfiguration.LockHostsFile`, `IFirewallClient.State.HasPassword`/`.Locked`
- Produces: the Security group — lock-hosts-file toggle, password status + Set/Change/Remove, Lock now / Unlock

The most involved group in this plan: three distinct sub-concerns (a plain immediate-commit toggle, a password form, and a lock/unlock action pair), each following the established patterns from earlier tasks/pages rather than inventing a new one.

- [ ] **Step 1: Add the localization keys**

In `LocKeys.cs`, extend `Settings`:

```csharp
            public const string SecurityLockHostsFile = "settings.security.lockHostsFile";
            public const string SecurityLockHostsFileDescription = "settings.security.lockHostsFileDescription";

            public const string SecurityPasswordSet = "settings.security.passwordSet";
            public const string SecurityPasswordNotSet = "settings.security.passwordNotSet";
            public const string SecurityNewPassword = "settings.security.newPassword";
            public const string SecurityConfirmPassword = "settings.security.confirmPassword";
            public const string SecuritySetPassword = "settings.security.setPassword";
            public const string SecurityRemovePassword = "settings.security.removePassword";
            public const string SecurityPasswordMismatchTitle = "settings.security.passwordMismatch.title";
            public const string SecurityPasswordMismatchDetail = "settings.security.passwordMismatch.detail";
            public const string SecurityPasswordUpdatedTitle = "settings.security.passwordUpdated.title";
            public const string SecurityPasswordUpdatedBody = "settings.security.passwordUpdated.body";
            public const string SecurityPasswordRemovedBody = "settings.security.passwordRemoved.body";
            public const string SecurityPasswordUpdateFailedTitle = "settings.security.passwordUpdateFailed.title";

            public const string SecurityLockedStatus = "settings.security.lockedStatus";
            public const string SecurityUnlockedStatus = "settings.security.unlockedStatus";
            public const string SecurityLockNow = "settings.security.lockNow";
            public const string SecurityLockFailedTitle = "settings.security.lockFailed.title";
            public const string SecurityUnlockPasswordPlaceholder = "settings.security.unlockPasswordPlaceholder";
            public const string SecurityUnlock = "settings.security.unlock";
            public const string SecurityUnlockFailedTitle = "settings.security.unlockFailed.title";
            public const string SecurityUnlockFailedDetail = "settings.security.unlockFailed.detail";
```

In `Strings.en.json`, extend `"settings"`:

```json
    "security": {
      "lockHostsFile": "Lock the hosts file",
      "lockHostsFileDescription": "Prevents other programs from modifying the hosts file.",
      "passwordSet": "A password is set.",
      "passwordNotSet": "No password is set.",
      "newPassword": "New password",
      "confirmPassword": "Confirm password",
      "setPassword": "Set password",
      "removePassword": "Remove password",
      "passwordMismatch": {
        "title": "Passwords do not match",
        "detail": "Type the same password in both fields."
      },
      "passwordUpdated": {
        "title": "Password updated",
        "body": "The configuration password was changed."
      },
      "passwordRemoved": {
        "body": "The configuration password was removed."
      },
      "passwordUpdateFailed": {
        "title": "Could not update the password"
      },
      "lockedStatus": "The configuration is locked.",
      "unlockedStatus": "The configuration is not locked.",
      "lockNow": "Lock now",
      "lockFailed": {
        "title": "Could not lock the configuration"
      },
      "unlockPasswordPlaceholder": "Password",
      "unlock": "Unlock",
      "unlockFailed": {
        "title": "Could not unlock the configuration",
        "detail": "The password was not accepted."
      }
    }
```

In `Strings.pt-BR.json`, extend `"settings"`:

```json
    "security": {
      "lockHostsFile": "Bloquear o arquivo hosts",
      "lockHostsFileDescription": "Impede que outros programas modifiquem o arquivo hosts.",
      "passwordSet": "Uma senha está definida.",
      "passwordNotSet": "Nenhuma senha está definida.",
      "newPassword": "Nova senha",
      "confirmPassword": "Confirmar senha",
      "setPassword": "Definir senha",
      "removePassword": "Remover senha",
      "passwordMismatch": {
        "title": "As senhas não coincidem",
        "detail": "Digite a mesma senha nos dois campos."
      },
      "passwordUpdated": {
        "title": "Senha atualizada",
        "body": "A senha da configuração foi alterada."
      },
      "passwordRemoved": {
        "body": "A senha da configuração foi removida."
      },
      "passwordUpdateFailed": {
        "title": "Não foi possível atualizar a senha"
      },
      "lockedStatus": "A configuração está bloqueada.",
      "unlockedStatus": "A configuração não está bloqueada.",
      "lockNow": "Bloquear agora",
      "lockFailed": {
        "title": "Não foi possível bloquear a configuração"
      },
      "unlockPasswordPlaceholder": "Senha",
      "unlock": "Desbloquear",
      "unlockFailed": {
        "title": "Não foi possível desbloquear a configuração",
        "detail": "A senha não foi aceita."
      }
    }
```

- [ ] **Step 2: Add the XAML**

Replace the `<!-- Security, Updates, Maintenance, and About groups are added here by Tasks 6-8. -->` comment with:

```xml
                <controls:SettingsExpander Header="{loc:Loc Key=settings.section.security}" IsExpanded="True">
                    <controls:SettingsExpander.HeaderIcon>
                        <FontIcon Glyph="&#xE72E;"/>
                    </controls:SettingsExpander.HeaderIcon>
                    <controls:SettingsExpander.Items>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.security.lockHostsFile}"
                                                Description="{loc:Loc Key=settings.security.lockHostsFileDescription}">
                            <ToggleSwitch x:Name="LockHostsFileToggle" Toggled="LockHostsFileToggle_Toggled"/>
                        </controls:SettingsCard>

                        <controls:SettingsCard IsClickEnabled="False">
                            <StackPanel Spacing="8" HorizontalAlignment="Stretch">
                                <TextBlock x:Name="PasswordStatusText" Style="{StaticResource BodyStrongTextBlockStyle}"/>
                                <TextBox x:Name="NewPasswordBox" PlaceholderText="{loc:Loc Key=settings.security.newPassword}"/>
                                <PasswordBox x:Name="NewPasswordConfirmBox" PlaceholderText="{loc:Loc Key=settings.security.confirmPassword}"/>
                                <StackPanel Orientation="Horizontal" Spacing="8">
                                    <Button x:Name="SetPasswordButton" Content="{loc:Loc Key=settings.security.setPassword}" Click="SetPasswordButton_Click"/>
                                    <Button x:Name="RemovePasswordButton" Content="{loc:Loc Key=settings.security.removePassword}" Click="RemovePasswordButton_Click"/>
                                </StackPanel>
                            </StackPanel>
                        </controls:SettingsCard>

                        <controls:SettingsCard IsClickEnabled="False">
                            <StackPanel Spacing="8" HorizontalAlignment="Stretch">
                                <TextBlock x:Name="LockStatusText" Style="{StaticResource BodyStrongTextBlockStyle}"/>
                                <Button x:Name="LockNowButton" Content="{loc:Loc Key=settings.security.lockNow}" Click="LockNowButton_Click"/>
                                <StackPanel x:Name="UnlockPanel" Orientation="Horizontal" Spacing="8" Visibility="Collapsed">
                                    <PasswordBox x:Name="UnlockPasswordBox" PlaceholderText="{loc:Loc Key=settings.security.unlockPasswordPlaceholder}"/>
                                    <Button x:Name="UnlockButton" Content="{loc:Loc Key=settings.security.unlock}" Click="UnlockButton_Click"/>
                                </StackPanel>
                            </StackPanel>
                        </controls:SettingsCard>
                    </controls:SettingsExpander.Items>
                </controls:SettingsExpander>

                <!-- Updates, Maintenance, and About groups are added here by Tasks 7-8. -->
```

Note: `NewPasswordBox` is a plain `TextBox`, not a `PasswordBox`, matching this codebase's existing convention of not masking the *first* entry of a new value (there is no precedent for password masking anywhere else in this app; `PasswordBox` is used for the confirm/unlock fields where a typo is the only risk being guarded against, but the primary intent here is simplicity over WinForms parity — `SettingsForm` used two plain `TextBox`es for both password fields). If stricter masking is wanted for `NewPasswordBox` too, swap it for `PasswordBox` in Step 3's code-behind (`NewPasswordBox.Text` becomes `NewPasswordBox.Password`) — either is a two-line change and neither affects the commit logic below.

- [ ] **Step 3: Wire the code-behind**

Extend `SeedControls` (add after the Blocklists block):

```csharp
            LockHostsFileToggle.IsOn = config.LockHostsFile;

            var hasPassword = App.Firewall.State?.HasPassword ?? false;
            var locked = App.Firewall.State?.Locked ?? false;

            PasswordStatusText.Text = Loc.T(hasPassword ? LocKeys.Settings.SecurityPasswordSet : LocKeys.Settings.SecurityPasswordNotSet);
            RemovePasswordButton.IsEnabled = hasPassword && !_committing;

            LockStatusText.Text = Loc.T(locked ? LocKeys.Settings.SecurityLockedStatus : LocKeys.Settings.SecurityUnlockedStatus);
            // Locking without a password is a server-side no-op (PasswordLock.Locked's setter is
            // gated on HasPassword) - disabling the button when there is nothing to lock with
            // keeps the UI from offering an action that would silently do nothing.
            LockNowButton.IsEnabled = hasPassword && !locked && !_committing;
            UnlockPanel.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
```

Add the toggle handler (near the other `Toggled` handlers):

```csharp
        private void LockHostsFileToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = LockHostsFileToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.LockHostsFile = value));
        }
```

Add the password/lock handlers (all plain `Button.Click` handlers, the same safe shape `RulesPage.RemoveButton_Click`/`ApplyButton_Click` use):

```csharp
        private async void SetPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;

            var password = NewPasswordBox.Text;
            if (password != NewPasswordConfirmBox.Password)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityPasswordMismatchTitle),
                    Loc.T(LocKeys.Settings.SecurityPasswordMismatchDetail));
                return;
            }

            await SetPasswordAsync(password, Loc.T(LocKeys.Settings.SecurityPasswordUpdatedBody));
        }

        private async void RemovePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;
            await SetPasswordAsync(string.Empty, Loc.T(LocKeys.Settings.SecurityPasswordRemovedBody));
        }

        private async Task SetPasswordAsync(string password, string successBody)
        {
            _committing = true;
            UpdateControlsEnabled();
            MessageType resp;
            try
            {
                resp = await App.Firewall.SetPasswordAsync(password);
            }
            catch (Exception ex)
            {
                _committing = false;
                UpdateControlsEnabled();
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityPasswordUpdateFailedTitle), ex.Message);
                return;
            }
            _committing = false;

            NewPasswordBox.Text = string.Empty;
            NewPasswordConfirmBox.Password = string.Empty;

            if (resp == MessageType.SET_PASSWORD)
            {
                await RefreshAsync();
                ShowNotice(InfoBarSeverity.Success, Loc.T(LocKeys.Settings.SecurityPasswordUpdatedTitle), successBody);
            }
            else
            {
                await RefreshAsync();
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityPasswordUpdateFailedTitle), FailureDetail(resp,
                    LocKeys.Settings.CommitFailedLockedDetail, LocKeys.Settings.CommitFailedStaleDetail,
                    LocKeys.Settings.CommitFailedGenericDetail));
            }
        }

        private async void LockNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;

            _committing = true;
            UpdateControlsEnabled();
            MessageType resp;
            try
            {
                resp = await App.Firewall.LockAsync();
            }
            catch (Exception ex)
            {
                _committing = false;
                UpdateControlsEnabled();
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityLockFailedTitle), ex.Message);
                return;
            }
            _committing = false;

            await RefreshAsync();
            if (resp != MessageType.LOCK)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityLockFailedTitle), FailureDetail(resp,
                    LocKeys.Settings.CommitFailedLockedDetail, LocKeys.Settings.CommitFailedStaleDetail,
                    LocKeys.Settings.CommitFailedGenericDetail));
            }
        }

        private async void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;

            var password = UnlockPasswordBox.Password;
            _committing = true;
            UpdateControlsEnabled();
            MessageType resp;
            try
            {
                resp = await App.Firewall.UnlockAsync(password);
            }
            catch (Exception ex)
            {
                _committing = false;
                UpdateControlsEnabled();
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityUnlockFailedTitle), ex.Message);
                return;
            }
            _committing = false;

            UnlockPasswordBox.Password = string.Empty;
            await RefreshAsync();

            if (resp != MessageType.UNLOCK)
            {
                // A wrong password is the common failure here, not a lock/changeset condition -
                // FailureDetail's generic branch ("The service returned {0}") would be honest but
                // unhelpful, so this uses its own specific wording instead.
                await ShowResultAsync(Loc.T(LocKeys.Settings.SecurityUnlockFailedTitle),
                    Loc.T(LocKeys.Settings.SecurityUnlockFailedDetail));
            }
        }
```

Finally, extend `UpdateControlsEnabled` (Task 4 left it empty) to disable the Security group's buttons while a commit is in flight, matching `RulesPage.UpdateRemoveButton`'s pattern:

```csharp
        private void UpdateControlsEnabled()
        {
            SetPasswordButton.IsEnabled = !_committing;
            var hasPassword = App.Firewall.State?.HasPassword ?? false;
            var locked = App.Firewall.State?.Locked ?? false;
            RemovePasswordButton.IsEnabled = hasPassword && !_committing;
            LockNowButton.IsEnabled = hasPassword && !locked && !_committing;
            UnlockButton.IsEnabled = !_committing;
        }
```

- [ ] **Step 4: Build**

`dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo` — 0 errors expected.

- [ ] **Step 5: Run the test suite**

`dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj` — PASS, including `LocTests` for the new keys.

- [ ] **Step 6: Verify against sample data**

Launch with `--sample-data` (starts with no password). Confirm: "No password is set", Lock now disabled, no Unlock panel. Type a password + matching confirm, click Set password — success notice, status flips to "A password is set", Lock now enables. Click Lock now — status flips to "The configuration is locked", the Unlock panel appears, Lock now disables. Type the same text (sample data accepts any non-empty input) into Unlock, click Unlock — status flips back, Unlock panel hides. Click Remove password — status flips to "No password is set" and Lock now disables again. Toggle Lock the hosts file — persists across Refresh. Type mismatched New/Confirm passwords and click Set password — the mismatch dialog appears, nothing commits. Then relaunch with `--sample-locked` (starts locked with a password) and confirm every action here (toggle, Set/Remove password, Lock now) shows the honest locked failure.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add Settings Security group: password management and lock/unlock"
```

## Task 7: Updates and About groups

**Files:**
- Modify: `SimpleDeFence.UI/Pages/SettingsPage.xaml`, `.xaml.cs`, `LocKeys.cs` + both JSONs

**Interfaces:**
- Consumes: `CommitToggleAsync` (Task 5), `ServerConfiguration.AutoUpdateCheck`
- Produces: the two simplest remaining groups — Updates (one toggle, matching the Protection/Blocklists shape exactly) and About (fully static, no IPC)

Per this plan's scope decision 2, "Check for updates now" (the manual action) is NOT included here — only the toggle. About mirrors `SettingsForm`'s existing About tab content (version, GitHub link, license, attributions) using the WinUI equivalents of its `Process.Start(..., UseShellExecute = true)` calls, which need no adaptation.

- [ ] **Step 1: Add the localization keys**

In `LocKeys.cs`, extend `Settings`:

```csharp
            public const string UpdatesAutoCheck = "settings.updates.autoCheck";
            public const string UpdatesAutoCheckDescription = "settings.updates.autoCheckDescription";

            public const string AboutVersion = "settings.about.version";
            public const string AboutHomepage = "settings.about.homepage";
            public const string AboutLicense = "settings.about.license";
            public const string AboutAttributions = "settings.about.attributions";
            public const string AboutLinkFailedTitle = "settings.about.linkFailed.title";
```

In `Strings.en.json`, extend `"settings"`:

```json
    "updates": {
      "autoCheck": "Automatically check for updates",
      "autoCheckDescription": "SimpleDeFence periodically checks for new versions."
    },
    "about": {
      "version": "Version {0}",
      "homepage": "GitHub / homepage",
      "license": "License",
      "attributions": "Attributions",
      "linkFailed": {
        "title": "Could not open"
      }
    }
```

In `Strings.pt-BR.json`, extend `"settings"`:

```json
    "updates": {
      "autoCheck": "Verificar atualizações automaticamente",
      "autoCheckDescription": "O SimpleDeFence verifica periodicamente se há novas versões."
    },
    "about": {
      "version": "Versão {0}",
      "homepage": "GitHub / site",
      "license": "Licença",
      "attributions": "Atribuições",
      "linkFailed": {
        "title": "Não foi possível abrir"
      }
    }
```

- [ ] **Step 2: Add the XAML**

Replace the `<!-- Updates, Maintenance, and About groups are added here by Tasks 7-8. -->` comment with:

```xml
                <controls:SettingsExpander Header="{loc:Loc Key=settings.section.updates}" IsExpanded="True">
                    <controls:SettingsExpander.HeaderIcon>
                        <FontIcon Glyph="&#xE895;"/>
                    </controls:SettingsExpander.HeaderIcon>
                    <controls:SettingsExpander.Items>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.updates.autoCheck}"
                                                Description="{loc:Loc Key=settings.updates.autoCheckDescription}">
                            <ToggleSwitch x:Name="AutoUpdateCheckToggle" Toggled="AutoUpdateCheckToggle_Toggled"/>
                        </controls:SettingsCard>
                    </controls:SettingsExpander.Items>
                </controls:SettingsExpander>

                <!-- Maintenance group is added here by Task 8. -->

                <controls:SettingsExpander Header="{loc:Loc Key=settings.section.about}" IsExpanded="True">
                    <controls:SettingsExpander.HeaderIcon>
                        <FontIcon Glyph="&#xE946;"/>
                    </controls:SettingsExpander.HeaderIcon>
                    <controls:SettingsExpander.Items>
                        <controls:SettingsCard x:Name="AboutVersionCard" IsClickEnabled="False"/>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.about.homepage}" IsClickEnabled="True" Click="AboutHomepage_Click"/>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.about.license}" IsClickEnabled="True" Click="AboutLicense_Click"/>
                        <controls:SettingsCard Header="{loc:Loc Key=settings.about.attributions}" IsClickEnabled="True" Click="AboutAttributions_Click"/>
                    </controls:SettingsExpander.Items>
                </controls:SettingsExpander>
```

- [ ] **Step 3: Wire the code-behind**

Extend `SeedControls` (add after the Security block):

```csharp
            AutoUpdateCheckToggle.IsOn = config.AutoUpdateCheck;

            AboutVersionCard.Header = Loc.T(LocKeys.Settings.AboutVersion,
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");
```

Add the toggle handler:

```csharp
        private void AutoUpdateCheckToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_seeding || _committing) return;
            var value = AutoUpdateCheckToggle.IsOn;
            DispatcherQueue.TryEnqueue(() => _ = CommitToggleAsync(config => config.AutoUpdateCheck = value));
        }
```

Add the three link handlers (`SettingsCard.Click` is a plain top-level event, the same safe shape as any other `Button.Click` in this codebase):

```csharp
        private void AboutHomepage_Click(object sender, RoutedEventArgs e)
            => OpenUrl("https://github.com/fcoltro/SimpleDeFence");

        private void AboutLicense_Click(object sender, RoutedEventArgs e)
            => OpenLocalDoc("License.rtf");

        private void AboutAttributions_Click(object sender, RoutedEventArgs e)
            => OpenLocalDoc("Attributions.txt");

        private void OpenUrl(string url)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Settings.AboutLinkFailedTitle), ex.Message);
            }
        }

        private void OpenLocalDoc(string fileName)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
                var psi = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(dir, fileName)) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Settings.AboutLinkFailedTitle), ex.Message);
            }
        }
```

- [ ] **Step 4: Build**

`dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo` — 0 errors expected.

- [ ] **Step 5: Run the test suite**

`dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj` — PASS, including `LocTests` for the new keys.

- [ ] **Step 6: Verify against sample data**

Launch with `--sample-data`. Toggle Automatically check for updates — persists across Refresh, shows the honest locked failure under `--sample-locked` like every other toggle in this page. Confirm the About group shows a real version string, and clicking GitHub/homepage opens the repository in the default browser (License/Attributions will fail gracefully with the "Could not open" InfoBar in a dev build, since `License.rtf`/`Attributions.txt` are packaged install-time assets not present next to the build output — confirm the failure is a clean InfoBar, not a crash).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add Settings Updates and About groups"
```

## Task 8: Maintenance group — Import and Export

**Files:**
- Modify: `SimpleDeFence.UI/Pages/SettingsPage.xaml`, `.xaml.cs`, `LocKeys.cs` + both JSONs

**Interfaces:**
- Consumes: `ConfigExport` (Task 2), `CommitAsync` (Task 4), `App.MainWindow` (existing, from Rules Task 6), `WinRT.Interop.WindowNative`/`InitializeWithWindow` (existing pattern from Rules)
- Produces: the last group — Import (.tws) replaces the whole server config; Export (.tws) writes the current one

Per this plan's Global Constraints, this task uses `Windows.Storage.Pickers.FileOpenPicker`/`FileSavePicker` (the design doc's recommended option over a WinForms dialog), following the exact `InitializeWithWindow` pattern Rules' executable picker already established and the exact try/catch-around-the-picker-call pattern that guards it. **This carries the same known, previously-flagged verification risk as Rules' executable picker**: the WinRT `FileOpenPicker`/`FileSavePicker` could not be live-tested end-to-end in an automated session across three separate attempts in the Rules plan (it hung; ruled out as a general environment problem, not proven to be a code defect). Say so honestly in this task's verification step rather than claiming a test that could not run.

- [ ] **Step 1: Add the localization keys**

In `LocKeys.cs`, extend `Settings`:

```csharp
            public const string MaintenanceImport = "settings.maintenance.import";
            public const string MaintenanceExport = "settings.maintenance.export";
            public const string MaintenanceFilePickerName = "settings.maintenance.filePickerName";
            public const string MaintenanceImportSuccessTitle = "settings.maintenance.importSuccess.title";
            public const string MaintenanceImportSuccessBody = "settings.maintenance.importSuccess.body";
            public const string MaintenanceImportFailedTitle = "settings.maintenance.importFailed.title";
            public const string MaintenanceExportSuccessTitle = "settings.maintenance.exportSuccess.title";
            public const string MaintenanceExportSuccessBody = "settings.maintenance.exportSuccess.body";
            public const string MaintenanceExportFailedTitle = "settings.maintenance.exportFailed.title";
```

In `Strings.en.json`, extend `"settings"`:

```json
    "maintenance": {
      "import": "Import configuration...",
      "export": "Export configuration...",
      "filePickerName": "SimpleDeFence settings",
      "importSuccess": {
        "title": "Configuration imported",
        "body": "{0} was imported."
      },
      "importFailed": {
        "title": "Could not import the configuration"
      },
      "exportSuccess": {
        "title": "Configuration exported",
        "body": "Saved to {0}."
      },
      "exportFailed": {
        "title": "Could not export the configuration"
      }
    }
```

In `Strings.pt-BR.json`, extend `"settings"`:

```json
    "maintenance": {
      "import": "Importar configuração...",
      "export": "Exportar configuração...",
      "filePickerName": "Configurações do SimpleDeFence",
      "importSuccess": {
        "title": "Configuração importada",
        "body": "{0} foi importado."
      },
      "importFailed": {
        "title": "Não foi possível importar a configuração"
      },
      "exportSuccess": {
        "title": "Configuração exportada",
        "body": "Salvo em {0}."
      },
      "exportFailed": {
        "title": "Não foi possível exportar a configuração"
      }
    }
```

- [ ] **Step 2: Add the XAML**

Replace `<!-- Maintenance group is added here by Task 8. -->` with:

```xml
                <controls:SettingsExpander Header="{loc:Loc Key=settings.section.maintenance}" IsExpanded="True">
                    <controls:SettingsExpander.HeaderIcon>
                        <FontIcon Glyph="&#xE74E;"/>
                    </controls:SettingsExpander.HeaderIcon>
                    <controls:SettingsExpander.Items>
                        <controls:SettingsCard IsClickEnabled="False">
                            <StackPanel Orientation="Horizontal" Spacing="8">
                                <Button x:Name="ImportButton" Content="{loc:Loc Key=settings.maintenance.import}" Click="ImportButton_Click"/>
                                <Button x:Name="ExportButton" Content="{loc:Loc Key=settings.maintenance.export}" Click="ExportButton_Click"/>
                            </StackPanel>
                        </controls:SettingsCard>
                    </controls:SettingsExpander.Items>
                </controls:SettingsExpander>
```

- [ ] **Step 3: Wire the code-behind**

Add `using System.Runtime.InteropServices.WindowsRuntime;` (for `IBuffer.ToArray()`) and `using System.Collections.Generic;` (for `FileTypeChoices`' `List<string>`) to the top of `SettingsPage.xaml.cs`, then add:

```csharp
        /// <summary>Same safe shape as RulesPage's Add pickers: MenuFlyoutItem/Button.Click is a
        /// plain top-level handler, so committing and showing a result dialog directly from here
        /// is fine per the reentrancy rule.</summary>
        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;

            var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".tws");

            if (App.MainWindow is null)
                return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            global::Windows.Storage.StorageFile? file;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceImportFailedTitle), ex.Message);
                return;
            }

            if (file is null)
                return; // Cancelled - not an error, no dialog, no notice.

            ConfigExport imported;
            try
            {
                var buffer = await global::Windows.Storage.FileIO.ReadBufferAsync(file);
                imported = SerializationHelper.Deserialize(buffer.ToArray(), new ConfigExport());
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceImportFailedTitle), ex.Message);
                return;
            }

            // Replace the whole server config - import means "become this document", not a
            // targeted mutation. Profiles is assigned before ActiveProfileName so the ActiveProfile
            // cache invalidation that setter triggers finds the new profile list, not the old one.
            var resp = await CommitAsync(config =>
            {
                config.LockHostsFile = imported.Service.LockHostsFile;
                config.AutoUpdateCheck = imported.Service.AutoUpdateCheck;
                config.Blocklists = imported.Service.Blocklists;
                config.Profiles = imported.Service.Profiles;
                config.ActiveProfileName = imported.Service.ActiveProfileName;
            });

            if (resp == MessageType.PUT_SETTINGS)
            {
                // The imported Controller (theme) is local-only and applies regardless of the
                // server commit's outcome having already succeeded - saving it after a successful
                // commit keeps the two in step, matching "import means become this document" for
                // the client-local half too.
                imported.Controller.Save();
                App.ApplyTheme(imported.Controller.UiTheme);

                await RefreshAsync();
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceImportSuccessTitle),
                    Loc.T(LocKeys.Settings.MaintenanceImportSuccessBody, file.Name));
            }
            else
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceImportFailedTitle), FailureDetail(resp,
                    LocKeys.Settings.CommitFailedLockedDetail, LocKeys.Settings.CommitFailedStaleDetail,
                    LocKeys.Settings.CommitFailedGenericDetail));
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_committing) return;

            var picker = new global::Windows.Storage.Pickers.FileSavePicker();
            picker.FileTypeChoices.Add(Loc.T(LocKeys.Settings.MaintenanceFilePickerName), new List<string> { ".tws" });
            picker.SuggestedFileName = "SimpleDeFence";

            if (App.MainWindow is null)
                return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            global::Windows.Storage.StorageFile? file;
            try
            {
                file = await picker.PickSaveFileAsync();
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceExportFailedTitle), ex.Message);
                return;
            }

            if (file is null)
                return; // Cancelled - not an error, no dialog, no notice.

            try
            {
                var export = new ConfigExport
                {
                    Service = App.Firewall.Config ?? new ServerConfiguration(),
                    Controller = ClientSettings.Load(),
                };
                var bytes = SerializationHelper.Serialize(export);
                await global::Windows.Storage.FileIO.WriteBytesAsync(file, bytes);

                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceExportSuccessTitle),
                    Loc.T(LocKeys.Settings.MaintenanceExportSuccessBody, file.Path));
            }
            catch (Exception ex)
            {
                await ShowResultAsync(Loc.T(LocKeys.Settings.MaintenanceExportFailedTitle), ex.Message);
            }
        }
```

Extend `UpdateControlsEnabled` to also gate the two new buttons:

```csharp
            ImportButton.IsEnabled = !_committing;
            ExportButton.IsEnabled = !_committing;
```

- [ ] **Step 4: Build**

`dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo` — 0 errors expected. Also verify the net48 WinForms app still builds clean (Framework MSBuild, separate `-t:Restore`/`-t:Build`) — this task's Core-side change (none beyond Task 2, already verified there) should not affect it, but confirm nothing regressed.

- [ ] **Step 5: Run the test suite**

`dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj` — PASS, including `LocTests` for the new keys. This is also the last task in the plan — confirm the full suite is green end to end.

- [ ] **Step 6: Verify against sample data, and be honest about what cannot be verified here**

Launch with `--sample-data`. Confirm both Import and Export buttons render in the Maintenance group. Attempt Export: if the `FileSavePicker` dialog appears (unlike Rules' `FileOpenPicker`, this is untested territory and may or may not hit the same hang), pick a location, confirm the success dialog names the saved path, and confirm the written file is valid JSON matching `ConfigExport`'s shape. Attempt Import with that same exported file: confirm the success dialog appears and the Applications-affecting fields (if any were changed) reflect the import after refresh. Attempt Import with a malformed/empty file: confirm the honest "Could not import" failure, not a crash.

**If either picker hangs the same way Rules' `FileOpenPicker` did** (no dialog window appears, the app otherwise stays responsive), do not fabricate a verification — report it exactly as Rules' final report did: isolated to the WinRT picker API in this automated/sandboxed session, code reads correct against Microsoft's documented pattern, real-machine confirmation still needed before shipping. This is a known, accepted, carried-forward risk (see this plan's Global Constraints and the design doc), not a new one.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add Settings Maintenance group: import and export"
```

## Done when

- `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj` passes (including the new `ClientSettingsTests`/`ConfigExportTests`, and `LocTests` with every new `settings.*`/`nav.settings` key).
- The net48 WinForms app still builds after every task (`SettingsForm` untouched and still works; `ClientSettings`/`ConfigExport` compile clean under net48 via the existing glob-include).
- The app launches onto Connections by default; Settings is reachable via the third nav item and shows all 7 groups working against `--sample-data`; every commit (toggle, password, lock/unlock, import) reports honestly and `--sample-locked` shows the locked failure on every one of them; the real client remains the default with no `--sample-data`.
- No `ContentDialog`/commit is ever reachable synchronously from inside another control's own event dispatch — every immediate-commit toggle in this plan defers via `DispatcherQueue.TryEnqueue`.

## Next plans

1. **"Check for updates now"** (the manual action) and porting `Updater`/`UpdateChecker` to Core — deferred by this plan's scope decision 2.
2. **`ControllerSettings`/`ClientSettings` reconciliation** at the net10 exe-merge migration (ROADMAP.md) — deferred by this plan's scope decision 4.
3. **A quicker Lock/Unlock surface in the mode chip** — deferred by this plan's scope decision 3.
4. **Retiring `SettingsForm`** once the net10 migration lands and this screen has reached full parity, mirroring how Rules retired `ApplicationsPage` only after shipping its replacement.
