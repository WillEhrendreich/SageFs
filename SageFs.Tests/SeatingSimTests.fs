module SageFs.Tests.SeatingSimTests

open Expecto
open Expecto.Flip
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

    testList "the twins reproduce the shortcut" [

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
