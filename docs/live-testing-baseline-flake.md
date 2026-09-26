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

## What is left — the baseline has it too

**Measured on the unmodified release commit `1b685a3d` (no work of mine
checked out), full tier:**

```
248 tests run in 00:19:17.8620972 for Integration (host)
  – 246 passed, 2 ignored, 0 failed, 2 errored.
TRUST tier=--integration-host registered=250 ran=250 passed=246
  failed=0 errored=2 ignored=2 verdict=TestsFailed (0 failed, 2 errored)
```

That is **byte-identical to the result on my tree** (246 passed, 2 errored,
19m07s vs 19m25s). So this failure is pre-existing at v0.6.831 and is not a
regression from any of the type-migration work. It is recorded rather than
silently carried, because the number the gate demands is "no tier other than
default is worse than it was".

## What is established about the mechanism

1. **Not a timeout.** Budget 60s → 180s made it worse (69s/129s → 188s/368s).
2. **Not CPU saturation.** Under 48 CPU spinners on 16 cores the test still
   passes — 39.6s instead of 10s, green. So the machine being busy is not it.
3. **Not a regression from the type-migration work.** The unmodified release
   commit fails identically, above.
4. **Not a port race.** `TestPorts` proved a port free on IPv4 while the
   dashboard binds IPv6 `[::1]`; fixed in `ed9220c1`, which took
   `address already in use` from 4 occurrences to 0 and the tier from 245 to
   246 passed. Real bug, real fix, not this one.
5. **The starved resource is the worker's test proxy.** Both suites WAIT for a
   worker to reach a ready state; the other 250-real-daemon suites pay a 94MB
   private `SageFs.Core` adoption each (`HostCoreAdoption`), and that is the
   contention. Sequencing the two suites against each other (`72979fd9`) did
   **not** fix it, because the pressure comes from the other 248 suites, not
   from each other.

## The targeted next step, and what has been ELIMINATED since

Measured on real paths and real logs, in the live REPL where possible:

- **Not a timeout.** Budget 60s → 180s made it worse (69s/129s → 188s/368s).
- **Not CPU saturation.** 48 CPU spinners on 16 cores: still passes, 39.6s.
- **Not a regression from this work.** Unmodified `1b685a3d` fails identically.
- **Not a port race.** `ed9220c1` took `address already in use` from 4 → 0.
- **Not `HostCoreAdoption`.** This was my next hypothesis and it is wrong.
  Replaying `findCandidates`' exact predicate in the REPL: **0 of the 4 fixture
  projects have a `SageFs.Core.dll` under their `bin`**, including both failing
  suites' fixtures. Only **2 adoptions happen in the entire tier** (not 58), and
  the machine has 38GB RAM free. Adoption is a rounding error here, not the cause.
- **Not the session being unready.** The test's own earlier waits succeed in the
  failing run: `waitForReadySession` passes and discovery reports
  `ready_with_tests`, `Total=3`, `DiscoveryRequiresEval=false`,
  `CoverageBitmapStats.TestsWithCoverage=3`. The worker IS up and the tests ARE
  instrumented and runnable.

So the gap is narrow and specific: the session is Ready, the three tests are
discovered and instrumented, `RunRequestedTests` is dispatched (the API replies
`Queued 3 test(s)`, `success: true`), and the status then sits at
`Passed=0 Stale=3 Running=0` — nothing in flight, nothing ever reported.

`SageFsApp.fs:2919` handles the effect and calls
`deps.GetStreamingTestProxy sid`; on `None` it dispatches every test as
`NotRun`, which is exactly the observed state. So the proxy is absent (not
slow — the 15s deadline in `5a7be2c7` did not help), and
`DaemonMode.fs:1845` resolves it from `snapshot.WorkerBaseUrls`. **The open
question is why that map does not contain the session's worker under
concurrency**, and it needs one instrumented failing run to answer: log the
`WorkerBaseUrls` keys and the proxy lookup at the moment the effect is handled.

That is a targeted change to `SageFsApp`/`DaemonMode` plus one run, not a
guess. Until then this is a recorded, bounded open item rather than a claim.

## How to re-check

```bash
# passes alone (~10s)
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll \
  --integration-host \
  --filter-test-case "editing a compiled F# file reruns tests against rebuilt output without an explicit rerun"

# 2 errored, on my tree AND on the unmodified release commit
dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary
```
