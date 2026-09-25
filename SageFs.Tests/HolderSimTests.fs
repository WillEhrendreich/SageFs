module SageFs.Tests.HolderSimTests

/// WHY — `SageFs.Simulation.HolderSim` runs seeded chains of carry/refuse
/// attempts against one cell. The property under test is that a refusal
/// changes NOTHING and a carry is actually applied — the two failure modes a
/// real implementation would plausibly have, and each is paired with a twin
/// that reintroduces it so a green run means something.

open Expecto
open Expecto.Flip
open SageFs.Simulation.HolderSim
open SageFs.Simulation.HolderInvariants

let private seeds = [ 1 .. 2000 ]

let private battery = lazy (seeds |> List.map (fun seed -> run (scenarioOf seed)))

let private report label (violations: Violation list) =
  if not (List.isEmpty violations) then
    let first = List.head violations

    failtestf
      "%s: seed %d — %s (replay: HolderSim.run (scenarioOf %d))"
      label
      first.Seed
      first.Why
      first.Seed

[<Tests>]
let tests =
  testList "Holder DST" [

    testCase "SAFETY — a refused migration changes nothing about what the cell serves" <| fun _ ->
      battery.Value
      |> List.iter (fun trace ->
        trace
        |> refusalChangesNothing
        |> report "refusal mutated the cell")

    testCase "SAFETY — a carried migration is actually applied, so a save cannot claim a carry it did not make" <| fun _ ->
      battery.Value
      |> List.iter (fun trace ->
        trace
        |> carryIsApplied
        |> report "carry was dropped")

    testCase "REPLAY — the same seed always produces the same served values" <| fun _ ->
      let first = run (scenarioOf 4242)
      let second = run (scenarioOf 4242)

      (first.Observations |> List.map (fun o -> o.Served))
      |> Expect.equal "a seeded scenario must be deterministic" (second.Observations |> List.map (fun o -> o.Served))

    testCase "NON-VACUOUS — the battery really mixes carries and refusals" <| fun _ ->
      let observations = battery.Value |> List.collect (fun t -> t.Observations)

      let carries = observations |> List.filter (fun o -> not o.Refused)
      let refusals = observations |> List.filter (fun o -> o.Refused)

      (carries.Length, 0)
      |> Expect.isGreaterThan "the battery must carry, or the apply invariant is untested"

      (refusals.Length, 0)
      |> Expect.isGreaterThan "the battery must refuse, or the no-change invariant is untested"

      // A refusal FOLLOWED BY a carry is the case a naive implementation gets
      // wrong, so require the battery to contain it.
      let mixed =
        observations
        |> List.pairwise
        |> List.exists (fun (a, b) -> a.Refused && not b.Refused)

      mixed
      |> Expect.isTrue "the battery must contain a refusal followed by a carry"

    testCase "TWIN — a refusal that still swaps breaks the no-change invariant" <| fun _ ->
      let breaking = seeds |> List.filter (fun seed -> alwaysSwapBreaksRefusal (scenarioOf seed))

      (breaking.Length, 0)
      |> Expect.isGreaterThan "the always-swap twin must break the refusal invariant on some seed"

    testCase "TWIN — a dropped carry breaks the apply invariant" <| fun _ ->
      let breaking = seeds |> List.filter (fun seed -> neverSwapBreaksCarry (scenarioOf seed))

      (breaking.Length, 0)
      |> Expect.isGreaterThan "the never-swap twin must break the apply invariant on some seed"
  ]
