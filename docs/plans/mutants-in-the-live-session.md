# Mutation testing in the live session

Roadmap item: `mutants-in-the-live-session` (Exploring, Live testing). Plan written 2026-10-26 against master `59a8692c`. Work order added 2026-11-11 against master `8975e898`; every `path:line` below was read on that tree.

## Work order

### 1. Outcome

Today nobody knows whether a mutant patched into the running host is cheaper than a build per mutant, and nothing in SageFs mutates user code. The first deliverable is that number, from a throwaway spike with its stop condition fixed before the first run. If the spike passes, the feature is: in a session with live testing on, ask which mutants of the function or file you just saved survive your tests, and get the survivors on their source lines in seconds. The outcome gate for the feature is `MutantJourneyTests` in the host tier (`--integration-host`, registered with `Integration.hostListConcurrent` per `HostRuntime`, the pattern at `SageFs.Tests/HotReloadStateOutcomeTests.fs:471`), on a fixture with a hand-listed set of mutants: reported survivors equal the baseline's for every supported cell, every unsupported cell names its reason, and `GET /api/live-testing/entries` shows the same verdicts after the run as before it. Linux x64 on net10 and net11 is the matrix the host tier runs. Windows and macOS are unproven, and the Harmony route depends on the patched MonoMod (`docs/how-hot-reload-works.md`), so a Windows result is not implied by a Linux one.

### 2. Where the code is today

- Mutation of SageFs's own tests: `SageFs.Tests/MutationTestingFramework.fs:25` (`Mutant<'a>`), `:91` (`computeMutationScore`); the `--mutation-score` tier is `SageFs.Tests/Program.fs:60`. It mutates a function handed to a test, never IL or source.
- IL patching in the host, reversible: `SageFs.Core/Features/GuardPatcher.fs:17` (`GuardLease` with `Release`), `:80` (`GuardTranspiler.Weave`), `:106` (`PatchRegistry`), `:138` (`Release`), `:153` (`ReleaseBeforeDetour`). Refusals: `GuardCoverage.fs:10` (`SkipReason`), `:81` (`describe`). Pure IL model: `GuardIl.fs`. What hot reload re-pointed: `DetourLedger.fs`. These files are compiled into the FSI host too: `SageFs.Core/SageFs.Core.fsproj:137-143` (Compile), `:172-176` (EmbeddedResource), `SageFs.Core/FsiHostBuild.fs:40-44` (the embed list), and `SageFs.FsiHost/FsiHost.fsproj`. A new patcher file needs all four entries.
- Runaway containment: `SageFs.Core/Features/GuardRuntime.fs:77` (`Guard.Check`), `:32` (`GuardCell.RequestStop`).
- Per-test coverage: `SageFs.Core/Features/CoverageInstrumenter.fs:383` (`instrumentShadowSolution`), `SageFs.Core/Features/LiveTestingTypes.fs:599` (`InstrumentationMap`), `:750` (`CoverageBitmap`), `:897` (`findCoverageAffected`), `:921` (`narrowByLines`), `:1751` (`TestCoverageBitmaps`).
- Detours: `SageFs.Core/Middleware/HotReloadCore.fs:310` (`detourMethodObserved`), `:369` (`detourMethod`), `:783-784` (`CreateNativeDetour`, and the comment that nothing undoes one).
- Build flags: `SageFs.Core/SessionBuild.fs:122` (`-p:Optimize=false`), `SageFs.Core/ProjectLoading.fs:1542` (`--multiemit-`), `:1550` (`--optimize-`).
- Running tests in a worker: `SageFs.Core/WorkerRoute.fs:48-49` (`RunTests`, `RunTestsStream`), `SageFs.Core/WorkerProtocol.fs:383` (`WorkerMessage.RunTests of tests * maxParallelism * replyId`).
- The throwaway worker the old plan waited on EXISTS. `confirm-builds-without-restarting-the-worker` is Done: `SageFs.Core/ConfirmationVenue.fs` (pure), `SageFs/ConfirmWorkers.fs:12-40` (`ThrowawayWorker`, `ThrowawaySpec`, `StartedThrowaway` with `AwaitReady` and `Retire`), `:164` (`start (ports: ThrowawayPorts)`), `SageFs.Simulation/ConfirmThrowawaySim`. The stopgap (pause and lease) and decision 3 below are moot.
- Bounds: `SageFs.Core/Timeouts.fs:288` (`perTestDefault`). Pause: `LiveTestingTypes.fs:239` (`LivePause`).
- Surfaces: `coverage_intel` is `SageFs/McpTools.fs:2077`; `apiVersion = 4` at `SageFs.Core/EndpointContracts.fs:38`.
- Stryker.NET: its Introduction and Mutations pages (read 2026-11-11) say nothing about F#; every mutator example is C#. Route B is a plain build per mutant unless the person finds F# support somewhere else `[not verified]`.
- No mutation fixture exists: `SageFs.Tests/fixtures/` has `LiveTestingSample` and `LiveTesting`, `samples/` has none.

### 3. Steps

The spike (steps 1 to 3) is thrown away. Nothing from it is committed except the write-up and this block.

1. **Spike, non-vacuity, route A.** In a REPL session on `SageFs.Tests/fixtures/LiveTestingSample` (or a 30-line Expecto project in the scratchpad with `let add a b = a + b` and one test): reflect the `MethodInfo` of `add`, build a Harmony transpiler that swaps the one `add` opcode for `sub`, patch, run the covering test in-process through Expecto, unpatch, run it again. Required: fail, then pass, `git diff` clean. **Stop condition:** Harmony refuses the method, or the test does not fail under the mutant. Either one is written as `Stopped: <why>` in Resume here and the item ends. Half a day. Pure spike, no product code.
2. **Spike, route C.** In a HotReload-workflow session on the same fixture, eval `let add a b = a - b`, then `run_tests` the compiled test. Did its verdict flip? Yes means a REPL redefinition reaches compiled tests, which `docs/hot-reload.md` and `run-tests-reuses-proven-results` both wait to know. No means route C is dead; record it, keep going with A. Half a day.
3. **Spike, agreement and timing.** New fixture `SageFs.Tests/fixtures/MutationFixture` (four modules, about 150 lines, tests weak on purpose at a third of the sites) with `mutants.md` beside it listing 40 mutants and the expected verdict each. Run all 40 by B and by A (and C if alive). Record per mutant: verdict and apply, run, revert, total milliseconds; machine and load as `LatencyStats.reportLine` prints them. **Stop condition (the decision rule, fixed now):** build the feature only if A agrees with B on at least 95 percent of the mutants both can run with every disagreement explained, costs under one twentieth of B per mutant including revert, and runs at least 70 percent of B's sites. Anything else: table into `docs/decisions.md`, Status `Stopped`, roadmap text rewritten by Will. One day.
4. **Feature, pure planner.** RED `SageFs.Tests/MutantPlanTests.fs` (module `SageFs.Tests.MutantPlanTests`; add `<Compile Include="MutantPlanTests.fs" />` to `SageFs.Tests/SageFs.Tests.fsproj` before `Program.fs`): a method with an `add` and a covering test yields one `Mutant` at that site with that test; an uncovered line yields `NoCoveringTest`; an `inline` function yields `Unsupported Inlined`. Then `SageFs.Core/Features/Mutants.fs` (after `LiveTestingTypes.fs` and `GuardIl.fs` in the fsproj) with the types in Design and `plan`, `score`. Pure domain. Property tests on `score` (test 3 below) and the mutation cases (test 4) in the same file.
5. **Feature, IL rewrite.** RED `MutantRewriteTests.fs`: each operator changes exactly one instruction; apply then undo is the identity on the `IlInstruction` list; a branch rewrite keeps `Marks`/`Opens`/`Closes`. Code in `Mutants.fs` beside `GuardIl.fs`'s rules. Pure. Parallel with 4 after the types land.
6. **Feature, the lease.** `SageFs.Core/Features/MutantPatcher.fs`, `MutantLease` with the same `Release` discipline as `GuardLease`; refusals reuse `SkipReason` and add the `Inlined`, `ModuleInitialiser`, `ClosureNotReachable` cases as a closed DU with one describe. Four fsproj/embed entries (see above). Edge code; the test is the host-tier journey.
7. **Feature, the run in a throwaway worker.** `DECISION:` how the daemon tells the worker to apply a mutant. Options: (a) a new `WorkerRoute` pair `Mutate`/`Unmutate` with a typed body; (b) ride on `Eval` with generated F#. Recommend (a): the route is daemon-to-worker wire, both ends ship in one package, `Plugin: n/a`. Spawn with `ConfirmWorkers.start` and a `ThrowawaySpec` for the session's targets, run covering tests with `WorkerMessage.RunTests`, `Retire` after. The session's own worker is never touched. Edge code.
8. **Simulation.** `SageFs.Simulation/MutantRunSim.fs` folding the real `Mutants.plan` and the lease step; invariants and twins as in Tests item 5. Tests in `SageFs.Tests/MutantRunSimTests.fs`.
9. **Route, event, surface.** `POST /api/live-testing/mutants` in `SageFs.Core/EndpointContracts.fs`, one SSE event per outcome, a `mutants` view on `coverage_intel`. `apiVersion` 4 to 5. sagefs.nvim lands first (a mark and a command); the commit carries `Plugin: done ...`.
10. **Journey.** `MutantJourneyTests.fs`, `Integration.hostListConcurrent` per runtime, cells from the matrix in "What done looks like", no skip cells.

### 4. How to check it

- Spike: `create_project_session` on the fixture, `send_fsharp_code` for each step, `run_tests` for the compiled side. Route B is `dotnet build -p:Optimize=false` then `dotnet test --no-build --filter <covering test>` in a scratch copy, timed with `Stopwatch` from an `.fsx` run through the session.
- Inner loop: `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --filter-test-list "Mutant" --summary` (list names, so `--filter-test-list`; read the `TRUST` line's `ran=`).
- Ratchets after every edit: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Acceptance: unfiltered `--summary`, then `--integration-host --summary` for the journey (`SAGEFS_HOST_SHARDS=5` on this machine).

### 5. Traps in this repo

- SAGE-005: `plan` must not take two `int`s or two `string`s side by side; `MutantId`, `IlSite`, `MethodKey` are single-case wrappers.
- Budgets only go down: `SageFs.Host/WorkerMain.fs` is at its line budget (2503, `RatchetBudgets.fileSize`), so the worker-side apply goes in a new file, with one call from `WorkerMain.fs`. A new test file has a `skipSites` budget of zero.
- `RatchetBudgets.toolList`: the `coverage_intel` description grows by about 80 tokens; run `--ratchets`, and if it is over, that is a `--tighten` conversation with Will, never a hand edit.
- `ModuleNameUniquenessTests`: every new file has a module name used nowhere else.
- No timing tests: the run ends on the SSE completion event; per-mutant bounds come from `Timeouts.perTestDefault` and the guard deadline.
- Wire change (route, event, `apiVersion`): plugin first, `Plugin:` trailer, every in-repo client in the same commit.
- Commit `SageFs.Core.fsproj`, `FsiHost.fsproj`, `SageFs.Tests.fsproj` whole, with a pathspec.
- Harmony and MonoMod are already referenced; no new NuGet package. Linux-only machine.

### 6. Size and order

Spike: two days (steps 1 to 3), one person, nothing in parallel. Feature if it pays: three to four weeks; steps 4 and 5 in parallel on disjoint new files, 6 after 5, 7 after 6, 8 with 7, 9 and 10 last. The first commit is the spike's write-up (`docs/decisions.md` and this block), whichever way it goes. If it goes ahead, the first code commit is the RED `MutantPlanTests.fs` with its fsproj line.

### 7. Not now

- `markers-on-expecto-cases`: per-case marks make a survivor's covering test visible, nice to have, not needed for the verdicts.
- Persisting outcomes keyed by `InputHashCoverage`: v2, once there is a run to skip.
- Comparing scores with Stryker: the population is IL-level, the doc says so, and no F# Stryker baseline exists to compare with.
- Route C as a product mechanism: even if step 2 flips the verdict, the detour cannot be undone (`HotReloadCore.fs:784`), so the revert story has to be built before it is a route.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | S for the experiment, L for the feature |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Work order step 1: the route A non-vacuity spike in a REPL session, with its stop condition. Then steps 2 and 3 (the table and the decision rule). Code only if the rule says go. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

First, a day of measurement that either kills the idea or earns the feature. The day ends with a written result in `docs/decisions.md` that says, for a fixture project, how long one mutant costs by each route, how often the routes agree with a plain build-per-mutant run, and which kinds of code cannot be mutated this way. If it pays, the feature is: ask the session which mutants of the function (or file) you just wrote survive your tests, and get an answer in seconds, with each survivor shown on its source line. The outcome gate for the feature is a journey on a fixture with a known set of surviving mutants that asserts the reported survivors equal the baseline's, across the matrix below, and that the session's live results are untouched afterwards.

| Axis | Cells |
|---|---|
| Code under mutation | plain function, closure body, method of a type, generic method, `inline` function, async/task code, module-level value |
| Operator | arithmetic, comparison boundary, boolean negation, logical and/or swap, constant plus one, remove a call |
| Mutant behaviour | killed, survives, loops forever, throws, overflows the stack, mutated line has no covering test |
| Session | clean, live testing running at the same time, hot reload workflow |

## What exists today

Nothing mutates the user's code. What exists is the pieces such a feature would stand on, and a different mutation tool for SageFs's own tests.

- SageFs's own mutation testing (`SageFs.Tests/MutationTestingFramework.fs`, `Mutant<'a> = { Name; Apply; Description }`) transforms a function or value handed to a test and never touches the source or IL of a program. `--mutation-score` is a tier that tallies killed and survived per case. It is the wrong shape for a user's code but its rule is the right one: a case that did not run counts as a survivor, never as a kill.
- Patching compiled methods in a running host is built and reversible: `SageFs.Core/Features/GuardPatcher.fs` weaves guards into IL through a Harmony transpiler (`GuardTranspiler.Weave`, `HarmonyBackend.patch` / `unpatch`), holds patches in a `PatchRegistry` with a `GuardLease` whose `Release` takes them off, and refuses methods it must not touch with a typed `SkipReason` (`NotOurCode`, `AsyncStateMachine`, `DynamicMethod`, `Generic`, `NoBody`, `DetouredByHotReload`, `PatchRefused`, `UnreadableBody`, `BudgetReached`; `GuardCoverage.fs`). The pure half, `GuardIl.fs`, models an instruction list (`IlInstruction { Marks; Opens; Closes; Opcode; Operand; Target }`) so a rewrite rule is a function FsCheck can generate input for. `DetourLedger.fs` records what hot reload has re-pointed, because patching a detoured method silently puts the original back.
- Runaway code is already contained: `GuardRuntime.fs` (`Guard.Check`, `Guard.EnterEnsure`, `GuardCell.RequestStop`) stops a loop after a deadline and turns a stack overflow into a catchable exception. A mutant that turns `i < n` into `i <= n` in a `while` is the classic way a mutation run hangs.
- Per-test coverage maps tests to sequence points: `CoverageInstrumenter.fs` reads every non-hidden sequence point from the PDB (file, line, columns) and `InstrumentationMap`/`CoverageBitmap` (`LiveTestingTypes.fs`) hold them. That is the standard way to run only the tests that reach a mutated line, and it is already stored per test (`TestState.TestCoverageBitmaps`).
- Hot reload re-points a compiled method to a new body from an eval: `SageFs.Core/Middleware/HotReloadCore.fs` (`detourMethod`, `detourMethodObserved`, a MonoMod `CreateNativeDetour`). Its own comment, near line 784, says "A detour is undone when it is disposed, and nothing here ever undoes one": detours are kept in a list and never disposed. `WorkerMessage.EvalLiveTestFile` evaluates a whole file with the file watcher's preprocessing (`evalLiveTestFile`, `SageFs.Host/WorkerMain.fs`).
- The session's build already turns optimisation off (`SessionBuild.optimizationDisablingProperty = "-p:Optimize=false"`), and hot reload sessions run with tiered compilation off. Both matter: an inlined callee never sees a patch on its original.
- The roadmap text for the sibling item says: "once I know a redefinition changes what your compiled tests call". That is unknown, and this item's first experiment answers it.

I could not find: any timing of a mutant, any code that enumerates mutation sites in IL or source, whether Stryker.NET mutates F# at all (I did not look at its current documentation), or a way to undo a hot reload detour.

## Design

### The experiment (one day, then a write-up)

Fixture: a new small project `samples/mutation-fixture` (Expecto, about 150 lines in four modules: arithmetic and boundaries, a DU decision, collection code, one `inline` helper and one `task` function) with tests that are weak on purpose at about a third of the sites. A human lists 40 mutants by hand with their expected outcome (killed or survives). That list is the ground truth; a generated population from the operator table is the larger set.

Three routes, one result table:

| Route | How a mutant is made and run | Costs to record |
|---|---|---|
| B (baseline) | Apply a source diff, `dotnet build -p:Optimize=false` (incremental), `dotnet test --no-build` with the covering tests only, revert. Stryker.NET too if it handles F# on the day; if it does not, B is the whole baseline and the doc says why | build, run, revert, total per mutant |
| A (IL) | In the FSI host: a Harmony transpiler rewrites one instruction of the compiled method (`blt` to `ble`, `add` to `sub`, `ldc.i4 n` to `n+1`), run the covering tests, `Unpatch` | apply, run, revert, total |
| C (eval and detour) | In a HotReload-workflow session, eval the mutated definition and let the detour re-point the compiled method; run covering tests; put the original back | same, plus how a revert is done at all |

Per mutant, every route reports one of: `Killed`, `Survived`, `TimedOut`, `Unsupported of why`. Unsupported reasons reuse the `SkipReason` vocabulary where it fits and add `Inlined`, `ModuleInitialiser`, `ClosureNotReachable`.

First actions, in this order, before any timing:

1. Non-vacuity. Apply the mutant `add a b = a - b` by route A and by route C; require the covering test to fail; revert; require it to pass; `git diff` clean. Route C's answer here is also the answer to "does an eval redefinition change what compiled tests call", which `run-tests-reuses-proven-results` and `docs/hot-reload.md` wait on. If C does not change the compiled test's result, C is dead and the doc records why.
2. Agreement. Run all 40 hand-listed mutants by every route and compare verdicts. A disagreement is explained (inlining, tiering, static initialiser) or it kills the route.
3. Timing, each route, 20 mutants, n warm. Machine and load recorded as `LatencyStats.reportLine` does.

Decision rule, fixed before the day so the numbers cannot choose it: build the feature if some route (a) agrees with B on at least 95 percent of the mutants both can run, every disagreement explained; (b) costs under one twentieth of B per mutant including revert; (c) can run at least 70 percent of B's mutation sites. Anything else is written up as "did not pay" with the table, and the roadmap entry is rewritten to say what was measured and why it stopped (the roadmap has no horizon for dropped items, so the entry stays under Exploring with that text, or leaves the page with an entry in `docs/decisions.md`).

The experiment is iterated in a SageFs session on the fixture (the repo's rule is the REPL is the inner loop), and its code is thrown away unless the feature goes ahead. Do not leave a loose script in `scripts/`.

### The feature, if it pays (the rest of this section is conditional)

The run does not happen in the session's own host. A mutant is live in the process for as long as its tests run, and any other test run in that host (the live loop, a keystroke, `run_tests`) would judge the mutant instead of the code. So mutants run in a throwaway worker built from the saved files, the one `confirm-builds-without-restarting-the-worker` added (`SageFs/ConfirmWorkers.fs`, Done as of 2026-11-11). That also keeps the session's FSI state. The pause-and-lease stopgap this paragraph used to describe is no longer needed.

New slice `SageFs.Core/Features/Mutants.fs`, pure:

```fsharp
type Operator = Arithmetic | Comparison | Negation | Logical | ConstantPlusOne | RemoveCall

type Mutant =
  { Id: MutantId
    Method: MethodKey
    Site: IlSite              // instruction index within the method
    Operator: Operator
    Where: SourceSpan }       // file, line, column, from the PDB sequence point

[<RequireQualifiedAccess>]
type MutantOutcome =
  | Killed of by: TestId
  | Survived
  | TimedOut                  // counted against the score, like a survivor
  | NoCoveringTest            // listed, never hidden, never in the denominator's favour
  | Unsupported of MutantSkip // a closed DU with a describe function
  | DidNotRun of why: string  // counts as Survived in the score

val plan : InstrumentationMap -> CoverageBitmap map -> MethodBody -> Mutant list * (Mutant * TestId list) list
val score : MutantOutcome list -> Score   // Score carries the counts and the unsupported list
```

The pure rewrite is a function over `IlInstruction list`, next to `GuardIl.fs`, so a property test can generate bodies. The edge is a `MutantLease` beside `GuardLease` with the same `Release` discipline and the same refusal vocabulary. A mutated method also gets the guard weave so a mutant that loops is stopped by the deadline that already exists (`Guard`), and the per-test timeout (`Timeouts.perTestDefault`) bounds the rest.

A mutant counts as killed only if a test that passed on the unmutated build fails on the mutant. The baseline run of the covering tests comes first, and a test that fails on the unmutated build is named as a baseline failure and excluded from the kill count.

### Surfaces and cost

Not a new MCP tool. A tool adds to a catalog of about 28,000 tokens and this one has no caller that cannot be served by an existing tool. Extend `coverage_intel` (an Analysis tool, no eval, no tests) with a `mutants` view for the survivors of a file, about 80 tokens of description. The run itself is a route, `POST /api/live-testing/mutants`, with an SSE event per outcome, so the dashboard and the editors show survivors as marks on the lines. That is a new route and event, so the Neovim and VS Code work lands in the plugin first and `apiVersion` moves to 5 (new route, new event; the VS Code extension requires an exact match). A `Plugin:` trailer on the commit says what sagefs.nvim does (a mark and a command) and that it must land first.

Persistence: none in v1. Outcomes are keyed by the input hash that `InputHashCoverage` already computes for a test, plus the mutant id, so a later phase can skip a mutant whose method and covering tests have not changed.

## Tests

For the experiment: the table is the test, plus the three first actions above, run by hand and written up. The non-vacuity check is the RED/GREEN of the day.

For the feature, RED first:

1. `MutantPlanTests.fs` (pure): a method with an `add` and a covering test yields a `Mutant` at the `add` with that test; an uncovered line yields `NoCoveringTest`; an `inline` function yields `Unsupported Inlined`. Fails today, no module.
2. `MutantRewriteTests.fs`: each operator rewrite changes exactly one instruction; applying and undoing is the identity on the instruction list; a branch rewrite keeps the label and region marks (`GuardIl`'s property is the template).
3. Property (FsCheck): `score` never counts `DidNotRun` or `TimedOut` as kills; adding an `Unsupported` outcome never raises the score; the unsupported list always appears with the score.
4. Mutation (`MutationTestingFramework`): `score` counting `TimedOut` as killed; the rewrite for `Comparison` swapping `<` and `>`; the baseline check removed. Each killed by an existing assertion.
5. Simulation, `SageFs.Simulation/MutantRunSim.fs`: ops are plan, apply, run covering tests, revert, cancel mid-run, host exit mid-mutant, a live-loop run request arriving, a second mutation request. It folds the REAL `Mutants.plan` and the lease logic. Invariants: at most one mutant applied per host at any instant; every trace ends with zero applied; no live-loop or `run_tests` verdict is recorded while a mutant is applied; a kill needs a baseline pass. Twins: skip the revert on cancel, run live tests during a mutant, count a timeout as a kill. Each must violate its invariant. Same seed, same trace.
6. Outcome gate, `MutantJourneyTests.fs`, tier `--integration-lt`: the fixture with its 40-mutant ground truth. Asserts the reported survivors equal the baseline's for every supported cell, that each `Unsupported` cell names its reason, and that after the run `GET /api/live-testing/entries` shows the same verdicts as before it and the session's REPL value is intact. No cell skips: a route that cannot run a cell reports `Unsupported`, and the journey fails if the cell is `DidNotRun`.

No wall-clock waits: the run's end is the SSE completion event; every per-mutant bound is `Timeouts.perTestDefault` and the guard deadline, taken from `Timeouts`.

## Steps

1. The experiment's first action (non-vacuity), by hand in a REPL session. Half an hour. Its answer about route C settles a sentence in `docs/hot-reload.md` and the second half of `run-tests-reuses-proven-results`.
2. The fixture and its 40 hand-listed mutants (`samples/mutation-fixture`). One hour.
3. Routes B, A, C, agreement and timing, the write-up in `docs/decisions.md`. The rest of the day.
4. If it pays: `Mutants.fs` plan and score with tests 1 to 4, new file only.
5. The IL rewrite and `MutantLease` (file next to `GuardPatcher.fs`; the fsproj needs the same embedded-into-the-host entries `GuardPatcher.fs` has, `SageFs.Core.fsproj`, `FsiHostBuild.fs`, `FsiHost.fsproj`). Parallel with 4.
6. The run in the throwaway worker (`ConfirmWorkers.start`, exists).
7. Route, SSE event, simulation (test 5), journey (test 6), `coverage_intel` view, `Plugin:` handoff with `apiVersion` 5, docs.

First week: steps 1 to 3 on day one, the write-up on day two, steps 4 and 5 for the rest of the week only if the rule says go.

## Measure first

The experiment is the measurement. What each result would change:

- C does not change what a compiled test calls: the live loop's "tests run against evaluated code" holds only for tests in the evaluated file itself, and that is a documentation correction of its own (`docs/how-live-testing-works.md`) even if mutants die.
- A agrees with B but loses on speed because the covering-test set is large: coverage narrowing is the lever, and the day should report how many tests a typical mutant's line reaches.
- A is fast but half the sites are `Unsupported`: report the supported fraction by code shape (closures, generics, `inline`, `task`) and decide whether the feature is "mutants for the straight-line core" and says so.
- Harmony on net11 works here only through the patched MonoMod (`docs/how-hot-reload-works.md`). Run the experiment on net10 and net11; a route that works on one is a smaller feature.

## Risks and dependencies

- Verdicts that differ from a real build are worse than no answer. That is why agreement is a gate and why mutation sites inside inlined or initialised-once code are `Unsupported`.
- IL mutants are not source mutants. A Stryker user expects mutants named by source token; the `Where` span from the PDB gives the line, and the doc must say the population is different (IL-level), so scores are not comparable with Stryker's.
- Mutating code in the session's own host corrupts live results. The plan forbids it except under pause and lease.
- Depends on: `confirm-builds-without-restarting-the-worker` (the throwaway worker), `markers-on-expecto-cases` (soft; per-case marks make a survivor's covering test visible), the hot reload items for route C only. Enables: it answers a question that `run-tests-reuses-proven-results` and `docs/hot-reload.md` both carry. Informed by `live-testing-numbers-on-bigger-projects` (how large a covering set is on a real suite).

## Size

S for the experiment (one day plus a half-day write-up). L for the feature: an IL rewrite, a lease, a run in a second process, a route, an event, two plugins.

## Decisions for Will

1. Is the decision rule above the right bar? Recommendation: yes, set before the day. In particular the 95 percent agreement: a mutation score people cannot trust is worse than none.
2. Surface: extend `coverage_intel`, or a new tool? Recommendation: extend it. Agents get survivors in the tool they already call for coverage, and the catalog does not grow.
3. (Closed 2026-11-11: the throwaway worker exists, so no stopgap is needed.)
4. How the daemon tells the worker to apply a mutant: a new `WorkerRoute` pair, or generated F# over `Eval`? Recommendation: the route, typed, `Plugin: n/a`.
