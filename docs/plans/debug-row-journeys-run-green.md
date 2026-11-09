# The debug-row journeys run green, then go back in the tier

Roadmap item: `debug-row-journeys-run-green` (Next, LiveTesting). Plan written 2026-11-08 against master 213fbc10.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S to M. Most of it is one hand run against a real attach. |
| Branch and worktree | none yet |
| Last green commit | none yet. The journeys are `SageFs.Tests/DebugRowJourneyTests.fs` as committed in 213fbc10, registered with no tier. |
| Next action | Run the one-test journey by hand against a real attach, with a debugger installed (`dotnet-debug` is in `~/.dotnet/tools` on this machine), and watch what the debugger does between the hold and the release. Write what you see into "Surprises". Only then change `resume` or the order of the calls. |
| Decisions made | 2026-11-08, Will: unregister both journeys and ship, rather than ship a hypothesis. They sit out of `LiveTestingShards.tree` and are plain `testCaseTask` values so nothing expects them. |
| Surprises | The release gate was their first run and it failed twice over. The release answered 502 `worker_failed` ("Connection reset by peer") because a confirming build had replaced the worker under the held test: the journey before restores `Hello.fs`, which asks a build to confirm. `fromRest` now waits for the confirmation phase to be `idle` first. Past that, the case waits on an attach that was never shown to finish. |

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
