# A REPL eval changes the running app

Roadmap item: `repl-eval-reaches-the-running-app` (Next, hot reload). Plan written 2026-10-26 against master 59a8692c.

## Work order

Written 2026-11-11 against master 8975e898. Every symbol the plan below names was found in the tree today; the file and line references here are the current ones. `SageFs/Mcp.fs` had uncommitted edits in the working tree, so grep its symbols rather than trusting a line.

### 1. Outcome

An eval that names its file and line, in a session whose app `run_app` started, is served by the app within a few hundred milliseconds, with the process and its state untouched, and the source file holds what the app runs. Today the same eval reaches the app in about 5.6 s (four runs: 5642, 5619, 5611, 5731 ms), nearly all of it the `dotnet build` the write-back route waits for.

Outcome gate: `SageFs.Tests/EvalReachesRunningAppTests.fs` (`--integration-host`) gains a case that waits for the `Patched` frame with origin `graft` before the `Patched` frame with origin `build`, and prints the milliseconds. Then the matrix: six declaration kinds (function body, value, instance member, task body, held closure, added helper) by runtime (net10, net11) by fast landing (on, off). No cell skips; a kind the graft refuses falls back to the build route and the cell asserts the named refusal. Linux only here; Windows and macOS are the `hot-reload-on-windows-and-macos` item.

### 2. Where the code is today

- Write-back: `SageFs.Core/Features/Tweak/EvalWriteBack.fs:17` `WriteBack = Written | NotPersisted`, `:91` `applySplice`, `:127` `decide`. `SageFs/EvalWriteBackWire.fs` writes through the nudge door's locks and journal. `SageFs/Mcp.fs` `evalFSharpCodeWithOutcome` (about `:1087`) calls `EvalWriteBackWire.persist` (about `:1186-1190`) and appends `WriteBack.describe` to the text.
- The build route in the worker: `SageFs.Host/RunAppDelta.fs:79` `Session.Start`, `:97` `Session.Save`: `SessionBuild.runBuildAsync` (`:109`), `PeImage.OfFile` (`:114`), `current.PrepareProbing` (`:122`), `DeltaApply.apply` (`:147`), then `current.Commit`. `SageFs.Host/WorkerMain.fs:1038` `deltaRoute`, started at `:2265-2270`.
- The chain: `SageFs.Core/Features/MetadataDelta/DeltaWriter.fs:452` `Prepare`, `:455` `PrepareProbing` (calls `MethodDiff.diff`, `MethodDiff.fs:182`), `:582` `let rec map` (token renumbering; raises `RudeCause.TypeAdded` and `MethodRemoved`). There is no `PrepareGraft` and no `TokenSource` (grep is empty).
- The IL source: `SageFs.Core/HostAgent.fs:109` `AssemblySources { Dynamic; Loaded }`. Core already decodes method bodies with `GetILAsByteArray` (`LiveValueTree.fs:331`, `Middleware/ValueReads.fs:69`).
- Protocol: `SageFs.FsiHost/FsiProtocol.fs:137` `Request` (`AgentDebugBegin` `:173`, `AgentValueReads` `:178` are the shape to copy), results from `:206`; dispatch in `SageFs.FsiHost/Program.fs:399,447`. `SageFs.Core/WorkerProtocol.fs:351` `WorkerMessage` (`ApplySaves` at `:408` is the nearest shape), responses `:532-537`.
- Freshness: `SageFs.Core/SessionReload.fs:261` `ReplFreshness.BehindApp`, `:299` `observe`.
- Sim: `SageFs.Simulation/DeltaRouteSim.fs:36` `Op` (Edit, Prepare, Apply, ArmFault, ReplaceProcess, Respawn, AttachDebugger, DetachDebugger, RequestStarts, RequestEnds, BoundElapses), `:130` `Policy` with its twins; `DeltaRouteInvariants.fs:107` `all` (six invariants). No graft op yet.
- Gate test: `EvalReachesRunningAppTests.fs:138` `Integration.hostList`, one case; the clock at `:189` is printed and never judged. Chain fixtures: `SageFs.Tests/DeltaProgram.fs` (`interpret` is the oracle), `DeltaChild.fs`, `DeltaRouteTests.fs`, `DeltaRouteSimTests.fs`.
- A setting to copy: `SageFs.Core/SettingsCatalog.fs:396` `starterToolList`, a `VToggle` descriptor.
- The spike's notes are `live-loop-plan-2026-10-02.md` at the repo root, untracked. Its 4 to 18 ms figures are a lead; no spike code is in the tree or in `git log`.

### 3. Steps

0. (measure, nothing lands) Measurements 1 to 4 below, in a throwaway `--integration-host` case on the ConsoleTicker fixture: eval `let renderLine ...`, add a temporary host request that returns `GetILAsByteArray()` and every token resolved with `Module.ResolveMember`, `ResolveType`, `ResolveString`, count unresolved tokens, and compare the FSI name (`FSI_NNNN.Ticker.renderLine`) with the compiled `MethodId` (`MethodDiff.fs:28`). Time `MetadataUpdater.ApplyUpdate` for an instance member. Write the numbers under "Measure first". An unresolved token class or a name that cannot be mapped shrinks the item, and the plan says so before any code.
1. (pure) RED `SageFs.Tests/GraftedDeltaTests.fs` (new; `<Compile Include="GraftedDeltaTests.fs" />` beside `DeltaRouteTests.fs`, fsproj line 690 area): "a grafted method is sent again by the next save even when the build holds the original body" and "a graft moves no other method's generation". Compile-red: `DeltaChain.PrepareGraft` does not exist.
2. (pure) `DeltaWriter.fs`: `GraftedBody`, the closed `GraftRefusal` with one `describe`, a `TokenSource` seam under `map` (`:582`), `PrepareGraft`; `MethodDiff.fs`: the grafted-set rule. Green for step 1.
3. (pure, sim) `DeltaRouteSim.fs`: ops `Graft`, `WriteBackLands`, `BuildLands`, `SourceReverts`; invariants G1 to G3 in `DeltaRouteInvariants.fs`; twins `runForgetsGrafted` and `runGraftAcrossReplace` in `Policy`. `DeltaRouteSimTests.fs` has the "every twin is caught" shape to copy.
4. (edge, host) `FsiProtocol.Request.AgentExportBodies`, the export in `HostAgent.fs`, dispatch in `FsiHost/Program.fs`. File-disjoint from step 2, so parallel.
5. (edge, worker) `RunAppDelta.Session.Graft` (the stages of `Save` minus the build), one new `WorkerMessage`, its handler in `WorkerMain.fs`. After 2 and 4.
6. (edge, daemon) `Mcp.fs`: on `Written`, ask the worker to graft and put the answer in the `writeBack` line ("applied to the running app (graft, N ms)", "will apply when the save lands", "not applied: <refusal>"); `SessionReload` gains `origin` (`graft` or `build`); a `VToggle` setting in `SettingsCatalog.fs`, off by default. Wire files change, so the commit carries a `Plugin:` trailer. `sagefs.nvim` posts `file_path` and `block_start_line` and quotes none of the reply text (checked at 42efc44c); re-check, then `Plugin: n/a`.
7. (test) The host case, the matrix, and the setting on for the host tier. Docs `how-hot-reload-works.md` and `mcp-tools.md`.

### 4. How to check it

- REPL first: a session on `SageFs.Tests`; `DeltaProgram` images and `DeltaChain` can be driven with `send_fsharp_code` before anything is written.
- `dotnet build SageFs.Tests -c Release`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --ratchets`.
- Inner loop: `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --summary --filter-test-list "grafted"`, and `--filter-test-list "DeltaRoute"` for the sim.
- Acceptance: unfiltered `--summary`, then `dotnet SageFs.Tests/bin/Release/net11.0/SageFs.Tests.dll --integration-host --summary` (it builds the ticker sample cold through `SessionBuild.runBuildAsync`).

### 5. Traps in this repo

- `DeltaWriter.fs` and `WorkerMain.fs` are large: check `RatchetBudgets.fileSize` before adding; a new file costs nothing, a budget raise is refused.
- SAGE-005: a `PrepareGraft(encId: Guid, body: GraftedBody)` is fine; never two positional `byte array`s.
- Child-process cases (`DeltaChild`) go through `SpawnEnvironment.apply` and need `DOTNET_MODIFIABLE_ASSEMBLIES=debug`.
- No stopwatch assertion: the order of the two `Patched` frames is the pass condition; milliseconds are printed.
- Step 6 is a wire change: `Plugin:` trailer, plugin first.
- A graft on a session that follows a trunk races `ApplySaves`: refuse it with a named reason.

### 6. Size and order

M. Step 0 is a day and decides the shape. Steps 1 to 3 are two to three days of pure work. Steps 4 and 5 are the unknown, two to four days. First commit: step 1's RED, after step 0's numbers are in this file.

### 7. Not now

- Type declarations in the write-back: the undo journal cannot address a type.
- New closure classes and new types (`TypeAdded` in both routes) wait on `delta-adds-fields-and-types`.
- Windows and macOS.

DECISIONS: the three under "Decisions for Will" below stand (graft only after `Written`, setting off for the first release, no type declarations). One more: whether the additive `origin` field moves `apiVersion` (4, `SageFs.Core/EndpointContracts.fs`). Recommendation: no; the comment there names endpoints and breaking shape changes only.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | M |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a grafted method is sent again by the next save. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

You eval a function or value declaration in a session whose app was started with `run_app`, naming the file and line. The
app serves the new body within a few hundred milliseconds of the eval returning, with its process, counters and caches
untouched. Today the same eval reaches the app after about 5.6 seconds, because the write-back route waits for a `dotnet build`.
The declaration is in the source file before the app runs it, so the source, the app and the REPL cannot disagree about
what the app is running.

The outcome gate extends `SageFs.Tests/EvalReachesRunningAppTests.fs`. Its matrix is runtime (net10, net11) by landing route
(fast landing on, fast landing off) by declaration kind (function body, value, instance member, task body, held closure,
added helper). Every cell ends with the app serving the new text and the verdict `Patched`, and the cell prints the
milliseconds from eval to served. Negative cells: an eval with no `file_path` leaves the app untouched and the result says
"not persisted"; an eval whose write-back is refused leaves the app untouched; a declaration the graft cannot carry (a
new closure class, a new type) falls back to the build route and names why. No cell may skip.

## What exists today

- The write-back route is in. `SageFs.Core/Features/Tweak/EvalWriteBack.fs` decides (`decide`, `applySplice`, the `WriteBack`
  verdict `Written | NotPersisted`). `SageFs/EvalWriteBackWire.fs` writes through the nudge door's file lock and journal, so
  `nudge_value action=undo` reverts it. `SageFs/Mcp.fs` `evalFSharpCodeWithOutcome` returns the verdict beside the eval result.
- The write-back accepts one declaration whose kind is a value or function binding. A type is refused by name, because the undo
  journal cannot address it (`addressable` in `decide`).
- After the write, the worker's file watcher takes the save. `RunAppDelta.Session.Save` (`SageFs.Host/RunAppDelta.fs`) runs
  `SessionBuild.runBuildAsync`, reads the build with `PeImage.OfFile`, asks `DeltaChain.PrepareProbing`
  (`SageFs.Core/Features/MetadataDelta/DeltaWriter.fs`) for a delta and hands it to `DeltaApply.apply`.
- `EvalReachesRunningAppTests.fs` drives that route end to end. Commit 57a8f866 records four runs of 5642, 5619, 5611 and
  5731 ms from eval to the app printing the new value. The build is nearly all of it.
- The IL of an eval lives in the FSI host process (`HostAgent.Agent`, with `AssemblySources.Dynamic` reading its dynamic
  assemblies). The delta chain lives in the worker (`deltaRoute` in `SageFs.Host/WorkerMain.fs`, started with
  `RunAppDelta.Session.Start`). The two talk through `SageFs.FsiHost/FsiProtocol.fs` (`Request`, `Response`).
- A 2026-10-01 spike grafted FSI-emitted IL through the chain and the app served the result 4 to 18 ms after the eval returned
  (an instance member took 126 to 386 ms in the runtime's apply). Its notes are in `live-loop-plan-2026-10-02.md` in the main
  checkout, which is untracked. I could not find the spike's code anywhere in the tree or in `git log`, so this plan treats
  its numbers as a lead and not as evidence.
- The notes list sharp edges: a graft silently loses to or diverges from the next save unless the chain's diff base accounts
  for it; a re-declared closure with no graft of its `Invoke` reads as "nothing changed"; an eval that adds an overload is
  dropped by `MethodDiff`; new closure classes and new types are `TypeAdded` in both routes.
- `ReplFreshness.observe` (`SageFs.Core/SessionReload.fs`) puts the REPL `BehindApp` for every save that took the
  metadata-delta mechanism, including one a write-back caused.

## Design

One rule keeps the graft honest: a graft is the early landing of a declaration whose write-back came back `Written`. An eval
that is not written back never touches the app, exactly as today. The source always holds what the app runs, so the divergence
the spike notes warn about cannot arise from a plain eval.

Two things still have to be true for a later build:

1. The chain remembers what it grafted. After the write-back save builds, the diff base must not treat a grafted method as
   unchanged because the build's body equals the old body (the user reverted the line before the build finished). The chain
   carries a set of grafted methods, and `MethodDiff` reports each of them as changed on the next save, so the build's body
   always replaces the graft.
2. A graft never changes what the next ordinary save computes for any other method.

```fsharp
/// A method body taken from FSI, with every token it holds spelled out so the writer can renumber it.
type GraftedBody =
  { Method: MethodId           // the compiled twin's identity
    Il: byte array
    MaxStack: int
    LocalsSignature: byte array
    Regions: ExceptionRegion list
    Tokens: GraftToken list }  // each token with the entity it names, as text the baseline index can look up

type GraftRefusal =            // closed set, one describe function
  | NoCompiledTwin of name: string
  | NewTypeNeeded of typeName: string           // waits on delta-adds-fields-and-types
  | ClosureShapeDiffers of typeName: string
  | OverloadAdded of name: string
  | UnreadableToken of token: int * why: string
```

Where code goes:

- `SageFs.FsiHost/FsiProtocol.fs` and `SageFs.Core/HostAgent.fs`: a request `AgentExportBodies` that returns `GraftedBody` values
  for the declaration an eval defined. It reads `MethodBody.GetILAsByteArray()` and resolves each token through
  `Module.ResolveMember` / `ResolveType` / `ResolveString` on the dynamic module.
- `SageFs.Core/Features/MetadataDelta/DeltaWriter.fs`: the writer's `map` (near line 582) renumbers tokens by asking
  `next.Describe`. Put a `TokenSource` interface under it with two implementations, the PE image it reads today and a grafted
  body's token list, then add `DeltaChain.PrepareGraft`. `MethodDiff.fs` gets the grafted-set rule.
- `SageFs.Host/RunAppDelta.fs`: `Session.Graft`, built from the same stages as `Save` minus the build, with the same gate,
  `DeltaApply.check`, probes and `PatchConfirmation`.
- `SageFs.Core/WorkerProtocol.fs` and `SageFs.Host/WorkerMain.fs`: one new worker message for "land this declaration now".
- `SageFs/Mcp.fs`: when `evalFSharpCodeWithOutcome` gets `Written`, ask the worker to graft and put the answer in the same
  `writeBack` field. The write-back line gains one of "applied to the running app (graft, N ms)", "will apply when the save
  lands" or "not applied: <GraftRefusal>".
- A setting in `SageFs.Core/SettingsCatalog.fs` turns the fast landing on. It ships off for the first release.

Wire and clients. No new tool. The `writeBack` field in the `send_fsharp_code` result grows a token for how the app got the
code (additive). The reload report's `mechanism` stays `metadata-delta`; a new `origin` field says `graft` or `build`. Per the
repo rule, the commit that adds either field carries a `Plugin:` trailer: sagefs.nvim posts `file_path` and
`block_start_line` already and quotes none of the result text (commit 42efc44c checked), so I expect `Plugin: n/a`, to be
re-checked when the field lands. Whether an additive field moves `apiVersion` (now 4 in `EndpointContracts.fs`) is Will's
call, since the comment there only names endpoints and breaking shape changes.

Token cost: no new tool. The `send_fsharp_code` description grows by two sentences, about 60 tokens.

Freshness. A graft puts the REPL ahead on the declaration it carried and still behind on compiled code that calls it, so
`BehindApp` stays correct. It lists the declaration a single time, though two saves (the graft and the confirming build) touch it.

## Tests

RED first, in this order:

1. `SageFs.Tests/GraftedDeltaTests.fs`, "a grafted method is sent again by the next save even when the build holds the original
   body". Built on `DeltaProgram` and `DeltaChild` (Cecil-written programs in a child started with
   `DOTNET_MODIFIABLE_ASSEMBLIES=debug`). Compile-red today, because `DeltaChain.PrepareGraft` does not exist. Once it
   compiles, it fails until the grafted set exists.
2. Same file, "a graft lands on the chain without moving the generation of any method it did not carry".
3. `SageFs.Tests/ReplFreshnessTests.fs`, "a graft and the save that confirms it name the declaration once".
4. Host tier, `EvalReachesRunningAppTests.fs`, "the app serves the evaluated value before the confirming build lands". It
   waits on events: the `Patched` frame with origin `graft` must arrive before the `Patched` frame with origin `build`. No
   stopwatch assertion decides pass or fail. The milliseconds are printed.

Property test: for any sequence of grafts and builds over a generated `DeltaProgram`, the process answers what the last
writer of each method said (`interpret` is the oracle that already exists).

Simulation: extend `SageFs.Simulation/DeltaRouteSim.fs`. New ops `Graft m`, `WriteBackLands`, `BuildLands`, `SourceReverts m`,
`ProcessReplaced`. New invariants in `DeltaRouteInvariants.fs`:

- G1: after any `BuildLands`, every grafted method runs the build's body.
- G2: a graft never runs on a chain from before a `ProcessReplaced`.
- G3: `Patched` with origin `graft` appears only after a call entered the grafted body.

Twins: `runForgetsGrafted` (diff base ignores the grafted set) must break G1 on the revert scenario; `runGraftAcrossReplace`
must break G2. The sim already has twins in `Policy`; add these two there.

Tiers: items 1 to 3 and the sim run in the default suite. Item 4 and the matrix run in `--integration-host`. Every wait in
the host tests is an event on the worker's reload stream, never a sleep.

## Steps

1. Measure first (below). No code lands.
2. Write RED tests 1 and 2 and the sim ops and twins. Files: `SageFs.Tests/GraftedDeltaTests.fs` (new),
   `SageFs.Simulation/DeltaRoute*.fs`.
3. Grafted set in the chain plus the `MethodDiff` rule. Files: `DeltaWriter.fs`, `MethodDiff.fs`. Green for 1, 2, G1, G2.
4. `TokenSource` seam and `PrepareGraft` in the writer. Same files as step 3, so after it.
5. Host export: `AgentExportBodies` in `FsiProtocol.fs`, `HostAgent.fs`, `SageFs.FsiHost/Program.fs`. File-disjoint from
   step 4, so it runs in parallel with it.
6. Worker wiring: `RunAppDelta.Session.Graft`, the worker message, `WorkerMain.fs`. After 4 and 5.
7. Daemon wiring, result line, freshness origin, setting: `Mcp.fs`, `SessionReload.fs`, `SettingsCatalog.fs`. Tests 3 and 4.
8. Matrix cells, docs (`how-hot-reload-works.md`, `mcp-tools.md`, `roadmap.md` and `RoadmapItems.fs` are the orchestrator's),
   the setting on in CI for the host tier.

First week: steps 1 to 4. The first commit is step 2.

## Measure first

1. Cross-process graft on a real worker. The spike did not run through a worker, across the FSI-host to worker boundary,
   against the Cecil-instrumented shadow baseline, on net10, or with an unsaved buffer. Experiment: a throwaway host-tier
   test that evals `let closureMsg () = "B"` in the fixture session, exports the body, builds a delta by hand and applies it
   in the worker. If it works on both runtimes, go on. If the instrumented baseline breaks it, the fallback is to graft only
   methods the instrumenter left untouched, which is a smaller feature.
2. Token resolution. Does `Module.ResolveMember` on FSI's dynamic module return every token that appears in a
   `--optimize-` body (member refs on FSharp.Core generics, `MethodSpec`, field tokens)? Experiment: export every method of
   the fixture's `Handlers.fs` twin from FSI and count unresolved tokens. A non-zero count names the shape to refuse.
3. Do FSI and the build agree on names? Under `--multiemit-` the method is `FSI_NNNN.Handlers.f`; the writer needs the
   compiled `MethodId`. Experiment: list both for the six fixture kinds. A mismatch means the export must map through the
   `ReloadPlanning` declaration names (`SourceDecl`) the write-back already holds.
4. Why an instance member took 126 to 386 ms in the spike's apply. Experiment: time `MetadataUpdater.ApplyUpdate` on an
   instance-member delta with and without a live instance. If the runtime does the slow part, the claim "a few hundred
   milliseconds" holds only for static code, and the plan says so.

## Risks and dependencies

- The graft carries code the production compiler did not produce. FSI optimizes differently, and a patch that matched the
  build only by name could behave differently in edge cases such as tail calls. The sim cannot see that. The matrix covers the
  six kinds, and the confirming build overwrites the graft within seconds, which bounds the exposure.
- Cohort landings and trunk follow also reach the chain (`ApplySaves`). A graft on a trunk session would race a landing.
  Refuse a graft on a session that follows a trunk.
- Depends on nothing unbuilt. New closure classes and types stay refused with `NewTypeNeeded` until
  `delta-adds-fields-and-types` ships. It raises how often the REPL is behind the app, which makes
  `level-the-repl-without-losing-it` more valuable.

## Size

M. The chain rule and the sim are days. The host export and token renumbering are the unknown, and a bad result from
measurement 1 or 2 would shrink the feature while the estimate stays where it is.

## Decisions for Will

- Graft only after a `Written` write-back. Recommend yes. It removes the divergence class the spike found.
- Ship the setting off by default for the first release. Recommend yes, then flip after a week of the host-tier matrix.
- Extend the write-back to type declarations so the build route can carry them? The undo journal cannot address a type today.
  Recommend no, until `delta-adds-fields-and-types` can carry a type at all.
