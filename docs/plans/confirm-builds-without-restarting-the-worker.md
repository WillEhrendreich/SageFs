# Confirm builds without restarting the worker

Roadmap item: `confirm-builds-without-restarting-the-worker` (Later, Live testing). Plan written 2026-10-26 against master `59a8692c`.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done |
| Size | L |
| Branch and worktree | none: worked on master |
| Last green commit | 3ce2bf0f |
| Next action | None for this item. What is left is under "What is left" below: apply a throwaway worker's coverage, and time a confirmation in both venues once the numbers harness can. |
| Decisions made | 2026-11-09, by the agent that built it, under the standing instruction to choose by what the code allows. (1) Swap when clean, throwaway when the session holds something, which is the plan's recommendation for decision 1. "Holds something" is counted by the daemon in the `SessionProxy` wrappers every route into a worker passes through (hand evals, evaluated buffers of other files). (2) A confirmation that would need a throwaway and cannot have one is refused with a named reason, never swapped, which is the plan's recommendation for decision 2. (3) The FSI-less test host (step 10) was not built: a throwaway is a whole worker, and a swap already runs two workers side by side, so the peak memory is what it was. A bare session's worker plus its FSI host is about 480 MB resident on this machine at idle (measured 2026-11-09 with `ps`); a project session is larger. |
| Surprises | See "As built" below. The owner is not in `SessionManager`; the verifying-session lock is in the reducer; two file-size budgets and three domain budgets were exact and the first version went over them, so the pieces that did were moved or rewritten (`RunBasis.provenanceOf` went to `BuildConfirmation.fs`, the message classifier lists every case, the bool field is a union). |

## What done looks like

You type, the verdict appears from the evaluated code, you stop for two seconds, a real build confirms or contradicts it, and your REPL session is exactly as it was: the definitions you evaluated, the values you bound, the unsaved edits you made in other files, the debugger you attached. The row still ends up `VerifiedByBuild` or `BuildDisagrees` with the compiler's message. The outcome gate is a journey that binds a value in the REPL, runs an edit-confirm cycle, and then reads the value back from the same session, across the matrix below.

| Axis | Cells |
|---|---|
| Session state before the confirm | clean (nothing evaluated by hand), REPL value bound, unsaved edit in a second file, hot-reload workflow with an app running |
| Build outcome | compiles and agrees, compiles and disagrees, does not compile, build does not answer in time |
| Memory | headroom, daemon under memory pressure |
| Session purpose | Working, Verifying (must never confirm) |

## What exists today

Verified by reading the code.

- The confirmation is a pure state machine, `BuildConfirmation.step` (`SageFs.Core/Features/BuildConfirmation.fs`): phases `Idle | Quiet | Building | RunningBuilt`, effects `StartQuietWindow | StartBuild | AbandonBuild | RunAgainstBuild | Mark`. It owns no clock or process. `SageFs.Simulation/BuildConfirmationSim.fs` folds the real `step` with three twins.
- The effect is performed in `SageFs/SageFsEffectHandler.fs`. `startConfirmationBuild` (line 353) reads the file from disk, compares it with the evaluated content, and if they match calls `deps.RestartSession sid (RestartPlan.Rebuild RestartSubject.Worker)`, then waits on `RebuildReadyWait.await (deps.AwaitReady sid) deps.ReadyDeadline`, then raises `BuildFinished`. The run against the build is an ordinary `RunAffectedTests` effect emitted by the reducer (`SageFsApp.fs` around line 935, `RunBasis.ConfirmingBuild generation`) and goes to `deps.GetStreamingTestProxy sid`, the session's own worker.
- That restart is `SessionManager`'s `RestartSession` with `rebuild = true` (`SageFs.Core/SessionManager.fs` around line 725). It is build-first: the live worker keeps serving while `runtime.RunBuildAsync` runs off the mailbox loop, and only a good build triggers `spawnFirst`, which starts the replacement, waits for it, then swaps. So the old worker and the new one are already alive together during every swap. The memory price of a second FSI host is paid transiently at each confirmation today.
- What the swap loses: the old worker's FSI state, which is everything evaluated by hand or by a keystroke, because the new worker loads compiled output from disk. `docs/decisions.md` ("Every row says what it ran against") records it and its reopen condition: "restarting the worker for a confirmation loses something users notice".
- Coverage depends on which worker ran. After the swap the daemon holds the new worker's `InstrumentationMaps` and per-test bitmaps. `SageFsEffectHandler`'s `onCoverage` accepts a reading only when `hits.Length = mergedMap.TotalProbes`, so a reading from a worker with a different map is dropped, not mixed.
- The cohort's verifying session was changed today (commit `d571efe9`). `LiveTestFileRouting.SessionPurpose = Working | Verifying`, `drivenByEdits` drops a `Verifying` session from the sessions a save or a keystroke drives, and `McpCohortIntegration.fs` marks the integration session with `SageFsMsg.SessionVerifiesLandings`. Its reason, in the type's own comment: a live loop's confirming build swaps the worker, and a swap in the middle of a verification is a race. The simulation `LiveTestFileRoutingSim` has an invariant and a twin for it.
- The test host is separable from FSI. `HostAgent.Agent` takes `AssemblySources` (`Dynamic`, `Loaded`) and a list of `TestExecutor`s; `AssemblySources.Dynamic` can be `fun () -> [||]`. The agent discovers and runs tests from loaded assemblies without an FSI session in the same process. I did not find an existing process that hosts an `Agent` alone.
- Cold cost of a whole worker, measured and published: `docs/repl-loop-benchmarks.md`, create-to-Ready 5.5 s (`samples/counter`) to 12.5 s (`SageFs.Core`, 86,000 lines). Standing spare workers were removed on purpose (`docs/decisions.md`, "No standby worker pool": they cost memory and held ports).

I could not find: a per-file table of unsaved buffers in the daemon (the live cycle keeps `LatestContent` for one active file), any measurement of a worker's resident memory next to the confirm timings, or a place where the daemon spawns a worker that is not a registered session.

## Design

Run the confirmation in a throwaway worker that belongs to the session, then discard it. The primary worker is never touched. It exists from the end of the quiet window to the end of the run, so the standby pool that `docs/decisions.md` retired does not come back.

### Types

New slice `SageFs.Core/Features/ConfirmationVenue.fs`, pure:

```fsharp
/// What a swap of this session's worker would lose. Two facts, each a DU.
[<RequireQualifiedAccess>]
type HandWork =
  | NothingEvaluated
  | EvaluatedSinceStart of evals: int

[<RequireQualifiedAccess>]
type UnsavedEdits =
  | NoneElsewhere
  | InFiles of paths: string list

type SwapWouldLose = { HandWork: HandWork; Unsaved: UnsavedEdits }

[<RequireQualifiedAccess>]
type KeptState =
  | ReplDefinitions of evals: int
  | UnsavedBuffers of paths: string list

[<RequireQualifiedAccess>]
type ConfirmRefusal =
  | MemoryPressure of headroom: string   // MemoryHeadroom is new; step 3 defines it from MemoryPressure.fs
  | VerifyingSession
  | LiveTestingOff

[<RequireQualifiedAccess>]
type Venue =
  /// Build, then swap the session's worker as today: nothing is lost, so coverage and maps refresh.
  | SwapInPlace
  /// Build, then run in a worker that dies after the run: the session keeps everything it holds.
  | ThrowawayWorker of keeps: KeptState list
  /// Not confirmed, and the rows say why.
  | Refused of ConfirmRefusal

val decide : SessionPurpose -> MemoryHeadroom -> SwapWouldLose -> Venue
```

`Refused VerifyingSession` is the rule `drivenByEdits` already encodes, now also at the venue, so a future path that bypasses the router still cannot confirm a verifying session. `HandWork` and `Unsaved` need facts the daemon may not have today; see Measure first.

`BuildAnswer` gains a case so the machine can tell "the build ran and the throwaway worker answered" from a swap, because the follow-up differs (a throwaway answer must not refresh the session's maps):

```fsharp
type BuildAnswer =
  | Built                         // swap: the session came back up on what it made
  | BuiltInThrowaway              // new: tests will run in the throwaway worker
  | DidNotBuild of message: string
```

`ConfirmationEffect.RunAgainstBuild` carries the target worker:

```fsharp
type RunTarget = SessionWorker | ThrowawayOf of generation: int64
```

### Edge

`SessionManager` owns the throwaway worker (single owner, like every process in this daemon). A new command `SessionCommand.SpawnConfirmWorker(id, generation, reply)` builds first through the same `runtime.RunBuildAsync` path, then spawns a worker with `WorkerSpawn` against the fresh output, records it in a new `ManagerState` field keyed by session and generation (not as a session, so `list_sessions`, `resolveSessionId` and `liveTestRoots` never see it and "Multiple sessions match workingDirectory" cannot happen), and answers with a streaming test proxy. `SessionCommand.RetireConfirmWorker` kills it. The worker uses the existing parent-death watch and the orphan sweep, so a daemon crash cannot leak it. Retire on every exit: verdict delivered, deadline reached (`Timeouts.liveTestConfirmationDeadline`), a newer edit abandoning the generation (`AbandonBuild`), the session stopping, the daemon shutting down.

`startConfirmationBuild` asks `ConfirmationVenue.decide` first. `SwapInPlace` keeps today's code path. `ThrowawayWorker` takes the new path. The deadline timer and `abandonConfirmationBuild` already exist per `(session, generation)` and cover both.

Heavy work admission: a throwaway worker plus a build is heavy. It takes the same lease and memory check the rebuild takes (`ExpensiveWorkLease`, `MemoryPressure`), and a refusal becomes `Venue.Refused (MemoryPressure ...)`, which marks the rows with a plain reason (the way `BuildDisagrees (BuildUnanswered ...)` does) and never goes quiet. I did not read `ExpensiveWorkLease.fs`; step 3 starts by reading it.

### Coverage

A throwaway run's coverage frames use the throwaway worker's maps. Phase 1 discards them and marks the session's coverage as "from an earlier build" in the status line, which is what a keystroke already does between confirmations ("the markers keep the last coverage recorded against a real build"). Phase 2 swaps in place whenever `decide` says nothing would be lost (clean session), which restores the map refresh in the common case. Phase 3, if the numbers ask for it, promotes the throwaway worker to primary when the session is clean, saving the second spawn.

### A lighter throwaway

If the measurements below say a full worker plus FSI host is too heavy, host an `HostAgent.Agent` alone in a small process that loads the built assemblies and runs the executors, with no FSI. Same protocol subset (`RunTests` streaming only). It is a larger build than the first version and is listed as step 10, gated on a number.

### Wire and clients

No route changes. The status already carries `Confirmation` (`idle | quiet | building | running_built`). A throwaway adds the worker's pid to the `building`/`running_built` phases only if the dashboard wants it; skip it in v1. Row provenance wire values are unchanged (`verified_by_build`, `build_disagrees`). `apiVersion` stays 4; `Plugin: n/a`. Persistence: none. Token cost: none, no tool changes.

### How this fits the cohort change made today

`SessionPurpose.Verifying` stays the gate. A verifying session is not driven by saves or keystrokes, so it never reaches `StartBuild`. The new `Venue.Refused VerifyingSession` is a second lock on the same door. The throwaway worker is deliberately not a session, so `liveTestRoots` (the router's view built from `model.Sessions.Sessions`) never lists it and `FileRoute` never sends it a save. The cohort's integration session still rebuilds through the landing gate's own path, which this plan does not touch. One thing to re-check after the cohort work settles: whether the landing gate ever relied on a Working session's confirming build to refresh the integration session; commit `d571efe9` removed that, and its note says the gate evaluates the rebased files itself.

## Tests

RED first.

1. `ConfirmationVenueTests.fs` (pure): a Verifying session is always `Refused VerifyingSession`; a clean Working session is `SwapInPlace`; a Working session with a bound value or an unsaved edit elsewhere is `ThrowawayWorker` naming what it keeps; memory pressure is `Refused`. Fails today, no module.
2. Property (FsCheck): `decide` is total; `SwapInPlace` is returned only when `SwapWouldLose` is empty; the reasons listed in `ThrowawayWorker` are exactly the nonempty parts of the input.
3. Extend `BuildConfirmationSim`: new ops `BuiltInThrowaway`, `ThrowawayDied`, and a venue per run. Invariants added: a throwaway answer never re-marks a newer generation; at most one throwaway per session at a time; every throwaway is retired when its generation ends (the sim tracks live throwaways as a set and requires it empty at `Idle`); a `Refused` venue produces a `Mark` with a reason, never silence. Twins: keep the throwaway when a newer edit abandons the generation (leak), apply a throwaway's coverage frame to the primary's maps, confirm a Verifying session. Each must violate its invariant. Same seed, same trace.
4. `LiveTestFileRoutingSim`: add the invariant "a throwaway worker is never a route target" with a twin that lists it among sessions.
5. Mutation: `decide` with the Verifying check removed; with `SwapWouldLose` always empty; with memory pressure ignored.
6. Manager tests with the fake runtime (`mkRuntime`/`withHarness` as the existing `SessionManager` tests use): `SpawnConfirmWorker` does not change `list_sessions`, does not touch the primary's proxy or pid, and `RetireConfirmWorker` leaves no process in the fake's spawn table.
7. Outcome gate, `LiveTestingJourneyTests`-style under `--integration-lt`: bind `let marker = 42` in the session through `send_fsharp_code`, make an edit that triggers a confirmation, wait for `Confirmation = idle` on the `/events` stream (the same wait `LtStream.awaitConfirmed` uses), then evaluate `marker` in the same session and require 42 and the same worker pid. Cells: clean (expects swap, pid changes, marker gone is the old behaviour and is asserted as such), bound value (pid unchanged), unsaved edit in file B (still evaluated afterwards), compile failure (primary untouched, rows say why), memory pressure (rows say why).
8. Numbers: the confirm-to-verdict time and peak resident memory for both venues, recorded by the harness of `live-testing-numbers-on-bigger-projects`.

No wall-clock waits in tests: every wait is on the machine's phase or on `AwaitReady`; the product's own bound stays `Timeouts.liveTestConfirmationDeadline`.

## Steps

1. Measure (below). No code. Output: a table in this plan's follow-up commit message and a record for the numbers harness.
2. `ConfirmationVenue.fs` with tests 1, 2, 5. Pure, new file. Independent of everything.
3. Read `ExpensiveWorkLease.fs` and `MemoryPressure.fs`; add the admission input to `decide`.
4. `BuildAnswer.BuiltInThrowaway` and `RunTarget` in `BuildConfirmation.fs`; extend the sim (test 3). Files: `BuildConfirmation.fs`, `SageFs.Simulation/BuildConfirmation*.fs`. Needs 2.
5. `SessionManager` command pair and `ManagerState` field (test 6). Files: `SageFs.Core/SessionManager.fs`, `WorkerSpawn.fs`, `SessionManagerCallbacks.fs`. `SessionManager.fs` is at its size budget (1554); the new code goes in a new file and the command arms are net-small. Parallel with 4.
6. Effect handler: venue choice in `startConfirmationBuild`, `RunTarget` honoured by `RunAffectedTests`. Files: `SageFs/SageFsEffectHandler.fs`, `SageFs/SageFsApp.fs` (net zero lines). Needs 4 and 5.
7. The journey (test 7) and routing invariant (test 4).
8. Docs: `how-live-testing-works.md` (the two limits paragraph and the Visual Studio row on isolation), `decisions.md` (the reopen condition is met; record the new decision), roadmap landmark `ConfirmationVenue.fs`.
9. Phase 2: swap in place when clean.
10. Only if step 1 says so: the FSI-less test host.

First week: step 1 on day one (it decides the shape), steps 2 and 3 in parallel, step 4 at the end of the week.

## Measure first

Run `scripts/machine-bench.fsx` (it already records stage times and peak memory from `/proc`, and takes `--memory-cap`) on the FromCSharp sample, `SageFs.Core`, and `SageFs.Tests`. For each, record: resident memory of one worker plus its FSI host; time from `BuildFinished` to Ready for a replacement worker; time to run the affected tests in it. What each result means:

- Peak memory of two workers above, say, a third of the 24 GiB reference machine on `SageFs.Tests`: the throwaway needs the memory-pressure refusal on day one and the FSI-less host (step 10) moves up.
- Spawn-to-Ready for a throwaway close to today's swap time: no latency regression, ship phase 1 as designed.
- Spawn-to-Ready much greater than the 5.5 to 12.5 s cold figures: the host-adapter cache (the warmup replay cache, `WarmupReplayCache.fs`) is not helping a second worker and step 10 is needed.
- Whether the daemon can answer "what would a swap lose" at all: today it knows one file's latest buffer. If it cannot know about unsaved edits elsewhere, phase 1 always uses the throwaway for a Working session that has ever evaluated by hand, and `SwapInPlace` is reserved for sessions with an empty eval history.

## Risks and dependencies

- Build output is shared. The throwaway loads the same `bin/` the primary shadow-copied from. The primary's shadow copies mean the build does not fight it for file locks (the comment in `SessionManager.fs` says the worker loads shadow copies); the throwaway must shadow-copy too, or a build in the next quiet window races its load. Test 6 needs a case for two builds close together.
- Coverage staleness in phase 1 (described above). It is a visible regression in one case (clean session) and phase 2 removes it.
- Two builds in flight for two sessions: `SessionBuild.buildConcurrencyLimit` (cores/4) already bounds them.
- Depends on: nothing in the roadmap. Informs: `debug-failing-tests-in-vscode` (its evaluated-code half needs a throwaway to debug the compiled copy without losing the session), `live-testing-numbers-on-bigger-projects` (measures both venues), `run-tests-reuses-proven-results` (a throwaway run is a `Compiled` run and can feed the cache).
- The cohort integration session is untouched. Do not reopen the cohort change from today.

## Size

L. The pure venue and the simulation are days, the `SessionManager` ownership of a second kind of process and its retirement on every exit path is the long part, and the file-size budgets force new files.

## Decisions for Will

1. Throwaway always for a Working session, or swap when clean? Recommendation: swap when clean. It keeps today's coverage refresh in the common case and uses the extra process only when the swap would cost you something.
2. If memory is short, refuse the confirmation (rows stay `Evaluated` with the reason) or fall back to the swap and lose the state? Recommendation: refuse. Losing a user's REPL state is the failure this item exists to remove, and an `Evaluated` row that says why is honest.
3. Is the FSI-less test host worth building if the numbers allow the full worker? Recommendation: only if step 1 shows memory is the problem.

## As built

What differs from the design above, and why.

- The venue is not `ConfirmationVenue.fs` as drawn. `SageFs.Core/ConfirmationVenue.fs` holds `Venue.decide` (swap, throwaway or refused, from what the worker holds, how the session starts and how much memory is left), `SessionHandWork` (the pure count of what a worker holds) and `ThrowawayBook` (the pure book of throwaway workers). There is no `HandWork`/`UnsavedEdits` pair: the daemon counts hand evals and the files whose editor buffer the live loop evaluated, per worker pid.
- The count lives where every route into a worker passes: `SessionProxy.notingWork` (`SageFs.Core/WorkerProtocol.fs`) wraps the two `GetProxy` closures in `DaemonMode.fs` and `ElmDaemon.fs`, and `ConfirmWorkers.HandWorkStore` keeps it. A reset empties it and a swap starts at zero because the pid changes.
- The owner is `SageFs/ConfirmWorkers.fs`, one mailbox over `ThrowawayBook.step`, not a `SessionManager` command pair. It builds with the session's own build, starts a worker through `WorkerSpawn.startWorkerProcess` (the Interactive workflow, so it watches no files and starts no app), waits for its port and for Ready with `WorkerStartup.await` and `WorkerPostReady.watchUntilReady`, and retires it with `WorkerStop.stopProcess`. It is not a registered session, so `list_sessions` never sees it. It was not put in `SessionManager.fs` because that file is at its size budget and the manager's state is not the right owner of a process that is not a session.
- `BuildAnswer` gained `BuiltInThrowaway` and `NotAttempted of reason`; `ConfirmationEffect.RunAgainstBuild` gained a `RunTarget` (`SessionWorker` or `Throwaway`). `BuildDisagreement` gained `NotConfirmed of reason`, shown on the wire as a `BuildDisagrees` row (`sagefs.nvim` has words for it).
- The run in the throwaway is the ordinary `RunAffectedTests` handler, called with a deps record whose streaming proxy points at the throwaway's URL and with no instrumentation maps, so it reports no coverage. The reducer emits it as `SageFsEffect.RunInThrowaway`.
- The verifying-session lock is in the reducer (`confirmFinishedRun` opens no confirmation for a session in `VerifyingSessions`) and not in `Venue.decide`, because the effect handler has no model and the reducer does.
- Memory admission reuses the memory supervisor's own refusal (`checkMemoryAdmission` in `DaemonMode.fs`); no lease is taken, because a swap never took one either.
- Every way a confirmation ends goes through `abandonConfirmationBuild`, which asks the owner to retire its worker. A session leaving the registry reaches the owner through `OnSessionRemoved`. A start that hangs is bounded by the confirmation's own deadline.

## What is left

- Coverage. A throwaway run applies no coverage, so a session that holds something keeps the coverage markers, per-test bitmaps and line baseline of its own worker until that worker is swapped. Phase 3 of the design (promote a clean throwaway to the session's worker) would bring coverage along; it needs the throwaway's maps to replace the session's, which the daemon does not do today.
- Numbers. The harness of `live-testing-numbers-on-bigger-projects` does not yet time a confirmation in either venue, and peak memory of a project session on `SageFs.Core` or `SageFs.Tests` is not measured.
- The debug-row journeys still wait on a managed debugger (see `debug-row-journeys-run-green`).
- Isolation. The confirming build writes into the project's normal output folder, so a regular `dotnet build` running at the same moment can collide with it. The throwaway worker's build could go to a folder of its own through the artifacts path that session options now carry. Nothing builds that way yet.
