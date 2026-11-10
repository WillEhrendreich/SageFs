# The debug-row journeys run green, then go back in the tier

Roadmap item: `debug-row-journeys-run-green` (Next, LiveTesting). Plan written 2026-11-08 against master 213fbc10.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done. Both row journeys and four more debug cases run green in `--integration-lt` against netcoredbg 3.2.0-1092: three full unfiltered runs in a row, `TRUST tier=--integration-lt registered=19 ran=19 passed=19 ... Trusted` each (2026-11-10). A fourth run, on the last commit (which only moved netcoredbg's working directory to the temp area), passed all six debug cases and failed three older cases (`LT restart` twice, one browser case) while another gate started beside it and the load average was 21; those three passed in the three runs before. |
| Size | S |
| Branch and worktree | `dbg-rows` in the clone `/home/will/scratch/dbg-agent` (2026-11-10, merge by the orchestrator). |
| Last green commit | the tip of `dbg-rows` when this block was written; the commits are listed in the hand-off report. |
| Next action | None for this item. What is left is in [debug-failing-tests-in-vscode.md](debug-failing-tests-in-vscode.md): the extension-host case (`sagefs.debugTest` in a real VS Code, netcoredbg as the `coreclr` adapter) cannot find a test to debug in the proof's hot-reload fixture, and the hosted pull-request job has no netcoredbg, so it says SKIPPED there. |
| Decisions made | 2026-11-08, Will: unregister both journeys and ship, rather than ship a hypothesis. 2026-11-10: they are registered again (`Integration.dedicatedCaseTask "--integration-lt"`, in `LiveTestingShards.tree`) because they have now passed, and `scripts/local-gate.fsx` sets `SAGEFS_REQUIRE_DEBUGGER=1` (`ReleaseRules.debuggerRequirementEnvironment`) because this machine has netcoredbg: a gate machine without one fails them with the install command. The breakpoint case is FIRST in the tree: it needs compiled code, and every journey that edits `Hello.fs` leaves the tests defined by eval (the hold then says `symbols: eval` and the case fails saying so). |
| Surprises | See "What the hand run showed" below. The release gate was the journeys' first run and it failed twice over: the release answered 502 `worker_failed` ("Connection reset by peer") because a confirming build had replaced the worker under the held test (the journey before restores `Hello.fs`, which asks a build to confirm; `fromRest` now waits for the confirmation phase to be `idle` first), and past that the case waited on an attach that was never shown to finish. |

## What the hand run with netcoredbg showed (2026-11-10)

Setup: a throwaway daemon from the Release build of this branch on port 38931 with its own `SAGEFS_DATA_DIR` and `SAGEFS_HOT_RELOAD=false` (how the `--integration-lt` runner starts one), a session on a copy of the FromCSharp sample, live testing enabled, 11 tests passed. The debugger is netcoredbg 3.2.0-1092 (`~/.local/bin/netcoredbg`), started as `netcoredbg --interpreter=cli --attach <pid>` with its stdin on a FIFO. `kernel.yama.ptrace_scope` is 1 on this machine. Every line below was read back from a file.

1. Hold: `{"status":"held","message":"Attach a .NET debugger to process 2768295, then continue to release the test.","pid":2768295,"ticket":"debug-2768295-1","testId":"C0BFBBB98079B555","testName":"add infers int","symbols":"compiled","symbolsNote":"","access":"open","accessNote":"","holdMs":240000,...}`.
2. Attach: after a screen of `library loaded: ...` and `thread created, id: ...` lines the debugger printed `stopped, reason: interrupted, thread id: 2768295, stopped threads: all, frame={System.Net.Sockets.dll` Interop.Sys.ReceiveMessage()}`. The process is stopped, every thread reads `S (sleeping)`, and `TracerPid` is 0 on all 13 threads: netcoredbg does not use ptrace for a managed attach.
3. `continue` on its console printed `^running`. The release then answered at once (HTTP 200, 0.036 s): `{"status":"attached","message":"The test ran under the debugger and finished.",...,"outcome":"passed","detail":"","durationMs":0.1668}`. So `Debugger.IsAttached` was true in the host and the test ran. The release is `attached` with the verdict, and the order is attach, `continue`, release. A release sent before `continue` would find the host stopped.
4. The row after it: `"Status":{"Case":"Passed","Fields":["00:00:00"]},"PreviousStatus":{"Case":"Passed","Fields":["00:00:00.0001472"]},"Provenance":{"Case":"Compiled"}`. The verdict is the debug run's, with no duration, and the previous status is the baseline's with its real duration. A normal run would have a duration, which is what the journeys assert on to say no normal run happened.
5. Breakpoints did not bind. `break <sample>/Hello.fs:109` answered `Breakpoint 1 at ...Hello.fs:109 --pending, warning: No executable code of the debugger's target code type is associated with this line.` and the library list said `library loaded: /tmp/sagefs-shadow-2759671-cd162abd/SageFs.Samples.FromCSharp.dll` / `no symbols loaded, base address: 0x7f4ec8546000, size: 23552(0x5c00)`. Cause, read with `System.Reflection.Metadata` on the DLL: the CodeView entry of the instrumented copy names `.../SageFs.Samples.FromCSharp.instrumented.pdb`, and the pdb that sits beside it is `SageFs.Samples.FromCSharp.pdb` (the move after the write renames it). The debugger looks for the name in the entry. After `cp SageFs.Samples.FromCSharp.pdb SageFs.Samples.FromCSharp.instrumented.pdb` and a fresh attach it said `symbols loaded`. That is a product defect in `CoverageInstrumenter.instrumentAssemblyInPlace`, fixed in this branch with a RED case first (`associated=(false, null)` before, `(true, ".../Gen.pdb")` after): without it no breakpoint in the person's test could bind under a live session, whatever the hold said (`symbols: compiled`).
6. With the fix's equivalent in place: `break <sample>/Hello.fs:49` (the `let add` line) answered `Breakpoint 2 at ...Hello.fs:49` with no `pending`, `info break` showed `Rslvd y`. After `continue` and the release the debugger printed `stopped, reason: breakpoint 2 hit, thread id: 2848281, stopped threads: all, times= 1, frame={SageFs.Samples.FromCSharp.Hello.add() at .../Hello.fs:49}` and the release answered after 30.042 s: HTTP 504 `{"status":"host_not_answering","message":"The test host (process 2768295) did not answer in 30 seconds. A host answers nothing while a debugger has stopped it at a breakpoint or an inspector has suspended it. Process 2846024 (netcoredbg) names it as its target. Continue it in the debugger, or detach the debugger or quit the inspector, then ask again: the ticket is still valid. ..."}`. The holder was found through the command line (`NamedAsTarget`), not through `TracerPid`, which stayed 0. After `continue` the same ticket answered `{"status":"attached",...,"outcome":"passed","durationMs":35858.4494}` and the session's health read Healthy.
7. A ticket across a confirming build, session holding nothing else: hold (`debug-2768295-4`), then an edit to `Hello.fs`. The confirmation went `idle` → `building` → `idle` and `/health` `workerPid` changed from 2759671 to 2874872: the build swapped the worker. The release answered `{"status":"no_such_hold","message":"The test host holds no test under that ticket. It restarted, or the hold ran out. Start debugging again."}` (HTTP 409). That is a product defect (a held test is something a swap loses, and the venue did not count it); fixed in this branch, see "As built".
8. The same with a session that holds something: `POST /exec {"code":"let handBound = 42;;"}`, hold (`debug-2876068-1`), edit `Hello.fs`, confirmation `quiet` → `building` → `idle`: `workerPid` stayed 2874872, the host pid stayed 2876068, attach, `continue`, release answered `attached` / `passed`, and `handBound + 1;;` answered `val it: int = 43`.

## As built (2026-11-10)

- `SageFs.Core/Features/CoverageInstrumenter.fs`: the instrumented copy is staged under the original's file name, so the debug directory names the pdb it has after the move (`WHY: an assembly instrumented in place still has symbols a debugger finds beside it`).
- `SageFs.Core/ConfirmationVenue.fs`, `WorkerProtocol.fs`, `SageFs/ConfirmWorkers.fs`, `SageFs/McpDebugTest.fs`: a hold is noted when the proxy forwards a begin or a release, taken back when a release hears `Ended`, and lapses after `Timeouts.debugHold` with no news. It is a kept state, so the confirmation runs in a throwaway worker.
- `SageFs.Tests/DebugAttachJourneyTests.fs` and `DebugRowJourneyTests.fs`: driven by netcoredbg's console, waiting on its lines (`stopped, reason:`, `^running`) and the daemon's answers. Cases: attach and release, breakpoint holds the host, ticket across a confirming build with and without a hand-bound value, one test's row, three tests' rows under one attach.
- `scripts/local-gate.fsx` passes `SAGEFS_REQUIRE_DEBUGGER=1` (`ReleaseRules.debuggerRequirementEnvironment`); the hosted pull-request job does not, and says SKIPPED.

## What the hand run showed (2026-11-09)

Setup: a throwaway daemon from the Release build (`--mcp-port 38921 --owner-pid 1 --no-resume`, `SAGEFS_DATA_DIR` and `SAGEFS_HOT_RELOAD=false` set as the `--integration-lt` runner sets them, never the user's 37749/37750), a session on a copy of the FromCSharp sample, live testing enabled, 11 tests passed, confirmation `idle`. Then `POST /api/live-testing/debug` for `add infers int`, `dotnet-debug attach <pid>` with its stdin on a FIFO so each command could be sent and read back, then `POST /api/live-testing/debug/continue`.

- The hold answered at once: `{"status":"held", ... "symbols":"compiled","access":"open", ... "ticket":"debug-3983416-1"}`.
- `dotnet-debug attach <pid>` printed `Attaching to process: 3983416 ...`, then `Ready to process analysis commands. Type 'help' to list available commands`. Its `help` lists `clrstack`, `clrthreads`, `dumpheap`, `gcroot`, `threads` and other SOS commands, and nothing that continues, steps or sets a breakpoint. `dotnet-debug attach --help` says `Starts an interactive shell with debugging commands to explore a process` and its `--commands` text says `dotnet dump`. It is `dotnet-dump analyze` against a live process, not a debugger.
- `threads` answered `*0 0x3D5FD1 (4022225)` and eight more lines, so the attach itself works.
- `continue` answered `ERROR: Unrecognized SOS command 'continue'`. So `resume` in the journeys never did anything.
- While the inspector is attached the host does not answer. `POST .../debug/continue` sent with it attached got no answer in 120 seconds (`HTTP 000 in 120.002108s`, the client's own bound). `TracerPid` stays 0 on every thread and every thread reads `S (sleeping)`, so this is not a ptrace stop: the runtime is suspended.
- `quit` on its console gives the host back. A release sent while it was attached answered `still_running` 45 seconds after it was sent (25 seconds until `quit`, then one park of 20 seconds), and the next release answered `released_without_debugger` (HTTP 409): `The test was released but no debugger was attached to the test host, so it did not run.` So `Debugger.IsAttached` was false for the inspector, and the test never ran.
- Killing it instead of quitting leaves the host frozen for good: after `kill -9` of the debugger, three releases in a row got no answer (`HTTP 000 in 120.001248s`, then `HTTP 000 in 120.000858s`) and the session stayed `Ready` in `GET /api/sessions` the whole time. Only ending the daemon cleared it.
- `DebugAttachJourneyTests` (in the tier) kills the debugger before the release, so on this machine its release never answers, `postOrTimeout` returns `None`, and the case prints `SKIPPED the attach: the daemon did not answer the release` and passes. It has never asserted `attached`. It is last in the tree, so the frozen host hurt nothing after it.
- The row after the released-without-debugger answer was unchanged: `"Status":{"Case":"Passed","Fields":["00:00:00.0001999"]},"PreviousStatus":{"Case":"Running"},"Provenance":{"Case":"Compiled"}`, the baseline's verdict.

What this means for the plan: the journeys' premise (a debugger that stops the process, takes `continue`, and lets the release run the test) needs a managed debugger. `dotnet-debug` cannot be one. netcoredbg is the one the Neovim journey and the extension-host case already name. It was not on this machine, the hand run could not fetch it (a download of a third-party binary was refused by the harness, and no one has installed it), and the gate machine has none, so the console protocol of step 2 is still unknown.

What changed in the tests from this: `DebugAttachJourneyTests` quits the debugger cleanly and then asks the release until it stops saying `still_running`, treats a release that never answers as a failure, and asserts what each kind of debugger can do (`DebuggerKind`: an inspector ends in `released_without_debugger`, a managed debugger in `attached`). The row journeys accept only a managed debugger and fail or skip with where to get one.

A defect outside this item's files, fixed on 2026-11-10 by another change: a host that is suspended answers nothing, and `FsiHostClient.AgentDebugContinue` waited for it with `CancellationToken.None`, so the daemon's release route never answered either. A debugger stopped at a breakpoint suspends the same way. The wait now ends at the host's park plus `Timeouts.debugHostAnswerGrace`, and the route answers `host_not_answering` (HTTP 504, not `host_lost`: the host is alive and its ticket stays valid) with the host's pid, what holds it when that can be found, and the next action. The session reads `Degraded` with the same words while the daemon holds a note of the silence. See "A host that answers nothing" in [LIVE_TESTING_GUIDE.md](../LIVE_TESTING_GUIDE.md). For these journeys: a release sent while the debugger is attached or after it was killed now answers `host_not_answering` and no longer never answers, so `postOrTimeout` returning `None` is no longer the way that case shows up, and a killed `dotnet-debug` leaves the host silent for good, so the release keeps answering `host_not_answering` until the session is hard reset.

## What done looks like

After a test is debugged, the row the user would see (the row behind `GET /api/live-testing/entries`, which every client reads) shows the verdict the debugger run gave it, with the provenance the daemon named when it held the test, and no normal run happened in between. Two cases check that: one test, and three tests under one attach. They have run green from the tier three times in a row on a loaded machine and an idle one, and a machine with no debugger fails with the install command rather than skipping when `SAGEFS_REQUIRE_DEBUGGER=1`.

## What exists today

- (Written 2026-11-08, before the work. As of 2026-11-10 the bullets below that name the journeys as unregistered or `resume` as a guess are out of date: see "As built" above.)
- `SageFs.Tests/DebugRowJourneyTests.fs` holds both cases. Their header comment says why they are not registered.
- `DebugAttachJourneyTests` is in the tier and green: a real debugger attaches to the process the daemon names, lists its threads, and the held test runs when the debugger lets it go. That proves the attach and not the verdict row.
- A debugger stops the process it attaches to until it is told to continue. The journeys call `resume`, which writes `continue` to the debugger's standard input before the release. Nobody has run that against a working attach, so it is a guess about the protocol.
- The daemon records a debug verdict on the row with its provenance and no duration, and keeps its table of granted holds per daemon (`HeldDebugTargets`, 703a55b1).
- A refused hold or release fails the journey with the daemon's own answer. It used to print SKIPPED, which read as a pass.

## Steps

All five are done (2026-11-10). The hand run is the section "What the hand run with netcoredbg showed". `resume` is `continue` on netcoredbg's console, waited on by its `^running` line, and it comes after the attach and before the release. The journeys ran alone and then in the tier (`ran=19`, the 19 cases of the whole `--integration-lt` tree, three Trusted runs in a row). `DebugRowJourneyTests.journeyTests` is in `LiveTestingShards.tree` and its cases are registered with `Integration.dedicatedCaseTask`; the shard test agrees with the registry.

## Tests

The journeys are the tests. No new case is needed, only the evidence that they pass for the right reason. Each one waits on the daemon's own status and the stream's frames and never on a timer.

## Risks and dependencies

- Whether the debugger's protocol allows this at all is open. If it does not, the journeys change shape (the debugger attaches, the test runs to the end under it) and the plan says so before any code.
- The sandbox the gate's tiers run in may stop `ptrace`. `HostAgent` already asks for `PR_SET_PTRACER` and Yama's `ptrace_scope` is 1 on this machine, so an attach from a child of the daemon works here. Check a tier clone before relying on that.

## Size

S to M. The hand run decides it.

## Decisions for Will

None. Plain follow-through on the call he made on 2026-11-08.
