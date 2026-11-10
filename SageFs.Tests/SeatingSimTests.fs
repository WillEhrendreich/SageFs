module SageFs.Tests.SeatingSimTests

open Expecto
open Expecto.Flip
open SageFs.Cohort
open SageFs.Simulation
open SageFs.Simulation.SeatingSim
open SageFs.Simulation.SeatingInvariants

/// DST over members that join, leave and ask for an empty conductor seat while a person answers yes, no, nothing, or is not
/// there, folded through the REAL `SeatingFlow` and `Cohort.decide`. The twins are the shortcut the tool must never be: one
/// seats on the plan alone, one asks and then seats whatever came back. The invariants must FAIL against them.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 100 }

let private steps = 80

let private assertHolds (states: State list) =
  match violations states with
  | [] -> ()
  | found -> failtestf "INVARIANT VIOLATION (first of %d): %s" found.Length (List.head found)

let private mentions (invariant: string) (found: string list) = found |> List.exists (fun message -> message.StartsWith invariant)

let private seatingsIn (states: State list) =
  states |> List.filter (fun s -> match s.Attempt with Some a -> a.Seated | None -> false) |> List.length

/// alice-like: m0 joins and holds the seat, m1 joins, m0 leaves; then m1 asks and the person replies.
let private vacantThenAsk (reply: Reply) =
  [ SimEvent.Join 0; SimEvent.Join 1; SimEvent.Leave 0; SimEvent.Ask(1, 1, reply) ]

/// The seeds the reach and teeth cases run, a fixed range so the case is the same on every run.
let private referenceSeeds = [ 0 .. 199 ]

/// m0 joins and holds the seat, m1 joins; m1 asks to replace the holder (named as whoever holds it) and the person replies.
let private holderThenAskToReplace (reply: Reply) (intervening: int option) =
  [ SimEvent.Join 0; SimEvent.Join 1; SimEvent.Join 2; SimEvent.AskToReplace(1, 1, currentHolder, reply, intervening) ]

let private replacementsIn (states: State list) =
  states
  |> List.filter (fun s -> match s.Attempt with Some a -> a.Seated && a.Shown.IsSome | None -> false)
  |> List.length

[<Tests>]
let tests =
  testList "DST seating a conductor" [

    testList "the real flow holds every invariant" [

      testPropertyWithConfig simConfig "seeded lifecycles: a seat changes hands only on a yes, and nobody is asked what the cohort would refuse" <|
        fun (seed: int) -> assertHolds (run Behavior.Real (scenarioOf seed steps))

      testProperty "same seed, same trace (replay by seed)" <| fun (seed: int) ->
        let events = scenarioOf seed steps
        let trace states = states |> List.map (fun (s: State) -> s.Cohort.Conductor, s.Attempt |> Option.map (fun a -> a.Asked, a.Seated))
        trace (run Behavior.Real events) |> Expect.equal "identical" (trace (run Behavior.Real events))

      testCase "a yes seats, and it is the only reply that does" <| fun _ ->
        let seated reply = vacantThenAsk reply |> run Behavior.Real |> seatingsIn
        seated Reply.Yes |> Expect.equal "yes seats" 1
        for reply in [ Reply.No; Reply.Dismiss; Reply.OtherAction; Reply.Silence; Reply.Broken; Reply.NotThere ] do
          seated reply |> Expect.equal (sprintf "%A does not" reply) 0

      testProperty "some seed reaches a seating, so the properties above are not about an empty run" <| fun (seed: int) ->
        [ for offset in 0 .. 20 -> seatingsIn (run Behavior.Real (scenarioOf (seed + offset) steps)) ]
        |> List.exists (fun n -> n > 0)
        |> Expect.isTrue "within 21 neighbouring seeds the real flow seats someone"
    ]

    testList "replacing a bound seat: only a yes, and only from the holder the person was shown" [

      testCase "a yes replaces the holder, and it is the only reply that does" <| fun _ ->
        let replaced reply = holderThenAskToReplace reply None |> run Behavior.Real |> replacementsIn
        replaced Reply.Yes |> Expect.equal "yes replaces" 1
        for reply in [ Reply.No; Reply.Dismiss; Reply.OtherAction; Reply.Silence; Reply.Broken; Reply.NotThere ] do
          replaced reply |> Expect.equal (sprintf "%A does not" reply) 0

      testCase "a yes over a seat that changed hands while the person read the question moves nothing" <| fun _ ->
        let states = holderThenAskToReplace Reply.Yes (Some 2) |> run Behavior.Real
        replacementsIn states |> Expect.equal "nobody was replaced" 0
        violations states |> Expect.isEmpty "and every invariant holds"
        (List.last states).Cohort.Conductor |> Expect.equal "the seat is with the member the holder handed it to" (ConductorBinding.Bound(memberOf 2))

      testCase "a yes with nothing in the way replaces the holder with the member who asked" <| fun _ ->
        let states = holderThenAskToReplace Reply.Yes None |> run Behavior.Real
        (List.last states).Cohort.Conductor |> Expect.equal "m1 holds the seat" (ConductorBinding.Bound(memberOf 1))

      testCase "seeds 0 to 199 reach a replacement in many runs, so the seeded property is not about a run that never replaces" <| fun _ ->
        let reaching = [ for seed in referenceSeeds -> replacementsIn (run Behavior.Real (scenarioOf seed steps)) ] |> List.filter (fun n -> n > 0) |> List.length
        (reaching >= 50) |> Expect.isTrue (sprintf "%d of %d runs replace someone" reaching (List.length referenceSeeds))

      testCase "seeds 0 to 199 reach an answer over a seat that changed hands in many runs, so the race is exercised" <| fun _ ->
        let raced (states: State list) =
          states
          |> List.exists (fun s -> match s.Attempt with Some a -> a.Asked = 1 && a.AtCommit.Conductor <> a.Before.Conductor | None -> false)
        let racing = [ for seed in referenceSeeds -> raced (run Behavior.Real (scenarioOf seed steps)) ] |> List.filter id |> List.length
        (racing >= 20) |> Expect.isTrue (sprintf "%d of %d runs hand the seat on while a question is open" racing (List.length referenceSeeds))

      testCase "seeds 0 to 199 hold every invariant against the real flow" <| fun _ ->
        for seed in referenceSeeds do
          assertHolds (run Behavior.Real (scenarioOf seed steps))
    ]

    testList "the twins reproduce the shortcut" [

      testCase "REPRODUCED - a tool that replaces on the plan alone replaces on a no" <| fun _ ->
        let found = violations (run Behavior.SkipsTheQuestionTwin (holderThenAskToReplace Reply.No None))
        found |> mentions "SEATS-ONLY-ON-A-YES" |> Expect.isTrue "a replacement after a no is caught"
        found |> mentions "A-BOUND-SEAT-CHANGES-HANDS-ONLY-FROM-THE-HOLDER-SHOWN" |> Expect.isTrue "and so is the holder-shown rule, which also needs a yes"
        violations (run Behavior.Real (holderThenAskToReplace Reply.No None)) |> Expect.isEmpty "the same events against the real flow hold"

      testCase "REPRODUCED - a tool that asks and ignores the answer replaces on a no" <| fun _ ->
        violations (run Behavior.IgnoresTheAnswerTwin (holderThenAskToReplace Reply.Silence None))
        |> mentions "SEATS-ONLY-ON-A-YES"
        |> Expect.isTrue "caught: it asked and still replaced"

      testCase "REPRODUCED - a tool that replaces whoever holds the seat when the answer comes takes it from a holder the person was never shown" <| fun _ ->
        let found = violations (run Behavior.ReplacesWhoeverHoldsNowTwin (holderThenAskToReplace Reply.Yes (Some 2)))
        found |> mentions "A-BOUND-SEAT-CHANGES-HANDS-ONLY-FROM-THE-HOLDER-SHOWN" |> Expect.isTrue "caught: m2 was not the holder the person saw"
        violations (run Behavior.ReplacesWhoeverHoldsNowTwin (holderThenAskToReplace Reply.Yes None))
        |> Expect.isEmpty "with nothing in the way the twin and the real flow agree, so the fault is only the race"

      testCase "seeds 0 to 199 break the replace-whoever-holds-now twin in some runs, and only through the holder-shown invariant" <| fun _ ->
        let found = [ for seed in referenceSeeds -> violations (run Behavior.ReplacesWhoeverHoldsNowTwin (scenarioOf seed steps)) ]
        let broken = found |> List.filter (List.isEmpty >> not)
        (List.length broken >= 5) |> Expect.isTrue (sprintf "the race twin breaks an invariant in %d of %d runs" (List.length broken) (List.length referenceSeeds))
        broken
        |> List.forall (mentions "A-BOUND-SEAT-CHANGES-HANDS-ONLY-FROM-THE-HOLDER-SHOWN")
        |> Expect.isTrue "every break is the holder-shown invariant, which is the rule the twin ignores"


      testCase "REPRODUCED - a tool that seats on the plan alone seats on a no, and on silence" <| fun _ ->
        for reply in [ Reply.No; Reply.NotThere ] do
          let found = violations (run Behavior.SkipsTheQuestionTwin (vacantThenAsk reply))
          found |> mentions "SEATS-ONLY-ON-A-YES" |> Expect.isTrue (sprintf "a seating after %A is caught" reply)
        violations (run Behavior.Real (vacantThenAsk Reply.No)) |> Expect.isEmpty "the same events against the real flow hold"

      testCase "REPRODUCED - a tool that asks and then ignores the answer seats on a no" <| fun _ ->
        let found = violations (run Behavior.IgnoresTheAnswerTwin (vacantThenAsk Reply.No))
        found |> mentions "SEATS-ONLY-ON-A-YES" |> Expect.isTrue "caught: it asked and still seated"

      testProperty "some seed breaks each twin (the invariants have teeth)" <| fun (seed: int) ->
        let broken (behavior: Behavior) =
          [ for offset in 0 .. 20 -> violations (run behavior (scenarioOf (seed + offset) steps)) ]
          |> List.exists (List.isEmpty >> not)
        broken Behavior.SkipsTheQuestionTwin |> Expect.isTrue "within 21 neighbouring seeds the skip-the-question twin breaks an invariant"
        broken Behavior.IgnoresTheAnswerTwin |> Expect.isTrue "within 21 neighbouring seeds the ignore-the-answer twin breaks an invariant"
    ]
  ]
