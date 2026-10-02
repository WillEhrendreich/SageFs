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

## A generic function is re-pointed in every body the runtime compiled for it, and restarts, naming why, when the program can make one nobody can list

This entry replaces an earlier one that said a generic function always restarts, because "a detour reaches only the
instantiations that have run". Will asked why Visual Studio can do generics and we can't. The earlier measurement was
half right, and the wrong half is the interesting one. Everything below was measured on net10.0.12 and net11.0.0-rc.1,
linux-x64, tiering off (the host's default), with the Harmony we ship, in a throwaway console project. The rows in
`SageFs.Tests/HotReloadParityTests.fs` and the tests in `GenericReloadTests.fs` reproduce every claim that matters.

**What the runtime does with a generic method.** With `DOTNET_JitStdOutFile` and `DOTNET_JitDisasmSummary=1`, one
generic function used with an int, a float, a struct and three reference types (a string and two records) compiled
four bodies: `wrap[int]` (101 bytes of code), `wrap[double]` (90), `wrap[struct]` (69) and ONE `wrap[System.__Canon]`
(135) for all three reference types. A function over two type arguments gets one shared body per pattern of value
types: `(*, int)`, `(*, float)`, `(int, *)` and `(*, *)` are four bodies, and detouring three of them left the fourth
on the old code.

**Who calls what.** A plain caller of a reference-type instantiation loads the exact instantiation (the method
descriptor) into the first argument register and calls the shared body directly: `mov rdi, <exact method>; mov rsi,
arg; call [wrap[System.__Canon]]`. The register differs between a call with a string and a call with a record. A
shared generic caller (`outer[__Canon]`) takes its own hidden argument, looks `wrap<T>` up in its dictionary
(`CORINFO_HELP_RUNTIMEHANDLE_METHOD`) and calls the same shared body. A value-type instantiation is `call
[wrap[int]]`, its own code, no hidden argument. A delegate and a reflection call end in the same code. So the shared
body is ONE piece of code that every reference-type caller reaches, and nothing reaches an instantiation by a path
that skips it.

**What a detour does.**

- A closed value-type instantiation: detouring `wrap<int>` moved the plain caller, the generic caller `outer<int>` and a
  delegate over it, and nothing else. A float that first ran after the detour got the old body.
- A closed reference-type instantiation, the way the earlier measurement did it: `wrap<string>` onto `wrap2<string>`
  moved EVERY reference type, including `wrap<Rec>` first used after, because they are one body. That is the half the
  earlier entry got wrong. But the new body runs with the type argument of the method it was pointed at: a body that
  prints `typeof<'T>.Name` printed `String:B` for a string, `String:B` for a record and `String:B` for another record.
  MonoMod's own source says it ("your hook will receive calls for all reference type-based implementations").
- `PatchTools.DetourMethod` wraps the replacement in a glue method that drops the hidden argument, so a stub that takes
  the exact instantiation fails to compile (`InvalidProgramException`).
- The open definition: `PrepareMethod` throws `ArgumentException`, and MonoMod throws `NotSupportedException` out of
  `MMReflectionImporter.ImportGenericParameter`. It is an exception, not a native fault. The exit 134 of the earlier
  measurement is what .NET does with an unhandled exception in a bare process (my spike, which did not catch it,
  aborted with the same exit code and that exception as its last output); `detourMethod` already catches it and reports
  `Failed`. A method closed over `__Canon` itself throws `InvalidProgramException`.
- Tiering on: the detour of a body is lost after tier-up (it lasted about two rounds of 60 calls and a 400 ms pause).
  That is the exposure every detour here has, and the host runs with tiering off.

**What works.** A native detour (MonoMod's `PlatformTriple.CreateNativeDetour`, reached by reflection the way
`detourMethod` reaches `PatchTools`) from the shared body to a stub with the shared body's own signature, hidden
argument included. The hidden argument says which instantiation the call is for, and where it lives depends on where
the method lives:

| the method | the hidden argument |
|---|---|
| generic, in an ordinary type | the exact method (`MethodBase.GetMethodFromHandle(RuntimeMethodHandle.FromIntPtr md)` gives it, and its generic arguments are the instantiation) |
| an instance member of a generic class | none: the type arguments are the object's |
| a static member of a generic class | the exact class (a MethodTable, `Type.GetTypeFromHandle`) |
| a generic instance method of a generic class | the exact method, which the runtime only turns back into a method given the exact class, and the object has it |
| a static generic method of a generic class | the exact method, and nothing at the call names its class: refused when a reference type is among its arguments (a value-type instantiation has its own code and is detoured on its own) |

The stub finds the exact instantiation, finds the new copy's entry for the same type arguments and calls it with
`calli`. With it, the same `typeof<'T>.Name` body printed `String:B`, `Rec:B`, `Rec2:B` and `Int32:A` (a value type
nobody patched): each reference type got its own type argument, the ones that had not run included.

**The design.** A save re-points a generic function body by body: one detour for each value-type instantiation the
program can reach (a stub with a probe), and one native detour for each shared body (a stub with a probe). Which bodies
exist is read from the program, not guessed: `GenericReload.reach` decodes the IL of every assembly that can name the
function (the declaring one, the ones that refer to it, the project's own, FSI's) and finds the method references
(`call`, `callvirt`, `newobj`, `ldftn`, `ldvirtftn`, `ldtoken`). It follows a generic caller with the type arguments it
is reached with (`Outer<float>` reaches `Inner<float>`) and a closed generic type through all its members, since a
virtual or interface call reaches a member no IL names. It took 23 ms for the parity fixture's assembly. Planned
first, applied after: one refusal anywhere stops every detour of the save, as for everything else.

It says it cannot list the instantiations, and the save restarts with the new cause `GenericInstantiationsUnknown`,
when: the program calls `MethodInfo.MakeGenericMethod` anywhere in those assemblies (it can make an instantiation no
code names), or `Type.MakeGenericType` while a generic type or method reaches the function (or the function is a member
of a generic type), a method could not be decoded, more than 200000 method contexts were reached, or the shape is one
the stub cannot take: a byref parameter, a struct returned through a buffer (it comes back before the hidden argument), a
generic struct's members, a static generic method of a generic class used with a reference type. The check is deliberately coarse: one
`MakeGenericMethod` call in a program refuses every generic edit in it. A finer rule (the call's `MethodInfo` comes from
a `ldstr` of this function's name) would restart less and prove less.

**The probes.** Each body gets a probe under the function's name, and the function is `Patched` once the host has seen
any of its new bodies run: the planner matches a probe to the declaration the user edited by name, and says the
declaration ran when one of its probes has been entered, which is how the several closures of one declaration already
work. So `Patched` for a generic function means a new body was seen running, and every body was patched in the same
save. It does not mean every instantiation was exercised. A body nothing runs stays unseen without the save saying so.
That is the weakest claim in this design, and tightening it (every probe of a declaration entered, or superseded)
means changing what the closures' confirmation means too.

**What it does not do.**

- Reflection that makes generic methods or types, as above: it restarts and says so. A delegate bound straight to a
  generic method is one: F# wraps a function used as a value in a closure (that row is `genericClosure` and it
  patches), so a direct delegate takes `CreateDelegate` and `MakeGenericMethod`, which is why `genericDelegate` is a
  restart.
- A real fix for those would not be a detour. Roslyn changes the method definition in the runtime's metadata, so the
  runtime re-JITs every instantiation, the ones that exist and the ones to come, and no list is needed: that is
  `MetadataUpdater.ApplyUpdate` with a delta, which `dotnet/fsharp#19941` is building the compiler side of. A profiler's
  `RequestReJIT` and `SetILFunctionBody` do the same job from the other side (a native profiler the host would load, set
  up at process start, which SageFs does start). Neither is planned for the detour route. An app started with
  `run_app` is the exception, and it came after this entry was written: the metadata-delta route in
  [the entry at the bottom of this page](#a-run_app-save-is-patched-by-a-metadata-delta-in-the-worker-and-every-refusal-is-a-restart-that-names-why)
  writes the delta itself and takes a generic body edit with no list of instantiations (row `generic` in
  `RunAppDeltaTests.fs`). I haven't run the reflection rows (`genericReflection`, `genericDelegate`) through it.

Evidence: `SageFs.Tests/HotReloadParityTests.fs` rows `generic`, `genericRef`, `genericKind` (a body that reads its own
type argument), `genericLate` (a float and a struct first compiled after the save), `genericNested`, `genericClosure`,
`genericInstanceMethod`, `genericStaticMethod`, `genericTypeInstance`, `genericTypeStatic` and `genericMethodOnType`, which
end `Patched` on net10.0 and net11.0, and `genericReflection` and `genericDelegate`, which restart with
`GenericInstantiationsUnknown`; `SageFs.Tests/GenericReloadTests.fs` for the scan (emitted IL), the grouping and the
real detours, including the ones that prove the list has to be complete (an unlisted value type keeps the old body).
Reopen it if: a program that uses `MakeGenericMethod` for something unrelated turns out to be common (the coarse rule
would then cost real restarts, and the finer one is the next step), or `Patched` for a generic function that was seen
running in one instantiation and not in another bites someone.

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

## A save that finds the session busy is evaluated when the run ends, and "confirmed" means no build is in flight

The provenance journey (`LT provenance: an evaluated verdict is confirmed by a real build`) failed about half the time
on the release gate, and the save-to-green latency test failed the same way once. The journey wrote a file and waited
for the verdict to show up as `Evaluated`. Sometimes it never did. Running the `--integration-lt` tier in a loop with
every frame the feed saw printed on a timeout showed two things.

The first is a product bug. After a type-check passes, the decision looks at the session, finds a run in flight, and
emits nothing. The run it owes for later was only kept when the effect was a `RequestRebuild`, and the live path
doesn't emit that any more (it evaluates the buffer instead, `redirectToEvalBuffer`). So the owed run was thrown away,
and a save is the last event of an edit, so nothing asked again. The verdict for that save never arrived, until the
next edit. Any run counts as busy, including the run that a confirmation's own worker restart causes. Now the queue
keeps the evaluation (`QueuedMeans.EvaluateThenRun`, with the text that was checked) and `promoteQueuedRebuild` starts
it when the busy run ends, if that text is still the newest. A rebuild is still queued as `RebuildThenRun` for the
compile-failure path. The RED proof is `SageFs.Tests/LiveTestRebuildCycleTests.fs` (commit 92cc909c).

The second is the journey waiting for the wrong thing. It waited for "no row is `Evaluated`" before it started. A
confirmation's build restarts the worker, and the run that restart causes marks every row `Compiled` while the build
is still going, so the rows said "confirmed" with a build in flight, and the journey edited a file in the middle of the
restart. The status now carries `Confirmation` (`idle`, `quiet`, `building`, `running_built`), the machine's own
phase, and the journeys wait for `idle`, asking again after each verdict or summary frame.

That left one hole, an edit that arrives while a confirmation's worker restart is under way. I didn't fix it then,
because I hadn't seen it fail once the journeys stopped overlapping the restart. It was real, and the next entry is
the fix.

Evidence: `SageFs.Core/Features/LiveTestingCycle.fs` (`QueuedMeans`, `promoteQueuedRebuild`),
`SageFs.Core/Features/BuildConfirmation.fs` (`ConfirmationPhase.toWireValue`), `SageFs/LiveTestStatusView.fs`,
`SageFs.Tests/LiveTestingProvenanceTests.fs`, `SageFs.Tests/LtStream.fs` (`awaitConfirmed`, `describeHistory`).
Reopen it if: a user reports a save with no verdict while a confirmation is building.

## A check or an eval asks a worker only when one is Ready, and waits for one while it is being replaced

The hole I left open in the entry above was real. Typing while a confirmation's build replaces the worker lost the
edit, and I could show it through the real daemon: write a valid file, wait for its verdict to be `Evaluated`, wait
for the status to say the confirmation is `building` and the session `Restarting`, then save a second edit that turns
one test red. The second verdict never came. I gave it three and a half minutes and the daemon logged nothing for it.

Three things were wrong, and they sit on one path.

1. The old worker keeps serving while the project builds, then `SessionManager` parks it and spawns the replacement
   with no worker URL registered. `withSession` answered a check or an eval that found no proxy with an `EvalFailed`
   and nothing else. A save is the last event of an edit, so nothing asked again.
2. After the replacement's `WorkerReady` commits, its proxy exists while the session is still `Starting`. A worker
   that isn't Active answers a type-check with no diagnostics and no symbols (`AppState`, the type-check query), and
   the daemon read that as a clean check. That can lift a compile block or select nothing.
3. A worker retired under a call throws, and that came back as `Cancelled` with nothing asking again.

I didn't see the false `blocked_by_compile_errors` the last agent saw in one failing run, and I can't say which of
these produced it. A worker answering for a project it hasn't loaded yet is the likeliest, which is why the fix
doesn't trust any answer from a worker that wasn't Ready.

The fix is a pure decision, `LiveCheckPump.step`, with the daemon side in `SageFs/LiveCheckRelay.fs`.

- A request reads what the session manager says about the worker (`viewOf`: Ready or Evaluating with a proxy is
  Serving that pid, Starting, Restarting and Building are Arriving even when a proxy exists, Faulted, crashed and
  stopped are Gone with the reason). If one is Serving, it is asked. If one is Arriving, the request waits on the
  manager's own `AwaitReady`, which answers the instant the session is Ready, so nothing polls. If none is coming,
  the request ends unanswered and says why.
- An answer is believed only when the worker that gave it is still the Ready worker that was asked. A call that came
  back from a retired worker, or while the session isn't Ready, counts for nothing and the request is asked again.
  Each redo needs the manager to have changed its mind, so it can't spin. A worker the manager calls Ready that
  doesn't answer ends the request unanswered, because asking it again would be a loop with nothing to end it.
- A newer type-check for a file replaces an older one that wasn't answered, so only the newest text gets a verdict
  and the answer for text that was typed over is never reported. Evals are the opposite, each is owed its run, in
  order. One relay per session, kind and file.
- Nothing unanswered is ever reported as an error in the user's code. It is a cancelled check, as before, with the
  reason in the daemon log.
- The wait is bounded by `ReadyDeadline` (30 s), the bound a rebuild already gives the same wait. A replacement that
  isn't Ready by then is a failed rebuild, and a check waiting longer would be waiting for a worker the rebuild gave
  up on. After the deadline the check ends cancelled and the next edit asks fresh.

The proof is `SageFs.Simulation/LiveCheckPumpSim.fs`: a seeded world of edits, a worker that stops, spawns, warms or
faults, calls that come back from whichever worker is there by then, and waits that end in any order. The invariants
are newest-edit-always-judged, no-false-blocked, no-false-clear, no-stale-apply, asks-only-a-ready-worker,
single-flight, every-request-ends-exactly-once, every-request-runs-in-order, stale-waits-do-nothing and
everything-resolves. Five twins each put one bug back (drop what can't be asked, ask any proxy, trust an answer
without looking again, apply an older answer, ask while a call is in flight) and each one is caught. Two journeys in
the `--integration-lt` tier type the second edit while the session says Restarting under a building confirmation
and while the confirmation runs its tests, and expect the newest verdict and no summary that says blocked. The
first one failed before the fix and passes after. I didn't see the second one fail on the unfixed code, so it is a
pin: with the worker Ready the old path was fine.

What I haven't done. A check that waits out the 30 s is dropped, not retried when the worker comes later. The
confirming build still restarts the worker, and everything in FSI state goes with it (the entry above). And the
other effects that read a proxy (`RunAffectedTests`, discovery) still drop a request that finds none. The check and
the eval are the two that decide whether an edit is judged, so those are the ones I moved.

One thing I found on the way and left alone. Line narrowing measures a save against the text the last build compiled,
not against the last text that was evaluated. In the second journey the first edit's build was the baseline when the
journey put the original file back, so the restore looked like a change to one line, selected one test, and left the
test the second edit had turned red as it was (the daemon's own decision names the one test). The journeys now wait
for the second text's confirmation, which moves the baseline. A user who types, waits for a build, makes a red edit
and puts it back within the quiet window can meet the same thing. It's a separate fix in the line-narrowing
baseline, and I haven't made it.

Evidence: `SageFs.Core/Features/LiveCheckPump.fs`, `SageFs/LiveCheckRelay.fs`, `SageFs/SageFsEffectHandler.fs`
(`relayFor`, `RequestFcsTypeCheck`, `EvalBufferThenRunAffected`), `SageFs.Simulation/LiveCheckPumpSim.fs`,
`SageFs.Tests/LiveCheckPumpSimTests.fs`, `SageFs.Tests/LiveCheckRelayTests.fs`, `SageFs.Tests/LiveTestingJourneyTests.fs`
(`editedWhileConfirmationIsIn`).
Reopen it if: a user reports a save with no verdict after the worker restarted, or the daemon log shows
`no worker was Ready in time` for a check on a project whose warmup is slower than 30 s.

## A metadata delta is computed against the module the worker actually loaded, looking through its coverage probes

An app started with `run_app` restarts on every function save, about six seconds. The runtime can do better: a metadata
delta applied with `MetadataUpdater.ApplyUpdate` changes method bodies of the loaded assembly in place, for every
instantiation of a generic method (past and future), for the closure objects the app holds and for the objects an
instance member runs on. dotnet/fsharp#19941 (Nat Elkins, a community contribution, unmerged) writes those deltas from
inside the compiler behind a flag no released SDK has. So this is a SageFs-owned emitter that reads two finished
assemblies, the one the process loaded and a fresh build of the project, and writes the delta. No code is taken from the
PR. When this entry was written nothing in the product called it; [the next entry](#a-run_app-save-is-patched-by-a-metadata-delta-in-the-worker-and-every-refusal-is-a-restart-that-names-why) is the part that does.

Which file does the worker load? Not the compiler's output. `createActorImmediate` shadow-copies every project DLL,
instruments the COPY in place with Cecil (a probe in front of every sequence point, 26,931 of them on SageFs.Core), and
hands the shadow paths on as the session's project targets, which is what `AppRunner.resolveProjectAssembly` loads with
`Assembly.LoadFrom`. So the metadata the runtime holds is a Cecil-written module whose every method body differs from
what the compiler wrote by its probes, and whose rows are numbered Cecil's way.

Three ways to deal with that. I took the third.

- Instrument the new build the same way before diffing. Probe slots are numbered across the whole assembly in sequence
  point order, so an edit that adds a sequence point renumbers every probe after it, and every method after it would read
  as changed. I did not build this, that is from reading `collectSequencePoints`.
- Do not instrument a module that will be hot reloaded. It costs the live-testing coverage of exactly the apps people
  watch, and instrumentation runs before anything knows the app will be hot reloaded.
- Diff against what is loaded, and look through the probes. A body is compared as instructions with token operands
  replaced by the text of what they name, branch targets as instruction indices and short forms folded to long ones, so
  a Cecil rewrite, a few bytes more or fewer, or a renumbered row is not a change. `StripCoverageProbes` drops each
  `ldc.i4 slot; call __SageFsCoverage::Hit` pair and sends a branch that landed on a probe to the instruction the probe
  was in front of.

What that costs: a method the delta patches runs without its probes until the next rebuild, so its coverage is gone for
that long. What live testing does with that is in the next entry: nothing reads coverage in the process the delta goes into.

How I know it holds. The fixture's `Handlers.fs` is built twice with the real F# compiler (Debug), the second with the
edits the `run_app` rows make and two lines added above everything. The first is run as the compiler wrote it and as the
real instrumenter rewrote it. In a child process started with `DOTNET_MODIFIABLE_ASSEMBLIES=debug` the generic function
gives the new text at an int, a string, a record and a double that had not run, the closure the app holds gives the new
text, the instance member carries on counting (A#2 then B#3), the task body and an added method give the new text
(`RealFSharpDeltaTests`, host tier, net11 here). A test pins the symbol against the real `CoverageInstrumenter`: it instruments an assembly and
counts the calls to `Hit` (all of them found with the probes kept, none left once they are looked through). Another runs
the loop for real: a program is compiled with Cecil, instrumented by the real instrumenter, loaded in a child process
started with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, patched by the delta, and every answer has to be the one an
interpreter gives for the next version. 16 seeds in the gate, 192 in a soak, all green.

What it carries: a new body for any method, with every token re-expressed in the loaded module's numbering (an existing
row when it names the same thing, a new AssemblyRef, TypeRef, TypeSpec, MemberRef, MethodSpec or StandAloneSig row when
it does not), string literals, exception regions, and a method added to a type the process already runs. Deltas chain:
the second sees the heaps and rows the first added. 1, 2 and 3 generations are checked: 24, 12 and 8 seeds in the gate,
288, 144 and 96 in a soak (`SAGEFS_DELTA_SCALE=12`).

What it refuses, each with its own cause (`RudeCause`, 15 of them): a type or method removed, a type added (a new lambda
adds a closure class, so that restarts), a field added or changed, a type's base, interfaces or flags changed, a property
or event changed, a custom attribute changed, a signature changed (named separately for a virtual member), method flags
or parameters changed, a virtual, constructor or generic method added, a changed static constructor or startup-class
method (a delta replaces a body and never runs it again, so a module-level value would read as patched and keep its old
value), closures that share a name changing in number, and a body it cannot read (`calli`, an unknown opcode or
signature element). The F# compiler puts line numbers in names, so a line added above anything renames it. Three kinds
turned up and the number is folded out of all three: closure classes (`f@18-3`), the static fields of a file's startup
class (`counter@49`), and the helper methods a Debug build writes for a task (`<Bind>__debug@59`). Names that then share
a text inside one type are told apart by their order, and a different number of them is a refusal. The last two the
Cecil tests could not have found. A real build of the run_app fixture did, the first time it was run with two lines
added above everything: the startup class's fields "changed", its constructor "changed", and a helper "was removed".

Things the soak found. A Cecil-written module with no string literal has no #US heap, and a delta adding the first
literal reads as "no string associated with token". That is a refusal now, and the test programs keep one literal. After
an update `GetMethods` did not list a method the delta added until the metadata-update handlers ran, which is the
contract `dotnet watch` keeps, so the apply side runs them. Told "everything" the runtime's `ClearCache` took 405 ms of
the 410; told the types the delta touched it takes 0.4 ms, so the payload carries the method tokens it writes.

Limits I know of and have not closed. An unchanged method with a `calli` refuses the whole save, because the body is
read before it is compared. Custom attribute changes are compared as text of constructor and value, except
`CompilationMappingAttribute`, which F# numbers by position. A method that is running when the update lands keeps its old
frames, from the docs, not tested. A method a Harmony detour already points at, and the debugger-attached
`NotSupportedException`, I read in the runtime source and did not run. Linux x64 only.

What it costs, measured on this machine (16 logical cores, Linux, .NET 11.0.0-rc.1.26425.128, a machine other agents
were building on) with SageFs.Core.dll as the subject: 9.4 MB, 46,153 methods, three bodies changed. n = 5 passes in one
child process, from nothing each time (the first has the JIT in it), then the next save once:

| | plain baseline | instrumented baseline, probes looked through |
|---|---|---|
| read both builds | 40 to 109 ms | 42 to 96 ms |
| diff and write, first pass | 2.0 s | 2.8 s |
| diff and write, passes 2 to 5 | 0.83 to 0.98 s | 1.25 to 1.38 s |
| the save after, previous build already read | 0.61 s | 0.73 s |
| `ApplyUpdate` | 5.3 ms | 7.1 ms |
| handlers after it | 7.6 ms | 9.9 ms |
| delta | 432 bytes of metadata, 42 of IL | the same |

Those are one run. The same test a few minutes later, after the field and method folding was added, gave 0.62 to 0.72 s
warm for the plain baseline on a quiet moment and 1.05 to 1.10 s on a busy one, so read them as ranges. Most of that is
the diff reading 46k methods. A project one tenth the size is about a tenth of it. Two things bring it
down further and are not done: keeping a fingerprint of each body of the previous build so a save reads only the new
one, and checking only the types whose source file changed. The instrumented column cannot use the fast path for bodies
that are byte-identical, because every instrumented body differs by its probes, so the first save against the worker's
real baseline is the slow one.

Evidence: `SageFs.Core/Features/MetadataDelta/` (`PeImage.fs`, `IlCanon.fs`, `MethodDiff.fs`, `DeltaWriter.fs`,
`DeltaApply.fs`, `RudeCause.fs`), `SageFs.Tests/MetadataDeltaTests.fs` with `DeltaProgram.fs`, `DeltaChild.fs` and
`DeltaInstrumentation.fs` (the numbers above print as `DELTA-BENCH` lines in a run's output),
`SageFs.Tests/RealFSharpDeltaTests.fs`, and
`SageFs.Tests/RunAppDeltaTests.fs`, the rows a `run_app` app has to pass once the planner uses this.
Reopen it if: instrumentation stops being applied to the shadow copy, or live testing needs a patched method's
coverage to survive the patch.

## A run_app save is patched by a metadata delta in the worker, and every refusal is a restart that names why

The emitter above was a library. This is the part that uses it: a save to an app SageFs started with `run_app` is built,
diffed against what the worker loaded, and applied, in the same process, with the state the app holds still in it. The
prior art for the whole route is dotnet/fsharp#19941 (Nat Elkins, a community contribution, unmerged); nothing was
ported, so there is no THIRD-PARTY-NOTICES entry.

The route lives in the worker (`SageFs.Host/RunAppDelta.fs`) because the app does, and takes its baseline when `run_app`
starts the app: the module the process loaded, read from the file it was loaded from and checked against the module in
memory. One chain per module, so a second run of the app in the same process carries on, and a restart (which replaces
the process) starts a new one. A save runs one at a time: precheck, `dotnet build` (the session's own, so it is the same
build a restart pays), prepare, decide, apply, settle. What each step decides is `DeltaSession`, pure, and the worker calls
the functions the simulation folds.

Choices, and what each cost:

- **The verdict is a case of the existing outcome, not a new surface.** `ReloadOutcome.ByMetadataDelta` carries
  `Pending | Patched | NeverEntered`, which are the detour's three stages, so the wire `outcome` and `type` keep their
  names and every client that reads them keeps working. How the patch got there is a new additive field, `mechanism`
  (`detour`, `metadata-delta`, empty when the verdict is not a patch), on the worker's reload payload and on the daemon's
  `lastReload`, as a closed set (`PatchMechanism`, one function spells it, an unknown spelling reads as none). The
  alternative was to read it from the message, which is what the first version of the rows did, and it is the thing a
  client must never have to do. It costs the clients a field they don't read yet: VS Code and the dashboard don't, and
  sagefs.nvim is a separate repo that needs telling.
- **A refusal is a `RestartReason`.** `RudeEdit of RudeCause` for an edit the compiled shapes show a delta can't carry
  (the case name is the wire `case`, the declaration is in the message), `MetadataDeltaUnavailable of why` for a process
  that can't take a delta right now: no baseline, the chain was started for another module, a debugger attached, a
  Harmony patch on a method the delta rewrites, the runtime missing a capability, an earlier delta that did not apply.
  Everything the source planner already sent to a restart is still sent there first, with its more specific reason
  (a type's shape, startup code, mutable state): `PatchRoute.choose` sends only a non-empty `PatchFunctions` to the build.
  So two rows restart for the planner's reason and one for the emitter's, and the rows say which.
- **"Patched" still waits for the new body to run.** Each updated method gets a call to `EntryProbes.EntryHooks.Enter` as
  its first instruction, written into the body. A detour has a stub to carry a probe; a delta replaces the body in place,
  so the probe is part of it. The 14-byte prefix shifts every exception region and every filter offset by its length, and
  the child-process proof runs probed bodies against the interpreter and reads every probe of a method that ran back as
  entered. A method the delta only adds has no probe: nothing runs it until a caller does, and the caller's probe shows
  the patch live. The wait is the process's own registry, not the host agent's, because the app is here.
- **Saves after a failure restart, a no-change save included.** The runtime cannot take a delta back, and a failed
  `ApplyUpdate` may have taken part of it. So a refused delta makes the route unusable for that process, and every save
  after it restarts until the daemon replaces the process. The same question is asked again at the moment of the call,
  against the standing as it is then, so two saves prepared from one generation cannot both land even if the lock around
  a save is ever loosened. The simulation has a twin for each of these and every twin is caught.
- **Whether the process may be edited is asked of the process, every save.** A debugger can attach after the app started,
  and the runtime throws for an update under one, so the route reads it before the build and says so. I attached
  netcoredbg to the worker for real: the app reports `Debugger.IsAttached`, the save is a restart that names it. The
  debugger a user opens on a failing test attaches to the FSI host (`HostAgent.DebugBegin` lives there), a different
  process, so it never touches this.
- **A method Harmony has patched is refused.** Harmony redirects the method's native code and a delta replaces the body
  the redirect wraps, so one of the two would be lost without a word. `DeltaApply.methodsPatchedByOthers` resolves the
  delta's method tokens and compares them with Harmony's own list of patched methods. SageFs's guards and detours patch
  in the FSI host and the delta goes into the worker, so today the two meet only when an app patches its own methods, and
  the fence is for that. A method a delta patched cannot meet a guard either, since guards never run in the worker.
- **Coverage.** A method patched into an instrumented module runs without its probes (the new body is the clean build's),
  and a child-process test pins it. Coverage is only read in the process the tests ran in (`HostAgent.TakeCoverage`, the
  FSI host), and a delta lands in the worker, so no number anyone reads is lost. If something starts reading coverage in
  the worker, a patched method reads as uncovered, and that test is where to look.
- **The runtime variable has a scope.** `DOTNET_MODIFIABLE_ASSEMBLIES=debug` means something only when the process starts,
  so the daemon sets it for a hot reload worker started for the route (`MetadataDeltaMode.workerEnvironment`), says
  `SAGEFS_METADATA_DELTA` beside it so the worker never guesses, and `ProcessEnvironment` strips it from everything the
  worker spawns, the save's own build and the FSI host included. A route that is off puts neither in the environment.

What it leaves undone: the FSI host keeps the code of the last build, so a REPL call to a function the save changed runs the
old body. That is a state the session carries and every surface shows, and the next entry is what I did about it and what I
chose not to. A field added to a type, a new closure class and a lambda that starts capturing something still restart, and
each says which.

Measured on this machine (16 logical cores, Linux, .NET SDK 11.0.0-rc.1.26425.128; the run_app fixture; n = 6 saves, medians):
file written to the new body served, 2.6 s on .NET 10 (twice) and 1.8 s then 2.6 s on .NET 11, the second with other builds running on the
machine. The build is 1.6 to 2.4 s of that, the diff and the delta written 5 ms, the runtime's call with its handlers 1 ms. A restart pays
that build and then starts a process, host to the app answering 4.4 to 5.6 s. Being editable cost a call-heavy loop (50 million calls of a
method the compiler may not inline, five runs after a warm-up) 489 ms against 481 ms on .NET 10 and 444 against 448 on .NET 11 the first
time, and 573 against 493 (1.16 times) and 470 against 481 the second. I had expected about 1.3 times from an earlier study. The build
SageFs makes is already unoptimized, so the process was paying most of it before.

It is on by default for a hot reload worker, and `SAGEFS_METADATA_DELTA=off` is the way back. The bar I set was: the whole host
tier and the browser journeys green with it on, a save served in a small fraction of a restart, and no cost I could measure on
the process for being editable. With the variable on, the host tier ran 312 tests, 307 passed and 5 are ignored by design,
none failed (`TRUST tier=--integration-host ... verdict=Trusted`), and the 8 browser journeys of `--integration-hr` passed. A
save was served in about a third of what a restart took, and the loop cost between nothing and 16 percent. What I weighed against it: the REPL
and live tests keep the last build's code (above), which a restart used to refresh, and every number here is one fixture
on one machine. I took the first as a documented limit, because a REPL in a hot reload session has always shown the code of the
last build or reset, and the second as the reason the escape hatch is one variable.

Evidence: `SageFs.Host/RunAppDelta.fs`, `SageFs.Core/Features/PatchRoute.fs`, `SageFs.Core/Features/MetadataDelta/DeltaSession.fs`,
`SageFs.Tests/RunAppDeltaTests.fs` (the rows, the chain, the build that fails, the route off, and the cost, printed as
`DELTA-COST`), `SageFs.Tests/DeltaRouteTests.fs`, `SageFs.Simulation/DeltaRoute*.fs` with `SageFs.Tests/DeltaRouteSimTests.fs`,
and the debugger row in `SageFs.Tests/HotReloadDebuggerTests.fs`.
Reopen it if: #19941 ships in an SDK (the emitter becomes the part that applies and confirms), or a client needs more than
`mechanism` to tell the two routes apart.

## Waits for the machine scale with a measured tier, and a start that runs out of patience retries with more

A session could not start on a 2009 four core with a spinning disk until somebody raised a 30 second limit by hand.
I measured it before changing anything (`scripts/machine-bench.fsx`, the numbers are in
[Slow machines](TROUBLESHOOTING.md#slow-machines)). The first start builds the FSI host once and that took 40
seconds there against 10 on a fast desktop. The inactivity limit was 30, so it killed the worker mid-build, and the
restart policy started it again with the same 30, five times, then reported an exit code. So two things were wrong, and
they got two fixes.

The waits were written for one machine. Every duration in `Timeouts.fs` is now declared as either a wait for the
machine (60 of them), scaled by the tier's factor, or fixed (94), with a reason. `TimeoutScalingTests` fails on a duration that is in
neither list, so a new wait is decided the day it is added. The tier comes from a single-thread calibration (thread
CPU time, because a busy machine stretches the wall clock of the same work 3 to 4 times and not the CPU time), the
cores and any CPU quota, the memory a cgroup leaves, and whether the disk spins. It is the slowest of those, because a
machine is as slow as its worst limit. The factors (1, 2, 5, 12) are set so the 30 s inactivity allowance
sits 1.9 to 2.9 times above the longest silent stretch measured in a tier; the table with machine and run counts is
in the troubleshooting page, and so is the tier I could only extrapolate.

A timeout was a crash. A start that goes silent is now reported as a start timeout, with exit events switched off
before the kill, and the manager retries it with twice the silence (never the same, never less, four attempts, never
past the absolute bound). A first attempt is as patient as the tier or the machine's own recorded starts say, whichever
is longer; the record is a smoothed mean plus four deviations, the way TCP estimates a round trip (RFC 6298), in
`machine-profile.json`. A give-up carries what it waited for, how long, how many times, the tier, and what to do. I
did not take an existing library's adaptive timeout: the rule is five lines and the part that matters is where it is
applied.

Rejected: letting the daemon's inactivity watcher count a live build child as progress, so a busy worker is never
killed. It would fix this one stall and hide the next. The worker's own phase budgets (`hostBuildRun`,
`fsiHostStartup`) are the right guard for a hung child, and they scale. Also rejected: probing the machine on every
start. It is read from the profile, and probed once per data directory (about a fifth of a second).

The proof is `SageFs.Simulation/StartEscalationSim.fs` and `StartLearningSim.fs`: seeded worlds over the real
escalation, with a twin for the old identical-retry loop, a twin for a loop with no end, and a twin that never learns.
The invariants (never-retry-with-a-smaller-budget, eventually-succeeds-if-the-machine-can, failure-names-the-wait,
no-unbounded-loop, first-attempt-honours-what-was-learned, next-first-attempt-covers-the-last-start,
stable-machines-stop-wasting) hold for the real code and fail on the twins. The live checks are in the troubleshooting
page.

What I haven't done. The tiers below `Constrained` are extrapolated: nothing I had access to is slower than a 2009 four
core, and a machine twice as slow again is a guess with a safety factor on it. CPU quotas and cgroup limits are
emulated here, a slower clock and a slower disk are not (they need root). A start whose silence is a build that is
legitimately slower than the escalation reaches (a cold host build on a machine under heavy load, past the fourth
attempt) is still given up on; the message says so and what to set. A stop costs 5 seconds on every machine, because
the worker never answers the shutdown request in time (2 s) and the process never exits in its grace (3 s); that is a
separate bug. Those waits are fixed on purpose: they are bounds before a kill, always spent in full, so scaling them
would only make a stop slower on a slow machine. I haven't fixed the cause.

One thing I learned on the way, so it isn't learned twice. A top-level `do` in an earlier file of an executable
project doesn't run at all: F# initialises those files when something in them is first used. The first build set the
tier from such a `do`, and the daemon log said the timeouts had been read before it. The settling is a `do` at the
top of `SageFs/Program.fs`, the last file.

Evidence: `SageFs.Core/MachineTier.fs`, `MachineProfile.fs`, `MachineCalibration.fs`, `StartLedger.fs`,
`StartEscalation.fs`, `StartTimeoutDecision.fs`, `WorkerStartup.fs`, `Timeouts.fs`;
`SageFs.Tests/TimeoutScalingTests.fs`, `StartEscalationTests.fs`, `StartEscalationSimTests.fs`,
`SessionManagerStartEscalationTests.fs`; `scripts/machine-bench.fsx`.
Reopen it if: a machine in the field starts slower than its tier's factor allows and the message did not say so, or a
wait that is fixed turns out to be waiting on the machine.

## A landing is a save in the trunk, handed to the worker by the daemon and not seen by its watcher

The cohort already landed agents' work: claims, a rebase in an integration worktree, a verification, a fast-forward of a ref.
Nothing used the result. The live loop wants the other half: what lands is served by the running app, with its state, and the
daemon says how. So there is now a **trunk checkout**, a second detached worktree that `set_integration_ref` makes, moved to a
landing's commit after the landing has landed and never before. A **trunk session** is a session whose working directory is it,
and nothing registers one: the working directory is the whole definition (`TrunkSessions.sessionsIn`).

I did not use the integration worktree as the trunk. The gate rewrites it for every landing it verifies, before the verdict, and a
trunk app there would have served changes that never landed. I did not let the worker's file watcher deliver the landing either.
The checkout is written by git, in several files at once, and the watcher would run the same save the daemon is about to hand over
and report a second save that changed nothing over the first, on the very row that is supposed to say what the landing did. So a
worker in a trunk session is told, before the checkout moves, to take its saves from landings only (`SetSaveSource`), and the
daemon hands it the files (`ApplySaves`). The worker runs its own pipeline over them, one at a time and in order, which is the
pipeline a person's save takes, and the verdict is the first terminal event that pipeline records for the file. The cost is that
a person's save in the trunk checkout is not hot reloaded there. The checkout is the daemon's, so I took it.

The decision is pure (`TrunkFollow.step`) and one owner folds it (`TrunkFollowOwner`), the way `BuildConfirmation` and the cohort
owner are built: a landing that lands while another is being followed waits, landings are followed in the order they landed, and
a report that a patch settled that arrives before the answer carrying the patch is held and applied when the answer lands.
The record says what the pipeline said and nothing more. `PatchPending` is not `Patched`; the worker's later report is what
makes it `Patched`.

The trunk builds the project before it tells a session that runs an app. The pipeline's patch lives in the running process, and
what a later start, or the daemon's own respawn for a restart, runs is the build on disk. The daemon respawns a worker without a
build when it finds nothing that holds the old shape (`RestartCost`), and a restart I provoked with a virtual member's new
signature came back serving the old code. That decision is another area's and I left it alone; building first makes the trunk
right whatever it decides. A session with no app is not built, because nothing runs, so its line says to rebuild before `run_app`.

What I measured, one daemon on this machine with three MCP connections and a fixture app that counts its requests in memory (the
five rows of `TrunkLandingOutcomeTests.fs`): a landing of one handler file ended `PatchPending by metadata-delta`, then
`Patched` once a request ran it; the same process answered before and after, and its counter went on from where it was; a
conflicting landing was refused with `RebaseConflict` and the app did not change; a restart-needing landing said `Restarted`
with the cause the planner named, ended in a new process with the landed code and a count that started over; a landing with a red
test was blocked and the trunk was told nothing.

What it leaves undone: the verifying session keeps what it evaluated for a landing it blocked, so the landing after is verified
against that, and the trunk row for a red test runs last for that reason. A function name two modules share was re-pointed in
both by the verifying session's re-evaluation (one in my first fixture), which I did not chase. The trunk is one checkout per
daemon, and the trunk record is in memory: a daemon restart forgets the lines, and the next landing's diff still brings the
checkout to the integration head.

Evidence: `SageFs.Core/Features/TrunkFollow.fs`, `TrunkFollowOwner.fs`, `TrunkSessions.fs`, `SageFs/TrunkFollowShell.fs`, the
worker side in `SageFs.Host/WorkerMain.fs` (`applyLandedSaves`), `SageFs.Simulation/TrunkFollow*.fs` with
`SageFs.Tests/TrunkFollowSimTests.fs`, `TrunkFollowTests.fs`, `TrunkFollowShellTests.fs`, and
`SageFs.Tests/TrunkLandingOutcomeTests.fs`, the five rows against a real daemon.
Reopen it if: a client needs the trunk lines as an SSE event or in `cohort://status`, a cohort gets more than one trunk, or the
daemon stops respawning a worker without a build.
## The REPL is behind the app after a delta, and the session says so instead of refreshing it behind your back

A delta goes into the worker, where the app runs. The FSI host, where `send_fsharp_code`, `check_fsharp_code` and live tests run,
keeps the build from before it. I measured that rather than assume it: the app served `closure:B` and the REPL answered
`closure:A`, on .NET 10 and 11 (`run_app repl freshness`). A tool that does that and says nothing is lying with every result,
and the first version of this route did exactly that, in a paragraph of a doc.

**What it is now.** `ReplFreshness` is `InSync | BehindApp of savesSince * declarations`, carried on `SessionInfo`. The session
manager folds it from the reload reports it already receives: a save that a metadata delta took puts the REPL behind, counted once
(its pending report and its confirmation are one save) and naming what it patched; a detour, a restart, a failed compile and a
save that changed nothing do not. A confirmation seen with no pending report before it (a daemon that joined late) still puts the
REPL behind, because silence is the one thing this state must not be. A replaced worker is built from the current build, so it
clears the state. The worker's pending report carries `declarations` for this, which is the only reason that field exists.
It is shown as a field in `get_session_status` (every shape) and `list_sessions` (text, JSON read model and `/api/sessions`), as
a warning after the result of every `send_fsharp_code` (success and failure alike), `check_fsharp_code` and `run_tests`, as a
field on their structured results, and as a line on the dashboard session card. The card line is a line of the card's single
column, so a narrow card only wraps it; a real Chromium page checks that at five widths with the dashboard's own stylesheet.
One test per surface, through the real tool member, reading the text an agent reads.

**The remedy it names is honest about its price.** `hard_reset_fsi_session` with `rebuild=true` brings the REPL level, and it
does that by replacing the worker, so the running app stops with it and its in-memory state is lost (`AppRun.acrossWorkerRestart`:
a running app died with the old worker). That is the thing the delta route exists to avoid, so the warning says so.

**Why I did not make the refresh automatic.** The REPL can be brought level without touching the app. The worker has its own
hard reset of the FSI host (`WorkerMessage.HardResetSession` with no rebuild): measured, two runs each, it took 2.5 and 2.7 s on
.NET 10 and 2.1 and 2.0 s on .NET 11, the REPL then answered the patched body, and the app kept its process and its state. I looked hard at doing
that in the background after a save, off the eval path and coalesced across a burst, and stopped, for three reasons that each
need their own work and one of which is silent:

- It wipes the REPL. A new FSI session has none of the definitions the user or an agent made, and none of what an init script
  defined. Doing it only when the REPL provably holds nothing needs an "idle and empty" check inside the eval actor (the router
  cancels a running eval before a hard reset, so the check cannot be made outside it) and a look for a startup profile. I did not
  build that.
- It kills a test run in flight. Tests run in the FSI host, which the actor does not see as busy, so the actor's idle phase
  proves nothing about them.
- It desynchronises live testing without a sound. The daemon fetches each session's instrumentation maps and test discovery once,
  when a worker becomes ready (`WorkerPostReady`). A new FSI host instruments a new copy of the assemblies, so probe numbering
  changes under a map the daemon still holds, and coverage is then attributed to the wrong lines. A worker restart re-fetches both. The editor's
  existing in-worker hard reset has the same exposure unless something else re-fetches, and I did not check which.

Applying the same delta to the FSI host's copy is the other way to remove the gap, and it is blocked more fundamentally: the host
reads coverage, and a patched method runs without its probes (a test pins that), so every patched method would read as uncovered in
the one process that reports coverage; and it needs the host started editable, a protocol message, and the delta's apply code in
the host's separate source list, where the guards patch with Harmony and the Harmony fence would refuse.

So the gap is announced everywhere an agent or a person looks, in words that say what to do and what it costs, and it is not
removed. What would remove it: the daemon re-fetching instrumentation maps and discovery after any FSI host swap, an actor-side
idle-and-empty check, and the host's in-flight test count. Then the refresh is a background step with the loud state held until it
finishes. Nothing here prevents it, and `ReplFreshness` is the state it would drive.

Evidence: `SageFs.Core/SessionReload.fs` (`ReplFreshness`), `SageFs.Core/SessionManager.fs` (the fold and the clear),
`SageFs.Tests/ReplFreshnessTests.fs` (the fold, the words, the wire), `SageFs.Tests/ReplFreshnessSurfaceTests.fs` (status, list,
send, check), `SageFs.Tests/McpRunTestsTests.fs`, `SageFs.Tests/ReplFreshnessDashboardTests.fs` (the card, in HTML and in
Chromium), `SageFs.Tests/SessionManagerRebuildOutcomeTests.fs` (the session carries it, a replaced worker clears it), and
`SageFs.Tests/RunAppDeltaTests.fs` (`run_app repl freshness`, the measurement).
Reopen it if: the daemon starts re-fetching maps and discovery after a host swap, or the FSI host can apply a delta and keep its
coverage.
## A run_tests receipt says whether the build it ran against is behind the files on disk

Nehemiah edited a test file on disk, did not rebuild, and called `run_tests`. The receipt said `3 passed` and nothing else. I
checked that against the code before building anything, because the report also said `get_session_status` already knew the edit was
not reflected, and it does not. `run_tests` never looks at the disk. The check that was meant to catch this, `StaleDefinitions` in
`sessionTrustObservation`, fired only when a `FileStatus` read `Stale`, and both places the daemon builds a `SessionContext` hard-code
`FileStatuses = []`, so nothing ever produced `Stale` and the check reported "current" by naming the artifact. `replFreshness` is a
different fact (the REPL behind a delta-patched app) and read `InSync`. A rebuild in progress leaves the old worker Ready, so a run
during one was trusted too.

**What it is now.** `SourceState` is `InSync | Stale of changed files | Rebuilding | Unknown of reason`, in
`SageFs.Core/Features/SourceState.fs`. The decision is a pure function of the rebuild record, when the worker loaded its build, and the
write times of the build output and of every file that builds into it. The disk reads sit at the edge (`SageFs/SourceStateProbe.fs`)
and every failed read arrives as a case of the evidence, so "I could not tell" is an answer and never a quiet "in sync": no project
loaded, a worker that did not report, a file or its directory that cannot be stat'ed, a project whose Compile items need MSBuild to
evaluate. A rebuild in progress answers by itself. A changed file outranks a file that could not be read, because it is the fact someone
can act on.

**Where the build stamp comes from.** From records that exist, not a new one. The worker's warmup report already carries when it
started and which assemblies it loaded, and the daemon already fetches it for `get_session_status`. The stamp of the build is the
write time of that assembly's output on disk: a source written after it is an edit after the build, and an output written after the
worker started means a newer build than the one the session runs (an outside `dotnet build`). I did not use `LastRebuild` for the
stamp, because it holds only the last daemon-driven rebuild and says nothing about a session that was started on a stale build or built
from outside.

**The receipt composes with what it had.** `RanReceipt` gains `Source`, and the verdict became a function of the counts and the source:
`AllPassed` only over `InSync`, and `PassedOnStaleSource`, `PassedWhileRebuilding` or `PassedOnUnknownSource` otherwise, so a pass over
a build that is behind never reads as plain `AllPassed`. A run is read against the disk when it is dispatched and when it first settles;
the worse of the two is the receipt's source, so an edit during the run counts (the `RunningButEdited` case the receipt used to lose),
and the reading is frozen at settle, so the same `receipt_id` does not change its mind because of an edit made afterwards. The run
still happens over a stale source. A refusal would have given the agent no receipt to read, and the point is that the receipt says what
it ran against. `observe` alone, with no source check, starts at `Unknown NotAssessed`, so a path that forgets to attach a source gets
the cautious verdict and never `AllPassed`.

**It is on every surface the status already is, next to `replFreshness` and named so the two cannot be confused**: `sourceState` in
every shape of `get_session_status` (ready, warming, faulted), a `Source:` line under every entry of `list_sessions`, and `source` on
the `run_tests` structured result.

**Why `FileStatuses` was not made real.** I considered filling it from this decision. It is a snapshot the Elm model holds, and an edit
on disk does not push into the Elm model, so a freshness read from it is stale the moment it is made. That is the same bug as the
original. So the staleness truth is `SourceState`, read when a tool asks, and the trust path (`targeted_verify`) now judges loaded
definitions from it. `StaleDefinitions` is therefore reachable for the first time. `FileStatuses` stays the dashboard's file list, and
nothing reads staleness from it. `FileReadiness.Stale` is still in the type because the dashboard has a colour for it
(`DashboardFragments.fs`), which I did not touch; nothing produces it, and removing the case is a one-line follow-up there.

**What it cannot see, and says so.** The stamp is a write time, so a file that was only touched (a branch switch does it) reads as
changed; that errs toward a warning. A file edited inside the window of a build that was already running is written before the output
and may not be in it; the output's write time is the end of the build, so the decision cannot tell, and the simulation keeps edits and
builds from overlapping in its ground truth for the same reason. Edits to `Directory.Build.props`, a lock file or a referenced project's
own sources outside the listed projects are not inputs it looks at. A project whose Compile items use a wildcard or an MSBuild property
is `Unknown`, not guessed. `/api/sessions`, the `sessions://list` resource and the dashboard card carry it too now (see the
follow-ups entry at the end of this file).

**What it costs.** One pass of file stats and one project-file parse per project, when a tool asks. I measured it on this repo's own
three projects (SageFs.Core alone lists 295 Compile items): 9 ms the first time and about 2.5 ms after, in a warm REPL session.
`list_sessions` also asks each live worker for its warmup report, all at once, which `get_session_status` already did for one.

**How it is proved.** `SourceStateTests` (the decision as examples and as properties, with an oracle written as a conjunction), and
`SourceStateProbeTests` against real files and write times. `SourceStateSim` is a DST over edits, builds that end or fail, workers that
load the build on disk or keep an older one, outside builds, files that stop being readable, a rebuild record delivered in any order and
a silent worker, with the invariants NEVER-GREEN-OVER-STALE, REBUILD-IS-NEVER-INSYNC, UNKNOWN-IS-NEVER-INSYNC and INSYNC-IS-EARNED.
Three twins put the bugs back: a decision that ignores write times, one that believes the rebuild record over the files, and one that
takes a failed read for an old stamp. They are caught on 159, 102 and 45 of the first 300 seeds, and the real decision breaks no invariant on any of
them. `McpRunTestsTests` runs `run_tests` through the tool layer over a project
on disk, `SourceStateSurfaceTests` reads every status shape, and `SourceStateOutcomeTests` is the gate through a real daemon and a
real MCP client over a fixture project of its own: untouched is `AllPassed` and `InSync`, a test file edited with no rebuild is
`PassedOnStaleSource` naming the file, a source edited too names both, a run during a rebuild is `PassedWhileRebuilding`, a rebuild
then reads `InSync` again, and a directory made unreadable reads `Unknown` with the reason.
Reopen it if: a client needs the field on `/api/sessions`, the project list can be read from the evaluated project instead of the
project file, or a stamp that survives an edit inside a build's window becomes cheap (a recorded build start).

## An analysis tool answers "measured" or "not available", and reads one session

Nehemiah, a policy engine that gates agent work on SageFs's answers, found that it could not tell "zero" from "unmeasured". I
read the code and the answer was three separate bugs and one missing type.

**The three feeds.** `send_fsharp_code` records each eval into a per-session store (`recordEvalForSession`). `impact_forecast`,
`get_cell_dependencies` and `diagnose` read the daemon-global store, which only the `/exec` path feeds, so they answered about
somebody else's evals or about none. I measured it on the live daemon: `get_cell_dependencies` listed fourteen cells and none
were mine, and `impact_forecast` stamped the same `LatencySpike` on every one. Second, the MCP text path stores
`Result: val x: int = 1`, and the two binding readers only looked at lines that start with `val `, so the first `val` line of
every statement was invisible and `plan_ripple`, `preview_what_if` and `suggest_next_cell` saw a session with no bindings. Third,
`diagnose` and `coverage_intel` read the primary live-testing cycle, never the cycle `run_tests` writes through. On the live daemon
`diagnose` reported a failing test that belongs to another agent's session and said nothing about the one in mine.

**What I changed.** Each tool now resolves one session the way `run_tests` does (`session_id`, then `working_directory`, then the
session the connection is on) and reads that session's own eval history and the cycle that owns that session's tests
(`SageFsModel.cycleOwnedBySession`). Nothing falls back to the global store or to the primary cycle. The binding readers take the
`Result: ` prefix off a line first, through one function, so the prefix has one reader and one name (`McpResultPrefix`).

**The closed answer.** `ToolAnswer = Measured | NotAvailable of NotAvailableReason`, in `SageFs.Core/Features/ToolAnswer.fs`.
The reason is a closed set with one exhaustive token, one plain sentence and one action each. I decided per tool what it needs.
The cell-history tools need a recorded eval (`NoEvalsYet`). `plan_ripple` and `preview_what_if` also refuse a cell id or a binding
the session does not have, where they used to answer with `?` and a zero. `diagnose` needs either an eval or a test result and names
the side it could not read in `Unmeasured`, so "No issues detected" is no longer a thing it can say about a side it never saw.
`coverage_intel` is the only one that fundamentally needs live testing, because coverage bitmaps come from its instrumented runs.
With a failing test and no coverage it says which switch is missing: the session is not in the LiveTesting workflow, the workflow
is on but live testing is off, or live testing is on and no run has recorded coverage yet. Tests ran and none failed is a
`Measured` empty list. The wire carries a typed `answer` field in the structured content and one plain sentence in the text; a
`Measured` text block is the measurement as it was, so nobody who parses it breaks.

**What the numbers mean.** `impact_forecast` measures REPL cells. Its downstream count is later evals that use a name this one
bound, and its P50 and P95 are the session's last twenty evals, the same on every row. It says nothing about the blast radius of a
change to source files or which tests a change affects, and its description now says so in those words, so nobody gates on it as
a blast-radius oracle. `get_cell_dependencies` used to print `TotalStale 0` and `all fresh` because it always asked for the
staleness of an empty changed set. It now reports `Staleness: NotMeasured` and no count.

**The tool list.** `discover_features` advertised six tools `tools/list` never had (`explore_namespace`, `explore_type`,
`get_completions`, `get_file_coverage`, `query_test_coverage`, `visualize_domain_model`), and their instructions text still sat in
`McpTools.fs`. I deleted the text. `discover_features` is now built from the registered set, read by reflection the way
`.WithTools<SageFsTools>()` reads it (`RegisteredTools.describe`): the name, summary and example call of each suggestion come from
the tool's own registration, discovery adds only a rank and a reason, and a registered tool with no rank is still listed, last.
`ToolSurfaceHonestyTests` fails if a rank names an unregistered tool, if discovery lists anything other than the registered set,
if an affordance state offers an unregistered tool, if `docs/mcp-tools.md` disagrees with the registered set, or if a description
names a tool that is retired or not registered. The six names are in `RetiredTool`, so `RetiredToolNameTests` scans for them.

**What I left.** `suggest_next_action` still reads the global store and the primary cycle, the same defect, and I did not touch it
because it is outside the seven tools I was asked about. The `/exec` path still records only into the global store, so an eval
sent from an editor is not visible to these tools and an MCP eval is not visible to the dashboard's pushes. Both writers should
record into the one per-session store, and that change is in `Mcp.fs` and `McpServer.fs`, which I did not edit.
`Mcp.discoverFeatures`, `Mcp.diagnose`, `Mcp.coverageIntel`, `Mcp.impactForecast`, `Mcp.planRipple`, `Mcp.previewWhatIf`,
`Mcp.suggestNextCell` and `Mcp.getCellDependencies` are now unreferenced and can be deleted.

Evidence: `SageFs.Core/Features/ToolAnswer.fs`, `SageFs/McpAnalysis.fs`, `SageFs.Core/Features/FeatureDiscovery.fs`,
`SageFs.Tests/EvalFeedHonestyTests.fs` (the prefix), `SageFs.Tests/ToolAnswerTests.fs` and `SageFs.Tests/McpAnalysisTests.fs`
(the decisions and the per-session routing), `SageFs.Tests/ToolSurfaceHonestyTests.fs` (the surface), and
`SageFs.Tests/HonestEmptiesOutcomeTests.fs` (a real MCP client against a real daemon: two sessions, one with a failing test).
Reopen it if: a tool that reads tests can say something useful with no recorded test result, the `/exec` and MCP writers share one
per-session store, or discovery needs to rank by the session's workflow.

## A build in a repo that builds its own SageFs.Core compiles against that Core, and a wait covers a rebuild

Three agents developed SageFs with SageFs and hit the same two walls, one after another. I read the code for each before changing anything.

**The skew.** `hard_reset_fsi_session rebuild=true` failed as soon as SageFs, SageFs.Host or SageFs.Tests used an API the worktree's
SageFs.Core had added and the running daemon's Core did not. The cause is in `SessionBuild.coreReferenceTargetsContent`: every build a
session runs gets the daemon's own `SageFs.Core.dll` injected as a `<Reference>`, so a user project can use the holder API without
writing a HintPath. That is right for a project with no Core of its own. For a project in a repo that builds SageFs.Core, MSBuild
hands the same injection to every project the build reaches through a ProjectReference, so each one compiled against the daemon's older
Core metadata and the new names were "not defined". The injection is a build-time reference and nothing else: the isolated FSI host
does not carry SageFs.Core, and a session already loads the project's own Core from its bin, so the host design does not make the
injection unnecessary, it only means skipping it for these projects costs nothing at run time.

**What it is now.** `CoreEvidence` (`SageFs.Core/CoreEvidence.fs`) reads the project files of what a session builds, and only the evidence
that cannot vouch for itself: the project is SageFs.Core (by file name or `AssemblyName`), it or a project it references has a
ProjectReference to SageFs.Core, or it wrote its own `<Reference Include="SageFs.Core">`. A `SageFs.Core.dll` in the project's bin is not
evidence, because an earlier injected build puts the daemon's Core there. `SessionBuild.decideInjection` turns that into
`InjectDaemonCore`, `UseProjectCore`, `NothingToInject` or `Refuse`. A project that cannot be read, or a referenced project that is
missing, is refused with the path and the reason before a build slot is taken, because guessing "no Core of its own" is exactly the
silent shadowing this fixes. A ProjectReference written with an MSBuild property other than `MSBuildThisFileDirectory` or
`MSBuildProjectDirectory` is read for its name but not followed, and a reference added by an imported props file is not seen. Those two
gaps leave the old behavior, not a new failure.

**The hint.** The "move X above Y" advice came from matching a missing type's name against any later declaration of that spelling,
including a union-case arm. `CompileOrderInsight` now matches by kind (a missing type is satisfied by a type, not by a `let` or a case),
and a name defined by a referenced project gets its own hint: a stale reference, rebuild that project, the files are in order.

**The wait.** `get_session_status wait_seconds` answered `NotNeeded` during a rebuild because a build-first rebuild keeps the old worker
serving and the lifecycle reads Ready. `ReadyWait` (`SageFs.Core/ReadyWait.fs`) is now the one decision: the session manager settles
its parked callers with it and the status tool decides whether to park with it, so they cannot disagree. A Ready session with a rebuild
in progress keeps the caller parked until the new worker is Ready. A rebuild that fails answers the callers parked through it with the
build's own error, so the outcome is `Faulted` and not a `BecameReady` from the worker that kept serving. The wait outcome set stays
`NotNeeded | BecameReady | Faulted | TimedOut`, and `wait.lastRebuild` carries the rebuild's outcome. The manager records a rebuild a
moment after the request is posted, so the tool layer remembers a rebuild it was asked for until the restart answers, and parks behind
it. The park is queued after the request, so the rebuild is on the record when it is looked at, with no polling.

**What I did not fix.** Adoption is a separate, run-time mechanism (`HostCoreAdoption`): the worker links the daemon's Core, and a
session whose project ships its own `SageFs.Core.dll` gets that Core copied over the worker's, if the version numbers match. Version
numbers only move when `scripts/ship.fsx` runs, so a worktree's Core and an older daemon's Host can match by number and still differ by
build, which is the `TypeLoadException` seen on a Core session. That needs the daemon and its host to come from the build the session
expects, and I left it. The outcome gate copies a real SageFs.Core for that reason: a stand-in Core has a different version and cannot be adopted.

Evidence: `SageFs.Core/CoreEvidence.fs`, `SageFs.Core/SessionBuild.fs` (`decideInjection`), `SageFs.Core/ReadyWait.fs`,
`SageFs.Core/SessionManager.fs` (`settleReadyWaiters`, `endRebuild`), `SageFs/SessionStatusPayload.fs`,
`SageFs.Simulation/ReadyWaitSim.fs` (callers, rebuilds, deadlines and faults in every order, with twins that return early, poll, never
time out and call a failed rebuild Ready), `SageFs.Tests/CoreInjectionTests.fs`, `SageFs.Tests/ReadyWaitManagerTests.fs`,
`SageFs.Tests/StatusWaitTests.fs`, and `SageFs.Tests/SelfHostCoreOutcomeTests.fs` (a real daemon, a copy of SageFs.Core with one extra
file, and a consumer that uses it).
Reopen it if: the daemon and its host are always built together with the session's Core, so adoption can compare builds and not
version numbers, or a project can add a SageFs.Core reference through a props file this does not read.

## A lease belongs to who asked, and a refusal says who holds it

Three agents were refused `acquire_full_build_lease` for 17 to 35 minutes with "you already hold 1/1 leases" while none of them
held one. A sibling did. Claude sub-agents of one session share one MCP connection id, a lease was keyed by that id, and so every
sibling was the same holder to the pool. The message blamed the caller, and `get_daemon_status` showed a connection id and an expiry
and nothing else. The "running to 00:01" they saw was a sibling releasing and asking again, which gave the new lease a fresh ttl.
I hit the same wall myself while doing this work: my build lease was refused for about fifteen minutes by a sibling on my own
connection, and the only way to wait was to retry by hand.

**A lease is attributable to a `Holder`**: the connection, the `agent_name` the caller passed, and its `working_directory`. Equality
is the identity, so two sub-agents on one connection that name themselves differently are two holders, and so are two agents with
the same name in two worktrees. The three acquire tools take `agent_name` and `working_directory`. I did not try to solve connection
identity itself. That is the larger design in `cohort-member-identity-as-capability.md`. This only makes the answer truthful and the
holder visible.

**The decision is a closed type**: `Granted`, `AlreadyHeld`, `Queued` and `Refused`. `AlreadyHeld` is the same holder asking again for
what it holds: it gets the lease back with the same id and the expiry is not renewed, so asking in a loop can never keep a lease
alive. `Queued` carries the place in line, the asks ahead, the leases that really hold the pool (agent, connection, directory, kind,
granted, expires), the cap and the pressure, and when to ask again. `Refused` is for a holder whose own other lease or own other
queued ask is in the way, and it names that lease and its id so the way out is in the message. One function, `explain`, turns a
decision into the words an agent reads. The wire keeps the three tokens the guard hook and `scripts/local-gate.fsx` already read
(`granted`, `wait`, `refused`) and writes `grant: already_held` beside `granted`, so a caller that proceeds on `granted` still works.
`get_daemon_status` shows one row per holder with the agent, directory, kind and seconds left. The lease id is not in the status,
because the id is the capability to release it.

**Leases always expire, and so do places in line.** The ttl of each kind was already finite and a lease that is never released was
already reclaimed on the next contact. What was missing is the queue: an ask is only promoted when its own holder asks again, so a
crashed agent at the front held up everyone behind it forever. An ask that is not repeated within `Timeouts.leaseAskStaleAfter` (five
minutes; a refused caller is told to come back within 30 seconds plus a few per place) now lapses.

**The proof is a simulation with a twin per invariant.** Four named invariants: mutual exclusion (no holder holds two, and a grant
never goes past the cap), every lease expires (a lease lapses at grant plus ttl and is reclaimed on contact, nothing moves that
moment, an unrepeated ask lapses), the refusal names the real holder (every lease a decision names is in the pool, the caller's own is
the caller's, a queued caller is shown all of the pool when it is full), and same holder idempotent. Each has a twin that breaks
exactly that: a pool that never reclaims, a pool that identifies callers by connection alone (the old behaviour), and a pool that
takes a second lease on a repeat ask. Over 600 seeds the real pool holds all four, and the twins are caught in 199 of 200 seeds, 200 of
200, 64 of 200 and 112 of 200. `LeaseHolderOutcomeTests` drives one real MCP connection against a real daemon as three siblings.

**What it does not do.** Two siblings that pass no `agent_name` are still one holder, so they share one lease and a release by one
frees it for both. The tool descriptions, the skill and the server instructions say to pass your own name. The dashboard has no lease
panel at all, so there was nothing there to make attributable and I did not add one.

Evidence: `SageFs.Core/ExpensiveWorkLease.fs`, `SageFs/McpLeaseWire.fs`, `SageFs.Simulation/LeaseSim.fs` and `LeaseSimInvariants.fs`,
`SageFs.Tests/ExpensiveWorkLeaseTests.fs`, `LeaseSimTests.fs`, `LeaseWireTests.fs` and `LeaseHolderOutcomeTests.fs`.
Reopen it if: connection identity stops collapsing sub-agents (then `agent_name` can go), or a lease ever needs to outlive its
connection.

## The analysis follow-ups: one session for all eight, both writers per session, and the same fact on three more surfaces

Closes what the entry above left. `suggest_next_action` now resolves one session like the other seven and answers `Measured` or
`NotAvailable`, with `Unmeasured` naming the side it did not read. `/exec` records an eval into its session's store as well as the
global one the dashboard's push reads, so an editor's evals reach the analysis tools; an MCP eval still records only per session. The
eight formatters nothing called any more (`diagnose`, `coverageIntel`, `impactForecast`, `planRipple`, `previewWhatIf`,
`suggestNextCell`, `getCellDependencies`, `discoverFeatures`), the formatters of five retired tools (`getCompletions`, `exploreType`,
`visualizeDomainModel`, `getFileCoverage`, `queryTestCoverage`) and the JSON builders behind them are deleted, and the `Mcp.fs` budget
went from 3945 to 3340. I kept `exploreNamespace` and `getCompletionsItems`, which the HTTP routes call, and
`formatFileCoverageResponse`, which two test files pin.

Three more things told an agent something false. The live-testing hint said to call `enable_live_testing`, a tool that does not exist;
live testing is switched on with `switch_workflow`. `run_tests` listed three of its six verdicts; a test now reads the verdict type's
own cases and compares them to the description, so a seventh cannot go unlisted. `formatWorkerEvalResult` wrote the literal
`Result: ` that the binding readers strip with `CellDependencyGraph.McpResultPrefix`, so the prefix has one name now.

**`sourceState` is on `/api/sessions`, the `sessions://list` resource and the dashboard card**, under its own name beside
`replFreshness`. `sessionsToJson` stays pure and takes the readings as a map, and a session with no reading says `Unknown
NotAssessed`, never in sync. The card has a line of its own (`session-card-source`) for a stale, rebuilding or unknown source and none
for a current one. The dashboard reads the source only for the session being viewed, whose warmup report the push loop already holds,
and for a session mid-rebuild, which answers without the disk. Every other card says it was not read (`CardSource.NotReadOnThisCard`),
because the alternative is a worker round trip per card per push. `send_fsharp_code` and `check_fsharp_code` results carry
`replFreshness` and not `sourceState`, for the same reason: the reading needs the worker's warmup report and a disk scan per eval, and
`get_session_status` is where to ask.

Evidence: `SageFs.Tests/McpAnalysisTests.fs`, `EvalRecordingScopeTests.fs`, `ToolSurfaceHonestyTests.fs`,
`ReplFreshnessDashboardTests.fs` (the card at five widths, both lines on one card), `SessionOperationsTests.fs`, and
`HonestEmptiesOutcomeTests.fs` (a real daemon: an editor eval through `/exec`, `suggest_next_action` per session, both session lists).
Reopen it if: the daemon caches each worker's warmup report (then every card can be read), or an eval result needs the source.

## A member token is minted outside the ledger, shown once, and checked on every tool

Nehemiah runs coding agents in sealed boxes and wanted each run to be its own member, with a narrow role, a scope and an expiry,
and a way to cut one off without touching the rest. The Phase 2 sketch (`cohort-member-identity-as-capability.md`) minted the
token inside `Cohort.decide` from the command's entropy. Every ledger row keeps the entropy its command used so replay is
deterministic, so that design writes the token in plaintext to the ledger, its exports and the replay files. Turned down.

What I built instead. `Capability.fs` is its own pure reducer outside the event ledger. The edge draws 32 random bytes, shows
the token once, and hands the reducer only its SHA-256. A grant is a closed role preset (`Observer`, `Analysis`, `Verifier`,
`Implementer`), a canonical scope prefix and an expiry. A grant can only be narrower than its minter's, and "narrower" is a
partial order the property tests check: a role is narrower when its set of tool classes is a subset, a scope when it is the same
directory or under it, an expiry when it is no later. A request that is wider is refused with the widenings named. It is never
clamped. A revoked token stays revoked and its hash cannot be minted again. A token unused for a cohort lease window lapses, and
none outlives its expiry. The cohort sees a token holder as an ordinary member whose id is `cap:<fingerprint>`.

Three things in the same change, because the token is no use without them:

- **A member's public id is not the connection's bearer handle.** It was `mcp:<Mcp-Session-Id>`, and `get_cohort_status` printed
  it to anyone, so anyone could present it and act as the conductor (read from the SDK source, not run live). It is a fingerprint
  now, and `MemberId.display` fingerprints a raw handle that was wrapped by hand, so a ledger row written by an older daemon cannot
  print one either. A test drives two connections through status, frame, SSE rows, ledger export and the lease listing and
  fails if any handle appears.
- **Claim paths are canonical.** `src/Foo/../Bar/x.fs` did not overlap `src/Bar/x.fs`, which broke plain claim exclusivity and
  would have let a naive prefix check pass `src/Foo/../Bar`. Both separators are one, `.` vanishes, `..` pops, and a path that
  leaves the repo or is rooted is refused.
- **The allow-list is classes, and a token-less connection is a named policy.** Authority used to gate only the eight cohort
  tools. A tool now has a class, a role is a set of classes, a tool with no class is refused, and `tools/list` shows a token only
  what it can call. `IdentityPolicy` (`ConnectionsAllowed`, the default, or `TokenRequired`, from `SAGEFS_IDENTITY_POLICY`) says
  what a connection with no token is. Without `TokenRequired` the narrow roles sit beside an unrestricted door. The first joiner is
  still the conductor under `TokenRequired` (a token-less `join_cohort` is admitted until a conductor exists), because nobody can mint
  without one, so whoever joins first on a fresh daemon is the conductor: the orchestrator starts the daemon and joins before it
  starts any agent.

The token travels in the `X-SageFs-Member-Token` header or in MCP `_meta`, set by the platform, never in a tool argument:
an agent's transcript keeps every argument, and the stdio bridge's warning log prints the raw message. `sagefs mcp` reads
`SAGEFS_MEMBER_TOKEN` and sends the header, so the bridge is a supported path too. A connection-wide header cannot tell apart
sub-agents sharing a connection (finding F8); only a per-call `_meta` can, and a token outranks the connection when both are there.

What it does not do. A scope is policy, not containment: a process that can write the file can still write it. Any role with
`Eval` runs arbitrary code as the daemon's OS user, so the roles without eval are the ones that mean something against a hostile
agent. Tokens live in memory and do not survive a daemon restart. A token is not bound to a session or checkout, so an
`Analysis` token can read any session the daemon serves. A conductor that has departed still holds conductor authority on a live
connection (the presence check in `Authority.present` and `authorityOfMember` comes after the binding), which this does not fix.

Evidence: `SageFs.Core/Capability.fs`, `SageFs/CapabilityStore.fs`, `SageFs/McpCapability.fs`, `SageFs.Core/Cohort.fs` (`ClaimPath`),
`SageFs.Core/MemberTable.fs`; `SageFs.Tests/CapabilityTests.fs`, `CapabilityWireTests.fs`, `CapabilitySimTests.fs` (the DST,
with eight twins that each break one invariant), `ClaimPathTests.fs` and `CohortIdentityLeakTests.fs`.
Reopen it if: persisting tokens across a restart becomes necessary (then the hashes go in a store that is not the cohort ledger),
a harness gives the model a per-call header (then the per-connection header can retire), or a role needs a tool list the classes
cannot express.
