# WinUI 3 GUI modernization — design

Date: 2026-08-08
Status: approved design, not yet implemented
Applies to: `SimpleDeFence.UI`

## Problem

The WinUI 3 shell currently has two screens — Status (mode switching) and Applications (a
read-only exception list) — built ad hoc as the first thing that would compile. There is no
information architecture, no visual language, and no plan for the rest of the WinForms surface
that still has to be ported.

Separately, the app has a usability problem inherited from TinyWall: it is **silent**. It never
nags, which is its main selling point, but that also means a user whose application has stopped
working has no way to find out that the firewall is why.

## Decisions

Each of these was chosen deliberately; the rationale matters more than the choice.

1. **Full target information architecture, decided up front.** Cheap now, expensive after three
   more screens exist.
2. **Hybrid centre of gravity.** Daily verbs (switch mode, whitelist by window/process) stay in
   the tray and hotkeys, because non-intrusiveness is the product's differentiator. The window
   earns its place by doing what a menu cannot: review, history, audit.
3. **The window leads with network activity, not rules.** This directly addresses the silence
   problem. "Why can't this app connect?" becomes answerable in one click.
4. **Native Fluent with its own voice.** Stock WinUI controls and behaviours as the base — a
   security tool has to look trustworthy and native — with a deliberate status language layered
   on top so it does not read as a default WinUI sample.
5. **One window.** Today `ConnectionsForm` and `SettingsForm` are separate top-level windows.
   They become destinations inside a single app.

## Information architecture

Three destinations in a `NavigationView`:

- **Connections** (landing)
- **Rules**
- **Settings**

There is deliberately **no Status destination**. Status is not somewhere you go; it is something
you always see. The current mode lives in a persistent **mode chip** in the nav pane footer,
visible on every page and clickable to change. This removes a page from the IA and makes state
ambient.

### Rejected alternatives

- *Activity / Connections / Rules / Settings* — splitting live connections from blocked events
  puts two indistinguishable labels in the nav; a user cannot tell which one answers their
  question.
- *App-centric master–detail* — elegant for "why is this app broken?", but forces global rules,
  special exceptions and rule-less blocked traffic into awkward exceptions to the model, and
  buries activity a level down.

## Shell

- `NavigationView`, Mica Alt backdrop, custom title bar (`ExtendsContentIntoTitleBar`) so the
  pane runs to the top edge — the standard Windows 11 app shape.
- Adaptive: expanded left pane when wide, compact icon rail at medium, overlay below ~640px.
- Nav footer holds the **mode chip**: status colour + icon + mode name. Click opens a flyout to
  switch mode, carrying the same Learning-mode confirmation the WinForms GUI shows.

## Screen: Connections

One scrolling surface with **three sections separated by layout**. Not tabs, not a toggle — a
switch would hide half the picture and force the user to remember which view they are in.

| Section | Contents | Row |
| --- | --- | --- |
| **Blocked** | Recent blocked attempts | app · what was refused · when · **Allow this app** |
| **Connected** | Established connections | app · remote address:port · protocol · state |
| **Open** | Listening ports | app · local port · protocol |

- Section headers carry icon, label and a **live count**, are **collapsible** with remembered
  state, and **stick while scrolling**.
- Columns align across sections so it reads as one continuous table, not three unrelated lists.
- A single search box filters all three sections at once.
- Collapsible sections replace the old checkbox trio ("show open ports / active connections /
  blocked apps"): the same control with less chrome, and counts stay visible when collapsed so
  hidden data is not forgotten.
- An auto-refresh toggle controls live updating.

**The inline "Allow this app" action on blocked rows is the core idea of this design.** It closes
the loop from "something is broken" to "fixed" without leaving the screen, and it is the one
thing a tray menu fundamentally cannot offer.

## Screen: Rules

- Search and filter over exceptions, grouped **Applications** and **Special**.
- Compact rows: icon, name, kind, policy chip, path.
- Selecting a rule opens a detail pane for editing its policy.
- **Add** is a split button exposing the signature pickers — browse for executable, running
  process, **pick a window**, UWP package — so those flows exist in the window as well as behind
  hotkeys.
- Multi-select for bulk removal.

## Screen: Settings

Uses `SettingsCard` / `SettingsExpander` from the WinUI Community Toolkit. This is the
established Windows 11 settings idiom; hand-rolling it would look worse and cost more.

**New dependency:** `CommunityToolkit.WinUI.Controls.SettingsControls`.

Groups: General · Protection · Blocklists · Security (password/lock) · Updates · Maintenance
(import/export) · About.

## Visual language

The identity is the **status language**, which is the right choice for a firewall — mode *is* the
product's primary state.

| Mode | Colour role | Treatment |
| --- | --- | --- |
| Normal | Success | Protected, nominal |
| Allow outgoing | Caution | Relaxed |
| Block all | Informational-strong | Locked down |
| Disabled | Neutral | Inactive |
| Autolearn | Accent-alt | Transient/special |

These are **roles, not hex values**. They bind to the WinUI system semantic brushes
(`SystemFillColorSuccess`, `SystemFillColorCaution`, `SystemFillColorNeutral`, and so on) so they
track the OS theme and high-contrast modes automatically. Only where no system brush fits is a
custom token defined, and it is defined once in a single resource dictionary rather than inline.

Applied consistently across tray icon, mode chip and page headers.

**Never colour alone.** Every status is colour *plus* icon *plus* word, so it survives colour
blindness and high-contrast themes.

Otherwise: strict WinUI type ramp; Segoe Fluent Icons; compact ~40px list rows (this is a
data-dense utility, not a marketing page); card surfaces for grouped content; the system accent
left untouched for interactive states.

## Cross-cutting

- **Every data view gets designed empty, loading and error states.** In a firewall an empty
  Blocked list is a *good* outcome, so it should read reassuringly ("Nothing blocked in the last
  24 hours"), not like a failure.
- Full keyboard access; Ctrl+F focuses search.
- `AutomationProperties` on interactive elements; no colour-only signalling.
- Reduced-motion respected; no gratuitous animation.
- Light/dark/system themes.

## Architecture

The present code-behind with manual `UpdateDisplay()` calls will not carry live-updating lists.

- **View models** with `INotifyPropertyChanged`, `ObservableCollection`, compiled `x:Bind`.
- `FirewallClient` gains an interface and is extended to expose firewall-log and connection data.
- **All IPC stays off the UI thread**, as now.
- **Display-mapping logic goes into Core as pure functions**, beside `ExceptionDescriptor` —
  event phrasing, time grouping, policy summaries. That keeps it unit-testable without a UI, the
  same pattern that already caught the `ServiceSubject`/`ExecutableSubject` bug.

### Sample data provider

A sample-data implementation of the client interface, selected by a `--sample-data` command-line
switch. The real client remains the default in every configuration, so sample data can never be
shown to a user by accident.

This exists because of a hard constraint (below): the real service will refuse this GUI until the
.NET 10 migration completes, so without it no screen could be run or visually verified while
being built. It also becomes a permanent fixture for screenshot checks and for exercising empty
and error states on demand.

## Error handling

Carries forward the principle already established in mode switching: distinguish **not connected**
from **locked** from **operation failed**, and never let an unrecognised response look like
success. On a firewall, an action that did not take must not appear to have taken.

## Testing

- Pure display-mapping functions in Core get unit tests, extending `SimpleDeFence.Tests`.
- View models are testable without a UI.
- The sample data provider makes empty/populated/error states reproducible for visual checks.

## Constraint: this cannot run against the real service yet

`PipeServerEndpoint.AuthAsServer` rejects any IPC client whose executable path differs from the
service's own. `SimpleDeFence.UI.exe` is therefore refused by a Release service, and the agreed
fix is the net48 → .NET 10 migration that folds the GUI into `SimpleDeFence.exe`
(see ROADMAP.md). Until that lands these screens show "Not connected" against a real install.

Sequencing decision: **build against the sample data provider now.** The migration proceeds
independently and simply swaps the provider. This keeps every screen runnable and verifiable
while it is being built, which this project has repeatedly shown to matter — a crash-on-launch
and a silent IPC rejection both survived a green build and were only caught by execution.

Additionally, WinUI 3 cannot render on the Hyper-V test VM's basic display adapter (no D3D), so
visual verification happens on the host.

## Implementation phasing

This design covers more than one sitting's work. It is deliberately kept as a single design
because the pieces share an IA and a visual language, but implementation should phase in this
order, each phase independently runnable and verifiable against sample data:

1. **Foundation** — client interface + sample data provider, view-model base, status/visual
   resource dictionary. Nothing visible changes; everything after depends on it.
2. **Shell** — `NavigationView`, Mica Alt, custom title bar, mode chip, adaptive behaviour.
   Replaces the current Status page.
3. **Connections** — the landing screen, including the inline "Allow this app" action.
4. **Rules** — reworked from the present Applications page, plus the add/pick flows.
5. **Settings** — `SettingsCard` groups.

Phases 1–3 deliver the core value; 4 and 5 can follow separately.

## Out of scope

Deliberately excluded to keep this deliverable:

- WHOIS lookup, VirusTotal integration, IP blocklists — separate roadmap items.
- App-centric drill-down — good idea, but as a later addition *within* Rules, not the organising
  principle.
- Localization of the WinUI GUI — the WinForms GUI keeps its localized resx; the new GUI is
  English-only for now and localization is its own piece of work.
- Retiring any WinForms form. Nothing is removed until its replacement reaches parity.
