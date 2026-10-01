# How SageFs runs your tests as you type

As you edit an F# file, SageFs type-checks the unsaved buffer in the session that already has your project loaded, works out which tests that edit can affect, and runs them, so pass and fail marks move before you save. If the buffer doesn't type-check, nothing runs and the last results stay up, marked as blocked by compile errors, and every selection carries a label saying why those tests were picked.

This page describes the v0.6.868 tree (`bba42706`), and every source link is a permalink to that commit. Statements about Microsoft's products were read on 2026-10-01. I didn't run SageFs for this page, so what follows is what the code says it does.

## What I learned from

NCrunch, Wallaby, testmon, Microsoft's test impact analysis and Visual Studio's Live Unit Testing are different answers to the same question, which is how to rerun fewer tests without lying about it.

NCrunch watches your open windows and the file system, builds the affected projects, and decides what's impacted in one of two ways. `CompareIL` compares the new assembly against the old at the IL level. `WatchText` marks any test covering any line in a changed source file, is "much less accurate", and doesn't wait for a build. The docs add that "no form of impact detection is 100% accurate. Tests should always be run before committing code" ([engine modes](https://www.ncrunch.net/documentation/concepts_engine-modes), [impact detection](https://www.ncrunch.net/documentation/reference_global-configuration_impact-detection-mode)). Wallaby.js runs tests as you type without saving, tracks change at a finer level than files, and puts coverage in the editor margin ([docs](https://wallabyjs.com/docs/)). pytest-testmon records which code each test ran through Coverage.py and compares file and function checksums to find what a change touches. It always reruns what failed last time, and it doesn't track static files or external services ([testmon](https://testmon.org/), [repo](https://github.com/tarpas/pytest-testmon)). Microsoft's test impact analysis in Azure Pipelines keeps a map from test method to dependencies, and when a change is one it can't reason about it runs everything ([docs](https://learn.microsoft.com/en-us/azure/devops/pipelines/test/test-impact-analysis)). Visual Studio's Live Unit Testing makes a workspace copy of your repo, applies your unsaved changes to it, builds, and runs the impacted tests on instrumented binaries ([docs](https://learn.microsoft.com/en-us/visualstudio/test/live-unit-testing)).

Jest and Vitest pick tests from the module graph and work on files on disk, so they react to a save, not a keystroke ([Jest](https://jestjs.io/docs/cli), [Vitest](https://vitest.dev/guide/features.html)).

The one property I take from the good ones: there is a map from code to the tests that exercise it, built from what the tests actually touched, and the map is never trusted past what it knows. NCrunch tells you to run everything before you commit. testmon reruns last time's failures. TIA runs all tests when it's out of its depth. Of the group, only Wallaby, Live Unit Testing and (going by its wording) NCrunch react before you save. That second thing, reacting to the buffer, is the part I most wanted.

## What F# and .NET don't hand you

Visual Studio's Live Unit Testing page says it is "supported only in .NET". The 2017 article named C# and Visual Basic, and the real-time test discovery post says that feature is "powered by the Roslyn compiler" and "only available for C# and Visual Basic projects" ([overview](https://learn.microsoft.com/en-us/visualstudio/test/live-unit-testing-intro?view=vs-2022), [2017 article](https://learn.microsoft.com/en-us/visualstudio/test/live-unit-testing?view=vs-2017), [.NET blog](https://devblogs.microsoft.com/dotnet/real-time-test-discovery/)). The current pages never mention F#. I read no Microsoft statement saying F# is unsupported, so all I'll say is that as of 2026-10-01 none of the pages I read says it is supported.

F# has no Roslyn. The thing that can check an unsaved F# buffer against your loaded project is the F# compiler's service, and an FSI session is an instance of it with your project already referenced. That's the seat I use.

The tests F# people write are mostly Expecto `testList` values with FsCheck properties inside. There's no attribute on each leaf to find in source, so SageFs reflects each `[<Tests>]` binding and flattens it into leaf cases ([`LiveTestingExecutors.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingExecutors.fs#L604-L627)). xUnit, NUnit, MSTest and TUnit work too ([built-in list](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingExecutors.fs#L676)).

And a default test run doesn't record which source lines a test touched. If you want a map from code to tests you have to build the recorder yourself.

## One keystroke, in the order the data moves

```
 keystroke in the editor
    |  300 ms after the last edit (VS Code)
    v
 POST /api/sessions/{sid}/buffer-changed      (the whole unsaved buffer)
    v
 only comments or whitespace changed? --yes--> cancel the pending check, queue nothing
    | no
    v
 tree-sitter (50 ms): where are the tests?    FCS type-check of the whole buffer
                                              (300 ms, backing off to 2 s)
                                                   |-- errors --> BlockedByCompileErrors,
                                                   |              last results stay
                                                   v
                       which tests?  name delta + dependency graph
                                     + coverage bitmaps (+ file scope on save)
                                     + changed lines against per-test coverage
                                     nothing narrows a compiled file --> all discovered tests
                                                   v   (decision + "why" label)
                       paused, or outside the scope? --> held back, said so
                                                   v
                       eval the buffer into the live FSI session
                                                   |-- eval fails --> nothing runs
                                                   v
                       run the selected tests, stream results in batches
                       (rows say "Evaluated")
                                                   v
                       read each test's coverage --> stored per test, used by the next selection
                                                   v
                       quiet for 2 s --> one real build of the saved text, tests run again
                       (rows say "VerifiedByBuild" or "BuildDisagrees")
```

### Session start: the recorder

Before any editing happens, the project outputs get shadow-copied and instrumented once with Cecil ([`ActorCreation.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/ActorCreation.fs#L114-L132)). The instrumenter reads every non-hidden sequence point from the PDB, including the ones inside F# closures, injects a static class with a `bool[]` called `Hits`, and inserts a `Hit(slot)` call at each point ([`CoverageInstrumenter.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/CoverageInstrumenter.fs#L11-L102)). A circuit breaker turns instrumentation off for the session after three straight failures ([same file](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/CoverageInstrumenter.fs#L307-L352)). Probes are booleans, not counts, so what you get back is a bitmap.

This is paid once, and it scales with project count. I haven't measured it on a large solution.

### The buffer leaves the editor

The VS Code extension waits 300 ms after the last edit and posts the whole unsaved buffer ([`Extension.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/sagefs-vscode/src/Extension.fs#L2385-L2435)). The daemon accepts it at `POST /api/sessions/{sid}/buffer-changed`, answers 202, and hands it to the Elm loop as `BufferContentChanged` ([`McpServer.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs/McpServer.fs#L2824-L2848)). Any client that can POST can drive it.

The first check is cheap. The buffer is normalized (comments dropped, whitespace dropped, strings kept) and compared to the last one. If they match, the pending type-check is cancelled and nothing is queued ([`TriviaNormalization`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L94-L181), [`onKeystroke`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L3967-L3998)). I'll flag the catch right here: F# indentation means something, and that normalizer throws it away along with the rest of the whitespace. I found no guard or test for an edit that only moves a line in or out of an offside block.

### Two debounces, two passes

A 50 ms pause starts tree-sitter, and a 300 ms pause starts the type-check, which backs off to 2 s when further typing keeps cancelling the checks ([`Timeouts.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Timeouts.fs#L154-L164)). None of those numbers has a recorded reason; the file says so next to each one. Tree-sitter finds where the tests are in the buffer, the one stage that doesn't need the code to compile, and it only finds them ([`TestTreeSitter.discover`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/TestTreeSitter.fs#L106-L118)). The native library is looked up per runtime id for win, linux and osx on x64 and arm64, and if it's missing, discovery from source returns nothing and logs why ([lookup](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/TestTreeSitter.fs#L39-L66)).

The type-check is `ParseAndCheckInteraction` on the whole buffer in the live session, with symbol uses extracted when there are no blocking errors ([`Diagnostics.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/Diagnostics.fs#L102-L123), [`SageFsApp.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs/SageFsApp.fs#L2640-L2696)). The whole buffer, not one function. There's no MSBuild on this path and no copy of your repo to get wrong.

If it fails on a keystroke, nothing is evaluated and nothing is run. The state becomes `BlockedByCompileErrors` with the file and the error count, and the last good results stay on screen ([`LiveTestActivity.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestActivity.fs#L125-L136), [`afterTypeCheck`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L4269-L4287)). A save is the exception: a compiled file that fails the check on save queues a rebuild so a real compiler can say the real error ([`fallbackRebuildAfterFailedTypeCheck`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L3666-L3706)). Don't expect test feedback on code that doesn't compile. What you get is the previous answer, labeled as blocked.

### Picking the tests

This is the map, and it has three sources that get merged.

The first is names. The type-check yields every symbol the file uses, and SageFs builds a dependency graph from them: which production symbols each test references, plus a call graph through the production code so a test reaches things transitively ([`buildFromSymbolUses`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L2214-L2257)). The symbols whose names moved are looked up in that graph ([`findAffected`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L2124-L2128)). The second is coverage: tests whose stored bitmaps intersect probes that live in the changed file ([`findCoverageAffected`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L695-L716)). The third only runs on save or an explicit run, because a name delta can't see a body rewrite (`a + b` to `a - b` moves no names), so every test that reaches any symbol the file declares gets selected. If nothing narrows a compiled file it falls back to every discovered test ([`decideAfterTypeCheck`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L3412-L3524)).

Every decision carries a cause (keystroke, save, explicit), a precision (`ExactDependencyMatch`, `CoverageApproximation`, `ConservativeFallback`, `NoImpactedTests`, `SuppressedByPolicy`), the symbols that moved, the tests chosen, the tests deferred, and a reason sentence. The status bar shows it as "why: exact (3 selected)" or "why: fallback rebuild (12 selected)" ([`LiveTestingDecision`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L846-L952)). Run policy decides what runs on every change: unit and property tests on every change, integration and browser on demand, architecture on save ([`RunPolicyDefaults`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L195-L211)).

The cohort landing gate (merging agents' work) has a stricter cousin, `verificationTestSet`, which never returns an empty set for a real diff against a real suite ([`AffectedTests.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/AffectedTests.fs#L48-L82)). That isn't the live keystroke path, which is rougher.

### Eval, then run

For a compiled `.fs` file with known buffer content, the effect that would have run or rebuilt the stale DLL is retargeted into evaluating the buffer in FSI and then running exactly the tests already selected ([`redirectToEvalBuffer`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L3639-L3664), [`SageFsApp.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs/SageFsApp.fs#L2707-L2759)). In the worker, the buffer goes through the same whole-file preprocessing the file watcher uses, so module identity is kept. If the eval fails, neither the compilation cache nor the discovery nor the last results are touched ([`evalLiveTestFile`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Host/WorkerMain.fs#L341-L416)). The discovery from that eval is merged over the compiled baseline by test id, and the dynamic one wins, so an edited-but-unsaved test overrides its compiled twin and doesn't show up twice ([`TestDiscoveryMerge.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/TestDiscoveryMerge.fs#L3-L26)).

Then the run. Each new run cancels the older one through a cancellation chain ([`CancellationChain`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingExecutors.fs#L1166-L1193)). Results stream back and get flushed to the model in batches of 25 or every 200 ms, at a parallelism of `max 4 (ProcessorCount / 2)` ([`SageFsApp.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs/SageFsApp.fs#L2951-L2975)). The per-test timeout defaults to 5 s and the whole run to 2 minutes, both overridable ([`Timeouts.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Timeouts.fs#L131-L142)). If you edit while a run is in flight, results that land afterward are labeled `StaleCodeEdited` and don't pass as fresh ([`TestRunPhase`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L997-L1027)). Any requested test that never reported gets a truthful "no result" with a reason, never a made-up failure ([`SageFsApp.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs/SageFsApp.fs#L2976-L2985)). The receipt that `run_tests` returns says the same thing for agents: a run where something didn't report is `Incomplete`, "never to be read as green" ([`TestRunReceipt.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/TestRunReceipt.fs#L46-L53)).

### Coverage comes back around

After each test, the worker reads the coverage the instrumented assemblies recorded, as a packed bitmap, and clears the hits, so the reading belongs to that one test. It goes back as a coverage frame beside the test's result, and the daemon stores it against that test alone (`WorkerHttpTransport.fs`, `CoverageProbes.readAndClear`, `SageFsEffectHandler.fs`). The probes of a process are one shared array, so two tests running at once cannot be told apart, which is why a run against a project with instrumented assemblies runs its tests one at a time. A run with nothing instrumented keeps the parallel path. Dynamic assemblies are skipped when reading, so only code loaded from disk is counted, and a reading that hit nothing never replaces a recorded bitmap (after a keystroke eval the tests run against dynamic code, and an empty reading would blank the gutter).

That does two things. The gutter and the file annotations list, per line, exactly the tests whose own recorded coverage reaches it. And selection can use it: an edit that moves no symbol name and changes only lines that recorded coverage speaks for selects only the tests that run those lines, labeled `line_coverage_narrowing`. The rules all point one way, toward running more. The buffer's lines are hashed and compared with the hashes of the text the assembly was compiled from, an edit that inserts or removes a line is not narrowed (every later line number moved), a changed line with no sequence point or one that runs when the module initializes is not narrowed, a test with no usable coverage is always included, and an empty answer widens (`CoverageBitmap.narrowByLines`, `LineEdit.between`, `decideAfterTypeCheck`). The reasoning, and the cost of the serial runs, are in [decisions.md](decisions.md).

### What a row says it ran against

A keystroke's tests run against code the session evaluated, not against a build, and every row now says which. `Compiled` is a build's output, `Evaluated` is evaluated code that nothing has confirmed, `VerifiedByBuild` is evaluated and then run again against a real build of the same text with the same verdict, and `BuildDisagrees` carries why: the compiler's message, both verdicts, or that the build never answered (`ResultProvenance` in `LiveTestingTypes.fs`). Once the editing has been quiet for two seconds, one build of the saved text is started through the session's own rebuild, and the tests run again against what it made. The verdict from the eval is on screen the whole time, the confirmation never delays it, an eval that ran no tests starts no build, and a burst of edits costs one build, for the last text. The decision is a pure state machine, `BuildConfirmation.step`, folded under seeded scenarios with three twins that reproduce the bugs its invariants exist for (`SageFs.Simulation/BuildConfirmationSim.fs`).

Two limits. A build can only confirm what is on disk, so an unsaved buffer stays `Evaluated`. And the build is a rebuild of the session, which restarts the worker: FSI state and unsaved edits in other files do not survive it.

### Pause and scope

Pause holds test runs back while the session keeps type-checking and keeps its evaluated code current, so resuming judges the code as it is now: it evaluates the latest buffer again and runs the tests that went stale. A scope (every test, only the ones matching a pattern, or all but the ones matching) narrows what automatic runs touch, and an explicit run always runs what it names (`/api/live-testing/pause`, `/resume`, `/scope`). What Visual Studio has beyond that, and why it isn't copied, is in [decisions.md](decisions.md).

## Compared with Visual Studio Live Unit Testing

Live Unit Testing is Enterprise-only and shipped in Visual Studio 2017 ([launch post](https://devblogs.microsoft.com/visualstudio/live-unit-testing-in-visual-studio-2017-enterprise/)). It is a mature product with a debugger attached, and I'm comparing a one-person tool to it.

| | Visual Studio Enterprise | SageFs |
|---|---|---|
| What it builds | A private workspace copy of the repo with unsaved edits applied, scoped parallel MSBuild builds of the relevant projects ([configure](https://learn.microsoft.com/en-us/visualstudio/test/live-unit-testing?view=vs-2022), [better and faster](https://devblogs.microsoft.com/visualstudio/live-unit-testing-preview-better-and-faster/)) | No workspace. The buffer is type-checked and evaluated in the live FSI session |
| Code that doesn't compile | Errors go to the Output window ([FAQ](https://learn.microsoft.com/en-us/visualstudio/test/live-unit-testing-faq?view=vs-2022)). What the glyphs show during a red build isn't documented | Nothing runs, last results stay, state says blocked |
| Picking tests | "impacted tests". The mechanism isn't documented | Three signals, each decision labeled with its precision and reason |
| Coverage | Per-line, with the count of tests that hit it, from instrumented binaries of the real build | Per-line, with the exact tests that hit it, from instrumented binaries of your project outputs (not of a fresh build of your unsaved text). The price is that tests run one at a time on an instrumented project |
| What a result ran against | Binaries from a real MSBuild of the solution | Each row says: evaluated code, a real build, a real build that agreed with the eval, or a real build that disagreed and why |
| Pause, include and exclude | Pause on battery and on debug, a playlist, an ignore file, memory caps | Pause, and an include or exclude set that automatic runs obey. No battery or debugger detection and no memory cap, see [decisions.md](decisions.md) |
| Debugging a failure | Hover the glyph, pick tests, Debug | None |
| Frameworks | xUnit, NUnit, MSTest | Expecto, xUnit (v2 and v3), NUnit, MSTest, TUnit |
| Languages | .NET. F# not mentioned on the current pages I read | F# |
| Platform, cost | Windows, ProjFS-backed workspace, Enterprise edition | Windows, Linux, macOS (tree-sitter binaries per runtime id), MIT licensed |
| Clients | Visual Studio | HTTP and SSE, so any editor. The VS Code client is in the repo. The Visual Studio extension is deprecated |

Where Visual Studio is ahead, and I mean it. Debugging: you hover a glyph, pick the tests and debug them. It runs tests against binaries from a real MSBuild of your solution, which is what ships, while on the keystroke path mine run against FSI-evaluated code, and I haven't shown that code behaves identically in every case. A type-check in an FSI session isn't your project's compile, so things only the real build catches can pass here and fail in `dotnet build`. What I did about it is smaller than matching it: each row says what it ran against, and a real build of the saved text confirms or contradicts the eval after the editing goes quiet, with the compiler's own message when it fails. That makes the gap visible and short. It does not make it zero, and an unsaved buffer is never confirmed. The whole-solution tooling is theirs too: a playlist, an ignore file, build hooks, pause on battery and on debug, memory caps ([configure](https://learn.microsoft.com/en-us/visualstudio/test/live-unit-testing?view=vs-2022)). I have pause and an include or exclude set, and I wrote down why I didn't copy the rest ([decisions.md](decisions.md)). The build happens in its own workspace so a regular build can't interfere, and mine shares the one session, and my confirming build restarts it. And it's been shipping since 2017.

What I think I do better is smaller than it sounds. There's no repo copy to get wrong, so the failures you hit are about your code. Comment and whitespace edits don't start a run, where Visual Studio starts a build "whenever it detects that source files have changed" and I found nothing in its docs that treats a comment edit differently. You can ask why a test ran. Expecto and FsCheck are first class. A failing FsCheck property carries its shrunk counterexample and is classed as a real failure, not a flake ([`FlakyClassification`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L1162-L1175)). And it runs on Linux.

## What's still rough

Per-test coverage costs parallelism. To attribute a reading to one test, the tests of an instrumented project run one at a time. I measured the keystroke path on the small sample below and nothing else, so I can't tell you what it costs a suite of slow tests. Line narrowing widens on any doubt, and I've exercised it in unit tests and on the sample, not on a large project.

On a keystroke, a body-only edit moves no symbol names, so the name signal is empty. Until [d6bd9e5e](https://github.com/WillEhrendreich/SageFs/commit/d6bd9e5e) the file-scope widening that covers this case ran only on a save or an explicit run, so `a + b` to `a - b` selected nothing while you typed and the pane stayed green. It now runs on a keystroke too, so the edit selects every test that reaches a symbol the edited file declares, and your run policy can still defer it and say so. Still open: with an empty dependency graph, or a graph that hasn't seen the test file that covers the symbol, that narrowing can find nothing, and I'm closing that.

The test-to-symbol graph attributes references to a test by a line-range heuristic: each test definition owns lines until the next one starts, and test functions are recognized by a `.Tests.` module name ([`LiveTestingTypes.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L2170-L2257), [`.Tests.`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L61-L63)). It only knows about test files that have been type-checked in this session, and the code says so ([comment](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L3449-L3452)).

Expecto leaves come from reflection, so they have no source line. Tree-sitter finds the `[<Tests>]` binding but not each `testCase`, so a marker on one specific `testCase` line isn't something you get today. Expecto categories come from the test name only, so a slow database test without "integration" in its name runs on every change under the default policy ([`LiveTestingExecutors.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingExecutors.fs#L616-L627)).

Code redefined by an eval lives in a dynamic assembly, which the coverage read skips. For a function you just edited the markers keep the last coverage that was recorded against a real build of it, so they describe the code as it was, not as you've typed it, until the confirming build runs the tests again.

Tests run inside the FSI host process alongside your session. I read the timeout and the cancellation and didn't look into what a test that blocks a thread or kills the process does to the session.

There's a `DebugTest` code-lens command in the model that no client consumes ([`TestAnnotations.fs`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/TestAnnotations.fs#L39-L43)). If your day is in Visual Studio, SageFs has no current client for it.

The flake rules (a window of ten runs, flaky at two flips with at least three samples) are numbers I picked ([`FlakyDefaults`](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Core/Features/LiveTestingTypes.fs#L1157-L1160)).

Latency is measured now, on one machine and one small project, and that is all the figures say. The `--integration-lt` tier starts a daemon, opens the FromCSharp sample (11 Expecto tests), and times two paths through the daemon's own `/events` stream, 20 samples each after 2 warm-up edits (`LiveTestingLatencyTests.fs`, `LatencyStats.fs`). From the edit leaving the client to the verdict on the edited function's test: p50 712 ms, p95 783 ms, min 651 ms, max 793 ms (the editor's own 300 ms pause is not in it). From a save to the suite back at all green with nothing running: p50 542 ms, p95 631 ms, min 512 ms, max 649 ms. The machine was an AMD Ryzen 7 5800XT, 16 threads, 24 GiB as the runtime reports it, Linux, .NET 11.0.0-rc.1, with other jobs running on it, and I ran the tier several times: the p50s stayed within 700 to 720 ms and 536 to 620 ms. The tier fails if the p95 of either passes 3 s, about four times what I measured, so a cold CI runner doesn't flake it. I have no figure for a large solution, for a project with slow tests, or for the first edit after a restart (the two warm-up edits are dropped).

## If you want to poke at it

- Run `list_tests` and `run_tests` over MCP and read the receipt. Ask `explain_test_failure` about a failure, and `coverage_intel` about a file.
- Enable live testing on a session and watch `GET /api/live-testing/status` while you edit a function a test covers. The integration test `HttpApiIntegrationTests.fs` ("editing a compiled F# file reruns tests against rebuilt output") drives this against the smoke sample ([test](https://github.com/WillEhrendreich/SageFs/blob/bba42706/SageFs.Tests/HttpApiIntegrationTests.fs#L1138-L1264)).
- Break the syntax mid-edit and check the status says blocked while the old results stay.
- Unit-level: `LiveTestingDecisionScenarioTests`, `LiveTestingAfterTypeCheckScenarioTests`, `CoverageInstrumenterTests`, `AffectedTestsTests`, `TestRunReceiptTests`. Full unfiltered run is `dotnet SageFs.Tests/bin/Release/net10.0/SageFs.Tests.dll --summary`, and a filtered run that matches nothing exits 0, so look for the test name in the output.
