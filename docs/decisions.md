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

"Fail loud" is a diagnosis, not a fix. If a user's project pins a different version of something than the
host was built with, SageFs adapts where adapting is safe and says so where it isn't. Refusal is the
last-resort safety net before any eval, never the first answer, except where running anyway would give a
wrong answer without telling anyone.

Where that stands, checked against the code and pinned by outcome tests (`ProjectPinOutcomeTests`, host
tier, each a real session on a small offline fixture project):

- A project's newer FSharp.Core, including one that only a NuGet package was compiled against, runs: the
  session gets a host whose FSharp.Core is the project's. FSharp.Core is backward compatible, so the host's
  own compiler service runs on it.
- A project's System.Text.Json, or any shared-framework assembly, pinned newer than the framework's, runs:
  the host is launched with an extra dependency manifest listing the project's copy.
- A project's FSharp.Core that is the same version as the host's but a different build keeps the older
  call-site rewrite for the project's own assembly.
- Two projects in one session that pin different versions of one assembly are refused, with a message naming
  both. One FSI process loads one version of an assembly, so picking either would run the other project against
  the wrong one. FSharp.Core and shared-framework assemblies are exempt, because for those the newest is right.
- A project that carries its own, newer FSharp.Compiler.Service is NOT adapted to. The host's own code is
  compiled against the SDK's, so the project's copy cannot replace it. The session is Degraded and names the
  versions and the way out (pin the SDK that ships that compiler service). A newer System.Text.Json can be
  taken in because the host only uses it through its public API, which is not a promise anyone makes about a
  compiler.
- What the host could not make right (an unreadable runtimeconfig.json, a target framework nobody
  recognises, a FSharp.Core rewrite that failed) is a Degraded session with the reason, on the dashboard, the
  MCP status and `/api/sessions`. It used to be a log line.

The variant-swapping half of the original design (version-matched Fantomas, Cecil and Harmony assemblies) is
gone. The isolated host has none of those libraries in it, so no variant ever existed, and `VariantSelector`
was deleted. `HostAdaptation` is the plan described above.

Not covered, and nothing here claims it: two projects that pin a package at different PACKAGE versions
with the same assembly version (the runtime cannot tell them apart, so nothing says anything), and a
project that overrides a shared-framework assembly with something that is not API compatible.

Evidence: `ProjectPinOutcomeTests`; `SageFs.Core/HostAdaptation.fs`; the isolated FSI host design
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

## The live bindings pane starts in Safe mode, and a click walks the binding again

The pane used to run every public getter of every class value after every eval. That is your code running, on a clock you
didn't choose, and a getter that loops, blocks or does I/O makes the pane the thing that hurts you. So there are three
modes, per session, and the default is the one that runs nothing of yours:

- **Safe** reads fields, and runs a getter only when its compiled body provably does nothing (a field read, a constant,
  straight-line arithmetic). Every other getter is listed with why it was not read, and a small button.
- **Everything** is the old behaviour, under the one-second walk budget. The pane says what it does when you pick it, because
  it runs your getters.
- **Off** does not open a class instance at all. Records, unions, tuples, lists and maps show in every mode.

Safe is the default because the other two have a cost you only pay if you ask. A mode you pick survives a reset and a hard
reset of the session (the worker tells each new session before it first reads it). It does not survive the worker process
restarting, and then `.SageFs/config.fsx` applies again: `{ DirectoryConfig.empty with ValueWalk = WalkEverything }`. The
setting is a closed union, not a string, and the config only ever applies to a worker nobody has chosen a mode for, so it can
never overrule a click on the pane.

A click on a listed getter walks that one binding again, with that one getter run, and the new tree replaces the old one in the
store. I didn't patch a value into the tree because the tree is the answer: if the getter threw, timed out or could not be
contained, the row says so, and a row that says "unknown, and why" is the same shape as every other row. The click only means
something in Safe mode. In Everything the getters already ran, and in Off the class is collapsed, so the host refuses with that
reason and the pane shows it. The result goes through the same store an eval's pull feeds, so the dashboard and the editors'
`live_bindings` event hear about a click the way they hear about an eval, and there is no second path to keep in sync.

What the containment stops is in the entry above, and it is worth saying again where the click is. The getter runs on a dedicated
thread under a 5 second deadline, and on Linux x86-64 under a seccomp filter that stops the network, writes and new processes. It
does not stop a spin, a stack overflow or an in-memory effect. A getter that never returns is given up on at the deadline and its
thread keeps spinning until the host restarts, so the browser journey that clicks one runs last. Everywhere else the getter runs
under the deadline only, and the line under the pane header says "no I/O containment here" with the reason. A getter that mutates
state in memory runs once, on your click, and is not undone.

The pane's header shows the real mode as the pressed one of three buttons, how many rows are not evaluated, and the containment
line of the last click. Rows and header wrap with no breakpoint, so no width can push a control off the pane or onto its neighbour.

Evidence: `SageFs.Core/Features/LiveBindingsPane.fs`, `SageFs.Core/DirectoryConfigTypes.fs` (`ValueWalk`),
`SageFs.FsiHost/FsiProtocol.fs` (`MemberClick.judge`), `SageFs/McpServer.fs` (`mapLiveBindingsRoutes`),
`SageFs.Tests/LiveBindingsPaneTests.fs`, `SageFs.Tests/LiveBindingsPanelTests.fs`, and the real-browser journeys in
`SageFs.Tests/LiveBindingsBrowserTests.fs`.
Reopen it if: the default mode surprises people more than Everything did, or a getter the classifier calls harmless turns out to do
something. The classifier is the thing to fix then, not the default.

## A clicked getter gets stack and loop guards, patched on for the length of the click

The syscall filter doesn't stop a spin or a stack overflow, and an overflow ends the host process. So when a click
runs a getter, `GuardPatcher` walks what that getter can reach and puts two guards into the IL of each method it can
patch. An entry guard calls `RuntimeHelpers.EnsureSufficientExecutionStack`, so a recursion throws a catchable
`InsufficientExecutionStackException` while about 128 KB are still free (the getter thread has 1 MiB). A check goes
in front of every jump back, and it throws `GuardAbortedException` once the evaluator's watchdog has asked that
thread to stop. The watchdog asks at the deadline, and every guarded loop on that thread ends within a few
milliseconds. A `try ... with _ -> ()` inside the loop doesn't help the getter: the stop stays set, so the next check
throws again. The guards come off when the click is over.

This is Harmony on demand, not weave-on-load. It reaches types FSI defined (a dynamic assembly) as well as compiled
DLLs that are already loaded, nothing is copied, and a rebuild needs nothing re-woven. The spike put the entry guard at
about 2 ns a call and the loop check at about 0.01 ns an iteration, which is why only the reachable methods get the
entry guard. The walk is breadth first, 12 calls deep and 64 methods at most, and anything past that is listed as
unguarded on the row. Closures and the implementations behind an abstract or interface call come along.

What the guards do not stop, said on the row as "not guarded: ..." and not hidden:
- A loop in code we don't own. Framework and package methods are never patched (only the getter's own assembly and
  FSI's dynamic assemblies are), so a spin inside a BCL or NuGet method runs until the deadline and the thread is
  abandoned.
- An async or task state machine. A throw at `MoveNext` entry escapes the machine's own try/catch and kills the
  process, so `MoveNext` of an `IAsyncStateMachine` is skipped. A spinning `task` loop is only given up on. (An F#
  `task` in a Debug build is closures, which are guarded; in Release it is a state machine, which is not. The rule
  tested is the state machine.)
- A native wait, `Thread.Sleep` and the like. `Thread.Interrupt` frees a managed wait, nothing frees a native one.
- Regex backtracking. A process-wide `REGEX_DEFAULT_MATCH_TIMEOUT` would catch it, but it would also change every
  regex in the user's own app for the life of the host, and the host's entry point isn't ours to edit, so it is not
  set. If we ever do it, it goes in the host's start-up and the pane says so.
- It is cooperative. A `finally` block runs with the stop still set, so cleanup can be cut short and state can be left
  torn. Running frames are never rewritten, so a frame already inside a method keeps going until its next check.
- Code the JIT inlined into an already compiled caller. A patch on the callee doesn't reach that copy.
- Not tried: generics (skipped, listed as such), C# async, ReadyToRun or trimmed assemblies, Windows and macOS, PDB
  carry-over.

Hot reload is the one real design risk. Patching a method hot reload has detoured replaces the detour with the
original at once, and taking our patch off does not put the detour back. A test reproduces it with raw Harmony
(`Orig()` goes 99 to 42 and stays there). So `detourMethod` now marks the method in `DetourLedger` and takes our patch
off before it detours, under the same gate our patcher takes, and the ledger remembers where each detour points. The
walk never patches a detoured method: it follows the detour to the new body and patches that, and a detour whose body
isn't known is refused with `DetouredByHotReload` on the row. A reload that lands during a click takes the guards off
that method first.

When a getter is abandoned (still running after the deadline and the grace), its guards stay on until its thread ends.
The thread may still be inside guarded code, and its stop is still set, so the first guard it meets throws. Taking the
patches off under it would let it run unchecked. The cap on abandoned getters bounds how many such leases there are,
and a simulation (`SageFs.Simulation/GuardSim.fs`) folds the real registry, cells and lifecycle rule through every
order of "thread ended", "click gave up", "reload" and "second click sharing a helper", with four twins that put the
naive rules back.

Evidence: `SageFs.Core/Features/GuardIl.fs`, `GuardRuntime.fs`, `GuardReachability.fs`, `GuardPatcher.fs`,
`DetourLedger.fs`, `ClickLifecycle.fs`, `MemberEvaluation.fs`; `SageFs.Tests/GuardIlTests.fs` (the weave keeps results
and every jump back has its check), `GuardChildTests.fs` (overflow, spin, catch-all, helper and wait in child
processes, each with a control that runs with guards off and dies or hangs), `GuardCoexistenceTests.fs`,
`GuardSimTests.fs`.
Reopen it if: a getter in a library we don't patch is what people actually click on. Then the answer is a wider
ownership rule, with the cost of patching more methods, not more cleverness in the walk.

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

Evidence: `SageFs.Core/Middleware/HotReloadCore.fs` (`planClosureWork`, `applyClosureWork`, `layoutFit`),
`SageFs.Core/Features/ReloadPlanning.fs` (`lambdaDiff`), and the real-app rows in `SageFs.Tests/HotReloadParityTests.fs`
(`inlineLambda`, `inlineCapture`, `taskLambda`, `asyncLambda`, `heldClosure`, `inlineNewCapture`) on net10.0 and
net11.0. The pure rules are in `SageFs.Tests/HotReloadClosureTests.fs`.
Reopen it if: a closure the compiler makes cannot be matched by name and line (a generated one with no line), or FSI
stops honouring `--optimize-`.

## An instance member reloads by re-pointing it, and the object keeps its fields

Hot reload registered module functions and static members and nothing else, so `member this.Render() = ...` on an
object the app built at startup restarted, and the reason it gave (`the signature of Greeter changed`) was wrong. A
member is a method like any other: the object the app holds calls it, and detouring the old method to the new one
reaches that object. The edit lands, and the object is the same one, so its fields (a counter, a cache) carry on. The
`instanceState` row reads `A#1` before the save and `B#2` after it.

Same condition as for closures, same reason: the new member is handed an old object and reads fields by offset, so the
new type has to have the old type's fields. A member that starts using a constructor argument gives the type a field
the object does not have, and that restarts and says `InstanceLayoutChanged` with the field it saw.

What counts as a member. Only what a class declares itself: not the `ToString` and `Equals` every type inherits, not
the members the compiler writes for a record or union, not a struct's (`this` is a byref there) and not a generic type's.
An instance property's getter is a plain member. A module's `get_x` and `set_x` are one mutable binding's pair, and the
planner tears them down together or not at all, so only a module's accessors get that treatment.

The held-copy record is keyed by name for a module function and by type and name for a member, so a `Render` on one
class and a `Render` on another are not the same entry.

Evidence: `SageFs.Tests/HotReloadParityTests.fs` rows `instance`, `instanceState`, `instanceNewField` on net10.0 and
net11.0; `SageFs.Tests/HotReloadClosureTests.fs` for what is registered.
Reopen it if: a member needs to be added to a type (Microsoft's mechanism supports it), or a virtual member's dispatch
turns out to differ between the old type and the new.
## A save that adds, removes or re-signs a declaration lands without a restart

Adding a type or a value, removing anything that isn't startup code, and changing a function's signature all restarted.
The planner read each as "something the running build never had, or lost, or shaped differently", and a restart was
the safe answer. It is safe, and it is also wrong for most of what a person does in an afternoon: add a helper and call
it, delete one, add a parameter.

What I did instead is say what is true. A declaration the running build never had needs no compiled original: it is
defined in FSI, and the saved code that uses it is patched to call it, in the same save. A function whose signature
changed is the same thing to the running app: a new method. The old one stays for whatever still holds it, and the
callers saved with it are moved onto the new one. That is what Microsoft's mechanism does as well (the build forces
every caller of a changed signature into the same edit). A removal leaves the old declaration in the process, and what
stopped using it was saved in the same breath, so there is nothing to re-point and nothing to restart for.

What still restarts: the entry point, a bare expression that runs at startup, a module alias, a change to a type's
shape, and everything the earlier cases refuse.

What the save says is counted honestly. A new declaration is "applied", not "seen running": it has no probe, because
nothing runs it until a caller does. So the caller's probe is what makes the save Patched. A save that only adds
something nothing calls yet ends as "not confirmed: the new code has not run", which is what is true of it.

One thing to know. A caller in ANOTHER file keeps calling the old method until you save that file as well. The build
would not pass until you did, so the window is short, but in it the old behaviour is what runs.

A removal on its own, with nothing else changed, reports "no declaration change". It changed nothing in the running
process, which is true, though it isn't the whole story.

Evidence: `SageFs.Tests/HotReloadParityTests.fs` rows `addedFunction`, `addedType`, `addedValue`, `removed`, `signature`
on net10.0 and net11.0; the planner rules in `SageFs.Tests/ReloadPlanningTests.fs`.
Reopen it if: callers in other files turn out to bite (a cross-file check of who calls a changed signature would let it
restart instead), or a removed declaration's old copy turns out to matter.

## A generic function restarts and says so, because a detour of a generic function reaches only part of it

A generic function is compiled once for every instantiation that runs, and the runtime keeps one body for all reference
types and one for each value type. Measured against the Harmony we ship: detouring the open definition throws (and the
process aborted on the next call), and detouring a closed instantiation changes that instantiation and nothing else. A
call with a type that has not run yet is compiled from the old IL afterwards. So a patch of a generic function would be
right for the calls that already happened and wrong for one that comes later, which is the kind of "Patched" this tool
exists not to say. Microsoft's mechanism edits the method in place and does not have this problem; we do.

The row that proves it (`generic`) saves an edit to `genericTag<'T>` that two call sites use with a string and an int.
Both instantiations had run, so detouring them would have looked right. A third (a float) came out with the old body.

Now a generic function is registered so a save can name it, never detoured, and a save that edits one that the app holds
is refused with `GenericFunction`, which names the function. The refusal stops every detour of the save and the whole-file
fallback is skipped, so the running app is left as it was. The remedy says what works: a function that is not generic is
re-pointed, so if it only has to work for one type, annotate its arguments with it.

Evidence: `SageFs.Tests/HotReloadParityTests.fs` row `generic` on net10.0 and net11.0, and the spike on a bare session
in this change's commit message.
Reopen it if: a way to detour every instantiation, present and future, shows up (a shared canonical body for reference
types would cover half of it, and half is not a claim worth making).

## The debugger row downloads a pinned debugger and checks its hash, because the row has to be real

"Does hot reload work while a debugger is attached" had no test, and Microsoft's answer for F# is "no". A faked row (a
flag that says a debugger is attached, a mock) would be worse than none. The row attaches Samsung's netcoredbg to the
FSI host, which is the process the route table lives in, asks that process whether a debugger is attached (and the twin
asks the same of an unattached one and gets false), then does real saves. Managed attach on Linux goes through the
runtime's own pipes, so it works without ptrace under the default Yama setting, and it works against .NET 10 and .NET 11.

netcoredbg is MIT licensed and has a Linux x64 release. The Microsoft debugger (vsdbg) is licensed for use with Microsoft
products only, so it is not an option. The release is pinned by URL and SHA-256, downloaded once into the fixture's
`.runs` folder (which is git-ignored), and refused if the hash differs. A machine that is not Linux x64, or cannot reach
GitHub the first time, fails the row and says why. It does not skip it.

What it proves is narrow, and the doc says so: a save lands and runs in a process a debugger is attached to. It does not
prove stepping, breakpoints in patched code, or an edit to a method the debugger is stopped in. Nothing sets a
breakpoint.

Evidence: `SageFs.Tests/HotReloadDebuggerTests.fs`.
Reopen it if: breakpoints in patched code turn out to matter to users (that is a separate row, and a harder one: a
detour rewrites the first bytes of the code a breakpoint may sit in), or CI cannot reach GitHub (cache the archive on
the runner).

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
