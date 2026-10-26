# Adding a field or a lambda without a restart

Roadmap item: `delta-adds-fields-and-types` (Later, hot reload). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | L |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: a capability probe, then `addLambdaCapturing`. Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

A save to a `run_app` app that adds a lambda capturing something, adds a field to a class or record, or adds a type, lands as a
metadata delta. The app keeps its process id and its state, the verdict is `Patched` once the new code has run, and the report
says the mechanism is `metadata-delta`. Edits the runtime cannot take stay restarts that name the declaration: a struct that
gains a field, a lambda that already exists and starts capturing something, a virtual member whose signature changes, a
changed module-level value.

The outcome gate adds rows to the `rows` table in `SageFs.Tests/RunAppDeltaTests.fs` (the table that already holds `generic`,
`closure`, `instance`, `addedMethod`, `rudeVirtual`, `rudeStruct`). Matrix: runtime (net10, net11) by edit (new capturing
lambda held in a route list; new non-capturing lambda; new `task { }` body; field added to a class with an instance member;
field added to a record; new top-level type used by a patched function) with each row's `Before`, `After`, same pid, and a
state counter that carries on. The existing refusal rows stay and must still restart with the same cause. No cell skips.

## What exists today

- `SageFs.Core/Features/MetadataDelta/DeltaWriter.fs` writes a delta from two finished assemblies. It carries new bodies for
  existing methods and methods added to an existing, non-generic, non-interface type (`AddedMethodUnsupported` otherwise).
  New rows it can add: AssemblyRef, TypeRef, TypeSpec, MemberRef, MethodSpec, StandAloneSig, user strings, and for an added
  method its MethodDef and Param rows with the EnC log entries `AddMethod` and `AddParameter` (`DeltaChain.write`, near line 492).
- Everything else is a named refusal in `RudeCause` (`RudeCause.fs`): `TypeAdded`, `FieldsChanged`, `PropertiesOrEventsChanged`,
  `AttributesChanged`, `AddedMethodUnsupported` (virtual, constructor, generic method, generic owner, interface owner),
  `StaticInitializerChanged`, `ClosureMatchAmbiguous`.
- `MethodDiff.fs` finds those refusals. `compareType` compares the type's shape, field list (`fieldsOf`), members
  (`membersOf`) and attributes, then the methods one by one. Closures are matched by `PeImage.FoldedName` (the name with the
  line number folded out); a group whose size changes is `ClosureMatchAmbiguous` and nothing is paired.
- The chain keeps a ledger of what it added so a later delta can name it: `KnownRows` and `ChainState` in `DeltaWriter.fs`
  (`Methods: Map<string, MethodRow>` only; there is no type or field ledger yet).
- The save never reaches the emitter for some type edits. `SageFs.Core/Features/PatchRoute.fs` `choose` sends only a
  `ReloadPlan.PatchFunctions` plan to `SaveRoute.ByMetadataDelta`. A plan that is `RestartRequired` (for example
  `TypeShapeChanged` for a record, row `rudeStruct`, `Cause.Planner`) is a restart before `MethodDiff` is asked. A save that
  adds a lambda is judged by the planner's closure rules first; I did not run what it answers, see Measure first.
- `DeltaApply.fs` checks the runtime before a call. `RequiredFeature` knows three names: `Baseline`, `AddMethodToExistingType`,
  `GenericUpdateMethod`, read from the runtime's internal `MetadataUpdater.GetCapabilities`.
- The existing proofs: `MetadataDeltaTests.fs` (seeded programs from `DeltaProgram.fs`, compiled with Cecil, run in a child
  with `DOTNET_MODIFIABLE_ASSEMBLIES=debug`, answers compared with an interpreter, one to three deltas in a row, twins),
  `RealFSharpDeltaTests.fs` (the F# compiler's own output) and `DeltaRouteSim.fs`.
- `docs/how-hot-reload-works.md` says it plainly: "Mine writes the rows for a method body and for a method added to a type,
  and not yet for a field or a type." It also says Microsoft takes some of these in C# and that the F# compiler PR #19941 would
  write them.

What the runtime may add, from my memory of its capability names and not from the runtime's source (I do not have that source
here, and `GetCapabilities` has not been read for this plan): Baseline, AddMethodToExistingType, AddStaticFieldToExistingType,
AddInstanceFieldToExistingType, NewTypeDefinition, ChangeCustomAttributes, UpdateParameters, and the Generic* variants. The
first step of this plan reads the real list on net10 and net11 and fixes this paragraph.

## Design

Order by value and by risk. Phase A, new closure and state-machine classes, covers "adds a lambda that captures something" and
a new `task { }` body, and needs the new-type capability only. Phase B, an instance field on an existing class or record with
its constructor, property and attribute rows, covers the second sentence of the roadmap item and feeds
`type-change-keeps-state`. Phase C, anything else the measurements allow.

Phase A, the rows for a new nested class: TypeDef, NestedClass, Field (the captured values), MethodDef for `.ctor` and `Invoke`,
Param, and the EnC log entries in the order the runtime wants them. The type's base is `FSharpFunc<_,_>`, a TypeSpec the writer
already rewrites. Attributes on a new compiler-generated type may be left out; measurement 2 checks that the runtime and the
debugger do not care. The patched caller's body refers to the new type's `.ctor` by MethodDef token, so `map` in `write`
(near line 582) maps a next-image MethodDef or TypeDef that the baseline lacks to the row the delta is adding, the way it
already does for an added method.

Matching closures by shape. Today a group whose size changed refuses. The new rule pairs old and new closures of a group by
canonical IL of `Invoke` plus the list of captured field types (`IlCanon` already produces the canonical form), treats an
unmatched new one as added, and treats an unmatched old one as left in place, since a delta cannot remove a type and the
compiler reuses names when lines shift. An ambiguity that survives this still refuses with `ClosureMatchAmbiguous`.

Types, the pure part:

```fsharp
/// What MethodDiff now says about a type the running assembly lacks.
type TypeAddition =
  { Type: string                       // Ns.Outer+Inner, as RudeCause spells it
    Fields: FieldAddition list
    Methods: MethodId list
    Nested: bool }

type MethodChange =                    // existing DU gains cases
  | ...
  | AddedType of TypeAddition
  | AddedField of typeName: string * FieldAddition
```

`KnownRows` gains `Types` and `Fields` maps so the next delta can mention a type or field this one added. `RudeCause.TypeAdded`
stays for the types the writer still refuses (generic, interface, enum, anything with a base it cannot spell). The text
"(a new closure class or type needs a restart)" in `RudeCause.describe` changes, because it becomes false for phase A.

Planner gate. `PatchRoute.choose` widens: with the app in the worker and the delta mode on, a plan that is `RestartRequired`
for a reason in {`ClosureShapeChanged`, `TypeShapeChanged`, `NewDeclaration`, an added value of a type} is handed to the
emitter as `SaveRoute.ByMetadataDelta`. The emitter or the runtime check then either takes it or refuses, and a refusal
reports the first refusal from the emitter, or the planner's own reason when the emitter had none. The pure decision lives in
`PatchRoute`, which `DeltaRouteSim` already folds. Failure always means a restart that names why, so the route cannot report
a patch it did not make.

What stays a restart, with the reason: a struct (the CLR cannot change a value type's layout); an existing lambda that starts
capturing (its live closure objects have no field for the value, and Microsoft draws the same line for C#, see
`how-hot-reload-works.md`); a static field or a module-level value (a delta never runs its initializer, which is what
`StaticInitializerChanged` protects); a virtual member's signature.

Old instances of a type that gained a field hold the CLR's zero in it. That is a semantic the user must be told about, and the
policy for which kinds are honest sits in `type-change-keeps-state`. This item only emits a refusal hook the writer calls with
the field's type (`RudeCause.FieldHasNoHonestZero`, owned there).

Wire and clients. No tool changes and no change to the SSE payload shape: new `RudeCause` cases surface as strings in the
existing `reasons` array (`ReloadRefusal.Case`). `sagefs.nvim` and `sagefs-vscode` render `Case` and `Message` as text, so a
new case needs nothing from them; `Plugin: n/a`. `apiVersion` does not move. Token cost: none.

Persistence: none. Each delta chain lives in the worker's memory and dies with the process.

## Tests

RED first:

1. `SageFs.Tests/MetadataDeltaTests.fs` or a new `MetadataDeltaTypesTests.fs`: "the runtime reports the capabilities this
   emitter needs" (a host-tier test that fails on a runtime missing `NewTypeDefinition`; prints the full list for the record).
2. The same file, "a closure class added by a later version is created by the patched method and answers like the
   interpreter". Needs `DeltaProgram.genNextVersion` to append a `Closure` and a `CallClosure` to an existing method. The
   oracle (`interpret`) already evaluates closures, so only the generator and `compile` change.
3. `RunAppDeltaTests.fs` rows `addLambdaCapturing`, `addLambdaPlain`, `addTaskBody` (the first red row, which proves the whole
   path; today it restarts), then `addClassField` and `addRecordField` for phase B. The fixture `Handlers.fs` gets the routes.
4. `SageFs.Tests/DeltaRouteTests.fs` (where `PatchRoute.choose` is tested today): "a closure-shape plan goes to the emitter when the app is in the worker and the delta mode is on, and
   to a restart otherwise". Red until `choose` changes.

Simulation: `SageFs.Simulation/DeltaRouteSim.fs` and its generators learn two ops, `AddClosure` and `AddField`. New invariants:

- D1: after any sequence of saves, calling every method answers what the interpreter says for the last version saved.
- D2: no save is reported `Patched` that the runtime refused (the sim's runtime refuses a configurable set).
- D3: a refusal always carries a cause and the process is unchanged afterwards.

Twins: `runForgetsNestedClass` (writes a type with no NestedClass row) breaks D1 in the real-runtime harness (the sim
cannot see a missing row); `runIgnoresRuntimeRefusal` breaks D2; `runFirstMatchByOrdinal` (pairs closures by position) breaks D1 when a lambda is
added above another. The mutation style that exists for the writer applies: remove one row kind at a time and each removal
must turn a harness case red.

Tiers: 1, 2 and 4 and the sim run where their current siblings run (`MetadataDeltaTests` is a host-tier test because it needs
a child process with the modifiable environment; check its `Integration` registration and follow it). Row tests run in
`--integration-host`. No waits on time: each row waits on the `Patched` frame, and the harness reads answers from the child
after `ApplyUpdate` returns.

## Steps

1. Capability probe (test 1) and the RED rows (test 3, first row only). Measure first runs here.
2. Harness: `DeltaProgram.fs` generator and `compile` support for added closures and added fields; `DeltaChild.fs` if the child
   needs a new command. Files: `SageFs.Tests/DeltaProgram.fs`, `DeltaChild.fs`, `MetadataDeltaTests.fs`. Runs in parallel with
   steps 3 and 4 since it owns test files only.
3. `MethodDiff.fs`: `AddedType`, closure matching by IL and fields. Files: `MethodDiff.fs`, `RudeCause.fs`, `PeImage.fs` if
   a helper is needed.
4. `DeltaWriter.fs`: type, nested class, field and constructor rows, `KnownRows.Types` and `Fields`, the capability names in
   `RequiredFeature` and `DeltaApply.fs`. After 3 (it consumes the new verdict), same file set otherwise disjoint from 2.
5. `PatchRoute.choose` and its test. Files: `PatchRoute.fs`, `DeltaRouteTests.fs`. Parallel with 4.
6. Phase B: instance field rows, property rows (Property, PropertyMap, MethodSemantics), custom attribute rows for the type
   members that carry `CompilationMapping`, constructors. Coordinates with `type-change-keeps-state` on `RudeCause.fs`.
7. State machines for `task { }` and `async { }` (check what the compiler emits in a Debug build).
8. `how-hot-reload-works.md` ("What it can't do yet" bullet and the comparison table row for adding members) and a
   `decisions.md` entry. The orchestrator owns the roadmap files.

First week: steps 1 to 4 for phase A, ending with `addLambdaCapturing` green on both runtimes. The first commit is step 1.

## Measure first

0. What the planner answers today for each target edit. Run the six edits through `ReloadPlanning.planReloadAsync` in the
   REPL (a pure call). If a new lambda is already `PatchFunctions`, step 5 shrinks; if it is a restart with
   `ClosureShapeChanged`, step 5 is required.
1. The runtime's real capability list on net10 and net11. If `NewTypeDefinition` is missing on net10, phase A ships for net11
   only, and the matrix cell for net10 says so in its name instead of skipping.
2. A hand-written delta in the Cecil child for each row kind, one at a time: a new nested class with a constructor and an
   `Invoke`; an instance field read from an object created earlier; a property; a constructor added to an existing class; a
   custom attribute change. For each, record applied, `ApplyUpdate` threw (which exception), or the child crashed. This table
   decides the scope of phases A and B and replaces the memory-based paragraph above.
3. The rows a real F# build adds for a new capturing lambda, using the `RealFSharpDeltaTests` approach (two builds of the
   fixture, diff the tables). It names every table the writer must learn, including any the compiler adds for debug info or
   attributes.
4. Whether a `MetadataUpdateHandler` or a framework cache reacts to a new type. `DeltaApply` runs handlers already
   (`HandlerRun`); check that ASP.NET routing, which holds the old closures, is untouched.

## Risks and dependencies

- Delta writing is unforgiving: a bad table leaves the process in a state the sim cannot see. The mitigations are the
  child-process harness with the interpreter oracle, the mutation twins, and the rule that a refused delta is never applied.
- The runtime applies a delta once and cannot undo it. A bug in the type rows shows as a failed save followed by a forced
  restart (the route already restarts after a refused apply), and the harm is bounded to that session.
- `dotnet/fsharp#19941`, and the roadmap item "Use the F# compiler's own deltas". If it ships in an SDK the compiler writes
  these rows. The apply, confirm and refuse parts stay. That argues for keeping phase A small and putting the rows behind one
  writer module. Waiting has no date, and the compiler change needs a flag users would have to set.
- Depends on nothing unbuilt. `type-change-keeps-state` waits on phase B. `repl-eval-reaches-the-running-app` waits on phase A
  for a graft of a body that needs a new closure class.

## Size

L. Phase A is two to three weeks including the harness. Phase B adds property, attribute and constructor rows and is another
two to three. Phase C has no estimate.

## Decisions for Will

- Is a lambda that starts capturing in an existing closure a permanent restart, as in C#? Recommend yes. Any fix changes what
  the app's live closure objects hold, which this item will not guess at.
- Ship phase A on its own as soon as `addLambdaCapturing` is green, or hold for phase B? Recommend ship phase A: adding a
  lambda to a route list is the common edit.
- Build the rows ourselves or wait for #19941? Recommend build, behind one module, and read the PR again when phase B starts.
