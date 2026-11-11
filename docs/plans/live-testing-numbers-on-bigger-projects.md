# Live testing numbers on bigger projects

Roadmap item: `live-testing-numbers-on-bigger-projects` (Later, Live testing). Plan written 2026-10-26 against master `59a8692c`.

## Work order

Written 2026-11-11 against master `8975e898`. This is measurement work: the first two steps produce numbers and need a quiet machine, and the code that follows exists to make the numbers reproducible and keep the docs honest. Corrections to the plan below, found while checking it: the runner is at `DashboardBrowserRunner.fs:703` (the plan says 667); `summarize` does have property tests (`SageFs.Tests/LatencyStatsTests.fs:44,55`); the figures 712 and 542 are quoted in six files (the plan says five; `Readme.md:515` is the sixth); and `scripts/machine-bench.fsx:645-677` already enables live testing and runs it once per stage, so it is closer to the driver this plan wants than the plan says.

### 1. Outcome

`docs/live-testing-numbers.md` gives p50, p95, min, max and n for each live-testing path on the FromCSharp sample, a synthetic project, `SageFs.Tests` and an F# compiler test project, each row naming machine, load, SageFs version and project commit, and the six places that quote 712 ms and 542 ms link to it. A fresh checkout runs one command and lands within 20 percent of the published small-subject figures on the same machine class. Gate: the page ratchet, the provenance test and the inline-figure budget in `--ratchets`; the small subject's p95 gate stays in `--integration-lt`. Linux only; no figure on another OS.

### 2. Where the code is today

- Measurement: `SageFs.Tests/LiveTestingLatencyTests.fs` (`keystrokeToVerdict` `:49`, `saveToGreen` `:89`, the two dedicated cases at `:173-181` registered through `Integration.dedicatedCaseTask "--integration-lt"`). Stats: `SageFs.Tests/LatencyStats.fs` (`samplesPerPath = 20` `:14`, `warmupEdits = 2` `:19`, `Summary` `:27`, `Machine` `:40`, `summarize` `:54`, `gate` `:68`, `machine ()` `:101`, `reportLine` `:109`). Bound: `SageFs.Tests/TestTimeouts.fs:214` `liveTestingKeystrokeP95Bound = secs 3.`.
- Runner: `SageFs.Tests/DashboardBrowserRunner.fs:703` `runLiveTestingBrowserJourneys`: starts `SageFs/bin/<cfg>/net11.0/SageFs.dll` (`TestInfrastructure.SageFsBinary.path`), a copy of the sample (`LiveTestingShards.sampleCopy`, `LiveTestingShards.fs:93`), sets `SAGEFS_HOT_RELOAD=false` `:735`, waits for 11 green `:909-922`, exports `SAGEFS_LT_FIXTURE_DIR` and `SAGEFS_LT_MCP_PORT` `:934-936`, then runs the shard's share of `LiveTestingShards.tree` (`LiveTestingShards.fs:27-36`, latency last).
- Stream helpers: `SageFs.Tests/LtStream.fs` (`SseFeed`, `expectFrame`, `awaitSettled`, env at `:55-56`).
- Histograms declared, recording unchecked: `SageFs.Core/Instrumentation.fs:40-46`. The debounce in the 712 figure: `SageFs.Core/Timeouts.fs:308` `liveTestFcsDebounce` 300 ms.
- Figure sites: `docs/how-live-testing-works.md:176`, `docs/live-testing-as-you-type.md:35,164`, `docs/vs-dotnet-watch.md:12`, `docs/progress.md:144`, `Readme.md:515`, and `scripts/roadmap/RoadmapItems.fs:433` (which generates `docs/roadmap.md`).
- Precedents: `scripts/machine-bench.fsx` (`--out`, `--label`, `--memory-cap`, `--ready-cap`, `/proc/loadavg` at `:211`), `SageFs.Tests/RoadmapDocTests.fs:51-55` (stale-page ratchet), `SageFs.Tests/RatchetBudgets.fs:18` (`fileSize`, a table that only goes down), `TestInfrastructure.fs:774` (`Ratchet.register`).
- Nothing of the design exists: no `SageFs.Core/LatencyRecord.fs`, no `scripts/bench-live-testing.fsx`, no `docs/live-testing-numbers.md`, no `docs/benchmarks/`.
- Subjects: `~/Work/fsharp-compiler-services` is at `cdb9dc5e5` with `tests/FSharp.Compiler.Service.Tests/FSharp.Compiler.Service.Tests.fsproj` and `tests/FSharp.Compiler.ComponentTests/...fsproj` present. Whether either loads into a session is `[not verified]`; that is step 1.

### 3. Steps

1. **Measurement A: reproduce the small subject.** Quiet machine: no other agents (`sagefs call list_sessions`), no gate (`dotnet fsi scripts/gate-tail.fsx -- --status`), `cat /proc/loadavg` first field under 1.0, the installed daemon idle. Then `dotnet build SageFs.Tests -c Release` (builds `SageFs` too) and `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-lt --summary`. Read the two `LATENCY` lines. Run it three times. Write the p50s and the load into `Resume here`. If the p50s are outside 700 to 720 and 536 to 620, the published figures already need the page, and the harness is what you are measuring first.
2. **Measurement B: count and load the big subjects.** From the REPL: `create_project_session` on `SageFs.Tests/SageFs.Tests.fsproj`, wait for Ready by `get_session_status`, `list_tests` for the count, note time to Ready from the status payload and the daemon, worker and host RSS (`ps -o rss= -p <pid>`; the worker pid is in `/health`). Repeat on `~/Work/fsharp-compiler-services/tests/FSharp.Compiler.Service.Tests`. A session that will not load is a SageFs bug to fix or file with the exact paths (`AGENTS.md`), and it blocks that subject. Record all of it in `Resume here`. One day. This step can end the item early if the counts and the floor-run times make the large-subject rows a non-event.
3. **RED** `SageFs.Tests/LatencyRecordTests.fs`: a record with an empty machine, zero discovered tests, or 19 samples is refused with a named reason; a valid record round-trips through a `SageFs.Json` profile byte for byte. Compile-red. **Green**: `SageFs.Core/LatencyRecord.fs` with the plan's types; move `Summary`, `summarize`, `Machine`, `reportLine` into it and make `LatencyStats.fs` a re-export so the existing tests do not move. Register the test file in `SageFs.Tests.fsproj` next to `LatencyStatsTests.fs` (`:384`). Pure.
4. **RED** `LatencyProbeTests.fs` and `FixtureTests.fs` (plan tests 5, 6). **Green**: `SageFs.Tests/LatencyProbe.fs` (pure red/green variants of a file) and `SageFs.Core/LatencyFixture.fs` (`render : SyntheticShape -> (path * content) list`, deterministic). Pure; parallel with step 3.
5. `scripts/bench-live-testing.fsx`, glue only, in the shape of `machine-bench.fsx`: throwaway daemon, session on `--subject`, wait for Ready by event, run each `PathId` with the `LtStream` helpers (move `LtStream.fs` into a shared place or reference the test assembly from the script; DECISION below), write the record with `--out`. First subject `fromcsharp`; it must reproduce step 1 within 20 percent or the script is wrong.
6. New paths on the small subject: keystroke to settled, edit no test reaches, confirming build, first edit after Ready. Each is a `PathId` case and a probe; no new project.
7. Synthetic grid (1,000 and 10,000 tests; 1 ms and 50 ms), then `SageFs.Tests` and FCS in reflink clones (`cp --reflink=auto -r`), deleted after. These are runs. Quiet machine, one subject at a time, nothing else on the box.
8. **RED** `LiveTestingNumbersDocTests.fs` (page equals render) and `LatencyFiguresTests.fs` (inline-figure budget, starts at 6 files; `docs/roadmap.md` is excluded because it is generated, and its sentence is fixed in `RoadmapItems.fs:433`). **Green**: `scripts/gen-live-testing-numbers.fsx` rendering `docs/live-testing-numbers.md` from `docs/benchmarks/live-testing/*.json`; both tests under `Ratchet.register`. Then replace the six inline figures with a sentence and a link, and the budget drops to zero by `--ratchets --tighten`.
9. Ratchet 4 (bound between 1.5x and 5x the recorded p95) in `TimeoutLiteralsTests` territory, landmark `docs/live-testing-numbers.md` in `RoadmapItems.fs:430`, `how-live-testing-works.md:158` "What's still rough" rewritten from the page.

### 4. How to check it

- REPL: session on `SageFs.Tests`; prove `LatencyRecord.create` and `summarize` by `send_fsharp_code`; `run_tests` with `pattern=LatencyRecord`.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Inner loop: `--summary --filter-test-list "Latency"`; read `ran=`.
- Acceptance: unfiltered `--summary`, then `--integration-lt --summary` (the small subject's gate), then the hand run of `scripts/bench-live-testing.fsx --subject fromcsharp` compared against the page.

### 5. Traps in this repo

- No bare durations: the 20 and 2 come from `LatencyStats` (moving to Core); new ceilings go in `TestTimeouts.fs` or `Timeouts.fs`, never at a call site (`TimeoutLiteralsTests` ratchets it).
- Budgets only go down: the inline-figure budget starts at the real count (6) and is lowered by `--tighten`.
- No new tier: `TrustSignalTests` fails for a tier `ci-pipeline.fsx` does not run. The script is run by hand; the ratchets are in the default suite.
- `SpawnEnvironment.apply` on every child process the script or a test starts.
- No timing-sensitive tests: the record tests are pure; only the script measures.
- JSON through a `SageFs.Json` profile (`JsonCentralizationTests`).
- Commit `SageFs.Tests.fsproj` and `SageFs.Core.fsproj` whole, by pathspec.
- Linux only: `/proc/loadavg`, reflink copies.

### 6. Size and order

Steps 1 and 2 are one quiet day and decide the rest. Steps 3 and 4 are two days, pure, in parallel. Step 5 is a day. Steps 6 to 7 are runs: an afternoon per subject on a quiet machine. Step 8 and 9 are a day. About two weeks of one person's calendar, mostly waiting for a quiet machine. First commit: the measurements from steps 1 and 2 written into `Resume here`. First code commit: step 3's RED.

### 7. Not now

- A daemon route for the stage histograms: only if step 2 shows the end-to-end number is not enough to explain where time goes, and then as its own commit with a `Plugin:` trailer.
- CI gating of the large subjects: no (decision 2 below).
- The onboarding trial on the F# compiler repo uses these numbers; it does not wait on the page.

### DECISIONs for Will (this section's additions)

- Where the stream helpers live so a script can use them: move `LtStream.fs` into `SageFs.Core` (it is HTTP and SSE client code, no test dependency) or let the script `#r` the test assembly. Recommendation: move it; a script that loads a test DLL is the pattern the repo is retiring.
- The rest are unchanged under "Decisions for Will" below.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a latency record with no machine is refused. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

This is a measurement plan. The product change at the end is a table, a page that is generated from it, and a ratchet.

## What done looks like

One page, `docs/live-testing-numbers.md`, generated from records in the repo, gives p50, p95, min, max and sample count for the live-testing paths on four subjects: the 11-test FromCSharp sample, a synthetic project we control, `SageFs.Tests` (about 14,000 tests), and the F# compiler's own test project. Every figure names its machine, load, SageFs version and project commit. The five places that quote 712 ms and 542 ms today link to it or are generated from it. A test fails when a page and its records disagree, when a record lacks its provenance, or when a prose page grows a latency figure of its own. The outcome gate is that a fresh checkout can run one command, get a record for the small subject, and land within 20 percent of the published 712/542 on the same machine class.

| Axis | Cells |
|---|---|
| Subject | FromCSharp (11), synthetic, `SageFs.Tests`, FCS test project |
| Path | keystroke to verdict, keystroke to settled, save to green, edit no test reaches, confirming build, first edit after Ready |
| Suite shape (synthetic only) | 1,000 and 10,000 tests, 1 ms and 50 ms and 1 s per test, 1 covering test and 1 percent and 30 percent covering |

## What exists today

- The measurement: `SageFs.Tests/LiveTestingLatencyTests.fs` times `keystrokeToVerdict` (the whole unsaved buffer posted to `buffer-changed`, clock stops when the edited function's test changes verdict on the daemon's `/events` stream) and `saveToGreen` (file written, clock stops when the suite is green with nothing running). 20 samples each after 2 warm-up edits (`LatencyStats.samplesPerPath`, `warmupEdits`). It prints a `LATENCY` line from `reportLine` (machine and count included) and gates the p95 against `TestTimeouts.liveTestingKeystrokeP95Bound` (3 s).
- The summary type is `SageFs.Tests/LatencyStats.fs`: `Summary { Count; Min; P50; P95; Max }`, `summarize` by nearest rank, `gate`, `Machine`, `reportLine`, which refuses to print a figure without its machine and count. It lives in the test project.
- The harness: `SageFs.Tests/LtStream.fs` (`SseFeed`, `expectFrame`, `awaitConfirmed`, `awaitSettled`, `LtEnv` reading `SAGEFS_LT_MCP_PORT` and `SAGEFS_LT_FIXTURE_DIR`). The runner starts the daemon and the FromCSharp sample; it is `--integration-lt` (`SageFs.Tests/DashboardBrowserRunner.fs`, around line 667).
- Where the two figures are written down: `docs/how-live-testing-works.md` ("What's still rough"), `docs/live-testing-as-you-type.md` (two places, one a "Performance Budget" table whose per-step rows are, in its own words, "design figures, not measurements"), `docs/vs-dotnet-watch.md`, `docs/progress.md`, and the roadmap entry. `Readme.md` (line 539) says any end-to-end speed number about SageFs "is an anecdote until something gates it", for everything else.
- The 712 ms includes the daemon's own FCS type-check delay (300 ms, `Timeouts.fs`), per `live-testing-as-you-type.md`. The editor's own 300 ms pause is not in it.
- Precedents to copy: `docs/repl-loop-benchmarks.md` (three projects, machine and load average printed, measured by hand, no script behind it) and `scripts/machine-bench.fsx` (throwaway daemon in its own data dir, per-stage p50/min/max, peak memory from `/proc`, `--out FILE` raw samples as JSON, `--label`, `--memory-cap`, systemd-run tiers). A generated-page ratchet: `scripts/gen-roadmap.fsx` with `SageFs.Tests/RoadmapDocTests.fs`. A budget that only goes down: `SageFs.Tests/RatchetBudgets.fs`, lowered by `SageFs.Tests.dll --ratchets --tighten`.
- Per-stage histograms are declared in `SageFs.Core/Instrumentation.fs` (`sagefs.test_cycle.fcs_typecheck_ms`, `treesitter_parse_ms`, `test_execution_ms`, `end_to_end_ms`, lines 40 to 46). I did not check where each is recorded, or whether any route returns them without an OTLP collector.
- Candidate subjects: `~/Work/fsharp-compiler-services` at `cdb9dc5e5` (2026-09-19). By grep, `tests/FSharp.Compiler.Service.Tests` has about 2,251 `[<Fact>]`/`[<Theory>]` attributes and `tests/FSharp.Compiler.ComponentTests` about 7,370. `SageFs.Tests` is "about 14,000" by the brief; my grep for `testCase "` alone is about 7,850, so the real count comes from `list_tests`, not from this plan.

I could not find: a published figure for the confirming build, for a suite with slow tests, for the first edit after Ready, or for the cost of instrumentation at session start (`how-live-testing-works.md` says "I haven't measured it on a large solution"). I also could not find a record format; the only structured output today is the `LATENCY` text line.

## Design

### Records

A record is one JSON file per subject and SageFs version under `docs/benchmarks/live-testing/`. New pure module `SageFs.Core/LatencyRecord.fs`, serialised through a `SageFs.Json` profile (no `JsonSerializerOptions` at a call site):

```fsharp
type SubjectId = FromCSharp | Synthetic of SyntheticShape | SageFsTests | FcsServiceTests

type PathId =
  | KeystrokeToVerdict            // the edited function's own test changes verdict
  | KeystrokeToSettled            // nothing running, all rows final
  | SaveToGreen
  | EditNoTestReaches             // the conservative floor: every discovered test runs
  | ConfirmingBuild               // quiet window over to Confirmation = idle
  | FirstEditAfterReady

type PathResult =
  | Measured of Samples          // raw milliseconds, ints, so the page can be recomputed
  | NotMeasured of why: string   // a cell with no number says why; no empty cell

type Record =
  { Schema: int
    Subject: SubjectId
    ProjectCommit: string
    DiscoveredTests: int
    SageFsVersion: string
    SageFsCommit: string
    Machine: Machine            // moves here from SageFs.Tests/LatencyStats.fs
    LoadAverageAtStart: float
    LoadAverageAtEnd: float
    Paths: Map<PathId, PathResult> }
```

`Summary`, `summarize`, `Machine` and `reportLine` move from the test project into Core, so the script and the test share one definition. `LatencyStats.fs` stays as a thin re-export for the existing journey tests.

### The driver

Not a new test tier. A new tier needs a CI stage (`AGENTS.md`: `TrustSignalTests` fails the fast suite for a registered tier that `ci-pipeline.fsx` does not invoke), and CI is a poor place to record numbers: the runner is shared and a 10,000-test session is minutes of machine time. `scripts/machine-bench.fsx` is already run by hand on a chosen machine. So:

- `scripts/bench-live-testing.fsx`, thin glue in the style of `scripts/machine-bench.fsx`: start a throwaway daemon on its own data dir and port pair, create a session on the subject, wait for Ready by event, run the paths, write the record. Takes `--subject`, `--out`, `--ready-cap`, `--label`.
- The probe edits come from library code with tests: `SageFs.Tests/LatencyProbe.fs` (pure): given a file's text and a function, produce the red and green variants, the way `LiveTestingLatencyTests` uses `greenAdd`/`redAdd`.
- The synthetic subject is a pure generator, `Fixture.render : SyntheticShape -> (path * content) list`, written to a temp directory at run time. 10,000 generated test cases are not checked in. `SyntheticShape` is a record of test count, per-test cost (a bounded CPU loop, not a timer, so the cost does not depend on the scheduler), and the covering fan-in of the probed function.
- Subjects that are real repositories (`SageFs.Tests`, FCS) are run in a reflink clone (`cp --reflink=auto -r`) that is deleted after, so the probe edit never dirties a checkout a person works in. The existing `ensuring cleanup` pattern restores the file, and the clone covers a crash.
- The existing `--integration-lt` tier keeps running the small subject with its 3 s p95 gate in CI. That stays as the harness's own regression test.

### Publication

`scripts/gen-live-testing-numbers.fsx` renders `docs/live-testing-numbers.md` from the records, the way `gen-roadmap.fsx` renders the roadmap. The five pages that quote 712 and 542 replace the figures with a sentence and a link, and the roadmap item moves to Built when the page exists (a landmark on `docs/live-testing-numbers.md`).

### Ratchets

All four are tests in the default suite, registered with `Ratchet.register` in `TestInfrastructure.fs` so `--ratchets` runs them.

1. Stale page: the page on disk equals the render of the records (as `RoadmapDocTests`).
2. Provenance: every record has a machine, a load average, a project commit, a SageFs version, a non-zero discovered count, and at least `samplesPerPath` samples for each `Measured` path. A shorter run is refused at write time and at read time.
3. One home: no page under `docs/` or `Readme.md` outside the generated one contains a latency figure of the shape `p50 <digits> ms` or `p95 <digits> ms`. The count of offending pages is a budget in `RatchetBudgets.fs`, starts at the real count and only goes down. Counted 2026-11-11: six files (`how-live-testing-works.md`, `live-testing-as-you-type.md`, `vs-dotnet-watch.md`, `progress.md`, `Readme.md`, and `docs/roadmap.md`, whose sentence comes from `scripts/roadmap/RoadmapItems.fs:433`).
4. Bounds follow measurements: each gated bound (`liveTestingKeystrokeP95Bound` today) must be at least 1.5 times and at most 5 times the recorded p95 of the subject it gates. A bound loose enough to pass anything fails, as does one tighter than the measurement. `--ratchets --tighten` lowers it.

Whether a record may go stale is a decision below.

### Wire and clients

None. No route, tool, or event changes; `apiVersion` stays 4. If step 1 shows a stage breakdown needs a daemon route, that is a separate change with its own plugin note.

## Tests

RED first.

1. `LatencyRecordTests.fs`: a record with an empty machine, a zero discovered count, or 19 samples is refused with a named reason; a valid record round-trips through the `SageFs.Json` profile byte for byte. Fails today, no module.
2. `LiveTestingNumbersDocTests.fs`: the generated page equals the render of the records; a `NotMeasured why` cell renders its reason. Fails until the page and one record exist.
3. `LatencyFiguresTests.fs` (ratchet 3): counts pages with an inline figure; budget 5; a sixth fails.
4. Property: `summarize` agrees with a reference quantile on any list of 20 to 200 durations. (Correction, 2026-11-11: `SageFs.Tests/LatencyStatsTests.fs:44,55` already hold two properties, min <= p50 <= p95 <= max and order independence; the reference-quantile property is the one still missing, and it moves with the function into Core.)
5. `LatencyProbeTests.fs`: the red and green variants differ in exactly the intended line, and applying red then green restores the original text exactly.
6. `FixtureTests.fs`: `Fixture.render` is deterministic for a shape, produces exactly the requested test count, and a 10,000-test shape renders in under a second (a bound on a pure function).
7. Mutation: `summarize` with `nearestRank` off by one; ratchet 4 with the lower bound removed.
8. Simulation: not applicable to the measurement itself, which is a wall-clock instrument by nature. The only timing logic here is the summary, and it is pure and property-tested. I am not inventing a scheduler for it.
9. Outcome gate: `scripts/bench-live-testing.fsx --subject fromcsharp` run by hand on the release machine, compared against the published page; the record it writes must pass test 1. It is a line in the release checklist, not a CI stage. I did not read `scripts/ship.fsx`; step 8 finds where the checklist lives.

No wall-clock waits in tests 1 to 7. The driver waits only on daemon events (`expectFrame`, `awaitConfirmed`, `awaitSettled`), as the existing journeys do, with the ceilings coming from `TestTimeouts` for the test tier and `--ready-cap` for the script.

## Steps

1. Count and load. Create sessions on `SageFs.Tests` and `tests/FSharp.Compiler.Service.Tests`; record `list_tests` count, time to Ready, discovery time, daemon plus worker plus host resident memory. If a session will not load, that is a SageFs bug to fix or file with the exact paths (`AGENTS.md`), and it blocks the subject. One day.
2. `LatencyRecord.fs` in Core, with tests 1 and 4; `LatencyStats.fs` re-exports. Pure.
3. `LatencyProbe.fs` and `Fixture.render` with tests 5 and 6. Pure; parallel with 2.
4. `scripts/bench-live-testing.fsx` against FromCSharp. Reproduce 712/542 within 20 percent; if it does not, fix the harness before anything else.
5. Add the paths the current test lacks for FromCSharp: keystroke to settled, edit no test reaches, confirming build, first edit after Ready. These are new numbers on the old subject and cost nothing in project size.
6. Synthetic subject: the small grid first (1,000 and 10,000 tests, 1 ms and 50 ms).
7. `SageFs.Tests` and the FCS project; reflink clones.
8. Page generator, page, ratchets 1 to 4 (tests 2, 3), then replace the five inline figures.
9. Roadmap landmark and `how-live-testing-works.md` "What's still rough" rewritten from the page.

First week: steps 1 to 5, with 2 and 3 in parallel (file-disjoint: `SageFs.Core/LatencyRecord.fs` against `SageFs.Tests/LatencyProbe.fs` and the fixture). Steps 6 and 7 are runs, not code, and need a quiet machine.

## Measure first

- Variance. Run the FromCSharp path ten times on an idle and on a loaded machine and look at the spread of the p50s. The page says the p50s stayed within 700 to 720 ms and 536 to 620 ms across several runs. If a 10,000-test subject has a spread of the same order as the effect between cells, n=20 is too few and the sample count rises before anything is published.
- Where the time goes. Read where `Instrumentation.fs`'s four histograms are recorded. If they are, read them from the daemon during the run (a `MeterListener` in the script's own process is not enough, the daemon is a separate process; an OTLP file exporter or a route is). If they are not recorded, the first stage breakdown needs a small change in `LiveTestingCycle`'s effect handler, and that goes in as its own commit.
- Whether per-test coverage forces serial runs at 10,000 tests. `how-live-testing-works.md` says a project with instrumented assemblies runs one test at a time. With 10,000 tests at 50 ms, a floor run is minutes. That number is the one most likely to change what the product says about itself.
- Instrumentation cost at session start on the large subjects (shadow copy plus Cecil pass): paid once, per `ActorCreation.fs`; unmeasured.

## Risks and dependencies

- The FCS repository needs its own bootstrap build before a session can load its test project. If that is more than a day, FCS slips behind `SageFs.Tests` and the page says which subjects are measured.
- A record is a point in time. A silent regression after the record is not caught by this plan; only the small subject's gate runs in CI. Decision below.
- The probe edit on a real repo is the risk of dirtying it. The reflink clone is the mitigation.
- Depends on nothing. Enables: `confirm-builds-without-restarting-the-worker` (both venues' costs come from path `ConfirmingBuild` and the memory column), `test-selection-that-learns` (it only pays where the floor run is long, which this measures), `run-tests-reuses-proven-results` (the time a reused result saves), and the onboarding trial on the F# compiler repository, which needs these exact numbers.

## Size

M. The code is small (a record, a generator, a script, four ratchets); the time is the runs on large subjects and what they turn up.

## Decisions for Will

1. May a record go stale? Recommendation: no red build for age; the page prints each record's date and SageFs version, and a record more than 30 releases old is marked "old" on the page. Wall-clock gating of big subjects in CI would flake.
2. Do the large subjects gate anything? Recommendation: no, they are published. Only the small subject keeps a p95 gate, with ratchet 4 keeping that bound honest.
3. Which FCS test project first? Recommendation: `FSharp.Compiler.Service.Tests` (about 2,251 tests); `ComponentTests` (about 7,370) is a later row once the first works.
4. Publish raw samples? Recommendation: yes, in the JSON, so anyone can recompute a percentile.
