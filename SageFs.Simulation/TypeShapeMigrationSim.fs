module SageFs.Simulation.TypeShapeMigrationSim

/// WHY — the type-shape migration's whole claim is "migrate only what is
/// decidable, refuse everything else", and the failure mode that would matter
/// is a migration that reports success while producing a value nobody derived.
/// That is a claim about MANY fields and many shape combinations, so it needs
/// to be folded rather than exemplified.
///
/// The twin is the exact bug a real implementation would have: treat a
/// non-defaulted new field as if it had a default. That is the one rule the
/// design's "refuse everything else" exists to stop, and it must break the
/// invariant.

open SageFs
open SageFs.Holder
open SageFs.TypeShapeMigration

let baseTimeUtc = System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)

/// The field kinds the generator can produce, including the undecidable one.
let allKinds : FieldKind list =
  [ FieldKind.IntField
    FieldKind.StringField
    FieldKind.BoolField
    FieldKind.Undecidable "byref-like" ]

type Scenario =
  { Seed: int
    /// Field names in the OLD shape.
    OldFields: string list
    /// (name, kind) pairs in the NEW shape.
    NewFields: (string * FieldKind) list
    /// Which new field names the new definition gives a default.
    Defaulted: string list }

type Observation =
  { Migrated: bool
    /// True when the decision claimed a carry AND some field was in fact
    /// undecidable — the shape the invariant forbids.
    ClaimedCarryWithUndecidable: bool }

type Trace =
  { Scenario: Scenario
    Observation: Observation }

/// The model under test, so the real function and the twin share one driver.
type Model =
  { Name: string
    Decide: RecordShape -> RecordShape -> (string -> bool) -> Holder.Migration<RecordShape, RecordShape>
    /// TWIN — the bug: treat every new field as defaulted, so a field with no
    /// default is silently added instead of refusing.
    AssumeDefaults: RecordShape -> RecordShape -> (string -> bool) -> Holder.Migration<RecordShape, RecordShape> }

let real : Model =
  { Name = "real"
    Decide = TypeShapeMigration.decideRecord
    AssumeDefaults = fun _ _ _ -> Holder.Migration.Refused("twin: unused") }

let twin : Model =
  { real with
      Name = "assume-defaults"
      Decide = (fun _ _ _ -> Holder.Migration.Refused("twin: unused"))
      // The bug, expressed as the real decision with every field assumed
      // defaulted: a new field with no default is added rather than refused.
      AssumeDefaults =
        fun oldShape newShape _ ->
          TypeShapeMigration.decideRecord oldShape newShape (fun _ -> true) }

let models = [ real; twin ]

/// A shape is undecidable if ANY field is `Undecidable`, or if a field that
/// exists in both changed kind. Both are the cases the real model must refuse.
let hasUndecidable (oldShape: RecordShape) (newShape: RecordShape) : bool =
  let oldIndex = TypeShapeMigration.fieldIndex oldShape
  let undecidableKind = newShape.Fields |> List.exists (fun f -> match f.Kind with | FieldKind.Undecidable _ -> true | _ -> false)

  let retyped =
    newShape.Fields
    |> List.exists (fun f ->
      match oldIndex |> Map.tryFind f.Name with
      | Some oldKind -> oldKind <> f.Kind
      | None -> false)

  undecidableKind || retyped

/// A new field with no default is also a must-refuse, and the ONLY case the
/// twin gets wrong — so the invariant is stated exactly there.
let missingDefault (scenario: Scenario) : bool =
  scenario.NewFields
  |> List.exists (fun (name, _) -> not (scenario.OldFields |> List.contains name) && not (scenario.Defaulted |> List.contains name))

let runWith (model: Model) (scenario: Scenario) : Trace =
  let oldShape : RecordShape =
    { TypeName = "T"
      Fields = scenario.OldFields |> List.map (fun name -> { Name = name; Kind = FieldKind.IntField }) }

  let newShape : RecordShape =
    { TypeName = "T"
      Fields = scenario.NewFields |> List.map (fun (name, kind) -> { Name = name; Kind = kind }) }

  let hasDefault name = scenario.Defaulted |> List.contains name
  let real = TypeShapeMigration.decideRecord oldShape newShape hasDefault
  let underTwin = model.AssumeDefaults oldShape newShape hasDefault

  let decision =
    match model.Name with
    | "real" -> real
    | _ -> underTwin

  let migrated =
    match decision with
    | Holder.Migration.Carried(_, _) -> true
    | Holder.Migration.Refused _ -> false

  // The claim the invariant checks: a migration that reported success while
  // the shapes actually contain something undecidable.
  let claimedWithUndecidable = migrated && (hasUndecidable oldShape newShape || missingDefault scenario)

  { Scenario = scenario; Observation = { Migrated = migrated; ClaimedCarryWithUndecidable = claimedWithUndecidable } }

let run (scenario: Scenario) : Trace = runWith real scenario
let runTwin (scenario: Scenario) : Trace = runWith twin scenario

/// The seeded scenarios. Short shapes, so the interesting combinations are
/// reachable and every field's role is visible in a failure message.
let scenarioOf (seed: int) : Scenario =
  let rng = System.Random(seed)

  let names = [ "Alpha"; "Beta"; "Gamma" ]
  let oldCount = 1 + (seed % 3)
  let oldFields = names |> List.truncate oldCount

  let newCount = oldCount + (seed % 2)
  let newFields =
    names
    |> List.truncate newCount
    |> List.mapi (fun i name ->
      // Every third field is undecidable, and every fourth changes kind, so
      // the battery really reaches both refusal reasons.
      let kind =
        match (i + seed) % 7 with
        | 0 -> FieldKind.Undecidable "byref-like"
        | 3 -> FieldKind.StringField
        | _ -> FieldKind.IntField

      name, kind)

  // Every other new field gets a default, so the missing-default refusal is
  // reached as often as the others.
  let defaulted =
    newFields
    |> List.mapi (fun i (name, _) -> i, name)
    |> List.filter (fun (i, _) -> (i + seed) % 2 = 0)
    |> List.map snd

  ignore rng
  { Seed = seed
    OldFields = oldFields
    NewFields = newFields
    Defaulted = defaulted }
