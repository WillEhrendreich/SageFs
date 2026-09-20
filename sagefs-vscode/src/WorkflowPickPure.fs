/// Pure decisions behind "Switch Workflow".
///
/// WHY this module exists — three measured defects in one 35-line command
/// (sagefs-ux-roast.md §4.2):
///
///   * The picker offered two workflows. `SessionWorkflow` has had THREE cases
///     since `LiveTesting` landed, and `LiveTesting` is the mode the
///     extension's own sidebar, gutters, CodeLenses and Test Explorer adapter
///     all exist to serve. It was simply absent from the only UI that can
///     select it.
///   * The picker called `HotReload` "Live", while `SessionWorkflow.label`
///     calls it "Hot Reload" — so you picked "Live" and the status bar then
///     said something else. The labels here are pinned to the daemon's own
///     `label` function by a contract test that loads the real
///     `SageFs.Core/WorkflowTypes.fs`, so they cannot drift again.
///   * Nothing marked which workflow the session was already in.
///
/// The wire strings are the aliases `SessionWorkflow.tryOfString` accepts
/// (`WorkflowTypes.fs:469-472`); the same contract test round-trips each one
/// through the real parser, so a rename on the daemon breaks here instead of
/// silently switching the user into the wrong mode.
///
/// No Fable dependency; tested under `dotnet fsi`
/// (tests/WorkflowPickContractTests.fsx).
module SageFs.Vscode.WorkflowPickPure

/// One workflow the user can switch into.
type WorkflowChoice = {
  /// The value POSTed to the daemon. Must parse via `SessionWorkflow.tryOfString`.
  Wire: string
  /// `SessionWorkflow.label` for that case, verbatim — this is what the status
  /// bar will read afterwards, so it is what the picker must say now.
  Label: string
  /// The codicon id shown beside the label.
  Icon: string
  /// One line on what the mode actually costs and gives.
  Detail: string
}

/// Every case of `SessionWorkflow`. A case missing here is a mode the user
/// cannot reach from VS Code at all.
let choices: WorkflowChoice list = [
  { Wire = "Interactive"
    Label = "REPL"
    Icon = "notebook"
    Detail = "Full interactive REPL. No hot reload, no test-on-save." }
  { Wire = "LiveTesting"
    Label = "Live Testing"
    Icon = "beaker"
    Detail = "Full REPL, and affected tests re-run as you type." }
  { Wire = "HotReload"
    Label = "Hot Reload"
    Icon = "globe"
    Detail = "Patches the running app on save. REPL restricted to expressions." }
]

/// One rendered quick-pick row.
type PickRow = {
  /// What the row reads, codicon token included — `label` is one of the two
  /// QuickPickItem fields where VS Code expands `$(...)`.
  Label: string
  /// The right-hand hint. Carries the "current" marker, so the picker can
  /// always answer "which one am I in?" without a second lookup.
  Description: string
  Detail: string
  Wire: string
  IsCurrent: bool
}

/// Build the rows for a session currently in `currentLabel` (a
/// `SessionWorkflow.label` value, e.g. what `/api/sessions` sends as
/// `workflowLabel`). An unrecognised current label simply marks nothing —
/// it never hides a choice.
let rows (currentLabel: string) : PickRow list =
  choices
  |> List.map (fun c ->
    let isCurrent = c.Label = currentLabel
    { Label = sprintf "$(%s) %s" c.Icon c.Label
      Description =
        match isCurrent with
        | true -> "current"
        | false -> ""
      Detail = c.Detail
      Wire = c.Wire
      IsCurrent = isCurrent })

/// Resolve a picked row's label back to the wire value. Total: an unknown
/// label yields `None` rather than defaulting into a workflow the user did
/// not choose.
let wireOfPickedLabel (pickedLabel: string) : string option =
  rows ""
  |> List.tryFind (fun r -> r.Label = pickedLabel)
  |> Option.map (fun r -> r.Wire)

/// The user-facing label for a wire value, for the confirmation message.
let labelOfWire (wire: string) : string option =
  choices |> List.tryFind (fun c -> c.Wire = wire) |> Option.map (fun c -> c.Label)
