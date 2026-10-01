module SageFs.Features.McpFrictionRecorder

open System
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
