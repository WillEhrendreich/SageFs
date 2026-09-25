module SageFs.Simulation.HolderInvariants

/// WHY — the holder's two safety properties, each paired with a twin that
/// reintroduces the corresponding bug. A twin that cannot break its
/// invariant means the invariant is asserting nothing, which the test suite
/// treats as a failure rather than a pass.

open SageFs
open SageFs.Holder
open SageFs.Simulation.HolderSim

/// A violation names the seed so the scenario replays exactly.
type Violation =
  { Seed: int
    Trace: Trace
    Why: string }

let private fail seed trace why : Violation list = [ { Seed = seed; Trace = trace; Why = why } ]

/// SAFETY — a refused migration must not change what the cell serves. The
/// before/after values are compared by their rendered form, so an invented
/// value is caught just as surely as a lost one.
let refusalChangesNothing (trace: Trace) : Violation list =
  let mutable previous = "0@v0"
  let mutable violations = []

  for observation in trace.Observations do
    if observation.Refused && observation.Served <> previous then
      violations <-
        violations
        @ fail trace.Scenario.Seed trace (sprintf "a refusal changed the served value from %s to %s" previous observation.Served)

    previous <- observation.Served

  violations

/// The version the rendered served-value carries, or -1 when it is unparseable.
let private servedVersion (served: string) : int =
  served.Split('@')
  |> Array.toList
  |> List.fold
    (fun found part ->
      match found with
      | Some _ -> found
      | None ->
        match System.Int32.TryParse(part) with
        | true, value -> Some value
        | _ -> None)
    None
  |> Option.defaultValue -1

/// A carried migration must actually be applied: the served version advances
/// by exactly one per carry. This is what stops a save claiming it carried
/// state while the app keeps serving the old value.
let carryIsApplied (trace: Trace) : Violation list =
  let mutable version = 0
  let mutable violations = []

  for observation in trace.Observations do
    if not observation.Refused then
      let served = servedVersion observation.Served

      if served <> version + 1 then
        violations <-
          violations
          @ fail
            trace.Scenario.Seed
            trace
            (sprintf "a carry was expected to serve version %d but served %d" (version + 1) served)

      version <- version + 1

  violations

/// The version a correct model must end on: the number of carries.
let expectedVersion (trace: Trace) : int =
  trace.Observations |> List.filter (fun o -> not o.Refused) |> List.length

/// Every invariant, in one list.
let all (trace: Trace) : Violation list =
  refusalChangesNothing trace @ carryIsApplied trace

// ── Twins ────────────────────────────────────────────────────────────

/// TWIN — `alwaysSwap` mutates the cell on a refusal, so this must fire.
let alwaysSwapBreaksRefusal (scenario: Scenario) : bool =
  let trace = runModel "always-swap" scenario
  not (List.isEmpty (refusalChangesNothing trace))

/// TWIN — `neverSwap` drops a successful migration, so this must fire.
let neverSwapBreaksCarry (scenario: Scenario) : bool =
  let trace = runModel "never-swap" scenario
  not (List.isEmpty (carryIsApplied trace))
