# Live-testing baseline-run flake (observed 2026-09-25)

## What it looks like

Two integration-host tests fail identically under a full-suite run, and pass
in isolation:

- `[Integration] HTTP API compiled live testing — editing a compiled F# file
  reruns tests against rebuilt output without an explicit rerun`
- `[Integration] MCP tool outcome gates — list_tests, explain_test_failure and
  diagnose report what live testing actually found`

Both die on their **baseline** assertion, before any edit is made:

```
baseline run should pass before editing the sample.
Run:   {"message":"Queued N test(s) for explicit run.","queued":N,"success":true}
Status:{"Summary":{"Total":N,"Passed":0,"Failed":0,"Stale":0,"Running":0},...}
```

## Evidence it is not a regression

| Run | Commit | Result | Duration |
|---|---|---|---|
| full `--integration-host` | `45b75eb1` (this work) | errored | 69s |
| **alone**, filtered | **`1b685a3d` (release, unmodified)** | **passed** | **10s** |
| full `--integration-host` | `45b75eb1` (this work) | errored | 69s |

The same test passes on the *unmodified release commit* when run alone, so the
failure is not caused by the holder/restart/migration/rewrite work. It is
sensitive to machine load: under a full-suite run the baseline run is queued
but never reaches `Passed >= N` inside its 60s window, so the test reports
`0 passed, 0 failed` and gives up.

## The shape of the bug

`POST /api/live-testing/run` returns `Queued N test(s) for explicit run` and
`success: true`, but the run is asynchronous. The test then polls
`waitForLiveTestingStatus` with a 60-second budget for
`Passed >= N && Failed = 0 && Running = 0 && Stale = 0`. Under load the queue
is not drained inside 60s, and the assertion reports the *stuck* state
(`0 passed, 0 failed`) rather than "the baseline never completed", so the
failure message is actively misleading: it reads as a broken product.

This is the same class of problem the Lemmings roast found on the agent side —
`0 passed, 0 failed` next to a discovered-test count is a claim a reader will
misread. The MCP-side fix (reporting `not run` explicitly) is in
`SageFs/McpPushNotifications.fs`; the HTTP-side fix would be to make the
baseline wait either for completion or for a named timeout, and to say which.

## Why it is not fixed here

It is pre-existing, it is load-dependent, and fixing the wait semantics means
changing a real live-testing behaviour with its own tests. It is recorded here
rather than folded into the type-migration work, so the next person does not
re-diagnose it from scratch. When it is fixed, the fix belongs with the
`waitForLiveTestingStatus` callers in `SageFs.Tests/HttpApiIntegrationTests.fs`
and `SageFs.Tests/McpToolOutcomeTests.fs`.

## How to re-check

```bash
# passes in isolation
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll \
  --integration-host \
  --filter-test-case "editing a compiled F# file reruns tests against rebuilt output without an explicit rerun"

# the full tier, where it surfaces
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary
```
