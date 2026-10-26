# run_tests reuses proven results

Roadmap item: `run-tests-reuses-proven-results` (Later, Live testing). Plan written 2026-10-26 against master `59a8692c`.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a stored pass under the current key is reused. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

You or an agent call `run_tests` on a session twice with no change to anything a test can see. The second receipt says `38 passed (36 reused from the run at 14:02, identical inputs)`, names the two that ran, and comes back in a fraction of the time. Change one covered file and exactly the tests that cover it run again. Anything the cache cannot vouch for runs: a test with no coverage, a failure, a flaky or property test, a test that reads files outside the instrumented sources, a result from evaluated code. The outcome gate is a journey across the matrix below that asserts both the receipt and what ran, read from the engine's own run record.

| Axis | Cells |
|---|---|
| Change between two runs | none, a covered source file, an uncovered file, `Directory.Packages.props`, the daemon version, a file evaluated over the build |
| Prior result | passed, failed, skipped, never covered, flaky, property |
| Source state | in sync, stale, rebuilding, unknown |
| Prior provenance | compiled, evaluated, verified by build |
| Caller | `run_tests` default, `run_tests` asking for a fresh run |

## What exists today

- The cache: `SageFs.Core/Features/TestRunKey.fs` (`InputHash.compute`, `TestRunKey { TestId; SessionId; InputHash }`, the private-constructor `TestResultCache` with `lookup`, `insert`, `count`) and `SageFs.Core/Features/LandingCache.fs` (`partition`, `failingOfCached`, `record`, `verify`). `verify` returns only the failing subset, and for the tests it ran it records synthesized results: `Passed TimeSpan.Zero` or `Failed (AssertionFailed "failed in a prior cached run")`. It caches failures. A receipt line needs the real message and duration, so `verify` cannot be reused as it stands; `partition` and `record` can.
- The key's input: `SageFs.Core/Features/InputHashCoverage.fs`. `trustBeyond blind toolchain readFile map bitmap` returns `HashTrust.Trusted hash` or `Untrusted` with one of `NoInstrumentation | BitmapDoesNotMatchMap | CoversNothing`. The hash covers the toolchain fingerprint (`toolchainFingerprint`: SageFs semantics version, `Directory.Packages.props`, `Directory.Build.props`, FSharp.Core version, runtime), the path and bytes of every source file the test's coverage bitmap names, and `blind` (files coverage cannot see, evaluated over the build).
- The one caller: the cohort landing gate, `SageFs/DaemonMode.fs` around lines 3020 to 3085, through `CohortOwner.LandingCacheOwner` (a `MailboxProcessor` single owner, in memory only; a daemon restart empties it).
- The receipt: `SageFs.Core/Features/TestRunReceipt.fs`. `RunReceipt` is `Refused | Pending | Started | Ran | Unattributable`. `observe` builds a `Ran` receipt from the engine's `RunRequests` record, and `lineOutcome` counts a result only if `ResultGenerations` says this very run stamped it ("a pass from an earlier run is not this run's pass"). The tool description in `SageFs/McpTools.fs` (`run_tests`, around line 2310) repeats it: "A pass from an earlier run is never counted."
- How a run's results get their generation: `RequestedRuns.stampResults` stamps each arriving result with the session's running generation (`RequestedRuns.started` sets the phase). A result arriving with no run in flight is stamped with nothing.
- The entry point: `SageFs/McpRunTests.fs` `runTests`: resolve session, `TestRunReceipt.plan`, `CohortLandingVerify.preflight`, read the disk (`sourceOfSession`, `SourceState`), dispatch `TuiEvent.RunTestsRequested (Some sid, dispatchCases, Some requestId)`, await the receipt, attach the source reading (`withReflectedSource`). The effect that runs it is `TestCycleEffect.RunRequestedTests` in `SageFs/SageFsEffectHandler.fs` (around line 962); it does nothing when its test array is empty.
- Inputs the engine already holds per session: `TestState.TestCoverageBitmaps`, `InstrumentationMaps` (the landing gate reads them the same way), `Provenances: Map<TestId, ResultProvenance>`, `LastResults`, `FlakyHistory`, `QuarantinedTests`.
- The honest limits recorded in the code: `inputHashOf` is `None` for any test without trustworthy coverage, so it always runs and is never cached (`LandingCache.verify` comment).

I could not find: any non-source input in the key. A test that reads a data file, a doc page, an environment variable or the clock is invisible to it. This repository has such tests (the generated-page ratchets read `docs/roadmap.md`, for one), so an unfiltered reuse here would let a stale pass stand after a doc edit. I also could not find a bound on `TestResultCache` size, or any persistence of it.

## Design

A second, separate cache for `run_tests`, built from the same pure parts, with stricter rules than the landing gate, because a landing is re-checked by a gate and a receipt is read by an agent as proof.

### Rules (the pure core)

New slice `SageFs.Core/Features/RunReuse.fs`:

```fsharp
[<RequireQualifiedAccess>]
type NotReusable =
  | NoTrustedKey of UntrustedHash          // from InputHashCoverage
  | NoPriorResult
  | PriorWasNotAPass of TestResult
  | PriorProvenance of ResultProvenance    // anything but Compiled or VerifiedByBuild
  | CategoryNeverReused of TestCategory    // Property, Architecture, Integration, Browser, Benchmark
  | Flaky of flips: int
  | Quarantined
  | ReadsOutsideTheKey of evidence: string // from the per-case body scan; see markers-on-expecto-cases
  | SourceNotInSync of SourceState

[<RequireQualifiedAccess>]
type ReuseDecision =
  | Reuse of TestRunResult * provedAt: DateTimeOffset
  | MustRun of NotReusable

type ReusePlan =
  { Reused: (TestCase * TestRunResult * DateTimeOffset) list
    MustRun: (TestCase * NotReusable) list }

val plan : cache -> RunFreshness -> SourceState -> keyOf:(TestCase -> HashTrust) -> TestCase array -> ReusePlan
val record : cache -> keyOf -> Receipt:RanReceipt -> state:LiveTestState -> cache
```

`RunFreshness = ReuseProven | RunEverything` is the caller's choice. Every `MustRun` carries why, and the receipt prints the reasons grouped (as `GuardCoverage` does for skips), so "why did this run again" has an answer.

Reuse is allowed only when all of these hold: the source state at dispatch is `InSync`; the test's last result is a real `Passed` recorded under the same `TestRunKey`; its provenance was `Compiled` or `VerifiedByBuild`; its category is `Unit`; it has no flake window with a flip and is not quarantined; its key is `Trusted`. A failure is never reused (a red must be re-proven, and a flaky red must not become sticky). Recording follows the mirror rules, applied only to a receipt that finished with `Source = InSync`, from the engine's real `LastResults` (the synthesized results of `LandingCache.verify` are never used).

### The edge

`McpRunTests.runTests` computes the plan after `CohortLandingVerify.Preflight.Dispatch dispatchCases`:

1. `keyOf` uses the session's own bitmaps, maps, and the same `toolchainFingerprint` and `filesEvaluatedOverBuild` the landing gate uses (extract that block of `DaemonMode.fs` into a function both call; it is duplicated inline today).
2. The run request gains the reused results: `TuiEvent.RunTestsRequested (sid, tests, requestId)` becomes a record with a `Reused: TestRunResult array`. `RequestedRuns.request` records all tests (reused and to-run) so the receipt lists every requested test. `TestRunRequest` (the effect payload) carries the reused array. The effect handler dispatches start, then the reused results as a `TestResultsBatch` (so `stampResults` stamps them with this run's generation), then runs the rest, then completes. When every test is reused the handler must still start and complete the run; today the `RunTestsRequested` arm in `SageFsApp.fs` returns no effect for an empty test array, and the effect handler ignores an empty `Tests`, so that case is the first one to get a RED test.
3. The receipt gets per-line origin and a count: `ReceiptLine` gains `Origin = RanNow | ReusedFrom of provedAt: DateTimeOffset`; `RanReceipt` gains `Reused: int`. The verdict does not change: `AllPassed` still needs every line to be a pass and the source `InSync`. The JSON gains `counts.reused` and per-line `origin`; `summarize` says how many were reused and why the others ran.
4. After a run settles, the settled receipt is recorded into the cache by a single owner (a `MailboxProcessor`, as `LandingCacheOwner` is). Bounded: at most `RunReuse.maxEntries` entries, oldest first out; the size is a named constant with its reason beside it.

The owner is separate from the landing owner. Keys include the session id, so sharing would be sound, but the landing cache caches failures and synthesizes results and this one must not.

### Wire, clients, token cost

- `run_tests` gains one optional parameter, `fresh` (a string `"true"` or empty, parsed at the edge into `RunFreshness`; the domain never holds a bool). The description sentence "A pass from an earlier run is never counted" is rewritten to say a pass from an earlier run is counted only when marked reused, on identical proven inputs. About 100 tokens added to the catalog of about 28,000; a catalog size check should be run before and after (I did not find one in the tests).
- The receipt gains additive fields. No editor consumes `run_tests`. `apiVersion` stays 4 as long as the fields are additive. `Plugin: n/a, run_tests is an MCP tool for agents; the editors read test_results_batch`.
- `docs/mcp-tools.md` and `docs/how-live-testing-works.md` change (the "receipt" paragraph).

### Persistence

Phase 1 is in memory, like the landing cache. Phase 2 persists proven results so the first `run_tests` after a daemon restart reuses. The coverage bitmaps already persist in `.sagetc` (`TestCachePersistence.fs`); a new section for results under key would be a format change, so it needs a `.sagetc` version bump and a reader that ignores the section in older files. That is a separate step and a decision below.

## Tests

RED first.

1. `RunReusePlanTests.fs` (pure, default suite): three tests, one with a stored pass under the current key, one with no stored result, one with a stored failure. `plan` reuses the first and runs the other two with `NoPriorResult` and `PriorWasNotAPass`. Fails today, no module.
2. A stored pass under an old key (the covered file changed) is `MustRun`, not reused: the same test, key from the new file content.
3. Every `NotReusable` case has a test that produces it and a test that its opposite reuses (provenance `Evaluated` blocks; `VerifiedByBuild` allows).
4. Property (FsCheck): for any generated history of runs and edits, a reused result's key equals the key computed from the files as they are now (the cache never answers for other bytes); `plan` partitions the input (reused and run are disjoint and cover it); `RunEverything` reuses nothing.
5. Mutation: `plan` with the `InSync` check removed; with the failure check removed; `record` accepting a `Stale` receipt; the key ignoring `blind`. Each killed.
6. `McpRunTestsReuseTests.fs`: with a fake model, a run whose tests are all reusable dispatches a request that starts and completes with no tests to run (the empty-array case) and the receipt is `AllPassed` with `reused = n`.
7. Receipt tests (`TestRunReceiptTests.fs` extended): a reused line shows `origin` and its original time; a reused pass still yields `PassedOnStaleSource` when the source is stale (reuse is not allowed then, so the receipt shows the tests ran); JSON round-trips.
8. Simulation, `SageFs.Simulation/RunReuseSim.fs`: pool of eight tests across three files and a `SourceState` sequence; ops: run, edit covered file, edit uncovered file, edit toolchain props, daemon version bump, flip a test to flaky, quarantine, stale source, rebuild in progress, evaluated run. Folds the REAL `RunReuse.plan` and `record`. Invariants: a reused result's key matches the current files; a failure is never reused; a result recorded under `Stale`, `Rebuilding` or `Unknown` never exists; every test in a receipt is accounted for (reused or ran). Twins: ignore the file content (key by test id only), record under a stale source, reuse failures, drop `blind`. Each must violate its invariant; the same seed gives the same trace.
9. Outcome gate: `RunReuseJourneyTests.fs`, `--integration-lt`, on the FromCSharp sample: run, run again (all reused, asserted from the receipt and from the `/events` stream showing no `RunAffectedTests` for them), edit a covered file and save (those tests run, the rest reuse), edit an uncovered file (all reuse), `fresh=true` (all run). Wait on `Confirmation = idle` and on receipts via `awaitReceipt`, no sleeps.

## Steps

1. `RunReuse.fs` with `NotReusable`, `plan`, tests 1 to 5. Pure, new file. Smallest first commit: `plan` with an empty cache runs everything.
2. `record` and the bounded owner, with the retention property (oldest out, size never above the constant). Parallel with step 1 after its types exist.
3. Extract the landing gate's key construction (`toolchain`, `blind`, `keyOf`) out of `DaemonMode.fs` into a function with its own tests, and make the gate call it. Files: `SageFs/DaemonMode.fs`, new `SageFs.Core/Features/RunKeys.fs`. Behaviour of the gate must not change; its existing tests are the guard.
4. Receipt types and JSON (test 7). Files: `TestRunReceipt.fs`, `McpTools.fs` description.
5. The request/effect plumbing for reused results (test 6). Files: `SageFs/TuiEvent.fs`, `SageFs/SageFsApp.fs` (net zero lines, it is at its budget), `SageFs/SageFsEffectHandler.fs`, `SageFs.Core/Features/LiveTestingTypes.fs` (where `TestRunRequest` is defined, near line 3636; the file is at its size budget, so the new field is paid for by moving a block out in the same commit). Needs 1 and 4.
6. `McpRunTests.runTests` computing and recording the plan. Needs 2, 3, 5.
7. Simulation (test 8) and journey (test 9).
8. Docs and the roadmap landmark (`RunReuse.fs`).
9. Phase 2 persistence, if wanted.

First week: step 1 on day one, steps 2 and 3 in parallel, 4 and 5 by the middle, 6 at the end. Steps 1, 3 and 4 touch different files.

## Measure first

- How many `SageFs.Tests` tests are reusable under these rules. Run the plan over a real session's state after one full run: count `Reuse` and each `NotReusable` reason. If more than half end up `ReadsOutsideTheKey` or `CategoryNeverReused`, the saving is small and the plan should say so before more is built.
- The time a reused result saves. Take it from `live-testing-numbers-on-bigger-projects` once the harness exists: floor runs on large suites, and the time of a `run_tests` with and without reuse on the synthetic subject.
- Whether an `Untrusted` key is common. `CoversNothing` and a stale bitmap size are the two ways, and a session on the `Interactive` workflow has no instrumentation at all (`NeedsAWorkflow` in `docs/mcp-tools.md`). Reuse then does nothing, and that must be said in the receipt, not silently.

## Risks and dependencies

- The key does not see non-source inputs. This is the main unsoundness and the rules above are the mitigation: category, flake history, and the per-case body scan. The scan needs the case body, which `markers-on-expecto-cases` produces (`SourceCase.Body`). Until it lands, `ReadsOutsideTheKey` cannot be decided per test, so v1 ships with reuse off by default (`fresh` defaults to true) and the pure core and the plumbing are exercised by the journey with reuse asked for. See decision 1.
- An agent that reads `AllPassed` with reused lines is trusting a cache. That is why the lines are labelled and `fresh=true` exists. If a reused line is ever wrong, the whole receipt contract loses trust, so the rules lean toward running.
- `SageFsApp.fs` and `LiveTestingTypes.fs` are at their size budgets; new logic goes in new files.
- The second half of the roadmap text, "run tests inside the live session once I know a redefinition changes what your compiled tests call", is not in this plan. `mutants-in-the-live-session` answers the question it waits on, as its first experiment.
- Depends on: `markers-on-expecto-cases` for per-case bodies (soft). Enables: `test-selection-that-learns` (a recorded history of proven results is a training row). Informed by: `live-testing-numbers-on-bigger-projects`, `confirm-builds-without-restarting-the-worker` (a throwaway worker's `Compiled` run can be recorded).

## Size

M. The pure core is days; the request and effect plumbing across `SageFsApp.fs` and the effect handler, which are at their budgets and carry the generation logic, is the delicate week.

## Decisions for Will

1. Default on or off for agents? Recommendation: off (`fresh` defaults to true) until step 1's measure shows what fraction is reusable and the per-case body scan exists, then on for `Unit` tests only. A receipt that agents treat as proof should not change meaning silently.
2. Persist across a daemon restart? Recommendation: later. It needs a `.sagetc` version bump, and the in-memory version already serves the loop that matters (repeated runs in one session).
3. May a reused pass count toward `AllPassed`? Recommendation: yes, with `counts.reused` and per-line origin in the receipt. The alternative, a new verdict token, breaks every agent that branches on the six tokens it knows.
