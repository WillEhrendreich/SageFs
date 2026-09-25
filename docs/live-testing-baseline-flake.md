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

## What is left — ANSWERED: cross-suite contention

The isolation experiment settles it. Three runs:

| Run | Result |
|---|---|
| test 1 alone (`--filter-test-case`) | **passed**, 10.7s |
| test 2 alone (`--filter-test-case`) | **passed**, 9.8s |
| test 1 via `--filter-test-list` (its own suite only) | **passed**, 10.9s |

Both tests pass when the other 248 suites are excluded. They fail only when all
250 run together. So the cause is **contention from the other suites**, not
either test, and not the port race already fixed in `ed9220c1`.

Expecto runs test LISTS in parallel, and this tier starts 58 daemons across 62
ports. The two live-testing suites are the ones that wait on a worker reaching
a ready state, so they are the ones that lose. The remaining work belongs in
the tier's **scheduling** — sequencing the suites that wait on a worker, or
bounding the tier's parallelism — not in either test and not in the product.

`5a7be2c7` (the 15s proxy deadline) is a correct change on its own terms and is
kept, but it did not turn these green and is not claimed as the fix.

## How to re-check

```bash
# passes alone (~10s)
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll \
  --integration-host \
  --filter-test-case "editing a compiled F# file reruns tests against rebuilt output without an explicit rerun"

# fails under the full tier
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary
```
