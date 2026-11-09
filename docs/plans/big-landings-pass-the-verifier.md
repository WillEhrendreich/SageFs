# A big landing waits on the run's progress, and a build-input change does not select the whole assembly

Roadmap item: `big-landings-pass-the-verifier` (Next, Agents). Plan written 2026-11-08 against master cfdd0eb0.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M for the progress-based stop, S to M for the smaller affected set, once measured |
| Branch and worktree | none yet |
| Last green commit | none yet. It starts from `SageFs/CohortLandingVerify.fs`, `SageFs.Core/Features/LiveTestingExecutors.fs` (`executeFiltered`) and `SageFs/DaemonMode.fs` (`affectedTestsFor`) as on master. |
| Next action | Do "Measure first": how long the whole non-Integration set takes to run in the integration session, with the results arriving. Then write the RED cases in "Tests". |
| Decisions made | none yet |
| Surprises | The cap is in two places. The daemon's wait is bounded (`awaitBudget`) and so is the run itself: the executor cancels it at `Timeouts.globalTestRun` wall clock. A fix to the daemon's wait alone leaves the run cancelled under it. |

## What done looks like

A landing that changes a project file or a shared source file such as `SageFs.Core/Timeouts.fs` is verified by the cohort's landing gate and not by hand. The gate keeps waiting for as long as tests are still reporting results, and ends a run that has stopped reporting, with the number that did and the number that did not. When a change cannot affect a test (a project file that only adds a source file), the landing does not run the whole assembly for it. Fail-closed stays: a test that is not a confirmed `Passed` still counts as failing.

## What exists today

Read from the tree on 2026-11-08. Each line names where.

- The wait. `CohortLandingVerify.runTestsInSession` dispatches the run and then waits twice, once for it to start and once for it to finish, each for `awaitBudget ()` (`SageFs/CohortLandingVerify.fs` lines 137 and 203 on). `awaitBudget` is `Timeouts.globalTestRun () + Timeouts.testRunAwaitSlack`. `globalTestRun` is `forMachine (TimeSpan.FromMinutes 2.0)` (`SageFs.Core/Timeouts.fs`, near line 278) and `testRunAwaitSlack` is `forMachine (30 s)`. Past it the answer is `Error "Test run timed out."`, which the performer reports as inconclusive: the landing is Blocked and does not fast-forward.
- The run. `LiveTestingExecutors.executeFiltered` links a cancellation source and calls `globalCts.CancelAfter(Timeouts.globalTestRun())`. It runs the tests in chunks of `maxParallelism` and calls `onResult` as each test finishes. At the cap the remaining tests never run and never report.
- The verdict. `CohortLandingVerify.failingOf` counts anything but a confirmed `Passed` as failing, a `Skipped` and "no result at all" included, on purpose (its comment says why). `Cohort.TestsCompleted of LandingId * failing * CohortScope` carries it.
- What the conductor saw. In the cohort run notes (`finding C-integration-in-verify`, 2026-11-08 03:45Z): a full run in the integration session had 19067 tests requested and 18529 of them "did not report". The same notes say what the hundred Blocked tests of two landings were: nearly all `[Integration]` tests, each `Skipped "Timed out after 5s"`. That cause is fixed (`CohortLandingScope` leaves `[Integration]` tests to the release gate and names them, 1e9b18a3). It does not cover a run that is cut short at the cap.
- The affected set. `AffectedTests.affected` selects a test when a file its coverage names matches a changed file, and always selects a test whose coverage is missing or empty. A project file is not a file coverage names, because coverage records source documents. So for a change to an `.fsproj` the narrow selects nothing, and the floor returns the whole discovered suite: `affectedTestsFor` (`SageFs/DaemonMode.fs`, near line 3097) and `AffectedTests.verificationTestSet` both do it on purpose ("NEVER empty on a real diff"). The build-input path, which rebuilds the integration worktree and restarts the session, runs the tests the narrow selected before the rebuild plus the tests the rebuild added, and falls back to every test when that is empty (`DaemonMode.fs` near line 3181). Right after a restart the session's coverage bitmaps are cold, so every test counts as affected there too.
- The part of the wait that is already progress-based. The wait on the integration session settling ends on a stall, not a deadline: `IntegrationSettle.judge` and `await`, with `Timeouts.cohortIntegrationStallWindow`, and a deterministic simulation (`IntegrationSettleSimTests`). The test run's wait did not get the same treatment.
- The size of a whole run. An unfiltered run of the default suite, directly through Expecto (not through the verifier), ran 16,333 tests in 4 minutes 24 seconds on 2026-11-08 (the machine's load at the time was not recorded). That is longer than the daemon's wait of `2 min + 30 s`, and I did not measure the run through the verifier.
- Not found. The coordinator reported that a landing which changes an `.fsproj` or `Timeouts.fs` selects about 17,000 tests and cannot finish, and that the 18 branches of the 0.6.940 week were landed by hand for that reason. I did not find that figure or that reason in the cohort notes. They say p24 was integrated by hand because its own landing would be judged by the old verifier, and that seven landings (p24, p0, p4, p3, p17, p19 and the live-bindings branch) went in by rebase and fast-forward after Will chose it ("we need a release soon with this"), with ten more handed back to their agents to rebase after conflicts. I did not count how many tests `Timeouts.fs` selects.

## Measure first

1. How long the whole set, minus `[Integration]` tests, takes to run in an integration session, with a count of results as they arrive. That number decides how much the progress-based stop matters and whether the narrower set is still wanted.
2. How many tests a change to `SageFs.Core/Timeouts.fs` selects with warm coverage, and with cold coverage after a rebuild. The coordinator's figure is not in the tree.
3. Whether the daemon's model records each result as it arrives. `onResult` fires per test in the executor. If the model only learns at the end of the run, the stall check has nothing to read.

## Decisions for Will

- A project file change. Two ways, and they can both be done: (a) classify the change, so a project file diff that only adds or removes `<Compile Include>` items selects the new tests and the tests that cover the files in that project's neighbourhood, or (b) leave a build-input change to the rebuild-verify the gate already does (the build succeeds, the new tests pass) and to the release gate's own tiers for the rest. (a) is more code and a place for a wrong narrow. (b) is a policy that a landing is not the full verification. I would do the progress-based stop first and decide after measurement 2, because a stop that follows progress may be enough on its own.
- What `globalTestRun` means after this. Today it is a wall-clock cap on a run. Under a progress-based stop it becomes a stall window (no result for this long ends the run), so it needs a new name that says what it is for, and the setting `livetest.globalTestRunTimeoutSeconds` and `docs/configuration.md` change with it. A user's own run of a big suite hits the same cap today, so this is a change to live testing and not only to the landing gate.

## Steps

1. RED, pure: `RunStall.judge` (a new module beside `IntegrationSettle`): given the count of requested tests that have reported, the count at the last look and the window, say `Advancing`, `Stalled` or `Finished`. A run that is still reporting past the old budget is `Advancing`. A run whose count did not move across one window is `Stalled`, carrying how many reported and how many did not. Commit it red.
2. RED, simulation: `CohortLandingVerifyDstTests` gains a virtual-clock run of a long, advancing suite and of one that stalls part way. A twin that carries the fixed deadline fails the first, which is how the case shows it has teeth.
3. `executeFiltered` cancels on a stall and not on a wall clock, using the same judgement, with each test still bounded by the per-test timeout. `runTestsInSession` waits on the same signal.
4. Rename `globalTestRun` for what it is, move it to the stall window, and update the setting, `docs/configuration.md` (regenerated by its script) and `docs/mcp-tools.md`.
5. Measure first item 2, then decide (a) or (b) above and build it with its own RED case in `AffectedTestsTests`.
6. An outcome case: a real landing of a project file change through a real daemon lands without a hand rebase. `docs/decisions.md` records the answer.

## Tests

- The pure `RunStall` cases and the simulation in steps 1 and 2. The twin with the fixed deadline must fail the advancing-run case.
- A property over any sequence of reports: a run is never ended while its count rises, and always ended after one quiet window.
- `AffectedTestsTests` for step 5, whichever way it goes: a landing that adds a `<Compile Include>` selects fewer tests than the assembly, and a landing that changes anything else in a project file still falls back to the floor.
- The outcome case in step 6 is the gate. The expected landing state comes from the daemon's own answer.

## Risks and dependencies

- This changes a live-testing default for everyone, since `executeFiltered` serves every run. The per-test timeout stays as the bound on a hung test, so a run cannot stall silently. A run whose tests each take the full per-test timeout is still advancing.
- Fail-closed has to survive. A stall ends the run as inconclusive and names how many did not report, and the landing does not fast-forward.
- A wire change if the status text or a setting key changes, so the commit carries a `Plugin:` trailer, and `sagefs.nvim` reads the landing line by its first word.
- `IntegrationSettle` and `CohortLandingScope` (p24) are the pattern and stay as they are.

## Size

M for the stop, S to M for the smaller set. Measurement 1 can shrink it.
