# Integration tier: two live-testing baseline tests fail only under the full tier

## Current state (2026-09-25)

| Run | Result |
|---|---|
| **each test alone**, `--filter-test-case` | **passed**, ~10s |
| full `--integration-host` (250 tests) | 2 errored, 246 passed |

The two:

- `[Integration] HTTP API compiled live testing — editing a compiled F# file
  reruns tests against rebuilt output without an explicit rerun`
- `[Integration] MCP tool outcome gates — list_tests, explain_test_failure and
  diagnose report what live testing actually found`

Both die on their baseline assertion with

```json
{"Summary":{"Total":3,"Passed":0,"Failed":0,"Stale":3,"Running":0}}
```

## What has been established, and how

1. **Not a timeout.** Raising the status budget 60s → 180s made it worse, not
   better (69s/129s → 188s/368s). Each test consumed its whole budget with
   nothing in flight.

2. **Not a regression from the type-migration work.** The same tests pass on
   the unmodified release commit `1b685a3d` when run alone.

3. **The run is dispatched and the proxy is not merely slow.** `Stale: 3` with
   `Running: 0` means three tests are discovered and none started.

4. **The port collision was real and is fixed** (`ed9220c1`). The suite's own
   message blamed "the port taken between reservation and bind", which sent me
   looking for a timing race that does not exist — 120 concurrent reservations
   produced zero collisions. The actual fault: `TestPorts.tryReservePair`
   proved a port free by binding IPv4 `127.0.0.1`, but the dashboard binds IPv6
   `[::1]`. Different address, same port number. That produced three
   `address already in use` failures; after the fix there are **zero** of them
   and the tier went from 245 to 246 passed.

5. **The mechanism is not broken.** Both tests pass alone in ~10 seconds, on
   the current tree, repeatedly.

## What is left

The two tests depend on a live worker reaching a registered `WorkerBaseUrls`
entry inside a 60s window, while the tier runs 250 real-daemon suites in
parallel. Something in that contention keeps the worker's test proxy from
becoming available in time. The two candidate causes, neither yet confirmed:

- the worker's *first* run is the expensive one (compile + instrument), so under
  contention 60s is genuinely not enough; or
- the worker is failing to register at all under contention, and the 15s proxy
  wait added in `5a7be2c7` simply expires.

`5a7be2c7` (proxy wait 750ms → a 15s deadline) is a correct change on its own
terms and is measured, but it did **not** turn these two green, so the
contention cause is elsewhere. It is kept because a fixed 750ms attempt count
was demonstrably the wrong shape; it is not claimed as the fix.

## How to re-check

```bash
# passes alone (~10s) — the isolation result is the honest one
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll \
  --integration-host \
  --filter-test-case "editing a compiled F# file reruns tests against rebuilt output without an explicit rerun"

# fails under the full tier
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary
```

A targeted next step: run the tier with the two live-testing suites isolated
from the other 248 and see whether they still fail. If they pass, the cause is
cross-suite contention and belongs in the tier's scheduling, not in either test.
