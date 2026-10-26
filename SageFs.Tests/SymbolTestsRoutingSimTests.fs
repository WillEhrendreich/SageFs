module SageFs.Tests.SymbolTestsRoutingSimTests

open Expecto
open Expecto.Flip
open SageFs.Features.LiveTesting
open SageFs.Simulation
open SageFs.Simulation.SymbolTestsRoutingSim
open SageFs.Simulation.SymbolTestsRoutingInvariants

/// DST for "which session's tests does `GET /api/dependency-graph` answer with". The subject is the
/// real `SymbolTests.ofCycle` over the asked session's own cycle, which `DependencyGraphRoute.read`
/// composes. The fold is `SageFs.Simulation/SymbolTestsRoutingSim.fs`; the twin answers every
/// session from the primary's cycle, the shape the route had.

let simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s), replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Router t.Scenario.Seed t.Scenario.Ops vs

let firedBy (t: Trace) = violations t |> List.map fst

[<Tests>]
let tests =
  testList "DST symbol-to-tests routing" [

    testList "the real answer holds the invariants" [

      testPropertyWithConfig simConfig
        "WHY — seeded scenarios: the answer for a session is that session's own, whoever is primary"
        <| fun (seed: int) -> assertHolds (run (SymbolTestsRoutingGenerators.fromSeed seed))

      testCase "WHY — primaryEmptyOtherAnalysed: a session that ran its tests is answered with its own symbols while the primary has analysed nothing" <| fun _ ->
        let t = run SymbolTestsRoutingGenerators.primaryEmptyOtherAnalysed
        let d = t.Final.Decisions |> List.exactlyOne
        d.Got.State |> Expect.equal "the map is filled" SymbolTestsState.Mapped
        d.Got.SymbolToTests |> Map.keys |> Set.ofSeq
        |> Expect.equal "both of session 1's symbols" (symbolsOf 1 |> Set.ofArray)
        assertHolds t

      testCase "WHY — twoSessionsEachItsOwn: each session is answered with its own symbols and the primary is the other" <| fun _ ->
        let t = run SymbolTestsRoutingGenerators.twoSessionsEachItsOwn
        t.Final.Decisions
        |> List.map (fun d -> d.Got.SymbolToTests |> Map.keys |> Set.ofSeq)
        |> Expect.equal "session 0's symbols, then session 2's" [ symbolsOf 0 |> Set.ofArray; symbolsOf 2 |> Set.ofArray ]
        assertHolds t

      testCase "WHY — analysedNotRun: the empty answer says coverage is what is missing" <| fun _ ->
        let t = run SymbolTestsRoutingGenerators.analysedNotRun
        (t.Final.Decisions |> List.exactlyOne).Got.State
        |> Expect.equal "no coverage recorded" SymbolTestsState.NoCoverageRecorded
        assertHolds t

      testCase "WHY — nothingYet: the empty answer says nothing has been analysed" <| fun _ ->
        let t = run SymbolTestsRoutingGenerators.nothingYet
        (t.Final.Decisions |> List.exactlyOne).Got.State
        |> Expect.equal "nothing analysed" SymbolTestsState.NothingAnalysed
        assertHolds t
    ]

    testList "the twin reproduces the wrong shape" [

      testCase "WHY — REPRODUCED: the primary-reader twin answers a session from another session's cycle" <| fun _ ->
        firedBy (runPrimaryReader SymbolTestsRoutingGenerators.primaryEmptyOtherAnalysed)
        |> Expect.contains
             "the never-empty invariant must fire"
             "a-session-that-analysed-and-ran-its-tests-is-never-answered-empty"

      testCase "WHY — REPRODUCED: the primary-reader twin lists the primary's symbols for another session" <| fun _ ->
        firedBy (runPrimaryReader SymbolTestsRoutingGenerators.twoSessionsEachItsOwn)
        |> Expect.contains
             "the only-its-own invariant must fire"
             "the-answer-lists-only-the-asked-sessions-own-symbols-and-tests"

      testCase "WHY — the invariants have teeth: some seeded scenario violates them under the twin" <| fun _ ->
        [ 1 .. 120 ]
        |> List.exists (fun seed ->
          not (List.isEmpty (violations (runPrimaryReader (SymbolTestsRoutingGenerators.fromSeed seed)))))
        |> Expect.isTrue "the primary-reader twin is caught by some seed"
    ]

    testList "determinism and replay" [

      testProperty "WHY — the same seed replays to the identical final state" <|
        fun (seed: int) ->
          let a = run (SymbolTestsRoutingGenerators.fromSeed seed)
          let b = run (SymbolTestsRoutingGenerators.fromSeed seed)
          a.Final = b.Final
    ]
  ]
