# A REPL eval changes the running app

Roadmap item: `repl-eval-reaches-the-running-app` (Next, hot reload). Plan written 2026-10-26 against master 59a8692c.

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
