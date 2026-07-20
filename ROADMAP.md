# Roadmap

SimpleDeFence is a fork of [TinyWall](https://github.com/pylorak/TinyWall). See [NOTICE.md](NOTICE.md) for
attribution. This document tracks where the project is headed.

## Phase 0 — Fork baseline (done)

- [x] Fork TinyWall source into this repository, keeping full upstream history for reference.
- [x] Public repo at [fcoltro/SimpleDeFence](https://github.com/fcoltro/SimpleDeFence).
- [x] Cosmetic rebrand (README, docs) — the app itself still identifies internally as TinyWall
      (service name, data folder, WFP rule grouping, .NET namespaces). That is a functional
      rename, not a cosmetic one, and is tracked separately below so it doesn't get done half-way.

## Phase 1 — Code structure review & incremental features (C#/.NET, current stack)

- [ ] Code structure review of the existing C#/.NET codebase.
- [ ] Full functional rename (service name, `%ProgramData%` folder, WFP rule grouping tag,
      `DataContract` namespaces, installer/MSI product name, tray/about strings). Needs care:
      the WFP rule grouping and service name are used to identify and clean up the app's own
      firewall rules — a half-done rename risks orphaned rules or duplicate services.
- [ ] "Add folder" exception: recursively scan a folder for `.exe`/`.dll` and add them all as
      application exceptions in one action.
- [ ] Investigate isolating Windows Update traffic as a distinct, controllable exception.
- [ ] Dark-theme GUI support.
- [ ] Better hosts file handling.
- [ ] Unified dialog boxes (consolidate the various ad-hoc dialogs into a consistent pattern).
- [ ] WHOIS query for remote addresses in the Connections window.
- [ ] Checkboxes in the mode menu.
- [ ] Connections window: auto-refresh checkbox.
- [ ] Connections window: always-on-top checkbox.
- [ ] VirusTotal integration for scanning/checking flagged applications.
- [ ] IP blocklist support.
- [ ] Migrate local storage to SQLite.
- [ ] Windows Action Center integration.

## Phase 2 — Modernization: Rust core + Tauri/Tailwind GUI

Full rewrite, targeting eventual replacement of the C#/.NET app:

- [ ] Design the Rust core (service/engine) architecture — WFP bindings, rule engine, config
      storage — informed by the Phase 1 code structure review.
- [ ] Stand up a Tauri + Tailwind CSS GUI shell talking to the Rust core.
- [ ] Port feature-by-feature from the C# app, validating parity before removing the old code path.
- [ ] Keep the C#/.NET app buildable and working throughout, until the Rust/Tauri app reaches
      parity — this is a strangler-fig migration, not a big-bang rewrite.

## Development environment

- [Dockerfile](Dockerfile) provides a containerized build of the current .NET Framework 4.8 app
  (requires Docker Desktop in Windows container mode, since net48 doesn't run on Linux containers).
- The Rust/Tauri stack in Phase 2 will get its own (cross-platform-friendly) Docker setup once that
  work starts.

## Backlog inherited from upstream

TinyWall's own maintainer kept a backlog in `future-ideas.txt`. Items not already listed above are
fair game to pull into a future phase.
