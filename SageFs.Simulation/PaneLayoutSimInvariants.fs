namespace SageFs.Simulation

open SageFs
open SageFs.Simulation.PaneLayoutSim

/// Invariants over `PaneLayoutSim`'s trace. Each is a pure function over the whole trace and names the step it fails at.
module PaneLayoutSimInvariants =

  type Violation = { Index: int; Why: string }

  let private keyOf (workflow: LayoutWorkflow) : string = LayoutWorkflow.key workflow

  let private savedAt (file: FileState) (workflow: LayoutWorkflow) : string option =
    Map.tryFind (keyOf workflow) (layerOf file)

  let private layoutIn (file: FileState) (workflow: LayoutWorkflow) : PaneLayout =
    LayoutBook.layoutOf workflow (LayoutBook.read (layerOf file))

  /// Every workflow's layout, as the file holds it, and every layout a tab drew, places every pane exactly once. A crash part way
  /// through a reset, a hand-corrupted file and a daemon restart never leave a half layout.
  let everyLayoutIsWhole (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.collect (fun (i, s) ->
      [ for workflow in LayoutWorkflow.all do
          if not (PaneLayout.isValid (layoutIn s.File workflow)) then
            yield { Index = i; Why = sprintf "the file holds a layout for %A that does not place every pane once" workflow }
        for KeyValue(tabNumber, tab) in s.Tabs do
          if not (PaneLayout.isValid tab.Drawn) then
            yield { Index = i; Why = sprintf "tab %d drew a layout that does not place every pane once" tabNumber } ])

  /// A readable file stays readable: only a hand edit corrupts it, and an edit that lands repairs a corrupt one.
  let anEditNeverCorruptsTheFile (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.choose (fun (i, s) ->
      match s.Op, s.Before, s.File with
      | Some Op.CorruptFile, _, _ -> None
      | _, (FileState.Absent | FileState.Layer _), FileState.Corrupt ->
        Some { Index = i; Why = sprintf "%A turned a readable file into a corrupt one" s.Op }
      | _ -> None)

  /// An edit in one workflow changes no other workflow's saved key. This is what two tabs on different workflows rely on, and
  /// what a save built from a stale copy of the whole book breaks.
  let anEditChangesOnlyItsOwnWorkflow (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.collect (fun (i, s) ->
      match s.Touched with
      | None -> []
      | Some touched ->
        [ for workflow in LayoutWorkflow.all do
            if workflow <> touched && savedAt s.Before workflow <> savedAt s.File workflow then
              yield { Index = i; Why = sprintf "an edit in %A changed the saved layout of %A" touched workflow } ])

  /// A reset that ran to its end leaves each workflow it was aimed at on its default.
  let aResetIsTheDefault (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.collect (fun (i, s) ->
      match s.Completed, s.ResetOf with
      | true, (_ :: _ as workflows) ->
        [ for workflow in workflows do
            if layoutIn s.File workflow <> PaneLayout.defaultFor workflow then
              yield { Index = i; Why = sprintf "after a reset, %A is not on its default layout" workflow } ]
      | _ -> [])

  /// A reset of everything that died part way leaves each workflow with the layout it had or with none saved, never in between.
  let aCrashedResetLeavesEachWorkflowWholeOrCleared (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.collect (fun (i, s) ->
      match s.Op with
      | Some(Op.CrashDuringResetAll _) ->
        [ for workflow in LayoutWorkflow.all do
            let before = savedAt s.Before workflow
            let after = savedAt s.File workflow
            if after <> before && after <> None then
              yield { Index = i; Why = sprintf "a crashed reset left %A with text that was neither its old layout nor cleared" workflow } ]
      | _ -> [])

  /// What a tab draws after it renders is the layout the file holds for its workflow, and the daemon's push (or a restart) brings
  /// every tab there. Two tabs on one workflow draw the same layout.
  let aRenderIsTheFile (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.collect (fun (i, s) ->
      let checkTab (tabNumber: int) =
        let tab = Map.find tabNumber s.Tabs
        match tab.Drawn = layoutIn s.File tab.Workflow with
        | true -> []
        | false -> [ { Index = i; Why = sprintf "tab %d drew a layout that is not what the file holds for %A" tabNumber tab.Workflow } ]
      match s.Op with
      | Some(Op.Render tabNumber)
      | Some(Op.Switch(tabNumber, _)) -> checkTab tabNumber
      | Some Op.Deliver
      | Some Op.Restart -> [ 0 .. tabCount - 1 ] |> List.collect checkTab
      | _ -> [])
