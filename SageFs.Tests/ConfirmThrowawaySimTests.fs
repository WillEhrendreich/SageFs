module SageFs.Tests.ConfirmThrowawaySimTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.ConfirmThrowawaySim
open SageFs.Simulation.ConfirmThrowawayInvariants

/// DST for the claim a throwaway worker needs to be safe: it is retired on every way a confirmation can end, whatever
/// order the world answers in. The fold under test is the real `ThrowawayBook.step`; see
/// `SageFs.Simulation/ConfirmThrowawaySim.fs` for the world around it and the twins.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s), replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

let private retired (t: Trace) : int list =
  t.Steps |> List.collect (fun s -> s.Effects) |> List.choose (function ThrowawayBook.Effect.Retire handle -> Some handle | _ -> None)

[<Tests>]
let confirmThrowawaySimTests =
  testList "DST throwaway workers" [

    testList "the real book holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded scenarios: the running workers are exactly the ones the book says serve, one per session, every ask answered"
        <| fun (seed: int) -> assertHolds (run (ConfirmThrowawayGenerators.fromSeed seed))

      testCase "served: the worker is retired when its generation ends" <| fun _ ->
        let t = run ConfirmThrowawayGenerators.served
        retired t |> Expect.hasLength "one worker retired" 1
        assertHolds t

      testCase "lateAfterEnd: a worker that comes up after its generation ended is retired the moment it is heard of" <| fun _ ->
        let t = run ConfirmThrowawayGenerators.lateAfterEnd
        retired t |> Expect.hasLength "the late worker is retired" 1
        assertHolds t

      testCase "supersededWhileStarting: the older worker, up after the newer one took its place, is retired" <| fun _ ->
        let t = run ConfirmThrowawayGenerators.supersededWhileStarting
        assertHolds t

      testCase "supersededWhileServing: the older worker is retired when the newer confirmation takes its place" <| fun _ ->
        let t = run ConfirmThrowawayGenerators.supersededWhileServing
        assertHolds t

      testCase "sessionGoes: the worker goes with its session" <| fun _ ->
        let t = run ConfirmThrowawayGenerators.sessionGoes
        retired t |> Expect.hasLength "the worker is retired" 1
        assertHolds t

      testCase "startFails: the caller is told, and nothing is left running" <| fun _ ->
        let t = run ConfirmThrowawayGenerators.startFails
        t.Steps
        |> List.collect (fun s -> s.Effects)
        |> List.exists (function ThrowawayBook.Effect.Fail _ -> true | _ -> false)
        |> Expect.isTrue "the waiting caller is told it failed"
        assertHolds t
    ]

    testList "the twins reproduce the bugs the invariants exist for" [
      testCase "REPRODUCED: a worker that comes up late is left running" <| fun _ ->
        violations (runLeaksLateStart ConfirmThrowawayGenerators.lateAfterEnd)
        |> List.map fst
        |> Expect.contains "live-is-what-serves must fire" "live-is-what-serves"

      testCase "REPRODUCED: a replaced worker is left running beside the new one" <| fun _ ->
        violations (runKeepsSuperseded ConfirmThrowawayGenerators.supersededWhileServing)
        |> List.map fst
        |> Expect.contains "one-per-session must fire" "one-per-session"

      testCase "REPRODUCED: the caller waiting on a replaced generation is never answered" <| fun _ ->
        violations (runStrandsWaiter ConfirmThrowawayGenerators.supersededWhileStarting)
        |> List.map fst
        |> Expect.contains "every-ask-is-answered must fire" "every-ask-is-answered"

      testCase "REPRODUCED: a worker whose session went is left running" <| fun _ ->
        violations (runIgnoresSessionGone ConfirmThrowawayGenerators.sessionGoes)
        |> List.map fst
        |> Expect.contains "live-is-what-serves must fire" "live-is-what-serves"

      testCase "the invariants have teeth: some seeded scenario violates them under each twin" <| fun _ ->
        let seeds = [ 1 .. 200 ]
        let anyViolates runTwin =
          seeds |> List.exists (fun seed -> violations (runTwin (ConfirmThrowawayGenerators.fromSeed seed)) |> List.isEmpty |> not)
        anyViolates runLeaksLateStart |> Expect.isTrue "leaks-late-start is caught by a generated scenario"
        anyViolates runKeepsSuperseded |> Expect.isTrue "keeps-superseded is caught by a generated scenario"
        anyViolates runStrandsWaiter |> Expect.isTrue "strands-waiter is caught by a generated scenario"
        anyViolates runIgnoresSessionGone |> Expect.isTrue "ignores-session-gone is caught by a generated scenario"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical steps (real book)" <| fun (seed: int) ->
        let a = run (ConfirmThrowawayGenerators.fromSeed seed)
        let b = run (ConfirmThrowawayGenerators.fromSeed seed)
        a.Steps = b.Steps
    ]
  ]
