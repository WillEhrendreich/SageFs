module SageFs.Tests.EvalBoundSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation.EvalBoundSim

/// DST for the claim an eval caller relies on: "you get an answer by your timeout plus the cancel's own bound, whatever the
/// eval does". The decision under test is the real `EvalWait.next`; see `SageFs.Simulation/EvalBoundSim.fs` for the scripted
/// eval (finishes, stops when cancelled, never answers), virtual time, and the three twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs -> failtestf "INVARIANT VIOLATION (%s) — replay this scenario:\n  %A\n  Violations=%A" t.Reducer t.Scenario vs

let private ids (t: Trace) = violations t |> List.map (fun v -> v.Id)

[<Tests>]
let evalBoundSimTests =
  testList "DST eval bound" [

    testList "the real decision holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios: answered by the bound, never cancelled early, cancelled once, Finished only when never cancelled"
        <| fun (seed: int) -> assertHolds (run (fromSeed seed))

      testCase "an eval nothing can stop is answered at the limit plus the answer bound, after one cancel" <| fun _ ->
        let t = run wedged
        assertHolds t
        t.CancelAsks |> Expect.equal "one ask, at the limit" [ wedged.Limit ]
        t.Answered |> Expect.equal "gave up when the bound ran out" (Some(wedged.Limit + wedged.AnswerWait, Answer.GaveUp))

      testCase "a loop with a check point is answered soon after the cancel, and the answer says it was cancelled" <| fun _ ->
        let t = run loops
        assertHolds t
        t.Answered |> Expect.equal "answered after the cancel" (Some(loops.Limit + 30, Answer.FinishedAfterCancel))

      testCase "work that finishes inside its limit is never cancelled" <| fun _ ->
        let t = run quick
        assertHolds t
        t.CancelAsks |> Expect.isEmpty "no cancel"
        t.Answered |> Expect.equal "answered when it finished" (Some(200, Answer.Finished))
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED: waiting without a bound never answers for an eval nothing can stop" <| fun _ ->
        ids (runNeverCancels wedged) |> Expect.contains "ANSWERED-BY-BOUND must fire" "ANSWERED-BY-BOUND"

      testCase "REPRODUCED: cancelling before the limit stops work that was entitled to its time" <| fun _ ->
        ids (runCancelsEarly loops) |> Expect.contains "NO-EARLY-CANCEL must fire" "NO-EARLY-CANCEL"

      testCase "REPRODUCED: waiting for the eval after the cancel never answers when it cannot stop" <| fun _ ->
        ids (runNeverGivesUp wedged) |> Expect.contains "ANSWERED-BY-BOUND must fire" "ANSWERED-BY-BOUND"

      testCase "the invariants have teeth: a generated scenario violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 300 ]
        let firesFor (twin: Scenario -> Trace) (id: string) =
          seeds |> List.exists (fun seed -> ids (twin (fromSeed seed)) |> List.contains id)
        firesFor runNeverCancels "ANSWERED-BY-BOUND" |> Expect.isTrue "never-cancels is caught"
        firesFor runCancelsEarly "NO-EARLY-CANCEL" |> Expect.isTrue "cancels-early is caught"
        firesFor runNeverGivesUp "ANSWERED-BY-BOUND" |> Expect.isTrue "never-gives-up is caught"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical trace" <| fun (seed: int) -> run (fromSeed seed) = run (fromSeed seed)
    ]
  ]
