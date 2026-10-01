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

### Still to do under this decision

Two functions pick "which tests does this change affect", and they do not share a floor.
The live loop (`TestCycleEffects.decideAfterTypeCheck`) uses the symbol graph, coverage, and on any
trigger (keystroke included) a file-scope narrow for body-only edits, and falls back to the whole suite only for a compiled
file whose dependency graph is empty. The cohort landing gate (`AffectedTests.verificationTestSet`)
uses coverage with a no-empty-escape floor. The code states the residual gap itself: if the
dependency graph has not yet seen the test file that covers a symbol, the live narrow finds nothing,
and only the landing gate's floor catches it. One selection rule with one floor, fail closed, is the
goal. Nothing has been changed here yet.

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

## A lambda in a route list reloads by re-pointing its closure, and only while the closure has room for the change

`get "/" (fun ctx -> ...)` has no name to re-point. The compiler turns the lambda into a closure class and the route
list holds instances of it, which is why an edit to one used to restart: the planner read the whole list as a value the
app had kept a copy of. The class has an `Invoke`, and the instances the app already built still run it, so a save that
only changes lambda bodies detours the old class's `Invoke` to the new one's.

The new `Invoke` is handed an OLD instance and reads what the lambda captured by field offset. That is only sound when
the two classes have the same fields, in the same order, of the same types, so the host checks it and refuses
otherwise. A lambda that starts capturing something, or gains or loses a lambda inside it, restarts and says
`ClosureShapeChanged` with the field it saw. That is the same line Microsoft draws for C# (the captured set has to stay
the same), for the same reason.

Matching old closures to new ones. The compiler names a closure after its binding and the line of the lambda
(`routes@72-3`, counted up in the order it makes them). The planner says which lambdas changed and where they sit,
counted from the declaration's first line, and only says so when nothing OUTSIDE a lambda changed (it blanks every
lambda out of both versions and compares what is left). The host reads those lines off the compiled assembly and off
the code FSI just compiled. Measured on this machine: the name carries the raw line of the code FSI compiled and
ignores the `# n "file"` directive, and the pipeline adds lines of its own (an `open`, a NoInlining attribute above
each function), so the host finds the declaration by its directive line in the code that was evaluated and counts from
there. Two lambdas that share a line cannot be told apart by name, so that edit does not take this path.

A value edited only in its lambdas is emitted as a function (`let routes () : T = ...`). FSI compiles the same closures
and defining a function runs nothing, so the new lambdas exist without the list being built a second time.

Hot reload sessions compile with `--optimize-`. SageFs builds the project with `Optimize=false` and FSI's default is to
optimize. Optimized, a `task { }` is a static state machine where the build made a chain of closures, and a captured
constant is folded into the closure, which changes its fields. Measured: with FSI optimizing, a lambda holding a task or
an async was refused as a different shape, and a lambda capturing a constant looked like it lost its capture. With
`--optimize-` the patch has the same shape as the code it replaces and both patch.

One refusal anywhere in a save stops every detour of it. The closures are matched first (nothing is detoured while
matching), and only if every one fits does anything move, so a restart never leaves the app half updated.

Evidence: `SageFs.Core/Middleware/HotReloadCore.fs` (`planClosureWork`, `applyClosureWork`, `layoutDifference`),
`SageFs.Core/Features/ReloadPlanning.fs` (`lambdaDiff`), and the real-app rows in `SageFs.Tests/HotReloadParityTests.fs`
(`inlineLambda`, `inlineCapture`, `taskLambda`, `asyncLambda`, `heldClosure`, `inlineNewCapture`) on net10.0 and
net11.0. The pure rules are in `SageFs.Tests/HotReloadClosureTests.fs`.
Reopen it if: a closure the compiler makes cannot be matched by name and line (a generated one with no line), or FSI
stops honouring `--optimize-`.
