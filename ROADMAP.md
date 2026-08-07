# Roadmap

SimpleDeFence is a fork of [TinyWall](https://github.com/pylorak/TinyWall). See [NOTICE.md](NOTICE.md) for
attribution. This document tracks where the project is headed.

## Phase 0 — Fork baseline (done)

- [x] Fork TinyWall source into this repository, keeping full upstream history for reference.
- [x] Public repo at [fcoltro/SimpleDeFence](https://github.com/fcoltro/SimpleDeFence).
- [x] Cosmetic rebrand (README, docs).
- [x] Full functional rename (service name, `%ProgramData%` folder, WFP rule grouping tag,
      `DataContract` namespaces, C# namespace, project/solution/exe names, installer/MSI product
      name + registry key + regenerated GUIDs, named pipe/mutex/self-update identifiers,
      tray/about strings). Class names (`TinyWallController`, `TinyWallService`, `TinyWallDoctor`,
      etc.) and resx resource *keys* were deliberately left as-is — internal identifiers only, no
      user-facing or collision-relevant meaning, renaming them would balloon the diff for no
      benefit. The bundled installer FAQ (`MsiSetup/Sources/ProgramFiles/SimpleDeFence/doc/faq.html`)
      still reads as TinyWall prose with real links to the upstream project/site — needs an
      editorial pass, not a mechanical one, so it's left as a follow-up.

## Phase 1 — Code structure review & incremental features (C#/.NET, current stack)

- [x] Code structure review of the existing C#/.NET codebase — see [ARCHITECTURE.md](ARCHITECTURE.md).
- [x] "Add folder" exception: recursively scan a folder for `.exe`/`.dll` and add them all as
      application exceptions in one action.
- [x] Investigate isolating Windows Update traffic as a distinct, controllable exception. **Finding:**
      the WFP engine already supports genuine per-service isolation (`ServiceNameFilterCondition` in
      `SimpleDeFence.Windows.WFP/FilterCondition.cs` derives the real Windows service SID and conditions
      on `FWPM_CONDITION_ALE_USER_ID` — this is not a gap, it already works). The actual bug was in the
      data: the bundled `Windows_Update` app-database entry only scoped this to `wuauserv`, which is
      **stopped** on modern Windows (verified on this machine) — `UsoSvc` (Update Orchestrator, the real
      driver since Windows 10) and `DoSvc` (Delivery Optimization, handles payload downloads) do the
      actual work now and weren't covered. To compensate, the entry fell back to an *unscoped*
      `svchost.exe` rule restricted only to ports 443/80 — which grants every other service sharing
      `svchost.exe` the same access, defeating the isolation goal entirely.
      Fixed by adding precise `ServiceSubject` components for `UsoSvc` and `DoSvc` and removing the
      broad fallback, in both `SimpleDeFence/Database/SpecialApplications/Special Windows Update.json`
      (source) and `MsiSetup/Sources/CommonAppData/SimpleDeFence/profiles.json` (the compiled file an
      actual install loads — normally regenerated from the source via `/develtool`'s Database Creator,
      patched directly here since that tool needs a built Windows app to run).
      **Known limitation, not fixed:** BITS is also used by Windows Update on some paths, but it's
      generic shared infrastructure used by many unrelated callers (Store, third-party installers) —
      WFP can't distinguish a BITS job made on Windows Update's behalf from any other caller's, so
      adding it here would reopen the same "grants access to everything else too" problem. Also not
      covered: Delivery Optimization's peer-to-peer *inbound* listening (default port 7680) if a user
      has LAN/Internet peer caching enabled beyond the default — this fix only covers `DoSvc`'s
      outbound connections.
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

## Phase 2 — GUI modernization: WinUI 3 (Windows App SDK) over the existing C# core

**Decision (2026-07-20, revised same day):** not Rust/Tauri, not a core rewrite. First cut of this
phase planned a Tauri + Tailwind CSS GUI talking to the unchanged C# service over IPC. Reconsidered
because this app is Windows-only — WFP has no cross-platform equivalent, so Tauri's entire value
proposition (one core, many OS targets) doesn't apply here. Worse, since the plan already kept the
WFP core in C#, Tauri's Rust side would've done no real work — just a second language/toolchain
(Rust + Node/npm) reimplementing the existing IPC protocol (`Protocol.cs`) to talk to a webview.

WinUI 3 (Windows App SDK) is the better fit: it's Microsoft's committed native flagship for Windows
desktop apps (recommitted at Build 2026, no competing framework planned, 3-year LTSC support
available), stays entirely in C#/.NET like the rest of the app (no IPC-language boundary — can share
the existing DTOs/`PipeClientEndpoint` code directly), and renders genuinely native Fluent 2/Mica UI
rather than a webview wrapper (the pattern Microsoft is currently steering developers *away* from).
Microsoft also ships an officially-supported incremental migration path (XAML Islands, hosting WinUI 3
controls inside an existing WinForms app) plus AI-assisted migration tooling in VS 2026 — a good match
for the strangler-fig approach this phase already needs.

The core (WFP rule construction, IPC, service lifecycle) still has zero test coverage today (see
[ARCHITECTURE.md](ARCHITECTURE.md)) — rewriting the least-tested, highest-consequence part of a
firewall into a new language first is the wrong order of operations regardless of GUI framework, so
the core stays C#/.NET and untouched throughout this phase either way.

- [x] Extract `SimpleDeFence.Core` — the protocol/IPC/config-model classes ARCHITECTURE.md identified
      as the core-vs-GUI seam, moved out of `SimpleDeFence/` into their own project. It multi-targets
      `net48;net10.0-windows` so the existing WinForms app and the new WinUI 3 app can both consume
      it (`SimpleDeFence.Utilities` and `SimpleDeFence.Windows` were multi-targeted to match). The
      WinForms app still glob-compiles these sources rather than referencing the assembly, keeping
      the existing "statically compiled into the application" build model intact.
- [x] Stand up a WinUI 3 shell that talks to the **existing, unchanged C# service** — same process
      family, same language, can reuse `Protocol.cs`/`PipeServerEndpoint`/`PipeClientEndpoint` as-is.
      `SimpleDeFence.UI` builds and its first screen calls `Controller.GetServerConfig` over the
      existing `SimpleDeFenceController` named pipe to show connection/mode/lock state.
      **Not yet verified at runtime against a live service** — compile-verified only.
- [ ] Use XAML Islands (or equivalent interop) to migrate screen-by-screen, not big-bang — keep
      WinForms forms working and buildable throughout until each WinUI 3 replacement reaches parity.
- [ ] Investigate VS 2026's Copilot-assisted "Modernize" tooling for the WinForms → WinUI 3 migration
      itself, since Microsoft built it for exactly this transition.
- [ ] Port feature-by-feature from the WinForms GUI, validating parity before removing each old form.

## Development environment

- `SimpleDeFence.Core`, `SimpleDeFence.UI`, `SimpleDeFence.Utilities` and `SimpleDeFence.Windows`
  build with a plain `dotnet build` — no Visual Studio needed.
- The **WinForms app (`SimpleDeFence.csproj`) does not**. It carries two `<COMReference>` entries
  (`NetFwTypeLib`, `TaskScheduler`) whose `ResolveComReference` task requires .NET Framework MSBuild
  plus the NETFX SDK tools (`TlbImp.exe`/`AxImp.exe`). `dotnet build` fails it with `MSB4803`, and
  Framework MSBuild without those tools fails with `MSB3091`. Building it locally needs the
  ".NET Framework 4.8 SDK" / "Windows SDK — .NET Framework tools" component installed. CI's Windows
  runners already have it, so `.github/workflows/build.yml` is unaffected.
- The WinUI 3 work in Phase 2 stays in the same .NET/Windows toolchain — no new dev environment
  needed beyond a current Windows App SDK workload.

## Backlog inherited from upstream

TinyWall's own maintainer kept a backlog in `future-ideas.txt`. Items not already listed above are
fair game to pull into a future phase.
