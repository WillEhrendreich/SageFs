# Hot Reload: How It Works

Save a `.fs` file and the change lands in the process that's already running your
app, including apps whose route table was built once at startup, the Falco /
Giraffe / Saturn pattern (`module App.Program` + `let routes = [...]`). No
restart. And the app's live state stays where it is: the counter you bumped,
the cache you filled. This is the thing I most wanted from a REPL and couldn't
get anywhere else, so I built it. The gaps I know about are further down,
under [Where it falls short right now](#where-it-falls-short-right-now). One
of them matters before you start: [how you start the app](#how-you-start-the-app-decides-which-of-two-things-happens)
decides whether a save patches it in place or restarts it.

> **Prerequisite:** Hot reload requires the **Hot Reload workflow**:
> `SessionWorkflow.HotReload`, which `switch_workflow` still spells `live` /
> `weblive` / `web` for historical reasons (I renamed the workflow late and
> didn't want to break anyone's muscle memory). It's off in the other two
> workflows, REPL (`Interactive`, the default) and Live Testing
> (`LiveTesting`), both of which keep full type redefinition instead. See
> [Workflow Modes](workflow-modes.md) for the full set and how to switch.

## How you start the app decides which of two things happens

Everything above is true for an app that lives in the same process as
SageFs's reload agent: one you start from FSI, or one an `.SageFs/init.fsx`
starts by `#load`ing your sources. There a save patches the running function
and your state stays where it is.

An app you start with `run_app` is different, and I found out by using it. It
runs on a thread in the worker, and the reload agent lives in the FSI host
(`HostAgent.fs` says so: [the agent runs in the process that loaded the
assemblies](https://github.com/WillEhrendreich/SageFs/blob/71f21e2fead3fffd71d06f58c7ffe6d717ccf824/SageFs.Core/HostAgent.fs#L1-L6),
and for an isolated session that's the host). So a patch landed in the host's
copy of the function and the app never called it. Until 0.6.845, a save to a
`run_app` app said `Hot reloaded 1 of 1 changed definition(s)` and changed
nothing you could see. I caught it by editing the ticker demo and watching the
output not change, on a build SageFs had made itself with optimizations off, so
the usual caveat below didn't explain it. A web route did the same thing.

From 0.6.845 a save to an app that `run_app` is running restarted it with your
change and said why: `renderLine changed, and this app runs in the worker, where
an in-place patch cannot reach it; restarting the app`. On the ticker that took
about six seconds on my machine. A restart resets the app's state. Carrying a
live value across a restart is a narrower feature that applies to a type or
value change on state the app registers with SageFs, and
[granular-restart-scope.md](granular-restart-scope.md) is where it's tracked; I
haven't tested it against a `run_app` save, so I'm not claiming it here.

That restart is still what happens with the metadata-delta route off. With it
on, a save to a `run_app` app is patched into the same process by a **metadata
delta**: SageFs builds your project, takes the difference between that build
and the assembly the worker loaded, and hands the runtime a delta
(`MetadataUpdater.ApplyUpdate`, the call `dotnet watch` makes for C#). Your
state stays where it is, and the save says `metadata-delta` as its
`mechanism`. [How it works](how-hot-reload-works.md#a-run_app-app-the-same-save-as-a-metadata-delta)
has the mechanism, the measurements and what it can't take.

| You start the app | It runs in | A save to a function | Your app's state |
|---|---|---|---|
| from FSI, or an `.SageFs/init.fsx` that `#load`s your sources | the reload agent's process | patched in place by a detour, no restart | stays where it is |
| `run_app`, metadata-delta route on | the worker | patched in place by a metadata delta, same process id | stays where it is |
| `run_app`, route off | the worker | SageFs restarts it with your change and says why | reset by the restart |

This route merged on 2026-10-01, after v0.6.875 was tagged, so it is on master and
not in a release yet. In v0.6.875 a `run_app` save does what the bottom row says. The
same goes for generics: patching a generic function in every instantiation is also
merged and unreleased, and in v0.6.875 a save to one restarts the app and names
`GenericFunction`.

The route is on by default. Set `SAGEFS_METADATA_DELTA=off` in the daemon's
environment and a `run_app` save restarts the app as it did before this route
existed ([configuration](configuration.md)). Measured on my machine on the test fixture,
a save is served in 1.8 to 2.6 s, against 6 to 8.5 s for the restart it replaces.

What a delta takes and what it doesn't, from the emitter's own refusals
(`RudeCause`): a new body for a method, a closure body, a task body, a method added to a
type the app already runs, all in every instantiation of a generic function.
Everything else restarts and names the declaration: a type or method removed, a
field added, a signature changed, a changed startup value, a new lambda that adds a
closure class, a lambda that starts capturing something. The planner turns away what
it can see from source (a type's shape, startup code, mutable state) before anything is
built, and the emitter turns away what only the compiled shapes show.

The rule that picks the route is [`PatchRoute.choose`](../SageFs.Core/Features/PatchRoute.fs),
the placement rule it falls back to is `AppPlacement.adjust`, and the worker applies
both to every save. The rows that prove it are
[`RunAppDeltaTests.fs`](../SageFs.Tests/RunAppDeltaTests.fs): one real app per row
on a real host, on .NET 10 and .NET 11, each ending in the same process serving
the new code and `Patched`, or in a restart that names what could not be patched.
[`RunAppSaveOutcomeTests.fs`](../SageFs.Tests/RunAppSaveOutcomeTests.fs) pins the
restart for the route off. The other "real app" tests here start their apps inside
FSI, where the agent is, so that is what they prove.

## The pipeline

1. The file watcher detects `.fs`/`.fsx` changes. A burst of events from one save settles for 200ms ([`Timeouts.fileWatchDebounce`](../SageFs.Core/Timeouts.fs)), and a second event for a file within 500ms of its last compile is dropped as the same save ([`Timeouts.doubleCompileGuard`](../SageFs.Core/Timeouts.fs)).
2. SageFs diffs the file against the source your loaded assembly was actually
   built from and emits only the **functions that changed**, against the
   compiled module's own identity. That keeps their parameter types as the
   compiled types.
3. [Harmony](https://github.com/pardeike/Harmony) re-points those methods at
   their new bodies at runtime. No restart.
4. The worker decides what the save did (applied, needs a restart, did not
   compile) and says so on its reload stream. A patch is reported **applied**
   first and **patched** only after the new code has been seen running, see
   [What "patched" means](#what-patched-means). The daemon keeps the last thing it
   said on the session, so you can read it without a browser tab: it's
   `lastReload` in `get_session_status` and `/api/sessions`, a `ReloadReported`
   event on the SSE stream, and a line in the MCP push an agent gets. The browser
   overlay still gets it too. Before this, a console app that needed a restart
   said so only in a worker log nobody was reading.
   (`SessionReloadTests` pins the wire shape against the worker's own events, and
   `SessionManagerRebuildOutcomeTests` pins that the session records it.)

Step 2 is the part that makes a startup-captured route table work. The route
list holds function values created at startup, but each of those still
dispatches to the handler's method entry point, so re-pointing the method
changes what the captured route serves. Harmony doesn't care that the
delegate was created six minutes ago. It cares where the call ends up.

### How long a save takes

Measured by the `--integration-hr` tier on a real running app: the clock starts
just before the first byte of the save is written and stops on a response the
app sent or a frame the daemon pushed (`HotReloadLatency.fs`,
`HotReloadLatencyTests.fs`). 20 saves per path after 2 warm-up saves, and the
tier fails if a path's p95 passes its bound in `TestTimeouts.fs`. The app is the
small `WebAppFixture`. The machine was an AMD Ryzen 7 5800XT, 16 threads,
Linux, .NET 11.0.0-rc.1, and the tier ran 8 times in a row on 2026-10-01 with
other jobs running on it. Each cell is the range of the 8 runs.

| What | p50 | p95 |
|---|---|---|
| A patched save, to the first response with the new body | 258 to 306 ms | 298 to 353 ms |
| The same save, to the daemon saying `Patched` | 296 to 346 ms | 338 to 392 ms |
| A save to an app `run_app` runs, to the restarted app's first response | 7.1 to 10.0 s | 7.7 to 16.2 s |

The third row is a restart, and I took those 8 runs before the metadata-delta route
existed (the route came in later the same day). With the route on, which is now the
default on master, the same kind of save is a delta and is served in 1.8 to 2.6 s
(measured separately, above). The tier's restart case has not been re-pointed at
the route-off path since, so a fresh run of it on master may measure a delta save.

About 200 ms of a patched save is the watcher's debounce (the worker says it
started compiling at 201 to 202 ms every run), so most of the time is a fixed
timer and not the patch. A restart is a rebuild, a new worker, a new FSI session
and a warm-up, and its spread follows how busy the machine is: one run on a quiet
machine had a patch at p50 259 ms and p95 261 ms, and a restart at p50 6.8 s and
p95 6.9 s. That is one
machine and one small app. I have no figure for a large app, and Microsoft
documents none for its hot reload (I re-read its Visual Studio, ASP.NET Core and
`dotnet watch` pages on 2026-10-01), so there is nothing to compare these with.
The stage-by-stage breakdown, the load runs and what I think is too slow are in
[How SageFs hot reloads F#](how-hot-reload-works.md#how-long-a-save-takes).

## What reloads, and what needs a restart

The rule I'm going for: code changes take effect, state stays, and SageFs
never does either one quietly. Every row below names the test that pins it.
The "real app" ones start a real host on .NET 10 and on .NET 11, save a real
file and read what the same process serves afterwards. The planner ones are
unit tests on the decision, and I've said which is which.

### Code

A change reaches the running app when it's a change to a **function body**, to
the body of a **lambda** the app already holds, or to a **member** of an object
it already built. The first table is the shape matrix (a named handler captured
in a route table). The second is the parity matrix: the edits .NET Hot Reload
takes for C# that the first one never asked about. Every row of the second
starts a real host on .NET 10 and on .NET 11, saves a real file and reads what
the same process serves, and it reads what the planner said about the save.

| Shape | What happens | Pinned by (real app) |
|---|---|---|
| `let handler (ctx: HttpContext) = ...` | reloads | shape matrix `plain` |
| `let handler : HttpHandler = fun ctx -> ...`, a value bound **directly** to a lambda | reloads, because F# compiles it to a method just like the line above | shape matrix `lambda` |
| a handler whose parameter type is declared in the same file | reloads | shape matrix `localType` |
| the **body** of a `static member Render x = ...` on a type | reloads | shape matrix `member` |
| a small function with no `[<MethodImpl(NoInlining)>]` | reloads | shape matrix `tiny` |
| a function the compiler inlined into its caller (`let inline`, or `AggressiveInlining`) | applied, never confirmed. The detour lands, the caller keeps its own copy of the old body, the page keeps serving the old result, and after the bound the save says the new code never ran | `InlinedCalleeOutcomeTests` (real F# inlining, real detour, real stub, real wait, in process). Not in the real-app matrix: SageFs builds with `Optimize=false`, where an `inline` function is still a call, so a real app built by SageFs does not show it |
| a function that reads or writes a `let mutable private` in its file | reloads, and it uses the app's OWN field, so reads and writes agree with the rest of the app | state tests, rule 1 `let mutable private` |

The shape matrix is `SageFs.Tests/WebAppHotReloadVerificationTests.fs` (it runs
on .NET 11).

| You save | What happens | Pinned by (real app, net10 + net11) |
|---|---|---|
| a lambda **written inline** in the route list (`"/", fun () -> ...`) | reloads. The lambda is a closure class and the list holds instances of it, so the class's `Invoke` is re-pointed | parity `inlineLambda` |
| the same lambda **capturing a value** computed at startup, body edited, captures unchanged | reloads, and the closure keeps the value it captured | parity `inlineCapture` |
| a lambda that holds a `task { }` or an `async { }` | reloads | parity `taskLambda`, `asyncLambda` |
| a function that **hands back a closure** the route table kept, and the lambda inside it changes | reloads. The function is patched and so is the closure the table holds | parity `heldClosure` |
| a named function whose body is a `task { }` or an `async { }` | reloads | parity `taskNamed`, `asyncNamed` |
| the body of an **instance member** of an object built at startup | reloads, and the object is the same one, so its fields carry on (`A#1` before the save, `B#2` after it) | parity `instance`, `instanceState` |
| a **new function** the saved code calls, with the call added in the same save | reloads. The new function is defined in FSI and the caller is patched onto it | parity `addedFunction` |
| a **new type**, or a **new value**, the saved code uses | reloads, the same way | parity `addedType`, `addedValue` |
| a function **taken out**, with the code that used it | reloads. The old one stays in the process for whatever holds it | parity `removed` |
| a function that **gains a parameter**, with its callers saved in the same save | reloads. It is a new method to the running app, and the callers move onto it | parity `signature` |
| a **generic function**, used with value types, reference types, or both | reloads in every instantiation: the ones that ran, a float or a struct that is first used (and so first compiled) after the save, and a reference type nobody named. A body that reads `typeof<'T>` gets each instantiation's own `'T` | parity `generic`, `genericRef`, `genericKind`, `genericLate` |
| a generic function **called from another generic function**, or used as a **first-class value** | reloads | parity `genericNested`, `genericClosure` |
| a **generic method** of a class, instance or static | reloads | parity `genericInstanceMethod`, `genericStaticMethod` |
| a member of a **generic type**: an instance member, a static one, or a generic method of it | reloads. Each object keeps its own type argument, and the objects built before the save run the new body | parity `genericTypeInstance`, `genericTypeStatic`, `genericMethodOnType` |

The generic rows merged on 2026-10-01 after v0.6.875 was tagged. They are on master
and not in a release yet. In v0.6.875 a save to a generic function restarts the app.

The planner only takes an edit as "just the lambdas" when nothing outside a
lambda changed (it cuts every lambda out of both versions and compares what is
left). A lambda edit is re-pointed only while the closure has room for it: the
same captured values and the same lambdas inside it. See
[the decision](decisions.md#a-lambda-in-a-route-list-reloads-by-re-pointing-its-closure-and-only-while-the-closure-has-room-for-the-change).
The same goes for an instance member: the type has to keep its fields.

**With a debugger attached.** Microsoft's pages say Hot Reload is not supported
for F# while you debug it. Mine has no such rule written down, so I tested it: a
real managed debugger (netcoredbg, pinned and checked by hash) is attached to the
process the app runs in, the process itself says a debugger is attached, and then
a lambda edit, an instance member edit, a named task and a signature change are
saved. Each lands and ends `Patched`
(`HotReloadDebuggerTests`, net10 + net11). What I did not test: stepping, a
breakpoint inside patched code, or saving an edit to a method the debugger has
stopped in. Nothing in that test sets a breakpoint.

A save of added or re-signed code is **applied**, and it is **Patched** once the
code that calls it has run. A save that only adds something nothing calls yet
ends as "not confirmed: the new code has not run", which is true of it. A caller
in **another file** keeps calling the old method until you save that file as
well; the build would not pass until you do.

### What "patched" means

Re-pointing a function is not the same as the app running its new body. The
detour can land, and the running app can still hold the old body: a caller that
had the function inlined carries its own copy and never calls the method that
was re-pointed. Every other signal (the detour is in place, the app holds the
compiled copy) reads as success in that case, so none of them is taken as proof.
The proof is the new body running.

So a save is reported in two steps:

| Step | Wire `type` | Outcome | What it means | Page |
|---|---|---|---|---|
| 1 | `pending` | `PatchPending` | The detours landed. Nothing has been seen running yet. The message says to exercise the changed code. | refreshes |
| 2a | `patched` | `Patched` | Every changed function the save still answers for has run its new body. | no second refresh |
| 2b | `neverentered` | `NeverEntered` | The bound (10 seconds, `SAGEFS_PATCH_CONFIRM_SECONDS`) passed and some changed functions have not run. The message names them and counts the ones that have. | no refresh |

An app with no traffic looks exactly like one that never calls the function, so
`NeverEntered` does not say why. It says to exercise that code path, and that a
restart picks the change up if the new code still does not run.

The same two steps, with the same wire `type`s, are what a metadata delta
reports. The `mechanism` field says which one it was: `detour` or
`metadata-delta`. A client reads that field and not the words, and it is empty for
a verdict that is not a patch.

How it is seen: the detour points at a small stub with the new body's exact
signature. The stub records an entry and then calls the new body
(`EntryProbes.fs`). The host keeps one probe per patched function, and a newer
save of the same function supersedes the older probe, so the older save does not
report a function it no longer owns. The decision itself is pure
(`PatchConfirmation.fs`). A function whose stub could not be built, and a
mutable binding's accessors, have no probe, so they are never reported as seen
running. A metadata delta has no stub to point at, since the body is replaced in
place, so the probe is the first thing written into the new body: a call that
records its entry. A method the delta only adds has no probe, because nothing runs
it until a caller does, and the caller's probe is what shows the patch live. A call
that was already inside the old body when the delta landed, and finishes
afterwards, proves nothing, and `DeltaRouteSimTests` has a twin that takes it as
proof and is caught.

`lastReload` in `get_session_status`, the `ReloadReported` event and the browser
overlay all carry these outcomes. `SessionReloadTests` pins the wire shape, and
`PatchConfirmationSimTests` is the deterministic simulation: seeded saves, calls,
replacements and bounds in every order, checking that `Patched` is only ever
produced for functions that ran. Two twins (claim `Patched` at save time, never
fire the bound) are caught by it. The state tests are `SageFs.Tests/HotReloadStateOutcomeTests.fs`
and run once per runtime.

### State

Module-level `let mutable`s are your app's live data, and a save treats them
that way:

| You... | What happens | Pinned by (real app, net10 + net11) |
|---|---|---|
| edit a function, and the file has a `let mutable` you didn't touch | its live value stays. Its initializer never runs again | rule 1, public `let mutable` |
| edit a function that uses a `let mutable private` | same, the value stays. The patch can't name a private member from FSI, so it gets a stand-in with the same name that reads and writes the app's own field. Nothing gets re-declared | rule 1, `let mutable private` |
| edit a `let mutable`'s **initializer**, same type (`= 10` to `= 25`) | the app keeps its live value, and the save says so: `kept 'App.State.tuned' = 13 (your new initializer 25 applies when you reset it)`. The page doesn't refresh, because nothing it would fetch changed | rule 3, keep |
| reset it (the **Reset** button in the dashboard's Hot Reload panel, or the `reset_hot_reload_state` MCP tool) | ONLY that initializer runs, and its value goes into the app's field. Nothing else in the file runs | rule 3, reset. The button itself is pinned by a browser journey: an open dashboard shows the kept notice without a reload, Reset is clicked, and the app serves the new value (`HotReloadBrowserTests`, `--integration-hr`, .NET 11) |
| change a `let mutable`'s **type** (`= 1` to `= "one"`) | restart needed. The live value has the old type, so there's nothing safe to keep, and the running app is left exactly as it was until you restart | rule 4, retype |

I check the type against the running app, not just your annotation. The save
asks the app what it's holding (it defines your new initializer as a function
and compares types, it never calls it), so `= 1` to `= "one"` gets caught
without a `: int` anywhere. If that check can't get an answer, it's a restart.
Nothing gets kept on a guess.

### Values

A plain `let` value you redefine (`let greeting = "hello"` to `"howdy"`) gets
its new value, as long as nothing in the running app kept a copy of the old
one. Re-pointing the value's getter is the easy part. The hard part is knowing
nobody copied the old value, because if something did, "Patched" is a lie.

So the running app tells me. When the session starts, before any of your code
runs, SageFs reads the IL of every method in your project that reads a module
value and works out what each read does with it: throws it away (`pop`, or a
local nothing reads), or lets it go somewhere (a field, a closure, an argument,
a return). Every method whose read goes somewhere gets a one-shot probe that
records the first time it runs and then comes off. While the app starts, every
getter also records who called it, which catches reads that don't show up in
anyone's IL (reflection). On a save, a value is patched only if every read that
has actually happened threw it away. After the patch it asks again, so a read
that raced the patch still counts.

| You redefine a public `let` value, and... | What happens | Pinned by |
|---|---|---|
| startup only computed it into a local it never used, and the function that returns it hasn't run yet | patched. The getter now returns the new value, the save says Patched, and the file's live state is left alone | rule 2, `greeting` (real app, net10 + net11) |
| a `lazy` reads it, and nothing has forced the lazy yet | patched. The first request forces the lazy, and it reads the new value | rule 2, unforced lazy (real app, net10 + net11) |
| startup copied it into a closure (`let atStartup = banner in fun () -> atStartup`) | restart needed, and the reason says who kept it: `the running app kept a copy of 'banner', and a patch can't reach a copy: <StartupCode$StateFixture>.$StateFixture.State..cctor (IL_0103) put it in a new StateFixture.State+handlers@88-10, while the app started`. The app still serves the old value until you restart, and the page isn't refreshed | rule 2 guard, `banner` (real app, net10 + net11) |
| a `lazy` read it after startup (a request forced it) | restart needed, naming the lazy's thunk. The Lazy cached what it read, and no patch reaches that | rule 2, forced lazy (real app, net10 + net11) |
| any code that hands it on (returns it, passes it along, stores it) has already run | restart needed. SageFs can't tell whether whoever got it kept it, so it doesn't guess | `ValueReadsTests`, and every interleaving in `ValueReadSimTests` (DST) |
| it isn't public, its annotation changed, or its assembly was built with optimizations | restart needed | planner: `ReloadPlanningTests`; evidence: `ValueReadsTests` |

That fourth row is where this falls short in practice, and it's worth saying
plainly: a page handler that renders `greeting` hands it on, so once you've
loaded that page, a save of `greeting` is a restart. Where rule 2 pays off is a
value nothing has used yet, or one that's only read and thrown away.

`ValueReadSimTests` is the deterministic simulation behind this. It runs the
real classifier, the real ledger and the real save check through 4000 seeded
interleavings of startup reads, requests, lazies forced late, the startup
window closing, probes coming off and saves landing anywhere (during startup
too), and checks that a Patched never lands on top of a copy of the old value.
Four twins put the naive rules back (ignore reads after startup, treat a stored
value like a dead local, check once and patch, record the caller after the read)
and it catches every one.

### Reflection reads

A value read through reflection (`PropertyInfo.GetValue`, `MethodBase.Invoke`,
`FieldInfo.GetValue`, a delegate made from its getter) has no read of it in
anyone's IL, so the probes can't see it. While the app starts, the getter's
own watch catches it. After that, SageFs watches the reflection entry points
themselves, and how it watches is a choice you make, because every option
costs something:

| Mode | What a reflective read costs | What an edit to that value does |
|---|---|---|
| `probe-callers` (the default) | about 40 ns once the caller's been seen. The first read from each caller walks the stack once (about 16 us) and rewires that caller's reflection calls so later reads name it for free | patched, if the caller threw the value away at that call. Restart, naming the caller, if it kept it |
| `mark-on-reflect` | about 20 ns more on every reflective call in the process. Plain reads cost nothing | restart. It doesn't look for who read it |
| `exact-every-read` | 8 to 16 us on EVERY read of a tracked value, plain reads included, for the app's life | patched or restart, like probe-callers, naming the caller |

Measured in the REPL on a loaded box (read-tracking-costs.md has the method and
the raw numbers), so treat them as orders of magnitude. A plain `let` read
through its getter is 3 ns, and only `exact-every-read` touches that.

When to pick which:

- **`probe-callers`** almost always. It's the precise one and it's nearly free.
  It falls back to a walk (16 us) for a reflection call it can't rewire (an API
  other than the `GetValue`/`Invoke` family, a generic method, a static
  initializer) and for a loop that never returns: rewiring a method changes its
  NEXT call, so a `while running do ...` that runs for the app's whole life keeps
  walking.
- **`mark-on-reflect`** if the reads are hot and you don't care about editing
  those values live. It's the cheapest, and it never guesses who read what.
- **`exact-every-read`** only to chase something down. It also sees reads that
  don't go through a reflection entry point at all: a compiled expression tree
  or a function pointer calling the getter directly. The other two modes don't
  see those.

Set the mode new sessions start in with the `hotreload.reflectionReadMode`
setting (the dashboard's settings panel, global or per repo). Switch a running
app from the Hot Reload panel or with the `set_reflection_read_mode` MCP tool.
Switching takes effect on the next read, with no restart. Moving to
`exact-every-read` puts the getter watches back on, except on a getter hot
reload already re-pointed (patching it again would undo that), which the entry
watch keeps covering.

**When a reflective loop gets hot** (1,000 reads of one value inside a second),
SageFs asks you, once per value, on the dashboard's Hot Reload panel and in
`set_reflection_read_mode`: which value, which caller, how fast, what the
current mode is costing you, and each choice with what it does. Pick one and
the question's answered.

One thing I found building this that you should know about: on .NET, a patch
on a runtime method that hasn't been recompiled yet gets thrown away when
tiered compilation recompiles it, and it doesn't come back. I measured it in
the REPL: 4,000 of 12,000 calls to `MethodBase.Invoke` still hit the patch,
and the other 8,000 didn't. SageFs checks for this. A canary read goes through
every entry point at every save, and if the watch ever stops seeing reads,
every value that session tracks restarts on its next edit until the app
restarts, and the panel says why. A read SageFs might have missed never gets a
Patched.

So you get a choice, the `hotreload.tieredCompilation` setting. It's in the
dashboard settings panel, global or per repo, and it applies the next time a
session starts, because it's an environment variable the host reads once when
it starts up.

`tiering-off-while-watching` is the default. It turns tiered compilation off
for the process running your app, so nothing gets recompiled and the watch
can't lapse. Your app pays for it: every method gets the fully optimized JIT
on its first call, and dynamic PGO never runs.

`keep-tiering` leaves the runtime alone. Your app runs at the runtime's normal
speed, but the watch can lapse, and after a lapse every tracked value restarts
on its next edit until the app restarts. You get more restarts, never a wrong
answer.

Here's what that cost looked like on my machine, for the hot reload test app
on net11 (5 runs each, taking turns, median):

| | App start | Per request, warm |
|---|---|---|
| `tiering-off-while-watching` | 8.5s | 340 µs |
| `keep-tiering` | 7.8s | 563 µs |

Tiering off costs about 0.7s at startup. Requests actually came out faster
with it off, because 2,200 requests in a couple of seconds isn't enough for
the runtime to finish promoting code to its optimized tier. A long-running app
with tiering on would catch up, and past that point PGO could win. For the
edit loop, where the app gets restarted a lot and rarely runs long, off is the
better deal, so that's the default. If your app does heavy work that you're
measuring, turn tiering back on for that repo.

I checked whether `exact-every-read` could skip this. Its getter watch covers
a plain call or a `PropertyInfo.GetValue` on a tracked value by itself. But a
`FieldInfo.GetValue` read of the backing field, or turning the getter into a
delegate, only ever shows up through the entry watch, in every mode. So the
setting applies the same way to all three modes.

Pinned by `ReflectionReadTrackingTests` (a real emitted app, including a
forced lapse), `ReflectionReadSimTests` (DST: 3000 seeded runs of reflective
reads from many callers, reads inside other reflective calls, threads, mode
switches, saves and injected lapses under `keep-tiering`: never Patched over a
copy, never Patched while lapsed, never a read filed under the wrong caller)
and the real-app rule 2 reflection tests in `HotReloadStateOutcomeTests`
(net10 + net11), including one that hammers a real app with `keep-tiering`
set and checks that a real lapse fails closed.

### Restarts

Anything that takes effect at **startup** can't be patched into a process
that's already started, and SageFs restarts the app instead (or, if SageFs
isn't the one running your app, tells you a restart is needed):

| Shape | Why not | Pinned by |
|---|---|---|
| a value computed once at startup and closed over, like `let getHome : HttpHandler = Response.ofHtml (pageLayout [])` or `let h = let x = compute () in fun () -> x` | it ran at module initialisation and the route captured the result, so nothing is called per request | shape matrix `eager` (real app) |
| an immutable value the running app kept a copy of | see the values table. It's never reported as patched, and the reason names who kept it | rule 2 guard, `banner`, and the forced lazy (real app, net10 + net11) |
| a `let mutable` whose type changed | see the state table | rule 4 (real app, net10 + net11) |
| a function that uses a **private function, value or type** in its file | FSI would need that member's code, not just a field, and a patch can't see private members. Private `let mutable`s are fine (see above) | planner: `ReloadPlanningTests` "carried live state" |
| a type whose **fields, cases or members** were added, removed or re-typed | the compiled assembly's shape no longer matches, and live instances were laid out by the old definition | planner: `ReloadPlanningTests`, `ReloadPlanningDecisionMutationTests` |
| the **entry point**, a bare expression that runs at startup, a module alias, added or removed | it takes effect when the process starts | planner: `ReloadPlanningTests` |
| a lambda that **starts capturing** something it did not, or gains or loses a lambda inside it | the closures the app already built have no room for the change. It says `ClosureShapeChanged` and names the field | parity `inlineNewCapture` |
| an instance member that starts reading a **constructor argument** (the compiler adds a field) | the objects the app already built do not have it. It says `InstanceLayoutChanged` and names the field | parity `instanceNewField` |
| a **generic function** in a program that can make instantiations by **reflection** (`MakeGenericMethod` anywhere in your code, or `MakeGenericType` while a generic type or method reaches the function), including a delegate bound straight to a generic method | the runtime compiles a generic function once per value type and once for all reference types, and SageFs patches every body it can find in your code. Reflection can make one that no code names, so a patch could leave it on the old body. It says `GenericInstantiationsUnknown` and names what it saw. If the function only has to work for one type, annotate its arguments with it and it is re-pointed like any other | parity `genericReflection`, `genericDelegate` |
| a generic function with a **byref parameter**, a **struct return**, a member of a **generic struct**, or a static generic method of a generic class used with a reference type | the stub that carries the exact instantiation cannot take an address or a return buffer, and nothing at that call names the class. It says `GenericInstantiationsUnknown` | `GenericReloadTests` (struct return) |

Each of those leaves the running app exactly as it was, and the whole save with
it: one refusal anywhere in a save stops every detour of it.

If a handler isn't picking up edits, check whether its value is **computed**
rather than **a function**: `let getHome : HttpHandler = fun ctx -> ...` and
`let getHome (ctx: HttpContext) = ...` both reload;
`let getHome : HttpHandler = Response.ofHtml (...)` is built once and won't.
This trips people up constantly and it's almost always this.

### Build without optimizations

Hot reload re-points **methods**. The F# compiler's Release optimizer inlines
small functions into their callers (including into the closures a route
table captures at startup), so in an optimized build there's often no call
left to re-point: the patch lands on a method nothing calls anymore. SageFs
builds your project with `-p:Optimize=false` for exactly this reason, so a
session SageFs built is covered. **If you build Release by hand after
starting the session**, that optimized assembly is what gets loaded and
edits to inlined functions won't reach the running app. Rebuild through
SageFs (`hard_reset` with `rebuild: true`, or the dashboard's HARD_RESET).

I don't trust a claim in these tables that isn't backed by a test, and for
anything about what the app actually does I want one that starts a real
process. It's too easy to convince yourself something works when it doesn't.

One requirement: the baseline is the source your loaded assembly was
built from, so build the project before starting the session. A source file
edited after its last build gets re-evaluated whole instead of patched, and
SageFs logs that it's doing so.

### File watching

A save only reaches SageFs if something is watching the directory. The daemon
roots one recursive watcher at each session's working directory, and
`bin`, `obj`, `.git`, `node_modules`, `.runs`, `artifacts` and nested checkouts
are filtered out of its events (`SageFs.Core/FileWatcher.fs`,
`startPrunedWatcherUnder`).

A working directory that is your home directory, one that contains it, or a
filesystem root is refused, and nothing is watched there. A recursive watch
costs an inotify watch per directory, and a daemon started in `$HOME` would hand
`$HOME` out as a session root. The refusal is not quiet. It is logged, and it
shows up in `/health` under `componentFailures` as `file-watcher:<directory>`
with the reason and a hint: open the session in a project directory, not the home
directory or a filesystem root, and restart it. That hint also says hot reload and
live testing get no file events for the session until you do. The check compares
whole path segments, so `/home/william` is not mistaken for `/home/will`, and a
dotfiles repo at `$HOME` is refused as well, since the cost is the directory
count whether or not `.git` is there.

The other failure you can hit is Linux running out of inotify instances. That is
reported the same way, with a hint to raise `fs.inotify.max_user_instances`.

## Where it falls short right now

I'd rather you hear this from me than find it at 11pm.

- **A lambda edit in a project you built Release by hand restarts.** SageFs
  builds your project with optimizations off, and its FSI compiles patches with
  `--optimize-` to match. An assembly you built optimized has closures of
  another shape (a `task { }` is a static state machine, a captured constant is
  folded away), so the new lambda can't be matched to the old one and the save
  says `ClosureShapeChanged`. Rebuild through SageFs and it holds.
- **A caller in another file keeps the old method after a signature change.**
  The saved callers move onto the new method; one you haven't saved yet still
  calls the old one, until you save it.
- **A generic function is patched in every body SageFs can find, and "found"
  means read from your code.** One `MakeGenericMethod` call anywhere in the
  assemblies that can name the function turns every generic edit in them into a
  restart, because that call can make an instantiation no code names. That is
  coarse on purpose. And `Patched` for a generic function means a new body was
  seen running and every body was patched in the same save, not that every
  instantiation has been called since.
- **Adding a member to an existing type is a restart on the detour route.**
  Microsoft's mechanism supports it. The detour route treats any change to a type's
  member list as a shape change. The metadata-delta route for a `run_app` app takes
  a method added to a type the app already runs, and restarts for an added field,
  a virtual member or a constructor.

- **A redefined value restarts once anything that hands it on has run.**
  Load the page that renders `greeting`, then edit `greeting`, and it's a
  restart: the handler returned the value to something SageFs can't follow.
  It never says Patched when it can't prove it, so you lose a restart, not the
  truth.
- **Some reads through reflection still aren't seen after startup.** The
  entry points SageFs watches cover `GetValue`, `Invoke` and delegates made
  from a getter. A compiled expression tree or a function pointer that calls the
  getter directly isn't one of them, unless you're in `exact-every-read`. See
  [Reflection reads](#reflection-reads).
- **Code you eval in the REPL that reads a value usually counts as a copy of it**
  (an eval that runs code counts as having read it), so a
  value you've poked at in the REPL restarts on its next edit.
- **When SageFs isn't the one running your app and a save needs a restart**
  (a signature or type change, say), SageFs re-evaluates the whole file
  instead, and that re-declares every `let mutable` in it. Your live state in
  that file is reset. The outcome says restart-required, which is true, but it
  doesn't say your state went with it. Start the app with `run_app` and SageFs
  patches it by metadata delta, or restarts it properly and says why.
- **A metadata delta changes the app, and not the REPL.** The delta goes into the
  process the app runs in, the worker. The FSI host, where `send_fsharp_code` and
  live tests run, keeps the code of the last build until the session restarts or you
  `hard_reset_fsi_session` with `rebuild`. A REPL call to a function you just saved
  runs the old body. I haven't built the second delta that would update it.
- **The build is the floor.** A save to a `run_app` app waits for `dotnet build`,
  1.6 to 2.4 seconds on my fixture, and everything after it is milliseconds. A
  detour has no build to wait for.
- **A debugger on the app's process, or a Harmony patch on a method the save
  rewrites, restarts.** The runtime refuses an update under a debugger, and a
  Harmony patch wraps the body a delta would replace. Both are named in the restart
  (`MetadataDeltaUnavailable`) before the runtime is asked.
- **A project built Release by hand can't take a delta.** The runtime edits only an
  assembly built without optimizations, which is what SageFs's own build makes.
- **The reset button runs just the initializer.** If the initializer uses
  something private in its file, the save can't check it and you get a restart
  instead of a kept value.

## What I'm working on

In the order I'm doing them. None of them is done until a real-app test proves
it on both .NET 10 and .NET 11.

Done already: a redefined immutable value gets its new value when nothing in
the running app kept a copy (the values table above). The running app says
where every read of the value went, so a Patched is never a guess.

Also done: an app started by `.SageFs/init.fsx` `#load`ing your sources
used to restart on an edit (.NET 10) or get patched on the wrong copy while
still saying "Patched" (.NET 11). The init script's `#load` skipped the
hot-reload middleware, so SageFs never learned which copy of a function the
app was holding. Now it tracks that, patches the copy the app holds, and never
reports "Patched" when it couldn't find it.

1. **Handlers that render a value.** A value a handler hands on is a restart
   once the handler has run (see above). Following the value past the
   handler, into the response, would let those patch too. That needs to know
   the response doesn't keep it, and I'd rather prove that than assume it.

It's the same place Flutter, React Fast Refresh and Clojure's `defonce` ended
up, and I think they got it right. The one thing people complain about in
Flutter is that it keeps state without telling you, which is why a kept value
always comes with a notice.

## Browser auto-refresh (DevReload)

Web apps need no extra configuration. SageFs auto-injects DevReload
middleware into your ASP.NET pipeline via
[Harmony](https://github.com/pardeike/Harmony), with no code changes on your
part. Your Falco/ASP.NET app gets browser auto-refresh once SageFs is
running. When a compile fails, an accessible error overlay appears in the
browser with source context and editor links, and the page reloads
automatically once the error is fixed. A save that only kept live state doesn't
refresh the page, since nothing it would fetch changed.

Set `SAGEFS_DEVRELOAD=0` (or `false`) to disable auto-injection.

The VS Code extension gives per-file and per-directory hot reload toggles.

See [HOT_RELOAD_STATUS.md](internal/HOT_RELOAD_STATUS.md) for the full
technical details, if you want to go spelunking.
