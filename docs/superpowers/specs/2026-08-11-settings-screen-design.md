# Settings screen — design

Date: 2026-08-11
Status: approved design, not yet implemented
Applies to: `SimpleDeFence.UI`, `SimpleDeFence.Core`, `SimpleDeFence` (WinForms, for the shared Core split)

## Problem

Settings is the third and last destination named in `docs/superpowers/specs/2026-08-08-winui-gui-modernization-design.md`'s information architecture. Connections and Rules are both shipped; Settings has no implementation plan yet, and no code.

The existing WinForms `SettingsForm` (732 lines of code-behind, four tabs) is the source of truth for what needs porting. Two of its four tabs — the full application-exceptions list editor and the special-exceptions checklist — are now entirely redundant: the Rules screen already covers both, better (grouping, search, a detail pane, multi-select removal). Nothing there needs porting; this design only concerns the remaining, genuinely new surface: General preferences, per-profile protection toggles, blocklists, the password/lock mechanism, update checking, and import/export.

## Decisions

1. **Full 7-group scope in one plan**, matching the Connections and Rules precedent (each shipped as a single plan). Each of the design doc's named groups — General, Protection, Blocklists, Security, Updates, Maintenance, About — is individually small once the two redundant tabs are dropped.
2. **Drop three WinForms General-tab settings that have no real WinUI behavior yet, rather than ship inert toggles**: `Language` (the WinUI GUI has no localized strings — explicitly out of scope in the modernization design doc), `EnableGlobalHotkeys` (no global-hotkey capture exists anywhere in `SimpleDeFence.UI`), and `AskForExceptionDetails` (controlled a per-add detail-prompt branch in the old WinForms Allow flow; Rules' and Connections' Allow flows always commit a fixed default policy with no such branch). This matches the "no inert entries" principle Rules' own Add-picker flyout already established (process/window items stayed off the flyout until the code behind them existed). General is left with one real setting — theme — as a result; that's an accepted trade of thinness for honesty.
3. **Generalize the commit primitive instead of adding a second one.** Rules' `CommitProfileChangesAsync(Action<ServerProfileConfiguration>)` only reaches the active profile, but Settings needs to mutate `ServerConfiguration`-level fields too (`Blocklists.*`, `AutoUpdateCheck`, `LockHostsFile` are not under `ActiveProfile`). Rather than add a parallel commit method — which would contradict Rules Task 3's explicit "one method, one failure contract" rationale — widen the existing one to `CommitConfigChangesAsync(Action<ServerConfiguration>)`, operating on the whole cloned config. Existing call sites in `RulesPage`/`ConnectionsPage` change mechanically to `config => mutate(config.ActiveProfile)`; the clone → mutate → PUT → adopt-response-honestly shape, and the `PUT_SETTINGS && !Warning` success contract, are unchanged.
4. **WinUI's local settings (just `UiTheme`) get their own file, not a share of WinForms' `ControllerConfig`.** The obvious move — split `ControllerSettings` into Core the way Rules Task 1 split `AppDatabase` — turns out not to work: `ControllerSettings`' WinForms-only fields (`ConnFormWindowState`, `ProcessesFormWindowState`, `ServicesFormWindowState`, `UwpPackagesFormWindowState`, plus `System.Drawing.Point`/`Size` window-geometry fields for forms that don't exist in WinUI) are typed with `System.Windows.Forms`/`System.Drawing` types. Neither `SimpleDeFence.Core` nor `SimpleDeFence.UI` has WinForms enabled today; sharing the literal class would drag that dependency into Core (undermining the direction this whole phase is about), and having WinUI write only a subset of fields back to the *same* file WinForms uses would silently discard whatever WinForms-only preferences were already in it (a partial-schema writer overwrites the whole file, not just the fields it knows about). A separate, WinUI-only settings file avoids both problems. The reconciliation cost is deferred to the eventual net10 exe merge (ROADMAP.md), which is a more natural point to solve it anyway.
5. **Import/Export stays cross-compatible with the existing `.tws` format without reusing the `ConfigContainer` type name.** `SimpleDeFence.Core`'s sources are glob-compiled into the WinForms project (the established pattern since Rules Task 1); a new Core type literally named `ConfigContainer` would collide with the existing WinForms-only class of that name in the same `SimpleDeFence` namespace. The Core-side export DTO gets a distinct name (`ConfigExport`, exact naming left to the implementation plan) with the same `{Service, Controller}` wire shape — `Service: ServerConfiguration` (unchanged), `Controller: ClientSettings` (the new minimal Core class from decision 4). A file WinForms exports round-trips into WinUI's `ConfigExport` with its many WinForms-only `Controller` fields silently ignored as unmapped; a file WinUI exports round-trips into WinForms' `ConfigContainer` with those same fields left at their defaults. This is a one-time, explicit, user-initiated action (unlike decision 4's live shared-file concern), so a lossy-on-the-fields-neither-side-cares-about round trip is an acceptable, minor cost, not a live data-loss risk.
6. **"Check for updates now" (the manual trigger) is deferred, not the `AutoUpdateCheck` toggle.** The toggle is a plain `ServerConfiguration` boolean, cheap to wire through `CommitConfigChangesAsync`. The manual check needs `Updater`/`UpdateChecker` (`SimpleDeFence/UpdateChecker.cs`), which is WinForms-only today and looks like a real subsystem in its own right (HTTP calls, version comparison, MSI install flow) — not something to fold silently into a Settings plan already covering 7 groups. Scoped out here as a named follow-up, the same treatment Rules gave its own deferred items.
7. **Password/lock is one Security group, matching the design doc's own grouping** ("Security (password/lock)"), not split out to the mode chip. The mode chip already shows an honest "Locked" status when a commit is refused; this design does not touch the shell. A future enhancement could surface a quicker unlock path there, but that's not required for Settings to be complete — the design's IA already places account/security management in Settings.

## Screen: Settings

`SettingsCard`/`SettingsExpander` from the WinUI Community Toolkit, per the modernization design doc. **New dependency, not yet added to `SimpleDeFence.UI.csproj`:** `CommunityToolkit.WinUI.Controls.SettingsControls`.

One scrolling page, seven groups in the design doc's order. Every control commits immediately on change — no OK/Cancel batching, matching the "commits are immediate" convention Rules established (its own scope decision 5). Each commit reports failure honestly through the same `ShowResultAsync`/`FailureDetail` pattern Rules/Connections already use, and refreshes the page on success.

| Group | Contents | Commit path |
| --- | --- | --- |
| **General** | Theme (Auto / Light / Dark) | Local only — `ClientSettings.Save()`, applied immediately via WinUI's theme mechanism (`RequestedTheme` on the root element) |
| **Protection** | Allow local subnet, Block network when display is off | `CommitConfigChangesAsync` → `config.ActiveProfile.AllowLocalSubnet` / `.DisplayOffBlock` |
| **Blocklists** | Enable blocklists (master), Hosts blocklist, Malware-port blocklist — the two sub-toggles disabled when the master is off, matching WinForms' existing `chkEnableBlocklists_CheckedChanged` relationship | `CommitConfigChangesAsync` → `config.Blocklists.*` |
| **Security** | Lock hosts file; password status (has one / not set) with Set / Change / Remove actions; Lock now (when unlocked) / Unlock with a password prompt (when locked) | `CommitConfigChangesAsync` → `config.LockHostsFile`; new `SetPasswordAsync`/`LockAsync`/`UnlockAsync` |
| **Updates** | Auto-check-for-updates toggle | `CommitConfigChangesAsync` → `config.AutoUpdateCheck` |
| **Maintenance** | Import (.tws), Export (.tws) | Import replaces the whole config, still through the honest `PUT_SETTINGS && !Warning` contract; Export serializes the current config via `FileSavePicker` |
| **About** | Version, GitHub/homepage link, license, attributions | Static — no IPC |

Lock state (`HasPassword`, `Locked`) needs no new plumbing to read: `IFirewallClient.State` already carries both, populated on every refresh.

## Architecture

Three changes to the client surface, all extending patterns already established in Rules/Connections rather than introducing new ones:

1. **`IFirewallClient.CommitProfileChangesAsync(Action<ServerProfileConfiguration>)` becomes `CommitConfigChangesAsync(Action<ServerConfiguration>)`.** Same clone → mutate → PUT → adopt-response contract (decision 3). `AllowAsync` (already a thin wrapper) adapts trivially. Existing `RulesPage`/`ConnectionsPage` call sites change to the one-line `config => mutate(config.ActiveProfile)` form.
2. **New `IFirewallClient` members:** `Task<MessageType> LockAsync()`, `Task<MessageType> UnlockAsync(string password)`, `Task<MessageType> SetPasswordAsync(string password)` (an empty string clears the password, matching `PasswordLock.SetPass`'s existing convention). All three are thin wrappers over `SimpleDeFence.Core.Controller`'s existing `LockServer()`/`TryUnlockServer(string)`/`SetPassphrase(string)` — no new server-side protocol work; these methods already exist and are already used by the WinForms controller.
3. **New Core types:** `ClientSettings` (namespace `SimpleDeFence`, holding just `UiTheme` plus `Load`/`Save` against its own file — decision 4) and `ConfigExport` (wrapping `ServerConfiguration` + `ClientSettings` for the `.tws` Import/Export wire format — decision 5). Both are plain data, net48-safe, following the same `SerializationHelper`/source-gen-context pattern `AppDatabase` already uses.

`SampleFirewallClient` gets sample implementations of all of the above (a fake password/lock state machine, an in-memory `ClientSettings`), matching the existing sample-provider convention.

## Cross-cutting

- **Every toggle read reflects live server/local state**, not a client-side assumption — this is a general-purpose settings surface for a firewall, so a stale-looking toggle that doesn't match reality is a worse failure mode than most.
- **Never let an unrecognised response look like success**, on every one of the new commit paths, matching the honesty contract established everywhere else in this app.
- The password Set/Change/Remove flow validates match-and-non-triviality client-side before committing, mirroring `SettingsForm.btnOK_Click`'s existing `txtPassword`/`txtPasswordAgain` check.
- Full keyboard access, `AutomationProperties`, colour-plus-icon-plus-word status — same cross-cutting requirements as the rest of the app, carried from the modernization design doc unchanged.

## Error handling

Same three-way distinction already established for mode switching and every commit path since: **not connected**, **locked**, **operation failed** — never conflated. A locked commit attempt (any of the seven groups) reports the existing `RESPONSE_LOCKED` honestly; a stale-changeset commit reports `RESPONSE_STALE_CHANGESET` (added in the Rules final review) honestly. Import failure (malformed file, wrong format) reports a distinct, honest error and does not partially apply.

## Testing

- `ClientSettings`/`ConfigExport`'s (de)serialization gets unit tests in `SimpleDeFence.Tests`, extending the pattern `AppDatabase`/`RuleList` already established — particularly the cross-compatibility claim in decision 5 (a WinForms-shaped `ConfigContainer` JSON payload deserializes into `ConfigExport` without throwing, ignoring the fields it doesn't know).
- View/page logic follows Rules' and Connections' precedent: no automated WinUI page tests, manual verification against the sample-data provider (and, where practical, live UI Automation against the running exe) substitutes, as it did throughout Rules — including for the one WinRT single-file/save-dialog interaction this design also uses, which carries the same known, previously-flagged verification risk as Rules' executable picker.

## Out of scope

- **"Check for updates now"** (the manual action) and porting `Updater`/`UpdateChecker` to Core (decision 6) — a follow-up.
- **A quicker Lock/Unlock surface in the mode chip** (decision 7) — the shell is not touched by this design.
- **Settings reconciliation between `ControllerSettings` (WinForms) and `ClientSettings` (WinUI)** — deferred to the net10 exe-merge migration (ROADMAP.md), which is a more natural point to decide how (or whether) old WinForms-side preferences carry forward.
- **Retiring `SettingsForm`.** Nothing is removed from the WinForms app until its replacement reaches parity, matching every prior phase of this migration.
