module SageFs.McpPushNotifications

open System
open System.Collections.Concurrent
open SageFs.Features.Diagnostics
open SageFs.Features.LiveTesting

/// Structured events for the push notification accumulator.
/// Stored as data, formatted for the LLM only on drain.
[<RequireQualifiedAccess>]
type PushEvent =
  /// Diagnostics changed — carries the current set of errors/warnings.
  | DiagnosticsChanged of errors: (string * int * string) list
  /// Elm model state changed — carries output & diag counts.
  | StateChanged of outputCount: int * diagCount: int
  /// A watched file was reloaded by the file watcher.
  | FileReloaded of path: string
  /// Session became faulted.
  | SessionFaulted of error: string
  /// Warmup completed.
  | WarmupCompleted
  /// Test summary changed — carries summary record.
  | TestSummaryChanged of summary: TestSummary * lastDecision: LiveTestingDecision option
  /// Test results batch — carries enriched payload with generation, freshness, entries, summary.
  | TestResultsBatch of payload: TestResultsBatchPayload
  /// File annotations — per-file inline feedback (test status, coverage, CodeLens, failures).
  | FileAnnotationsUpdated of annotations: FileAnnotations
  /// Diagnosis report ready — composed analysis of failures, staleness, suggestions, performance.
  | DiagnosisReady of report: SageFs.Features.Diagnostician.DiagnosticReport
  /// Impact alert — a cell's performance or downstream impact crossed a threshold.
  | ImpactAlert of report: SageFs.Features.ImpactForecast.ImpactForecastReport
  /// Prioritized action queue ready — ranked "what to do next" for the session.
  | ActionQueueReady of report: SageFs.Features.ActionPrioritizer.ActionQueueReport
  /// Resolved test source locations — maps test names to file paths and line numbers.
  | TestSourceLocations of locations: Features.LiveTesting.TestSourceLocation list
  /// Failure narratives — Passed→Failed transition context for each failing test.
  | FailureNarrativesUpdated of narratives: Map<Features.LiveTesting.TestId, Features.LiveTesting.FailureNarrative>
  /// Elm loop exception — surfaced from a catch site inside the Elm dispatch loop.
  | SystemAlarm of phase: string * message: string

/// Whether an event REPLACES the previous instance of the same kind
/// (state/set semantics) or ACCUMULATES alongside it (delta/list semantics).
[<RequireQualifiedAccess>]
type MergeStrategy = Replace | Accumulate

module PushEvent =
  /// Determine how an event merges with existing events of the same type.
  let mergeStrategy = function
    | PushEvent.DiagnosticsChanged _ -> MergeStrategy.Replace
    | PushEvent.StateChanged _ -> MergeStrategy.Replace
    | PushEvent.SessionFaulted _ -> MergeStrategy.Replace
    | PushEvent.FileReloaded _ -> MergeStrategy.Accumulate
    | PushEvent.WarmupCompleted -> MergeStrategy.Replace
    | PushEvent.TestSummaryChanged _ -> MergeStrategy.Replace
    | PushEvent.TestResultsBatch _ -> MergeStrategy.Replace
    | PushEvent.FileAnnotationsUpdated _ -> MergeStrategy.Replace
    | PushEvent.DiagnosisReady _ -> MergeStrategy.Replace
    | PushEvent.ImpactAlert _ -> MergeStrategy.Replace
    | PushEvent.ActionQueueReady _ -> MergeStrategy.Replace
    | PushEvent.TestSourceLocations _ -> MergeStrategy.Replace
    | PushEvent.FailureNarrativesUpdated _ -> MergeStrategy.Replace
    | PushEvent.SystemAlarm _ -> MergeStrategy.Replace

  /// Discriminator tag used for Replace dedup.
  let tag = function
    | PushEvent.DiagnosticsChanged _ -> 0
    | PushEvent.StateChanged _ -> 1
    | PushEvent.FileReloaded _ -> 2
    | PushEvent.SessionFaulted _ -> 3
    | PushEvent.WarmupCompleted -> 4
    | PushEvent.TestSummaryChanged _ -> 5
    | PushEvent.TestResultsBatch _ -> 6
    | PushEvent.FileAnnotationsUpdated _ -> 7
    | PushEvent.DiagnosisReady _ -> 8
    | PushEvent.ImpactAlert _ -> 9
    | PushEvent.ActionQueueReady _ -> 10
    | PushEvent.TestSourceLocations _ -> 11
    | PushEvent.FailureNarrativesUpdated _ -> 13
    | PushEvent.SystemAlarm _ -> 12

  /// Format a single event for LLM consumption — actionable, concise.
  let formatForLlm = function
    | PushEvent.DiagnosticsChanged errors when errors.IsEmpty ->
      "✓ diagnostics cleared"
    | PushEvent.DiagnosticsChanged errors ->
      let lines =
        errors
        |> List.truncate 5
        |> List.map (fun (file, line, msg) ->
          sprintf "  %s:%d — %s" (IO.Path.GetFileName file) line msg)
      let header = sprintf "⚠ %d diagnostic(s):" errors.Length
      let truncNote =
        match errors.Length > 5 with
        | true -> sprintf "\n  … and %d more" (errors.Length - 5)
        | false -> ""
      sprintf "%s\n%s%s" header (String.concat "\n" lines) truncNote
    | PushEvent.StateChanged (outputCount, diagCount) ->
      sprintf "state: output=%d diags=%d" outputCount diagCount
    | PushEvent.FileReloaded path ->
      sprintf "📄 reloaded %s" (IO.Path.GetFileName path)
    | PushEvent.SessionFaulted error ->
      sprintf "🔴 session faulted: %s" error
    | PushEvent.WarmupCompleted ->
      "✓ warmup complete"
    | PushEvent.TestSummaryChanged (s, lastDecision) ->
      let suffix =
        lastDecision
        |> Option.map (fun decision -> sprintf " — %s" (LiveTestingDecision.statusBarHint decision))
        |> Option.defaultValue ""
      sprintf "🧪 tests: %d total, %d passed, %d failed, %d stale, %d running%s" s.Total s.Passed s.Failed s.Stale s.Running suffix
    | PushEvent.TestResultsBatch payload ->
      sprintf "🧪 %d test result(s) received (%A)" payload.Entries.Length payload.Freshness
    | PushEvent.FileAnnotationsUpdated ann ->
      sprintf "📝 file annotations for %s (%d tests, %d lenses, %d failures)"
        (IO.Path.GetFileName ann.FilePath) ann.TestAnnotations.Length ann.CodeLenses.Length ann.InlineFailures.Length
    | PushEvent.DiagnosisReady report ->
      let severityIcon =
        match report.Severity with
        | SageFs.Features.Diagnostician.DiagnosticSeverity.Critical -> "🔴"
        | SageFs.Features.Diagnostician.DiagnosticSeverity.Warning -> "🟡"
        | SageFs.Features.Diagnostician.DiagnosticSeverity.Info -> "🟢"
      sprintf "%s diagnosis: %d failure(s), %d affected cell(s), %d suggestion(s)"
        severityIcon report.Failures.Length report.AffectedCells.Length report.SuggestedFixes.Length
    | PushEvent.ImpactAlert report ->
      let icon =
        match report.Recommendation with
        | SageFs.Features.ImpactForecast.ImpactRecommendation.Refactor -> "🔴"
        | SageFs.Features.ImpactForecast.ImpactRecommendation.Investigate -> "⚠️"
        | SageFs.Features.ImpactForecast.ImpactRecommendation.Acceptable -> "✅"
      sprintf "%s impact: cell %d — P95=%.0fms, %d downstream"
        icon report.CellId report.P95Ms report.DownstreamCellCount
    | PushEvent.ActionQueueReady report ->
      let icon =
        match report.HealthGrade with
        | SageFs.Features.ActionPrioritizer.SessionHealthGrade.Critical _ -> "🔴"
        | SageFs.Features.ActionPrioritizer.SessionHealthGrade.NeedsAttention _ -> "⚠️"
        | SageFs.Features.ActionPrioritizer.SessionHealthGrade.Healthy -> "✅"
      sprintf "%s action queue: %d action(s), %d failure(s), %d blind spot(s)"
        icon report.Actions.Length report.TotalFailures report.TotalBlindSpots
    | PushEvent.TestSourceLocations locs ->
      sprintf "📍 source locations: %d test(s) resolved" locs.Length
    | PushEvent.FailureNarrativesUpdated narratives ->
      let count = narratives |> Map.filter (fun _ n -> n.Summary <> "") |> Map.count
      sprintf "🔍 failure narratives: %d test(s) with causal context" count
    | PushEvent.SystemAlarm (phase, msg) ->
      sprintf "🚨 system alarm [%s]: %s" phase msg

type AccumulatedEvent = {
  Timestamp: DateTimeOffset
  Event: PushEvent
}

/// Thread-safe accumulator with smart dedup.
/// Replace-strategy events overwrite the previous instance.
/// Accumulate-strategy events are appended.
type EventAccumulator() =
  let events = ConcurrentQueue<AccumulatedEvent>()
  let maxEvents = 50
  let replaceLock = obj()

  member _.Add(evt: PushEvent) =
    let entry = { Timestamp = DateTimeOffset.UtcNow; Event = evt }
    match PushEvent.mergeStrategy evt with
    | MergeStrategy.Replace ->
      lock replaceLock (fun () ->
        let tag = PushEvent.tag evt
        let temp = ResizeArray()
        let mutable item = Unchecked.defaultof<AccumulatedEvent>
        while events.TryDequeue(&item) do
          match PushEvent.tag item.Event <> tag with
          | true -> temp.Add(item)
          | false -> ()
        for e in temp do events.Enqueue(e)
        events.Enqueue(entry))
    | MergeStrategy.Accumulate ->
      events.Enqueue(entry)
      lock replaceLock (fun () ->
        while events.Count > maxEvents do
          events.TryDequeue() |> ignore)

  member _.Drain() =
    lock replaceLock (fun () ->
      let result = ResizeArray()
      let mutable item = Unchecked.defaultof<AccumulatedEvent>
      while events.TryDequeue(&item) do
        result.Add(item)
      result.ToArray())

  member _.Count = events.Count
