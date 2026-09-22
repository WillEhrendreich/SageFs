namespace SageFs.Simulation

open System
open SageFs.Features.Tweak

/// Deterministic Simulation Testing for one tweak address across an
/// interleaved, seeded stream of tweak events, concurrent file changes, a
/// journal, and crashes, folding the REAL `TweakTransaction.step`,
/// `TweakAddress.resolve`/`replaceRange`, `LiteralEdit.setLiteral` and
/// `TweakLog` operations, never a reimplementation of them.
///
/// Same design rules as the repo's other DST harnesses (`CohortLandingSim`,
/// `FsiEmitSim`): chaos is DATA (a seeded, ordered event list), the real
/// decision functions are the subject, and a TWIN reintroduces a historical
/// or hypothetical bug so the invariants can be shown to have teeth.
///
/// The file under simulation is always:
///   module M
///   let x = <N>       -- the address under test
///   let other = <M>   -- an unrelated binding, to prove edits don't leak
module TweakSim =

  let address : TweakAddress.TweakAddress =
    { ModulePath = [ "M" ]; BindingName = "x"; Path = [] }

  let private otherAddress : TweakAddress.TweakAddress =
    { ModulePath = [ "M" ]; BindingName = "other"; Path = [] }

  let fileOf (x: int64) (other: int64) : string =
    sprintf "module M\nlet x = %d\nlet other = %d\n" x other

  /// Insert a comment right after the module header, the exact "reformat"
  /// shape `TweakAddressTests` already proves preserves every address; the
  /// sim's job is to prove that holds under a chaotic interleaving too.
  let private insertReformatComment (source: string) : string =
    let anchor = "module M\n"
    let idx = source.IndexOf anchor + anchor.Length
    source.Substring(0, idx) + "// reformatted\n" + source.Substring(idx)

  [<RequireQualifiedAccess>]
  type SimEvent =
    /// Drive one tweak from Started through Applied (subject to any pending
    /// forced failure).
    | Tweak of value: int64
    /// The NEXT `Tweak` will fail type-checking instead of succeeding.
    | ForceTypeCheckFail
    /// The NEXT `Tweak` will fail evaluation instead of succeeding.
    | ForceEvaluateFail
    /// Persist the current applied-but-unsaved tweak to the file.
    | Save
    /// Someone edits `x` directly in the file, outside the tweak system
    /// entirely (a hand edit, another tool, a second tweak session).
    | UserEditsFile of value: int64
    /// Someone edits the UNRELATED `other` binding.
    | EditOtherBinding of value: int64
    /// A whitespace/comment-only reformat.
    | Reformat
    /// Roll back the most recent applied/saved op.
    | RollbackLast
    /// The process dies: anything not yet Applied is lost; the log and the
    /// file survive.
    | Crash
    /// An editor now holds unsaved changes to this file.
    | OpenEditorDirty
    | CloseEditor
    /// Resolves an open conflict on the tweaked address, if there is one.
    | ResolveConflict

  type Scenario =
    { Seed: int
      InitialX: int64
      InitialOther: int64
      Events: SimEvent list }

  /// Which `Save` implementation the trace was folded under.
  [<RequireQualifiedAccess>]
  type SaveBehavior =
    /// Production: refuses (raises a conflict) when the address's hash
    /// moved since the tweak's own baseline.
    | Real
    /// TWIN: the bug section 2 of the spec exists to prevent: writes the
    /// new value with no hash check at all, whatever is there now.
    | SkipHashCheckTwin

  [<RequireQualifiedAccess>]
  type RollbackBehavior =
    /// Production: `TweakLog.rollback`, structural, hash-checked.
    | Real
    /// TWIN: a naive rollback that finds the op's own "after" text ANYWHERE
    /// in the file via a raw string search and puts "before" back, with no
    /// address and no hash check: it happily rewrites whatever now occupies
    /// that text, including an unrelated LATER edit that merely renders the
    /// same.
    | NaiveLineBasedTwin

  [<RequireQualifiedAccess>]
  type CompactionBehavior =
    /// Production: `TweakLog.compact`, never compacts past the first
    /// must-keep event.
    | Real
    /// TWIN: keeps only the last N events by raw COUNT, consulting nothing:
    /// dirtiness, open conflicts and presets are never asked. An unsaved
    /// tweak sitting behind the window is just gone.
    | DropsUnsavedTwin

  /// Whether the simulated editor currently holds unsaved changes to this
  /// file. Not a `bool`: `OpenEditorDirty`/`CloseEditor` are two distinct
  /// facts about the world, named as such, so `applySave`'s guard reads as
  /// "no unsaved editor changes are in the way" rather than a bare `false`.
  [<RequireQualifiedAccess>]
  type EditorDirtiness =
    | NoUnsavedEditorChanges
    | UnsavedEditorChanges

  /// Whether the NEXT `Tweak` event is scheduled to fail, and at which
  /// step. Not a `TweakStep option`: `None` would have meant "nothing
  /// scheduled" only by convention, a `Some` reader would still have to
  /// remember what the step inside it means. Named cases make both facts
  /// explicit at the case itself.
  [<RequireQualifiedAccess>]
  type ScheduledFailure =
    | NoFailureScheduled
    | FailureScheduled of step: TweakTransaction.TweakStep

  /// The address's hash captured as the CURRENT tweak's baseline, the
  /// moment it started, what `Save` checks the live file against. Not a
  /// `string option`: "no baseline captured yet" is a different fact than
  /// "captured, and it's this hash", worth its own case rather than folding
  /// both into whether a string is present.
  [<RequireQualifiedAccess>]
  type TweakBaseline =
    | NoBaselineCaptured
    | BaselineCaptured of hash: string

  /// Ground truth, tracked independently of the log: is there an
  /// applied-but-unsaved tweak right now? Untouched by anything except a
  /// successful Tweak (moves to Dirty) or a successful Save (moves to
  /// Clean); a crash, a user edit, a reformat, or a rollback of something
  /// else entirely must never flip it either way on their own. Not a
  /// `bool`: this DU exists purely so `dirtySetMatchesGroundTruth` reads as
  /// a comparison between two named facts, not two bits whose meaning lives
  /// only in a doc comment.
  [<RequireQualifiedAccess>]
  type GroundTruthDirtiness =
    | GroundTruthClean
    | GroundTruthDirty

  type State =
    { X: int64
      Source: string
      Log: TweakLog.EventLog
      Snapshot: TweakLog.Snapshot
      /// A full, NEVER-compacted mirror of every event ever appended, kept
      /// only so an invariant can compare "snapshot plus tail" against the
      /// true, uncompacted history without reimplementing compaction. Not
      /// part of what any real implementation would keep; a verification
      /// aid, same role `ModelState` plays in `FsiEmitSim`.
      ShadowLog: TweakLog.EventLog
      Editor: EditorDirtiness
      Txn: TweakTransaction.TweakState<int64>
      ScheduledFailure: ScheduledFailure
      Baseline: TweakBaseline
      GroundTruth: GroundTruthDirtiness
      Settings: TweakLog.TweakLogSettings
      Clock: int64 }

  /// A small policy so a ~20-event scenario actually exercises compaction,
  /// production's real defaults (`RetentionPolicy.defaults`) are sized for
  /// thousands of events, which no seeded scenario here produces.
  let simRetention : TweakLog.RetentionPolicy =
    { UndoWindow = 2; MaxEvents = 6; MaxBytes = 100_000L }

  /// The sim's default settings: LiveOnBudget so a mid-run trace actually
  /// exercises compaction the way the existing invariants expect. Callers
  /// that want to prove the OnSessionClose story build their own settings
  /// record (see `traceWith`/`runWith`) and call `closeSessionNow` at the end.
  let defaultSettings : TweakLog.TweakLogSettings =
    { CompactionMode = TweakLog.CompactionMode.LiveOnBudget
      ReplayScope = TweakLog.ReplayScope.SageFsWritesOnly
      Retention = simRetention }

  let initial (settings: TweakLog.TweakLogSettings) (x: int64) (other: int64) : State =
    { X = x
      Source = fileOf x other
      Log = TweakLog.EventLog.empty
      Snapshot = TweakLog.Snapshot.empty
      ShadowLog = TweakLog.EventLog.empty
      Editor = EditorDirtiness.NoUnsavedEditorChanges
      Txn = TweakTransaction.initial x
      ScheduledFailure = ScheduledFailure.NoFailureScheduled
      Baseline = TweakBaseline.NoBaselineCaptured
      GroundTruth = GroundTruthDirtiness.GroundTruthClean
      Settings = settings
      Clock = 0L }

  /// Append one event to BOTH the real (compactable) log and the
  /// never-compacted shadow, the two always agree on ids, since both start
  /// at 1 and see exactly the same events in exactly the same order.
  let private appendEvent (s: State) (event: TweakLog.TweakLogEvent) : State =
    let log, _ = TweakLog.EventLog.append s.Log s.Clock event
    let shadow, _ = TweakLog.EventLog.append s.ShadowLog s.Clock event
    { s with Log = log; ShadowLog = shadow }

  let private maybeCompact (behavior: CompactionBehavior) (s: State) : State =
    let bytes = int64 (TweakLog.TweakLogFormat.encodeStream s.Log.Events).Length
    match TweakLog.shouldCompact s.Settings bytes s.Log.Events.Length with
    | false -> s
    | true ->
      match behavior with
      | CompactionBehavior.Real ->
        let newSnapshot, tail = TweakLog.compact s.Snapshot s.Log.Events s.Settings.Retention Set.empty
        { s with Snapshot = newSnapshot; Log = { s.Log with Events = tail } }
      | CompactionBehavior.DropsUnsavedTwin ->
        let tail = s.Log.Events |> List.rev |> List.truncate s.Settings.Retention.UndoWindow |> List.rev
        { s with Log = { s.Log with Events = tail } }

  /// Simulates the session closing: compacts unconditionally (the
  /// `closeSession` promise), whatever `CompactionMode` the run used.
  let closeSessionNow (behavior: CompactionBehavior) (s: State) : State =
    match behavior with
    | CompactionBehavior.Real ->
      let newSnapshot, tail = TweakLog.closeSession s.Snapshot s.Log.Events s.Settings Set.empty
      { s with Snapshot = newSnapshot; Log = { s.Log with Events = tail } }
    | CompactionBehavior.DropsUnsavedTwin ->
      let tail = s.Log.Events |> List.rev |> List.truncate s.Settings.Retention.UndoWindow |> List.rev
      { s with Log = { s.Log with Events = tail } }

  let private applyTweak (s: State) (value: int64) : State =
    let text = string value
    let baselineHash =
      match TweakAddress.resolve s.Source address with
      | Ok r -> TweakBaseline.BaselineCaptured r.Hash
      | Error _ -> TweakBaseline.NoBaselineCaptured
    let started = TweakTransaction.step s.Txn (TweakTransaction.TweakEvent.Started text)
    let parsed = TweakTransaction.step started TweakTransaction.TweakEvent.Parsed
    let typeChecked =
      match s.ScheduledFailure with
      | ScheduledFailure.FailureScheduled TweakTransaction.TweakStep.TypeCheck ->
        TweakTransaction.step parsed (TweakTransaction.TweakEvent.TypeCheckFailed "chaos: forced type-check failure")
      | _ -> TweakTransaction.step parsed TweakTransaction.TweakEvent.TypeChecked
    let evaluated =
      match s.ScheduledFailure with
      | ScheduledFailure.FailureScheduled TweakTransaction.TweakStep.Evaluate ->
        TweakTransaction.step typeChecked (TweakTransaction.TweakEvent.EvaluateFailed "chaos: forced evaluate failure")
      | _ -> TweakTransaction.step typeChecked (TweakTransaction.TweakEvent.Evaluated value)
    let applied =
      match evaluated with
      | TweakTransaction.TweakState.Evaluated _ -> TweakTransaction.step evaluated TweakTransaction.TweakEvent.Applied
      | other -> other
    match applied with
    | TweakTransaction.TweakState.Applied(v, _) ->
      let before = TweakAddress.resolve s.Source address |> Result.map _.Text |> Result.defaultValue (string s.X)
      let s = appendEvent s (TweakLog.TweakLogEvent.TweakApplied(address, before, string v, TweakAddress.contentHash (string v)))
      { s with
          Txn = applied
          X = v
          GroundTruth = GroundTruthDirtiness.GroundTruthDirty
          ScheduledFailure = ScheduledFailure.NoFailureScheduled
          Baseline = baselineHash }
    | other -> { s with Txn = other; ScheduledFailure = ScheduledFailure.NoFailureScheduled; Baseline = baselineHash }

  let private applySave (behavior: SaveBehavior) (s: State) : State =
    match s.Txn, s.Editor with
    | TweakTransaction.TweakState.Applied(v, _), EditorDirtiness.NoUnsavedEditorChanges ->
      match TweakAddress.resolve s.Source address with
      | Error _ -> s
      | Ok resolved ->
        let hashChangedUnderneath =
          match s.Baseline with
          | TweakBaseline.BaselineCaptured expected -> expected <> resolved.Hash
          | TweakBaseline.NoBaselineCaptured -> false
        match behavior, hashChangedUnderneath with
        | SaveBehavior.Real, true ->
          appendEvent s (TweakLog.TweakLogEvent.ConflictRaised(address, string v, resolved.Text, resolved.Text))
        | SaveBehavior.Real, false when TweakLog.canSave s.Log address |> Result.isError ->
          // An open conflict on this address blocks the save outright,
          // consulted the same way a real product's save path has to.
          s
        | _ ->
          match LiteralEdit.setLiteral s.Source address (LiteralEdit.LiteralValue.Integer v) with
          | Error _ -> s
          | Ok newSource ->
            let fileHashBefore = TweakAddress.contentHash s.Source
            let s =
              appendEvent s
                (TweakLog.TweakLogEvent.TweakSaved(address, resolved.Text, string v, TweakAddress.contentHash (string v), fileHashBefore))
            { s with
                Source = newSource
                GroundTruth = GroundTruthDirtiness.GroundTruthClean
                Txn = TweakTransaction.step s.Txn TweakTransaction.TweakEvent.Journaled }
    | _ -> s

  let private applyRollback (behavior: RollbackBehavior) (s: State) : State =
    let opId =
      s.Log.Events
      |> List.choose (fun e ->
        match e.Event with
        | TweakLog.TweakLogEvent.TweakApplied _
        | TweakLog.TweakLogEvent.TweakSaved _ -> Some e.Id
        | _ -> None)
      |> List.tryLast
    match opId with
    | None -> s
    | Some opId ->
      match behavior with
      | RollbackBehavior.Real ->
        match TweakLog.rollback s.Log s.Snapshot opId s.Source with
        | Ok(TweakLog.RollbackOutcome.Applied newSource) ->
          let s = appendEvent s (TweakLog.TweakLogEvent.RolledBack opId)
          { s with Source = newSource }
        | Ok(TweakLog.RollbackOutcome.Conflict(wrote, now, before)) ->
          appendEvent s (TweakLog.TweakLogEvent.ConflictRaised(address, wrote, now, before))
        | Error _ -> s
      | RollbackBehavior.NaiveLineBasedTwin ->
        match s.Log.Events |> List.tryFind (fun e -> e.Id = opId) |> Option.map _.Event with
        | Some(TweakLog.TweakLogEvent.TweakApplied(_, before, after, _))
        | Some(TweakLog.TweakLogEvent.TweakSaved(_, before, after, _, _)) ->
          match s.Source.IndexOf(after: string) with
          | -1 -> s
          | i -> { s with Source = s.Source.Substring(0, i) + before + s.Source.Substring(i + after.Length) }
        | _ -> s

  let step (saveBehavior: SaveBehavior) (rollbackBehavior: RollbackBehavior) (compactionBehavior: CompactionBehavior) (s: State) (ev: SimEvent) : State =
    let s = { s with Clock = s.Clock + 1L }
    let s' =
      match ev with
      | SimEvent.ForceTypeCheckFail -> { s with ScheduledFailure = ScheduledFailure.FailureScheduled TweakTransaction.TweakStep.TypeCheck }
      | SimEvent.ForceEvaluateFail -> { s with ScheduledFailure = ScheduledFailure.FailureScheduled TweakTransaction.TweakStep.Evaluate }
      | SimEvent.Tweak value -> applyTweak s value
      | SimEvent.Save -> applySave saveBehavior s
      | SimEvent.UserEditsFile value ->
        match TweakAddress.resolve s.Source address with
        | Error _ -> s
        | Ok resolved ->
          let newSource = TweakAddress.replaceRange s.Source resolved.Range (string value)
          let s = appendEvent s (TweakLog.observeUserEdit s.Settings s.Source newSource address (string value))
          { s with Source = newSource }
      | SimEvent.EditOtherBinding value ->
        match TweakAddress.resolve s.Source otherAddress with
        | Error _ -> s
        | Ok resolved ->
          let newSource = TweakAddress.replaceRange s.Source resolved.Range (string value)
          let s = appendEvent s (TweakLog.observeUserEdit s.Settings s.Source newSource otherAddress (string value))
          { s with Source = newSource }
      | SimEvent.Reformat ->
        let newSource = insertReformatComment s.Source
        let s = appendEvent s (TweakLog.observeReformat s.Settings s.Source newSource)
        { s with Source = newSource }
      | SimEvent.RollbackLast -> applyRollback rollbackBehavior s
      | SimEvent.Crash ->
        let revivedX =
          match TweakAddress.resolve s.Source address with
          | Ok r ->
            match Int64.TryParse r.Text with
            | true, v -> v
            | false, _ -> s.X
          | Error _ -> s.X
        { s with X = revivedX; Txn = TweakTransaction.initial revivedX; ScheduledFailure = ScheduledFailure.NoFailureScheduled }
      | SimEvent.OpenEditorDirty -> { s with Editor = EditorDirtiness.UnsavedEditorChanges }
      | SimEvent.CloseEditor -> { s with Editor = EditorDirtiness.NoUnsavedEditorChanges }
      | SimEvent.ResolveConflict ->
        match TweakLog.hasOpenConflict s.Log address with
        | false -> s
        | true -> appendEvent s (TweakLog.TweakLogEvent.ConflictResolved address)
    maybeCompact compactionBehavior s'

  /// The full state trace, oldest first (including the initial state), so
  /// invariants can look at consecutive pairs and at every point in time,
  /// not just the end.
  let traceWith
    (settings: TweakLog.TweakLogSettings)
    (saveBehavior: SaveBehavior)
    (rollbackBehavior: RollbackBehavior)
    (compactionBehavior: CompactionBehavior)
    (scenario: Scenario)
    : State list =
    scenario.Events
    |> List.scan (step saveBehavior rollbackBehavior compactionBehavior) (initial settings scenario.InitialX scenario.InitialOther)

  /// `trace` under the sim's default settings (LiveOnBudget), the shape
  /// every pre-existing invariant/twin was written against.
  let trace saveBehavior rollbackBehavior compactionBehavior scenario : State list =
    traceWith defaultSettings saveBehavior rollbackBehavior compactionBehavior scenario

  let runWith settings saveBehavior rollbackBehavior compactionBehavior scenario : State =
    traceWith settings saveBehavior rollbackBehavior compactionBehavior scenario |> List.last

  let run saveBehavior rollbackBehavior compactionBehavior scenario : State =
    trace saveBehavior rollbackBehavior compactionBehavior scenario |> List.last

  /// A seeded, deterministic scenario. `fromSeed n` is a pure function of
  /// `n`, so replaying a seed reproduces the identical trace forever.
  let scenarioOf (seed: int) : Scenario =
    let rng = Random(seed)
    let n = 5 + rng.Next 20
    let events =
      [ for _ in 1 .. n ->
          match rng.Next 12 with
          | 0 -> SimEvent.Tweak(int64 (rng.Next 1000))
          | 1 -> SimEvent.ForceTypeCheckFail
          | 2 -> SimEvent.ForceEvaluateFail
          | 3 -> SimEvent.Save
          | 4 -> SimEvent.UserEditsFile(int64 (rng.Next 1000))
          | 5 -> SimEvent.EditOtherBinding(int64 (rng.Next 1000))
          | 6 -> SimEvent.Reformat
          | 7 -> SimEvent.RollbackLast
          | 8 -> SimEvent.Crash
          | 9 -> SimEvent.OpenEditorDirty
          | 10 -> SimEvent.CloseEditor
          | _ -> SimEvent.ResolveConflict ]
    { Seed = seed
      InitialX = int64 (rng.Next 1000)
      InitialOther = int64 (rng.Next 1000)
      Events = events }
