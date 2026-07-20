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
| `TinyWallServer` (SimpleDeFence/TinyWallService.cs:19) | 1948 | WFP rule construction + IPC message handling + service lifecycle, all in one class |
| `TinyWallController` (SimpleDeFence/TinyWallController.cs:15) | 1367 | UI orchestration + business logic (talks to the server, drives every form) |
| `DarkModeCS` (SimpleDeFence/DarkModeCS.cs:21) | 1233 | Vendored third-party theming lib — not worth refactoring, WinUI 3's built-in dark mode replaces it in Phase 2 |
| `SettingsForm` + Designer (SimpleDeFence/SettingsForm.cs:15, SimpleDeFence/SettingsForm.Designer.cs:3) | 682 + 676 | Typical WinForms code-behind bulk |

Standout hotspots inside `TinyWallServer`:
- `ConstructFilter` (SimpleDeFence/TinyWallService.cs:71) — 86 outgoing edges, touches nearly everything.
- `AssembleActiveRules` (SimpleDeFence/TinyWallService.cs:71) — 221 lines.
- `PathMapper.ConvertPath` (SimpleDeFence.Windows/PathMapper.cs:335) — 161 lines, 55 outgoing edges; the
  `%VarName%`-style path-variable resolver used throughout rule matching.

## Test coverage — the real risk

20 high-degree hotspot nodes have zero test coverage, including the entire core: `ConstructFilter`,
`TwMessage` (the IPC message dispatcher), `PathMapper.ConvertPath`, `ServiceBase`,
`TinyWallController`. Only `PathMapper` has any tests at all (`TestConversion`).

This matters for the roadmap: **renaming internals and porting to Rust both need a regression
safety net that doesn't currently exist.** Before either of those, the highest-leverage move is
adding characterization tests around `ConstructFilter`/`AssembleActiveRules` and IPC message
handling.

## Architectural seams for the WinUI 3 migration (Phase 2)

From bridge-node/chokepoint analysis, a core-vs-GUI split lines up naturally with the existing
structure — this stays true regardless of GUI framework, since the core (C#, untouched) and the GUI
(migrating to WinUI 3) were already cleanly separable:

- **Core (stays C#, untouched):** rule construction/install (`ConstructFilter`, `InstallFirewallRules`),
  IPC protocol, `ServiceBase` lifecycle, `PathMapper`.
- **GUI-only (migrates to WinUI 3):** `TinyWallController`, all `*Form` classes, `DarkModeCS` (WinUI 3
  has first-class dark mode support, so this vendored theming lib goes away entirely rather than
  needing a replacement).

## Noise, not signal

The ~50 "isolated nodes" found by the graph are mostly vendored P/Invoke structs (`Privilege.cs`,
`TaskDialog.cs`) and WinForms event handlers only ever called by the framework — not real dead
code, safe to ignore.
