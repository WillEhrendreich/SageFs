module SageFs.Tests.CompileListSimTests

/// DST for the classifier that says which files a compile-list edit can affect (`SageFs.Core/Features/ProjectFileChange.fs`,
/// `affectedFiles`, through `SageFs.Simulation.CompileListSim`): a landing that only adds a source file used to run the whole
/// assembly, because coverage never names a project file.
///
/// The world is a project with an ordered file list, and the ground truth is F#'s rule written out apart from the classifier: a
/// file means its content and what the files compiled before it that it can see mean, in order. A seeded edit (an append, an
/// insertion that may shadow a name, a removal, a move) changes which files mean something new, and SAFETY says the real
/// classifier selects every one of them. The twins are real mutants, and each must be caught.
open System
open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.CompileListSim

let seeds = [ 1 .. 1500 ]

let describe (trace: Trace) : string =
  sprintf "seed=%d edit=%s before=%d files -> selected %A, truly affected %A"
    trace.Scenario.Seed
    (match trace.Scenario.Edit with
     | Edit.Insert placements -> sprintf "insert %A" (placements |> List.map (fun (i, f) -> i, f.Name))
     | Edit.Remove names -> sprintf "remove %A" names
     | Edit.Move (a, b) -> sprintf "move %d to %d" a b)
    trace.Scenario.Before.Length trace.Selected trace.Truth

let expectNone (label: string) (bad: Trace list) =
  match bad with
  | [] -> ()
  | _ -> failtestf "%s: %d of %d seeds.\n%s" label bad.Length seeds.Length (bad |> List.truncate 5 |> List.map describe |> String.concat "\n")

let violations (check: Trace -> bool) (name: string) (selector: Selector) : Trace list =
  seeds |> List.map (fun seed -> run name selector (CompileListGenerators.fromSeed seed)) |> List.filter (check >> not)

[<Tests>]
let tests =
  testList "DST which files a compile-list edit can affect" [

    testList "the real classifier" [
      testCase "SAFETY — over seeded appends, insertions (some shadowing a name), removals and moves, every file whose meaning changed is selected" <| fun _ ->
        violations safe "real" real |> expectNone "a file whose meaning changed was not selected"

      testCase "WITHIN THE ORDER BOUND — it never selects a file the compile order alone says cannot be affected, except the added ones" <| fun _ ->
        violations withinOrderBound "real" real |> expectNone "a file outside the order bound was selected"

      testCase "NARROW — a plain file nobody names, inserted anywhere, selects exactly itself" <| fun _ ->
        violations narrowForUnnamedInsertion "real" real |> expectNone "an unnamed insertion selected more than itself"

      testCase "WHY — the hand-made unnamed insertion into the middle of thirty files selects only the new file, where the order rule selects twenty-one" <| fun _ ->
        let trace = run "real" real CompileListGenerators.unnamedInsertion
        trace.Selected |> Expect.equal "only the new file" [ "unnamed.fs" ]
        (orderOnlyTwin (fun _ -> failwith "unused") (trace.Scenario.Before |> List.map _.Name) trace.After).Length
        |> Expect.equal "the order rule alone reshapes everything from the insertion on" 21
        safe trace |> Expect.isTrue "and it is safe"

      testCase "WHY — a file that names a module which a new file now shadows is selected, and so is the file that names that file" <| fun _ ->
        let trace = run "real" real CompileListGenerators.transitiveChain
        trace.Selected |> Expect.equal "the new file, the file that sees both Utils, and the file that names that file" [ "shadow.fs"; "g.fs"; "h.fs" ]
        safe trace |> Expect.isTrue "and it is safe"
    ]

    testList "the twins are real mutants and each is caught (teeth)" [
      testCase "TWIN — selecting only the added files is unsafe when a later file can see them" <| fun _ ->
        violations safe "added only" addedOnlyTwin |> Expect.isNonEmpty "SAFETY must catch a classifier that ignores what the new file shadows"

      testCase "TWIN — dropping the transitive step is unsafe, and the hand-made chain shows the case" <| fun _ ->
        violations safe "no transitive" noTransitiveTwin |> Expect.isNonEmpty "SAFETY must catch a classifier that stops one step short"
        safe (run "no transitive" noTransitiveTwin CompileListGenerators.transitiveChain) |> Expect.isFalse "h sits on g's changed signature"

      testCase "TWIN — one file short at the first difference is unsafe" <| fun _ ->
        violations safe "off by one" offByOneTwin |> Expect.isNonEmpty "SAFETY must catch an off-by-one at the first difference"

      testCase "TWIN — the order rule alone is safe and not narrow: NARROW separates it from the real classifier" <| fun _ ->
        violations safe "order only" orderOnlyTwin |> Expect.isEmpty "the order rule is a sound upper bound"
        violations narrowForUnnamedInsertion "order only" orderOnlyTwin |> Expect.isNonEmpty "NARROW must catch the rule that selects everything after an insertion"
    ]

    testList "the battery is not vacuous" [
      testCase "WHY — the seeded edits include every kind, shadowing, transitive reach and opaque files, so a green SAFETY is not green by default" <| fun _ ->
        let scenarios = seeds |> List.map CompileListGenerators.fromSeed
        let count (pick: Edit -> bool) = scenarios |> List.filter (fun s -> pick s.Edit) |> List.length
        (count (function Edit.Insert _ -> true | _ -> false) > 0) |> Expect.isTrue "insertions"
        (count (function Edit.Remove _ -> true | _ -> false) > 0) |> Expect.isTrue "removals"
        (count (function Edit.Move _ -> true | _ -> false) > 0) |> Expect.isTrue "moves"
        let traces = scenarios |> List.map (run "real" real)
        (traces |> List.exists (fun t -> t.Truth.Length > 1)) |> Expect.isTrue "an edit that affected more than one file"
        (traces |> List.exists (fun t -> t.Selected.Length < t.After.Length - 1)) |> Expect.isTrue "an edit the classifier narrowed"
        (traces |> List.exists (fun t -> t.Scenario.Before |> List.exists (fun f -> f.Opaque))) |> Expect.isTrue "a project with an opaque file"

      testCase "WHY — the real classifier selects strictly fewer files than the order rule on some seeds, so narrowing is measured and not assumed" <| fun _ ->
        let fewer =
          seeds
          |> List.exists (fun seed ->
            let scenario = CompileListGenerators.fromSeed seed
            let r = run "real" real scenario
            let o = run "order only" orderOnlyTwin scenario
            r.Selected.Length < o.Selected.Length)
        fewer |> Expect.isTrue "narrower than the order rule on at least one seed"
    ]

    testList "determinism / replay" [
      testProperty "same seed => identical scenario, selection and truth" <| fun (seed: int) ->
        let a = run "real" real (CompileListGenerators.fromSeed seed)
        let b = run "real" real (CompileListGenerators.fromSeed seed)
        a.Selected = b.Selected && a.Truth = b.Truth && a.After = b.After
    ]
  ]
