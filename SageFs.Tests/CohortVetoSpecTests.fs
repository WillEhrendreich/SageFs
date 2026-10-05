module SageFs.Tests.CohortVetoSpecTests

open Expecto
open Expecto.Flip
open SageFs.Simulation

// The exhaustive proof for the four commands that tools issue now (`DelegateConductor`, `WithdrawLanding`,
// `VetoLanding`, `ResolveVeto`). See SageFs.Simulation/CohortVetoSpec.fs: five actors, every command against every
// landing and every target, every pipeline outcome, and a twin per rule that must be caught by it.

[<Tests>]
let tests =
  testList "DST cohort veto, withdraw and delegation spec (exhaustive proof)" [

    testCase "the bounded space is fully enumerated and no rule is violated at any state or step" <| fun _ ->
      let r = CohortVetoSpec.proof ()
      r.Complete |> Expect.isTrue "the frontier emptied within the cap, so an empty violation list is a proof of the bound"
      r.Capped |> Expect.isFalse "the cap was not hit"
      Expect.isGreaterThan "the model reaches a non-trivial space" (r.Nodes, 500)
      r.Violations |> Expect.equal "no rule is violated" []

    testCase "the exploration actually reaches every step the rules are about, so the proof is not vacuous" <| fun _ ->
      let r = CohortVetoSpec.proof ()
      let expected = CohortVetoSpec.facts |> List.map fst |> List.sort
      r.Reached |> Expect.equal "every named fact was reached at some step" expected

    testCase "every twin is caught by the rule it was built for" <| fun _ ->
      for (name, twin, rule) in CohortVetoSpec.twins do
        let r = CohortVetoSpec.explore twin CohortVetoSpec.defaultCap
        r.Violations |> Expect.contains (sprintf "the twin '%s' must violate %s" name rule) rule

    testCase "every rule has at least one twin, except the structural one, so no rule passes vacuously" <| fun _ ->
      let covered = CohortVetoSpec.twins |> List.map (fun (_, _, rule) -> rule) |> Set.ofList
      let ruleNames = (CohortVetoSpec.stateRules |> List.map fst) @ (CohortVetoSpec.stepRules |> List.map fst)
      // QUEUE-SERIAL has its own proof in CohortSpec.fs; AT-MOST-ONE-CONDUCTOR is structural (the seat is one
      // binding), and HANDOFF-LEAVES-ONE-CONDUCTOR is the rule that gives it teeth.
      let ownedElsewhere = set [ "QUEUE-SERIAL"; "AT-MOST-ONE-CONDUCTOR" ]
      ruleNames
      |> List.filter (fun r -> not (covered.Contains r) && not (ownedElsewhere.Contains r))
      |> Expect.equal "every rule is either caught by a twin here or owned by another proof" []
  ]
