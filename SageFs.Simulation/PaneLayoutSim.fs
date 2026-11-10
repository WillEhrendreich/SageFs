namespace SageFs.Simulation

open System
open SageFs

/// Deterministic Simulation Testing for the dashboard's panel layouts: seeded schedules of three browser tabs on different
/// workflows moving, closing, reopening and resetting panels, tabs rendering and the daemon's push reaching them late, a crash
/// part way through a multi-key reset, a hand-corrupted settings file and a daemon restart. Every edit folds through the REAL
/// `LayoutBook.writesFor` and the real layer function (`PaneLayoutStore.applyWrite`, which is `SettingsStore.applyChange`), and
/// every render reads the layer with the real `LayoutBook.read`. The file's own atomicity and lock are `SettingsStoreSim`'s
/// subject; this one is about what the layout code decides to write. The schedule is data: an op list a seed produces, and
/// replaying a seed gives the same trace.
///
/// The twin is a save that writes the whole book from the tab's last-rendered copy, which is what a tab holding a stale
/// in-memory layout would do. Two tabs editing different workflows before the push reaches either lose each other's edit,
/// and the invariant that an edit changes only its own workflow's key catches it.
module PaneLayoutSim =

  /// The settings layer file as the layout code sees it.
  [<RequireQualifiedAccess>]
  type FileState =
    | Absent
    | Layer of Map<string, string>
    /// Someone saved the file with a typo.
    | Corrupt

  [<RequireQualifiedAccess>]
  type Op =
    /// A click on a menu button of the tab's own workflow.
    | EditOwn of tab: int * LayoutEdit
    /// The menu's "Reset all layouts".
    | ResetEverything of tab: int
    /// A one-key edit whose writer died before its move: the file is as it was.
    | CrashDuringEdit of tab: int * LayoutEdit
    /// "Reset all layouts" died after it had cleared `applied` of its keys.
    | CrashDuringResetAll of tab: int * applied: int
    | CorruptFile
    | Render of tab: int
    /// The daemon's push reaches every tab.
    | Deliver
    | Switch of tab: int * LayoutWorkflow
    | Restart

  type Scenario = { Seed: int; Ops: Op list }

  [<RequireQualifiedAccess>]
  type Behavior =
    | Real
    /// TWIN: the save is the whole layer, built from the tab's last-rendered copy.
    | StaleBookTwin

  type Tab =
    { Workflow: LayoutWorkflow
      /// The layer this tab last rendered from: what it believes the file holds.
      SeenLayer: Map<string, string>
      /// The layout this tab last drew for its workflow.
      Drawn: PaneLayout }

  type State =
    { Step: int
      Op: Op option
      File: FileState
      Before: FileState
      Tabs: Map<int, Tab>
      /// The workflow the op edited, when it edited one; None for a reset of everything and for every op that edits nothing.
      Touched: LayoutWorkflow option
      /// The op was a reset of one workflow or of everything, and what it was aimed at.
      ResetOf: LayoutWorkflow list
      /// The edit ran to the end (not a crash) and the file was readable or absent when it began.
      Completed: bool }

  let tabCount = 3

  let layerOf (file: FileState) : Map<string, string> =
    match file with
    | FileState.Absent -> Map.empty
    | FileState.Layer layer -> layer
    | FileState.Corrupt -> Map.empty

  let private draw (workflow: LayoutWorkflow) (file: FileState) : Map<string, string> * PaneLayout =
    let layer = layerOf file
    layer, LayoutBook.layoutOf workflow (LayoutBook.read layer)

  let private render (tabNumber: int) (file: FileState) (state: State) : State =
    let tab = Map.find tabNumber state.Tabs
    let layer, drawn = draw tab.Workflow file
    { state with Tabs = Map.add tabNumber { tab with SeenLayer = layer; Drawn = drawn } state.Tabs }

  let initial : State =
    let tab workflow = { Workflow = workflow; SeenLayer = Map.empty; Drawn = PaneLayout.defaultFor workflow }
    { Step = 0
      Op = None
      File = FileState.Absent
      Before = FileState.Absent
      Tabs = Map.ofList [ 0, tab LayoutWorkflow.Repl; 1, tab LayoutWorkflow.HotReload; 2, tab LayoutWorkflow.LiveTesting ]
      Touched = None
      ResetOf = []
      Completed = false }

  let private applyAll (writes: LayoutWrite list) (layer: Map<string, string>) : Map<string, string> =
    writes |> List.fold (fun acc write -> PaneLayoutStore.applyWrite write acc) layer

  /// What the daemon does for one edit under a behaviour: the writes it decides on, the layer they start from, and how many
  /// of them land.
  let private carryOut (behavior: Behavior) (edit: BookEdit) (applied: int option) (tab: Tab) (file: FileState) : FileState * bool =
    match behavior with
    | Behavior.Real ->
      let layer = layerOf file
      let writes = LayoutBook.writesFor layer edit
      let landed = match applied with Some n -> List.truncate n writes | None -> writes
      match writes, landed with
      | [], _ -> file, true
      | _, [] -> file, false
      | _, _ -> FileState.Layer(applyAll landed layer), List.length landed = List.length writes
    | Behavior.StaleBookTwin ->
      let writes = LayoutBook.writesFor tab.SeenLayer edit
      match writes with
      | [] -> file, true
      | _ -> FileState.Layer(applyAll writes tab.SeenLayer), true

  let step (behavior: Behavior) (state: State) (op: Op) : State =
    let state = { state with Step = state.Step + 1; Op = Some op; Before = state.File; Touched = None; ResetOf = []; Completed = false }
    let editBy (tabNumber: int) (edit: BookEdit) (applied: int option) (touched: LayoutWorkflow option) (resets: LayoutWorkflow list) =
      let tab = Map.find tabNumber state.Tabs
      let file, completed = carryOut behavior edit applied tab state.File
      { state with File = file; Touched = touched; ResetOf = resets; Completed = completed }
    match op with
    | Op.EditOwn(tabNumber, edit) ->
      let workflow = (Map.find tabNumber state.Tabs).Workflow
      let resets = match edit with LayoutEdit.Reset -> [ workflow ] | _ -> []
      editBy tabNumber (BookEdit.InWorkflow(workflow, edit)) None (Some workflow) resets
    | Op.ResetEverything tabNumber -> editBy tabNumber BookEdit.ResetAll None None LayoutWorkflow.all
    | Op.CrashDuringEdit(tabNumber, edit) ->
      let workflow = (Map.find tabNumber state.Tabs).Workflow
      editBy tabNumber (BookEdit.InWorkflow(workflow, edit)) (Some 0) (Some workflow) []
    | Op.CrashDuringResetAll(tabNumber, applied) -> editBy tabNumber BookEdit.ResetAll (Some applied) None []
    | Op.CorruptFile -> { state with File = FileState.Corrupt }
    | Op.Render tabNumber -> render tabNumber state.File state
    | Op.Deliver -> [ 0 .. tabCount - 1 ] |> List.fold (fun s tabNumber -> render tabNumber s.File s) state
    | Op.Switch(tabNumber, workflow) ->
      let tab = Map.find tabNumber state.Tabs
      render tabNumber state.File { state with Tabs = Map.add tabNumber { tab with Workflow = workflow } state.Tabs }
    | Op.Restart -> [ 0 .. tabCount - 1 ] |> List.fold (fun s tabNumber -> render tabNumber s.File s) state

  /// Every state, oldest first, including the initial one.
  let trace (behavior: Behavior) (scenario: Scenario) : State list =
    scenario.Ops |> List.scan (step behavior) initial

  let private randomEdit (rng: Random) : LayoutEdit =
    let pane = PanelId.all.[rng.Next PanelId.all.Length]
    match rng.Next 14 with
    | 0 | 1 | 2 -> LayoutEdit.MoveToRegion(pane, PaneRegion.Dock)
    | 3 -> LayoutEdit.MoveToRegion(pane, PaneRegion.Sidebar)
    | 4 | 5 -> LayoutEdit.MoveUp pane
    | 6 -> LayoutEdit.MoveDown pane
    | 7 | 8 -> LayoutEdit.Close pane
    | 9 | 10 -> LayoutEdit.Reopen pane
    | _ -> LayoutEdit.Reset

  /// A pure function of `seed`: replaying a seed gives the identical schedule.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random seed
    let n = 25 + rng.Next 60
    let ops =
      [ for _ in 1 .. n ->
          let tab = rng.Next tabCount
          match rng.Next 20 with
          | 0 | 1 | 2 | 3 | 4 | 5 | 6 -> Op.EditOwn(tab, randomEdit rng)
          | 7 -> Op.ResetEverything tab
          | 8 -> Op.CrashDuringResetAll(tab, rng.Next 3)
          | 9 -> Op.CrashDuringEdit(tab, randomEdit rng)
          | 10 -> Op.CorruptFile
          | 11 | 12 | 13 -> Op.Render tab
          | 14 | 15 | 16 -> Op.Deliver
          | 17 | 18 -> Op.Switch(tab, LayoutWorkflow.all.[rng.Next LayoutWorkflow.all.Length])
          | _ -> Op.Restart ]
    { Seed = seed; Ops = ops }
