# A command palette and one timeline

Roadmap item: `command-palette-and-one-timeline` (Exploring, Dashboard). Plan written 2026-10-26 against master 59a8692c.

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

- Keys are data: `KeyBindings.all` in `SageFs/Dashboard.fs` (line ~192) lists each binding with `Keys`, `Shown`, `Help`,
  `Effect` and `Confirm`, and `tableJson` feeds the one inline keyboard script. `Ctrl+K` is not bound. The script already
  handles `Ctrl+Tab`, `Ctrl+=`, `Ctrl+-`, and clicks the viewing card's Stop button for confirmed stops.
- The inline JavaScript is capped at 6144 bytes (`DashboardSnapshotTests.fs`, "total inline JS payload is under 6KB"), and a
  second test requires no state management in it. A palette written as script would fight that budget; one written as
  Datastar attributes does not touch it.
- The pattern for a modal is `NewSessionDialogView.fs`: a native `<dialog>` with `Ds.effect` calling `showModal()` from a signal
  (`Signals.NewSessionOpen`), `Ds.preserveAttr "open"`, and a `close` handler folding Esc back into the signal. It debounces typing
  with Datastar's own `250ms` modifier (`SuggestDebounce`).
- The actions are existing routes. `Dashboard.fs` `createEndpoints` maps (lines ~3430 to 3700) among others:
  `/dashboard/session/create`, `/dashboard/reset`, `/dashboard/hard-reset`, `/dashboard/cancel-eval`, `/dashboard/clear-output`,
  `/dashboard/switch-workflow`, `/dashboard/live-testing/enable` and `/disable`, `/dashboard/set-theme`,
  `/dashboard/hygiene/scan` and `/tidy/{plan}`, `/dashboard/session/switch/{id}`, `/dashboard/settings` (page). The palette needs
  no new action routes.
- A scrubber exists for the cohort: `renderCohortScrubControl` (a range input posting `/dashboard/cohort/scrub`),
  `DashboardStreamCommand.SetCohortViewingSeq` on the tab's own channel, and the pure `CohortScrubber`. It is the model for a
  per-tab cursor. It builds its post with a hand-written `@post('...')` string in `Ds.onEvent`; the new cursor should use the
  typed builders.

Timeline side, event by event:

| Kind | Where it is recorded today | What is kept |
|---|---|---|
| Eval | `FeatureHooks.recordEval` into `FeaturePushState.History` (`EvalStore`, bounded to a byte budget and a cell cap), and `CachedTimeline` (`EvalTimeline`, 1000 entries: cell, start, duration, status) | per-session store via `Mcp.recordEvalForSession` (line 178), and a global store for the `/exec` door that has no MCP connection, so two feeds exist |
| Hot reload | `ensureReloadRelay` in `DaemonMode.fs` (~line 3243) receives each worker verdict, posts `SessionManager.ReloadObserved` and triggers `SseEvent.ReloadReported` | only the last verdict, in `SessionInfo.Reload`. No history |
| Test run | `SageFsEffectHandler.fs` (~lines 1109, 1142) dispatches `TuiEvent.TestRunCompleted`; `McpServer.fs` (~1985) emits `test_run_completed` | `LiveTestState` keeps per-test `History` (the previous duration) and `LastTiming`; no list of runs |
| Tweak | `SageFs.Core/Features/Tweak/TweakLog.fs` (an event-sourced `EventLog` with `At: int64` from a "sim clock") and the nudge journal (`NudgeFs.journalPathFor`, used by `LiveBindingsTweakService`) | two stores; I did not trace which is the live one |

- `get_message_journal` (`Mcp.fs` line ~2968) is "synthesized from eval history": it builds a `MessageJournal.Journal` from
  `EvalHistory` code strings with source `"eval"`. There is no real journal of events. `get_eval_timeline` and
  `get_session_filmstrip` read the same eval history. `sagefs.nvim` and `sagefs-vscode` consume only the `eval_timeline` SSE event
  (sparkline stats); neither calls `get_message_journal`.
- `SageFs.Core/TimeTravel.fs` and `ModelSnapshot.fs` implement a ring of immutable model snapshots with step back and forward.
  I found no caller outside their own files, so they are unused. `SageFs.Core/RingBuffer.fs` is the copy-on-write ring (each push
  copies the whole backing array).
- `SseEvent` (`SageFs/SseEvent.fs`) has 20 cases on two channels; none is a timeline entry.

So the sources exist, but only evals have history. Hot-reload verdicts, test runs and tweaks each need a recorder.

## Design

### Palette

```fsharp
type CommandAvailability =
  | Available
  | Unavailable of because: HiddenBecause          // the same case type dashboard-follows-context defines

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
(`Dashboard.fs` line ~749) so a burst keeps the last cursor. The strip is part of the snapshot and the one `#main` morph, drawn
as SVG marks the way `Features.CohortLanes.toSvg` draws lanes: one lane per kind, a distinct shape per kind, each mark labelled
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
