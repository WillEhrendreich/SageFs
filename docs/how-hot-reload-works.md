# How SageFs hot reloads F#

You save an F# file while your app is running, and the app starts running your new function bodies in the same process, with the counter you bumped and the cache you filled still where they were. When a save can't be patched in place, SageFs says so and names the reason, and it only says "patched" after it has watched the new code run.

This page describes the tree at `e47b355b`, which is master on 2026-10-01, and every source link is a permalink to that commit. The last release, v0.6.870, predates the work on closures, instance members, added, removed and re-signed declarations, and the generic refusal that this page describes. That work was merged on 2026-10-01 and is in the next release. Statements about Microsoft's tools are dated 2026-10-01, because that's the day I checked and they're moving.

## What I borrowed

Erlang keeps two versions of a module alive at once. A fully qualified call (`m:loop()`) always lands in the newest, and process state lives in the process, not the module, so a code swap leaves it alone. When the shape of the state has to change, `gen_server` gives you a `code_change/3` hook ([code loading](https://www.erlang.org/doc/system/code_loading.html), [gen_server](https://www.erlang.org/doc/apps/stdlib/gen_server.html)). Elixir inherits that and adds the trigger: `r/1` and `recompile/1` in IEx, and Phoenix recompiling on the next request ([IEx.Helpers](https://iex.hexdocs.pm/IEx.Helpers.html), [Phoenix.CodeReloader](https://phoenix.hexdocs.pm/Phoenix.CodeReloader.html)).

Clojure sends every call to a named function through a Var, so redefining the function is a pointer swap the caller never notices ([Vars](https://clojure.org/reference/vars)). The REPL guide names the catch: code that captured the function value instead of the Var keeps the old one, and a value derived from a redefined Var stays derived from the old one ([REPL workflow](https://clojure.org/guides/repl/enhancing_your_repl_workflow)). Common Lisp's standard has the inlining version. A file compiler may assume a call to a function defined in the same file means that function, and if you redefine it at run time "the consequences are unspecified" ([HyperSpec](https://www.lispworks.com/documentation/HyperSpec/Body/03_bbc.htm)).

Lisp and Smalltalk images treat the running program as one live heap the tools can reach into, with a protocol for what happens to existing instances when a class changes ([CLHS 4.3.6](https://www.lispworks.com/documentation/HyperSpec/Body/04_cf.htm), [Pharo](https://pharo.org/features)). Flutter keeps your state across a reload and treats globals and static fields as state, so it doesn't re-run their initializers ([Flutter](https://docs.flutter.dev/tools/hot-reload)).

What I take from the lot is one property: a call goes through something the tool can re-point, and state lives somewhere the re-pointing doesn't touch. Every system above also has a section on things that copied what you swapped, because that's where the bugs live. On .NET the thing you can re-point is a method's entry point, and the copies are callers that inlined the old body and values computed once at startup. Most of this page is about those two.

## What F# and .NET don't hand you

As of 2026-10-01 I know of no shipped Microsoft tool that hot reloads F# in place. The Visual Studio page says "Hot Reload, or Edit and Continue, is not supported when you debug F# code" ([VS](https://learn.microsoft.com/en-us/visualstudio/debugger/hot-reload)). For `dotnet watch`, Microsoft's own issue says "F# projects always fall back to rebuild and restart" ([dotnet/sdk#55464](https://github.com/dotnet/sdk/issues/55464), still open when I looked on 2026-10-01). F# edit-and-continue is being built in the open, though, by a community contributor (Nat Elkins) with the F# team reviewing. [dotnet/fsharp#19941](https://github.com/dotnet/fsharp/pull/19941) was opened 2026-06-12 and last updated 2026-09-28, and on 2026-10-01 it is open and not merged. It sits behind `--test:HotReloadDeltas`, which its description says is off by default. That description lists method edits, member additions, closures, state machines and generic methods as supported, says unsupported edits require a rebuild, and mentions a standalone `fsharp-watch` preview. I haven't run any of it.

Then the F# specifics. A module compiles to a class of static members, and module-level `let` values are computed by a static initializer ([modules](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/modules), [spec](https://fsharp.github.io/fslang-spec/program-structure-and-execution/)). A Falco or Giraffe route table is a list built once at startup out of handlers. That's the Clojure captured-value problem with a different accent.

FSI makes it harder. Every submission is wrapped in its own `FSI_NNNN` type, and by default each lands in its own dynamic assembly ([`HotReloadCore.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L800-L809)). To keep re-pointing the same function save after save, everything has to live in one assembly, which is `--multiemit-`, and the price is that you can't redefine a `type` in the REPL (FS0037). SageFs puts that tradeoff in the workflow type so the illegal combination can't be built ([`WorkflowTypes.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/WorkflowTypes.fs#L1-L9), [`ProjectLoading.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/ProjectLoading.fs#L1206-L1211)).

And the optimizer. The F# compiler and the JIT both inline small functions, and a patch on a function nothing calls anymore changes nothing. That's the Lisp footnote on your machine.

## One save, in the order the data moves

```
 save Foo.fs
    |
    v
 file watcher (200 ms settle, 500 ms duplicate-save guard)
    |
    v
 is there a baseline (the source your assembly was built from)?
    | no  -> re-evaluate the whole file (resets the let mutables in it)
    v yes
 plan: diff declarations against that baseline
    |-- function bodies changed ----------> patch
    |-- lambdas inside a value changed ---> patch the closures the app holds
    |-- declaration added, removed, or
    |   function re-signed -----------------> patch (new code defined in FSI)
    |-- let mutable, same type -----------> keep the live value, say so
    |-- public let value redefined -------> ask the running app who copied it
    |-- type shape, startup code, ... ----> restart, with a named reason
    v
 emit ONLY the changed functions, opened onto the compiled module
    v
 FSI evals it (NoInlining injected, compiled --optimize-)  -- type error --> nothing changes
    v
 match old closures and objects to the new code
    |-- a closure or object that no longer fits, or a generic function
    |   --> refuse the WHOLE save: restart, app left as it was
    v
 pair each old method with its new one, detour through a probe stub
    v
 PatchPending  (page refreshes)
    v
 your app calls the function --> stub records "entered"
    v
 Patched      (or NeverEntered after 10 s, naming the functions)
```

### Before the first save

SageFs builds your project with `-p:Optimize=false`, which stamps the assembly so the JIT won't inline across it, and a regression test stops anyone "cleaning that up" ([`SessionBuild.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/SessionBuild.fs#L110-L122)). A hand-built Release assembly is spotted from its `DebuggableAttribute` ([`BuildOptimization.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/BuildOptimization.fs#L8-L22)).

It records a baseline: each source file's declarations as they were when the loaded assembly was built. A file newer than the assembly has no trustworthy baseline and gets re-evaluated whole instead of diffed ([`WorkerMain.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Host/WorkerMain.fs#L1922-L1940), [`ReloadPlanning.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/ReloadPlanning.fs#L883-L889)). And it reads the IL of every method that reads a module value, before any of your code has run. The values section says why ([`ValueReadTracking.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/ValueReadTracking.fs#L1-L32)).

### The watcher and the plan

The watcher waits 200 ms for an editor's burst of write events to settle and drops another event for the same file within 500 ms as a duplicate ([`Timeouts.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Timeouts.fs#L394-L401)).

Then the planner parses both versions and compares declaration by declaration. A function with a changed body is a patch, and so is one whose signature changed: the old method stays for whatever holds it, and the callers saved in the same save move onto the new one. A declaration the running build never had (a function, a type, a value) needs no compiled original, so it's defined in FSI and the saved code that uses it is patched to call it. A removal leaves the old declaration in the process, since whatever stopped using it was saved with it. A type whose shape (fields, cases, member signatures) is unchanged and only member bodies moved is a patch. A value that changed only inside its lambdas is a patch too, and the next section says how. A `let mutable` with the same header is "keep". A public `let` value with the same header is "redefine", which means the plan says yes and the running app gets asked ([`outcomeOf`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/ReloadPlanning.fs#L831-L881), [removals and additions](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/ReloadPlanning.fs#L541-L568), [`planReload`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/ReloadPlanning.fs#L1235-L1291)). What still restarts is the entry point, a bare expression that runs at startup, a nested module added or removed, a changed type shape (which includes adding a member to an existing type) and a mutable that changed type. The reasoning for added, removed and re-signed code is in [decisions.md](decisions.md#a-save-that-adds-removes-or-re-signs-a-declaration-lands-without-a-restart).

If SageFs started the app with `run_app`, the app runs in the worker and the patching agent lives in the FSI host, so a patch lands in a copy the app never calls. Until 0.6.845 that said "Hot reloaded 1 of 1" and changed nothing. Now such a save is a restart that says why ([`AppPlacement.adjust`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/ReloadPlanning.fs#L1498-L1529), [`docs/hot-reload.md`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/docs/hot-reload.md#L28-L48)). Start the app from FSI or an `init.fsx` and it patches in place.

### Emitting only what changed

This is the step that makes the startup-captured route table work. Re-evaluating the whole file redeclares the types that file defines. A handler whose signature mentions one now has a parameter type from FSI's assembly while the compiled method wants the project's, so the detour pairing fails on parameter-type equality and the app keeps calling the old body. The browser still refreshes, so it looks like it worked ([`ReloadPlanning.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/ReloadPlanning.fs#L1542-L1560)).

So the patch is only the changed functions, written inside the file's module path with `open global.X.Y` on the compiled module, which binds the new bodies to the running app's own types. `#` line directives keep compiler errors on your source lines ([`CompilationContext.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/CompilationContext.fs#L632-L684)). A captured route still dispatches to the handler's method entry point, so re-pointing the method changes what the route serves. Harmony doesn't care that the delegate was created six minutes ago ([`docs/hot-reload.md`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/docs/hot-reload.md#L83-L87)).

### Eval and detour

The eval goes through a middleware that injects `[<MethodImpl(NoInlining)>]` on the functions being re-emitted, then asks the agent to register what the eval defined and detour it ([`HotReloading.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloading.fs#L138-L211), [`HotReloading.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloading.fs#L229-L262)). If the buffer doesn't compile nothing is detoured, and the overlay shows the error. A hot reload session compiles its FSI patches with `--optimize-`, to match the project, which SageFs builds with `Optimize=false`. Optimized, a `task { }` becomes a static state machine where the build made a chain of closures, and a captured constant gets folded into the closure, so the patch would never match the code it replaces ([`ProjectLoading.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/ProjectLoading.fs#L1212-L1219)).

The agent walks the new dynamic assembly, collects the static methods and the instance methods a class declares itself, strips the `FSI_NNNN` segments so qualified names match the compiled ones, and pairs each old method with a same-named compatible new one ([`getAllMethods`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L88-L110), [`planDetours`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L359-L373)). Generic methods are registered so a save can name them, and are never detoured, for the reason in the section on generics below ([`compatibleForDetour`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L1245-L1288)). Only methods first defined by this eval are targets. I learned that one loudly: pairing two older copies both ways made their entry points jump to each other forever, 100% CPU, can't suspend. It also tracks which copy your app actually holds, because under `--multiemit-` the one FSI assembly piles up a same-named copy per eval and re-pointing the wrong one changes nothing ([`handleNewAsmFromRepl`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L1296-L1447)). With no evidence the app's own copy moved, a save can't be reported as patched.

The detour itself is Harmony's `PatchTools.DetourMethod` with MonoMod underneath, which overwrites the first bytes of the JIT-compiled code with a jump ([`detourMethod`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L271-L312)). A canary compares the machine code before and after. It said "unchanged" on reloads that demonstrably worked, so it's a warning and not a verdict ([comment](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L748-L763)). On .NET 11 all of this works only because I run a patched MonoMod ([`Directory.Packages.props`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/Directory.Packages.props#L46-L54)).

### Lambdas, instance members and generics

A lambda written inline in a route list (`get "/" (fun ctx -> ...)`) has no name to re-point. The compiler turns it into a closure class, and the list holds instances of it. The class has an `Invoke`, and the instances the app already built still run it, so a save that only changes lambda bodies detours the old class's `Invoke` to the new one's. The planner only reads a save as "just the lambdas" when nothing outside a lambda changed: it cuts every lambda out of both versions and compares what's left. A value edited only in its lambdas is emitted as a function, so FSI compiles the same closures and defining a function runs nothing ([`lambdaDiff`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/ReloadPlanning.fs#L715-L731), [`asFunction`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/ReloadPlanning.fs#L746-L763)). The host then matches old closures to new ones by the name the compiler gave them (binding plus source line) and applies nothing until every one fits ([`planClosures`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L1103-L1145), [`planClosureWork`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L1158-L1178), [`applyClosureWork`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L1180-L1243)).

The new `Invoke` is handed an OLD closure and reads what it captured by field offset, so it's only sound when both classes have the same fields in the same order with the same types ([`layoutFit`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L1062-L1076)). A lambda that starts capturing something, or gains or loses a lambda inside it, restarts and says `ClosureShapeChanged` with the field it saw. That's the line Microsoft draws for C#, for the same reason. Two lambdas on one source line can't be told apart by name, so that edit restarts. The full reasoning, and what I measured about the compiler's closure names, is in [decisions.md](decisions.md#a-lambda-in-a-route-list-reloads-by-re-pointing-its-closure-and-only-while-the-closure-has-room-for-the-change).

An instance member is a method like any other. The object the app holds calls it, so detouring the old method to the new one reaches that object, and its fields (a counter, a cache) carry on. Same condition as closures: a member that starts reading a constructor argument gives the type a field the live object doesn't have, and that restarts and says `InstanceLayoutChanged`. Only what a class declares itself counts: not the `ToString` and `Equals` every type inherits, not the members the compiler writes for a record or union, not a struct's, not a generic type's ([registration](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L99-L110), [the refusals](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L1380-L1409), [decisions.md](decisions.md#an-instance-member-reloads-by-re-pointing-it-and-the-object-keeps-its-fields)).

A generic function is the one place I stop. The runtime compiles it once per instantiation that runs. I measured it against the Harmony I ship: detouring the open definition throws, and detouring a closed instantiation changes that instantiation and nothing else, so a call with a type that hasn't run yet is compiled from the old IL afterwards. A patch would be right for the calls that already happened and wrong for a later one, which is the kind of "Patched" this tool exists not to say. So a save that edits a generic function the app holds is refused with `GenericFunction`, naming the function, and the whole save is refused with it ([decisions.md](decisions.md#a-generic-function-restarts-and-says-so-because-a-detour-of-a-generic-function-reaches-only-part-of-it)). Microsoft's mechanism edits the method in place and doesn't have this problem. I do.

The rows that prove all of this are `SageFs.Tests/HotReloadParityTests.fs`: one real app per row, on .NET 10 and .NET 11, each ending in `Patched` after the new body ran or in a restart that names the right reason ([rows](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Tests/HotReloadParityTests.fs#L63-L182)). They run in the host tier (`--integration-host`), not in the default suite.

### State

A module-level `let mutable` compiles to a getter and setter pair over a backing field, and every read and write goes through them. That's why a detour reaches mutable state at all, and why half a redirect is worse than none. I measured it: redirect only one leg and every write is silently lost, whichever leg it was, and nothing throws. So one binding's getter and setter live in a single record that can't hold one without the other ([`HotReloadCore.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/HotReloadCore.fs#L375-L395)).

A fresh copy of the module in FSI's assembly would have its own fields, with your counter back at zero. Microsoft's mechanism doesn't have this problem, because the edit happens inside the same assembly. For public mutables the `open global.X.Y` from the emit step binds the patch to the app's field. A private one can't be named from FSI, so it gets a stand-in: a private type with a same-named static property that reads and writes the app's own field by reflection ([`LiveStateEmit.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/LiveStateEmit.fs#L142-L174)). Edit a mutable's initializer at the same type and the app keeps its live value and the save says so (`kept 'App.State.tuned' = 13 ...`). A Reset in the dashboard, or the `reset_hot_reload_state` tool, runs only that initializer. Change its type and it's a restart, with the running app left exactly as it was.

### Values

A plain `let greeting = "hello"` changed to `"howdy"` is the hard one. Re-pointing the getter is easy. Knowing nobody copied the old value is the job, because if something did, "patched" is a lie.

So the IL does the talking. At session start SageFs decodes every method that reads a module value and classifies each read as thrown away (a `pop`, or a local nothing reads) or escaped (a field, a closure, an argument, a return). Each escaping reader gets a one-shot probe that records the first time it runs. While the app starts, every getter also files its caller, which catches reads that appear in nobody's IL, meaning reflection ([`ValueReads.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/ValueReads.fs#L1-L24)). On a save the value is patched only if every read that has actually happened threw it away, and the evidence is taken again after the patch to catch a read that raced it ([`verdictOf`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/ValueReads.fs#L451-L465), [`checkSave`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/ValueReads.fs#L819-L832), [`WorkerMain.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Host/WorkerMain.fs#L1345-L1373)). Otherwise it's a restart that names who kept it.

The reflection watch has a cost. A Harmony patch on a runtime method is lost when tiered compilation recompiles it, so by default the app process runs with `DOTNET_TieredCompilation=0` ([`ValueReadTracking.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/ValueReadTracking.fs#L1596-L1622)). On my hot reload test app that was about 0.7 s of startup, median of five ([`docs/hot-reload.md`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/docs/hot-reload.md#L327-L341)). It's a setting.

### Saying "patched" when it is

A detour landing proves nothing about what your app does, since a caller that inlined the old function never enters the method I re-pointed. So the detour points at a small `DynamicMethod` stub with the new body's exact signature, which records an entry under a probe id and then calls the new body ([`EntryProbes.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/EntryProbes.fs#L9-L20), [`stubFor`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Middleware/EntryProbes.fs#L172-L199)). When the detours land the save is `PatchPending` and the page refreshes. When the stub fires it's `Patched`. If ten seconds pass (`SAGEFS_PATCH_CONFIRM_SECONDS`) with some functions never entered, it's `NeverEntered` and names them. A newer save of the same function supersedes the older probe ([`PatchConfirmation.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/PatchConfirmation.fs#L105-L124), [`step`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Features/PatchConfirmation.fs#L173-L190), [`Timeouts.fs`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/SageFs.Core/Timeouts.fs#L390)). An idle app looks exactly like a function nothing calls, so `NeverEntered` can't say which. It tells you to exercise the code path.

A new declaration has no probe, because nothing runs it until a caller does, so it counts as applied and the caller's probe is what makes the save `Patched`. A save that only adds something nothing calls yet ends as "not confirmed: the new code has not run", which is what's true of it.

The decision is pure, and `PatchConfirmationSimTests` throws seeded saves, calls, replacements and timeouts at it in every order and checks that `Patched` only appears for functions that ran. Two deliberately wrong twins (claim `Patched` at save time, never fire the bound) are caught.

## Compared with .NET hot reload

Microsoft's mechanism (`dotnet watch`, Visual Studio, C# Dev Kit) is Roslyn emitting metadata, IL and PDB deltas that the runtime applies to the loaded assembly with `MetadataUpdater.ApplyUpdate` ([docs](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.metadataupdater.applyupdate)). Same assembly, same types, same static fields. That's a better mechanism than a detour: no second copy of a module, no state stand-ins, no inlined caller to catch.

Until the 2026-10-01 merges, most of the rows below said Microsoft was ahead and my answer was "restart". Several are closed, so the table says, row by row, what the code and the tests do now.

| | Microsoft (C#) | SageFs (F#) |
|---|---|---|
| F# | Not supported in VS, and `dotnet watch` restarts ([VS](https://learn.microsoft.com/en-us/visualstudio/debugger/hot-reload), [sdk#55464](https://github.com/dotnet/sdk/issues/55464)). Compiler work in progress ([dotnet/fsharp#19941](https://github.com/dotnet/fsharp/pull/19941), open and unmerged on 2026-10-01) | Patches in place today, with the limits below |
| Inline lambda in a route list | Lambda body edits supported when the signature and captured variables stay the same ([Roslyn](https://github.com/dotnet/roslyn/blob/main/docs/wiki/EnC-Supported-Edits.md)) | Patched under the same condition. A lambda that starts capturing something restarts with `ClosureShapeChanged`. Rows `inlineLambda`, `inlineCapture`, `inlineNewCapture` in `HotReloadParityTests.fs`, on .NET 10 and 11 |
| async and task bodies | Not compared here, I didn't check Roslyn's list | Named and inline `task { }` and `async { }` bodies are patched (rows `taskNamed`, `asyncNamed`, `taskLambda`, `asyncLambda`). A project you built Release by hand has closures of another shape and restarts |
| Added, removed and re-signed code | Adding methods, fields, properties and types is supported. Signature changes are supported per the Roslyn wiki, while the Learn page still lists "Modify signatures" ([Learn](https://learn.microsoft.com/en-us/visualstudio/debugger/supported-code-changes-csharp)) | A new function, type or value lands, a removed function lands, and a function that gains a parameter lands with its callers saved in the same save (rows `addedFunction`, `addedType`, `addedValue`, `removed`, `signature`). Adding a member to an existing type, or changing a type's shape, restarts. A caller in another file keeps the old method until you save that file too |
| Instance members | Supported | Patched while the type keeps its fields, and the object is the same one (rows `instance`, `instanceState`). A member that needs a new field restarts with `InstanceLayoutChanged` |
| Generics | Supported (generics on .NET 8+) | Restart, naming the function (`GenericFunction`, row `generic`), because a detour reaches only the instantiations that have run |
| "Did it take effect" | The agent acknowledges the apply, and the SDK's own notes say an acknowledged update is treated as applied ([AGENTS.md](https://github.com/dotnet/sdk/blob/main/src/Dotnet.Watch/AGENTS.md)) | Pending, then patched only after the new body ran |
| With a debugger attached | Not supported for F#. C# supports edits in break mode and active statements | A save lands and ends `Patched` with netcoredbg attached to the process the app runs in (`HotReloadDebuggerTests.fs`, four rows, .NET 10 and 11, Linux x64). Nothing sets a breakpoint, steps, or edits a method the debugger is stopped in |

For the handler case, ASP.NET's page says route creation is run-once "unless the code update is to a route handler delegate" ([ASP.NET](https://learn.microsoft.com/en-us/aspnet/core/test/hot-reload)), so C# has the same rule. A Falco handler written `let handler (ctx: HttpContext) = ...` gets you save, refresh, see it, with the counter still bumped. A value computed once at startup and captured (`let h = Response.ofHtml (...)`) restarts in both worlds.

I found no Microsoft counterpart for two things I do: proving no copy of a redefined value exists before patching it, and waiting for the new body to run before saying so. The confirmation exists because a detour can be bypassed, so it's me covering for a weaker mechanism. The value analysis is different. Startup logic is run-once there, the docs say so, and I can't find anything that tries to patch it.

Where Microsoft is still ahead, and what I plan about each. Every plan below was written on 2026-10-01 and none has a ship date yet.

- Generics. A restart that says why. The plan is to keep it a restart unless a way to detour every instantiation, present and future, turns up ([decisions.md](decisions.md#a-generic-function-restarts-and-says-so-because-a-detour-of-a-generic-function-reaches-only-part-of-it)).
- Adding a member or a field to an existing type. It restarts, and I haven't planned the work. The entry in [decisions.md](decisions.md#an-instance-member-reloads-by-re-pointing-it-and-the-object-keeps-its-fields) names it as the reason to reopen that design.
- Debugging. Microsoft's break-mode edits, active statements and stepping against an edited method are theirs. My row proves a save lands under a debugger and nothing past that. A breakpoint in patched code is a separate and harder row, because a detour rewrites the first bytes of the code a breakpoint may sit in ([decisions.md](decisions.md#the-debugger-row-downloads-a-pinned-debugger-and-checks-its-hash-because-the-row-has-to-be-real)).
- The mechanism, and the first-party surface in Visual Studio, C# Dev Kit and `dotnet watch`, with browser refresh, Razor and Blazor on top. Nothing to close there. The browser surface beyond what DevReload does is on my list as "later".
- Starting your app any way you like. An app started with `run_app` restarts on a save.
- No JIT tax from me turning tiering off.
- Windows and macOS, since my CI is Linux only ([`main.yml`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/.github/workflows/main.yml#L24-L30)). I need machines to close that one.
- Latency. No test measures save-to-patch, so I quote no number ([`Readme.md`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/Readme.md#L104)). Live testing has a measured gate now and hot reload doesn't. It's the next row I'd add.

Where it's level on the rows above, I mean the edit and its condition, not the mechanism or the polish.

And the thing I can't claim. I used to believe what I do for F# wasn't possible any other way on .NET. As a technical claim that's false. Detouring a method in a running process is standard (Harmony and MonoMod.RuntimeDetour exist for it), and the metadata-deltas work in PR #19941 (a community contribution from Nat Elkins, reviewed by the F# team) is the other route, with closures, state machines and generic methods on its list. What I can say, dated: as of 2026-10-01 no shipped Microsoft tool hot reloads F# in place, and SageFs does it today by method detour. If that ships in an SDK, this page becomes "what SageFs adds on top", which is the state rules and the confirmation. I'd be glad of that.

## What's still rough

A redefined value restarts once anything that hands it on has run. Load the page that renders `greeting`, edit `greeting`, and it's a restart. It never says patched when it can't prove it, so you lose a restart and not the truth ([`docs/hot-reload.md`](https://github.com/WillEhrendreich/SageFs/blob/e47b355b/docs/hot-reload.md#L448-L452)). Reflection reads through a compiled expression tree or a function pointer aren't watched after startup unless you're in the costly `exact-every-read` mode.

If SageFs isn't the one running your app and a save needs a restart, SageFs re-evaluates the whole file, and that resets every `let mutable` in it. The outcome says restart-required, which is true, but not that your state went with it. An app started with `run_app` restarts on every function save (the ticker took about six seconds here).

The new edits have edges. A generic function and a member added to an existing type restart. A caller in another file keeps calling a re-signed function's old method until you save it. A lambda edit in a project you built Release by hand restarts, because its closures are the wrong shape for the match, and rebuilding through SageFs fixes it. A save that only adds code nothing calls ends "not confirmed" until something calls it, and a removal on its own reports "no declaration change". The debugger row needs Linux on x64 and downloads a pinned netcoredbg once (checked by SHA-256, and a machine that can't reach GitHub fails the row instead of skipping it), and it proves a save lands, not that you can step through it.

No test measures save-to-patch latency. The patched MonoMod fork is a dependency I own and have to keep alive. And you can't redefine a type in the REPL while hot reload is on.

## If you want to poke at it

- Switch a session to the Hot Reload workflow (`switch_workflow`, spelled `live`, `weblive` or `web`), start a Falco app from FSI or an `.SageFs/init.fsx`, edit a handler, and read `lastReload` from `get_session_status`.
- Edit `let greeting = ...` before and after loading the page that renders it, and compare the outcomes.
- Tests: `HotReloadParityTests` (the matrix of edits above, real host, .NET 10 and 11), `HotReloadDebuggerTests`, `HotReloadClosureTests` (the pure rules, default suite), `WebAppHotReloadVerificationTests` (the shape matrix, real host, .NET 11), `HotReloadStateOutcomeTests`, `InlinedCalleeOutcomeTests`, `RunAppSaveOutcomeTests`, `PatchConfirmationSimTests`, `ValueReadSimTests`. The real-host ones run under `--integration-host`. A full unfiltered run of the default suite is `dotnet SageFs.Tests/bin/Release/net10.0/SageFs.Tests.dll --summary`. A filtered run that matches nothing exits 0, so look for the test's name in the output.
- `set_reflection_read_mode` switches the reflection watch on a running app.
