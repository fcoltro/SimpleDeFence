# WinUI 3 exe-merge and WinForms retirement — design

Date: 2026-08-15
Status: approved design, not yet implemented
Applies to: `SimpleDeFence` (WinForms exe), `SimpleDeFence.UI` (WinUI 3), `SimpleDeFence.Core`, `MsiSetup`

## Problem

ROADMAP.md's next net10-migration step, now that `SimpleDeFence.csproj` targets net10
(`net10-retarget`, merged 2026-08-15 at `831d7e3`): fold `SimpleDeFence.UI` (the WinUI 3 GUI) into
the same executable and port the remaining WinForms-only screens. `AuthAsServer`
(`PipeServerEndpoint.cs`) refuses IPC from any executable other than itself — a deliberate
anti-tampering control (decided 2026-08-08, reaffirmed here) — so a separate `SimpleDeFence.UI.exe`
can never drive the real service. Folding the GUI into `SimpleDeFence.exe` is the only option that
doesn't weaken that control.

This design started narrower (just the exe-merge mechanism) and grew, through brainstorming, to
cover the full remaining piece of Phase 2: the tray icon, DevelTool, and the update-checking /
settings-reconciliation gaps that block deleting WinForms outright. The three turned out to be
inseparable in practice — deleting the WinForms controller orphans `SettingsForm`, which is only
blocked on the same Updater/`ControllerSettings` gaps this design closes.

**End state, decided explicitly:** WinUI becomes the *only* GUI. Every WinForms screen this design's
own investigation found — controller, tray, DevelTool, and their now-redundant dialogs — gets
deleted once real-machine verification confirms parity, in the same implementation pass. No
transitional fallback flag.

## Decisions

### 1. Exe-merge mechanism: `ProjectReference` + bootstrap method, not merged XAML compilation

`SimpleDeFence.UI.csproj` keeps its current shape unchanged — `OutputType=WinExe`, `UseWinUI=true`,
its own `App`/`MainWindow`/pages. This deliberately **preserves its standalone
`SimpleDeFence.UI.exe --sample-data` dev workflow**, the same one that verified the Connections/
Rules/Settings ports so far (ROADMAP.md, Phase 2). .NET supports referencing a `WinExe`-output
project as a library from another project; this is an established, if less common, pattern.

`SimpleDeFence.csproj` adds a `ProjectReference` to `SimpleDeFence.UI.csproj` and a
`PackageReference` to `Microsoft.WindowsAppSDK` (needed only to call the WinUI bootstrap APIs) —
**not** `<UseWinUI>true</UseWinUI>` on itself, since it authors no XAML of its own. This avoids
combining WinForms' `.resx` designer pipeline and WinUI's XAML markup compiler inside one project —
an untested combination — by keeping XAML compilation entirely inside `SimpleDeFence.UI.csproj`.

`SimpleDeFence.UI.App` gains a small public bootstrap method (e.g. `RunAsControllerGui(string[]
args)`) that does what its currently-generated `Main` does today: set up a
`DispatcherQueueSynchronizationContext` and call `Microsoft.UI.Xaml.Application.Start(...)`,
constructing `App` from inside the callback. `SimpleDeFence/Program.cs`'s `StartController` calls
this method instead of `Application.Run(new SimpleDeFenceController(opts))`. `StartDevelTool`
similarly calls a new WinUI DevelTool bootstrap (see Decision 5).

Only one GUI framework's message loop ever runs per process launch — never both concurrently — so
there is no STA/threading conflict to resolve; Main's thread is STA either way, and whichever branch
is taken owns it exclusively until that GUI exits, matching `StartController`'s existing
`RestartOnQuit` loop shape.

**Open risk, not resolvable by reasoning alone:** whether `Application.Start` actually builds and
runs correctly when called from a hand-written `Main` in a project that isn't itself `UseWinUI=true`
and references a `WinExe`-output library project. This must be the first thing the implementation
plan verifies — a real `dotnet build` + launch — before any further work depends on it.

`AuthAsServer` needs **zero changes**: it still compares the same running exe's path to itself,
regardless of which GUI framework that exe just launched.

### 2. Approaches considered and rejected

- **Relaxing `AuthAsServer` to accept a signed companion exe.** Already decided against on
  2026-08-08 — widening the check weakens a real anti-tampering control in a firewall. Not
  reconsidered here.
- **XAML Islands** (embedding WinUI controls inside a WinForms window). Technically unblocked now
  that the exe is net10 (the earlier blocker was Windows App SDK requiring .NET 6+), but solves a
  different problem: incrementally mixing controls inside one window. `SimpleDeFence.UI` is already
  a complete, independent app shell (`NavigationView`, its own `Window`, `App.xaml`) — re-architecting
  it into embeddable islands would be strictly more work than Decision 1 for no benefit here.

### 3. Tray icon port

Rebuilt in `SimpleDeFence.UI` using `H.NotifyIcon.WinUI` (the actively-maintained community package;
Windows App SDK has no first-party tray-icon support as of 2026 — this was the recommendation
already on record in the net10-retarget design doc). Full parity surface, traced from
`SimpleDeFenceController.cs`:

- Dynamic mode icon (`firewall`/`shield_red_small`/`shield_yellow_small`/`shield_grey_small`/
  `shield_blue_small`, swapped on mode/connection-state change) and the traffic-rate tooltip text.
  The source `.ico` assets live under `SimpleDeFence/Resources/img/`; they need to become reachable
  from `SimpleDeFence.UI/Assets` too (copy or link — implementation plan's call).
- Context menu: mode switching (Normal/Block All/Allow Outgoing/Disabled/Learn), Manage, Connections,
  Lock, Elevate, Allow Local Subnet, Enable Hosts Blocklist (the only surviving piece of "hosts-file
  management" — the Lock-Hosts-File *setting* is already ported to WinUI's Settings page), the three
  "whitelist by X" quick-add flows (executable/process/window), and Quit.
- Mouse-click handling (`Tray_MouseClick`) and balloon-tip-click handling. The latter likely becomes
  Windows' modern toast-notification click-through instead of a literal balloon tip — `H.NotifyIcon`
  supports both; picking the right one is an implementation-time UX check, not a design-time
  decision.
- The tray's "Lock" action needs a password-unlock prompt. Rather than build this twice, it shares
  a single new WinUI unlock-dialog component with `SimpleDeFenceDoctor.Uninstall()`'s own unlock
  flow (currently `PasswordForm` — see Cross-cutting) — one component, two call sites.

### 4. Port the "Add folder" bulk-exception flow to Rules

`SettingsForm.cs`'s `btnAppAddFolder_Click` — pick a folder via `FolderBrowserDialog`, recursively
collect every `.exe`/`.dll` under it, and add each as an exception using the app database's default
recommendation (`AppDatabase.GetExceptionsForApp(subject, false, out _)`, deliberately no per-file
prompt since a folder can match dozens of files, e.g. a full game install directory) — has no WinUI
equivalent yet. Rules' own original port already flagged this as a known, deferred gap in
ROADMAP.md, alongside "disk auto-detect" and "drag-and-drop." Unlike those two, this design brings
it into scope now: `SettingsForm.cs` is the only place this logic lives, and it's already on this
design's own deletion list (Cross-cutting) — deleting it without porting this flow first would be a
real, silent feature loss for anyone who currently uses it (a folder-picker instead of adding
executables one at a time), not just a UI relocation.

Ported to WinUI's Rules page as a new option on its existing "Add" split button, alongside the four
pickers it already has (executable file, running process, window, UWP package). Uses a WinRT
`FolderPicker` (the same picker pattern DevelTool/Settings already use) in place of
`FolderBrowserDialog`; the recursive-collect helper (`CollectExeAndDllFiles` — pure file-system
recursion, no WinForms dependency today despite living in `SettingsForm.cs`) and the
`GetExceptionsForApp` call carry over unchanged.

**Still explicitly out of scope:** "disk auto-detect" and "drag-and-drop" — the other two flows
ROADMAP already deferred. This design only pulls in what it would otherwise delete out from under
users; it doesn't use this as an excuse to finish Rules' whole deferred list.

### 5. DevelTool port

Mechanical, not a redesign. `DevelToolForm.cs` is an internal, never-user-facing batch/build tool
(file-association DB builder, app-collection compiler, an update-package builder that hashes/signs
MSIs, a resx satellite-resource optimizer, batch code-signing via `signtool.exe`) — plain textboxes,
buttons, listboxes, and file/folder pickers. Every click handler already only calls existing backend
classes (`ExecutableSubject`, `DatabaseClasses.*`, `SerializationHelper`, `Hasher`,
`WinTrust.VerifyFileAuthenticode`, `Utils.CompressDeflate`, `Utils.StartProcess`) that stay
untouched. It becomes a WinUI window — a `Pivot` or similar tabbed layout mirroring today's sections
— reachable the same way, via `/develtool`. File/folder pickers become `FileOpenPicker`/
`FolderPicker`, the same WinRT picker pattern Settings' Import/Export already proved working
end-to-end on a real desktop (2026-08-13).

### 6. Updater / update-checking port

`SimpleDeFence/UpdateChecker.cs` splits along its existing internal seam:

- `UpdateChecker.GetDescriptor()` (fetches and validates the update descriptor JSON) is already
  framework-agnostic except for one `Application.ProductVersion` read (swappable for an
  assembly-version read that works from Core) and its dependency on `UpdateDescriptor`/
  `UpdateModule`, which already live in `SimpleDeFence.Core/ServerState.cs`. This half moves to
  `SimpleDeFence.Core`, closing the ROADMAP item ("porting `Updater`/`UpdateChecker` to Core").
- `Updater` (the interactive progress/prompt/download flow) is genuinely WinForms-coupled —
  `Microsoft.Samples.TaskDialog`, `System.Net.WebClient` (already flagged obsolete, `SYSLIB0014`),
  and a `Thread`/`.Interrupt()`/`.Abort()` cancellation pattern. **`Thread.Abort()` on net10 throws
  `PlatformNotSupportedException` at runtime** — it still compiles (confirmed: this file built clean
  in the net10-retarget verification), but the cancel path has never actually been exercised, so this
  is a real latent bug, not a hypothetical one. The WinUI port rebuilds this as a `ContentDialog`-based
  flow using `HttpClient` and proper `CancellationToken`-based async/await — both fixing the dormant
  bug and modernizing the transport in the same change, since the rewrite touches every line anyway.
  This is wired to the WinUI Settings page's still-missing "Check for updates now" action.

### 7. `ControllerSettings` / `ClientSettings` reconciliation

Simpler than the ROADMAP wording implies. `ClientSettings` (`SimpleDeFence.Core/ClientSettings.cs`)
already documents this exact deferral in its own header comment. Inspecting `ControllerSettings`
(`SimpleDeFence/Settings.cs`) shows most of its fields are WinForms window-geometry bookkeeping
(`FormWindowState`/`Point`/`Size`/column-widths) for five dialogs this design deletes
(Connections/Processes/Services/UwpPackages/Settings forms) — once those forms are gone, that
bookkeeping is meaningless, not just unported.

**No migration path is needed.** This repo has shipped no release since the fork (per the
net10-retarget design doc), so there is no installed base with an existing `ControllerConfig` file
to preserve. `ClientSettings` simply gains the three fields that are real behavior, not window
geometry — `Language`, `AskForExceptionDetails`, `EnableGlobalHotkeys` — and `ControllerSettings`
plus `ConfigContainer.Controller` are deleted outright alongside the WinForms code that used them.
`PasswordLock` (`Settings.cs`) is unaffected — it's already pure file-based hash storage with no
WinForms dependency.

## Architecture

**`SimpleDeFence.UI`:**
- `App` gains `RunAsControllerGui(string[] args)` (Decision 1) and an equivalent DevelTool bootstrap.
- New tray-icon component (`H.NotifyIcon.WinUI`-based) wired into `App`/`MainWindow`'s lifecycle,
  replacing what `SimpleDeFenceController`'s `Tray`/`TrayMenu` fields do today (Decision 3).
- New DevelTool window/pages under `Pages/` (or a dedicated folder), reusing existing backend classes
  unchanged (Decision 5).
- `RulesPage`'s existing "Add" split button gains a folder-based bulk-add option, reusing
  `CollectExeAndDllFiles`/`AppDatabase.GetExceptionsForApp` unchanged (Decision 4).
- New shared password-unlock `ContentDialog` component, used by both the tray's "Lock" action and
  `SimpleDeFenceDoctor.Uninstall()`'s unlock-before-stop flow (Decision 3 note, Cross-cutting below).
- `SettingsPage` gains the "Check for updates now" action, calling the new WinUI `Updater` (Decision 6).

**`SimpleDeFence.Core`:**
- Gains the descriptor-fetching half of `UpdateChecker` (Decision 6).
- `ClientSettings` gains `Language`, `AskForExceptionDetails`, `EnableGlobalHotkeys` (Decision 7).

**`SimpleDeFence` (the exe):**
- `Program.cs`: `StartController`/`StartDevelTool` call the new `SimpleDeFence.UI` bootstrap methods
  instead of `Application.Run(new SimpleDeFenceController(...))` / `new DevelToolForm()`.
- `SimpleDeFenceDoctor.Uninstall()`'s `PasswordForm` usage is replaced by the shared WinUI
  unlock-dialog component (Decision 3 note).
- `SimpleDeFence.csproj` gains the `ProjectReference` + `Microsoft.WindowsAppSDK` package (Decision 1).

## Cross-cutting: WinForms retirement surface

Confirmed by cross-reference (`grep` for `new <Form>(` across `SimpleDeFence/`): every one of the
following is only ever instantiated from `SimpleDeFenceController`, `SettingsForm`,
`SimpleDeFenceDoctor`, or each other. Once the three parents are replaced (Decisions 1, 3, 4, 5, 6),
all of the following become genuinely unreachable and are deleted in the same implementation pass,
together with their `.Designer.cs`/`.resx` families:

`SimpleDeFenceController.cs`, `SettingsForm.cs`, `DevelToolForm.cs`, `ApplicationExceptionForm.cs`,
`AppFinderForm.cs`, `PasswordForm.cs`, `ConnectionsForm.cs` (the legacy pre-WinUI version — distinct
from `SimpleDeFence.UI/Pages/ConnectionsPage.xaml`), `Processes.cs`, `Services.cs`,
`UwpPackagesForm.cs`, plus `SimpleDeFence/Settings.cs`'s `ControllerSettings` class and
`ConfigContainer.Controller`.

Nothing about the IPC protocol, WFP rule construction, the service's firewall behavior, or
`AuthAsServer` changes anywhere in this design — every decision is either a GUI-framework
substitution that preserves existing behavior (tray, DevelTool), a genuine but narrowly-scoped bug
fix bundled into a rewrite that was already touching those lines (`Thread.Abort`), or a deletion of
code made unreachable by the substitutions.

## Error handling

Every port preserves existing behavior and error handling exactly: `TaskDialog` error/prompt dialogs
become `ContentDialog` equivalents with the same messages and the same decision points;
`WebClient`'s exception handling becomes `HttpClient`'s with the same user-facing outcomes. The one
deliberate behavior *fix* (`Thread.Abort` → proper cancellation) is called out explicitly rather than
folded in silently, matching this repo's convention of flagging scope expansions rather than hiding
them in an otherwise-mechanical port.

## Testing

- The existing `SimpleDeFence.Tests` suite must stay green throughout — matching every prior phase's
  bar.
- Tray icon, DevelTool, and Updater flows are not meaningfully unit-testable (real UI interaction,
  real HTTP calls, a real tray icon) and need real-machine verification before their WinForms
  equivalents are deleted — the same standard every prior WinUI phase (Connections, Rules, Settings)
  already used.
- The exe-merge mechanism's core build viability (Decision 1's open risk) is verified first, before
  any tray/DevelTool/Updater work depends on it succeeding.
- Regression surface: nothing in the IPC/WFP/service layers is touched, so a green suite plus
  successful net10 builds are necessary but not sufficient — real-machine GUI verification is what
  actually proves this design, as with every WinUI phase before it.

## Out of scope

- **The default-GUI flip's own staging was decided, not left open:** there is no transitional
  fallback flag. WinForms is deleted in the same implementation pass once real-machine verification
  passes, not in a later cleanup.
- **Any further WinUI screen redesign or new functionality.** This design is a like-for-like port of
  existing WinForms functionality onto WinUI, not a UX redesign — mirroring how Connections, Rules,
  and Settings were each themselves like-for-like ports before any new functionality was considered.
- **x86/arm64 self-contained publishing.** Already an explicit follow-up from the net10-retarget
  design; unaffected by this one.
