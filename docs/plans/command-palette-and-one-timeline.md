# A command palette and one timeline

Roadmap item: `command-palette-and-one-timeline` (Exploring, Dashboard). Plan written 2026-10-26 against master 59a8692c. Work order added 2026-11-11 against master 8975e898.

## Work order

Two deliverables, one shared idea. The palette is the first half and ships alone. The timeline is the second half and is large; its work order starts at step 7.

### 1. Outcome

Palette: today every dashboard action is a button somewhere on the page or a key in `KeyBindings.all`, and there is no Ctrl+K. After the palette, Ctrl+K or a header button opens a `<dialog>` listing every action, typing narrows it, an action that cannot run now is listed greyed with its reason in the vocabulary the panels already use (`PaneRelevance.ReasonLine`), Enter runs the first match, and a destructive action asks first with the same sentence the key does.

Timeline: today only evals have history (`EvalStore`, `EvalTimeline`); hot-reload verdicts, test runs and tweaks keep at most their last value. After the timeline, one strip per session shows all four kinds on one axis, a per-tab cursor scrubs it, a drawer shows what each entry recorded, and `get_message_journal` reads the same record instead of synthesising one from eval history.

Outcome gates: palette, a headless Chromium journey (`DashboardMobileBrowserTests.fs` technique) that opens the palette by button and by Ctrl+K, types "hot", sees only matching rows, presses Enter and observes the route's effect, presses Esc and finds focus back on the button, at 375, 768 and 1400 px, with zero console and `PatchElementsNoTargetsFound` errors. Timeline, the browser matrix in "What done looks like" plus one assertion per kind inside the journey that already drives that kind. Linux only; the Ctrl+K browser-shortcut collision is unproven in Firefox and in Chromium outside Playwright.

### 2. Where the code is today

Verified by reading on 2026-11-11.

Palette:

- `KeyBindings.all` is `SageFs/Dashboard.fs:230` (not ~192); `Confirmation` `:196`; `tableJson` `:261`; the inline keyboard script is the `sprintf` at `:347`. `Ctrl+K` appears nowhere in `Dashboard.fs` (grep count 0). `Ctrl+Tab` is bound at `:245`.
- The inline JS budget is 6,144 bytes (`SageFs.Tests/DashboardSnapshotTests.fs:858-867`), and `:842-856` forbids state management and GET fetches in inline scripts.
- The modal pattern is `SageFs/NewSessionDialogView.fs`: `Signals.NewSessionOpen` (`:30-45`), `SuggestDebounce = "250ms"` (`:26`), `showModal()` from a `Ds.effect` (`:36`).
- `createEndpoints` is `Dashboard.fs:3848`; the action routes are literal strings at `:3932-4130` (`/dashboard/eval`, `/reset`, `/hard-reset`, `/cancel-eval`, `/switch-workflow`, `/clear-output`, `/hygiene/scan`, `/set-theme`, `/live-testing/enable`, `/disable`, `/session/create`, `/session/stop-others`, `/layout`, `/cohort/scrub`, `/cohort/seat`, and more). No `DashboardRoutes.fs` exists; the literals are inline.
- `dashboard-follows-context` is Done. Its vocabulary is `PaneRelevance = OnPage | ReasonLine of name | NothingToShow of why` (`SageFs/PaneMenuView.fs:21-27`) computed by `relevanceOf (facts: PanelFacts) (pane: PanelId)` (`:76`); `PanelFacts` is built at `Dashboard.fs:1057`. The plan's `HiddenBecause` does not exist; use `PaneRelevance`.
- `move-and-dock-dashboard-panes` is In progress (step 3 of 8): the six sidebar panels already move, close, reopen and reset from a Panels menu (`PaneMenuView.fs`), so `ShowPane`/`ClosePane`/`ResetLayout` have routes to call (`/dashboard/layout`, `:4103`).
- The per-tab scrub model: `renderCohortScrubControl` (`SageFs/DashboardFragments.fs:3776`), `DashboardStreamCommand.SetCohortViewingSeq` (`SageFs/DashboardTypes.fs:1646`), `StreamBurst.add` (`Dashboard.fs:792`), `Features.CohortScrubber` (`SageFs.Core/Features/CohortScrubber.fs`).

Timeline:

- Eval: `Mcp.recordEvalForSession` (`SageFs/Mcp.fs:126`, called at `:1170`) into `FeatureHooks.recordEvalWithCost`; `CachedTimeline: EvalTimeline.TimelineState` (`SageFs.Core/Features/FeatureHooks.fs:25`, recorded at `:79`). `getEvalTimeline` `Mcp.fs:2980`. `getMessageJournal` is `Mcp.fs:2943`, comment "synthesized from eval history" at `:2942`; the tool is `McpTools.fs:2297`.
- Hot reload: `ensureReloadRelay` (`SageFs/DaemonMode.fs:3775`) posts `SessionCommand.ReloadObserved` (`SageFs.Core/SessionManager.fs:112`, handled `:1254`) and raises `ReloadReported`. Last verdict only.
- Test run: `TuiEvent.TestRunCompleted` dispatched at `SageFs/SageFsEffectHandler.fs:1157` and `:1190`; `formatTestRunCompletedEvent` at `McpServer.fs:2230`.
- Tweak: `TweakLog.LoggedEvent = { Id; At: int64; Event }` (`SageFs.Core/Features/Tweak/TweakLog.fs:195`), `At` is a sim-clock reading (`:64-66`); the journal path is `NudgeFs.journalPathFor` wired at `SageFs/EvalWriteBackWire.fs:94`; `SageFs/LiveBindingsTweakService.fs` exists. Which store is live is still untraced.
- `SageFs/SseEvent.fs` has 22 cases (not 20). `SageFs.Core/TimeTravel.fs` and `ModelSnapshot.fs` have no caller outside their own files (grep); `RingBuffer.fs` exists.
- Lanes: `Features.CohortLanes.project : LedgerEntry list -> LaneModel` (`SageFs.Core/Features/CohortLanes.fs:224`); the SVG is drawn by `CohortTerritory.toSvg` (named in `CohortLanes.fs:294`), not `CohortLanes.toSvg`.
- Simulation building blocks: `SageFs.Simulation/FaultInjection.fs` (fsproj `:50`), `RetentionSim.fs` and `RetentionSimInvariants.fs` (`:265-266`), tests `SageFs.Tests/RetentionSimDstTests.fs`.
- Browser journeys named in the plan all exist: `DashboardMobileBrowserTests.fs`, `HotReloadBrowserTests.fs`, `LiveTestingBrowserTests.fs`, `DashboardLiveBindingsTweakBrowserTests.fs`.
- Tool list budget: `RatchetBudgets.toolList` is 38,286 characters for the full catalog (`SageFs.Tests/RatchetBudgets.fs:462`). A longer `get_message_journal` description fails `ToolListBudgetTests`; the new description must be no longer than the old one or the budget is raised in the same commit with the reason, as the `:455-461` comment records earlier raises.

### 3. Steps

Palette:

1. Measure (one hour). Render one node with the typed `Ds.onEvent` and a `__window` modifier and read the attribute; if it passes through, Ctrl+K costs no inline bytes. Record the current inline JS byte count from the `DashboardSnapshotTests` message. Open the dashboard in a real Firefox and Chromium (headless screenshot only, per house rule) and note whether Ctrl+K reaches the page.
2. RED, pure. `SageFs.Tests/DashboardCommandsTests.fs`, list `DashboardCommands`: "WHY - every keyboard action that is not ClientOnly is a palette command with the same confirmation". Register in `SageFs.Tests/SageFs.Tests.fsproj` next to `DashboardSnapshotTests.fs` (line 239). Fails to compile. Commit red.
3. GREEN, pure. `SageFs/DashboardCommands.fs`: `DashboardCommand`, `CommandAvailability = Available | Unavailable of PaneRelevance`, `catalogue : PanelFacts -> SessionInfo list -> Command list`, `Invoke = PostRoute of string | OpenSignal of string | Navigate of string`. Add to `SageFs/SageFs.fsproj` before `Dashboard.fs`. The property "catalogue is total and every Unavailable has a non-empty reason".
4. Edge. `SageFs/PaletteView.fs`: the `<dialog>`, `Signals.PaletteOpen`, `Signals.PaletteQuery`, one `<button>` per command with `Ds.show` over the lowercased search string, Enter through a `Ds.onEvent` expression. Snapshot test in `DashboardSnapshotTests.fs` that the dialog is in the shell and the JS budget still holds.
5. Edge. The header "Commands" button and Ctrl+K: a `KeyBindings` row with `Effect.ClientOnly` if step 1 found the attribute route, else about 100 bytes in the keyboard script at `Dashboard.fs:347`. `dashboard.css` rows.
6. Browser journey (the palette gate above) in `DashboardMobileBrowserTests.fs` style, run in `--integration-browser`.

Timeline:

7. Measure (half a day). Count producer events during one `HotReloadBrowserTests` and one `LiveTestingBrowserTests` run; read `LiveBindingsTweakService.fs` and `EvalWriteBackWire.fs` and write down which tweak store is live and whether its `At` is wall time; run one eval through `/exec` and one through MCP in a test host and count `recordEvalForSession` calls.
8. RED, pure. `SageFs.Tests/SessionTimelineTests.fs`, list `SessionTimeline`: "WHY - an eval, a reload, a test run and a tweak recorded out of order come back in sequence order, each once", plus the capacity and `Evicted` properties. Commit red.
9. GREEN, pure. `SageFs.Core/Features/SessionTimeline.fs` with the types in Design (`TimelineKind`, `TimelineEntry`, `TimelineCursor`, `Resolved`), a single-writer ring. Add to `SageFs.Core.fsproj` after `EvalTimeline.fs`.
10. `SageFs.Core/Features/TimelineOwner.fs` in the `CohortOwner.fs` style (mailbox, published immutable snapshot), and `SageFs.Simulation/SessionTimelineSim.fs` with invariants (a) to (e) and the double-door twin; registered in both fsproj files.
11. Producer hooks, one commit each, file-disjoint: `Mcp.fs:126` (eval, deduplicated with the `/exec` door by session and cell), `DaemonMode.fs:3775` (reload), `SageFsEffectHandler.fs:1157` and `:1190` (test run), the tweak completion in `LiveBindingsTweakService.fs`. Each adds one call and one test through its own door.
12. `DashboardStreamCommand.SetTimelineCursor` beside `SetCohortViewingSeq` (`DashboardTypes.fs:1646`), a case in `StreamBurst.add` (`Dashboard.fs:792`), `SageFs/TimelineView.fs` (SVG marks with a shape per kind, labelled; range input with arrow keys; drawer), and the `#main` morph.
13. `getMessageJournal` (`Mcp.fs:2943`) reads the timeline; description in `McpTools.fs:2297` and `docs/mcp-tools.md` updated; trailer `Plugin: n/a no client reads this tool`. Per-kind browser assertions in the four journeys.

DECISION: split the roadmap item in two (palette, timeline). Recommendation: yes, before step 7.
DECISION: scrub is inspect-only for v1. Recommendation: yes; state restore is its own item.
DECISION: Ctrl+K only, plus the button. Recommendation: yes.
DECISION: in memory per session (recommended) or persisted beside the tweak log.

### 4. How to check it

- Inner loop: a session on `SageFs.Tests`; `send_fsharp_code` the `catalogue` property and the ring cases before writing files.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets` (JS budget, tool list, type scale, glyphs).
- Filtered: `... --summary --filter-test-list "DashboardCommands"`, `"SessionTimeline"`, `"Dashboard snapshot"`.
- Acceptance: unfiltered `--summary` (TRUST `Trusted`); `--integration-browser --summary` for the palette and strip journeys; `--integration-hr` and `--integration-lt` for the reload and run entries.

### 5. Traps in this repo

- Budgets only go down: the 6,144-byte inline JS cap, `RatchetBudgets.toolList`, `DashboardTypeScaleTests` (rem/em only, no px), `DashboardGlyphTests` (monochrome glyphs from the closed `Glyph` type, no emoji).
- Theme tokens: pick colours by meaning; a kind is told apart by shape and a word, never by colour alone.
- The "one morph, one `#main`" invariant: the dialog and the strip are part of the shell every render state composes, or the SSE morph throws `PatchElementsNoTargetsFound`.
- SAGE-005: `catalogue` takes `PanelFacts` and a typed list; `TimelineEntry` carries `SessionId` and a unit-of-measure seq, never two `int64`s side by side.
- No `let mutable` in the pure ring: the owner holds the mutable state at the edge.
- Timing: browser waits are `waitForSSE` on a morph, never a sleep; the simulation injects its clock.
- `Mcp.fs` and `DaemonMode.fs` are the largest files in the repo and in `RatchetBudgets.fileSize`; a hook is one call, and the budget is not raised.
- `McpTools.fs` and `docs/mcp-tools.md` are wire-surface files: those commits carry `Plugin:` trailers.
- Commit `.fsproj` files whole. Linux only.

### 6. Size and order

Palette, steps 1 to 6: about a week. Timeline, steps 7 to 13: three weeks or more. Step 2 first; 3 and 4 serial; 5 after 1. Step 11's four hooks can run in parallel once step 9's types are committed. First commit: `test(palette): RED every keyboard action that is not client-only is a palette command` with `DashboardCommandsTests.fs` and the fsproj line.

### 7. Not now

- State restore by scrubbing (`TimeTravel.fs`, `ModelSnapshot.fs`): unused code; a separate item with its own measurement of `SageFsModel` sharing.
- The live bindings and app output as movable panes: `move-and-dock-dashboard-panes`, in progress.
- Ctrl+Shift+P and other bindings: only if someone asks.
- Persisting the timeline across daemon restarts.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | palette M, timeline L |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: every keyboard action is a palette command. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

The item is two things with one shared idea (the dashboard's actions and events as data). They can ship separately: the palette
is small and should go first, the timeline is large.

## What done looks like

Ctrl+K (and a visible button) opens a palette listing every dashboard action. Typing narrows the list. An action that cannot run
right now is listed greyed out with the reason in one line, in the vocabulary of `dashboard-follows-context`. Enter runs the
first match, and a destructive action asks first, as the keyboard shortcuts do. Separately, a timeline strip shows one
session's evals, hot-reload saves, test runs and tweaks on one axis, each kind distinguishable without colour. You can drag a
cursor along it, or use the arrow keys, and a detail drawer shows what that entry recorded. "Back to live" returns. The cursor
is yours: another tab keeps following live. The outcome gate is a browser matrix over {empty timeline, one kind, all four kinds,
a ring that has wrapped} x {live, scrubbed to the middle, scrubbed to an evicted entry} x {375, 768, 1400 px}, plus, for each kind,
an assertion inside the browser journey that already drives that kind.

## What exists today

Verified by reading the code.

Palette side:

- Keys are data: `KeyBindings.all` in `SageFs/Dashboard.fs` (line 230 on 2026-11-11) lists each binding with `Keys`, `Shown`, `Help`,
  `Effect` and `Confirm`, and `tableJson` feeds the one inline keyboard script. `Ctrl+K` is not bound. The script already
  handles `Ctrl+Tab`, `Ctrl+=`, `Ctrl+-`, and clicks the viewing card's Stop button for confirmed stops.
- The inline JavaScript is capped at 6144 bytes (`DashboardSnapshotTests.fs`, "total inline JS payload is under 6KB"), and a
  second test requires no state management in it. A palette written as script would fight that budget; one written as
  Datastar attributes does not touch it.
- The pattern for a modal is `NewSessionDialogView.fs`: a native `<dialog>` with `Ds.effect` calling `showModal()` from a signal
  (`Signals.NewSessionOpen`), `Ds.preserveAttr "open"`, and a `close` handler folding Esc back into the signal. It debounces typing
  with Datastar's own `250ms` modifier (`SuggestDebounce`).
- The actions are existing routes. `Dashboard.fs` `createEndpoints` (line 3848 on 2026-11-11) maps (lines 3932 to 4130) among others:
  `/dashboard/session/create`, `/dashboard/reset`, `/dashboard/hard-reset`, `/dashboard/cancel-eval`, `/dashboard/clear-output`,
  `/dashboard/switch-workflow`, `/dashboard/live-testing/enable` and `/disable`, `/dashboard/set-theme`,
  `/dashboard/hygiene/scan` and `/tidy/{plan}`, `/dashboard/session/switch/{id}`, `/dashboard/settings` (page). The palette needs
  no new action routes.
- A scrubber exists for the cohort: `renderCohortScrubControl` (`SageFs/DashboardFragments.fs:3776`, a range input posting `/dashboard/cohort/scrub`),
  `DashboardStreamCommand.SetCohortViewingSeq` (`SageFs/DashboardTypes.fs:1646`) on the tab's own channel, and the pure `CohortScrubber` (`SageFs.Core/Features/CohortScrubber.fs`). It is the model for a
  per-tab cursor. It builds its post with a hand-written `@post('...')` string in `Ds.onEvent`; the new cursor should use the
  typed builders.

Timeline side, event by event:

| Kind | Where it is recorded today | What is kept |
|---|---|---|
| Eval | `FeatureHooks.recordEvalWithCost` into `FeaturePushState.History` (`EvalStore`, bounded to a byte budget and a cell cap), and `CachedTimeline` (`EvalTimeline`, `FeatureHooks.fs:25`, `:79`) | per-session store via `Mcp.recordEvalForSession` (`Mcp.fs:126`), and a global store for the `/exec` door that has no MCP connection, so two feeds exist |
| Hot reload | `ensureReloadRelay` in `DaemonMode.fs` (line 3775) receives each worker verdict, posts `SessionManager.ReloadObserved` (`SessionManager.fs:112`) and triggers `ReloadReported` | only the last verdict, in `SessionInfo.Reload`. No history |
| Test run | `SageFsEffectHandler.fs` (lines 1157, 1190) dispatches `TuiEvent.TestRunCompleted`; `McpServer.fs:2230` emits `test_run_completed` | `LiveTestState` keeps per-test `History` (the previous duration) and `LastTiming`; no list of runs |
| Tweak | `SageFs.Core/Features/Tweak/TweakLog.fs` (an event-sourced `EventLog` with `At: int64` from a "sim clock") and the nudge journal (`NudgeFs.journalPathFor`, used by `LiveBindingsTweakService`) | two stores; I did not trace which is the live one |

- `get_message_journal` (`Mcp.fs:2943`, tool at `McpTools.fs:2297`) is "synthesized from eval history": it builds a `MessageJournal.Journal` from
  `EvalHistory` code strings with source `"eval"`. There is no real journal of events. `get_eval_timeline` and
  `get_session_filmstrip` read the same eval history. `sagefs.nvim` and `sagefs-vscode` consume only the `eval_timeline` SSE event
  (sparkline stats); neither calls `get_message_journal`.
- `SageFs.Core/TimeTravel.fs` and `ModelSnapshot.fs` implement a ring of immutable model snapshots with step back and forward.
  I found no caller outside their own files, so they are unused. `SageFs.Core/RingBuffer.fs` is the copy-on-write ring (each push
  copies the whole backing array).
- `SseEvent` (`SageFs/SseEvent.fs`) has 22 cases on two channels (counted 2026-11-11); none is a timeline entry.

So the sources exist, but only evals have history. Hot-reload verdicts, test runs and tweaks each need a recorder.

## Design

### Palette

```fsharp
type CommandAvailability =
  | Available
  | Unavailable of because: PaneRelevance          // the case type dashboard-follows-context built (SageFs/PaneMenuView.fs:21)

type Confirmation = NoPrompt | AskFirst of prompt: string      // reuse KeyBindings.Confirmation

type DashboardCommand =
  | NewSession | StopViewingSession | ResetSession | HardResetSession | CancelEval | ClearOutput
  | SwitchWorkflowTo of SessionWorkflow | EnableLiveTesting | DisableLiveTesting
  | SwitchToSession of SessionId | OpenSettings | OpenEvaluate | SetTheme of themeName: string
  | ScanWorkspace | TidyWorkspace
  | ShowPane of PaneId | ClosePane of PaneId | ResetLayout          // from move-and-dock-dashboard-panes
  | OpenFrictionPreview                                            // from friction-reports-you-can-read-first
```

`DashboardCommands.catalogue : PanelFacts -> SessionInfo list -> Command list` is pure and exhaustive. Each `Command` carries
a label, a group, search keywords, availability, a confirmation and an `Invoke`: `PostRoute of string` (a `DashboardRoutes`
literal), `OpenSignal of signalName`, or `Navigate of href`. A test requires every `KeyBindings.Effect` other than `ClientOnly`
to have a command with the same confirmation, so the two lists cannot drift.

Render: the palette is a `<dialog id="palette">` in the shell, always present, driven by `Signals.PaletteOpen`, with
`Ds.effect` for `showModal()`, an input bound to `Signals.PaletteQuery`, and one `<button>` per command. Filtering is client side
and instant: each row has `Ds.show` over a search string the server rendered (label plus keywords, lowercased) and the query.
Availability is server truth in the morph, so a command that becomes unavailable updates with the next push; the query is a
signal, so a morph never clears it. Enter runs the first visible, enabled row through a short `Ds.onEvent` expression (an
attribute, so the inline script budget is untouched). Ctrl+K needs one line in the keyboard script (about 100 bytes) that clicks a visible
"Commands" button, the same trick the stop shortcut uses, or a `Ds.onEvent` with the `__window` modifier if the typed builder
passes it through (measure first).

### Timeline

New vertical slice in `SageFs.Core/Features/SessionTimeline.fs` (pure) plus `TimelineOwner.fs` (a mailbox owner in the style
of `CohortOwner.fs`/`ManifestOwner.fs`, publishing an immutable snapshot that readers dereference without touching the mailbox).

```fsharp
type TimelineKind =
  | EvalRan of cell: int * outcome: EvalStatus * durationMs: int64
  | HotReloadSaved of file: string * verdict: ReloadCase * patched: int * considered: int
  | TestRunFinished of passed: int * failed: int * skipped: int * durationMs: int64
  | TweakWritten of binding: string * undone: UndoState        // UndoState = Applied | Undone | RolledBack

type TimelineEntry = { Seq: int64<timelineSeq>; At: DateTimeOffset; Session: SessionId; Kind: TimelineKind }

type TimelineCursor = AtLive | AtEntry of Seq: int64<timelineSeq>      // per tab, a DU not an option
type Resolved = Live | Entry of TimelineEntry | Evicted of Seq: int64<timelineSeq> * oldestKept: int64<timelineSeq>
```

An entry holds identifiers and small numbers and carries no payload. The detail drawer asks the owner of the payload (`EvalStore` by
cell, `SessionInfo.Reload` or the stored facts for a save, the test summary, the tweak log), so the timeline never duplicates
what is already bounded elsewhere. The ring is per session, capacity 2000, newest wins; `Evicted` is a real answer, so a cursor
on an evicted entry says so instead of showing a wrong one.

Producers, four small hooks, one per file: eval at `Mcp.recordEvalForSession` and its `/exec` twin (deduplicated by session and
cell id); hot reload at `ensureReloadRelay`; test run at the `TestRunCompleted` dispatch; tweak at the nudge completion. Each
posts a command to the owner; none blocks the producer.

Scrub means "inspect an entry". It does not restore state. Restoring REPL bindings at a past moment needs snapshots the product does
not take, and `TimeTravel.fs` is unused. The honest v1 shows, per kind, what was recorded (eval code, result and the existing
`EvalDiff` against the previous eval; the reload verdict, declarations and suggested action; the run tally and failing tests;
the tweak's before and after). Whether to go further is a decision below.

The cursor is per tab: `DashboardStreamCommand.SetTimelineCursor` beside `SetCohortViewingSeq`, with a case in `StreamBurst.add`
(`Dashboard.fs:792`) so a burst keeps the last cursor. The strip is part of the snapshot and the one `#main` morph, drawn
as SVG marks the way `CohortTerritory.toSvg` draws the lanes `Features.CohortLanes.project` builds: one lane per kind, a distinct shape per kind, each mark labelled
for screen readers, a range input with arrow-key stepping. At 480 px and below the strip scrolls horizontally inside its own
box and the drawer stacks under it.

`get_message_journal` is rewritten to read this timeline. Same tool, same arguments, so no new tool and no catalogue growth
(the catalogue is about 28,000 tokens already); only its description changes, from "synthesized from eval history" to the
real thing. The `eval_timeline` SSE event the editors read is unchanged.

Persistence: in memory, for the life of the session. A daemon restart empties it. Nothing is added to the binary manifest.

Wire and clients: the palette and strip are dashboard-only. The `get_message_journal` change touches `Mcp.fs` and
`docs/mcp-tools.md` (a wire-surface file by the repo's list), so those commits carry `Plugin: n/a no client reads this tool`
(I grepped `sagefs.nvim/lua` and `sagefs-vscode/src` and found no use), and `DashboardTypes.fs` commits carry
`Plugin: n/a dashboard markup only`. `apiVersion` stays 4: the tool's text gains entries, it loses none.

Files: new `DashboardCommands.fs`, `PaletteView.fs`, `SessionTimeline.fs`, `TimelineOwner.fs`, `TimelineView.fs`,
`DashboardRoutes.fs` (shared with the context plan); changed `Dashboard.fs`, `DashboardTypes.fs`, `DashboardFragments.fs`
(shell), `Mcp.fs`, `DaemonMode.fs`, `SageFsEffectHandler.fs`, `LiveBindingsTweakService.fs`, `dashboard.css`.

## Tests

RED first.

1. `DashboardCommandsTests`: "every keyboard action that is not client-only is a palette command with the same confirmation".
   Compile-red.
2. `SessionTimelineTests`: "an eval, a reload, a test run and a tweak recorded out of order come back in sequence order, each
   once". Compile-red.
3. Properties: capacity is never exceeded and the newest N are kept; `Resolved` of an evicted seq is `Evicted`, never another
   entry; `catalogue` is total over generated facts and every `Unavailable` has a non-empty reason.
4. Palette outcome: Playwright types "hot" and sees only matching rows, presses Enter and the action's route is hit (a
   loopback request log or the effect on the page), and Esc returns focus to the button that opened it.
5. Mutation: a `Mutant` that drops the confirmation, one that drops the dedupe.

Simulation (`SageFs.Simulation/SessionTimelineSim.fs`, `SessionTimelineSimInvariants.fs`, tests `SessionTimelineSimDstTests.fs`,
after `RetentionSim.fs`). Pool: two sessions, four producers, two tabs. Ops: the four producer events with reorder and
duplicate faults (`FaultInjection.fs`), `EvalViaExecDoor` and `EvalViaMcpDoor` for the same cell, `SessionStopped`, `TabScrubs`,
`TabGoesLive`, `RingFills`. Invariants: (a) sequence numbers are strictly increasing in storage order; (b) the ring holds the
newest `capacity` entries with no gap at the new end; (c) one producer event gives exactly one entry; (d) a tab's cursor never
moves because another tab's did; (e) an `AtEntry` cursor resolves to that entry or to `Evicted`. Twin: record on both eval doors
without the dedupe, which violates (c). Clock injected, no waits.

Outcome gates: the in-memory browser matrix above (`DashboardMobileBrowserTests.fs` technique). Per kind, an extra assertion in the
journey that already drives it: the eval entry in the browser dashboard journey, the reload entry in `HotReloadBrowserTests.fs`
(`--integration-hr`), the run entry in `LiveTestingBrowserTests.fs` (`--integration-lt`), the tweak entry in
`DashboardLiveBindingsTweakBrowserTests.fs`. Each waits on an SSE morph (`waitForSSE`), never a sleep, and checks zero console
and `PatchElementsNoTargetsFound` errors.

## Steps

Palette (first, about a week):

1. RED test 1. `DashboardCommands.fs` and `DashboardRoutes.fs` literals. Owns those files and `DashboardCommandsTests.fs`.
2. `PaletteView.fs`: dialog, query signal, rows, Enter. Owns `PaletteView.fs`, `dashboard.css`.
3. Ctrl+K and the header button. Owns the keyboard script and `KeyBindings` in `Dashboard.fs`.
4. Availability from `decisions`; browser journey. Needs the context plan.

Timeline (after the palette):

5. RED test 2 and the pure `SessionTimeline.fs` with properties. Owns the new file and tests.
6. `TimelineOwner.fs` and the simulation with its twin. Parallel with 5 once the types exist.
7. Producer hooks, one per sub-agent, file-disjoint: `Mcp.fs`, `DaemonMode.fs`, `SageFsEffectHandler.fs`,
   `LiveBindingsTweakService.fs`. Each adds a recorder call and a test through its own door.
8. `SetTimelineCursor`, the strip and the drawer. Owns `TimelineView.fs`, `Dashboard.fs` (stream region), `DashboardTypes.fs`.
9. `get_message_journal` reads the timeline; docs line; per-kind browser assertions.

## Measure first

- Does the typed `Ds.onEvent` pass a `__window` modifier through to `data-on:keydown__window`? Experiment: render one node and
  read the attribute. If it does, Ctrl+K costs no inline bytes. Also confirm in real Chromium and Firefox windows that
  `preventDefault` on Ctrl+K stops the address-bar focus; Playwright dispatches to the page, so it cannot show this. I have not
  verified it.
- How often do producers fire and how big is a ring? Experiment: count entries during the existing live-testing and hot-reload
  journeys, and measure the retained size of 2000 entries. `RingBuffer.push` copies the whole backing array per push; at 2000
  entries that is a 16 KB copy per event, probably fine at a few events per second, and a single-writer mutable ring in the
  owner is the fallback.
- Is the eval double feed real? Run one eval through `/exec` and one through MCP in a test host and count timeline calls.
- Which tweak store is live, and is its `At` wall time or a simulated clock? Read `LiveBindingsTweakService.fs` and
  `EvalWriteBackWire.fs`; the timeline needs real timestamps.
- Does `SageFsModel` structural sharing make a snapshot ring affordable? Only needed if Will wants state scrubbing.

## Risks and dependencies

- Ctrl+K collides with browser shortcuts (Firefox search, Chromium omnibox on some platforms). The visible button is the
  fallback.
- A timeline that is empty after every daemon restart may disappoint; say so in the drawer ("kept for this session only").
- The producer hooks sit in the largest files in the repo (`Mcp.fs`, `DaemonMode.fs`); keep each hook to a call and a test.
- Depends on `dashboard-follows-context` (availability reasons) and `move-and-dock-dashboard-panes` (pane commands, the strip as
  a pane). `friction-reports-you-can-read-first` supplies one command.

## Size

Palette M (about a week). Timeline L (three weeks or more: four recorders, an owner, a strip, a drawer, a simulation). Overall L;
splitting the roadmap item in two is cleaner.

## Decisions for Will

- Split into two roadmap items, palette first? Recommend yes.
- Scrub as inspect-only for v1, or invest in state snapshots? Recommend inspect-only; state restore is a separate item with its
  own measurement.
- Ctrl+K only, or also Ctrl+Shift+P? Recommend Ctrl+K plus the button, and add the second only if someone asks.
- Keep the timeline in memory per session (recommended), or persist it beside the tweak log?
