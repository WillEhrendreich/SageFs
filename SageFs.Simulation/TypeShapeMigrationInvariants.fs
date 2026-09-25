module SageFs.Simulation.TypeShapeMigrationInvariants

/// WHY — the design's rule is "refuse everything else", and the failure that
/// matters is a migration that reports SUCCESS while carrying a field nobody
/// could decide. That is a claim about combinations, so it is folded, and the
/// twin that breaks it is the exact bug the rule exists to stop.

open SageFs
open SageFs.Simulation.TypeShapeMigrationSim

type Violation =
  { Seed: int
    Trace: Trace
    Why: string }

let private fail seed trace why : Violation list = [ { Seed = seed; Trace = trace; Why = why } ]

/// Whether the trace's shapes contain anything undecidable. Derived from the
/// trace's own decision inputs so it cannot drift from the sim. Declared before
/// the invariants that use it, so the file reads top-down.
let hasUndecidableFor (trace: Trace) : bool =
  let oldIndex =
    trace.Scenario.OldFields
    |> List.map (fun name -> name, SageFs.TypeShapeMigration.FieldKind.IntField)
    |> Map.ofList

  trace.Scenario.NewFields
  |> List.exists (fun (name, kind) ->
    match kind with
    | SageFs.TypeShapeMigration.FieldKind.Undecidable _ -> true
    | _ ->
      match oldIndex |> Map.tryFind name with
      | Some old -> old <> kind
      | None -> false)

/// SAFETY — a migration that claims to have carried must not have carried
/// anything undecidable. There is no "mostly migrated" branch, and this is the
/// invariant that says so.
let neverClaimsAnUndecidableCarry (trace: Trace) : Violation list =
  if trace.Observation.ClaimedCarryWithUndecidable then
    fail trace.Scenario.Seed trace "a migration claimed a carry over a shape the rules cannot decide"
  else
    []

/// SAFETY — the inverse must also be absent: a fully decidable shape should
/// migrate. Without this, "always refuse" would pass the invariant above, and
/// a product that never migrates is not the thing we are building.
let decidableShapesMigrate (trace: Trace) : Violation list =
  let decidable = (not (hasUndecidableFor trace)) && not (trace.Scenario |> missingDefault)

  if decidable && not trace.Observation.Migrated then
    fail trace.Scenario.Seed trace "a fully decidable shape refused, so the migration can never actually apply"
  else
    []

let all (trace: Trace) : Violation list =
  neverClaimsAnUndecidableCarry trace @ decidableShapesMigrate trace

/// TWIN — treating every new field as defaulted must break the invariant.
let assumeDefaultsBreaksTheRule (scenario: Scenario) : bool =
  not (List.isEmpty (neverClaimsAnUndecidableCarry (runTwin scenario)))
