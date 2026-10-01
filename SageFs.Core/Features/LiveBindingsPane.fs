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

  let containmentLine (report: ClickReport) : ContainmentLine =
    match report with
    | NoClickYet -> NothingClickedYet
    | ClickAnswered outcome ->
      match outcome with
      | MemberShown(_, ContainedBy _) -> LineSays "ran under a syscall filter (no network, no file writes, no new processes)"
      | MemberShown(_, NotContained why) -> LineSays(sprintf "no I/O containment here: %s" (SandboxUnavailable.describe why))
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
                | MemberShown(replacement, _) ->
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
