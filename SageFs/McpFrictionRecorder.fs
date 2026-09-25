module SageFs.Features.McpFrictionRecorder

open System
open System.Text.Json
open System.Threading.Tasks
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionTelemetry
open SageFs.Features.ObservedFrictionTypes
open SageFs.Features.ObservedFriction

open Microsoft.FSharp.Core

/// The daemon-side friction report bundle: the pure Core read model
/// (`FrictionTelemetry.FrictionReport`) plus observed (passively-detected)
/// friction signals computed over the SAME version-filtered event stream
/// (Brief B7, observed-friction-plan.md §B7).
///
/// This does NOT live on `FrictionTelemetry.FrictionReport` in
/// SageFs.Core, and Core's compile order is NOT reordered to make it fit.
/// `Features/FrictionTelemetry.fs` compiles at SageFs.Core.fsproj:116;
/// `Features/ObservedFrictionTypes.fs`/`ObservedFriction.fs` (which define
/// `DetectedSignal`/`detectAll`) compile later at :127-134, and
/// `FrictionSanitize.fs` depends on `FrictionTelemetry.FrictionReport`, so
/// reordering would cascade. `SageFs.fsproj` (this project) compiles
/// after ALL of SageFs.Core, so `detectAll` is fully available here — the
/// daemon layer is where the two Core-side read models are threaded
/// together, not Core itself.
type FrictionReportWithSignals = {
  Report: FrictionReport
  ObservedSignals: DetectedSignal list
}

type FrictionEnvelope = {
  Events: FrictionEvent list
  Feedback: ExplicitFeedback list
}

[<CLIMutable>]
type StoredEvent = {
  OccurredAtUtc: DateTimeOffset
  SessionId: string
  ToolName: string
  IntentKind: string
  OutcomeKind: string
  BlockerKind: string option
  ResolutionKind: string option
  ResolutionToolName: string option
  DurationMs: int
  FollowUpKind: string
  FollowUpToolName: string option
  ContextCostKind: string
  SageFsVersion: string
  /// Brief B9 additive fields — absent on JSON serialized before B9, which
  /// deserializes them to the CLR string default (null); `decodeEvent` below
  /// treats a null the same as "" so old envelopes still decode cleanly.
  AgentKey: string
  ErrorSignature: string
}

[<CLIMutable>]
type StoredFeedback = {
  OccurredAtUtc: DateTimeOffset
  SessionId: string
  ToolName: string
  FeedbackKind: string
  ShortReason: string
  AlternativeKind: string
  AlternativeToolName: string option
  SageFsVersion: string
}

[<CLIMutable>]
type StoredEnvelope = {
  Events: StoredEvent list
  Feedback: StoredFeedback list
}

module private Codec =
  let options = JsonSerializerOptions(WriteIndented = false)

  let empty : FrictionEnvelope = { Events = []; Feedback = [] }

  let outcomeParts outcome =
    match outcome with
    | FrictionOutcome.CompletedCleanly -> "CompletedCleanly", None, None, None
    | FrictionOutcome.EncounteredBlocker blocker -> "EncounteredBlocker", Some (string blocker), None, None
    | FrictionOutcome.RecoveredVia ResolutionKind.SolvedWithRetry -> "RecoveredVia", None, Some "SolvedWithRetry", None
    | FrictionOutcome.RecoveredVia (ResolutionKind.SolvedWithDifferentTool tool) -> "RecoveredVia", None, Some "SolvedWithDifferentTool", Some (ToolName.value tool)
    | FrictionOutcome.RecoveredVia ResolutionKind.SolvedAfterReset -> "RecoveredVia", None, Some "SolvedAfterReset", None
    | FrictionOutcome.RecoveredVia ResolutionKind.SolvedAfterSessionSwitch -> "RecoveredVia", None, Some "SolvedAfterSessionSwitch", None
    | FrictionOutcome.RecoveredVia ResolutionKind.Unresolved -> "RecoveredVia", None, Some "Unresolved", None
    | FrictionOutcome.AbandonedWithoutResolution -> "AbandonedWithoutResolution", None, None, None

  let followUpParts followUp =
    match followUp with
    | FollowUp.NoFollowUpYet -> "NoFollowUpYet", None
    | FollowUp.FollowedByTool tool -> "FollowedByTool", Some (ToolName.value tool)
    | FollowUp.SessionEnded -> "SessionEnded", None

  let contextCostText = function
    | ContextCost.Tiny -> "Tiny"
    | ContextCost.Focused -> "Focused"
    | ContextCost.Heavy -> "Heavy"

  let explicitFeedbackKindText = function
    | ExplicitFeedbackKind.ToolOutputWasTooLarge -> "ToolOutputWasTooLarge"
    | ExplicitFeedbackKind.ToolIntentWasUnclear -> "ToolIntentWasUnclear"
    | ExplicitFeedbackKind.ToolNameWasMisleading -> "ToolNameWasMisleading"
    | ExplicitFeedbackKind.NeededAnotherToolToFinish -> "NeededAnotherToolToFinish"
    | ExplicitFeedbackKind.ResultDidNotEstablishTrust -> "ResultDidNotEstablishTrust"

  let alternativeParts = function
    | AlternativePath.NoAlternativeRecorded -> "NoAlternativeRecorded", None
    | AlternativePath.ResolvedWithTool tool -> "ResolvedWithTool", Some (ToolName.value tool)
    | AlternativePath.ResolvedOutsideMcp -> "ResolvedOutsideMcp", None

  let parseIntent = function
    | "VerifyChangedBehavior" -> Ok IntentKind.VerifyChangedBehavior
    | "RunExactTest" -> Ok IntentKind.RunExactTest
    | "ExploreCode" -> Ok IntentKind.ExploreCode
    | "InspectFailure" -> Ok IntentKind.InspectFailure
    | "RecoverSession" -> Ok IntentKind.RecoverSession
    | "ManageSession" -> Ok IntentKind.ManageSession
    | other -> Error (sprintf "Unknown intent kind '%s'." other)

  let parseBlocker = function
    | "SessionAmbiguous" -> Ok BlockerKind.SessionAmbiguous
    | "SessionMissing" -> Ok BlockerKind.SessionMissing
    | "SessionWarming" -> Ok BlockerKind.SessionWarming
    | "LoadedStateUnknown" -> Ok BlockerKind.LoadedStateUnknown
    | "LoadedStateStale" -> Ok BlockerKind.LoadedStateStale
    | "TypeIdentityCompromised" -> Ok BlockerKind.TypeIdentityCompromised
    | "ExactTestNotFound" -> Ok BlockerKind.ExactTestNotFound
    | "OutputTooLarge" -> Ok BlockerKind.OutputTooLarge
    | "AffordanceMismatch" -> Ok BlockerKind.AffordanceMismatch
    | "TransportFailure" -> Ok BlockerKind.TransportFailure
    | "OperationFailed" -> Ok BlockerKind.OperationFailed
    | "InvalidRequest" -> Ok BlockerKind.InvalidRequest
    | other -> Error (sprintf "Unknown blocker kind '%s'." other)

  let parseResolution resolutionKind resolutionTool =
    match resolutionKind, resolutionTool with
    | Some "SolvedWithRetry", _ -> Ok ResolutionKind.SolvedWithRetry
    | Some "SolvedWithDifferentTool", Some tool -> ToolName.create tool |> Result.map ResolutionKind.SolvedWithDifferentTool
    | Some "SolvedAfterReset", _ -> Ok ResolutionKind.SolvedAfterReset
    | Some "SolvedAfterSessionSwitch", _ -> Ok ResolutionKind.SolvedAfterSessionSwitch
    | Some "Unresolved", _ -> Ok ResolutionKind.Unresolved
    | None, _ -> Error "Missing resolution kind."
    | Some other, _ -> Error (sprintf "Unknown resolution kind '%s'." other)

  let parseOutcome outcomeKind blockerKind resolutionKind resolutionTool =
    match outcomeKind with
    | "CompletedCleanly" -> Ok FrictionOutcome.CompletedCleanly
    | "EncounteredBlocker" ->
      match blockerKind with
      | Some blocker -> parseBlocker blocker |> Result.map FrictionOutcome.EncounteredBlocker
      | None -> Error "Blocked outcome missing blocker kind."
    | "RecoveredVia" ->
      parseResolution resolutionKind resolutionTool |> Result.map FrictionOutcome.RecoveredVia
    | "AbandonedWithoutResolution" -> Ok FrictionOutcome.AbandonedWithoutResolution
    | other -> Error (sprintf "Unknown outcome kind '%s'." other)

  let parseFollowUp followUpKind followUpTool =
    match followUpKind with
    | "NoFollowUpYet" -> Ok FollowUp.NoFollowUpYet
    | "FollowedByTool" ->
      match followUpTool with
      | Some tool -> ToolName.create tool |> Result.map FollowUp.FollowedByTool
      | None -> Error "Follow-up tool missing tool name."
    | "SessionEnded" -> Ok FollowUp.SessionEnded
    | other -> Error (sprintf "Unknown follow-up kind '%s'." other)

  let parseContextCost = function
    | "Tiny" -> Ok ContextCost.Tiny
    | "Focused" -> Ok ContextCost.Focused
    | "Heavy" -> Ok ContextCost.Heavy
    | other -> Error (sprintf "Unknown context cost '%s'." other)

  let parseFeedbackKind = function
    | "ToolOutputWasTooLarge" -> Ok ExplicitFeedbackKind.ToolOutputWasTooLarge
    | "ToolIntentWasUnclear" -> Ok ExplicitFeedbackKind.ToolIntentWasUnclear
    | "ToolNameWasMisleading" -> Ok ExplicitFeedbackKind.ToolNameWasMisleading
    | "NeededAnotherToolToFinish" -> Ok ExplicitFeedbackKind.NeededAnotherToolToFinish
    | "ResultDidNotEstablishTrust" -> Ok ExplicitFeedbackKind.ResultDidNotEstablishTrust
    | other -> Error (sprintf "Unknown explicit feedback kind '%s'." other)

  let parseAlternative kind toolName =
    match kind with
    | "NoAlternativeRecorded" -> Ok AlternativePath.NoAlternativeRecorded
    | "ResolvedWithTool" ->
      match toolName with
      | Some tool -> ToolName.create tool |> Result.map AlternativePath.ResolvedWithTool
      | None -> Error "Alternative tool missing name."
    | "ResolvedOutsideMcp" -> Ok AlternativePath.ResolvedOutsideMcp
    | other -> Error (sprintf "Unknown alternative kind '%s'." other)

  let encodeEvent (event: FrictionEvent) : StoredEvent =
    let outcomeKind, blockerKind, resolutionKind, resolutionTool = outcomeParts event.Outcome
    let followUpKind, followUpTool = followUpParts event.FollowUp
    { OccurredAtUtc = event.OccurredAtUtc
      SessionId = SessionRef.value event.Session
      ToolName = ToolName.value event.Tool
      IntentKind = string event.Intent
      OutcomeKind = outcomeKind
      BlockerKind = blockerKind
      ResolutionKind = resolutionKind
      ResolutionToolName = resolutionTool
      DurationMs = DurationMs.value event.Duration
      FollowUpKind = followUpKind
      FollowUpToolName = followUpTool
      ContextCostKind = contextCostText event.ContextCost
      SageFsVersion = event.SageFsVersion
      AgentKey = event.AgentKey
      ErrorSignature = event.ErrorSignature }

  /// Brief B9: a JSON envelope written before this field existed deserializes
  /// it to CLR `null` (this is a `[<CLIMutable>]` type, not an F# record
  /// construction) — treat null exactly like "", mirroring the SQLite
  /// migration's back-compat default.
  let private orEmpty (s: string) = match s with null -> "" | v -> v

  let decodeEvent (event: StoredEvent) : Result<FrictionEvent, string> =
    match SessionRef.create event.SessionId with
    | Error err -> Error err
    | Ok session ->
      match ToolName.create event.ToolName with
      | Error err -> Error err
      | Ok tool ->
        match parseIntent event.IntentKind with
        | Error err -> Error err
        | Ok intent ->
          match parseOutcome event.OutcomeKind event.BlockerKind event.ResolutionKind event.ResolutionToolName with
          | Error err -> Error err
          | Ok outcome ->
            match DurationMs.create event.DurationMs with
            | Error err -> Error err
            | Ok duration ->
              match parseFollowUp event.FollowUpKind event.FollowUpToolName with
              | Error err -> Error err
              | Ok followUp ->
                match parseContextCost event.ContextCostKind with
                | Error err -> Error err
                | Ok contextCost ->
                  Ok
                    { OccurredAtUtc = event.OccurredAtUtc
                      Session = session
                      Tool = tool
                      Intent = intent
                      Outcome = outcome
                      Duration = duration
                      FollowUp = followUp
                      ContextCost = contextCost
                      SageFsVersion = orEmpty event.SageFsVersion
                      AgentKey = orEmpty event.AgentKey
                      ErrorSignature = orEmpty event.ErrorSignature }

  let encodeFeedback (feedback: ExplicitFeedback) : StoredFeedback =
    let alternativeKind, alternativeTool = alternativeParts feedback.AlternativeUsed
    { OccurredAtUtc = feedback.OccurredAtUtc
      SessionId = SessionRef.value feedback.Session
      ToolName = ToolName.value feedback.Tool
      FeedbackKind = explicitFeedbackKindText feedback.Kind
      ShortReason = feedback.ShortReason
      AlternativeKind = alternativeKind
      AlternativeToolName = alternativeTool
      SageFsVersion = feedback.SageFsVersion }

  let decodeFeedback (feedback: StoredFeedback) : Result<ExplicitFeedback, string> =
    match SessionRef.create feedback.SessionId with
    | Error err -> Error err
    | Ok session ->
      match ToolName.create feedback.ToolName with
      | Error err -> Error err
      | Ok tool ->
        match parseFeedbackKind feedback.FeedbackKind with
        | Error err -> Error err
        | Ok kind ->
          match parseAlternative feedback.AlternativeKind feedback.AlternativeToolName with
          | Error err -> Error err
          | Ok alternative ->
            Ok
              { OccurredAtUtc = feedback.OccurredAtUtc
                Session = session
                Tool = tool
                Kind = kind
                ShortReason = feedback.ShortReason
                AlternativeUsed = alternative
                SageFsVersion = feedback.SageFsVersion }

  let deserialize (text: string) =
    match String.IsNullOrWhiteSpace text with
    | true -> empty
    | false ->
      let stored = JsonSerializer.Deserialize<StoredEnvelope>(text, options)
      let events =
        stored.Events
        |> List.map decodeEvent
        |> List.choose (function | Ok value -> Some value | Error _ -> None)
      let feedback =
        stored.Feedback
        |> List.map decodeFeedback
        |> List.choose (function | Ok value -> Some value | Error _ -> None)
      { Events = events; Feedback = feedback }

  let serialize (value: FrictionEnvelope) =
    let stored : StoredEnvelope =
      { Events = value.Events |> List.map encodeEvent
        Feedback = value.Feedback |> List.map encodeFeedback }
    JsonSerializer.Serialize(stored, options)

module Recorder =
  [<Literal>]
  let StoreKey = "mcp-friction"

  // ── FrictionStore-backed operations (preferred, durable SQLite) ──

  let appendEventDirect (store: FrictionSqlite.FrictionStore) (event: FrictionEvent) =
    task {
      return store.AppendEvent event
    }

  let appendFeedbackDirect (store: FrictionSqlite.FrictionStore) (feedback: ExplicitFeedback) =
    task {
      return store.AppendFeedback feedback
    }

  let readEnvelopeDirect (store: FrictionSqlite.FrictionStore) =
    task {
      let eventsResult = store.ReadEvents()
      let feedbackResult = store.ReadFeedback()
      return
        match eventsResult, feedbackResult with
        | Ok events, Ok feedback ->
          Ok ({ Events = events; Feedback = feedback } : FrictionEnvelope)
        | Error err, _ -> Error err
        | _, Error err -> Error err
    }

  /// Filter an envelope to only include events/feedback matching the given version.
  /// Empty string means "pre-versioning" data; None means "no filter, return all."
  let filterByVersion (versionFilter: string option) (envelope: FrictionEnvelope) =
    match versionFilter with
    | None -> envelope
    | Some version ->
      { Events = envelope.Events |> List.filter (fun e -> e.SageFsVersion = version)
        Feedback = envelope.Feedback |> List.filter (fun f -> f.SageFsVersion = version) }

  let summarizeDirect (store: FrictionSqlite.FrictionStore) (versionFilter: string option) =
    task {
      let! envelopeResult = readEnvelopeDirect store
      return
        match envelopeResult with
        | Ok envelope ->
          let filtered = filterByVersion versionFilter envelope
          let observedSignals = ObservedFriction.detectAll DetectorConfig.defaults filtered.Events
          Ok (
            [ yield sprintf "Top blockers: %d" (Summaries.topBlockers filtered.Events |> List.length)
              yield sprintf "Tracked tools: %d" (Summaries.toolSummaries filtered.Events |> List.length)
              yield sprintf "Explicit feedback items: %d" filtered.Feedback.Length
              yield sprintf "Observed signals: %d" observedSignals.Length
              match versionFilter with
              | Some v -> yield sprintf "Filtered to version: %s" v
              | None -> () ]
            |> String.concat "\n")
        | Error err -> Error (sprintf "Friction store read failed: %s" err)
    }

  /// Builds the report AND the observed (passively-detected) signals over
  /// the same version-filtered event stream (Brief B7). See
  /// `FrictionReportWithSignals`'s doc comment for why this bundle lives
  /// here rather than as a field on Core's `FrictionReport`.
  ///
  /// `detectorConfig` defaults to the live defaults. It is injectable so a
  /// test replaying a HISTORICAL harvest — recorded before a tool rename —
  /// can pin the name that harvest was actually captured under instead of
  /// silently producing no polling signal. Production always uses the default.
  let reportDirectWith
    (store: FrictionSqlite.FrictionStore)
    (versionFilter: string option)
    (detectorConfig: DetectorConfig)
    =
    task {
      let! envelopeResult = readEnvelopeDirect store
      return
        match envelopeResult with
        | Ok envelope ->
          let filtered = filterByVersion versionFilter envelope
          let report = Summaries.frictionReport filtered.Events filtered.Feedback
          let observedSignals = ObservedFriction.detectAll detectorConfig filtered.Events
          Ok ({ Report = report; ObservedSignals = observedSignals } : FrictionReportWithSignals)
        | Error err -> Error (sprintf "Friction store read failed: %s" err)
    }

  let reportDirect (store: FrictionSqlite.FrictionStore) (versionFilter: string option) =
    reportDirectWith store versionFilter DetectorConfig.defaults

  // ── Friction signals -> action queue (roast §1/§3: `ObservedFriction.
  // detectAll` was computed and shown by read-model tools, but nothing fed
  // it into `ActionPrioritizer`'s ranked queue) ──────────────────────────

  /// The prefix `McpServer.fs`'s `recordToolFailure` stamps when a tool
  /// call's argument parsing fails BEFORE the tool name resolves (e.g.
  /// "unknown (missing argument)") — `ObservedFrictionUnattributed` already
  /// turns that exact class into its own `UnattributedFailure` signal
  /// (Strong confidence, carries no fabricated tool identity). A
  /// TOOL-KEYED signal (ExcessivePolling / InvalidStateCall / RetryLoop /
  /// Abandonment) that still carries an "unknown"-prefixed tool name would
  /// double-count the same failure under a made-up tool identity, so those
  /// are EXCLUDED here rather than surfaced as an action against a tool
  /// that was never really named. `UnattributedFailure` itself is never
  /// filtered — it is the correct, already-honest home for that evidence.
  let private isUnattributedToolName (name: string) =
    name.StartsWith("unknown", System.StringComparison.Ordinal)

  /// Project one detected friction signal (`ObservedFriction.detectAll`'s
  /// output) into what `ActionPrioritizer.compose` needs. Lives here — not
  /// in `ActionPrioritizer.fs` — because that file compiles BEFORE
  /// `ObservedFrictionTypes.fs` in `SageFs.Core.fsproj` (see
  /// `FrictionSignalReport`'s own doc comment); this file compiles after
  /// all of Core, so `DetectedSignal` is fully available here.
  let tryProjectFrictionSignal (detected: DetectedSignal) : SageFs.Features.ActionPrioritizer.FrictionSignalReport option =
    let signalId = FrictionSignal.id detected.Signal
    let windowText (w: EvidenceWindow) =
      sprintf "%s .. %s" (w.FirstAtUtc.ToString("O")) (w.LastAtUtc.ToString("O"))
    let mk evidenceCount windowDescription : SageFs.Features.ActionPrioritizer.FrictionSignalReport option =
      Some { SignalId = signalId; EvidenceCount = evidenceCount; WindowDescription = windowDescription }
    let mkForTool tool evidenceCount windowDescription =
      match isUnattributedToolName (ToolName.value tool) with
      | true -> None
      | false -> mk evidenceCount windowDescription
    match detected.Signal with
    | FrictionSignal.ExcessivePolling(tool, calls, _successes, window) ->
      mkForTool tool calls (windowText window)
    | FrictionSignal.ResetThrash(resets, window) ->
      mk resets (windowText window)
    | FrictionSignal.HardResetAfterCreate gap ->
      mk 1 (sprintf "%dms after create" (DurationMs.value gap))
    | FrictionSignal.UnattributedFailure(rawTool, count) ->
      mk count rawTool
    | FrictionSignal.InvalidStateCall(tool, blocked) ->
      mkForTool tool blocked ""
    | FrictionSignal.RepeatedSameError(blocker, run) ->
      mk run (string blocker)
    | FrictionSignal.RetryLoop(tool, attempts) ->
      mkForTool tool attempts ""
    | FrictionSignal.Abandonment(tool, lastBlocker) ->
      mkForTool tool 1 (string lastBlocker)
    | FrictionSignal.SlowTimeToFirstSuccess(elapsed, failedBefore) ->
      mk (max 1 failedBefore) (sprintf "%dms elapsed" (DurationMs.value elapsed))

  /// Read + detect + project observed friction signals for the action
  /// queue. `FrictionStore.ReadEvents` is already synchronous (a plain
  /// SQLite call — Task-wrapped elsewhere only to match an async-shaped
  /// interface), version-scoped via `filterByVersion` so a signal can never
  /// straddle an upgrade boundary. Shared by `Mcp.fs`'s `suggest_next_action`
  /// tool and `McpServer.fs`'s `ModelChanged` push-notification subscription.
  let computeFrictionSignalReports (frictionStore: FrictionSqlite.FrictionStore option) =
    match frictionStore with
    | None -> []
    | Some store ->
      match store.ReadEvents() with
      | Error _ -> []
      | Ok events ->
        let currentVersion = SageFsVersion.current ()
        let filtered =
          filterByVersion (Some currentVersion) ({ Events = events; Feedback = [] } : FrictionEnvelope)
        ObservedFriction.detectAll DetectorConfig.defaults filtered.Events
        |> List.choose tryProjectFrictionSignal
