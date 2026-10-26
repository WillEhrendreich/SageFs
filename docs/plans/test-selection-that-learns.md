# Test selection that learns from history

Roadmap item: `test-selection-that-learns` (Exploring, Live testing). Plan written 2026-10-26 against master `59a8692c`.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S for the recorder, the model unsized |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: ordering is a permutation of its input. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

Phase 1 is a question with a number on it: on recorded run history, does any order of the already-selected tests, learned or plain, put the first red earlier than the order the live loop uses now, by a margin fixed in advance? If yes, the live loop runs its selected set in that order, labels tests with a record of flipping, and says why on the row. If no, the item ends with the replay table in `docs/decisions.md`. Selection itself, meaning which tests run, is not touched by any of this: the learned layer orders and labels, it never widens, skips or excludes. The outcome gate for the shipped part is a journey on a fixture where the failing test sits last under today's order: it must report the same set of tests, in the new order, with the failing one earlier, across the matrix below.

| Axis | Cells |
|---|---|
| Selected set | 1 test, 12 tests, the whole suite (the conservative floor), a set with a quarantined test |
| History | empty (cold start), 50 runs, 5,000 runs, history written by an older version |
| Run mode | project with instrumented assemblies (serial), project without (parallel chunks) |
| Order policy | today's, last-failed-first, covering-the-changed-lines-first, learned |

## What exists today

Verified by reading the code. Which selection algorithm runs matters here, so first the two.

- Live inner loop: `LiveTestingCycle.decideAfterTypeCheck` (`SageFs.Core/Features/LiveTestingCycle.fs`) merges name delta (`TestDependencyGraph.findAffected`), coverage (`CoverageBitmap.findCoverageAffected`), file scope, and line narrowing (`narrowByLines`), with a conservative floor (`ConservativeFallback`: a compiled file where all of them find nothing selects every discovered test). Its result carries a `LiveTestingDecision` with cause, precision, symbols moved, tests chosen, tests deferred and a reason sentence. `docs/how-live-testing-works.md` ("Picking the tests") describes it.
- Cohort landing gate: `AffectedTests.verificationTestSet` (`SageFs.Core/Features/AffectedTests.fs`): intersect each test's coverage bitmap with the diff's files, fail closed on untrusted coverage, never return an empty set for a real diff. It runs the whole set to completion in chunks and reports every failure (`CohortLandingVerify.runTestsInSession`).
- These are two algorithms with different floors. `AffectedTests.fs` documents the gap in-tree and keeps a deliberately unwired `nameOnlyTwin` as the witness. The open item to unify them is in `open-problems-2026-09-30.md` ("Test runs"), an untracked file that exists only in the main checkout (`/home/will/Work/SageFs`); a fresh clone does not have it.
- How a selected set runs: `TestOrchestrator.executeFiltered` (`SageFs.Core/Features/LiveTestingExecutors.fs`, around line 1070) takes the array in the order given, cuts it into chunks of `maxParallelism`, runs each chunk with `Async.Parallel`, and emits each result through `onResult` as it finishes. The daemon passes `max 4 (ProcessorCount / 2)` and batches results to the model by 25 or 200 ms (`SageFsEffectHandler.fs`). A project with instrumented assemblies runs one test at a time. So in the live loop the order of the array is the order results reach the gutter; time to the first red depends on it. The landing gate uses the same executor but waits for the whole set, so order buys nothing there.
- Where the order is decided today: I did not trace it. The selected ids pass through `Set` and `Map` operations and `Array.partition` for quarantine (`SageFsApp.fs` around line 1768), so the order is probably the ordering of `TestId` or of discovery, with nobody choosing it.
- What history exists: `SageFs.Core/Features/TestCachePersistence.fs` (`.sagetc`) persists per test the coverage bitmap, the last result with duration and message (`ResultEntry`), and the flake window (`FlakyEntry`: a ten-slot pass/fail ring, `FlakyDefaults.windowSize = 10`, flaky at two flips with three samples). The maintainers' older note that the flake window is wiped on restart is out of date: the `FlakyEntries` section exists to fix exactly that. What does not exist is any time series: no timestamps per run, no record of which edit preceded which failure, no order a run used. The design docs say the same ("there is no per-test outcome time series anywhere in the repo", `sagefs-learn-iteration-v2.md` C8).
- What uses history today: `FlakyDetection.recordResult`, `FlakyClassification` (`Environmental | PropertyCounterexample | Stable | Insufficient`; an FsCheck counterexample is classed as a real failure), and `QuarantineLogic.evaluate/apply/isQuarantined`, wired in `SageFsApp.fs` (around lines 867 and 1768): a test classified `Environmental` is excluded from the next automatic selection, recorded on `LastDecision`, and echoed to the session output. That is a deterministic rule that does skip tests, which this item's learned layer must not copy.
- The design: documents at the root of the main checkout (`sagefs-learn-idea.md`, `-iteration.md`, `-iteration-v2.md`, `-from-cscheck.md`, and the roast they answer), all untracked (the checkout's `.git/info/exclude` hides root-level `.md` files), and no code: there is no `SageFs.Learn` project and no reference to it in any `.fs`, `.fsproj` or `.fsx` except three comments in the cohort simulation files that point at `sagefs-learn-from-cscheck.md`. `sagefs-learn-iteration-v2.md` is the usable one. Its decisions I keep: models only order and label; the deterministic selection and its fail-closed shell are the product; the recorder comes first because the corpus is empty on day zero; a model is on probation until it beats the strongest simple baseline by a stated margin; no early stop that censors its own labels; a kill criterion.
- Retention machinery to reuse: `LocalDataRetention.fs` (`MaxAge`, `MaxRows` per table), `SageFs.Simulation/RetentionSim.fs`, `LocalData.fs` for paths under the data dir. The SQLite adapters for friction (`FrictionSqlite.fs`) and the cohort ledger are the existing stores with retention.

I could not find: any measurement of how large selected sets are on a real suite (the median selected set on `SageFs.Tests` edits is unknown, and if it is 3 tests, ordering is moot), or any record of what order a run used.

## Design

Three layers, each shippable alone. The learned model is the last and the least likely.

### Layer 1: record what happened (the prerequisite)

New slice `SageFs.Core/Features/RunHistory.fs`:

```fsharp
type RunRow =
  { Project: ProjectKey          // the repo identity, so worktrees of one repo share history
    Run: RunGeneration
    At: DateTimeOffset
    Test: TestId
    Outcome: HistoryOutcome      // Passed | Failed of FailureKind | Skipped | NoResult
    DurationMs: int              // meaningless for a debugger run, which has Origin = Debug
    Origin: HistoryOrigin
    Trigger: RunTrigger          // Keystroke | FileSave | ExplicitRun
    Provenance: ResultProvenance
    Position: int                // index in the order the run used
    Change: ChangeEpochId }      // joins to the files and symbols that moved before this run

type HistoryOrigin = Ran | Reused | Debug     // a reused or debugger result is never a training sample
```

The row has no bool and no option. A `ChangeEpoch` table holds the files (path, content hash) and symbols moved, so a failure can be tied to the edit that preceded it. Recording is a pure function from the batches the model already receives (`TestResultsBatch`, `RunTestsRequested`, the decision) to rows, plus an append at the edge. Storage: decision 1. Retention through `LocalDataRetention` with a named constant for each bound and its reason. Disable-able, local only, never sent anywhere (the friction rules apply: `docs/mcp-tools.md`, friction).

### Layer 2: order the selected set with rules (no learning)

New slice `SageFs.Core/Features/RunOrder.fs`, pure:

```fsharp
type OrderPolicy = AsSelected | LastFailedFirst | CoveringChangedLinesFirst | FastestFirst | Combined of OrderPolicy list

val order : OrderPolicy -> RunHistory -> Selected -> TestCase array
```

`order` returns a permutation of its input and nothing else: same multiset, deterministic tie-break by `TestId`, and with empty history it returns the input unchanged. `CoveringChangedLinesFirst` uses what `decideAfterTypeCheck` already computed: the tests whose own coverage reaches the changed lines (`LineCoverageNarrowing`), then the file, then the symbol, then the rest. This is where most of the value is likely to be, and it needs no model.

The order is applied at one place, where the selected array is handed to the run: the `RunAffectedTests` and `RunRequestedTests` effects. The decision text gains a clause ("ordered by: covers the changed lines first") so the row can say why, additive to `LiveTestingDecision`. The wire carries it in the existing reason string; no new field, `apiVersion` stays 4, `Plugin: n/a`.

### Layer 3: a small learned ranker, on probation

Only if layer 2's replay leaves room. A small additive model (logistic regression or a generalised additive model; a tree ensemble cannot say why a row scored as it did) over per-test features: last-failed flag, coverage tier from layer 2, recent failure rate, flake flips, duration quantile, time since last pass. Trained locally on the recorded history, a few kilobytes, retrained when a trigger fires (N new red-containing runs), stamped with its training window, and falling back to layer 2 when the stamp is stale or the file is missing or from another version. It ranks within the selected set. It produces a score and a short reason per test, nothing else.

Flake labelling: the model may mark a test "failed in 4 of its last 10 runs with no edit to what it covers" on the row. It may not exclude it. Exclusion stays with `QuarantineLogic`'s deterministic rule (decision 2).

### Which path this extends

The live inner loop after selection. Selection itself and the landing gate stay as they are. Reasons: the live loop streams results, so order is time to the first red on screen; the landing gate waits for everything; and a model stacked on a selector with a known floor inherits the selector's leaks. The unification of the two selection algorithms is a separate piece of work and this plan does not wait for it.

## Tests

RED first.

1. `RunOrderTests.fs` (pure, default suite): `order` of any policy on a generated set is a permutation of it. With empty history it equals the input. Fails today, no module. This is the first commit.
2. `RunHistoryTests.fs`: a batch of results with a trigger and a change set becomes rows with the right positions; a `Reused` or `Debug` result is recorded with its origin and excluded by `trainingRows`; the same batch twice records once (keyed by run and test); retention drops the oldest beyond `MaxRows` and rows older than `MaxAge`; read-back equals write.
3. Property (FsCheck): features for run n are computed from rows with run < n only (no leakage; a generator that builds histories and a check that shuffling later rows changes nothing); `order` is idempotent for a fixed history; `LastFailedFirst` places every test that failed in the last run before every one that did not.
4. Mutation: `order` that drops the last element; that duplicates one; that sorts descending where ascending is meant; the retention bound off by one. Each killed.
5. Simulation, `SageFs.Simulation/RunOrderSim.fs`: pool of 30 tests with coverage sets, failure probabilities and durations; ops are edit a file, run (with the policy), flip a test to flaky, restart the daemon (history reloaded), write an older-version history file. It folds the REAL `RunOrder.order` and `RunHistory` record functions. Invariants: the run's tests equal the selected set (no test added, none dropped); a quarantined test never appears because of the order layer; history after a restart equals history before it; the first-red position under `CoveringChangedLinesFirst` is never worse than `AsSelected` on a run whose red test covers the changed lines. Twins: an order that drops a test, an order that adds an unselected test, a ranker trained on the run it is judged on (leakage). Each must violate its invariant. Same seed, same trace.
6. Replay harness (`RunOrderReplayTests.fs` plus a driver in the style of `scripts/machine-bench.fsx`): reads recorded history, replays runs under each policy with a time-split (train on earlier, judge on later), reports median tests-run-before-first-red and median time to first red from the recorded durations, per subject. This is the experiment's instrument, run by hand, results into `docs/decisions.md`.
7. Outcome gate, `RunOrderJourneyTests.fs`, tier `--integration-lt`, on a 200-test fixture where the only failing test is last in `TestId` order: assert from the `/events` stream and the receipt that the set of tests run equals the set selected, and that the failing test's verdict arrives before at least half the others in the new order; cells for instrumented (serial) and not (chunks). The wait is on stream frames.

## Steps

1. Trace the order. Find where the selected array is built and where it is handed to `executeFiltered` (`decideAfterTypeCheck`, `LiveTestCycleState.triggerExecutionForAffected`, the two effects). Write test 1 against the pure function and commit it red. Half a day.
2. `RunOrder.fs` with `AsSelected` and `LastFailedFirst` (tests 1, 3, 4). New file, no wiring.
3. `RunHistory.fs` row type, recording function, retention (test 2). New file. Parallel with 2.
4. The append at the edge and the store. Files: a new adapter beside `FrictionSqlite.fs` or a binary section (decision 1), `SageFs/DaemonMode.fs` wiring, `LocalDataRetention.fs`. Needs 3.
5. `CoveringChangedLinesFirst` using the decision's own tiers (needs 2). Wire the order at the effect (`SageFsApp.fs` is at its size budget, 2467 lines, so one line there and the rest in the new file). The decision text clause.
6. Simulation (test 5) and the journey (test 7).
7. Replay harness (test 6) and the first numbers, on the synthetic subject of `live-testing-numbers-on-bigger-projects` first, then a collection period of normal dogfooding.
8. Only then, layer 3, if the table says the margin exists.

First week: steps 1 to 3, with 2 and 3 in parallel on disjoint new files, then 4 and 5. Layer 3 is not in week one.

## Measure first

- How wide are selected sets in practice? From the decision log on `SageFs.Tests` edits (the `LiveTestingDecision` carries the tests chosen), and from the numbers harness's paths 2 and 4. If the median is under about five tests, ordering is moot, and the only reason to continue is the floor run.
- How many red-containing runs there are. A ranker needs a few hundred; the recorder collects them during ordinary use. Without them the replay table is empty, and the plan says so rather than training on synthetic failures.
- What the current order is, step 1. If it is already roughly coverage-first by accident, layer 2 gains little.
- The kill criterion, fixed now: layer 3 ships only if its median tests-before-first-red on held-out runs is at least 20 percent below the best of layers 1 and 2, it trains in under a second on 5,000 runs, and it explains each score in a line. Otherwise stop at layer 2 and record the table.

## Risks and dependencies

- Training on its own labels: a run that stops at the first red censors the rest. The live loop does not stop at red, so labels are complete; a future early-stop would break that and must keep the full-run labelling.
- Self-selection: a test ordered first fails first, and its failure record then favours it. The replay holds each run's real outcome fixed and varies only the order.
- Non-stationary history after a big refactor. The stamp and the fallback cover it.
- Privacy and size: local only, bounded by retention.
- Depends on: `live-testing-numbers-on-bigger-projects` (suites and a synthetic subject where the floor run is long), `run-tests-reuses-proven-results` (shares the "recorded result with an origin" idea; a reused result must be tagged so it is not a sample), `markers-on-expecto-cases` (per-case location and category are features). Enables: nothing else in this group.

## Size

S for layers 1 and 2 with the replay harness (one to two weeks); layer 3 is unsized until the table exists. The item is Exploring because the likely finding is that a deterministic order captures most of the gain.

## Decisions for Will

1. Where does the history live? Recommendation: a table in a SQLite store beside the friction and cohort stores. It already has retention and a simulation, and appending a row is cheaper than rewriting a `.sagetc`. The binary manifest fits the "binary from day one" direction but needs a version bump and a new section.
2. May a learned score ever exclude a test? Recommendation: no, never, in this item. Exclusion stays with `QuarantineLogic`, a deterministic rule with a visible reason, and the learned layer can only label.
3. Is the live loop the right consumer to optimise? Recommendation: yes. It is the only path where order reaches a human; the landing gate and `run_tests` wait for the whole set.
