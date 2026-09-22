namespace SageFs.Simulation

open SageFs.Features.Tweak
open SageFs.Simulation.TweakSim

/// Invariants over `TweakSim`'s trace.
module TweakSimInvariants =

  type Violation = { Index: int; Why: string }

  let private violation i why : Violation option = Some { Index = i; Why = why }

  /// The event this exact step appended, if any, never a stale "last
  /// event" left over from several steps ago. `Log.Events` only ever grows
  /// (until compaction trims its FRONT) or keeps its newest entry, so "a
  /// genuinely new id" is the only safe way to tell "this step logged
  /// something" from "the log's tail still shows what an earlier step
  /// logged".
  let private justAppended (before: State) (after: State) : TweakLog.TweakLogEvent option =
    let beforeLastId = before.Log.Events |> List.tryLast |> Option.map _.Id |> Option.defaultValue 0
    match after.Log.Events |> List.tryLast with
    | Some logged when logged.Id > beforeLastId -> Some logged.Event
    | _ -> None

  /// A tweak is never applied without passing type-check. Exhaustively
  /// proven at the pure `TweakTransaction` level already, over adversarial,
  /// out-of-order event sequences (`TweakTransactionTests`), that is the
  /// real proof. What this checks is the SIM's own wiring: a `Tweak` event
  /// the scenario scheduled to fail (via `ForceTypeCheckFail`/
  /// `ForceEvaluateFail`, still pending going into it) must never still land
  /// on `Applied`. A single `Tweak` sim-event drives the WHOLE
  /// parse->typecheck->evaluate->apply pipeline in one step, so the
  /// intermediate `TweakTransaction` states never appear in the sim's own
  /// state trace, checking against the scenario's chaos, not the trace's
  /// Txn shape, is what's actually observable here.
  let neverAppliedWithoutTypeCheck (scenario: Scenario) (states: State list) : Violation list =
    List.zip scenario.Events (states |> List.pairwise)
    |> List.indexed
    |> List.choose (fun (i, (ev, (before, after))) ->
      match ev, before.PendingFailure with
      | SimEvent.Tweak _, Some _ ->
        match after.Txn with
        | TweakTransaction.TweakState.Applied _ -> violation i "a scheduled forced failure still reached Applied"
        | _ -> None
      | _ -> None)

  /// A failure always keeps the last good value: X never moves on a
  /// transition into `Failed`.
  let failureKeepsLastGoodValue (states: State list) : Violation list =
    states
    |> List.pairwise
    |> List.indexed
    |> List.choose (fun (i, (before, after)) ->
      match after.Txn with
      | TweakTransaction.TweakState.Failed _ when after.X <> before.X ->
        violation i (sprintf "X moved from %d to %d on a failed step" before.X after.X)
      | _ -> None)

  /// After a crash, the journal re-offers exactly the unsaved tweaks: none
  /// lost, none duplicated. The log survives a crash untouched, so this is
  /// checked as an equality that must hold at EVERY step (crash included)
  /// between the log's own dirty-set and ground truth tracked independently
  /// in the fold.
  let dirtySetMatchesGroundTruth (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.choose (fun (i, s) ->
      let logSaysDirty = TweakLog.dirtySet s.Log |> Set.contains address
      match logSaysDirty = s.DirtyGroundTruth with
      | true -> None
      | false -> violation i (sprintf "log dirty=%b but ground truth=%b" logSaysDirty s.DirtyGroundTruth))

  /// A save's own logged `TweakSaved.textBefore` always matches what was
  /// really at the address the instant before that save landed, the
  /// direct evidence that a save didn't just overwrite whatever it found.
  let saveRecordsTheRealTextBefore (states: State list) : Violation list =
    states
    |> List.pairwise
    |> List.indexed
    |> List.choose (fun (i, (before, after)) ->
      match justAppended before after with
      | Some(TweakLog.TweakLogEvent.TweakSaved(addr, textBefore, _, _, _)) when addr = address ->
        match TweakAddress.resolve before.Source addr with
        | Ok resolvedBefore when resolvedBefore.Text <> textBefore ->
          violation i "TweakSaved recorded a textBefore that doesn't match what was really there before the save"
        | _ -> None
      | _ -> None)

  /// A save never overwrites an expression whose hash changed underneath
  /// it: whenever `Save` fires while the file's live hash differs from the
  /// tweak's own baseline, the outcome must be a refusal
  /// (`ConflictRaised`), never a `TweakSaved`.
  let saveNeverOverwritesAChangedHash (states: State list) : Violation list =
    states
    |> List.pairwise
    |> List.indexed
    |> List.choose (fun (i, (before, after)) ->
      match before.Txn, before.TweakStartHash with
      | TweakTransaction.TweakState.Applied _, Some expected ->
        match TweakAddress.resolve before.Source address with
        | Ok resolved when resolved.Hash <> expected ->
          match justAppended before after with
          | Some(TweakLog.TweakLogEvent.TweakSaved _) -> violation i "saved over a hash that had changed underneath the tweak"
          | _ -> None
        | _ -> None
      | _ -> None)

  /// The unrelated `other` binding's on-disk text never changes except via
  /// an explicit `EditOtherBinding`, a tweak/save/rollback/reformat on `x`
  /// never leaks into it.
  let otherBindingOnlyChangesWhenEdited (scenario: Scenario) (states: State list) : Violation list =
    List.zip scenario.Events (states |> List.pairwise)
    |> List.indexed
    |> List.choose (fun (i, (ev, (before, after))) ->
      let textOf (s: State) =
        TweakAddress.resolve s.Source ({ ModulePath = [ "M" ]; BindingName = "other"; Path = [] }: TweakAddress.TweakAddress)
        |> Result.map (fun r -> r.Text)
      match textOf before, textOf after, ev with
      | Ok b, Ok a, SimEvent.EditOtherBinding _ -> None
      | Ok b, Ok a, _ when b <> a -> violation i (sprintf "the 'other' binding changed from %s to %s without an EditOtherBinding event" b a)
      | _ -> None)

  /// A reformat never breaks the address: it must still resolve, to the
  /// same text, right after every `Reformat` event.
  let reformatNeverBreaksTheAddress (scenario: Scenario) (states: State list) : Violation list =
    List.zip scenario.Events (states |> List.pairwise)
    |> List.indexed
    |> List.choose (fun (i, (ev, (before, after))) ->
      match ev with
      | SimEvent.Reformat ->
        match TweakAddress.resolve before.Source address, TweakAddress.resolve after.Source address with
        | Ok b, Ok a when b.Text = a.Text -> None
        | Ok _, Error e -> violation i (sprintf "a reformat broke the address: %A" e)
        | Ok b, Ok a -> violation i (sprintf "a reformat changed the resolved text from %s to %s" b.Text a.Text)
        | _ -> None
      | _ -> None)

  /// The encoded log never exceeds the configured byte budget for longer
  /// than it takes the very next step to compact it back down, i.e.
  /// `maybeCompact` runs every step, so two consecutive over-budget states
  /// in a row is the only thing that would mean growth isn't actually
  /// bounded.
  /// Only meaningful under `LiveOnBudget`, mid-session, `OnSessionClose`
  /// intentionally lets the log grow past budget until the session
  /// closes, that's the whole point of the mode, so this only checks the
  /// states where a compaction pass was actually allowed to run.
  let logStaysWithinBudget (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.choose (fun (i, s) ->
      match s.Settings.CompactionMode with
      | TweakLog.CompactionMode.OnSessionClose -> None
      | TweakLog.CompactionMode.LiveOnBudget ->
        let bytes = int64 (TweakLog.TweakLogFormat.encodeStream s.Log.Events).Length
        match bytes <= s.Settings.Retention.MaxBytes with
        | true -> None
        | false -> violation i (sprintf "log grew to %d bytes, over the %d budget" bytes s.Settings.Retention.MaxBytes))

  /// Snapshot plus tail always folds to the same projection as the full,
  /// uncompacted history would, checked at every step against `ShadowLog`,
  /// the sim's own never-compacted mirror of every event ever appended.
  let snapshotPlusTailMatchesFullHistory (states: State list) : Violation list =
    states
    |> List.indexed
    |> List.choose (fun (i, s) ->
      let full = TweakLog.project s.ShadowLog
      let fromSnapshot = TweakLog.projectFromSnapshot s.Snapshot s.Log.Events
      match fromSnapshot.Known.TryFind address, full.Known.TryFind address with
      | Some a, Some b when a <> b ->
        violation i (sprintf "snapshot+tail says %s but the full, uncompacted history says %s" a b)
      | None, Some b -> violation i (sprintf "snapshot+tail lost the address entirely; full history still has %s" b)
      | _ -> None)

  /// An open conflict on the tweaked address blocks further saves AND
  /// rollbacks on it, checked live in the trace: neither a `TweakSaved`
  /// nor a `RolledBack` may land in a step where the address already had
  /// an open conflict going in.
  let openConflictBlocksSaveAndRollback (states: State list) : Violation list =
    states
    |> List.pairwise
    |> List.indexed
    |> List.choose (fun (i, (before, after)) ->
      match justAppended before after with
      | Some(TweakLog.TweakLogEvent.TweakSaved _)
      | Some(TweakLog.TweakLogEvent.RolledBack _) when TweakLog.hasOpenConflict before.Log address ->
        violation i "a save or rollback landed while the address had an open conflict"
      | _ -> None)

  /// Under `ReplayScope.EverythingAsDiffs`, `replayWholeFile` folded over
  /// the FULL (never-compacted) event history must reproduce exactly the
  /// file the trace actually ended on, including bytes SageFs itself never
  /// wrote (user edits, reformats).
  let everythingAsDiffsReplaysWholeFileExactly (initialSource: string) (states: State list) : Violation list =
    match states with
    | [] -> []
    | first :: _ ->
      match first.Settings.ReplayScope with
      | TweakLog.ReplayScope.SageFsWritesOnly -> []
      | TweakLog.ReplayScope.EverythingAsDiffs ->
        let final = states |> List.last
        match TweakLog.replayWholeFile initialSource final.ShadowLog.Events with
        | Ok replayed when replayed = final.Source -> []
        | Ok replayed ->
          [ { Index = states.Length - 1
              Why = sprintf "replayWholeFile produced %s but the actual final file is %s" replayed final.Source } ]
        | Error e -> [ { Index = states.Length - 1; Why = sprintf "replayWholeFile failed: %s" e } ]

  let all (scenario: Scenario) (states: State list) : Violation list =
    neverAppliedWithoutTypeCheck scenario states
    @ failureKeepsLastGoodValue states
    @ dirtySetMatchesGroundTruth states
    @ saveRecordsTheRealTextBefore states
    @ saveNeverOverwritesAChangedHash states
    @ otherBindingOnlyChangesWhenEdited scenario states
    @ reformatNeverBreaksTheAddress scenario states
    @ logStaysWithinBudget states
    @ snapshotPlusTailMatchesFullHistory states
