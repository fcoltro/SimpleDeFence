# Rules Screen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework the WinUI Rules destination — currently the read-only Applications page — into the designed Rules screen: grouped Applications/Special exceptions, search, compact rows with policy chips, a detail pane for policy editing, an Add split button exposing the signature pickers, and multi-select bulk removal. This is Phase 4 of `docs/superpowers/specs/2026-08-08-winui-gui-modernization-design.md`.

**Architecture:** Rule display/group/filter/edit logic lives in `SimpleDeFence.Core` as pure, unit-tested functions, mirroring `ExceptionDescriptor`/`ConnectionActivity`. The app-database parsing (`DatabaseClasses`) moves from the WinForms-only project into Core so the WinUI GUI can resolve special-exception definitions; the TaskDialog/Resources-dependent parts stay WinForms-side as partial-class members. `IFirewallClient` gains a general profile-commit method that shares the changeset discipline and failure contract of "Allow this app" — `AllowAsync` is reimplemented on top of it so there is exactly one commit path. Every mutation applies immediately with honest success/failure reporting.

**Tech Stack:** C#, WinUI 3 / Windows App SDK 2.3.1, .NET 10 (`net10.0-windows10.0.19041.0`), xunit.

## Global Constraints

Carried over unchanged from the Connections plan:

- Target framework for `SimpleDeFence.UI` and `SimpleDeFence.Tests` is `net10.0-windows10.0.19041.0`; `SimpleDeFence.Core` multi-targets `net48;net10.0-windows10.0.19041.0`.
- **Anything added to `SimpleDeFence.Core` must compile under net48**, because `SimpleDeFence.csproj` glob-compiles Core's sources into the net48 WinForms app. No `net10`-only APIs there.
- **Status is never signalled by colour alone** — always colour *plus* icon *plus* word.
- **The real client is the default in every configuration.** Sample data is only reachable via the `--sample-data` / `--sample-locked` command-line switches.
- **All IPC calls stay off the UI thread.**
- **Never let an unrecognised response look like success.** Applies to every new commit: add, edit, remove, special-toggle. Only `MessageType.PUT_SETTINGS` may read as success.
- **Nothing is removed from the WinForms app.** It stays buildable throughout, including `SettingsForm` and `ApplicationExceptionForm` (untouched — they keep working until their own retirement plan). Deleting the WinUI `ApplicationsPage` is not covered by this constraint: it is new WinUI code being superseded by its own replacement.
- Build the net48 app with `-t:Restore` and `-t:Build` as **separate** MSBuild invocations (see ROADMAP.md).
- Localization: every new user-visible string needs a `LocKeys` constant plus matching entries in **both** `SimpleDeFence.Core/Localization/Strings.en.json` and `Strings.pt-BR.json` — `LocTests` fails the build otherwise.
- `SimpleDeFence.Tests` runs single-threaded (`DisableTestParallelization`) because of `Loc`'s process-wide static culture state.
- `SimpleDeFence.Tests` runs single-threaded (`DisableTestParallelization`) because of `Loc`'s process-wide static culture state.

## Deliberate scope decisions (read before objecting to a "gap")

1. **Policy editing covers presets only**: Blocked, Unrestricted, Unrestricted (LAN only), and TCP/UDP-restricted with the four port fields. `RuleListPolicy` (custom rules) entries render read-only in the detail pane with their summary — the full custom-rule editor is the single biggest editor surface in the WinForms app (`ApplicationExceptionForm`, ~700 lines with its designer) and is its own follow-up plan. Editing a rule that has a `RuleListPolicy` through presets is refused rather than silently discarding the custom rules.
2. **"Pick a window" is a dialog listing visible top-level windows**, not the WinForms hotkey-grab. The hotkey flow needs a global hotkey plus cursor tracking; the window list achieves the same outcome without leaving the window, which is the design's stated reason for the pickers to exist here at all.
3. **Disk auto-detect (`AppFinderForm`) and drag-and-drop / add-folder stay WinForms-only** for now. The Add split button covers the four pickers the design names: executable file, running process, window, UWP package.
4. **The app database is read from disk** (`profiles.json` under the shared `%ProgramData%\SimpleDeFence` folder) by the GUI process, exactly as the WinForms GUI does today — no server changes. A missing/unreadable file (no service installed, sample runs) is not an error: the Special group renders its designed empty state.
5. **Commits are immediate** — no OK/Cancel batching like the WinForms `SettingsForm`'s `TmpConfig` model. This matches the Connections page's Allow action and the design's window-as-review-surface direction. Bulk removal gets a confirmation dialog; everything else applies on click with honest failure reporting.
6. **Special exceptions are toggles, not removable rows.** The profile stores enabled special-exception IDs as strings; the definitions come from the app database. The Special group is split Recommended/Optional exactly as the WinForms settings UI does (`TWUI:Recommended` flag), and definitions flagged `TWUI:Hidden` are never shown.
7. **The WinUI `ApplicationsPage` is replaced by `RulesPage`** and deleted. The nav destination was already labelled Rules and pointed at it; the new page is a superset of its behaviour (search + read-only list), so nothing the old page did is lost.

## File Structure

**Created:**
- `SimpleDeFence.Core/Database/AppDatabase.cs` — data half of the split (namespace stays `SimpleDeFence.DatabaseClasses`, now `public partial`)
- `SimpleDeFence.Core/Database/Application.cs` — moved, minus `LocalizedName`
- `SimpleDeFence.Core/Database/SubjectIdentity.cs` — moved as-is, made `public`
- `SimpleDeFence.Core/RuleList.cs` — pure rule-list logic: `RuleRow`, `RuleGroup`, `RuleListBuilder.Build/Filter`, `RuleEdit.ApplyPreset/RemoveExceptions/SetSpecialEnabled` (net48-safe)
- `SimpleDeFence.Tests/RuleListTests.cs` — tests for the above
- `SimpleDeFence.UI/Pages/RulesPage.xaml` / `.xaml.cs` — the screen
- `SimpleDeFence.Windows/TopLevelWindows.cs` — `EnumWindows` interop: visible top-level window list (net48+net10 safe)

**Modified:**
- `SimpleDeFence/DatabaseClasses/AppDatabase.cs` — becomes the WinForms-only partial (`DBPath`, parameterless `Load()`, `TryGetApp`, `GetExceptionsForApp`)
- `SimpleDeFence/DatabaseClasses/Application.cs` — becomes the WinForms-only partial (`LocalizedName`)
- `SimpleDeFence/AppSerializationContext.cs` — drops the three DatabaseClasses entries (their only consumers, the `GetJsonTypeInfo()` methods, now use Core's context)
- `SimpleDeFence.Core/SerializationHelper.cs` — source-gen context gains `AppDatabase`/`Application`/`SubjectIdentity`
- `SimpleDeFence.UI/Services/IFirewallClient.cs` — `CommitProfileChangesAsync`, `GetAppDatabaseAsync`, `GetRunningProcessesAsync`, `GetTopLevelWindowsAsync`
- `SimpleDeFence.UI/Services/FirewallClient.cs` / `SampleFirewallClient.cs` — the above, plus `AllowAsync` reimplemented on the commit path
- `SimpleDeFence.UI/MainWindow.xaml.cs` — nav tag `rules` → `RulesPage`
- `SimpleDeFence.Core/Localization/LocKeys.cs`, `Strings.en.json`, `Strings.pt-BR.json` — new keys

**Deleted:**
- `SimpleDeFence.UI/Pages/ApplicationsPage.xaml` / `.xaml.cs` (superseded by RulesPage)
- `SimpleDeFence/DatabaseClasses/AppDatabase.cs`, `Application.cs`, `SubjectIdentity.cs` original full bodies (moved/split)

## Task 1: Move the app-database parsing into Core

**Files:**
- Move: `SimpleDeFence/DatabaseClasses/Application.cs`, `SubjectIdentity.cs` → `SimpleDeFence.Core/Database/`
- Split: `SimpleDeFence/DatabaseClasses/AppDatabase.cs` → `SimpleDeFence.Core/Database/AppDatabase.cs` (data) + WinForms-side remainder
- Modify: `SimpleDeFence.Core/SerializationHelper.cs`, `SimpleDeFence/AppSerializationContext.cs`

**Interfaces:**
- Produces: `SimpleDeFence.DatabaseClasses.AppDatabase` (public partial; `Load(string filePath)`, `Save`, `KnownApplications`, `GetApplicationByName`, `FastSearchMachineForKnownApps`, `GetJsonTypeInfo`), `Application` (public partial), `SubjectIdentity` (public)

Why: the WinUI Rules page needs special-exception *definitions* (id → name, Recommended/Hidden flags) to render the Special group. Those definitions live in `profiles.json`, parsed by classes that currently exist only in the WinForms project. All three files are plain data objects; only `AppDatabase.TryGetApp`/`GetExceptionsForApp` (TaskDialog prompt) and `Application.LocalizedName` (WinForms resx) touch WinForms-only APIs, so exactly those members stay behind as partial-class members in the WinForms project.

- [ ] **Step 1: Move `SubjectIdentity.cs` as-is**

`git mv SimpleDeFence/DatabaseClasses/SubjectIdentity.cs SimpleDeFence.Core/Database/SubjectIdentity.cs`, then make the class `public`. It uses only `SimpleDeFence.Windows` (already multi-targeted) and `SimpleDeFence.Parser` (already Core), so it is portable unchanged.

- [ ] **Step 2: Split `Application.cs`**

Core side (`SimpleDeFence.Core/Database/Application.cs`): the whole current file, but class becomes `public partial class Application`, the `LocalizedName` property is removed, and `GetJsonTypeInfo()` returns `SimpleDeFenceJsonContext.Default.Application` (Core's source-gen context in `SerializationHelper.cs`, extended in Step 4).

WinForms side (`SimpleDeFence/DatabaseClasses/Application.cs`, kept): a partial holding only `LocalizedName`:

```csharp
using System.Text.Json.Serialization;

namespace SimpleDeFence.DatabaseClasses
{
    public partial class Application
    {
        // WinForms-only: resolves the display name through the app's resx resources. The WinUI
        // GUI uses Loc instead, so this member stays on this side of the split.
        [JsonIgnore]
        public string LocalizedName
        {
            get
            {
                try
                {
                    string ret = Resources.Exceptions.ResourceManager.GetString(Name);
                    return string.IsNullOrEmpty(ret) ? Name : ret;
                }
                catch
- [ ] **Step 3: Split `AppDatabase.cs`**

Core side (`SimpleDeFence.Core/Database/AppDatabase.cs`): `public partial class AppDatabase` with `_KnownApplications`, both constructors, `KnownApplications`, `GetApplicationByName`, `FastSearchMachineForKnownApps`, `Save`, `GetJsonTypeInfo` (Core context), and a path-taking Load:

```csharp
public static AppDatabase Load(string filePath)
{
    return SerializationHelper.DeserializeFromFile(filePath, new AppDatabase());
}
```

WinForms side (`SimpleDeFence/DatabaseClasses/AppDatabase.cs`, kept): partial holding `DBPath`, the parameterless `Load()` (`=> Load(DBPath);`), `TryGetApp`, and `GetExceptionsForApp` verbatim, with its current usings (`Microsoft.Samples.TaskDialog`, `System.Globalization`).

- [ ] **Step 4: Extend Core's source-gen context**

In `SimpleDeFence.Core/SerializationHelper.cs`, add to the context class:

```csharp
[JsonSerializable(typeof(DatabaseClasses.AppDatabase))]
[JsonSerializable(typeof(DatabaseClasses.Application))]
[JsonSerializable(typeof(DatabaseClasses.SubjectIdentity))]
```

The context class in that file is `SourceGenerationContext` (internal partial); the moved `GetJsonTypeInfo()` implementations return `SourceGenerationContext.Default.AppDatabase` / `.Application` / `.SubjectIdentity`.

- [ ] **Step 5: Trim the WinForms serialization context**

In `SimpleDeFence/AppSerializationContext.cs`, remove the three `[JsonSerializable(typeof(DatabaseClasses.*))]` entries. Grep first for `AppSourceGenerationContext.Default.AppDatabase` / `.Application` / `.SubjectIdentity` — every consumer must be one of the moved `GetJsonTypeInfo()` methods; if any other call site appears, stop and keep that entry.

- [ ] **Step 6: Build everything**

`dotnet build SimpleDeFence.UI` (drags Core net10), then the net48 app via Framework MSBuild (separate Restore/Build). Expected: both clean. The WinForms app glob-compiles Core, so the moved files are compiled twice into the net48 app — that is the existing model, not a problem.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Move app-database parsing into Core, split WinForms-only members"
```

## Task 2: Core rule-list logic

**Files:**
- Create: `SimpleDeFence.Core/RuleList.cs`
- Test: `SimpleDeFence.Tests/RuleListTests.cs`

**Interfaces:**
- Consumes: `ServerProfileConfiguration`, `DatabaseClasses.AppDatabase`/`Application` (Task 1), `ExceptionDescriptor` (existing)
- Produces: `SimpleDeFence.RuleGroup` (`Applications`, `SpecialRecommended`, `SpecialOptional`), `SimpleDeFence.RuleRow`, `SimpleDeFence.RuleListBuilder` (`Build`, `Filter`), `SimpleDeFence.RuleEdit` (`ApplyPreset`, `RemoveExceptions`, `SetSpecialEnabled`)

Why: grouping, filtering, and the edit/remove/toggle mutations are pure data transformations. Putting them in Core makes them unit-testable without a UI and reusable from the WinForms app later — the same pattern that caught the `ServiceSubject`/`ExecutableSubject` bug.
                {
                    return Name;
                }
            }
        }
    }
}
```

Check the current file's `#nullable` state and keep it identical in both halves.

- [ ] **Step 1: Write the failing tests**

Create `SimpleDeFence.Tests/RuleListTests.cs`:

```csharp
using SimpleDeFence;
using SimpleDeFence.DatabaseClasses;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class RuleListTests
    {
        private static Application Special(string name, bool recommended, bool hidden = false)
        {
            var app = new Application { Name = name };
            app.Flags!["TWUI:SPECIAL"] = null;
            if (recommended) app.Flags["TWUI:RECOMMENDED"] = null;
            if (hidden) app.Flags["TWUI:HIDDEN"] = null;
            return app;
        }

        private static FirewallExceptionV3 ExeRule(string path)
            => new(new ExecutableSubject(path), new TcpUdpPolicy(true));

        [Fact]
        public void Build_groups_applications_and_splits_special_by_recommendation()
        {
            var profile = new ServerProfileConfiguration("Default");
            profile.AppExceptions.Add(ExeRule(@"C:\Apps\a.exe"));
            profile.SpecialExceptions.Add("Windows_Update");

            var db = new AppDatabase(new List<Application>
            {
                Special("Windows_Update", recommended: true),
                Special("Gaming", recommended: false),
                Special("Secret", recommended: true, hidden: true),
                new Application { Name = "NotSpecial" }, // no TWUI:Special flag
            });

            var rows = RuleListBuilder.Build(profile, db);

            Assert.Equal(3, rows.Count); // hidden and non-special definitions never appear
            Assert.Equal(RuleGroup.Applications, rows.Single(r => r.Name == "a.exe").Group);
            var winUpd = rows.Single(r => r.SpecialId == "Windows_Update");
            Assert.Equal(RuleGroup.SpecialRecommended, winUpd.Group);
            Assert.True(winUpd.Enabled);
            var gaming = rows.Single(r => r.SpecialId == "Gaming");
            Assert.Equal(RuleGroup.SpecialOptional, gaming.Group);
            Assert.False(gaming.Enabled);
        }

        [Fact]
        public void Build_sorts_applications_by_name_case_insensitively()
        {
            var profile = new ServerProfileConfiguration("Default");
            profile.AppExceptions.Add(ExeRule(@"C:\Apps\zed.exe"));
            profile.AppExceptions.Add(ExeRule(@"C:\Apps\Alpha.exe"));

            var rows = RuleListBuilder.Build(profile, new AppDatabase());

            Assert.Equal(new[] { "Alpha.exe", "zed.exe" }, rows.Where(r => r.Group == RuleGroup.Applications).Select(r => r.Name).ToArray());
        }

        [Fact]
        public void Filter_matches_name_and_detail_case_insensitively()
        {
            var profile = new ServerProfileConfiguration("Default");
            profile.AppExceptions.Add(ExeRule(@"C:\Apps\Firefox\firefox.exe"));
            profile.AppExceptions.Add(ExeRule(@"C:\Apps\git.exe"));

            var rows = RuleListBuilder.Build(profile, new AppDatabase());

            Assert.Single(RuleListBuilder.Filter(rows, "FIRE"));
            Assert.Single(RuleListBuilder.Filter(rows, @"C:\Apps\git"));
            Assert.Equal(2, RuleListBuilder.Filter(rows, "").Count);
        }

        [Fact]
        public void ApplyPreset_keeps_id_and_swaps_policy()
        {
            var original = ExeRule(@"C:\Apps\a.exe");
            var edited = RuleEdit.ApplyPreset(original, new HardBlockPolicy());

            Assert.Equal(original.Id, edited.Id);
            Assert.Equal(PolicyType.HardBlock, edited.Policy.PolicyType);
            Assert.Same(original.Subject, edited.Subject);
        }

        [Fact]
        public void ApplyPreset_refuses_rule_list_policies()
        {
            var original = new FirewallExceptionV3(new ExecutableSubject(@"C:\Apps\a.exe"), new RuleListPolicy());

            Assert.Throws<InvalidOperationException>(() => RuleEdit.ApplyPreset(original, new UnrestrictedPolicy()));
        }

        [Fact]
        public void RemoveExceptions_removes_exactly_the_given_ids()
        {
            var profile = new ServerProfileConfiguration("Default");
            var keep = ExeRule(@"C:\Apps\keep.exe");
            var drop = ExeRule(@"C:\Apps\drop.exe");
            profile.AppExceptions.Add(keep);
            profile.AppExceptions.Add(drop);

            RuleEdit.RemoveExceptions(profile, new[] { drop.Id });

            Assert.Single(profile.AppExceptions);
            Assert.Equal(keep.Id, profile.AppExceptions[0].Id);
        }

        [Fact]
        public void SetSpecialEnabled_adds_once_and_removes()
        {
            var profile = new ServerProfileConfiguration("Default");

            RuleEdit.SetSpecialEnabled(profile, "Windows_Update", true);
            RuleEdit.SetSpecialEnabled(profile, "Windows_Update", true); // idempotent
            Assert.Single(profile.SpecialExceptions);

            RuleEdit.SetSpecialEnabled(profile, "Windows_Update", false);
            Assert.Empty(profile.SpecialExceptions);

            RuleEdit.SetSpecialEnabled(profile, "Windows_Update", false); // removing absent is a no-op
            Assert.Empty(profile.SpecialExceptions);
        }
    }
}
```

Note: `Flags` values in the dictionary are case-insensitive keys (`HasFlag` upper-cases the lookup), so the tests insert upper-case keys directly.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: FAIL to compile — `RuleListBuilder`/`RuleEdit`/`RuleGroup` do not exist.
- [ ] **Step 3: Write the Core implementation**

Create `SimpleDeFence.Core/RuleList.cs`:

```csharp
using SimpleDeFence.DatabaseClasses;
using SimpleDeFence.Localization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleDeFence
{
    /// Which section of the Rules screen a row belongs to.
    public enum RuleGroup
    {
        Applications,
        SpecialRecommended,
        SpecialOptional,
    }

    /// <summary>One row of the Rules list, already grouped and ready to render.</summary>
    public sealed class RuleRow
    {
        /// <summary>Set for application rules; null for special rows.</summary>
        public FirewallExceptionV3? Exception { get; init; }

        /// <summary>Set for special rows (the profile's string id); null for application rules.</summary>
        public string? SpecialId { get; init; }

        public RuleGroup Group { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string Policy { get; init; } = string.Empty;
        public bool IsBlocked { get; init; }

        /// <summary>Special rows only: whether the profile currently enables this exception.</summary>
        public bool Enabled { get; init; }
    }

    /// <summary>
    /// Pure transformations that turn a profile (+ the app database, for special-exception
    /// definitions) into the grouped, filtered Rules list. Shared so the mapping is unit-tested
    /// without a UI, mirroring ExceptionDescriptor/ConnectionActivity.
    /// </summary>
    public static class RuleListBuilder
    {
        public static IReadOnlyList<RuleRow> Build(ServerProfileConfiguration profile, AppDatabase db)
        {
            var rows = new List<RuleRow>();

            foreach (var ex in profile.AppExceptions)
            {
                var d = ExceptionDescriptor.Describe(ex);
                rows.Add(new RuleRow
                {
                    Exception = ex,
                    Group = RuleGroup.Applications,
                    Name = d.Name,
                    Kind = d.Kind,
                    Detail = d.Detail,
                    Policy = d.Policy,
                    IsBlocked = d.IsBlocked,
                    Enabled = true,
                });
            }

            foreach (var app in db.KnownApplications)
            {
                // Mirrors the WinForms settings UI: special, non-hidden definitions only, split
                // by the recommended flag.
                if (!app.HasFlag("TWUI:Special") || app.HasFlag("TWUI:Hidden"))
                    continue;

                rows.Add(new RuleRow
                {
                    SpecialId = app.Name,
                    Group = app.HasFlag("TWUI:Recommended") ? RuleGroup.SpecialRecommended : RuleGroup.SpecialOptional,
                    Name = app.Name.Replace('_', ' '),
                    Kind = Loc.T(LocKeys.Rules.SpecialKind),
                    Detail = string.Empty,
                    Policy = Loc.T(LocKeys.Rules.SpecialPolicy),
                    IsBlocked = false,
                    Enabled = profile.HasSpecialException(app.Name),
                });
            }

            return rows
                .OrderBy(r => r.Group)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<RuleRow> Filter(IReadOnlyList<RuleRow> rows, string term)
        {
            if (string.IsNullOrWhiteSpace(term))
                return rows;

            return rows.Where(r =>
                r.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || r.Detail.Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
    }

    /// <summary>Pure profile mutations behind the Rules screen's commits.</summary>
    public static class RuleEdit
    {
        /// <summary>
        /// Returns a copy of <paramref name="original"/> with the policy swapped, keeping the Id so
        /// ServerProfileConfiguration.AddExceptions replaces the old entry rather than adding a twin.
        /// RuleListPolicy is refused: the preset editor cannot represent custom rules, and silently
        /// discarding them would be a lie.
        /// </summary>
        public static FirewallExceptionV3 ApplyPreset(FirewallExceptionV3 original, ExceptionPolicy newPolicy)
        {
            if (original.Policy.PolicyType == PolicyType.RuleList)
                throw new InvalidOperationException("Preset editing cannot represent a custom rule list.");

            return new FirewallExceptionV3(original.Subject, newPolicy)
            {
                Id = original.Id,
                CreationDate = original.CreationDate,
                Timer = original.Timer,
                ChildProcessesInherit = original.ChildProcessesInherit,
            };
        }

        public static void RemoveExceptions(ServerProfileConfiguration profile, IReadOnlyCollection<Guid> ids)
        {
            profile.AppExceptions.RemoveAll(ex => ids.Contains(ex.Id));
        }

        public static void SetSpecialEnabled(ServerProfileConfiguration profile, string id, bool enabled)
        {
            var present = profile.HasSpecialException(id);
            if (enabled && !present)
                profile.SpecialExceptions.Add(id);
            else if (!enabled && present)
                profile.SpecialExceptions.RemoveAll(s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase));
        }
    }
}
```

New loc keys used here: `rules.specialKind` ("Special exception") and `rules.specialPolicy` ("Predefined") — add them to `LocKeys`, both JSON files, in Step 5.
New loc keys used here: `rules.specialKind` ("Special exception") and `rules.specialPolicy` ("Predefined") — added to `LocKeys` and both JSON files in Step 5.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj`
Expected: PASS — all tests green.

- [ ] **Step 5: Add the localization keys**

`LocKeys.cs`, new nested class:

```csharp
public static class Rules
{
    public const string SpecialKind = "rules.specialKind";
    public const string SpecialPolicy = "rules.specialPolicy";
}
```

`Strings.en.json`, new top-level section:

```json
"rules": {
  "specialKind": "Special exception",
  "specialPolicy": "Predefined"
},
```

`Strings.pt-BR.json`:

```json
"rules": {
  "specialKind": "Exceção especial",
  "specialPolicy": "Predefinida"
},
```

- [ ] **Step 6: Verify net48, then commit**

Core is glob-compiled into the net48 app; verify via Framework MSBuild (separate Restore/Build), then:

```bash
git add -A
git commit -m "Add Core rule-list grouping, filtering, and edit mutations"
```

## Task 3: Extend IFirewallClient with profile commits and picker data

**Files:**
- Modify: `SimpleDeFence.UI/Services/IFirewallClient.cs`, `FirewallClient.cs`, `SampleFirewallClient.cs`, `ConnectionsSnapshot.cs` (add the picker row types)
- [ ] **Step 2: Add the picker row types**

In `ConnectionsSnapshot.cs`, add:

```csharp
    /// <summary>One row of the process picker: a running process with its resolved path.</summary>
    internal sealed class ProcessListEntry
    {
        public uint ProcessId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
    }

    /// <summary>One row of the window picker: a visible top-level window and its process.</summary>
    internal sealed class WindowListEntry
    {
        public string Title { get; init; } = string.Empty;
        public uint ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string ProcessPath { get; init; } = string.Empty;
    }
```

- [ ] **Step 3: Implement the real client**

In `FirewallClient.cs`, reimplement `AllowAsync` as a wrapper and add the new members:

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
                    SerializationHelper.Serialize(Config));
                mutate(clone.ActiveProfile);

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

        public Task<AppDatabase?> GetAppDatabaseAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var path = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "SimpleDeFence", "profiles.json");
                    return System.IO.File.Exists(path) ? AppDatabase.Load(path) : null;
                }
                catch (Exception)
                {
                    // A missing/unreadable database is a normal state (service not installed,
                    // permissions), not an error - the Special group renders its empty state.
                    return (AppDatabase?)null;
                }
            });
        }

        public Task<IReadOnlyList<ProcessListEntry>> GetRunningProcessesAsync()
        {
            return Task.Run(() =>
            {
                var list = new List<ProcessListEntry>();
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    using (p)
                    {
                        var path = ResolvePath(unchecked((uint)p.Id));
                        // No path means we cannot build a rule for it - leave it out rather than
                        // offering a row that would commit a broken exception.
                        if (string.IsNullOrEmpty(path))
                            continue;

                        list.Add(new ProcessListEntry
                        {
                            ProcessId = unchecked((uint)p.Id),
                            Name = p.ProcessName,
                            Path = path,
                        });
                    }
                }

                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                return (IReadOnlyList<ProcessListEntry>)list;
            });
        }
```

**Interfaces:**
- Consumes: `Controller.SetServerConfig` (Core), `SerializationHelper` clone pattern (existing in `AllowAsync`)
- Produces: `Task<MessageType> CommitProfileChangesAsync(Action<ServerProfileConfiguration> mutate)`, `Task<AppDatabase?> GetAppDatabaseAsync()`, `Task<IReadOnlyList<ProcessListEntry>> GetRunningProcessesAsync()`; `AllowAsync` reimplemented on the commit path. (`GetTopLevelWindowsAsync` arrives in Task 7 together with the window-enumeration interop it depends on.)

Why: the Rules screen commits four kinds of mutation (add, edit, remove, special-toggle) and all of them are the same operation — clone the cached config, mutate the clone's active profile, PUT_SETTINGS, adopt the response. One method, one failure contract, one place where an unrecognised response is handled. `AllowAsync` becomes a thin wrapper so the Connections page is unaffected.

- [ ] **Step 1: Extend the interface**

In `IFirewallClient.cs`, add:

```csharp
        /// <summary>
        /// The one commit path: clone the cached config, mutate the clone's active profile, put
        /// it back. Only PUT_SETTINGS means the change took; anything else (locked, changeset
        /// conflict, unrecognised) is a failure the caller must show as one.
        /// </summary>
        Task<MessageType> CommitProfileChangesAsync(Action<ServerProfileConfiguration> mutate);

        /// <summary>The bundled app database (special-exception definitions), or null when the
        /// file is absent/unreadable - a missing database is a normal state, not an error.</summary>
        Task<AppDatabase?> GetAppDatabaseAsync();

        /// <summary>Running processes with resolved paths, for the process picker.</summary>
        Task<IReadOnlyList<ProcessListEntry>> GetRunningProcessesAsync();
```

Add `using System;` (already present) and `using SimpleDeFence.DatabaseClasses;`.


- [ ] **Step 4: Implement the sample client**

In `SampleFirewallClient.cs`, reimplement `AllowAsync` as a wrapper and add:

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

        public Task<AppDatabase?> GetAppDatabaseAsync()
        {
            // A small built-in set so the Special group is exercisable on sample data: one
            // recommended, one optional, one hidden (never rendered).
            var db = new AppDatabase(new List<Application>
            {
                MakeSpecial("Windows_Update", recommended: true),
                MakeSpecial("Gaming", recommended: false),
                MakeSpecial("Hidden_Service", recommended: true, hidden: true),
            });
            return Task.FromResult<AppDatabase?>(db);
        }

        private static Application MakeSpecial(string name, bool recommended, bool hidden = false)
        {
            var app = new Application { Name = name };
            app.Flags!["TWUI:SPECIAL"] = null;
            if (recommended) app.Flags["TWUI:RECOMMENDED"] = null;
            if (hidden) app.Flags["TWUI:HIDDEN"] = null;
            return app;
        }

        public Task<IReadOnlyList<ProcessListEntry>> GetRunningProcessesAsync()
        {
            IReadOnlyList<ProcessListEntry> list = new List<ProcessListEntry>
            {
                new() { ProcessId = 5150, Name = "firefox", Path = @"C:\Program Files\Mozilla Firefox\firefox.exe" },
                new() { ProcessId = 4242, Name = "tracker", Path = @"C:\Users\sample\AppData\Local\Telemetry\tracker.exe" },
            };
            return Task.FromResult(list);
        }
```

The sample profile already enables no special exceptions, so both visible sample specials render as available-to-enable; enable one during verification.

- [ ] **Step 5: Build and commit**

`dotnet build SimpleDeFence.UI` — the page does not consume the new members yet, so this proves the client surface compiles. Then:

```bash
git add -A
git commit -m "Add profile-commit path and picker data to the firewall client"
```

## Task 4: RulesPage — groups, search, multi-select, remove

**Files:**
- Create: `SimpleDeFence.UI/Pages/RulesPage.xaml`, `RulesPage.xaml.cs`
- Delete: `SimpleDeFence.UI/Pages/ApplicationsPage.xaml`, `ApplicationsPage.xaml.cs`
- Modify: `SimpleDeFence.UI/MainWindow.xaml.cs` (nav tag `rules` → `RulesPage`), `LocKeys.cs` + both JSONs

**Interfaces:**
- Consumes: `RuleListBuilder`/`RuleEdit` (Task 2), `IFirewallClient.CommitProfileChangesAsync`/`GetAppDatabaseAsync` (Task 3)
- Produces: the Rules screen minus the detail pane (Task 5) and the Add pickers (Tasks 6/7)

Layout: title, InfoBar, toolbar (filter box, Remove button with count, Refresh, busy ring), then a two-section scrolling surface matching the Connections page's visual language — an **Applications** Expander (multi-select ListView of app rules) and a **Special** Expander (two subsections, Recommended/Optional, rows with ToggleSwitches). The detail pane arrives in Task 5 beside the Applications list.

- [ ] **Step 1: Add the localization keys**

`LocKeys.cs`, extend the `Rules` class:

```csharp
public static class Rules
{
    public const string SpecialKind = "rules.specialKind";
    public const string SpecialPolicy = "rules.specialPolicy";

    public const string Title = "rules.title";
    public const string SectionApplications = "rules.section.applications";
    public const string SectionSpecialRecommended = "rules.section.specialRecommended";
    public const string SectionSpecialOptional = "rules.section.specialOptional";
    public const string FilterPlaceholder = "rules.filterPlaceholder";
    public const string EmptyApplications = "rules.empty.applications";
    public const string EmptySpecial = "rules.empty.special";
    public const string EmptyFiltered = "rules.empty.filtered";
    public const string Remove = "rules.remove";
    public const string RemoveConfirmTitle = "rules.removeConfirm.title";
    public const string RemoveConfirmBody = "rules.removeConfirm.body";
    public const string RemoveSuccessTitle = "rules.removeSuccess.title";
    public const string RemoveSuccessBody = "rules.removeSuccess.body";
    public const string RemoveFailedTitle = "rules.removeFailed.title";
    public const string RemoveFailedLockedDetail = "rules.removeFailed.lockedDetail";
    public const string RemoveFailedGenericDetail = "rules.removeFailed.genericDetail";
    public const string SpecialToggleFailedTitle = "rules.specialToggleFailed.title";
    public const string Add = "rules.add";
}
```

`Strings.en.json`, extend `rules`:

```json
"rules": {
  "specialKind": "Special exception",
  "specialPolicy": "Predefined",
  "title": "Rules",
  "section": {
    "applications": "Applications",
    "specialRecommended": "Special (recommended)",
    "specialOptional": "Special (optional)"
  },
  "filterPlaceholder": "Filter by name or path",
  "empty": {
    "applications": "No application exceptions yet.",
    "special": "No special exceptions available.",
    "filtered": "Nothing matches the current filter."
  },
  "remove": "Remove",
  "removeConfirm": {
    "title": "Remove exceptions?",
    "body": "Remove {0} exception(s)? The affected applications will be blocked again."
  },
  "removeSuccess": {
    "title": "Exceptions removed",
    "body": "{0} exception(s) were removed."
  },
  "removeFailed": {
    "title": "Could not remove the exceptions",
    "lockedDetail": "Unlock the configuration before removing exceptions.",
    "genericDetail": "The service returned {0}. The exceptions were not removed."
  },
  "specialToggleFailed": {
    "title": "Could not change the special exception"
  },
  "add": "Add"
},
```

`Strings.pt-BR.json`, extend `rules` with the matching Portuguese entries (specialKind/specialPolicy from Task 2; translate the rest to match).

- [ ] **Step 2: Write the page code-behind**

Create `SimpleDeFence.UI/Pages/RulesPage.xaml.cs`:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    /// <summary>One selectable application-rule row.</summary>
    public sealed class RuleListItem
    {
        public RuleRow Row { get; init; } = null!;
        public string Name => Row.Name;
        public string Kind => Row.Kind;
        public string Detail => Row.Detail;
        public string Policy => Row.Policy;
        public bool IsBlocked => Row.IsBlocked;
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

        public RulesPage()
        {
            InitializeComponent();
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
                ShowNotice(InfoBarSeverity.Error, Loc.T(LocKeys.Connections.GatherFailedTitle), ex.Message);
            }
            finally
            {
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
                item.ToggleRequested += async (_, enabled) => await ToggleSpecialAsync(item, enabled);
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

        private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRemoveButton();

        private void UpdateRemoveButton()
        {
            var count = AppsList.SelectedItems.Count;
            RemoveButton.IsEnabled = count > 0 && !_committing;
            RemoveButton.Content = count > 0
                ? Loc.T(LocKeys.Rules.Remove) + " " + Loc.T(LocKeys.Connections.SectionCount, count)
                : Loc.T(LocKeys.Rules.Remove);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = AppsList.SelectedItems.Cast<RuleListItem>().ToList();
            if (selected.Count == 0 || _committing)
                return;

            var confirm = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = Loc.T(LocKeys.Rules.RemoveConfirmTitle),
                Content = Loc.T(LocKeys.Rules.RemoveConfirmBody, selected.Count),
                PrimaryButtonText = Loc.T(LocKeys.Rules.Remove),
                CloseButtonText = Loc.T(LocKeys.Common.Cancel),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
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
                    LocKeys.Rules.RemoveFailedLockedDetail, LocKeys.Rules.RemoveFailedGenericDetail));
            }
        }

        private async Task ToggleSpecialAsync(SpecialListItem item, bool enabled)
        {
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
                    LocKeys.Rules.RemoveFailedLockedDetail, LocKeys.Rules.RemoveFailedGenericDetail));
            }
        }

        /// <summary>One serialized commit path; concurrent commits are refused, never raced.</summary>
        private async Task<MessageType> CommitAsync(Action<ServerProfileConfiguration> mutate)
        {
            _committing = true;
            UpdateRemoveButton();
            try
            {
                return await App.Firewall.CommitProfileChangesAsync(mutate);
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
            }
        }

        private static string FailureDetail(MessageType resp, string lockedKey, string genericKey) => resp switch
        {
            MessageType.RESPONSE_LOCKED => Loc.T(lockedKey),
            _ => Loc.T(genericKey, resp),
        };

        private async Task ShowResultAsync(string title, string body)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = body,
                CloseButtonText = Loc.T(LocKeys.Common.Ok),
            };

            try
            {
                await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                // Single-dialog-per-XamlRoot rule; fall back to the InfoBar rather than crash.
                ShowNotice(InfoBarSeverity.Informational, title, body);
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

- [ ] **Step 3: Write the page XAML**

Create `SimpleDeFence.UI/Pages/RulesPage.xaml`:

```xml
<Page
    x:Class="SimpleDeFence.UI.Pages.RulesPage"
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

        <TextBlock Grid.Row="0" Text="{loc:Loc Key=rules.title}" Style="{StaticResource TitleTextBlockStyle}"/>

        <InfoBar Grid.Row="1" x:Name="Notice" IsOpen="False" IsClosable="True"/>

        <Grid Grid.Row="2" ColumnSpacing="12">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBox Grid.Column="0" x:Name="FilterBox" PlaceholderText="{loc:Loc Key=rules.filterPlaceholder}"
                     TextChanged="FilterBox_TextChanged" MaxWidth="360" HorizontalAlignment="Left"/>
            <Button Grid.Column="1" x:Name="RemoveButton" Content="{loc:Loc Key=rules.remove}"
                    Click="RemoveButton_Click" IsEnabled="False"/>
            <Button Grid.Column="2" x:Name="RefreshButton" Content="{loc:Loc Key=common.refresh}" Click="RefreshButton_Click"/>
            <ProgressRing Grid.Column="3" x:Name="Busy" IsActive="False" Width="20" Height="20" VerticalAlignment="Center"/>
        </Grid>

        <ScrollViewer Grid.Row="3">
            <StackPanel Spacing="12">
                <Expander IsExpanded="True" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
                    <Expander.Header>
                        <TextBlock x:Name="AppsHeader" Style="{StaticResource SubtitleTextBlockStyle}"/>
                    </Expander.Header>
                    <StackPanel>
                        <TextBlock x:Name="AppsEmpty" Style="{StaticResource BodyTextBlockStyle}"
                                   Opacity="0.7" Margin="0,8" Visibility="Collapsed"/>
                        <ListView x:Name="AppsList" SelectionMode="Multiple"
                                  SelectionChanged="AppsList_SelectionChanged">
                            <ListView.ItemTemplate>
                                <DataTemplate x:DataType="local:RuleListItem">
                                    <Grid Padding="0,6" ColumnSpacing="14"
                                          Background="{x:Bind RowBackground}">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="220"/>
                                            <ColumnDefinition Width="110"/>
                                            <ColumnDefinition Width="*"/>
                                            <ColumnDefinition Width="Auto"/>
                                        </Grid.ColumnDefinitions>
                                        <TextBlock Grid.Column="0" Text="{x:Bind Name}" Style="{StaticResource BodyStrongTextBlockStyle}" TextTrimming="CharacterEllipsis"/>
                                        <TextBlock Grid.Column="1" Text="{x:Bind Kind}" Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.8"/>
                                        <TextBlock Grid.Column="2" Text="{x:Bind Detail}" Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.8" TextTrimming="CharacterEllipsis"/>
                                        <TextBlock Grid.Column="3" Text="{x:Bind Policy}" Style="{StaticResource CaptionTextBlockStyle}" Opacity="0.8"/>
                                    </Grid>
                                </DataTemplate>
                            </ListView.ItemTemplate>
                        </ListView>
                    </StackPanel>
                </Expander>
```

                <Expander IsExpanded="True" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">
                    <Expander.Header>
                        <TextBlock Style="{StaticResource SubtitleTextBlockStyle}" Text="{loc:Loc Key=rules.specialKind}"/>
                    </Expander.Header>
                    <StackPanel>
                        <TextBlock x:Name="SpecialEmpty" Style="{StaticResource BodyTextBlockStyle}"
                                   Opacity="0.7" Margin="0,8" Visibility="Collapsed"/>
                        <TextBlock x:Name="SpecialRecommendedHeader" Style="{StaticResource BodyStrongTextBlockStyle}" Margin="0,4"/>
                        <ListView x:Name="SpecialRecommendedList" SelectionMode="None">
                            <ListView.ItemTemplate>
                                <DataTemplate x:DataType="local:SpecialListItem">
                                    <Grid Padding="0,4" ColumnSpacing="14">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="*"/>
                                            <ColumnDefinition Width="Auto"/>
                                        </Grid.ColumnDefinitions>
                                        <TextBlock Grid.Column="0" Text="{x:Bind Name}" VerticalAlignment="Center"/>
                                        <ToggleSwitch Grid.Column="1" IsOn="{x:Bind IsOn, Mode=TwoWay}" OnContent="" OffContent=""/>
                                    </Grid>
                                </DataTemplate>
                            </ListView.ItemTemplate>
                        </ListView>
                        <TextBlock x:Name="SpecialOptionalHeader" Style="{StaticResource BodyStrongTextBlockStyle}" Margin="0,8,0,4"/>
                        <ListView x:Name="SpecialOptionalList" SelectionMode="None">
                            <ListView.ItemTemplate>
                                <DataTemplate x:DataType="local:SpecialListItem">
                                    <Grid Padding="0,4" ColumnSpacing="14">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="*"/>
                                            <ColumnDefinition Width="Auto"/>
                                        </Grid.ColumnDefinitions>
                                        <TextBlock Grid.Column="0" Text="{x:Bind Name}" VerticalAlignment="Center"/>
                                        <ToggleSwitch Grid.Column="1" IsOn="{x:Bind IsOn, Mode=TwoWay}" OnContent="" OffContent=""/>
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

Blocked rows reuse the old ApplicationsPage treatment: a `RowBackground` property on `RuleListItem` (the same `SolidColorBrush` tint `ExceptionRow.PolicyBackground` used, including the `global::Windows.UI` qualification comment), so no converter is needed:

```csharp
        public global::Microsoft.UI.Xaml.Media.Brush RowBackground => IsBlocked
            ? new global::Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(0x40, 0xE8, 0x11, 0x23))
            : new global::Microsoft.UI.Xaml.Media.SolidColorBrush(global::Microsoft.UI.Colors.Transparent);
```

A theme-aware blocked tint is the shell plan's already-deferred design pass; this deliberately matches the existing page until then.

- [ ] **Step 4: Rewire the nav and delete the old page**

In `MainWindow.xaml.cs`, change the `"rules"` switch arm from `typeof(ApplicationsPage)` to `typeof(RulesPage)`. Then:

```bash
git rm SimpleDeFence.UI/Pages/ApplicationsPage.xaml SimpleDeFence.UI/Pages/ApplicationsPage.xaml.cs
```

- [ ] **Step 5: Build**

Run: `dotnet build SimpleDeFence.UI/SimpleDeFence.UI.csproj -c Debug -v:minimal -nologo`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 6: Verify by running against sample data**

```bash
SimpleDeFence.UI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\SimpleDeFence.UI.exe --sample-data
```

Navigate to Rules. Confirm: Applications lists the sample exceptions (firefox.exe, git-remote-https.exe, the two svchost services, tracker.exe tinted blocked, the global UDP rule); Special shows "Windows Update" (recommended) and "Gaming" (optional), the hidden sample never renders; filtering by "fire" narrows the list; multi-select two app rows → Remove shows the count, confirm dialog commits, list refreshes without them; toggle "Gaming" on → refresh keeps it on. Then relaunch with `--sample-locked`: removal and toggling show the locked failure, never a success.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Replace Applications page with the Rules screen: groups, filter, multi-select remove, special toggles"
```

## Task 5: Detail pane with policy editing

**Files:**
- Modify: `SimpleDeFence.UI/Pages/RulesPage.xaml` / `.xaml.cs`, `LocKeys.cs` + both JSONs

**Interfaces:**
- Consumes: `RuleEdit.ApplyPreset` (Task 2), `CommitProfileChangesAsync` (Task 3)
- Produces: selecting an application row opens a detail pane for editing its policy

The Applications section gains a second column: list left (2*), detail pane right (*, min 320px), shown when exactly one application row is selected. The pane shows the subject (name, kind, detail) and a policy editor:

- Radio presets: **Blocked**, **Unrestricted**, **Unrestricted (LAN only)**, **TCP/UDP only** (four port fields: TCP out, UDP out, TCP in, UDP in, plus a LAN-only checkbox). Selecting a preset seeds the fields from the current policy.
- **Apply** commits `RuleEdit.ApplyPreset(selected, policy)` through `CommitAsync`; success refreshes (selection resets — the row is rebuilt), failure shows the honest dialog.
- A selected row whose policy is `RuleListPolicy` shows a read-only note instead of the editor ("Custom rule lists can't be edited here yet"), per scope decision 1.

New loc keys: `rules.detail.*` — `title` ("Rule"), `policyBlocked`/`policyUnrestricted`/`policyUnrestrictedLan`/`policyTcpUdp` labels, `tcpOut`/`udpOut`/`tcpIn`/`udpIn` field headers, `lanOnly` ("LAN only"), `apply` ("Apply"), `customRulesReadOnly` note, `applySuccess`/`applyFailed.*` (title/lockedDetail/genericDetail). Add to `LocKeys` + both JSONs.

- [ ] **Step 1: Add the detail-pane localization keys** (as listed above)
- [ ] **Step 2: Add the pane to the XAML** — wrap the Applications Expander in a two-column Grid; the right column is a `Border` (`x:Name="DetailPane"`, `Visibility="Collapsed"`) containing the subject header, the four radio buttons, the four port TextBoxes, the LAN checkbox, and Apply.
- [ ] **Step 3: Wire the code-behind** — `AppsList_SelectionChanged` shows/hides the pane and seeds it from the selected row's policy; `ApplyButton_Click` builds the chosen `ExceptionPolicy`, calls `RuleEdit.ApplyPreset`, commits via `CommitAsync`, reports honestly. A `RuleListPolicy` selection shows the read-only note and disables Apply.
- [ ] **Step 4: Build** — `dotnet build SimpleDeFence.UI` clean.
- [ ] **Step 5: Verify against sample data** — select firefox.exe, change Unrestricted→Blocked, Apply, confirm the row's policy text flips; select a RuleListPolicy row (add one to the sample config for this check) and confirm the read-only note appears and Apply is disabled.
- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add Rules detail pane with preset policy editing"
```

## Task 6: Add split button — executable and UWP pickers

**Files:**
- Modify: `SimpleDeFence.UI/Pages/RulesPage.xaml` / `.xaml.cs`, `SimpleDeFence.UI/App.xaml.cs` (expose the window handle), `LocKeys.cs` + both JSONs

**Interfaces:**
- Consumes: `UwpPackageList` (Core), `AllowAsync` (existing), `WinRT.Interop.WindowNative`/`InitializeWithWindow` (WinAppSDK)
- Produces: an **Add** SplitButton in the toolbar whose flyout offers the four pickers; this task wires executable + UWP, the other two arrive in Task 7

Details:

- `App.xaml.cs` exposes the window: `internal static Window? MainWindow { get; private set; }` set in `OnLaunched`. The pickers need it for `WinRT.Interop.WindowNative.GetWindowHandle`.
- The executable picker: `Windows.Storage.Pickers.FileOpenPicker` filtered to `.exe`, initialized with `WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd)`. A cancelled pick is not an error — no dialog, no notice.
- The UWP picker: a ContentDialog with a filter TextBox and a ListView of `new UwpPackageList()` entries (display name + family name); picking one commits `new AppContainerSubject(package)` via `AllowAsync` with the plan-standard `TcpUdpPolicy(true)`.
- Both commits report through the page's existing `ShowResultAsync`/`FailureDetail` with the `connections.allowSuccess.*` / `connections.allowFailed.*` keys (reuse — the operation is identical).
- New loc keys: `rules.addPickExecutable` ("Executable file..."), `rules.addPickProcess` ("Running process..."), `rules.addPickWindow` ("Window..."), `rules.addPickUwp` ("UWP app..."), `rules.pickUwpTitle` ("Choose a UWP app").

- [ ] **Step 1: Add the localization keys** (as listed above, both JSONs)
- [ ] **Step 2: Expose the window in `App.xaml.cs`** as described.
- [ ] **Step 3: Add the SplitButton to the toolbar XAML** — `Grid.Column="1"`, shifting Remove/Refresh right; flyout with executable + UWP items only (process/window items are omitted entirely until Task 7 adds them — the shell plan's "no inert entries" reasoning).
- [ ] **Step 4: Wire the two pickers in code-behind** as described.
- [ ] **Step 5: Build** — clean.
- [ ] **Step 6: Verify against sample data** — UWP picker lists packages on a real machine; picking one adds an AppContainer row. Executable picker: pick any .exe (e.g. notepad.exe), confirm it appears and the success dialog names it. Cancel both pickers once — nothing happens, no error shown.
- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add Rules split-button pickers: executable file and UWP package"
```

## Task 7: Running-process and window pickers

**Files:**
- Create: `SimpleDeFence.Windows/TopLevelWindows.cs`
- Modify: `SimpleDeFence.UI/Services/IFirewallClient.cs`, `FirewallClient.cs`, `SampleFirewallClient.cs`, `ConnectionsSnapshot.cs` (`WindowListEntry`), `SimpleDeFence.UI/Pages/RulesPage.xaml.cs`

**Interfaces:**
- Produces: `SimpleDeFence.Windows.TopLevelWindows.EnumerateVisible()` (net48+net10-safe P/Invoke), `IFirewallClient.GetTopLevelWindowsAsync()`, the two remaining flyout items

- [ ] **Step 1: Write the window enumeration**

Create `SimpleDeFence.Windows/TopLevelWindows.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleDeFence.Windows
{
    /// <summary>A visible top-level window with a non-empty title.</summary>
    public readonly struct WindowInfo
    {
        public WindowInfo(string title, uint processId)
        {
            Title = title;
            ProcessId = processId;
        }

        public string Title { get; }
        public uint ProcessId { get; }
    }

    /// <summary>EnumWindows interop for the window picker. Pure P/Invoke - net48 and net10 safe.</summary>
    public static class TopLevelWindows
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthW(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static List<WindowInfo> EnumerateVisible()
        {
            var result = new List<WindowInfo>();
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;

                int len = GetWindowTextLengthW(hWnd);
                if (len == 0)
                    return true;

                var sb = new StringBuilder(len + 1);
                GetWindowTextW(hWnd, sb, sb.Capacity);
                GetWindowThreadProcessId(hWnd, out var pid);
                result.Add(new WindowInfo(sb.ToString(), pid));
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }
}
```

- [ ] **Step 2: Add `GetTopLevelWindowsAsync` to the client surface** — the interface member, the real implementation (enumerate → resolve path per pid → drop pathless rows → sort by title; same shape as `GetRunningProcessesAsync`), and the sample implementation (two fake windows, e.g. "Mozilla Firefox" → firefox.exe). Add `WindowListEntry` to `ConnectionsSnapshot.cs`:

```csharp
    /// <summary>One row of the window picker: a visible top-level window and its process.</summary>
    internal sealed class WindowListEntry
    {
        public string Title { get; init; } = string.Empty;
        public uint ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string ProcessPath { get; init; } = string.Empty;
    }
```

- [ ] **Step 3: Wire the two dialogs** — a shared picker-dialog shape (filter box + ListView) like the UWP one. Process rows show name + path; window rows show title + process name. Both commit `new ExecutableSubject(path)` via `AllowAsync` with the standard policy. Same cancellation-is-not-an-error rule.

- [ ] **Step 4: Build** — UI clean; net48 app clean (the new Windows file is glob-compiled into it).

- [ ] **Step 5: Verify against sample data** — both dialogs list their sample rows; picking one adds the rule; filter narrows; cancel is silent. On a real desktop the window picker lists real windows.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add running-process and window pickers to the Rules screen"
```

## Done when

- `dotnet test SimpleDeFence.Tests/SimpleDeFence.Tests.csproj` passes (including the new `RuleListTests`, and `LocTests` with the new `rules.*` keys).
- The net48 WinForms app still builds after every task (the DatabaseClasses split compiles into it unchanged in behaviour; `SettingsForm`'s special-exception list still works).
- `SimpleDeFence.Windows` builds clean on both TFMs with the new interop.
- The app launches onto Connections by default; Rules shows the sample exceptions grouped Applications/Special; remove/edit/toggle/add all commit and report honestly; `--sample-locked` shows the locked failure on every mutation; the real client remains the default with no `--sample-data`.

## Next plans

1. **Settings** — `SettingsCard` groups (adds the `CommunityToolkit.WinUI.Controls.SettingsControls` dependency).
2. **Custom rule-list editor** — the `RuleListPolicy` editing surface the detail pane refuses today (scope decision 1).
3. **Smarter "Allow this app"** — port `AppDatabase.GetExceptionsForApp`-style known-app policy suggestions (now unblocked: the database lives in Core since Task 1).
4. **Disk auto-detect / add-folder / drag-and-drop** — the remaining WinForms-only add flows (scope decision 3).
