# The debug-row journeys run green, then go back in the tier

Roadmap item: `debug-row-journeys-run-green` (Next, LiveTesting). Plan written 2026-11-08 against master 213fbc10.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Blocked on a managed debugger. The hand run is done (2026-11-09) and it shows the journeys cannot pass with `dotnet-debug`. |
| Size | S once a managed debugger (netcoredbg) is on the gate machine and its console protocol has been run once. |
| Branch and worktree | none. Hand run done in a reflink clone of the repo (`~/Work/SageFs-debugrun-clone`, a scratch copy) because the daemon's file watcher takes in the whole repo, and other agents' edits in the shared tree restarted the worker under the held test. |
| Last green commit | none. The journeys are `SageFs.Tests/DebugRowJourneyTests.fs`, registered with no tier. |
| Next action | Put netcoredbg on PATH on a machine (https://github.com/Samsung/netcoredbg/releases), repeat the hand run below with it: hold a test, attach netcoredbg to the pid (check its real flag) with its console on stdin, find the line that continues the process, release, read the answer. Fix `resume` and the attach arguments from that, then follow "Steps" 3 to 5. Then set `SAGEFS_REQUIRE_DEBUGGER=1` where the tier runs, in the same change that puts netcoredbg on the gate machine: today the extension-host debug case in `VscodeCommandProofTests.fs` also reads the variable and needs netcoredbg, so setting it first turns `--integration-host` red. |
| Decisions made | 2026-11-08, Will: unregister both journeys and ship, rather than ship a hypothesis. They sit out of `LiveTestingShards.tree` and are plain `testCaseTask` values so nothing expects them. 2026-11-09: they stay unregistered, `SAGEFS_REQUIRE_DEBUGGER` is not added to the gate (reason above), and the row journeys refuse `dotnet-debug` outright. |
| Surprises | See "What the hand run showed" below. The release gate was the journeys' first run and it failed twice over: the release answered 502 `worker_failed` ("Connection reset by peer") because a confirming build had replaced the worker under the held test (the journey before restores `Hello.fs`, which asks a build to confirm; `fromRest` now waits for the confirmation phase to be `idle` first), and past that the case waited on an attach that was never shown to finish. |

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

- `SageFs.Tests/DebugRowJourneyTests.fs` holds both cases. Their header comment says why they are not registered.
- `DebugAttachJourneyTests` is in the tier and green: a real debugger attaches to the process the daemon names, lists its threads, and the held test runs when the debugger lets it go. That proves the attach and not the verdict row.
- A debugger stops the process it attaches to until it is told to continue. The journeys call `resume`, which writes `continue` to the debugger's standard input before the release. Nobody has run that against a working attach, so it is a guess about the protocol.
- The daemon records a debug verdict on the row with its provenance and no duration, and keeps its table of granted holds per daemon (`HeldDebugTargets`, 703a55b1).
- A refused hold or release fails the journey with the daemon's own answer. It used to print SKIPPED, which read as a pass.

## Steps

1. By hand: start a daemon on the FromCSharp sample, hold one test, attach, and watch the debugger's output. Does it stop? Does `continue` on standard input go on? Does the release then answer with an outcome? Write down what each step prints.
2. Fix `resume` (or the call order) from what step 1 showed. If the protocol needs the debugger's own request rather than a line on standard input, use that.
3. Run `oneTestRow` alone, then `threeTestsOneAttach` alone, then both in the tier's order. Each must show `ran>0` in the `TRUST` line.
4. Put `DebugRowJourneyTests.journeyTests` back in `LiveTestingShards.tree` and register the cases with `Integration.dedicatedCaseTask "--integration-lt"`. The shard test refuses a registered case that is not in the tree, and that is the check the move is complete.
5. Update the comment in `LiveTestingShards.fs` and the header of the journeys file, and set this plan Done with the landmark that proves the tier holds them.

## Tests

The journeys are the tests. No new case is needed, only the evidence that they pass for the right reason. Each one waits on the daemon's own status and the stream's frames and never on a timer.

## Risks and dependencies

- Whether the debugger's protocol allows this at all is open. If it does not, the journeys change shape (the debugger attaches, the test runs to the end under it) and the plan says so before any code.
- The sandbox the gate's tiers run in may stop `ptrace`. `HostAgent` already asks for `PR_SET_PTRACER` and Yama's `ptrace_scope` is 1 on this machine, so an attach from a child of the daemon works here. Check a tier clone before relying on that.

## Size

S to M. The hand run decides it.

## Decisions for Will

None. Plain follow-through on the call he made on 2026-11-08.
