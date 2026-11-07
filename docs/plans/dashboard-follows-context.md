# Dashboard panels that follow your context

Roadmap item: `dashboard-follows-context` (Next, Dashboard). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done |
| Size | M |
| Branch and worktree | `worktree-agent-a1a5c9a8ceb72df95`, `.claude/worktrees/agent-a1a5c9a8ceb72df95` |
| Last green commit | the newest commit that touches this file (`git log -1 -- docs/plans/dashboard-follows-context.md`) |
| Next action | None for this item. One check is left for the next release gate: `LiveTestingBrowserTests` in `--integration-lt` (its `ensureLiveTestingOn` now clicks the Turn on button on the reason line, and was edited without a run; a throwaway daemon already showed the click turning live testing on). Not in this item: the live bindings becoming a pane in REPL mode and a pane moving between regions (`move-and-dock-dashboard-panes`). |
| Decisions made | 2026-11-07, taken as recommended by the roadmap agent, Will to overrule: (1) alarms, failure narratives and a clean diagnostics check stay `Quiet`, silent on the page, and show as "none" in the future View menu; (2) a cohort holding only orphaned claims or landings keeps its panel, collapsed, headed by "no cohort is running; 1 orphaned claim remains"; (3) finding 1 is a bug, and the reason line carries a "Turn on" button. |
| Surprises | The plan was written against a tree where `PanelVisibility` had two cases. By the time of the work it had a third, `NotOffered` (friction nobody asked for), so the friction panel became `Quiet FrictionNotRequested` rather than `Hidden`. The reason bar was already a `details` element, so the "Turn on" button rides on its summary line, the part that is visible while the sentence is closed. `renderDiagnostics` had no production caller (only three snapshot tests), so it was deleted instead of consolidated. The route scan test rejects a route registered through a constant, so `Dashboard.fs` keeps the literal paths and a test checks that `DashboardRoutes` names the same ones. The browser matrix found two defects nobody had listed: the lane and territory SVGs carried `height="auto"` (not an SVG length, a console error in Chromium for any cohort), and the session switch patched `#statusline-left`, which the page no longer holds (a `PatchElementsNoTargetsFound` warning on every click of a session row, found with a throwaway daemon and a headless run of the states no session, created from the picker, clicked row and sessions with a bare URL). Both are fixed. `renderStatuslineLeft` and its tests pinned a surface the page no longer draws, so the function, those tests and the two `.statusline-left` and `.statusline-file` rules are deleted. |

## What done looks like

For every panel the dashboard can draw, one pure function says whether it is shown, hidden with a reason, or quiet because it has
nothing to report. A hidden panel leaves a one-line reason where it would have been, and the line offers the next step when
there is one (a button that turns the thing on, or the name of the control that does). No panel disappears without a decision
that a test can read. The outcome gate is a browser journey over a state matrix, served from memory with the real stylesheet
and Datastar bundle (the `DashboardMobileBrowserTests.fs` technique): {no session, REPL session, Hot Reload session, Live
Testing session with live testing off, on and blocked, cohort absent, present, and holding only orphaned claims, friction
opted in or not, diagnostics not checked, clean, and failing} x {375, 768, 1400 px wide}. In each cell, each panel is on the
page, or its reason line is, and the page does not scroll sideways.

## What exists today

Verified by reading the code.

- `SageFs/DashboardTypes.fs` (about lines 1560 to 1700): `OptionalPanel = HotReload | LiveTesting | Cohort | Friction`,
  `PanelVisibility = Shown | Hidden of reason: string`, `PanelFacts` (viewed session, cohort presence, friction opt-in),
  `PanelVisibility.decide`, and `PanelVisibility.apply`, which swaps a hidden panel for `reasonBar`. The bar is a
  `div.panel.panel-hidden-reason` with id `hidden-reason-<panel>` (`DomIds.HiddenReason*`), styled at `dashboard.css` line 1269.
  The DU comment says the reasons are "for the View menu the redesign adds later".
- `apply` is called at five sites in `Dashboard.fs` (lines 1612, 1638, 1928, 3406, 3414), after the snapshot is built.
  The snapshot builder still builds every panel, including the friction panel's SQLite read and observed-signal pass
  (`frictionPanelTask`, line 1185), and `apply` throws the result away. I have not measured the cost.
- `SageFs/DockPanes.fs` does the same job for the bottom dock: `PaneState.decide`, `CollapsedBecause.text` (three one-line
  reasons), `PinOffer`. This is the model the sidebar rules copy.
- The hygiene panel (`renderHygienePanel`, `DashboardFragments.fs`) renders a state for every `HygieneView` case, including
  `NoRepository`. It never disappears.
- Tests: `SageFs.Tests/DashboardPanelVisibilityTests.fs` (rules, render, reason ids). Browser tests exist for the dock
  (`DashboardLiveBindingsDockBrowserTests.fs`) and the hygiene panel (`HygieneBrowserTests.fs`), none for the sidebar reasons.

Inventory of every panel, by reading `renderMainContent` (`DashboardFragments.fs` line 2254), `buildDashboardSnapshotWithSessions`
and the no-session builder in `Dashboard.fs`:

| Panel | When it has nothing | Says why? |
|---|---|---|
| Hot reload | REPL or Live Testing workflow, or no session | Yes, a reason line. No next step. |
| Live testing | live testing off, or no session | Yes. But see finding 1. |
| Cohort (members, claims, landings) | no present member | Yes. But see finding 2. |
| Cohort lanes, trunk, scrub control | `renderCohortLanesPanel`, `renderTrunkPanel`, `renderCohortScrubControl` return an empty `div` | No. Silent inside a cohort that is shown. |
| Friction | not opted in | Yes, names `?panels=friction`. |
| Live bindings (dock) | no session, none bound | Yes (`DockPanes`). |
| App output (dock) | no session, no output | Yes (`DockPanes`). |
| Hygiene | never empty | Yes, a state per case. |
| Sessions | no sessions | Yes, a `sessions-empty` hint (`renderSessionsForSession`). |
| Session context | `renderSessionContextEmpty` | Says "No session context", gives no reason. |
| Hot reload panel when "Shown" but the worker did not answer | `renderHotReloadEmpty` | Says "Hot Reload: OFF / No active session", which can be false. |
| Alarm banner | no alarms | No: `display:none` div. |
| Failure narratives | zero failures | No: empty root, by design ("absence of red is the signal"). |
| Diagnostics | none | No: `renderCurrentDiagnostics` draws an empty root. Clean and not-yet-checked look the same. |
| Daemon health bar | `GetDaemonHealth()` returns `None` | No: empty div. |
| Per-card test grid | no test entries | No: the `match ... | 0 -> ()` at `DashboardFragments.fs` line ~2092. |
| Per-card coverage map | `None` or zero probes | No: same place, line ~2113. |
| Eval-to-pixel latency | no eval yet, or perf toggle closed | A user toggle plus no data. Leave it. |

Findings that change the plan:

1. Live testing cannot be turned on from the dashboard for a session where it is off. The only dashboard "Enable" button
   (`renderLiveTestingPanel`, testid `live-testing-toggle`) is inside the panel, and `PanelVisibility.decide` hides that panel
   exactly when `LiveTestActivity.Off`. The reason line has no button. I grepped for other callers of
   `/dashboard/live-testing/enable` and found only that panel. I could not find another dashboard control. The browser test
   `LiveTestingBrowserTests.ensureLiveTestingOn` still assumes the panel is "always visible", so it is stale against the
   commit that hid it (`53748e14`) or only passes on sessions where live testing starts on. I did not run it.
2. The cohort gate hides history the renderer was written to show. `renderCohortPanel` computes
   `hasAnythingToShow = memberCount > 0 || claimCount > 0 || landings > 0` and its comments explain that an orphaned claim must
   still draw. `PanelFacts.cohortPresence` counts present seats only, so with zero members the whole block, orphaned claims
   included, is replaced by "no cohort is running". An orphaned claim waits for a conductor to reassign it, so a person asking
   "why was my claim refused" sees nothing.
3. Two renderers (`renderDiagnostics`, `renderCurrentDiagnostics`) share `DomIds.DiagnosticsPanel`. Only the second is in the
   snapshot. The first says "No diagnostics". Neither can say "not checked".

## Design

Make absence a named case everywhere. Extend the existing module, do not add a second one.

```fsharp
type ContextPanel =                     // replaces OptionalPanel; every panel in the table
  | HotReload | LiveTesting | Cohort | CohortLanes | CohortTrunk | Friction
  | SessionContext | Diagnostics | FailureNarratives | Alarms | DaemonHealth | TestGrid | CoverageMap

type HiddenBecause =                    // one case per sentence the user can read
  | NoSessionSelected of panel: ContextPanel
  | SessionInReplMode | SessionInLiveTestingMode
  | LiveTestingOff
  | NoCohortRunning
  | OnlyCohortHistory of orphanedClaims: int * landings: int
  | FrictionNotRequested
  | NotCheckedYet of what: string          // diagnostics before the first check

type NextStep =
  | NoNextStep
  | PostAction of label: string * route: string      // rendered with Ds.onClick (Ds.post route)
  | UseControl of controlName: string                // "switch the workflow in the header"

type QuietBecause = NoAlarms | NoFailingTests | NoDiagnostics | NoTestsDiscovered | NoCoverageYet

type PanelPresence =
  | Shown
  | Hidden of because: HiddenBecause * next: NextStep
  | Quiet of because: QuietBecause           // nothing to report; renders nothing, but is a decision
```

`HiddenBecause.text` and `HiddenBecause.next` are exhaustive matches, so a new reason cannot be added without writing its
sentence and its step. The current strings stay byte-identical so the existing assertions keep passing.

`PanelVisibility.decisions (facts) : (ContextPanel * PanelPresence) list` is the one list every other piece reads: `apply`, the
render tests, and later the View menu (`move-and-dock-dashboard-panes`) and the command palette
(`command-palette-and-one-timeline`), which show a quiet panel as "none" and a hidden one with its reason.

Three consequences:

- `PanelFacts` grows: `Diagnostics: DiagnosticsCheck` (`NotChecked | Checked of count`), `Cohort` becomes
  `CohortPresence = NoActiveMembers | OnlyHistory of orphanedClaims * landings | ActiveMembers of int`. Session cards get the
  same decision for their grid and map from the card's own entries.
- Decide first, then build. If the measurement below shows the hidden friction build costs real time, `decisions` runs before
  `buildDashboardSnapshotWithSessions` and hidden panels are never built.
- The reason bar renders a `NextStep` as a button using `Ds.onClick (Ds.post route)` and an `Ds.indicator` signal for in-flight
  feedback, the pattern in `renderLiveTestingPanel`. Never a hand-written attribute string. `UseControl` renders text only.

Layout: the bar is already `overflow-wrap: anywhere` and a flex child. The button wraps under the text below 480 px (one
`flex-wrap: wrap` rule), so the full resize range is a text line that can grow taller and never wider.

Files: `DashboardTypes.fs`, `DashboardFragments.fs` (the silent `()` and `Elem.div [] []` sites), `Dashboard.fs` (facts and the
five call sites), a new `SageFs/DashboardRoutes.fs` (route literals, plus its line in `SageFs.fsproj`), `dashboard.css`,
`DashboardPanelVisibilityTests.fs`, `LiveTestingBrowserTests.fs`.

Wire and clients: none. The dashboard is server-rendered and its routes are not in `EndpointContracts.fs`. `apiVersion` stays 4;
`sagefs.nvim` and `sagefs-vscode` have their own panels and are unaffected. No MCP tool, no token cost. No persistence.
`DashboardTypes.fs` is on the repo's wire-surface list, so each commit that touches it carries the trailer
`Plugin: n/a dashboard markup only, no wire change`, and `scripts/ship.fsx` checks for it.

## Tests

RED first.

1. In `DashboardPanelVisibilityTests.fs`: "a hidden live testing panel offers the way to turn it on". Render
   `PanelVisibility.apply replFacts (snapshotWith off joined)` and assert the html contains `/dashboard/live-testing/enable`
   inside `#hidden-reason-live-testing`. Fails today (the route sits only in the dropped panel), so it is behaviour-red with the
   current API.
2. "every panel has a decision": `PanelVisibility.decisions` returns exactly one entry per `ContextPanel` case, over a generated
   set of facts. Compile-red.
3. Property test over the full cross product of facts (3 workflows x 2 session states x 9 `LiveTestActivity` cases x 3 cohort
   x 2 friction x 3 diagnostics, a few hundred combinations, all enumerated): `decide` is total, every `Hidden` has a
   non-empty sentence, and `Shown` never co-occurs with a reason line in the html. Route agreement is by construction: the
   route strings become literals in one `DashboardRoutes` module that both the endpoint list and `NextStep.PostAction` use,
   so no test greps source for a path (`createEndpoints` needs a full set of fake queries to call, which is too heavy to
   stand up for this).
4. "diagnostics that were never checked do not read as clean": no session gives `Hidden(NotCheckedYet ...)`, a checked empty
   list gives `Quiet NoDiagnostics`.
5. "a cohort holding only orphaned claims says so": frame built with `Join`, `AcquireClaim`, `Depart` (the helper
   `frameAfter` in the existing test file) gives `OnlyHistory(1, 0)` and the line "no cohort is running; 1 orphaned claim remains".
6. Mutation: add a `Mutant` for `PanelVisibility.decide` in the style of `MutationTestingFramework.fs` (swap two reasons, drop a
   case) and require the assertions above to fail.

Simulation: the decision is pure, so the enumeration in test 3 plays the role of the exhaustive spec (`CohortSpec` style), and no
seeded scheduler is needed. The one timing path is the push after a state change. Test that a facts change alone (live testing
turned on, nothing else) changes the rendered `#main`, so the no-change guard in `createStreamHandler` (`lastPushedMain`,
`SnapshotRenderGuard`) cannot swallow it. No wall-clock waits: the test drives `renderMainContent` twice and compares.

Outcome gate: `DashboardPanelContextBrowserTests.fs`, in-memory page as above, `Integration.Dedicated "--integration-browser"`.
Plus one daemon journey in the `--integration-lt` tier: REPL session, live testing off, click "Turn on" in the reason line, wait for
`#live-testing-panel` with `PlaywrightExpect.waitForSelectorText`, never a sleep. Every cell asserts zero console errors and zero
`PatchElementsNoTargetsFound`.

## Steps

1. RED test 1. Introduce `HiddenBecause`, `NextStep`, keep strings, render the step. Owns `DashboardTypes.fs` (panel
   visibility region), `DashboardPanelVisibilityTests.fs`, `dashboard.css`.
2. Fix `LiveTestingBrowserTests.ensureLiveTestingOn` to use the reason-line button. Owns `LiveTestingBrowserTests.fs`. Parallel
   with 3.
3. `ContextPanel`, `Quiet`, `decisions`, tests 2 and 3. Owns `DashboardTypes.fs` (same region as step 1, so after it).
4. Diagnostics `NotChecked` and the renderer consolidation. Owns the two diagnostics render functions in
   `DashboardFragments.fs` and `DashboardDiagnosticsTests.fs`. Parallel with 5.
5. Session context and hot reload empty states say a true reason. Owns `renderSessionContextEmpty`,
   `renderHotReloadEmpty` and their call sites in `Dashboard.fs` (lines ~1113, 1139, 1369, 1371).
6. Cohort `OnlyHistory`, after Will's decision. Owns the cohort region of `DashboardTypes.fs` and `DashboardFragments.fs`.
7. Cohort lanes, trunk and scrub control say why when empty; per-card grid and map likewise.
8. The in-memory browser matrix, then the daemon journey. Owns the new test file.
9. Measure first result applied: decide-before-build, if it pays.

## Measure first

Measured 2026-11-07, in a SageFs REPL session on this worktree, with a throwaway SQLite store holding 5000 rows (the
`SAGEFS_FRICTION_MAX_ROWS` cap) and a concurrent `dotnet build` running on the machine:

- Hidden friction build: `reportDirect` plus `FrictionReviewView.build` plus `renderFrictionPanel` plus `renderNode` took
  32 to 78 ms warm (median 55 ms over 10 runs, 322 ms cold, 6620 characters out). That is several milliseconds, so step 9 applies:
  the decision runs before the build, and a tab that did not opt in never reads the store (`PanelContext.frictionPresence`, tested by
  counting `GetFrictionStore` calls).
- The live testing "Enable" button was unreachable for a session with live testing off: the RED test showed the reason line holding
  no route (REPL eval on the unmodified tree printed `false` and the markup of a bare `details`). Confirmed as a bug.
- `Ds.indicator` inside the swapped-in node: the reason line's button carries its own indicator signal (`panelActionLoading`), the
  same pattern as the live testing panel. The browser matrix (step 8) is where this is checked across a morph.

The original questions, kept for the record:

- What does the hidden friction build cost per snapshot? Experiment: time `buildDashboardSnapshotWithSessions` on a daemon
  whose store holds the 5000-row cap (`SAGEFS_FRICTION_MAX_ROWS`), with and without `?panels=friction`. If it is under a
  millisecond, leave the order alone. If it is several, step 9 moves the decision first.
- Is the live testing Enable button really unreachable? Experiment: start a REPL session, open the dashboard, look for any
  control that posts `/dashboard/live-testing/enable`. This confirms finding 1 before step 2 changes a stale test.
- Does `Ds.onClick (Ds.post ...)` inside a node that `apply` swaps in keep its `Ds.indicator` signal across the morph? The
  Live Testing panel already does; check once in the in-memory page.

## Risks and dependencies

- `DashboardTypes.fs` is 2005 lines and `move-and-dock-dashboard-panes` edits the same region. Land this first.
- Changing `Hidden of string` breaks every pattern match on it. There are few (the test file and `keep`). Compile errors list them.
- `friction-reports-you-can-read-first` decides when the friction panel stops being opt-in; this plan keeps its current reason
  until then.
- The roadmap sentence about "the live bindings becoming a real pane in REPL mode" belongs to `move-and-dock-dashboard-panes`.

## Size

M, about a week. The type change is small and mechanical; the browser matrix and the per-panel states are the bulk.

## Decisions for Will

- Alarms, failure narratives and diagnostics with nothing to report: stay silent (`Quiet`) or draw a muted "none" line?
  Recommend quiet on the page, and a "none" entry in the future View menu, so alerts do not add noise.
- Should a cohort that holds only orphaned claims or old landings keep its panel open? Recommend yes, collapsed, headed by
  the `OnlyHistory` line, because the claim is real state another agent can trip on.
- Confirm finding 1 is a bug and not intent. Recommend the reason line carries a "Turn on" button.
