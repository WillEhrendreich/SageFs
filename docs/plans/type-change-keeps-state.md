# Type changes keep your running state

Roadmap item: `type-change-keeps-state` (Later, hot reload). Plan written 2026-10-26 against master 59a8692c.

## Resume here

This block is the item's own handoff. Whoever works on the item updates it in the same commit as the work, so a different person, session or tool can pick up from here without anyone retelling it. The rules that apply to every item are in [Picking up an item](README.md#picking-up-an-item-in-any-harness).

| Field | Now |
|:--|:--|
| Status | Not started |
| Size | L |
| Branch and worktree | none yet |
| Last green commit | none yet |
| Next action | Run what "Measure first" asks for (it can end the item), then write the first failing test: `FieldZero` for every kind (property). Then take the steps below in order. |
| Decisions made | none yet (the open ones are under "Decisions for Will") |
| Surprises | none yet |

## What done looks like

You add a field to a record in a `run_app` app and save. The app is not restarted, the records it already holds keep every
field they had, and records built after the save carry the new field. You declared nothing and registered nothing. The save
report names each field the existing records now hold a default for ("Order.Note, 0 existing values"), so nothing is silent.
A field whose kind has no honest default (another record, a function, a string until phase 2) restarts the app and names that
field.

The outcome gate adds rows to `SageFs.Tests/RunAppDeltaTests.fs` (the `rows` table, same shape as `rudeStruct`). Matrix: runtime
(net10, net11) by field kind (int, float, bool, option, DateTime, Guid, enum: kept; string, list, map, record, function:
restart naming the field) by where the value lives (a module-level `let mutable`, a ref cell, a dictionary captured by a
closure). Each kept cell asserts the same process id, an old value that still reads its other fields, a new value with the new
field, and the verdict `Patched`. Each refused cell asserts a restart whose reason names the field. No cell skips. This item
cannot go green before `delta-adds-fields-and-types` can add a field and its accessors (see Dependencies).

## What exists today

- The user-visible limit, in the roadmap: a record that gains a field keeps its live value only if the app registers a
  boundary, and only for simple fields.
- The registration API is `Registry.DeclareWithSubject` in `SageFs.Core/RestartBoundaries.fs` (a `Boundary` has `Id`, `Holds`,
  `Migrate`, `Subject`). I found no sample and no doc where an app calls it; `SageFs.Tests/ShapeMigrationTests.fs` (near line
  132) does. The user's app references only FSharp.Core (`samples/demos/SageFs.Samples.ConsoleTicker`), so reaching `SageFs.Core`
  from the app is itself a step the user would have to take.
- The migration is `SageFs.Core/ShapeMigration.fs`, `migrate`: records only; it builds the new value through the one-argument-
  per-field constructor of the FSI-compiled type. Its `kindOf` knows int, int64, uint32, string, char, bool and returns `None`
  for everything else, and two `None` kinds compare equal, so a field retyped between two other kinds is caught only when the
  constructor call throws. Its `defaultOf` supplies a value type's zero, `""`, or a type with a parameterless constructor, and
  refuses an F# `Map`, `list` or `option` field.
- `SageFs.Core/TypeShapeMigration.fs` decides on `FieldKind = IntField | StringField | BoolField | Undecidable`, and takes
  `hasDefault: bool`. `SageFs.Core/MigrationPlan.fs` decides on field names alone ("a field whose kind actually changed is
  therefore not detected here").
- The decision chain is `WorkerMain.fs` `migrationWorthFor` -> `RestartCost.decideFromLivenessAndMigration` ->
  `RestartAction.MigrateAndRespawn` -> `RestartPlan.Migrate boundaryId`. In `SessionManager.fs` (near line 732) `Migrate`
  takes the same arm as `RespawnOnly`: a respawn with no build. What I could not find is what carries the live value from the
  old worker process to the new one. `docs/granular-restart-scope.md` says the build was dogfooded in a live session, and its
  own text calls an earlier conclusion outdated. Measurement 1 settles it.
- Liveness is unanswered: `docs/granular-restart-scope.md` records that .NET has no per-type instance count, so
  `Liveness.Unknown` is the honest answer and pays a build.
- The delta route refuses a type edit in two places. `ReloadPlanning` rules it a restart before the emitter is asked (row
  `rudeStruct`, `Cause.Planner`), and `PatchRoute.choose` (`SageFs.Core/Features/PatchRoute.fs`) sends only a `PatchFunctions`
  plan to the delta. The emitter itself says `RudeCause.FieldsChanged` and `PropertiesOrEventsChanged`
  (`MethodDiff.fs`, `compareType`).
- `docs/hot-reload.md` rule 4 ("when state can't carry over safely ... restart and say why") is the contract this item keeps.

## Design

The route follows the process. A `run_app` app keeps its process, so the type edit goes through the delta route, the existing
objects stay where they are, and the CLR gives the new field its zero value in each of them. That makes the zero value the
whole policy question, and it also makes most of the old migration machinery unneeded for this case.

Phase 1, kinds whose CLR zero is the honest default. For these the CLR already did the work and no object is touched:

| Kind | Zero the old objects hold |
|---|---|
| int, int64, uint and friends, float, decimal, byte | 0 |
| bool | false |
| char | the NUL character |
| `option`, `voption` | `None` / `ValueNone` |
| `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid` | the type's `default` |
| enum | the member with value 0, if one exists; otherwise refuse |

Phase 2, kinds whose zero is not null: `string` (`""`), `list` (`[]`), `Map` and `Set` (empty), arrays. The old objects hold
null here, so a reader would throw. Two ways to fix that, chosen by measurement 3:

- Patch each existing instance's field through reflection (an `initonly` instance field accepts `FieldInfo.SetValue`). This
  needs the instances. A heap snapshot through the existing `SageFs.Core/Features/GcDumpCapture.fs` could list them with a
  cost of a pause. A reflection walk from module-level roots cannot prove it found every instance, and this item fails closed,
  so a walk alone is not enough.
- Write the getter so a null field reads as the zero. Direct `ldfld` readers inside the generated members (`Equals`,
  `GetHashCode`, `CompareTo`) would need the same rewrite, so this is only viable if measurement 3 shows the compiler reads
  record fields through the getter.

Pure core, replacing the three overlapping vocabularies above (`kindOf`, `FieldKind`, `defaultOf`) with one:

```fsharp
type FieldZero =                          // closed set; one describe; one wire token
  | ClrDefaultIsHonest of kind: string    // phase 1
  | NeedsInstancePatch of kind: string * zero: string   // phase 2
  | NoHonestZero of kind: string          // refuse, naming the field

module FieldZero =
  let forType (t: System.Type) : FieldZero            // total; no bool, no option
  let describe : FieldZero -> string
```

It lives in a new `SageFs.Core/FieldZero.fs`. `ShapeMigration.migrate` switches to it (a fix for the existing path that needs
nothing else), and the emitter side reads the field's type from the metadata signature and asks the same table by type name,
so the planner side and the executor cannot disagree. The `hasDefault: bool` parameter in `TypeShapeMigration.decideField`
becomes a `FieldZero` argument.

Where the delta route changes (all owned by `delta-adds-fields-and-types`; this item adds only the policy):

- `PatchRoute.choose` stops sending a `TypeShapeChanged` plan straight to a restart when the app runs in the worker and the
  delta mode is on. It asks the emitter. If the emitter or `FieldZero` refuses, the restart reason is the first refusal.
- The delta writer's field-add path calls `FieldZero.forType` for each added instance field and refuses with a new
  `RudeCause.FieldHasNoHonestZero of typeName * field * kind`.

Report. `Declarations` and the kept-state notice (`KeptStateReport` in `SageFs.Core/DevReload.fs`) gain a "defaulted" list: type,
field, zero, and how many instances if the phase-2 snapshot counted them. The dashboard and `get_session_status` show it
through the existing `lastReload` object (additive field). Per the repo rule the commit carries a `Plugin:` trailer; nvim
reads `lastReload` through `status_fields.lua`, so I expect `Plugin: done <show defaulted fields>`. `apiVersion` does not move
for an additive field. Token cost: none, no tool changes.

Out of scope here, with the reason: carrying a value across a respawn for a type the delta cannot change (a removed field, a
retyped field). That needs a transfer of a live object graph between two processes, which nothing in the tree does. If
measurement 1 shows the current `Migrate` path already does it, the plan is to leave it alone and document it.

## Tests

RED first, committed with the repo's red-first marker (skill `sagefs-workflow`):

1. `SageFs.Tests/FieldZeroTests.fs`, a property over a generated set of `System.Type`s: "`forType` answers `ClrDefaultIsHonest`
   exactly for the kinds in the phase 1 table, whatever record or tuple wraps them". Plus examples: float, option, Guid,
   string (phase 2), a record (refuse). Compile-red today.
2. `ShapeMigrationTests.fs`, "a float field survives a migration". Fails today because `kindOf` has no float, so `migrate`
   refuses a type it could carry.
3. `RunAppDeltaTests.fs` rows `recordAddsInt`, `recordAddsFloat`, `recordAddsOption`, and a refusal row `recordAddsFunction`
   expecting a restart naming the field. The fixture `SageFs.Tests/fixtures/RunAppDeltaFixture/Handlers.fs` gets a record held in
   a module-level `let mutable`, a ref cell and a closure-captured dictionary. The first three are red until the delta writer
   adds fields; the refusal row passes once the emitter is the one giving the reason.
4. A planner test in `ReloadPlanningTests.fs`: "with the delta route on and the app in the worker, a record gaining a field is
   handed to the emitter". Red until `PatchRoute.choose` changes.

Simulation. `SageFs.Simulation/DeltaRouteSim.fs` gets a program whose "type" has a list of fields. New ops `AddField k`,
`SaveWithField`, `ReadOld`, `ReadNew`. Invariants: T1, a read of an old object never returns a value the program did not
write or the zero of its kind; T2, a field of a kind with no honest zero is never added without a restart; T3, an old
object's other fields never change across a field add. Twins: `runZeroEverything` (a wrong zero for strings) breaks T1;
`runAddAnyKind` (skips `FieldZero`) breaks T2. The deterministic child-process harness (`MetadataDeltaTests.fs`,
`DeltaProgram.genNextVersion`) gets a `mutate` that adds a field, so the runtime itself answers what an old object holds.

Tiers: items 1, 2, 4 and the sim in the default suite; item 3 and the harness in `--integration-host`. No waits on time: rows
wait on the `Patched` frame and the app's own responses.

## Steps

1. Measure first (below), without code.
2. `FieldZero.fs` and its property tests (RED 1 then green). Wire `ShapeMigration` to it (RED 2 then green). This is
   independent of every other item and improves the existing path today. Files: `SageFs.Core/FieldZero.fs` (new),
   `ShapeMigration.fs`, `TypeShapeMigration.fs`, `SageFs.Core.fsproj`.
3. Commit the RED rows of test 3 and test 4. Files: `RunAppDeltaTests.fs`, the fixture, `ReloadPlanningTests.fs`.
4. `RudeCause.FieldHasNoHonestZero` and the policy hook the writer calls. Files: `RudeCause.fs`, `DeltaWriter.fs`. Waits for
   the field-add path of `delta-adds-fields-and-types`; coordinate by owning `RudeCause.fs` here and `DeltaWriter.fs` there.
5. `PatchRoute.choose` widening and the report's defaulted list. Files: `PatchRoute.fs`, `DevReload.fs`, `ReloadBroadcast.fs`,
   `SessionReload.fs` (parses the new field).
6. Phase 2 for strings, lists and maps, once measurement 3 picks the mechanism.
7. Docs: `hot-reload.md` (the restart table row for "add a field"), `how-hot-reload-works.md`.

First week: steps 1 to 3. They need no other item and put the target on the board.

## Measure first

1. What `Migrate` does today across a respawn. Experiment: a `run_app` app with a module-level `let mutable orders: Order list`
   and a registered boundary with `ShapeMigration.hook`; edit `Order` to add an int field; read `orders` before and after and
   the pid. If the list survives, find how (it will name the transfer mechanism). If it does not, the roadmap sentence "can keep
   its live value today" is only true for the FSI-compiled path, and `docs/granular-restart-scope.md` needs a correction.
2. The CLR's answer for an old instance. Experiment in the Cecil child harness: add an instance field and a constructor to a
   class, apply the delta, read the field from an object built before. Expected: zero. Also whether the runtime reports
   `AddInstanceFieldToExistingType` in `MetadataUpdater.GetCapabilities()` on net10 and net11 (the repo checks only three
   names today). If an old instance faults, phase 1 is dead and the plan reduces to the `FieldZero` table.
3. Does the F# compiler read a record's fields through the getter in a Debug build? Disassemble the fixture's `Handlers.fs`
   record. It decides phase 2's mechanism.
4. What a snapshot costs. Time `GcDumpCapture` on the fixture and on `SageFs.Core`'s size. Over about 2 s, phase 2 becomes an
   opt-in and the default for string fields stays "restart, naming it".

## Risks and dependencies

- Depends on `delta-adds-fields-and-types` for fields, properties, constructors and custom attribute rows (a record's fields
  carry `CompilationMapping` attributes, and `RudeCause.AttributesChanged` refuses attribute changes today). That item depends
  on nothing here.
- Zero in an old object is a silent semantic. The report line is the mitigation, and it must be on every surface that shows
  `lastReload`. A user who adds `Discount: float` and sees old orders at 0.0 has been told, and may still be surprised.
- Struct records and structs never take this route; the CLR cannot add to a value type's layout.
- Competes with, and may be absorbed by, `Use the F# compiler's own deltas` (dotnet/fsharp#19941). The `FieldZero` policy
  survives that change, because the compiler does not decide what an old object holds.

## Size

L. The policy is days. The real work is the field, property, constructor and attribute rows in item 4, and the phase-2
instance patch, which has no mechanism chosen yet.

## Decisions for Will

- Is "the zero of the kind, said loudly" an acceptable value for old records? The alternative is asking you for an expression
  per field, which breaks "needs nothing from you". Recommend the zero, with the report line.
- Strings in phase 1 or phase 2? Recommend phase 2, because a null string in an old object throws on first use.
- Leave the boundary API in place? Recommend yes for now, and remove it only after measurement 1 says what it does.
