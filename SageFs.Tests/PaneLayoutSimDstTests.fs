/// Deterministic Simulation Testing for the dashboard's panel layouts: three tabs on different workflows editing, rendering and
/// being pushed late, a crashed reset, a corrupted file and a restart, folded through the real `LayoutBook.writesFor` and layer
/// function. The twin saves the whole book from a tab's stale copy, and the "an edit changes only its own workflow" invariant
/// catches it. The seeds are checked to reach the cases the invariants are about, so a green run means something.
module SageFs.Tests.PaneLayoutSimDstTests

open Expecto
open Expecto.Flip
open SageFs
open SageFs.Simulation
open SageFs.Simulation.PaneLayoutSim

let seeds = [ 1 .. 400 ]

let violationsFor (behavior: Behavior) (invariant: State list -> PaneLayoutSimInvariants.Violation list) =
  seeds
  |> List.map (fun seed -> let scenario = scenarioOf seed in scenario, invariant (trace behavior scenario))
  |> List.filter (fun (_, violations) -> not (List.isEmpty violations))

let expectNone (label: string) (bad: (Scenario * PaneLayoutSimInvariants.Violation list) list) =
  match bad with
  | [] -> ()
  | _ ->
    failtestf "%s: %d of %d seeds.\n%s" label bad.Length seeds.Length
      (bad
       |> List.truncate 3
       |> List.map (fun (s, vs) ->
         sprintf "seed=%d\n  %s" s.Seed (vs |> List.truncate 3 |> List.map (fun v -> sprintf "[%d] %s" v.Index v.Why) |> String.concat "\n  "))
       |> String.concat "\n")

let realTraces = lazy (seeds |> List.map (fun seed -> trace Behavior.Real (scenarioOf seed)))

[<Tests>]
let paneLayoutSimDstTests =
  testList "Pane layout DST" [

    testCase "the scenarios are deterministic: the same seed gives the same schedule and the same end state" <| fun _ ->
      (scenarioOf 7).Ops |> Expect.equal "the schedule" (scenarioOf 7).Ops
      let a = trace Behavior.Real (scenarioOf 7) |> List.last
      let b = trace Behavior.Real (scenarioOf 7) |> List.last
      (a.File, a.Tabs) |> Expect.equal "the end state" (b.File, b.Tabs)

    testList "the real layout code holds every invariant" [
      testCase "EVERY-LAYOUT-IS-WHOLE, in the file and on every tab, through crashes, corruption and restarts" <| fun _ ->
        violationsFor Behavior.Real PaneLayoutSimInvariants.everyLayoutIsWhole
        |> expectNone "a layout did not place every pane once"

      testCase "AN-EDIT-NEVER-CORRUPTS-THE-FILE" <| fun _ ->
        violationsFor Behavior.Real PaneLayoutSimInvariants.anEditNeverCorruptsTheFile
        |> expectNone "an edit corrupted a readable file"

      testCase "AN-EDIT-CHANGES-ONLY-ITS-OWN-WORKFLOW" <| fun _ ->
        violationsFor Behavior.Real PaneLayoutSimInvariants.anEditChangesOnlyItsOwnWorkflow
        |> expectNone "an edit changed another workflow's saved layout"

      testCase "A-RESET-IS-THE-DEFAULT" <| fun _ ->
        violationsFor Behavior.Real PaneLayoutSimInvariants.aResetIsTheDefault
        |> expectNone "a reset that ran to its end left a workflow off its default"

      testCase "A-CRASHED-RESET-LEAVES-EACH-WORKFLOW-WHOLE-OR-CLEARED" <| fun _ ->
        violationsFor Behavior.Real PaneLayoutSimInvariants.aCrashedResetLeavesEachWorkflowWholeOrCleared
        |> expectNone "a crashed reset left a workflow half way"

      testCase "A-RENDER-IS-THE-FILE, and a push or a restart brings every tab there" <| fun _ ->
        violationsFor Behavior.Real PaneLayoutSimInvariants.aRenderIsTheFile
        |> expectNone "a tab drew something other than what the file holds"
    ]

    testList "the seeds reach the interesting cases, so a green run means something" [
      testCase "some seeds repair a corrupt file with an edit that lands" <| fun _ ->
        realTraces.Value
        |> List.exists (List.exists (fun s -> s.Before = FileState.Corrupt && (match s.File with FileState.Layer _ -> true | _ -> false)))
        |> Expect.isTrue "at least one seed edits over a corrupt file"

      testCase "some seeds crash a reset of everything after some keys were cleared" <| fun _ ->
        realTraces.Value
        |> List.exists (List.exists (fun s ->
          match s.Op with
          | Some(Op.CrashDuringResetAll _) -> layerOf s.Before <> layerOf s.File
          | _ -> false))
        |> Expect.isTrue "at least one seed dies part way through a reset that had something to clear"

      testCase "some seeds have two workflows saved at once" <| fun _ ->
        realTraces.Value
        |> List.exists (List.exists (fun s ->
          LayoutWorkflow.all |> List.filter (fun w -> Map.containsKey (LayoutWorkflow.key w) (layerOf s.File)) |> List.length >= 2))
        |> Expect.isTrue "at least one seed holds layouts for two workflows"

      testCase "some seeds have a tab drawing a layout that is not the default" <| fun _ ->
        realTraces.Value
        |> List.exists (List.exists (fun s -> s.Tabs |> Map.exists (fun _ tab -> tab.Drawn <> PaneLayout.defaultFor tab.Workflow)))
        |> Expect.isTrue "at least one seed moves a panel and a tab draws it"

      testCase "some seeds have a tab rendering from a copy of the file that is out of date" <| fun _ ->
        realTraces.Value
        |> List.exists (List.exists (fun s -> s.Tabs |> Map.exists (fun _ tab -> tab.SeenLayer <> layerOf s.File)))
        |> Expect.isTrue "at least one seed has a push that has not reached a tab yet"
    ]

    testCase "TWIN: a save of the whole book from a tab's stale copy is caught by the own-workflow invariant" <| fun _ ->
      violationsFor Behavior.StaleBookTwin PaneLayoutSimInvariants.anEditChangesOnlyItsOwnWorkflow
      |> List.isEmpty
      |> Expect.isFalse "two tabs on different workflows lose each other's edit and the sim sees it"

    testCase "TWIN: the same twin is not caught by the invariants that are about something else, so each invariant has its own subject" <| fun _ ->
      violationsFor Behavior.StaleBookTwin PaneLayoutSimInvariants.everyLayoutIsWhole
      |> List.isEmpty
      |> Expect.isTrue "the twin writes whole layers, so they stay whole"
  ]
