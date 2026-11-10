# Move and dock dashboard panes

Roadmap item: `move-and-dock-dashboard-panes` (Later, Dashboard). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress (step 3 of 8 is next; steps 1, 2 and 4 to 7 are done for the six sidebar panels). Slice 1 built 2026-11-10: the six sidebar panels move between the sidebar and a dock region under the output, reorder, close, reopen and reset, per workflow, from a Panels menu in the header, saved in the settings layer file. The live bindings dock, the app output pane, per-pane header menus and drag and drop are not built (see "What is left"). |
| Size | L (slice 1 is about a third of it) |
| Branch and worktree | none: committed on master |
| Last green commit | e70b4aa0 (the slice's own test lists and the browser matrix pass on it) |
| Next action | Make the live bindings dock and the app output movable panes. They need their own wrap rules first: `.live-dock` is a direct flex child of `.main-area` (its `order: -1` at phone width and its `height: var(--dock-max-height)` depend on it), so the first step is a RED browser cell that puts it in `.sidebar-inner` at 200 to 600 px and reads `layoutProblems`. |
| Decisions made | The layout lives in the global settings layer file, not a new `layouts.json` (below). Buttons come first and live in one menu; drag and drop stays optional. The output and the Sessions list are pinned. Per-workflow defaults are all today's order for now (open question for Will below). |
| Surprises | `.live-dock` cannot be wrapped without breaking its CSS, which cut the slice: the bindings dock is not a pane yet. Datastar's `Ds.preserveAttr "open"` keeps the menu open across the morph, so no signal was needed. The render skip guard compared the snapshot alone, so a layout edit with an unchanged snapshot would have been skipped: the layout view is now part of what the guard compares. |

## Slice 1, as built

What exists, and where it differs from the design below. The design section is left as it was written, so the differences are listed here.

- Pure model: `SageFs.Core/PaneLayout.fs` (in Core, not `SageFs/`, so the settings layers can use it). Six `PanelId`s (`PaneId` was taken by the deprecated terminal UI; hot reload, live testing, session context, friction, agent team, workspace hygiene), two `PaneRegion`s (sidebar, dock), four `LayoutWorkflow`s (REPL, hot reload, live testing, no session). `PaneLayout` is three lists, and `PaneLayout.apply` is total: a move to a region, move up, move down, close, reopen and reset. `LayoutBook.writesFor` turns an edit into the writes it makes against the saved layer (a layout equal to its default is cleared, not stored; an edit that changes nothing writes nothing). Edits and saved text have one spelling each (`BookEdit.token`, `PaneLayout.render`) and refuse with a named `LayoutRefusal`.
- Persistence: `SageFs.Core/PaneLayoutStore.fs`, through `SettingsStore` on the global layer file, keys `dashboard.layout.repl`, `.hotreload`, `.livetesting`, `.nosession`. This replaces the design's `layouts.json`: the layer store already has the atomic write, the lock and the `.corrupt` copy, and a layout is one more key. They are not catalog descriptors (no row on the Settings page), because a descriptor needs a new `SettingValue` case and a row of `sidebar=a,b;dock=c` text is a worse editor than the menu. The store remembers what it read until the file's stamp changes, so a push costs one stat.
- Render: `renderMainContentIn (view: PaneLayoutView) snap` in `DashboardFragments.fs`; `renderMainContent snap` is the same with the default view, so none of the tests that build a snapshot by hand had to change. There is no `Layout` field on `DashboardSnapshot` for that reason. Panels are drawn bare, with no wrapper element, so the default page is the page that was always drawn (the sidebar CSS selects `.sidebar-inner > .panel-hidden-reason`). The dock region, `#pane-dock`, is drawn only when a panel is in it.
- Controls: one Panels menu (`SageFs/PaneMenuView.fs`), a `<details>` in the header whose body is fixed to the viewport. One row per panel with To dock or To sidebar, Up, Down and Close, a Reopen button for a closed one, a note for a panel that has only a reason line or nothing to report, and Reset this layout and Reset all layouts (disabled when they would change nothing). Every button has an accessible name of its own. The design put a menu on each pane header; the panels are already-drawn nodes from six different renderers, so slice 1 has the one menu.
- Wiring: `POST /dashboard/layout?e=<workflow>/<edit>` (`createPaneLayoutEditHandler` in `Dashboard.fs`). The edit names its workflow, the one the menu was drawn for, so a click cannot land on a workflow the person switched to a moment later. The handler saves and calls `infra.TriggerStateChange`, so every open page is pushed the new layout in the same morph. The render skip guard now compares `(snapshot, layout view)`.
- Not an API change: the route is a `/dashboard` UI route, `apiVersion` stays 4, and no MCP tool or parameter was added.

Proof: `PaneLayoutTests` (properties over generated edit sequences, mutants, the store on real temporary directories), `PaneMenuViewTests` (the page drawn from a layout), `PaneLayoutSimDstTests` (three tabs, a crashed reset, a corrupt file, a restart, and a twin that saves a stale whole book), and `DashboardPaneLayoutBrowserTests` (headless Chromium, 375, 768 and 1400 px: four layouts, and the moves by click and by key, a reload, a reset).

## What is left

- The live bindings dock and the app output pane as movable panes (needs the wrap rules described under "Next action").
- A menu on each pane's own header, so a panel can be moved from where you are looking at it.
- Drag and drop (step 8), with Datastar event attributes.
- The "measure first" questions: whether a moved panel keeps its scroll position and its open `<details>` (the open state is a signal, so it does; scroll position is unmeasured), and how much of the 6 KB inline-JS budget is left (slice 1 added none).
- A daemon journey with a real daemon: move a panel, reload, restart the daemon on the same data directory, switch workflow, reset. The browser matrix runs the real handler logic and a real store, but not the daemon's route plumbing.
- The command palette entries (`command-palette-and-one-timeline` depends on this).
- Whether each workflow should start with a different layout (live testing first in a live testing session, for instance). Today all four defaults are the order the sidebar always had.

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
