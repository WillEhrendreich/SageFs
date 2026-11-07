# Level the REPL after a patch without losing it

Roadmap item: `level-the-repl-without-losing-it` (Next, hot reload). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress (steps 1 to 8 built; the outcome gate's in-flight cell and the large-solution reset time are open) |
| Size | M |
| Branch and worktree | `worktree-agent-a9111e89c20aeb7f4`, `/home/will/Work/SageFs/.claude/worktrees/agent-a9111e89c20aeb7f4` |
| Last green commit | the commit that carries this block: `dotnet SageFs.Tests.dll --summary` (unfiltered) 15060 of 15060, `--ratchets` 517 of 517, both Trusted. The host tier ran only the three `run_app repl leveling` cells on net11 (green); the net10 cells need the net10 host built (`dotnet build build/GateProducts.proj -c Release`) and were not run here. |
| Next action | Run the host tier's `run_app repl leveling` cells on a machine that is not loaded (they were run once, filtered, here). Add the in-flight cell: it needs a fixture project with a test that blocks until released, so a `RunTests` is in flight while `LevelRepl` is asked. Measure the host reset on a large solution (Measure first, item 2). Then set Status to Done with the landmark `SageFs.Core/ReplLeveling.fs` `ReplLeveling`. |
| Decisions made | 2026-11-07, the agent on this item, from the plan's recommendations: defer-only (no replay of definitions); `Leveling` and `CannotLevel` are `BehindApp` on the wire, with an additive `blockedBy` array; leveling is always on, with no setting. A level waits for a test run in flight instead of answering `TestsRunning` and being retried: the worker's gate holds the level until the last run leaves, so no timer and no second channel from the worker to the daemon are needed. |
| Surprises | See "What changed from the plan" below. |

## What done looks like

After a metadata delta lands in a `run_app` app, the session brings its FSI host up to the patched build in the background
whenever that loses nothing: the REPL holds none of your definitions, no init script defined anything, no test run is in
flight and no eval is running. The app keeps its process id and its state. Live testing's coverage maps and discovery are
fetched again before the session says it is level. The session stays loudly `BehindApp` until that has finished, and when it
cannot level it says which of those four things stopped it and what to do.

The outcome gate extends the `run_app repl freshness` case in `SageFs.Tests/RunAppDeltaTests.fs`. Matrix: runtime (net10,
net11) by REPL contents (untouched; one user definition; a value defined by `.SageFs/init.fsx`) by test run (idle; in flight)
by app (running). Expected: untouched and idle levels by itself and the REPL answers the patched body with the same pid;
a user definition or an init-script value never levels and the reason names it; a run in flight finishes first and leveling
follows its end. No cell skips.

## What exists today

- `ReplFreshness` (`SageFs.Core/SessionReload.fs`): `InSync | BehindApp of savesSince * declarations | Unknown`.
  `ReplFreshness.observe` moves it on every reload report with the metadata-delta mechanism. `ReplFreshness.afterWorkerSwap`
  clears it, and only `SessionManager` line ~500 (a worker replaced) calls that. I found no code that clears it after an
  in-worker FSI host reset, so the editor's `RequestHardReset` (`SageFs/SageFsEffectHandler.fs`) leaves the banner up after a
  successful reset. Measurement 3 confirms.
- The folding happens in `SessionManager` at `SessionCommand.ReloadObserved` (`Freshness = ReplFreshness.observe ...`). That
  arm is the natural trigger.
- The worker can level the host already. `WorkerMessage.HardResetSession(false, rid)` reaches `AppState.fs` where the router
  (`HardResetSession` arm, ~line 1928) first cancels the running eval and interrupts its thread, then posts `EvalHardReset`.
  The `EvalHardReset` arm (~line 1476) disposes the FSI session, makes new shadow copies, re-instruments and builds a new
  session. The test `the REPL answers the build from before a delta, and the worker's own reset of its FSI host brings it level`
  in `RunAppDeltaTests.fs` measured 2.5 and 2.7 s on net10 and 2.1 and 2.0 s on net11 on the tiny fixture, with the pid
  unchanged. I have no number for a large solution.
- The daemon already fetches live testing state again after a host swap: `SessionRefetch.postRefetchLiveTestingStateAfterReset`,
  gated by `WorkerPostReady.RefetchGate.decide` (refuses a session whose worker is not `Ready` or has a swap parked) and
  `judgeReply` (drops an answer from a replaced worker). `SwapRefetchTests.fs` and `WorkerPostReadyRefetchTests.fs` pin it.
  The refetch is fire-and-forget (`refetch id handle.Pid session.Proxy`); nothing tells the caller it finished.
- Facts the actor owns: `evalStats.EvalCount` (`SageFs.Core/Affordances.fs`), `StartupConfig.StartupProfileLoaded : string option`
  (`AppState.fs` ~line 84) and the `SessionActivity` of the phase (`EvalActorDecision.fs`: `Idle | Evaluating | Cancelling |
  HostCrashed`). The pure router is `EvalActorDecision.decide`, and `SageFs.Simulation/EvalActorSim.fs` folds it.
- Test runs execute in the host through the worker: `WorkerMessage.RunTests` in `SageFs.Host/WorkerMain.fs` (~line 288) calls
  `TestOrchestrator.executeFiltered`. Nothing counts runs in flight. `latestDynamicRunTest` (~line 747) points at tests an
  `EvalLiveTestFile` defined in the current host; I did not check whether a reset clears it.
- `docs/decisions.md` ("The REPL is behind the app after a delta, and the session says so...") names the missing pieces: the
  actor-side idle-and-empty check and the host's in-flight test count. The refetch half is done.

## Design

Facts and verdict, pure, in a new file `SageFs.Core/ReplLeveling.fs` compiled before `AppState.fs` (the same reason
`EvalActorDecision.fs` is its own file, and `AppState.fs` has a line budget in `ArchitectureTests`):

```fsharp
type LevelBlocker =                       // closed set, one describe, one wire token
  | EvalRunning of SessionActivity
  | ReplHoldsDefinitions of evalCount: int
  | InitScriptDefined of path: string
  | LiveTestsDefined                      // latestDynamicRunTest is set
  | TestsRunning of count: int
  | SwapInProgress
  | GateDriven                            // the cohort gate drives this session; see the risks

type LevelVerdict =
  | Level
  | Deferred of LevelBlocker list         // never empty

type LevelFacts =
  { Activity: SessionActivity
    EvalCount: int
    StartupProfile: StartupProfile
    DynamicTests: DynamicTests
    TestRuns: TestRunGate.Standing }
```

`StartupProfile` and `DynamicTests` are two-case DUs (`NoneLoaded | Loaded of path`), because the existing `string option` is
the Option-as-state the house rules forbid; the actor's `StartupConfig` field converts at the edge.

Closing the race. The check and the reset run in one actor message, `EvalLevel`, so no eval can start between them.
The router must not cancel anything for it: the point of leveling is that nothing was running. Tests need the same
guarantee, so `TestRunGate` in the worker is a small state machine, `Idle | Running of int | Leveling`:

- `RunTests`, `EvalLiveTestFile` and `DebugTestBegin` enter the gate. While the gate is `Leveling` they await its completion
  event, so a test run waits two seconds at worst and is never killed.
- `EvalLevel` asks the gate to move `Idle -> Leveling`. A gate in `Running n` answers `TestsRunning n` and the actor
  answers `Deferred`. When the last run leaves, the gate raises one event (`TestRunsDrained`) which the worker forwards, and
  the daemon asks again. No timer.

Daemon side, in `SessionManager`:

- On `ReloadObserved` that leaves the session `BehindApp` with a `Ready` worker and no swap parked, post `LevelRepl`.
  A faulted, restarting or cohort-driven session is never leveled (see risks).
- `LevelRepl` sends a new worker message `WorkerMessage.LevelRepl(rid)`. The reply is `Leveled | Deferred of blockers`.
- On `Leveled`, the daemon runs the refetch and waits for both answers (discovery and maps). The state is
  `ReplFreshness.Leveling` internally until both arrive from the worker pid that was asked, then `InSync`. A failed refetch
  makes it `Unknown` with the reason, never `InSync`.
- On `Deferred`, the state stays `BehindApp` and carries the blockers; a `TestsRunning` deferral is retried on
  `TestRunsDrained`, the others wait for something the user does.

Wire and clients. `ReplFreshness.toWire` keeps its three states. `Leveling` maps to `BehindApp` on the wire with a different
`message` ("being brought level, about 2 seconds"), and `BehindApp` gains an additive `blockedBy` array of the blocker tokens.
That keeps `sagefs.nvim` (`lua/sagefs/closed_set.lua`, `repl_freshness.STATE`) and `sagefs-vscode`
(`ReloadReportPure.fs`, `BehindApp of ...`) working unchanged. Adding a fourth wire state would force both clients first and
move `apiVersion` (VS Code requires an exact match). The trade is that a client cannot draw a spinner. I recommend the
additive route now. The commit carries `Plugin: done <show blockedBy in the banner>` or `Plugin: n/a` after nvim is checked.
The dashboard card line (`DashboardFragments.fs`, `ReplFreshness.banner`) shows the blockers.

Token cost: no new tool. The banner text that `get_session_status`, `send_fsharp_code` and others append grows by one short
sentence while blocked. The `hard_reset_fsi_session` description gains "a rebuild-free reset is automatic when nothing would
be lost", about 30 tokens.

## Tests

RED first:

1. `SageFs.Tests/ReplLevelingTests.fs`, "an idle REPL with no evals, no init script, no dynamic tests and no runs levels";
   plus one case per blocker. Compile-red today (no `ReplLeveling` module). A table-driven property: for any facts,
   `Level` iff the blocker list is empty.
2. `SessionManagerLevelOutcomeTests.fs`, built like `SessionManagerRebuildOutcomeTests.fs` on the `mkRuntime` fake:
   "a delta report on a Ready worker makes the manager ask the worker to level, and the session is InSync only after the
   refetch answers from that same worker pid". A stale answer from a replaced worker leaves it `BehindApp`.
3. `ReplFreshnessTests.fs`: "an in-worker reset that resolves Ok clears BehindApp", the bug in the first section.
4. Host tier in `RunAppDeltaTests.fs`: the matrix above. The "tests in flight" cell starts a run, saves, and asserts the REPL
   is still behind until the run's own completion event, then level.

Simulation. Extend `SageFs.Simulation/EvalActorSim.fs` with op `Level` and a new `TestRunGateSim.fs` (ops `RunStart`,
`RunEnd`, `Level`, `DebugBegin`) whose invariants are:

- L1: a level never runs while an eval is `Evaluating` or `Cancelling`, or while a run is `Running`.
- L2: a `Submit` or `RunStart` that arrives during `Leveling` is held and then served, never dropped or run on the old host.
- L3: a level never runs when `EvalCount > 0`.
- L4: the refetch is asked once per level and its answer is judged by pid.

Twins: `runCancelsFirst` (the router cancels the running eval before it levels, today's behaviour for `HardResetSession`)
breaks L1; `runCheckThenAct` (the gate reads, then the reset runs later) breaks L1 under a `RunStart` between them;
`runNoPidJudge` (accepts a stale answer) breaks L4.

Tiers: the pure tests, the manager test and the sims in the default suite; the matrix in `--integration-host`. No wall-clock
waits: the host cells wait on the worker's `/events` frames and on the session's own freshness transition.

## Steps

1. Measure first (below).
2. RED tests 1 and 3, the sim ops and twins. Files: `SageFs.Tests/ReplLevelingTests.fs` (new),
   `SageFs.Tests/ReplFreshnessTests.fs`, `SageFs.Simulation/EvalActorSim.fs`, `SageFs.Simulation/TestRunGateSim.fs` (new).
3. `ReplLeveling.fs` (pure) and the clear-after-reset fix in `SessionManager`. Green for 1 and 3.
4. `TestRunGate` in the worker and its three enter points. Files: `SageFs.Host/WorkerMain.fs` only.
5. `EvalLevel` in the actor: extract the `EvalHardReset` body into a function shared with `EvalLevel`; the router posts it
   without cancelling. Files: `SageFs.Core/AppState.fs`, `EvalActorDecision.fs`. Steps 4 and 5 are file-disjoint and run in
   parallel.
6. `WorkerMessage.LevelRepl`, the response, the HTTP route (`WorkerHttpTransport.fs`, `HttpWorkerClient.fs`,
   `WorkerProtocol.fs`).
7. Manager side: `LevelRepl` command, `Leveling`, the refetch completion signal, `TestRunsDrained` retry. Test 2.
8. Wire text, dashboard line, `blockedBy`. Matrix cells. Docs.

First week: steps 1 to 5. The first commit is step 2.

## Measure first

1. What does `EvalCount` count? Create a session on a project with and without `.SageFs/init.fsx`, read `EvalCount` from the
   worker status right after Ready. If warmup or init evals bump it, "empty" needs a separate user-eval counter. If it stays
   0, the field is enough.
2. Hard reset cost on a large solution. The 2 to 2.7 s figure is the tiny fixture. Run the same test against `SageFs.Tests`'s
   project and record p50 over five runs. Over about 10 s, the automatic step needs a progress line on the dashboard card
   in addition to the banner.
3. Does the in-worker reset clear `BehindApp` today? Expect no; confirm with the existing test plus one assertion.
4. What a test run in flight does when the host is disposed. This builds the RED for the gate and decides whether the gate
   needs to drain or may cancel.
5. Does a reset leave `latestDynamicRunTest` pointing at the disposed host? If so, that is a separate defect to fix in step 4.

## Risks and dependencies

- Never level a session the cohort gate drives. The verifying session is driven by the gate alone (commit d571efe9). Leveling
  it would disturb a landing verification. The manager treats it as a blocker, `GateDriven`, in the facts.
- Trunk sessions follow landings and also go `BehindApp`. They have no user REPL, so they level freely, but not during a
  landing in flight.
- The refetch is fire-and-forget today, so adding a completion signal touches code `WorkerPostReady` owns.
- Depends on nothing unbuilt. `repl-eval-reaches-the-running-app` makes `BehindApp` more frequent; this item keeps that
  frequency from turning into noise. Nothing depends on it.

## Size

M. The gate, the actor message and the manager command are each small, and the three sims are the larger part. The only
open number is the reset cost on a big solution.

## Decisions for Will

- Replay your definitions into the fresh host instead of deferring? `EvalStore` already keeps every cell. A replay of
  declaration-only cells (functions, types, opens, by `ReloadPlanning.extractDecls` kind) is plausible. A value binding
  re-runs its initializer, which is a side effect, so those stay blockers. Recommend: ship the defer-only version first, then
  decide on replay with real usage.
- Keep `Leveling` off the wire (my plan) or add a fourth state and move `apiVersion`? Recommend off the wire.
- Should leveling apply to every `BehindApp` session, or only when a setting is on? Recommend always on, because the user
  loses nothing, and a setting to turn it off for the first release.

## What changed from the plan (2026-11-07)

Measure first:

1. `EvalStats.EvalCount` cannot say "empty". The init script and the startup profile run through `FsiSession.evalOrThrow` in `init`,
   not through `EvalRun`, so the count is 0 after Ready with or without an init script (read from the code, not from a status).
   But a hard reset keeps `evalStats`, so once any eval has run the count never returns to 0, and a hot reload's `#load` counts as
   an eval. The actor now keeps its own `hostHolds` (`ReplLeveling.HostHolds`): evals started since the host was built, leaving out
   the hot reload's (`hotReload` argument), and the init script that ran in the host. Every reset empties it.
2. The host reset on a large solution is not measured. The host-tier cell prints `REPL-LEVELING [...] ... took N ms` for the
   run_app fixture; that is the same tiny project as the plan's 2 to 2.7 s figure.
3. Nothing cleared `BehindApp` after an in-worker reset, as expected. `SessionProxy.observingHostRebuilds` now sees every
   `HardResetSession` that resolves Ok on the session's own proxy (the editor's reset, the smart reset, a level) and posts
   `HostRebuilt`. A soft `ResetSession` does not rebuild the shadow copy (`EvalReset` re-warms on the existing one), so it leaves the
   state alone.
4. What a test run in flight does when its host is disposed is not measured. The gate makes it unreachable for a level (a level
   waits), so the answer only matters for a user's own hard reset while a run is in flight. That case is unchanged.
5. A reset does not clear `latestDynamicRunTest`, so after a hard reset it still points at the disposed host. Not fixed here: a level
   never resets while it is set (`LiveTestsDefined`), and the editor's hard reset still leaves it. The buffer's discovered tests have
   the same problem, in `mkLiveTestEvalSupport`'s closure.

Built differently from the steps:

- The simulation is one world, `SageFs.Simulation/ReplLevelSim.fs`, instead of an `EvalActorSim` op plus a `TestRunGateSim`: the
  races are between the worker's handler, the actor, the gate and the daemon's pid judgement, so the four invariants (and a fifth,
  `InSync` only when the host was built at or after the latest save) are checked over all of them at once, with seven twins.
- A level waits for the last test run (`TestRunGate.LevelPending`) and holds new runs back so it cannot starve. There is no
  `TestsRunning` deferral to retry and no `TestRunsDrained` event; `TestsRunning` stays in the blocker set as a fact the actor is
  told and a reason a level would not have acted.
- Every test entry point holds the gate: `RunTests`, `EvalLiveTestFile` and the streamed run (`/run-tests-stream`, which the first
  plan missed, and which is the one live testing uses). A test held for a debugger holds the gate only for the length of the
  `DebugTestBegin` call and sets the keeper's debug hold, which the level counts as a run in flight and refuses on: holding the gate
  until the editor ends the hold would have a pending level hold every other run behind a hold nobody may ever end.
- `ReplFreshness` gained `Leveling` and `CannotLevel` (and `FreshnessUnknown.LevelFailed`). `DashboardFragments.fs` and
  `SessionReadiness.fs` each needed one match arm, so the card line shows the blockers without any other dashboard change.
- Three file budgets rose with comments in `RatchetBudgets.fs` (`SessionManager.fs`, `AppState.fs`, `SessionManagerCallbacks.fs`).

What sagefs.nvim and the VS Code extension need: nothing to keep working. Both read `replFreshness.state == "BehindApp"` and
`message`; the new states use the same name and wording in `message`. To show the reason, read `replFreshness.blockedBy`
(an array of tokens: `NotServing`, `EvalRunning`, `HostGone`, `ReplHoldsDefinitions`, `InitScriptDefined`, `LiveTestsDefined`,
`TestsRunning`, `LevelAlreadyRunning`, `SwapInProgress`, `GateDriven`). A client cannot tell leveling from a plain gap except by
the words in `message`.
