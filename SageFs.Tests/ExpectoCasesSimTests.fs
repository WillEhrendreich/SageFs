module SageFs.Tests.ExpectoCasesSimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.ExpectoCasesSim

/// DST for the marks the gutter shows while a file is edited: the seeded pool is a generated
/// source tree plus an edit schedule (insert a line above, rename a case, delete a case, reorder
/// lists, add a computed name). Each step re-reads the text through the REAL
/// `ExpectoCases.discover` and `ExpectoCases.place`, then folds the REAL
/// `FileAnnotations.project`. The twin places by line offset from the binding without re-reading
/// the text, so it must drift after an insert above. See
/// `SageFs.Simulation/ExpectoCasesSim.fs` for the model, the generators and the invariants.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 300 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION - replay this scenario:\n  Seed=%d\n  Edits=%A\n  Violations=%A"
      t.Scenario.Seed t.Scenario.Edits vs

let private twinFires (scenario: Scenario) : Violation list = violations (runTwin scenario)

[<Tests>]
let tests =
  testList "DST expecto marks" [

    testList "the real place and projection hold the invariants" [
      testPropertyWithConfig simConfig
        "seeded pools - after every edit the mark still names what the text says"
        (fun (seed: int) ->
          let t = run (fromSeed seed)
          t.Placements
          |> List.exists (fun p -> p.IsExact)
          |> Expect.isTrue "the generator produced at least one Exact mark, so the run is not vacuous"
          assertHolds t)

      testCase "insertLineAboveACase: re-reading keeps every Exact mark on the line that holds its name" <| fun _ ->
        let t = run insertLineAboveACase
        t.Placements |> List.exists (fun p -> p.IsExact)
        |> Expect.isTrue "the scenario placed at least one Exact mark"
        assertHolds t
    ]

    testList "the offset twin drifts, because it stops re-reading the text" [
      testCase "insertLineAboveACase: the twin puts a mark on a line that no longer holds the name" <| fun _ ->
        match twinFires insertLineAboveACase with
        | [] -> failtest "the twin must violate the name-on-line invariant once a line is inserted above it (a twin that never breaks proves nothing)"
        | vs ->
          vs
          |> List.exists (function NameNotOnLine _ -> true | _ -> false)
          |> Expect.isTrue "the twin's stale offset lands on a line whose text is not the case's name"

      testCase "same seed, same trace: the real run and the twin are both deterministic" <| fun _ ->
        let scenario = fromSeed 42
        run scenario |> Expect.equal "two runs of one seed place identically" (run scenario)
        runTwin scenario |> Expect.equal "two twin runs of one seed place identically" (runTwin scenario)

      testCase "seeds 1 to 400: the twin breaks somewhere, and the real run never does" <| fun _ ->
        [ 1 .. 400 ]
        |> List.exists (fun seed -> not (List.isEmpty (twinFires (fromSeed seed))))
        |> Expect.isTrue "at least one seed makes the offset twin violate an invariant"
        [ 1 .. 400 ]
        |> List.filter (fun seed -> not (List.isEmpty (violations (run (fromSeed seed)))))
        |> Expect.isEmpty "the real run holds every invariant for every seed"
    ]
  ]
