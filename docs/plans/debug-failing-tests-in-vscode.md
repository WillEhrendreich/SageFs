# Debug failing tests properly in VS Code

Roadmap item: `debug-failing-tests-in-vscode` (Next, Live testing). Plan written 2026-10-26 against master `59a8692c`.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | In progress (steps 1 to 5, 7, the batch, the simulation and the mutation cases are done; the two row journeys and four more debug cases run green in `--integration-lt` against netcoredbg, 2026-11-10, see `debug-row-journeys-run-green`; the extension-host case ran once and stopped at "no discovered test", see "Written and never run") |
| Size | M for the Next scope, L overall |
| Branch and worktree | `worktree-agent-ad20eb97ce17072e5` at `.claude/worktrees/agent-ad20eb97ce17072e5` (2026-11-07, merge by the orchestrator) |
| Last green commit | see the branch log: the last `feat(debug)` commit on it |
| Next action | The row journeys are back in the tier and green (`debug-row-journeys-run-green`, Done). Still here: (1) the extension-host case: it needs a session with tests and hot reload off, and the proof's fixture is a HotReload session on a daemon with `SAGEFS_HOT_RELOAD=true`, where discovery answers `ready_zero_tests`; give that case a fixture of its own, then the rest of its sequence (the pause around the hold, the `startDebugging` path, the row) runs for the first time. (2) "Measure first" run 1 (a test stopped at a breakpoint longer than the watchdog), which the hand run of 2026-11-10 covered for 35.9 s only (one test held at a breakpoint for 35.858 s, the session stayed Healthy, the same ticket answered `attached`). (3) Microsoft's adapter, by hand per release. Slice 4's evaluated-code half waits on `confirm-builds-without-restarting-the-worker`. |
| Decisions made | 2026-11-07, the implementing agent: (1) Accepted by the coordinator 2026-11-07: several tests are taken one after another by the client under one attach, not by a batch hold in the host. The host slot still holds one test, the debugger stays on the host between tests, so no daemon wire change, no change to `Hold`, and each verdict records as its test ends. The `Hold` head-and-tail design in "Slice 4" is not built. (2) Auto-pause stays client-driven (slice 3) and is skipped when the person had already paused. (3) A debug verdict overwrites the row and records no duration. The plan's open decisions 1 and 2 follow the plan's recommendations, still for Will to confirm. |
| Surprises | The host's `DebugEnd.Attached` names no test, so the daemon keeps a bounded table of the holds it granted (`McpDebugTest.heldTargets`, keyed by session and ticket, taken on the first `Ended`) to say which row a verdict belongs to; that also gives "exactly one dispatch per ticket" at the route. `DebugRunRecord` fields are `OwnerSession`, `HoldTicket`, `Verdict`, `CodeBasis` because `Session`, `Ticket`, `Result` as field names made record expressions elsewhere in Core infer the wrong type. The per-file annotation push is gated on a key that moves with no row verdict, so `testSummaryProjectionKey` gained a debug-run count (a count of verdicts ever recorded, since the ticket log is bounded). `HostAgent.fs` is embedded in the FSI host, so the host cannot reference `DebugRunRecord`; the record lives in the daemon only. The `run_tests` argument is `pattern`; `tools/sagefs-call.fsx` sends `testPattern`, which the daemon ignores, so a filtered call from that driver starts the whole suite. |

## What done looks like

You click Debug on a failing test in VS Code, the C# debugger attaches to the process that runs it, the test runs under your breakpoints, and when it finishes the test's mark, lens and Test Explorer row show the verdict you just saw, on every client, without a normal run. You can select several tests in the Test Explorer and debug them in one attach, one after another. When the test lives in code the session evaluated, SageFs offers the compiled copy so your breakpoints bind. The outcome gate is a journey under `--integration-lt` that runs the three configurations below and asserts the row the user would see; the HTTP answer is only the means.

| Axis | Cells |
|---|---|
| Test code | compiled (PDB), defined by eval (no PDB) |
| Selection | one test, three tests |
| Verdict | passes under the debugger, fails under the debugger, debugger detached early |
| Client | VS Code extension host, Neovim (unchanged, must keep working) |

No cell may skip silently. A machine without a debugger fails with a "prerequisite missing" case that names the install command.

## What exists today

Verified by reading the files named.

- The daemon holds a test in the host and hands back a pid and a ticket. Routes `POST /api/live-testing/debug` and `/debug/continue` are in `SageFs/McpServer.fs` (around line 4117, `respondDebug`), which delegate to `McpDebugTest.beginDebug` and `McpDebugTest.continueDebug` in `SageFs/McpDebugTest.fs`. Those call `DebugTestRequest.beginDebug` / `continueDebug` (`SageFs.Core/DebugTestRequest.fs`), which send `WorkerMessage.DebugTestBegin` and `DebugTestContinue` to the worker (`SageFs.Host/WorkerMain.fs` around line 320).
- The host side is `HostAgent.TestDebug` (`SageFs.Core/HostAgent.fs` from line 293): a pure `step` over a one-slot `Hold` (`Idle | Holding | Awaiting | Running | Ended`), and the `DebugHold` shell around it. `Agent.DebugBegin` / `DebugContinue` expose it. The slot holds exactly one `TestCase`.
- `DebugHold` runs the test with `firstAnswer runners test` (`HostAgent.fs` around line 722), not through `executeOne`. So there is no per-test timeout (good for a breakpoint) and also no stdout capture and no coverage read.
- The result of a finished hold is `DebugProgress.Ended (DebugEnd.Attached TestResult)`. `McpDebugTest.continueDebug` turns it into a `DebugWire` and drops it. Nothing dispatches it into the Elm model. That is the "debug runs updating the gutter" gap. `docs/LIVE_TESTING_GUIDE.md` says so under "What it does not do yet".
- The VS Code side: `sagefs-vscode/src/TestDebugCommand.fs` (`debugTest`, `begin'`, `attachAndWait`, `waitForTest`), pure decisions in `TestDebugPure.fs` (`DebugAnswerView`, `beginStep`, `continueStep`, `chooseDebugger`, `RecommendedExtensionId = "ms-dotnettools.csharp"`), HTTP in `SageFsClient.fs` (`debugTest`, `debugContinue` near line 742), registration in `Extension.fs` (`reg "sagefs.debugTest"`, near line 2866). The Test Explorer profile in `TestControllerAdapter.fs` (`debugHandler`) debugs only `items.[0]` of the request.
- A difference from Neovim that I found by reading both: `sagefs.nvim/lua/sagefs/debug_test.lua` sends `sessionId` in both bodies (`build_hold_body`, `build_continue_body`). The VS Code client sends `{ testId }` and `{ ticket }` only. The neighbouring `sagefs.runTest` command goes through `sessionCommand` and does name its session. On a daemon with two sessions the VS Code debug route resolves its session by the daemon's fallback rules, and the continue can land on a different session than the hold.
- The attach is proven for Neovim's debugger only: `SageFs.Tests/DebugAttachJourneyTests.fs` (tier `--integration-lt`) attaches `dotnet-debug`/`netcoredbg`, lists the threads of the pid the daemon named, then releases the test. It prints `SKIPPED the attach` when the machine cannot reach the worker or the debugger is absent. No test drives the VS Code extension through a real attach. `sagefs-vscode/tests/TestDebugContractTests.fsx` covers the pure module, and `sagefs-vscode/test-electron/ExtensionHostSuite.fs` runs commands inside a real extension host, but none of its cases debugs.
- The simulation exists: `SageFs.Simulation/TestDebugSim.fs`, invariants in `TestDebugSimInvariants.fs`, tests in `SageFs.Tests/TestDebugSimTests.fs`. Route tests: `DebugTestRoutesHttpTests.fs`, `DebugTestRequestTests.fs`, `TestDebugTests.fs`.
- Live testing already has a pause: `LivePause = Live | Paused` (`LiveTestingTypes.fs`), routes `/api/live-testing/pause` and `/resume`. `docs/decisions.md` ("Pause and an include or exclude set are enough") says a debug session is the editor's to pause with.

I could not find: any use of the pause route by the debug command, a test of a test stopped at a breakpoint for longer than the watchdog (`Timeouts.watchdogGracePeriod` is 30 s forMachine), or a per-file buffer table (not needed here).

## Design

Four slices, in dependency order. Slices 1 to 3 are the Next-horizon scope. Slice 4 waits on `confirm-builds-without-restarting-the-worker`.

### Slice 1: the VS Code client names its session

Move body construction into `TestDebugPure` (pure, runs under `dotnet fsi`): `holdBody testId sessionId` and `continueBody ticket sessionId`, mirroring Neovim. `Extension.fs` passes the window's session id the way `sagefs.runTest` does. `sessionId` is already an optional field on both routes, so no wire change and no `apiVersion` move.

### Slice 2: a debug run is recorded where every other run is

New file `SageFs.Core/Features/DebugRunRecord.fs`:

```fsharp
type DebugRunRecord =
  { Session: string
    Ticket: string
    Result: TestRunResult
    /// Compiled when the PDB existed, Evaluated when the test was defined by eval.
    Basis: ResultProvenance }

/// Pure: the cycle with this one verdict on its row. A ticket already recorded changes nothing.
val record : DebugRunRecord -> LiveTestCycleState -> LiveTestCycleState
```

Rules, each a test below:

- The row takes the verdict and the provenance from `SymbolSupport` (`CompiledWithSymbols` gives `Compiled`, `DefinedByEval` gives `Evaluated`).
- Idempotent per ticket. `DebugHold.Continue` is "safe to repeat", and a late continue after the hold ended still answers, so the daemon sees `Ended (Attached _)` more than once. The cycle keeps a bounded set of recorded tickets.
- A debug run is not a flake sample. It skips `FlakyDetection.recordResult` and `evaluateQuarantineForBatch`, because a test stopped at a breakpoint for ten minutes says nothing about stability.
- It skips `PendingRunSummary` (no "N passed" line appears later under an unrelated run).
- It records the verdict and `Output = None`. The duration the host measured includes time stopped at breakpoints. Record `TimeSpan.Zero` and make the row show no duration for a debug verdict. This is the one place I am unsure what the row renders for zero; step 3 reads that code first.

Wiring: a new `TuiEvent.DebugRunFinished of DebugRunRecord`, dispatched from `McpDebugTest.continueDebug` through `ctx.Dispatch` (the same hook `McpRunTests.runTests` uses) when the answer is `DebugProgress.Ended (DebugEnd.Attached result)`. Existing SSE (`test_results_batch`, `file_annotations`) carries the change to VS Code, Neovim and the dashboard, so no client needs new code for the gutter. `SageFsApp.fs` (2467 lines) and `LiveTestingTypes.fs` (3708) sit exactly at their `RatchetBudgets.fileSize` entries: the reducer arm must be net zero lines in `SageFsApp.fs`, so it is one line delegating to `DebugRunRecord.record`, paid for by moving a comparable block out in the same commit.

The host also has to leave coverage clean. A debug run executes instrumented code and sets probe bits that no `TakeCoverage` clears, so the next normal test's reading would include the debugged test's lines. `DebugHold`'s `runTest` wrapper calls `CoverageProbes.readAndClear` before and after and discards the reading. (Feeding it to the daemon as that test's coverage is possible later; it is out of scope here.)

### Slice 3: automatic runs wait while a test is held

A held or running debug test stops the whole host process at a breakpoint, so a keystroke eval or a type-check sent to that host would queue behind it until `LiveCheckRelay`'s 30 s bound. The VS Code command calls `POST /api/live-testing/pause` when the hold is granted and `/resume` in its `finally`, the way `docs/decisions.md` says an editor should. Resume must run on every exit path, including a detach and a dead attach, so the `finally` goes in `TestDebugCommand.debugTest` around `begin'`. A later daemon-owned pause (a `LivePause` case carrying why) would give Neovim the same for free. It changes a wire shape, so it is a separate step and a decision below.

### Slice 4: several tests, then evaluated code

Several tests. Change the slot to hold a head and a tail (so an empty batch cannot be built), and run them sequentially under one attach. `WorkerMessage.DebugTestBegin` carries the list too; that message is between the daemon and its own worker, which ship together.

```fsharp
type Hold =
  | Idle
  | Holding of ticket: DebugTicket * tests: (TestCase * TestCase list)
  | Awaiting of ticket: DebugTicket * tests: (TestCase * TestCase list)
  | Running of ticket: DebugTicket * remaining: TestCase list * finished: (TestId * TestResult) list
  | Ended of ticket: DebugTicket * outcome: DebugEnd   // Attached carries (TestId * TestResult) list
```

Parallel debugging is refused by construction: one process, one debugger, one thread stops everything. The request gains an optional `testIds` array beside `testId`; the response gains a `results` array and keeps the flat fields for the single-test case. Additive, so `apiVersion` stays 4. Per-test progress (the gutter updates as each test finishes, not at the end) needs `DebugProgress.StillRunning` to carry the finished-so-far list and the daemon to record the new ones per poll. Do the end-of-batch record first, per-test progress second. `TestControllerAdapter.debugHandler` passes every id in `request.include`. `sagefs.nvim` needs nothing; it keeps sending `testId`, and the plugin trailer is `Plugin: n/a, request and response are additive and Neovim debugs one test`.

Evaluated code. `DebugWire.Symbols` already says `"eval"` and `SymbolsNote` already tells the person to rebuild. Offer it: when `Symbols = eval` the extension asks "Build and debug the compiled copy?" and the daemon runs the confirming build in a throwaway worker, so the session's FSI state survives (`confirm-builds-without-restarting-the-worker`), then holds the test in that worker. The pid in the answer is the throwaway worker's. Until that item lands, the note stays and nothing else changes. The unsaved buffer cannot be debugged this way and the message says so.

## Tests

RED first, in this order. Names are proposals.

1. `sagefs-vscode/tests/TestDebugContractTests.fsx`: `holdBody "t1" "s1"` contains `"sessionId":"s1"` and `continueBody "debug-1-1" "s1"` the same. Fails today because the functions do not exist.
2. `SageFs.Tests/DebugRunRecordTests.fs` (default suite, pure): a cycle with test `T` discovered and `Passed` gets `Failed` after `record` of a failed debug result for `T`, with provenance `Compiled`; the same record twice changes nothing the second time; a `DefinedByEval` record gives `Evaluated`; the flake window and `PendingRunSummary` are untouched.
3. `DebugTestRoutesHttpTests.fs`: with a fake worker that answers `Ended (Attached (Failed ...))`, two `continue` calls dispatch exactly one `DebugRunFinished`.
4. `HostAgentDebugCoverageTests`: probes set during a held run are not visible to the next `TakeCoverage`.
5. Property (FsCheck): any interleaving of repeated `continue` calls and `Expire`/`HostEnding` events yields at most one record per ticket, and none for `NoDebuggerWithin`, `ReleasedWithoutDebugger`, `NoSuchHold`, `HostLost`.
6. Mutation: `DebugRunRecord.record` with the ticket check removed, with the flake skip removed, with `Basis` swapped. Each must be killed (`MutationTestingFramework.Mutant<'a>`).
7. Simulation: extend `TestDebugSim` with the daemon side. Pool: two sessions, three tests, tickets. Ops: begin, release with and without a debugger, repeat continue, host ends, expire. Invariants: a recorded verdict has an `Attached` end for the same ticket; the model holds at most one record per ticket; a record only ever lands in the session that began the hold. Twins: record on every continue (breaks idempotence), record `NoDebuggerWithin` as a failure (fabricates a failure), resolve the session by the daemon default (cross-session record). Each twin must violate its invariant; same seed gives the same trace.
8. Batch (slice 4): `Hold` property tests: results come back in the order requested, a failure in test 2 does not skip test 3, `HostEnding` mid-batch ends the rest as `HostLost`.

Status 2026-11-08: the row journey is written and NOT registered in `--integration-lt` (see the note in `LiveTestingShards.tree`). Its first run was the 0.6.940 release gate: the release answered 502 `worker_failed` (a confirming build replaced the worker under the held test; fixed with `fromRest`), and past that it waits on a debugger attach that was not shown to complete on the machine it was run on. It goes back into the tier when the attach-and-resume protocol has been run to green by hand.

Outcome gate. `SageFs.Tests/DebugRowJourneyTests.fs`, tier `--integration-lt`, built on `DebugAttachJourneyTests` (reuse `attachAndAsk`). After the attach and release, read `GET /api/live-testing/entries` (the route the harness already fetches in `LtStream.withEntries`) and assert the debugged test's row verdict. Matrix: failing test and passing test, compiled code, one and three tests. The eval cell asserts `symbols = "eval"` and the note, and the row says `Evaluated`. A missing debugger fails with the install command; it does not print SKIPPED. To retire the existing `SKIPPED` branches, `machineCondition` cases become failing cases when the tier is the self-hosted gate (`SAGEFS_REQUIRE_DEBUGGER=1`), one named switch.

VS Code real attach. Add a case to `sagefs-vscode/test-electron/ExtensionHostSuite.fs` that executes `sagefs.debugTest` for a failing test inside a real extension host. Microsoft's `coreclr` adapter is not redistributable to a CI image, so the test registers its own `DebugAdapterDescriptorFactory` for type `coreclr` that launches `netcoredbg --interpreter=vscode` (the debugger the Neovim journey already uses), and asserts on the daemon's entries, with no UI scraping. This proves the extension's sequence (hold, `startDebugging`, `onDidStartDebugSession`, continue, detach) against a real adapter. It does not prove Microsoft's adapter attaches, and the plan says so in the guide.

No wall-clock waits. Every wait ends on an event: the debug session's start/terminate events, the daemon's `/events` frames through `LtStream.expectFrame`, the hold's own `Ended`. The existing `Timeouts.debugAttachGrace` stays the host's bound and is not copied into a test.

## Steps

1. Slice 1 (RED test 1, then `TestDebugPure` bodies, then `SageFsClient` and `Extension.fs`). Files: `sagefs-vscode/src/TestDebugPure.fs`, `SageFsClient.fs`, `Extension.fs`, the contract test. Commit trailer: `Plugin: n/a, the VS Code client now sends what Neovim already sends`.
2. Host coverage hygiene (RED test 4). Files: `SageFs.Core/HostAgent.fs`. Parallel with 1.
3. Read how a `Passed` with zero duration renders (`LiveTestStatusView.fs`, `TestAnnotations.fs`); then `DebugRunRecord.fs` with tests 2, 5, 6. New file plus its fsproj entry and `SageFs.Tests` entries.
4. Wire `TuiEvent.DebugRunFinished`, the one-line reducer arm, `McpDebugTest.continueDebug` dispatch (tests 3, 7). Files: `SageFs/TuiEvent.fs`, `SageFs/SageFsApp.fs`, `SageFs/McpDebugTest.fs`, `SageFs.Simulation/TestDebugSim*.fs`. Needs 3.
5. Pause around the VS Code run (slice 3). Files: `TestDebugCommand.fs`, `SageFsClient.fs`. Needs 1. Parallel with 3 and 4.
6. The row journey (`DebugRowJourneyTests.fs`) and the extension-host case with the `netcoredbg` shim. Needs 4 and 1.
7. Docs: `LIVE_TESTING_GUIDE.md` (remove "not recorded in the live-testing state"), `how-live-testing-works.md` (the Debugging section and the Visual Studio table row), the roadmap landmark moves to `DebugRunRecord.fs`.
8. Batch hold (slice 4a), then per-test progress (4b), then `TestControllerAdapter.debugHandler` and a multi-select cell in the journey.
9. Compiled-copy offer for evaluated code, after `confirm-builds-without-restarting-the-worker`.

First week: steps 1 to 5 are file-disjoint enough for three sub-agents (client and pause; host coverage; record and wiring), and step 6 lands at the end of the week.

## Measure first

- A test stopped at a breakpoint for four minutes: does anything recycle the host? Experiment: hold a test, attach netcoredbg, hit a breakpoint, wait past `watchdogGracePeriod` (30 s, scaled), read `get_session_status` and the daemon log. If the worker or host is recycled, the debug session dies and slice 3 must also stop the watchdog from counting a held host as unresponsive, which is a `SessionManager` change and moves the size to L.
- How long `ms-dotnettools.csharp`'s adapter takes to attach to the host. The 5 s `debugAttachGrace` was set from netcoredbg's 1.5 s. I have no number for vsdbg. A manual run on a machine with Microsoft's VS Code gives it; if it exceeds the grace, the release path returns `released_without_debugger` for every real user.
- What `DebugHold` does for an in-process (non-isolated) session, where the host is the worker itself and a breakpoint freezes the worker's HTTP server. The continue route would stop answering.

## Risks and dependencies

- Microsoft's debugger is licensed for Microsoft's VS Code build. The proof above uses netcoredbg, which is an honest limit on what CI shows.
- `TestResultsBatchPayload.deriveCompletion` marks a batch Complete when it returned every requested result; a one-test debug record must not look like a full discovery to clients. Clients sweep on `DiscoveryGeneration`, so this should hold. Test 2 should assert the record does not bump `DiscoveryGeneration`.
- Depends on `confirm-builds-without-restarting-the-worker` for slice 4's evaluated-code half. The Built item `neovim-debug-a-failing-test` shares the routes: any wire change here needs the plugin's `KNOWN_STATUSES` and bodies checked in the same stretch.
- Enables: nothing in this group depends on it, but the out-of-run record path in step 4 is the pattern `run-tests-reuses-proven-results` follows for a reused verdict.

## Written and never run

**Update, 2026-11-10.** The first case (the row journeys) has run: both pass in `--integration-lt` against netcoredbg 3.2.0-1092 (see `debug-row-journeys-run-green`). The second ran once, `--integration-host --filter-test-list "VS Code command proof"` with `SAGEFS_REQUIRE_DEBUGGER=1`, with a VS Code 1.145.3 that `@vscode/test-electron` downloaded: the extension host started with the shim extension loaded, enabling live testing was proven, and `debugTestSequence` then failed with `the daemon has no discovered test to debug in this session` (it reads the rows 60 seconds after enabling). The cause is the proof's own daemon. It runs with `SAGEFS_HOT_RELOAD=true` and a HotReload session, and on that session discovery answers `ready_zero_tests`; the same sample with hot reload off discovers 11 tests (both read by hand with `/api/live-testing/status`). Setting the daemon's hot reload off was tried and did not give tests either, because the session's workflow is HotReload. The sequence after the rows (pause, `startDebugging`, continue, row) has still not run. Because a machine that has netcoredbg now offers the shim, this case is red on that machine until the fixture changes, whether or not `SAGEFS_REQUIRE_DEBUGGER` is set. The case also now waits for the rows to appear (`awaitEntries`) where it used to read them once.

Both cases below were written on 2026-11-07 on a machine with no usable debugger (`dotnet-debug` is installed, its licence is not accepted, and there is no `netcoredbg`). They compile and are registered in their tiers. Neither has executed, so each is a hypothesis until it has. The simulation, the mutation cases and every case named in the commit log as passing did run.

| Case | What it checks | Command that runs it |
|:--|:--|:--|
| `SageFs.Tests/DebugRowJourneyTests.fs`, two cases in `--integration-lt` | One test debugged: its row in `GET /api/live-testing/entries` shows the verdict the release answered and the provenance the hold named, with no normal run between. Three tests debugged one after another under one attach: each row shows its own verdict. | `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-lt --summary` |
| `sagefs-vscode/test-electron/ExtensionHostSuite.fs`, `debugTestSequence`, run by the `--integration-host` proof in `VscodeCommandProofTests.fs` | `sagefs.debugTest` in a real extension host, with the `coreclr` adapter the shim in `test-electron/coreclr-shim` contributes (it execs `netcoredbg --interpreter=vscode`). It watches the daemon: live testing goes paused while the test is held and live again after the debug session ends, then the test's row holds a verdict and a provenance. | `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary` (the runner offers the shim only when `netcoredbg` is on PATH) |

With `SAGEFS_REQUIRE_DEBUGGER=1` a machine without a debugger fails both with the install command. Without it they print `SKIPPED` and pass, which is why the gate that wants them sets it. What they cannot show: that Microsoft's `ms-dotnettools.csharp` adapter attaches. That is checked by hand, per release, with the second measurement below.

Guesses in them that the first run will confirm or break: the shape of a `TestId` in the entries JSON (a string or a one-field union), `program` in the shim's `package.json` running a shell script as the adapter, and `netcoredbg` accepting `attach` with `processId` over its `vscode` interpreter.

## Measure first: what it takes to run

Measurement 3 is answered from source and needs no run: a session is never in-process in production (`SageFs.Host/WorkerMain.fs` sets `FsiKind = SessionKinds.Isolated`, and `SageFs.Core/SessionKinds.fs` says production never constructs `InProcess`), so a breakpoint stops the FSI host and the worker's HTTP server keeps answering. The `continue` route is not frozen.

Measurements 1 and 2 need a real attach and a debugger. Will accepts the licence, not an agent:

1. Accept the licence once: `dotnet-debug attach --accept-license-agreement 1`. Put `netcoredbg` on PATH too if the extension-host case is to run (https://github.com/Samsung/netcoredbg/releases).
2. Measurement 1, a test stopped at a breakpoint for longer than `Timeouts.watchdogGracePeriod` (30 s, scaled): start a daemon and a session on a project with a test, `POST /api/live-testing/debug` with `{ "pattern": "<test name>", "sessionId": "<id>" }`, attach `dotnet-debug attach <pid>` (or `netcoredbg`) to the pid it names, set a breakpoint in the test with the debugger's own command, release with `POST /api/live-testing/debug/continue`, wait two minutes, then `get_session_status` for the session and read the daemon log for a recycle of the host or worker. A recycled host answers whether slice 3 must also teach the watchdog about a held host.
3. Measurement 2, how long `ms-dotnettools.csharp` takes to attach: in Microsoft's VS Code with the C# extension, run `sagefs.debugTest` on a test of a running session, and read the notification. A `released without debugger` answer means the attach took longer than `Timeouts.debugAttachGrace` (5 s, scaled), and the number to record is the Debug Console's time from the attach request to `Attached`. Repeat three times and write the three numbers here.

## What sagefs.nvim needs

Written 2026-11-07. The wire did not change (no field, route or `apiVersion` move), so nothing breaks. These are the things the plugin should take, as its own work, with a busted spec first for each and a check in real Neovim against the daemon:

1. Gutter and lens after a debug run. The daemon now records the verdict on the row, so the `test_results_batch` and `file_annotations` frames carry it. Add a spec that after `:SageFsDebugTest` finishes the sign and the panel row show the verdict with no `:SageFsRunTests`. Check the plugin does not drop a frame whose run generation did not move.
2. A debug verdict has no duration (the row's duration is zero) and the row's provenance says `Compiled` or `Evaluated`. Make the panel and the virtual text hide a zero duration instead of printing `0ms`, and show the provenance if it already does for normal rows.
3. Pause while held. VS Code pauses live testing for the session before it holds a test and resumes in every exit path, only when the status `Pause` field read `live` first. Neovim does not. Do the same in `debug_test.lua`: read `GET /api/live-testing/status?session=<id>`, `POST /api/live-testing/pause` with `{ "sessionId": ... }`, resume in the finally path.
4. Several tests (optional, parity). `debug_test.lua` could take a list and run hold, release, hold, release against the one attach, the way `TestDebugCommand.fs` does. The daemon needs nothing for it.

## Size

M for steps 1 to 7 (the Next scope: one to two weeks, mostly the record path and the extension-host proof), L overall with slice 4, because the batch hold changes the pure `Hold` type and its simulation.

## Decisions for Will

1. Where should auto-pause live? Recommendation: client-driven now (slice 3), daemon-owned later as a `LivePause` case with a reason, so Neovim gets it with the same commit and the dashboard can show "paused: debugging".
2. Should a debug verdict overwrite a normal verdict on the row? Recommendation: yes, it is the latest real run of that code. The debug origin is recorded in the daemon log and nowhere on the row.
3. Is the netcoredbg shim an acceptable stand-in for Microsoft's adapter in CI? Recommendation: yes, with the guide stating that Microsoft's adapter is checked by hand per release.
