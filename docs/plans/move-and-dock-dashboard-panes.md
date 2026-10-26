# Move and dock dashboard panes

Roadmap item: `move-and-dock-dashboard-panes` (Later, Dashboard). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | L |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: moving a pane leaves every pane placed exactly once. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

Every pane except the output and the session list can sit in the sidebar or the bottom dock, in an order you choose, be closed,
and be reopened from a View menu that also lists the panes SageFs is hiding for you and why. Each workflow (REPL, Hot Reload,
Live Testing, no session) keeps its own layout, a Reset button puts one back, and the layout survives a reload and a daemon
restart. At phone width (768 px and below) docking is ignored and panes stack in region order, as the page stacks today. The
outcome gate is a browser matrix: {the default layout for each workflow, everything in the dock, everything in the sidebar,
everything closed} x {375, 768, 769, 1024, 1400, 2200 px}. In each cell no pane is clipped, the page does not scroll sideways,
every open pane can be scrolled into view, and the default layout renders the same markup it renders today.

## What exists today

Verified by reading the code.

- The layout is fixed in markup. `renderMainContent` (`SageFs/DashboardFragments.fs` line 2254) places `snap.OutputPanel` and
  `snap.BindingsPanel` in `.main-area`, and `snap.HotReloadPanel`, `LiveTestingPanel`, `SessionContextPanel`, `FrictionPanel`,
  `CohortPanel`, `HygienePanel` in `.sidebar-inner`, in that order. `DashboardSnapshot` (`DashboardTypes.fs` line ~1530) holds
  each as an `XmlNode`.
- The bottom dock has two panes decided by `DockPanes.PaneState.decide` (`SageFs/DockPanes.fs`): live bindings and app output,
  drawn by `LiveBindingsDock.renderDock` (`LiveBindingsDock.fs` line 469) and `AppOutputView.fs`. Folding, pinning and the
  filter are Datastar signals (`DockSignals`: `LiveFilter`, `LivePins`, `LivePanePinned`, `LivePaneCollapsed`), so a morph never
  resets them.
- Two sizes persist per browser in `localStorage`: `sagefs.dockHeight` (a Datastar signal restored by `Ds.onInit`, written in the
  drag handle's `pointerup`) and `sagefs.sidebarWidth` (the inline script in `Dashboard.fs` line ~328, counted against the 6 KB
  inline-JS budget in `DashboardSnapshotTests`).
- Server-side per-tab state lives in `PageChoices<'v>` (`SageFs/PageChoices.fs`) keyed by the client id the page minted, for
  example `frictionOptInByClient`. A tab's live-pushed choices go through `DashboardStreamCommand` on
  `infra.ConnectionChannels`: `RetargetView` for the viewed session, `SetCohortViewingSeq` for the scrubber. Line 921 pokes a
  tab with `StateChange (ModelChanged (0, 0))`.
- Server-side persisted preferences exist: themes live in `<SageFsDir>/themes.json`, written by `saveThemes` and read by
  `loadThemes` (`DashboardTypes.fs` line ~1833), keyed by working directory.
- The viewed session is a Datastar signal (`Signals.ViewingSessionId`) the server follows; there is no URL parameter. The repo's
  rule for navigation state is signals synced with the backend.
- `PanelVisibility.Hidden` carries a reason "for the View menu the redesign adds later" (`DashboardTypes.fs`). The
  `dashboard-follows-context` plan turns that into `PanelVisibility.decisions`, which this menu reads.
- Responsive CSS: `.app-layout` is a flex row (`dashboard.css` line 141); at `max-width: 768px` it becomes a column, the sidebar
  goes full width capped at `50vh`, `.resize-handle` is hidden and `.live-dock` is capped at `22rem` (lines 2190 to 2270). The
  sidebar's inner column has `min-width: var(--sidebar-width)` (320 px) on desktop.
- I did not find `dashboard-ux-redesign.md` in the repository; the notes about the redesign live outside it.

What is missing: any model of placement, any persistence of it, any control to change it, and a way to render a pane in a region
other than the one `renderMainContent` hardcodes. The roadmap's "the live bindings becoming a real pane in REPL mode" is the same
gap seen from one pane.

## Design

Where the state lives: the layout is a server-held, persisted preference, shared by every tab, one per workflow. The gesture in
flight (which pane is being dragged, whether the View menu is open) is a Datastar signal on the tab. Reasons:

| Option | For | Against |
|---|---|---|
| Client signals plus `localStorage` only | Matches the signal rule; no server code | Regions are server-rendered, so the server must know the layout to draw the pane in the right container. A workflow switch happens on the server. A phone and a desktop would not share it. |
| Per-tab `PageChoices` | Same pattern as the friction opt-in | The client id is minted per page load, so the layout would reset on every reload. |
| Server file `layouts.json`, like `themes.json` | Survives reload, restart and device; workflow switch is a server event; one render path | A second tab sees your change (wanted); a phone shares it, so docking is ignored below 769 px |

Sizes (`sagefs.dockHeight`, `sagefs.sidebarWidth`) stay in `localStorage`: a width fits one screen, a layout fits a workflow.

Pure core, new file `SageFs/PaneLayout.fs` (no Falco, no IO):

```fsharp
type PaneId = HotReload | LiveTesting | SessionContext | Friction | Cohort | Hygiene | LiveBindings | AppOutput
type Region = SidebarRegion | DockRegion
type Placement = DockedAt of Region * slot: int | ClosedByUser

type WorkflowKey = ReplLayout | HotReloadLayout | LiveTestingLayout | NoSessionLayout   // from SessionWorkflow, exhaustive

type Layout = private { Placements: Placement[] }          // indexed by PaneId, so every pane has exactly one placement
type LayoutEdit =
  | MovePane of PaneId * Region * slot: int
  | ClosePane of PaneId
  | ReopenPane of PaneId                                    // returns to its default region, last slot
  | ResetLayout

type LayoutLoad =
  | Loaded of Layout
  | FellBackToDefault of reason: string                     // unreadable file or unknown shape, shown on the menu
```

Pinned panes: the output and the session list are not `PaneId`s and cannot be moved or closed. `apply : LayoutEdit -> Layout ->
Layout` renormalises slots into a dense `0..n-1` per region, so no edit can leave a gap or a duplicate. `Layout.default` for each
`WorkflowKey` reproduces today's order exactly.

Relevance and placement are separate axes, and rendering joins them:

| Placement | Relevance (from `PanelVisibility.decisions`) | Draws |
|---|---|---|
| `DockedAt r` | `Shown` | the pane, in region `r` |
| `DockedAt r` | `Hidden because` | the reason line, in region `r` (where the pane would be) |
| `DockedAt r` | `Quiet` | nothing |
| `ClosedByUser` | any | nothing; the View menu lists it with "reopen" |

Persistence: `<SageFsDir>/layouts.json`, written through `SageFs.Json` (never a hand-built `JsonSerializerOptions`), atomically by
tmp plus move as `SettingsStore` does. A pane is written by an explicit exhaustive name function (no union encoding), so the
file reads the same on .NET 10 and 11. A missing pane (a later release adds one) takes its default; an unknown name is dropped
with a note on the menu. An absent file is the default layout. The binary manifest is untouched; there is no migration.

Edge and data flow: `POST /dashboard/layout/edit` reads the tab's signals (`paneId`, `paneRegion`, `paneSlot`, the client id and
`viewingSessionId`), resolves the workflow from the viewed session (`q.GetSessionWorkflow`), applies the edit to that workflow's
layout in a `LayoutStore` held on `DashboardInfra`, saves, and pokes every channel in `infra.ConnectionChannels` so all tabs
render the new layout in the same push. The one-morph rule holds: the layout is one more input to the snapshot, and the
no-change guard compares the full html as it does now.

Render: `renderMainContent` takes `snap.Layout` and calls one `renderRegion` for each of `SidebarRegion` and `DockRegion`. The
bottom dock container (`#live-dock`, its collapsed bar and its pin) moves into the region renderer; `LiveBindingsDock.renderDock`
splits into `renderBindingsPane` and `renderAppOutputPane` so either can sit in the sidebar. Each pane header gets a
menu of real buttons ("Move to dock", "Move to sidebar", "Move up", "Move down", "Close"), each `Ds.onClick (Ds.post ...)`. Buttons
come first because they work from the keyboard and screen readers; drag and drop is the last, optional step and uses Datastar
event attributes (`dragstart`, `drop`), no new inline script.

View menu, a `<details>` in the tabline beside the gear: one row per pane with its state (open in sidebar, open in dock, closed,
hidden because ..., quiet), a reopen button for closed ones, and "Reset this layout" and "Reset all layouts". The command palette
(`command-palette-and-one-timeline`) lists the same actions.

Resize rules reasoned through: at 769 px and up the two regions are the sidebar column and the dock row. A pane in the dock gets
the dock's `max-height` ceiling and scrolls inside it. A pane in the sidebar scrolls the sidebar. At 768 px and below the existing
media query stacks `main-area` then `sidebar`, so panes appear in region order: dock panes under the output, sidebar panes under
those. The View menu stays reachable because the tabline already scrolls horizontally under 480 px.

Wire and clients: dashboard-only. The route is page-internal and not in `EndpointContracts.fs`, so `apiVersion` stays 4. The
editors have their own panes and are unaffected. No MCP tool, no token cost. Commits that touch `DashboardTypes.fs` (a
wire-surface file by the repo's list) carry `Plugin: n/a dashboard markup only, no wire change`.

Files: new `PaneLayout.fs`, `PaneLayoutJson.fs`, `PaneMenuView.fs`; changed `DashboardTypes.fs` (snapshot field, `DashboardInfra`
field), `DashboardFragments.fs` (`renderMainContent`), `LiveBindingsDock.fs`, `DockPanes.fs`, `Dashboard.fs` (route, workflow
resolution), `DashboardRoutes.fs` (from the context plan), `dashboard.css`, `SageFs.fsproj` (compile order: after
`DockPanes.fs`, before `DashboardFragments.fs`).

## Tests

RED first, in order.

1. `PaneLayoutTests`: "moving a pane to the dock puts it in the last dock slot and leaves every other pane placed exactly once".
   Compile-red (module absent).
2. FsCheck properties over generated edit sequences: (P1) every `PaneId` has exactly one placement; (P2) slots in each region are
   dense and unique; (P3) `ResetLayout` equals the default for that workflow; (P4) `Close` then `Reopen` returns the pane to its
   default region, last slot; (P5) an edit to one workflow's layout never changes another's; (P6) serialise then parse is the
   identity; (P7) parse of a file missing a pane fills the default, and of a file naming an unknown pane drops it and says so.
3. `layouts.json` corrupt or unreadable gives `FellBackToDefault reason`, the menu shows the file path and why, and the default
   renders. Absence is a DU case.
4. Render tests: default layout html equals today's html. Capture today's `renderMainContent` output as a Verify snapshot in
   step 1, before any change, so the refactor is proved byte-identical. Then layout x relevance table above, case by case.
5. Mutation: a `Mutant` that skips renormalisation, one that lets `Close` drop a placement; the properties must fail.

Simulation (`SageFs.Simulation/PaneLayoutSim.fs`, `PaneLayoutSimInvariants.fs`, tests `PaneLayoutSimDstTests.fs`, modelled on
`RetentionSim.fs`). Pool: two tabs, three workflows, the layout file. Ops: `Edit(tab, edit)`, `SwitchWorkflow(tab)`,
`CrashDuringSave`, `CorruptFile`, `DaemonRestart`, `TabConnects`, `TabDisconnects`. Seeded schedule, injected clock, faults from
`FaultInjection.fs` (reorder, duplicate, drop). Invariants: (a) after quiescence every connected tab renders the same layout for
the same workflow; (b) the file is absent or parses; (c) an edit in one workflow never changes another; (d) the rendered regions
contain every open pane once; (e) reset equals default. Twin: a save that writes the whole book from a stale in-memory copy,
which breaks (c) when two tabs edit different workflows. No wall-clock waits: the poke is an event and the sim steps by schedule.

Outcome gate: `DashboardPaneLayoutBrowserTests.fs`, in-memory page (the `DashboardMobileBrowserTests.fs` technique, real stylesheet
and Datastar bundle) for the matrix above, using the `layoutProblems` helper from `DashboardLiveBindingsDockBrowserTests.fs`.
Plus one daemon journey with its own data dir (`--integration-browser`): create a REPL session, move the hygiene panel (always
relevant) to the dock, reload, assert it is still there, restart the daemon on the same data dir, assert it, switch workflow and
assert the other layout is untouched, press Reset. Waits are `waitForSSE` and selector-text waits; zero console errors and zero
`PatchElementsNoTargetsFound` in every cell.

## Steps

1. Capture the Verify snapshot of today's default markup, then RED test 1 and `PaneLayout.fs` with its properties. Owns
   `PaneLayout.fs`, `PaneLayoutTests.fs`, the `.fsproj` line.
2. `PaneLayoutJson.fs` and `LayoutStore` (atomic save, `LayoutLoad`), with the simulation and its twin. Owns
   `PaneLayoutJson.fs`, `PaneLayoutSim*.fs`. Parallel with 3.
3. Split `renderDock` into per-pane renderers, same output. Owns `LiveBindingsDock.fs`, `DockPanes.fs`. Parallel with 2.
4. `renderRegion` and `snap.Layout`; default layout equals the snapshot from step 1. Owns `DashboardFragments.fs`
   (`renderMainContent`), `DashboardTypes.fs`. Depends on the context plan's `decisions`.
5. Route, store on `DashboardInfra`, poke all tabs, workflow resolution, pane header buttons. Owns `Dashboard.fs`,
   `PaneMenuView.fs`, `DashboardRoutes.fs`.
6. View menu and Reset. Owns `PaneMenuView.fs`, `dashboard.css`.
7. Browser matrix, then the daemon journey. Owns the new test file.
8. Optional: drag and drop with Datastar event attributes.

## Measure first

- Does a moved pane keep its scroll position, its open `<details>` and its change-flash when the morph moves it between
  containers? Experiment: in the in-memory page, render the bindings pane in the dock, scroll its tree, then morph it into the
  sidebar and read `scrollTop`, `details.open` and the flash class. If scroll resets, a moved pane scrolls to top once, which is
  acceptable and should be stated; if the flash animation replays on every move, step 4 needs `Ds.preserveAttr` on the pane root.
- Does the bindings tree, with knobs and the tweak strip, fit a 200 to 600 px sidebar column? Experiment: the same page with the
  pane in `.sidebar-inner` at each `--sidebar-width`, run `layoutProblems`. If it overflows, the pane needs its own wrap rules
  before the matrix can pass.
- How much of the 6 KB inline-JS budget is left? The plan adds none, but the number says how much room the drag step has.

## Risks and dependencies

- `renderMainContent` and `DashboardTypes.fs` are shared with `dashboard-follows-context` and `command-palette-and-one-timeline`.
  Land the context plan first.
- A pane that reflows badly at 320 px (cohort matrix, hygiene plan rows) is a CSS job per pane, found by the matrix.
- Two people on two screens share one layout. Docking below 769 px is ignored, so a phone cannot break a desktop layout, but a
  desktop edit shows on the phone's stacking order.
- Depends on `dashboard-follows-context` (decisions list). `command-palette-and-one-timeline` depends on this for its layout
  commands.

## Size

L, three weeks or more. The pure core is small; the region refactor of `renderMainContent`, the dock split, the View menu and the
width matrix are the weight.

## Decisions for Will

- Server-held layout per workflow (recommended) or per-browser `localStorage`? The first matches `themes.json` and the
  workflow-switch event; the second matches the signal rule for navigation state but cannot place a server-rendered pane.
- Buttons first, drag and drop later? Recommend yes: buttons are keyboard-reachable and cheap to test.
- Are the output and the session list pinned? Recommend yes; closing the transcript leaves nothing to read.
