# Live-testing: a queued run that never executes (observed 2026-09-25)

## What it looks like

Two integration-host tests fail under a full-suite run, and pass when run alone:

- `[Integration] HTTP API compiled live testing — editing a compiled F# file
  reruns tests against rebuilt output without an explicit rerun`
- `[Integration] MCP tool outcome gates — list_tests, explain_test_failure and
  diagnose report what live testing actually found`

Both die on their **baseline** assertion, before any edit is made:

```
baseline run should pass before editing the sample.
Run:   {"message":"Queued 3 test(s) for explicit run.","queued":3,"success":true}
Status:{"Summary":{"Total":3,"Passed":0,"Failed":0,"Stale":0,"Running":0},
        "DiscoveryState":"ready_with_tests"}
```

## It is not a regression from the type-migration work

| Run | Commit | Result | Duration |
|---|---|---|---|
| full `--integration-host` | `45b75eb1` | errored | 69s |
| **alone**, filtered | **`1b685a3d` (release, unmodified)** | **passed** | **10s** |

The same test passes on the unmodified release commit when run alone, so the
holder/restart/migration/rewrite work did not cause it.

## It is not a timing problem either — this was measured

I first assumed the queue was merely slow under load, and raised the budget
from 60s to 180s to test that. **The longer budget disproved it:**

| Budget | Test 1 | Test 2 |
|---|---|---|
| 60s | errored at 69s | errored at 129s |
| **180s** | **errored at 188s** | **errored at 368s** |

Each test consumed its *entire* budget and still reported
`Total: 3, Passed: 0, Failed: 0, Running: 0`. A slow run finishes inside a
large budget; a run that never starts finishes at no budget. So this is a
**live product defect**, and the earlier "load-sensitive flake" reading was
wrong.

## The shape

`Running: 0` with `Passed: 0` and `Failed: 0` is the tell: nothing is in
flight. The request was accepted and the work never happened.

`SageFs/McpServer.fs:3195` dispatches `TuiEvent.RunTestsRequested` after
replying `Queued N test(s) for explicit run` with `success: true`.
`SageFs/SageFsApp.fs:1606` handles that event and emits
`Features.LiveTesting.TestCycleEffect.RunRequestedTests`. The dispatch looks
correct; whatever consumes that effect on the worker side is not starting the
run.

This is the same class of problem the Lemmings roast found on the agent side:
**a claim that is not backed by an outcome.** `Queued`/`success` asserts work
was accepted, and `Total: 3` asserts tests exist, but neither means any test
ran. The MCP-side fix for that pattern shipped in
`SageFs/McpPushNotifications.fs`; this is the HTTP/live-testing side and it is
still open.

## Where the work is

Follow `Features.LiveTesting.TestCycleEffect.RunRequestedTests` to its
handler and find where the request stops becoming a `TestRunStartedAt`. The
answer is a worker-side dispatch bug, not a timeout or an HTTP route.

## How to re-check

```bash
# passes in isolation (10s), which is why it hides so easily
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll \
  --integration-host \
  --filter-test-case "editing a compiled F# file reruns tests against rebuilt output without an explicit rerun"

# the full tier, where it surfaces
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary
```
