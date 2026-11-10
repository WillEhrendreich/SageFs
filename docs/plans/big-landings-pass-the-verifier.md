# A big landing waits on the run's progress, and a build-input change does not select the whole assembly

Roadmap item: `big-landings-pass-the-verifier` (Built, Agents). Plan written 2026-11-08 against master cfdd0eb0, built 2026-11-09 against master d4a66a21.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Done (the commit that adds `SageFs.Core/Features/RunStall.fs`). Step 6, a real landing through a real daemon, has not run, and what is unproven is listed at the end. |
| Size | M for the progress-based stop, S to M for the smaller set |
| Branch and worktree | none; on master |
| Last green commit | 59a67142. Filtered runs only: the `RunStall`, `DST the wait on a test run`, `ProjectFileChange`, `DST which files a compile-list edit can affect` and `Cohort landing verification` lists ran 26, 13, 31, 12 and 15 with nothing failing, and the `CohortLandingVerify`, `Timeouts` and `Settings panel render` lists passed. The ratchet lane ran 554 with six red, none in a file of this item. No unfiltered tier has run. |
| Next action | Install this build as the daemon and land a real project-file change through the cohort (step 6). Until the daemon runs this build, the worker still cancels a run two minutes after it starts: the measurement below is that. |
| Decisions made | The stall window replaces `globalTestRun` and keeps its two-minute value. The gate looks at the run once per window and calls it stalled when the count of reported tests did not move across one window. A project file change that is not a compile-list edit runs the whole suite, and so does any other build input (props, targets, a lock file, the SDK pin, a resource). A compile-list-only edit selects the tests that cover the files whose meaning it can have changed, read from the file texts. The floor stays. |
| Surprises | See "What the measurement found" and "The order rule is not enough". |

## What done looks like

A landing that changes a project file or a shared source file such as `SageFs.Core/Timeouts.fs` is verified by the cohort's landing gate and not by hand. The gate keeps waiting for as long as tests are still reporting results, and ends a run that has stopped reporting, with the number that did and the number that did not. When a change cannot affect a test (a project file that only adds a source file), the landing does not run the whole assembly for it. Fail-closed stays: a test that is not a confirmed `Passed` still counts as failing.

## What shipped

The wait.

- `RunStall` (`SageFs.Core/Features/RunStall.fs`, pure over the looks, BCL only). `judge` says `Finished`, `KeepWaiting` or `Stalled` from the look at the previous slice and the look now: the same count of reported tests a window apart is a stall, a higher count is progress, and a run not started at two looks is `NeverStarted`. `await` is the loop around it with two ports, the same shape as `IntegrationSettle.await`. `watchWith` runs a real run under the watch and cancels it on a stall.
- `Timeouts.testRunStallWindow` replaces `globalTestRun`, and `livetest.testRunStallWindowSeconds` replaces `livetest.globalTestRunTimeoutSeconds`. A run is judged by `RunStall.effectiveWindow`: never less than twice the per-test timeout, because a test may take that long to report.
- `LiveTestingExecutors.executeFiltered` has no deadline of its own. A hung test is bounded by the per-test timeout and a caller cancels the token. The worker's two callers (`WorkerHttpTransport` for the stream and `WorkerMain` for the plain run, which also had its own `30 s plus a tenth of a second per test` cap) run it through `RunStall.watch`, so the cap that was in two places is in none.
- `CohortLandingVerify.awaitRun` replaces the two budgeted waits in `runTestsInSession`: one loop, `RunStall.await` over `advance`, `lookAt` and `RequestedRuns.reportedIn`, which counts the requested tests with a result stamped by this run's generation. A stall answers `Error` naming how many reported and how many did not, and the landing does not fast-forward. `failingOf` and the generation stamp are unchanged, so a test that never reported still fails the landing.

The smaller set.

- `ProjectFileChange` (`SageFs.Core/Features/ProjectFileChange.fs`, pure). `diff` turns a project file's two texts into a closed set of changes: `CompileList`, `ReferenceChanged`, `PackageChanged`, `PropertyChanged`, `OtherItemChanged`, `Unreadable`, `ProjectCreated`, `ProjectDeleted`, `OtherBuildInput`. Whitespace and comments are no change. `forcesWholeSuite` is true for every kind but `CompileList`. `select` says `WholeSuite` with the reasons, or `NarrowToFiles`.
- `affectedFiles` is the rule for a compile-list edit. A file's meaning is its text and the meanings of the files compiled before it that it can see, in order. So a file is affected when the sequence of files before it that it can see differs between the two lists, or when it can see an affected file. `factsOfText` says what a file can be seen through: a module is reached by its own name, so a file that never names it cannot see it. A namespace that declares a type, an `AutoOpen` module, an extension or a file it cannot read is opaque and everything after it is affected.
- `DaemonMode.verifyByRebuild` takes the selection. The whole suite runs for `WholeSuite`. For `NarrowToFiles` the tests that cover those files run, plus the tests the rebuild discovered that were not known before, plus the tests that cover the landing's existing source files with the floor they always had. The floor for the landing as a whole stays: a selection of nothing runs every test.

## What the measurement found

Timed on 2026-11-09 in a session on `SageFs.Tests` (category `unit`) at a load average of 45 (other agents building on the same machine), against a daemon that does not run this fix: 18886 tests requested, the run ended at the old two-minute cap with 1688 passed, 1 failed, 4 skipped and 17193 that did not report. That is the defect, measured through the session. It says the machine managed about 14 results a second at that load, so a whole run through the verifier would take about 22 minutes there and 4 minutes 24 seconds when the machine is quiet (the earlier direct timing). It was not run to the end: the daemon is the user's and only an install of this build makes the worker stop cutting at the clock.

The daemon's model records each result as it arrives. The performer batches results (25 or 200 ms) into `TestResultsBatch`, which stamps each with the run's generation, so the count the gate watches rises during the run.

How many tests a change to `SageFs.Core/Timeouts.fs` selects was not measured. Coverage in the integration session is cold until tests have run in it, and a cold test counts as affected, so the figure depends on whether the session had run anything.

## The order rule is not enough

The first version of the classifier used the compile order alone: the files from the first difference on. On the real lists that was 935 of 1096 files for two test files inserted in the middle of `SageFs.Tests`, and 116 for one file inserted in the middle of `SageFs.Core`. With the file texts read, the same two edits select the new files only. The order rule stays as the upper bound (`reshapedFiles`) and `CompileListSim` checks that the classifier never goes past it.

## Tests

- `RunStallTests`: the judgement by example, three properties (a count that rises is never called stalled, the first look that does not rise ends it, one quiet look after any rise ends it), the loop over scripted looks, and `watchWith` with windows the test ends itself (a stall names the count that did not report, a run that reports in every window over forty windows is never cancelled, the caller's token is `Cancelled`, a failure in the work reaches the caller, and the real executor under the watch).
- `RunStallSim` and `RunStallSimTests`: the real `RunStall.await` over seeded scripts on a virtual clock, five named invariants, the cohort's 19067-test run (2.2 windows) finishing, 538 of 19067 and then quiet naming 538 and 18529, and a twin with the fixed deadline that cuts the cohort's run at one window.
- `CohortLandingVerifyDstTests` (new list): the real `SageFsUpdate.update` and `awaitRun` over a worker script on a virtual clock. A 2000-test run over 2.2 windows lands exact. A run that goes quiet is an `Error` naming its count and is never an `Ok`. Two twins: the old budgeted wait over a worker that cuts at the cap answers `Ok` with 1000 or more tests failing for a run whose tests all pass, and the new wait over a worker that still cuts is not exact either, which is why the cap in the worker is part of the fix.
- `ProjectFileChangeTests`, `CompileListSimTests`: the diff kinds, the rule, the scan, three properties, and the simulation over 1500 seeded edits (append, insertion that may shadow a name, removal, move) against F#'s own rule written apart from the classifier. SAFETY holds for the real classifier. Three twins are caught (added files only, no transitive step, off by one) and the order rule is shown safe and not narrow.
- `CohortGitTests`: `fileAt` and `existsAt` against a real repository (an `[Integration]` list, run with `--integration-host`).

## What is still unproven

- A real landing of a project file change through a real daemon (step 6). No landing ran.
- Whether the daemon, running this build, finishes a 19000-test run through the verifier, and how long it takes.
- The rule that a module is reached only by naming it. It is F#'s name resolution as I read it and the simulation proves the algorithm against that reading. The scan is regular expressions over text. A construct I did not think of that makes a file visible without its name, or a file the scan reads as a plain module that is not, would select too little. The scan errs to opaque on anything it does not recognise, and a file it cannot read is opaque.
- Projects that reference the changed project. A new type that shadows one a downstream file already opens is left to the build and the release gate.
- Other build inputs (props, targets, lock files, a resource) now run the whole suite. Before, they narrowed by whatever else was in the diff. This is stricter on purpose and costs run time on a landing that bumps a package.
- `existsAt` answers "no" when git cannot resolve the commit, so a failing git call reads a changed source file as one the landing added and leaves it out of the floor.
- Time to run `Timeouts.fs`'s selection was not measured (see above).
