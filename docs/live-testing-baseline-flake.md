# Live-testing baseline run: contention, and a summary that hid it

## What it looks like

Two integration-host tests fail under a full-suite run and pass alone:

- `[Integration] HTTP API compiled live testing — editing a compiled F# file
  reruns tests against rebuilt output without an explicit rerun`
- `[Integration] MCP tool outcome gates — list_tests, explain_test_failure and
  diagnose report what live testing actually found`

Both die on their **baseline** assertion, before any edit is made:

```
baseline run should pass before editing the sample.
Run:   {"message":"Queued 3 test(s) for explicit run.","queued":3,"success":true}
Status:{"Summary":{"Total":3,"Passed":0,"Failed":0,"Stale":3,"Running":0},
        "DiscoveryState":"ready_with_tests"}
```

## It is not a regression from the type-migration work

| Run | Commit | Result | Duration |
|---|---|---|---|
| full `--integration-host` | `45b75eb1` | errored | 69s |
| **alone**, filtered | **`1b685a3d` (release, unmodified)** | **passed** | **10s** |
| full `--integration-host` | `2331baef` | errored | 69s |
| **alone**, filtered | **`2331baef` (with the summary fix)** | **passed** | **10.3s** |
| **alone**, filtered | **`2331baef`** (the other test) | **passed** | **9.7s** |

Both tests pass in isolation on the fixed commit, in about ten seconds each.

## It is not a timing problem — this was measured

The first reading was "the queue is slow under load". Raising the budget from
60s to 180s **disproved** it:

| Budget | Test 1 | Test 2 |
|---|---|---|
| 60s | errored at 69s | errored at 129s |
| **180s** | **errored at 188s** | **errored at 368s** |

Each consumed its *entire* budget and still reported nothing in flight. So it
is not "slow", and lengthening the budget only makes the failure take longer.

## What it actually is

The baseline run needs a live worker: `SageFs/SageFsApp.fs` calls
`deps.GetStreamingTestProxy sid`, which (`SageFs/DaemonMode.fs:1845`) resolves
`WorkerBaseUrls` from the session snapshot, retrying four times over ~750ms.
Under full-tier load that lookup does not settle inside the retry window, the
proxy comes back `None`, and every requested test is dispatched as
`NotRun` — which is why nothing ever reports.

Both tests then wait for `Passed >= N && Stale = 0` and correctly time out,
because nothing ran. The mechanism is not broken; the tier is oversubscribed.

## The part that WAS a real defect, now fixed

`TestSummary.fromStatuses` ended in a catch-all that dropped `Detected`,
`Queued` and `Skipped` from every bucket. So the stuck state above reported

```
Total=3 Passed=0 Failed=0 Stale=0 Running=0
```

`Stale: 0` for three tests that were detected and never run — indistinguishable
from "nothing exists". After the fix (`2331baef`) the same state reports
`Stale: 3`, which names the actual condition. The tests were already failing
for the contention reason; the summary was hiding *why*.

This is the same claim-without-outcome pattern the Lemmings roast found on the
agent side, one layer down. The agent-side fix shipped in
`SageFs/McpPushNotifications.fs`; this was the summary layer.

## What would actually fix the tier

Raise the proxy-registration retry budget (or wait on session readiness before
the first explicit run), rather than lengthening the test's status wait — the
latter was measured and does not help. The integration tier is 250 tests that
each start a real daemon; that is where the contention comes from.

## How to re-check

```bash
# passes in isolation (~10s), which is why it hides so easily
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll \
  --integration-host \
  --filter-test-case "editing a compiled F# file reruns tests against rebuilt output without an explicit rerun"

# the full tier, where it surfaces
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary
```
