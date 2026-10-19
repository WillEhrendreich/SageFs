module SageFs.Tests.StopOffLoopSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.StopOffLoopSim

/// DST for the claim "a stop never holds the session manager's mailbox". See `SageFs.Simulation/StopOffLoopSim.fs` for the
/// model, its two loops (the spec, which reaps aside, and the twin, which waits inside the loop the old way) and the shared
/// invariants. The REAL mailbox is judged by the same invariants in SessionManagerStopOffMailboxTests.fs.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match StopOffLoopInvariants.violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Sessions=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Sessions t.Scenario.Ops vs

let private violatedIds (t: Trace) : string list = StopOffLoopInvariants.violations t |> List.map fst

/// The most workers that were leaving at once at any point of a trace.
let private mostLeavingAtOnce (t: Trace) : int =
  t.Steps |> List.map (fun s -> Set.count s.InFlightAfter) |> List.fold max 0

[<Tests>]
let tests =
  testList "DST stop off the loop" [

    testList "the spec (reap aside) holds the invariants" [
      testPropertyWithConfig simConfig
        "seeded histories of stops, lists, gets, creates, restarts and releases — a busy stop never delays an answer"
        <| fun (seed: int) -> assertHolds (run (StopOffLoopGenerators.fromSeed seed))

      testCase "aListDuringAStop: a list posted while a stop is in flight is answered at once" <| fun _ ->
        let t = run StopOffLoopGenerators.aListDuringAStop
        t.Steps |> List.map (fun s -> s.Reply) |> Expect.allEqual "every step is answered" Reply.Answered
        assertHolds t

      testCase "fourStopsAtOnce: four stops overlap, all four are leaving together" <| fun _ ->
        let t = run StopOffLoopGenerators.fourStopsAtOnce
        mostLeavingAtOnce t |> Expect.equal "four workers leaving at once" 4
        assertHolds t

      testCase "everyCommandDuringAStop: get, create, restart and list are all answered while a stop is in flight" <| fun _ ->
        let t = run StopOffLoopGenerators.everyCommandDuringAStop
        t.Steps |> List.map (fun s -> s.Reply) |> Expect.allEqual "every step is answered" Reply.Answered
        assertHolds t
    ]

    testList "the twin reproduces the bug the invariants exist for" [
      testCase "REPRODUCED — a list posted behind a stop that waits inside the loop is left pending" <| fun _ ->
        let t = runAwaitsInline StopOffLoopGenerators.aListDuringAStop
        violatedIds t |> Expect.contains "the delayed list must be caught" "a-busy-stop-never-delays-an-answer"
        violatedIds t |> Expect.contains "the stop that waited must be caught" "a-stop-is-answered-at-once"

      testCase "REPRODUCED — four stops behind one another never leave together (the 22 s queue)" <| fun _ ->
        let t = runAwaitsInline StopOffLoopGenerators.fourStopsAtOnce
        mostLeavingAtOnce t |> Expect.equal "one worker leaving at a time" 1
        violatedIds t |> Expect.contains "serialised stops must be caught" "stops-of-different-sessions-overlap"

      testCase "REPRODUCED — every command posted behind the stop is pending" <| fun _ ->
        let t = runAwaitsInline StopOffLoopGenerators.everyCommandDuringAStop
        t.Steps
        |> List.filter (fun s -> s.Op <> Op.Stop 1 && s.Op <> Op.Release 1)
        |> List.map (fun s -> s.Reply)
        |> Expect.allEqual "get, create, restart and list all waited" Reply.Pending

      testCase "the invariants have teeth: some seeded history violates them under the twin" <| fun _ ->
        let violating =
          [ 1 .. 300 ]
          |> List.filter (fun seed -> StopOffLoopInvariants.violations (runAwaitsInline (StopOffLoopGenerators.fromSeed seed)) |> List.isEmpty |> not)
        (List.length violating > 100) |> Expect.isTrue (sprintf "most generated histories catch the twin, got %d of 300" (List.length violating))
    ]

    testList "the generator is not vacuous" [
      testCase "a good share of seeded histories have two or more workers leaving at once, so the overlap invariant is exercised" <| fun _ ->
        let overlapping =
          [ 1 .. 300 ]
          |> List.filter (fun seed -> mostLeavingAtOnce (run (StopOffLoopGenerators.fromSeed seed)) >= 2)
        (List.length overlapping > 60) |> Expect.isTrue (sprintf "overlap appears in many seeds, got %d of 300" (List.length overlapping))
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical trace (spec)" <| fun (seed: int) ->
        run (StopOffLoopGenerators.fromSeed seed) = run (StopOffLoopGenerators.fromSeed seed)

      testProperty "same seed => identical trace (twin)" <| fun (seed: int) ->
        runAwaitsInline (StopOffLoopGenerators.fromSeed seed) = runAwaitsInline (StopOffLoopGenerators.fromSeed seed)
    ]
  ]
