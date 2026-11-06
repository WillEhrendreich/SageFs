namespace SageFs.Features

open System
open System.Collections.Concurrent
open SageFs
open SageFs.FsiHost.FsiProtocol

/// What the live-bindings pane knows beyond the tree: the walk mode a session is in, what the last click came to, how many
/// rows are listed and not read, and how a clicked binding's new tree replaces the old one. All of it is pure (the one
/// store aside), so the dashboard, the daemon routes and the editors say the same thing in the same words.
module LiveBindingsPane =

  /// Whether anyone has chosen the session's walk mode since its worker started. A worker that was never told gets the
  /// config's choice once; a worker that was told keeps what it was told, so a config can never overrule a click.
  type WalkModeOrigin =
    | StartedWithDefault
    | ChosenByUser

  /// What a worker answers a live-values pull with: the snapshot, and the mode it was walked in.
  type LiveValuesReading =
    { Mode: ValueWalk
      Origin: WalkModeOrigin
      Snapshot: LiveValueTree.LiveValueSnapshot }

  /// The reading's wire text, built around a snapshot that is already JSON so a big tree is not read and written again.
  /// It is exactly what serializing the typed `LiveValuesReading` writes on the worker wire, whose field names are camelCase
  /// (a test holds the two together).
  let readingJson (mode: ValueWalk) (origin: WalkModeOrigin) (snapshotJson: string) : string =
    use stream = new System.IO.MemoryStream()
    use writer = new System.Text.Json.Utf8JsonWriter(stream)
    writer.WriteStartObject()
    writer.WritePropertyName "mode"
    writer.WriteRawValue(WorkerProtocol.Serialization.serialize mode)
    writer.WritePropertyName "origin"
    writer.WriteRawValue(WorkerProtocol.Serialization.serialize origin)
    writer.WritePropertyName "snapshot"
    writer.WriteRawValue snapshotJson
    writer.WriteEndObject()
    writer.Flush()
    System.Text.Encoding.UTF8.GetString(stream.ToArray())

  /// What the last click on a "not evaluated" row came to, or that nothing has been clicked.
  type ClickReport =
    | NoClickYet
    | ClickAnswered of MemberOutcome

  /// The line under the pane's header that says how the last clicked getter was kept from doing harm.
  type ContainmentLine =
    | NothingClickedYet
    | LineSays of text: string

  /// The containment sentence plus what the guards covered and whether one stopped the getter.
  let private withGuards (containment: string) (guards: GuardOutcome) : string =
    let trip = GuardCoverage.describeTrip guards.Trip
    match trip with
    | "" -> sprintf "%s; %s" containment (GuardCoverage.describe guards.Coverage)
    | stopped -> sprintf "%s; %s; %s" containment (GuardCoverage.describe guards.Coverage) stopped

  let containmentLine (report: ClickReport) : ContainmentLine =
    match report with
    | NoClickYet -> NothingClickedYet
    | ClickAnswered outcome ->
      match outcome with
      | MemberShown(_, ContainedBy _, guards) ->
        LineSays(withGuards "ran under a syscall filter (no network, no file writes, no new processes)" guards)
      | MemberShown(_, NotContained why, guards) ->
        LineSays(withGuards (sprintf "no I/O containment here: %s" (SandboxUnavailable.describe why)) guards)
      | MemberRefused refusal -> LineSays(sprintf "not run: %s" (MemberClick.describeRefusal refusal))
      | MemberUnavailable reason -> LineSays(sprintf "not run: %s" (MemberClick.describeUnavailable reason))
      | BindingNotFound name -> LineSays(sprintf "not run: %s is not in the session any more" name)

  /// How many rows the snapshot lists without having read them, at any depth, in every binding.
  let notEvaluatedCount (snapshot: LiveValueTree.LiveValueSnapshot) : int =
    let rec held (node: LiveValueTree.LiveValueNode) : int =
      let here =
        match node.Kind with
        | LiveValueTree.NodeKind.NotEvaluated _ -> 1
        | _ -> 0
      here + (node.Children |> List.sumBy held)
    snapshot.Bindings |> List.sumBy (fun binding -> held binding.Root)

  /// What a row listed without being read offers, by why it was left alone.
  type RowAction =
    /// A getter that calls code or loops: one click runs it, under containment.
    | ClickToRun
    /// Nothing a click can do: a sequence is not enumerated, and a collapsed class is opened by the mode, not by a row.
    | NothingToClick
    /// A click was made and did not give a value. The row reads as unknown, with the reason.
    | ClickFailed

  let rowActionOf (reason: LiveValueTree.NotEvaluatedReason) : RowAction =
    match reason with
    | LiveValueTree.NotEvaluatedReason.GetterRunsCode
    | LiveValueTree.NotEvaluatedReason.GetterLoops -> ClickToRun
    | LiveValueTree.NotEvaluatedReason.SequenceNotEnumerated
    | LiveValueTree.NotEvaluatedReason.ClassesCollapsed -> NothingToClick
    | LiveValueTree.NotEvaluatedReason.EvaluationTimedOut
    | LiveValueTree.NotEvaluatedReason.EvaluationThrew _
    | LiveValueTree.NotEvaluatedReason.EvaluationNotContained _ -> ClickFailed

  /// What a person can do about a held row from what is on the screen. Each case names a control that is really drawn, so a
  /// sentence built from it never points at one that is not.
  [<RequireQualifiedAccess>]
  type HeldNext =
    /// The run button on the row runs the getter, under a deadline.
    | RunWithButton
    /// The run button on the row runs it again.
    | RetryWithButton
    /// The mode buttons in the pane's header change what is read.
    | SwitchMode of ValueWalk
    /// Nothing on screen changes this row.
    | NothingOnScreen

  let heldNext (reason: LiveValueTree.NotEvaluatedReason) : HeldNext =
    match reason with
    | LiveValueTree.NotEvaluatedReason.GetterRunsCode
    | LiveValueTree.NotEvaluatedReason.GetterLoops -> HeldNext.RunWithButton
    // A sequence is read in the mode that runs everything, and a class is opened in the mode that runs only what does nothing.
    | LiveValueTree.NotEvaluatedReason.SequenceNotEnumerated -> HeldNext.SwitchMode WalkEverything
    | LiveValueTree.NotEvaluatedReason.ClassesCollapsed -> HeldNext.SwitchMode WalkSafe
    | LiveValueTree.NotEvaluatedReason.EvaluationTimedOut
    | LiveValueTree.NotEvaluatedReason.EvaluationThrew _ -> HeldNext.RetryWithButton
    | LiveValueTree.NotEvaluatedReason.EvaluationNotContained _ -> HeldNext.NothingOnScreen

  /// The sentence a held row shows: why it was not read, and the one thing to do about it that this screen offers. The wording is
  /// chosen by the case, so a new reason cannot be left with the walk's terse text.
  let heldSaying (reason: LiveValueTree.NotEvaluatedReason) : string =
    match reason with
    | LiveValueTree.NotEvaluatedReason.GetterRunsCode -> "Calls other code, so it was not run. The run button runs it under a deadline."
    | LiveValueTree.NotEvaluatedReason.GetterLoops -> "Loops or calls itself, so it was not run. The run button runs it under a deadline."
    | LiveValueTree.NotEvaluatedReason.SequenceNotEnumerated ->
      sprintf "A sequence: reading it runs the code behind it, so it was not read. Switch the mode to %s to read it." (ValueWalk.name WalkEverything)
    | LiveValueTree.NotEvaluatedReason.ClassesCollapsed ->
      sprintf "Class values are closed in this mode. Switch the mode to %s to open them." (ValueWalk.name WalkSafe)
    | LiveValueTree.NotEvaluatedReason.EvaluationTimedOut -> "The getter did not return in time. The run button tries again."
    | LiveValueTree.NotEvaluatedReason.EvaluationThrew message -> sprintf "The getter threw: %s. The run button runs it again." message
    | LiveValueTree.NotEvaluatedReason.EvaluationNotContained why -> sprintf "Not run: %s. A retry would be refused the same way." why

  /// One line the pane says about itself, apart from any row. One exhaustive function, so each says what is true and what to do next.
  [<RequireQualifiedAccess>]
  type PaneNote =
    /// The filter is set and no binding's name contains it.
    | NoNameMatchesFilter
    /// The walk stopped at its depth or size limit for at least one value.
    | ValuesCutShort
    /// The walk's values, as of the moment they were read (a clock time the caller has already spelled, with its zone).
    | ValuesAsOf of at: string
    /// There is no walk yet: the values are what FSI printed.
    | ReadFromPrinted
    /// Shown while at least one row has a control: where a change goes, before anyone makes one.
    | ChangesGoToTheFile
    /// The same, when nothing is watching the files the rows are in (the usual state in a REPL session): the consequence is said once,
    /// here, ahead of any edit, and no row repeats it until it has been written.
    | ChangesGoToAnUnwatchedFile
    /// The label of the mode buttons: what they govern.
    | ModeGoverns
    /// The one-line meaning of the chosen mode.
    | ModeMeans of ValueWalk
    /// The count of bindings no file defines.
    | NotInAnyFile of count: int
    /// The summary of the closed disclosure that explains the badges and the glyphs.
    | LegendSummary

  /// Whether the chosen mode's meaning is drawn as a line of its own. The standard mode is what a person gets without choosing, so its
  /// meaning stays in the buttons' tooltips and the line is spent only when the mode is not the standard one.
  [<RequireQualifiedAccess>]
  type ModeCaption =
    | Shown of ValueWalk
    | NotShown

  module ModeCaption =
    let ofMode (mode: ValueWalk) : ModeCaption =
      match mode = ValueWalk.standard with
      | true -> ModeCaption.NotShown
      | false -> ModeCaption.Shown mode

  module PaneNote =
    let text (note: PaneNote) : string =
      match note with
      | PaneNote.NoNameMatchesFilter -> "No binding's name contains that. Clear the filter to see them all."
      | PaneNote.ValuesCutShort ->
        sprintf "Some values were cut short: the pane reads %d levels deep and %d items per collection. Bind the part you want to its own name to see all of it." LiveValueTree.MaxDepth LiveValueTree.MaxChildren
      | PaneNote.ValuesAsOf at -> sprintf "Values as of %s" at
      | PaneNote.ReadFromPrinted -> "read from FSI's printed output"
      | PaneNote.ChangesGoToTheFile -> "Edits go to the file."
      | PaneNote.ChangesGoToAnUnwatchedFile -> "Changes are saved to the file. Nothing watches the file for changes, so the session keeps its old values until you apply them."
      | PaneNote.ModeGoverns -> "Look inside objects:"
      | PaneNote.LegendSummary -> "What do the tags mean?"
      | PaneNote.ModeMeans choice ->
        let meaning =
          match choice with
          | WalkSafe -> "reads fields, and runs only property getters that provably do nothing. The other getters wait for your click."
          | WalkEverything -> "runs your property getters after every eval. That is your code running."
          | WalkOff -> "class values stay closed. Records, unions, tuples, lists and maps still show."
        sprintf "%s: %s" (ValueWalk.name choice) meaning
      | PaneNote.NotInAnyFile count -> sprintf "%d not defined in a file" count

  type ReplaceOutcome =
    | Replaced of LiveValueTree.LiveValueSnapshot
    | NotInSnapshot of name: string

  /// A click's answer replaces that binding's tree where it stands. The generation stays the walk's own (a click is not
  /// a new walk), and the capture time is when the click answered.
  let replaceBinding (answeredAt: DateTimeOffset) (replacement: LiveValueTree.LiveBindingValue) (snapshot: LiveValueTree.LiveValueSnapshot) : ReplaceOutcome =
    match snapshot.Bindings |> List.exists (fun binding -> binding.Name = replacement.Name) with
    | false -> NotInSnapshot replacement.Name
    | true ->
      Replaced
        { snapshot with
            Bindings = snapshot.Bindings |> List.map (fun binding -> if binding.Name = replacement.Name then replacement else binding)
            CapturedAt = answeredAt }

  /// What to do about a worker's walk mode when the config has an opinion.
  type ModeAdvice =
    | ApplyConfigured of ValueWalk
    | LeaveModeAsItIs

  let adviseConfigured (configured: ValueWalk) (reading: LiveValuesReading) : ModeAdvice =
    match reading.Origin, reading.Mode = configured with
    | StartedWithDefault, false -> ApplyConfigured configured
    | StartedWithDefault, true
    | ChosenByUser, _ -> LeaveModeAsItIs

  /// What the pane remembers about one session, beside the snapshot.
  type PaneNotes =
    { Mode: ValueWalk
      Click: ClickReport }

  /// Per-session notes. A session nobody has touched is in the standard mode with no click.
  module PaneStore =
    type T = ConcurrentDictionary<string, PaneNotes>

    let create () : T = T()

    let private untouched : PaneNotes = { Mode = ValueWalk.standard; Click = NoClickYet }

    let notesOf (store: T) (sessionId: string) : PaneNotes =
      match store.TryGetValue sessionId with
      | true, notes -> notes
      | false, _ -> untouched

    let recordClick (store: T) (sessionId: string) (report: ClickReport) : unit =
      store.AddOrUpdate(sessionId, { untouched with Click = report }, fun _ notes -> { notes with Click = report }) |> ignore

    /// A mode switch forgets the last click: its row is not there any more, so its containment line would be a stale claim.
    let setMode (store: T) (sessionId: string) (mode: ValueWalk) : unit =
      store.AddOrUpdate(sessionId, { untouched with Mode = mode }, fun _ _ -> { Mode = mode; Click = NoClickYet }) |> ignore

    /// The mode a pull said the worker was in. Keeps the last click while the mode is unchanged.
    let observeMode (store: T) (sessionId: string) (mode: ValueWalk) : unit =
      store.AddOrUpdate(sessionId, { untouched with Mode = mode }, fun _ notes -> match notes.Mode = mode with | true -> notes | false -> { Mode = mode; Click = NoClickYet }) |> ignore

    let remove (store: T) (sessionId: string) : unit = store.TryRemove sessionId |> ignore

  /// Everything the daemon keeps about the pane, one value the dashboard, the MCP eval hook and the routes all share.
  type Hub =
    { Adaptive: LiveBindingsAdaptive.State
      Notes: PaneStore.T
      /// The walk mode a session's config asks for (the standard when it asks for none). Read only when it could matter.
      ConfiguredWalk: string -> ValueWalk }

  /// What one render of the pane needs: the snapshot, and what the pane remembers beside it.
  type PaneView =
    { Snapshot: LiveValueTree.LiveValueSnapshot
      Notes: PaneNotes }

  let viewOf (hub: Hub) (sessionId: string) : PaneView option =
    LiveBindingsAdaptive.tryGet hub.Adaptive sessionId
    |> Option.map (fun snapshot -> { Snapshot = snapshot; Notes = PaneStore.notesOf hub.Notes sessionId })

  /// The daemon's side of the pane: asking a worker, and putting what it answers in the stores every client reads. `ask` is
  /// the worker proxy, passed in, so the same code serves the dashboard's eval hook, the MCP eval hook and the routes.
  module Feed =
    type Ask = WorkerProtocol.WorkerMessage -> Async<Result<WorkerProtocol.WorkerResponse, SageFsError>>

    let private unexpected (sessionId: string) (reply: WorkerProtocol.WorkerResponse) : SageFsError =
      SageFsError.WorkerCommunicationFailed(sessionId, sprintf "the worker answered with %A" reply)

    /// A reading the worker answered a pull or a mode switch with goes into the stores: the mode first, so the render the
    /// snapshot's change triggers already shows it.
    let ingest (adaptive: LiveBindingsAdaptive.State) (notes: PaneStore.T) (sessionId: string) (readingJson: string) : Result<LiveValuesReading, SageFsError> =
      WorkerProtocol.Serialization.tryDeserialize<LiveValuesReading> readingJson
      |> Result.map (fun reading ->
        PaneStore.observeMode notes sessionId reading.Mode
        LiveBindingsAdaptive.update adaptive sessionId { reading.Snapshot with SessionId = sessionId }
        reading)

    /// Choose the walk mode of a session's worker, and show what it walks like now.
    let setMode (ask: Ask) (adaptive: LiveBindingsAdaptive.State) (notes: PaneStore.T) (sessionId: string) (mode: ValueWalk) : Async<Result<LiveValuesReading, SageFsError>> =
      async {
        match! ask (WorkerProtocol.WorkerMessage.SetValueWalk(mode, sprintf "mode-%s" sessionId)) with
        | Result.Error error -> return Result.Error error
        | Result.Ok(WorkerProtocol.WorkerResponse.LiveValuesResult(_, json)) -> return ingest adaptive notes sessionId json
        | Result.Ok other -> return Result.Error(unexpected sessionId other)
      }

    /// Pull a session's live values after an eval. A worker nobody has told a mode is given the config's, once; a worker a
    /// user has told keeps what it was told. `configuredWalk` is only called when it could matter.
    let pull (ask: Ask) (adaptive: LiveBindingsAdaptive.State) (notes: PaneStore.T) (sessionId: string) (configuredWalk: unit -> ValueWalk) : Async<unit> =
      async {
        match! ask (WorkerProtocol.WorkerMessage.GetLiveValues(sprintf "live-%s" sessionId)) with
        | Result.Error error -> Utils.Log.warn "[LiveBindings] the live values pull for %s failed: %s" sessionId (SageFsError.describe error)
        | Result.Ok(WorkerProtocol.WorkerResponse.LiveValuesResult(_, json)) ->
          match ingest adaptive notes sessionId json with
          | Result.Error error -> Utils.Log.warn "[LiveBindings] the live values for %s could not be read: %s" sessionId (SageFsError.describe error)
          | Result.Ok reading ->
            match reading.Origin with
            | ChosenByUser -> ()
            | StartedWithDefault ->
              match adviseConfigured (configuredWalk ()) reading with
              | LeaveModeAsItIs -> ()
              | ApplyConfigured mode ->
                match! setMode ask adaptive notes sessionId mode with
                | Result.Ok _ -> ()
                | Result.Error error -> Utils.Log.warn "[LiveBindings] the configured walk mode for %s could not be applied: %s" sessionId (SageFsError.describe error)
        | Result.Ok other -> Utils.Log.warn "[LiveBindings] %s" (SageFsError.describe (unexpected sessionId other))
      }

    /// Run one clicked getter. The answer is remembered, and the binding's new tree replaces the old one. The snapshot is
    /// stamped again even when no tree changed (a refusal), so every client hears that the click's line changed.
    let evaluateMember
      (ask: Ask)
      (adaptive: LiveBindingsAdaptive.State)
      (notes: PaneStore.T)
      (sessionId: string)
      (binding: string)
      (path: string list)
      : Async<Result<MemberOutcome, SageFsError>> =
      async {
        match! ask (WorkerProtocol.WorkerMessage.EvaluateLiveMember(binding, path, sprintf "click-%s" sessionId)) with
        | Result.Error error -> return Result.Error error
        | Result.Ok(WorkerProtocol.WorkerResponse.LiveMemberResult(_, json)) ->
          match WorkerProtocol.Serialization.tryDeserialize<MemberOutcome> json with
          | Result.Error error -> return Result.Error error
          | Result.Ok outcome ->
            PaneStore.recordClick notes sessionId (ClickAnswered outcome)
            let now = DateTimeOffset.UtcNow
            match LiveBindingsAdaptive.tryGet adaptive sessionId with
            | None -> ()
            | Some snapshot ->
              let next =
                match outcome with
                | MemberShown(replacement, _, _) ->
                  match replaceBinding now replacement snapshot with
                  | Replaced replaced -> replaced
                  | NotInSnapshot _ -> { snapshot with CapturedAt = now }
                | MemberRefused _
                | MemberUnavailable _
                | BindingNotFound _ -> { snapshot with CapturedAt = now }
              LiveBindingsAdaptive.update adaptive sessionId next
            return Result.Ok outcome
        | Result.Ok other -> return Result.Error(unexpected sessionId other)
      }
