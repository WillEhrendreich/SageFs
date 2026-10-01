# Things we looked at and said no to

Every roast round, somebody (human, agent, or an agent channelling a human) suggests the same five
ideas. This page is the bouncer's list. Each entry says what got turned away, why, and where the
evidence lives, so nobody has to re-litigate it from scratch. If you think one of these deserves a
second look, good: the last line of each entry says what would change the answer.

Two ground rules for this page. A decision with no evidence behind it doesn't get a line here. And
"we decided" only appears when something in the repo or in the project's recorded preferences backs
it up. Where a call is still open, it says so.

---

## The dashboard is one big morph, not a pile of little patches

The dashboard pushes the whole `#main` element over SSE and lets Datastar morph it. Every few
roasts someone proposes splitting that into per-panel fragment patches "for incremental updates".
Turned down. One authoritative render path is easier to reason about than a swarm of patchers that
can disagree with each other, and the payload cost belongs to the transport: compress the stream
(Brotli) and diff at the protocol level, leave the renderer alone.

The one render-side thing worth fixing is a timer push that fires when nothing changed, and that's
what the no-change suppression in the stream handler is for.

Evidence: the project's recorded preferences (the "fat morph" entry in `CLAUDE.md`'s taste notes);
`SnapshotRenderGuard` in `SageFs/Dashboard.fs`.
Reopen it if: someone measures a real latency or bandwidth problem that compression can't fix.

## Browser tests live in the F# suite, full stop

Playwright.NET inside Expecto. No separate TypeScript Playwright project humming along beside the
real tests, however tidy its folder looks. A second harness in a second language is a second place
for the truth to hide.

Evidence: `SageFs.Tests/DashboardBrowserTests.fs` and the `--integration-browser` tier;
the recorded preference in `CLAUDE.md`.
Reopen it if: a browser behaviour genuinely can't be driven from Playwright.NET.

## No standby worker pool

There used to be a pool of pre-warmed spare workers waiting to take over. The accounting never
worked out: the spares cost real memory and held ports, the boot contention cost startup time, and
the high-value case they were meant for (restart) was never the one wired to use them. Restart is now
spawn-first: start the replacement, wait for it to be ready, then swap, and the old worker keeps
serving until the new one is proven. Fewer moving parts and the same user-visible result.

Evidence: commits `553c70a3` and `6397cef9` (docs corrected after the removal);
`standby-session-rework.md` (the hand-off that did the removal).
Reopen it if: measured cold-start time, not a hunch, becomes the main complaint.

## Borrow Akka's ideas, don't borrow Akka

Supervision with backoff and child restart is a great idea. The Akka.NET package that comes with it
is a big dependency for a tool whose whole pitch is a small, inspectable closure. So the mechanisms
(restart policy, jitter, single-owner actors) are written in F# here, and nothing from Akka ships in
the daemon.

Same spirit for the supervisor itself: a thin F# one, not a native one in another language. A
second language in the stack has to earn its seat, and F# was enough to reach the isolation goal.

Evidence: the recorded preferences in `CLAUDE.md`; `SageFs.Core/RestartPolicy.fs`,
`ResilientActor.fs`.
Reopen it if: the F# supervisor can't hit a structural guarantee that a native one could.

## Kill the orphaned worker by watching the parent, not with a job object

Windows job objects are a fine way to make child processes die with their parent, on Windows.
SageFs runs on Linux and macOS too, so the worker polls its parent's pid instead. A hard kill of
the daemon (Task Manager, `kill -9`, a crash, a power cut) has to take the workers with it, and
"lookup failed" counts as "parent is dead", so a flaky probe can never orphan a worker.

Evidence: the recorded preferences in `CLAUDE.md`; the parent-death monitor in `SageFs.Core`.
Reopen it if: there's a cross-platform mechanism that's strictly stronger.

## When a project pins a version, adapt to it. Don't refuse.

"Fail loud" is a diagnosis, not a fix. If a user's project pins a different FSharp.Core or
System.Text.Json than the host was built with, the host re-initializes against the project's pins
where the APIs are compatible, swaps in version-matched variants where SageFs's own code is
coupled to the library, and moves feature dependencies out of the host entirely. Refusal is the
last-resort safety net before any eval, never the first answer.

Where that stands, checked against the code on 2026-10-01: what exists is the isolated host with a tiny
closure, the SDK's own FSharp.Core and compiler, a renamed Harmony, and a call-site rewrite that makes the
project's own assembly bind to the host's FSharp.Core. Re-initializing against a project's pins and swapping in
version-matched variants are designed (`HostAdaptation`, `VariantSelector`) and tested as pure logic, but nothing
calls them and no variant assemblies exist. Two projects in one solution that resolve different versions of the
same package, and a project that builds its own FSharp.Compiler.Service, are not covered by a test. Closing those
is open work, and nothing here should claim it until a test does.

Evidence: the recorded preferences in `CLAUDE.md`; the isolated FSI host design
(`SageFs.FsiHost`).
Reopen it if: an adaptation turns out to be unsound for a specific library.

## Live tweaking edits expressions, not just literals

The plan for phase three of live tweaking was briefly "scrub the numbers in the literals". That was
rejected as too small: an expression such as `gravity * 2.0` or `if hardMode then 80 else 100` is
just as editable as a number, and the source file gets the expression back, not a baked value.

Evidence: `live-tweak-spec.md`.
Reopen it if: the expression path can't be type-checked against the live value safely.

## Code changes land, state stays, nothing happens silently

The rule for hot reload: a redefined immutable gives you the new value; a live mutable keeps what
it has and tells you about it, with a way to reset it. The alternative, restarting on every edit
"to be safe", throws away exactly the state people wanted to keep, and the alternative where state
quietly survives without telling you is worse. Both are out.

Evidence: `hot-reload-state-spec.md`; `docs/hot-reload.md` (the what-reloads / what-restarts table,
each row pinned by a test).
Reopen it if: a class of state turns up that can't be kept or reported honestly.

## Dashboard navigation is a signal, not a URL

There used to be a `?session=` query parameter choosing what the page shows. It's gone. A Datastar
signal points at the session in view (or at nothing, which shows the picker), synced with the
backend. The URL is for things a human might bookmark on purpose, like `?panels=friction`, never for
which session happens to be selected.

Evidence: the recorded preferences in `CLAUDE.md`; `Signals.ViewingSessionId` in
`SageFs/DashboardTypes.fs`.
Reopen it if: people want deep links to a session badly enough to design them properly.

## Use the Datastar builders, not hand-typed attributes

Interactivity goes through the typed API (`Ds.signal`, `Ds.onInit (Ds.get ...)`,
`Response.ssePatchSignal`), not through strings that happen to look like `data-signals:...`. The
builders are the supported surface and the compiler checks them.

Evidence: the recorded preferences in `CLAUDE.md`; `SageFs/Dashboard.fs`.
Reopen it if: a needed attribute has no builder yet. Then add the builder.

---

## Agents get a door into the test engine, not a test runner of their own

`run_tests` existed, then got removed in March 2026 (commit `1961300a`, "MCP is read-only observer of
live testing"). With no verb, an agent that wants to run tests has to run `dotnet test` itself, which
is a second engine whose answers can disagree with `list_tests`, the dashboard and the cohort landing
gate. The sentence in `docs/mcp-tools.md` that called the removal
deliberate came from a voice-polishing pass, not from a recorded decision.

It's back, and the shape is the point: `run_tests` dispatches the same `RunTestsRequested` the
dashboard and editors send, tagged with a request id, and the receipt it returns is a pure read of the
engine's own record of that request (`RunRequests`, `ResultGenerations`, `LastResults`). There is one
source of truth and one more way in. It never counts a pass from an earlier run, it names every test
that didn't pass in this run and why, and `Incomplete` is not green.

Evidence: `SageFs/McpRunTests.fs`, `SageFs.Core/Features/TestRunReceipt.fs`, and
`SageFs.Tests/TestRunReceiptTests.fs`, which pins that the receipt and the cohort landing gate agree
on which tests did not pass.
Reopen it if: the engine can't answer a question an agent needs. Then extend the engine's record,
don't build a runner beside it.

### One floor for "which tests does this change affect"

Two functions answer that question: the live loop (`TestCycleEffects.decideAfterTypeCheck`) and the
cohort landing gate (`AffectedTests.verificationTestSet`). The landing gate never returns an empty set
for a real diff against a real suite. The live loop used to, and an empty set reads as green.

Now they share the floor. The live loop narrows with the symbol graph, coverage, and a file-scope
narrow for body-only edits (every test that reaches a symbol the file declares). If all of that finds
nothing for a compiled `.fs` file, on any trigger, it selects every discovered test and labels the
decision `ConservativeFallback` with the reason. Before, that only happened for a save, or when
symbol names had changed. A keystroke on a cold graph, and any edit to a file whose covering test the
graph had not seen yet, both selected nothing. The run policy still decides whether a keystroke may
run, and says `SuppressedByPolicy` when it does not. Scripts (`.fsx`) are evaluated, not compiled,
so they keep their narrow answer.

The cost is real: a compiled file no test reaches now re-runs the suite on each save, and on each
keystroke for a category set to `OnEveryChange`. I took that over a pane that stays green on a
regression. The way to make it cheaper is a better graph, not a smaller floor.

The keystroke gate that cancels a type-check for "no change" had two holes, also closed. It threw away
all whitespace, but F# reads indentation, so moving a line into or out of an offside block, or
`x -1` to `x - 1`, looked like no edit. It now keeps each line's starting column and a single space
between tokens, and still drops comments, blank lines, trailing spaces and CRLF. And a trivia keystroke
used to cancel the check pending for the real edit typed just before it.

Evidence: `SageFs.Tests/LiveTestingAfterTypeCheckScenarioTests.fs` ("Live testing never reads an empty
selection as green", "Live testing keystroke trivia gate"), driven through
`LiveTestCycleState.handleFcsResult` and `onKeystroke`.
Reopen it if: the suite-per-save cost on unreached files turns out to hurt in practice. Fix the graph.

## One JSON facade over FSharp.SystemTextJson, not a source-generated backend

.NET 11's `System.Text.Json` writes an F# union and .NET 10's throws, which broke `get_session_status` on the net10
tool asset. The cause was 16 separate options objects and about 150 call sites each deciding for themselves, not the
F# converter library, which was already a dependency and works on both runtimes. `SageFs.Json`
(`SageFs.Core/Json.fs`) is the one place that serializes and deserializes: a closed set of named profiles over
FSharp.SystemTextJson, golden-text tests that run in the net11 tier and the net10 tier, and a per-file ratchet in
`JsonCentralizationTests` that only goes down.

Serde.FS.Json (a source-generated, reflection-free backend) was evaluated against SageFs's real shapes and turned
down. It throws on anonymous records (the dominant payload shape: 128 distinct shapes in three files), has no
profiles, writes neither of the two pinned wires, converts floats through `Decimal` so .NET 10 and .NET 11 write
different text, is 6 to 13 times slower to serialize with several times the allocation, needs a .NET 9.0.0 runtime to
build, and has one maintainer and a 1.0 beta. It wins only on the first call in a fresh process, which a warm-up call
removes. The full report is `serde-fs-evaluation-2026-09-30.md` (untracked), and the spike is on the branch
`worktree-agent-af7099f03d7c43d44`.

Evidence: `SageFs.Core/Json.fs`, `SageFs.Tests/JsonTests.fs`, `SageFs.Tests/JsonCentralizationTests.fs`.
Reopen it if: Serde.FS reaches a stable 1.0 with more than one publisher, a generator that runs on the SDK's own
runtime, floats that agree across runtimes, per-call options plus anonymous-record support, and a reader that is not
slower than STJ. The report lists the five conditions.

## A duration has a name and one home, and a ratchet counts the places that don't

A timeout is a decision, so it carries what the wait is for and why that long, and it lives in one place:
`Timeouts` for the product, `TestTimeouts` for the ones a test picks on purpose. The first net10 gate run
showed what a bare number costs: the AppRunner tests gave a web app 500 ms to begin building its host while
production gives 10 s, so a cold ASP.NET host build on a loaded runner read as a console app. The values had
no names, so the gap between test and production was invisible.

`TimeoutLiteralsTests` counts every inline duration (`TimeSpan.From...` with a number, `Task.Delay n`,
`Thread.Sleep n`, `.AddSeconds n`, `WaitForExit n`, a `...Ms = n` binding, a bare `60_000`) outside the central
files, with a budget per file that only goes down. About 780 sites were found on the first count. The first
pass named 62 product sites and woke a dead env var (`SAGEFS_BUILD_TIMEOUT_MINUTES`, read by nothing because
`SessionBuild` had its own `600_000`). The rest followed, product and tests both, and the table is down to one
row: `WorkflowTypes.fs`, which the VS Code contract scripts `#load` on their own and which can't reach `Timeouts`.
About a hundred of the test hits weren't durations at all (byte sizes, seeds, counts), and those got names too.

Evidence: `SageFs.Core/Timeouts.fs`, `SageFs.Tests/TestTimeouts.fs`, `SageFs.Tests/TimeoutLiteralsTests.fs`.
Reopen it if: a constant stops saying why it is that long. Then fix the comment or the value, don't inline it.

## Looking at a value never runs the user's code on the eval thread

The live bindings pane walks every bound value after each eval, and it does that on the one thread the
session evals on. A `Task` is a class, so the walk read `Task.Result`, which waits. Binding a pending Task at
top level (`let t = client.GetAsync url`) hung every eval after it, and nothing looked wrong from outside: the
worker was idle and the session said Ready. A stack dump of the FSI host showed the eval thread inside the walk.

So a Task, a ValueTask and a Lazy are shown by their state, and `Result`, `Exception` and `Lazy.Value` are read
only once there is a value to read. A property is read once per walk, not twice. For any other getter that
blocks, one walker thread takes the bindings in order and the caller waits on each with a deadline
(`Timeouts.liveValueBindingBudget`, 1 s). A binding that misses it shows as unreadable and is not walked again,
the stuck walker is retired, and a fresh one carries on with the next binding. After 16 of those the pass says so
and stops starting new ones. The cost of a blocked getter is one second, once, and the bindings around it still
show. The first version started a thread per binding and cost 80 times more on every eval (24.65 ms against 0.30
ms for 200 bindings), so the guard is one thread per pass (0.40 ms).

Evidence: `SageFs.Core/Features/LiveValueTree.fs`, `SageFs.Tests/LiveValueTreeTests.fs`, and the real-daemon case in
`SageFs.Tests/HttpApiIntegrationTests.fs`.
Reopen it if: a getter that is slow but finite shows up as unreadable often enough to matter. Then the budget is
wrong, or the walk should move off the eval thread entirely and publish when it is ready.

## After a rebuild restart we await the session manager, we don't poll it

`RequestRebuild` used to read `ListSessions` and `GetStreamingTestProxy` every 50 ms, then every 250 ms, for up to
30 s. It is one `AwaitReady` now, raced against the deadline and the rebuild's cancel token (`RebuildReadyWait`). That
one await is enough because `WorkerReady` installs the worker URL before the session can be marked Ready, and the manager
publishes after every step, so Ready already implies a proxy. A test over seeded arrival orders on the real manager
checks it, and its twin (Ready with no `WorkerReady`) is caught.

Evidence: `SageFs/RebuildReadyWait.fs`, `SageFs.Tests/RebuildReadyWaitTests.fs`, `SageFs.Core/SessionManager.fs` (`WorkerReady`, `settleReadyWaiters`).
Reopen it if: Ready is ever marked before the URL is installed. The claim test fails first.

## A getter is evaluated on a thread with a syscall filter, and the filter only stops I/O

When the live-bindings pane evaluates a user's property getter on a click, `ThreadSandbox.run` puts it on a fresh
dedicated thread and installs a seccomp filter on that thread first. `NoNetwork` makes socket, connect, accept, bind,
listen, send* and socketpair fail with EPERM. `NoNetworkNoWritesNoSpawn` adds opens with a write, create, truncate or
append flag (checked on the flags argument), unlink, rename, mkdir, rmdir, truncate, chmod, chown, symlink, link, fork,
vfork, execve, execveat, and clone without CLONE_THREAD, so the runtime can still make threads. clone3 answers ENOSYS,
because its flags sit in a struct the filter can't read, and glibc then falls back to clone. A denied call fails with an
errno and the getter sees a SocketException, an UnauthorizedAccessException or a Win32Exception. The filter never kills
or traps.

It does not stop a spin, a stack overflow, an in-memory effect, a write to a file descriptor that was already open, or a
getter that issues 32-bit compat syscalls on purpose. A thread the work starts inherits the filter, and a task the work
hands to a pool thread does not. There is no deadline in it: the caller owns the deadline and abandons a stuck thread,
and the filter dies with the thread. Linux x86-64 is the only verified platform. Aarch64 is in the type and reported
unverified, other OSes report `NotLinux`, and if the filter can't be installed the work does not run. The caller shows
the reason, so the pane says "no I/O containment here" and why. The BPF is built by a pure function and run through a
small interpreter in the tests against a separately written model.

Evidence: `SageFs.Core/ThreadSandbox.fs`, `SageFs.Tests/ThreadSandboxTests.fs`.
Reopen it if: arm64 gets a machine to verify on, or a getter that does I/O through an fd opened before the filter turns
out to matter.

## A dead FSI host is a state the session reports, and we don't restart it for the user

Found live on 0.6.865: a thread in user code threw, the isolated FSI host aborted (exit 134), and the session stayed
Ready, Healthy, with the same worker pid. Every later eval said "the FSI host exited (code 134)" and the guidance
added "Do NOT reset the session, previous definitions are still valid", which was false: the host held the session,
so everything in it was gone. The reason was only in the worker's own log.

Now the worker watches its host (`FsiHostSession.Ended`, one task that completes once with `Retired` when we disposed
it or `Crashed` with the exit code and the last 40 lines it wrote). The eval actor folds that end through the same
pure `decide` as everything else, stamped with the session generation, so a stop or reset never counts as a crash and
a replaced host's late exit does nothing to its successor. A crash becomes `SessionActivity.HostCrashed`, which the
worker reports as `SessionStatus.HostCrashed`, the daemon keeps as `SessionLifecycleStatus.HostCrashed` (worker handle
and crash, so a reset can still reach the worker), and health reads `Failed` with the crash and the way out. The 5 s
health probe carries the worker's status to the registry, so the dashboard, `list_sessions` and the status payload
agree within one probe. An eval after a crash is refused with `SageFsError.FsiHostCrashed`, a typed case, so nothing
re-guesses advice from its text. The reset tools stay admitted and bring the session back.

What we turned down is restarting the host on our own. It would hide the cause, lose the user's definitions without
saying so, and loop on a program that crashes the host every time. If it comes back it should be a setting that
restarts once, tells the user what was lost, and gives up after a second crash inside a short window.

Evidence: `SageFs.Core/FsiHostClient.fs`, `SageFs.Core/EvalActorDecision.fs`, `SageFs.Simulation/EvalActorSim.fs`
(four twins: a stop reported as a crash, a replaced host's crash, an eval run against a dead host, a crash reported
twice), `SageFs.Tests/FsiHostLostTests.fs`, `SageFs.Tests/HostCrashTests.fs`, and the real-host case in
`SageFs.Tests/HostCrashRecoveryTests.fs`.
Reopen it if: users lose work to host crashes often enough that "tell them and let them reset" costs more than a
restart that says what it dropped.

## Coverage is attributed per test, so tests that touch instrumented code run one at a time

Coverage used to be one bitmap per run batch, handed to every test in it. That is why a gutter could only say "this
line is covered by something" and why selection widened to every test that reaches a function. The probes of a process
are one shared array, so two tests running at once cannot be told apart by reading it. The worker therefore reads and
clears the probes after each test and sends the reading as a coverage frame beside that test's result, and the daemon
stores it against that test alone. To make the reading belong to one test, a run against a project with instrumented
assemblies runs its tests one at a time. A run with no instrumented assembly keeps the parallel path, because there
is nothing to attribute.

What it costs: a suite of slow tests on an instrumented project is serial now, so its wall clock is the sum, not the
longest. The reading itself is cheap, because the worker finds the probe arrays once per loaded assembly set and then
reads and clears them in place. If that cost matters to you, the knob is not a faster read, it is a project with fewer
slow tests in the live set, or running them through an explicit run.

A line's `CoveringTests` list is exact when any test has coverage recorded for the file, and an empty list there
means "no test hit this line", which is the honest answer. When nothing is recorded for the file the old graph-based
list is kept. A reading that hit nothing never replaces a recorded bitmap: after a keystroke eval the tests run
against dynamic code, which the probes cannot see, and an all-false reading would blank the gutter.

Evidence: `SageFs.Host/WorkerHttpTransport.fs` (the attributed stream), `SageFs.Core/Features/CoverageProbes.fs`
(`readAndClear`), `SageFs.Core/Features/TestAnnotations.fs`, `SageFs.Tests/LiveTestingPerTestCoverageTests.fs`,
`SageFs.Tests/LiveTestingCoverageStreamTests.fs` and the real-path journey in
`SageFs.Tests/LiveTestingJourneyTests.fs`.
Reopen it if: a way to tell two overlapping tests apart appears (per-thread or per-async-context probes), or
serial runs turn out to cost real users more than the attribution gives them.

## A line edit selects only the tests that run that line, and every doubt widens it

An edit that moves no symbol name used to select every test that reaches the function. With per-test coverage it can
select only the tests whose own coverage reaches the lines that changed. The rules are all one-directional: any doubt
keeps the wider selection.

  - The buffer's lines are hashed (FNV-1a over the text) and compared with the hashes recorded when the assembly was
    instrumented, so the "changed lines" are measured against the text the coverage describes, not against whatever
    the last keystroke was.
  - An edit that inserted or removed a line shifts every later line number, so it is not narrowed.
  - A changed line with no sequence point has no coverage that speaks for it. A changed line that runs when the module
    initializes runs once per process, in whichever test got there first, so it is not narrowed either.
  - A test with no usable coverage is not known to be unaffected, so it is always included.
  - An empty answer is not an answer: it widens.

The decision says which way it went (`line_coverage_narrowing` against the graph-based precisions), so the status bar
can show why a keystroke ran two tests and not twenty.

Evidence: `CoverageBitmap.narrowByLines` and `LineEdit.between` in `SageFs.Core/Features/LiveTestingTypes.fs`,
`SageFs.Core/Features/LiveTestingCycle.fs` (`decideAfterTypeCheck`), `SageFs.Tests/LiveTestingPerTestCoverageTests.fs`.
Reopen it if: a narrowed run ever misses a test it should have run. That is the failure this design exists to make
impossible, and a case for it should become a refusal rule, not a heuristic.

## Every row says what it ran against, and a real build confirms or contradicts an evaluated verdict

A keystroke's tests run against code the live session evaluated, not against a build. That is the fast path and it
can be wrong in ways a build is not (the session has the whole project loaded, so a file can reach a module that
comes after it in compile order, which the compiler refuses). A row used to look the same either way. Now each row
carries a closed `ResultProvenance`: `Compiled` (a build produced what ran), `Evaluated` (ran against evaluated
code, nothing has confirmed it), `VerifiedByBuild` (evaluated, then run against a real build of the same text, and
the verdicts agree) and `BuildDisagrees` with the reason (`BuildFailed` with the compiler's message, `ResultDiffers`
with both verdicts, `BuildUnanswered`). A disagreeing row's verdict is the build's, because that is what ships.

The confirmation is a pure state machine (`BuildConfirmation.step`): an evaluated run opens it, the editing going
quiet starts one build, a newer buffer abandons a build of older text, every answer carries the generation it was
asked for, and a deadline turns silence into `BuildUnanswered`. It never delays the evaluated result, it never runs
when nothing was evaluated, and a burst of keystrokes costs one build, for the last. The build is the session's own
rebuild, so it is the same build the user would get.

What it costs and where it stops: the rebuild restarts the session's worker, so FSI state and unsaved edits in other
files do not survive it. The confirmation also refuses to confirm text that is not on disk (a build cannot confirm
what only the editor has), so an unsaved buffer stays `Evaluated`, which is honest.

Evidence: `SageFs.Core/Features/BuildConfirmation.fs`, `SageFs.Simulation/BuildConfirmationSim.fs` with its three
twins (a build answer applied to the wrong generation, a build started without waiting for quiet, a failure that says
nothing), `SageFs.Tests/BuildConfirmationSimTests.fs`, `SageFs.Tests/LiveTestingProvenanceTests.fs` and the journeys
in `SageFs.Tests/LiveTestingJourneyTests.fs`.
Reopen it if: restarting the worker for a confirmation loses something users notice. The way out is confirming in a
second worker, which costs a second FSI host's memory.

## Pause and an include or exclude set are enough; memory caps and battery detection are not copied

Visual Studio's Live Unit Testing has playlists, an include and exclude list, a pause on battery and while debugging,
and a memory cap on its test host. What closes the gap with the smallest design is two controls: pause (the session
keeps type-checking and keeps its evaluated code current, and only holds the test runs back, so resuming judges the
code as it is now) and a scope (every test, only the ones matching a pattern, or all but the ones matching) that
automatic runs obey. An explicit run always runs what it names, so a scope never makes a test unrunnable.

The rest needs no equivalent here:
  - Pause on battery. The daemon has no business knowing whether a laptop is plugged in, and an editor can call the
    pause route when it does.
  - Pause while debugging. A debug session is the editor's, and it can pause live testing the same way.
  - A memory cap. The test host is the FSI worker, which has its own limits and its own restart path, and a cap that
    kills it mid-run is worse than a pause the user chose.
  - Playlists. A scope pattern is a playlist that a user can write in one line.

Evidence: `LivePause` and `TestScope` in `SageFs.Core/Features/LiveTestingTypes.fs`, the selection in
`SageFs.Core/Features/LiveTestingCycle.fs`, the routes in `SageFs/McpServer.fs`,
`SageFs.Tests/LiveTestingScaleControlsTests.fs` and the journeys in `SageFs.Tests/LiveTestingJourneyTests.fs`.
Reopen it if: users with large suites ask for named groups that outlive a session.
