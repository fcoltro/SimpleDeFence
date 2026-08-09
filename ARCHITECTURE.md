# Architecture Notes

Structural review of the forked codebase (C#/.NET Framework 4.8), done with the code knowledge
graph on 2026-07-20 at commit `6795bfa`. Kept as a reference for sequencing the work in
[ROADMAP.md](ROADMAP.md) — the functional rename and the WinUI 3 GUI migration both benefit from
knowing where the seams and risks already are.

## Structural picture

Two clean, well-scoped modules and one big blob:

- `SimpleDeFence.Windows.Services` (`ServiceBase`, 105 nodes, cohesion 0.50) — Windows service lifecycle,
  cleanly separated.
- `SimpleDeFence.Windows.WFP` + `SimpleDeFence.Windows` (rule/filter primitives, path/handle utilities, ~590
  nodes combined) — reasonably cohesive native-interop layer.
- The main app (`SimpleDeFence/`) is one 659-node community with only 0.31 cohesion — nearly everything
  user-facing (forms, controller, service, dark-mode theming) is tangled together rather than split
  into cohesive subsystems.

## God classes

Candidates for decomposition before/during the rewrite:

| Class | Lines | Concern mix |
|---|---|---|
| `SimpleDeFenceServer` (SimpleDeFence/SimpleDeFenceService.cs:19) | 1948 | WFP rule construction + IPC message handling + service lifecycle, all in one class |
| `SimpleDeFenceController` (SimpleDeFence/SimpleDeFenceController.cs:15) | 1367 | UI orchestration + business logic (talks to the server, drives every form) |
| `DarkModeCS` (SimpleDeFence/DarkModeCS.cs:21) | 1233 | Vendored third-party theming lib — not worth refactoring, WinUI 3's built-in dark mode replaces it in Phase 2 |
| `SettingsForm` + Designer (SimpleDeFence/SettingsForm.cs:15, SimpleDeFence/SettingsForm.Designer.cs:3) | 682 + 676 | Typical WinForms code-behind bulk |

Standout hotspots inside `SimpleDeFenceServer`:
- `ConstructFilter` (SimpleDeFence/SimpleDeFenceService.cs:71) — 86 outgoing edges, touches nearly everything.
- `AssembleActiveRules` (SimpleDeFence/SimpleDeFenceService.cs:71) — 221 lines.
- `PathMapper.ConvertPath` (SimpleDeFence.Windows/PathMapper.cs:335) — 161 lines, 55 outgoing edges; the
  `%VarName%`-style path-variable resolver used throughout rule matching.

## Test coverage — the real risk

20 high-degree hotspot nodes have zero test coverage, including the entire core: `ConstructFilter`,
`TwMessage` (the IPC message dispatcher), `PathMapper.ConvertPath`, `ServiceBase`,
`SimpleDeFenceController`. Only `PathMapper` has any tests at all (`TestConversion`).

This matters for the roadmap: **renaming internals and porting to Rust both need a regression
safety net that doesn't currently exist.** Before either of those, the highest-leverage move is
adding characterization tests around `ConstructFilter`/`AssembleActiveRules` and IPC message
handling.

**Update (2026-08-08):** a `SimpleDeFence.Tests` project now exists (xunit, net10, covering
`SimpleDeFence.Core`) and runs in CI. It currently only covers `ExceptionDescriptor` — the
shared rule-description logic both GUIs render entries from. The core hotspots named above are
still untested; the project is now there to put those tests in.

## The IPC boundary is authenticated (found 2026-08-08)

`PipeServerEndpoint.AuthAsServer` (SimpleDeFence/PipeServerEndpoint.cs:91) resolves the connecting
client's PID to an executable path and requires it to equal `ProcessManager.ExecutablePath` — the
service's own running image. Anything else is refused, and `Controller` surfaces that as
`COM_ERROR`.

This makes the core-vs-GUI seam below a **process-internal** seam, not a process boundary. Any GUI
must live in the same executable as the service, or the check has to change. The WinForms GUI
satisfies it only because `SimpleDeFence.exe` runs as both service and controller depending on its
command line.

Two things make this easy to miss: it is compiled out under `#if !DEBUG`, so development builds
accept any client, and nothing fails until a Release install is exercised against a real service.

## Architectural seams for the WinUI 3 migration (Phase 2)

From bridge-node/chokepoint analysis, a core-vs-GUI split lines up naturally with the existing
structure — this stays true regardless of GUI framework, since the core (C#, untouched) and the GUI
(migrating to WinUI 3) were already cleanly separable:

- **Core (stays C#, untouched):** rule construction/install (`ConstructFilter`, `InstallFirewallRules`),
  IPC protocol, `ServiceBase` lifecycle, `PathMapper`.
- **GUI-only (migrates to WinUI 3):** `SimpleDeFenceController`, all `*Form` classes, `DarkModeCS` (WinUI 3
  has first-class dark mode support, so this vendored theming lib goes away entirely rather than
  needing a replacement).

## Noise, not signal

The ~50 "isolated nodes" found by the graph are mostly vendored P/Invoke structs (`Privilege.cs`,
`TaskDialog.cs`) and WinForms event handlers only ever called by the framework — not real dead
code, safe to ignore.
