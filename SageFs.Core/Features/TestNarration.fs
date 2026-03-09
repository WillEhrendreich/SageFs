namespace SageFs.Features

open System
open SageFs.Features.LiveTesting

/// Level of detail for narration output.
[<RequireQualifiedAccess>]
type NarrationDetail = Minimal | Normal | Full

module TestNarration =

  /// Produce a human-readable label for a TestRunStatus.
  let statusLabel = function
    | TestRunStatus.Detected -> "Detected"
    | TestRunStatus.Queued -> "Queued"
    | TestRunStatus.Running -> "Running"
    | TestRunStatus.Stale -> "Stale"
    | TestRunStatus.PolicyDisabled -> "Disabled by policy"
    | TestRunStatus.Skipped reason -> sprintf "Skipped: %s" reason
    | TestRunStatus.Passed dur ->
      sprintf "Passed in %.0fms" dur.TotalMilliseconds
    | TestRunStatus.Failed (failure, dur) ->
      match failure with
      | TestFailure.AssertionFailed msg ->
        sprintf "Failed in %.0fms — %s" dur.TotalMilliseconds msg
      | TestFailure.ExceptionThrown (msg, _) ->
        sprintf "Failed in %.0fms — %s" dur.TotalMilliseconds msg
      | TestFailure.TimedOut after ->
        sprintf "Timed out after %.1fs" after.TotalSeconds

  /// Short test name from fully-qualified name.
  let private shortName (fullName: string) =
    match fullName.LastIndexOf('.') with
    | -1 -> fullName
    | idx -> fullName.Substring(idx + 1)

  /// Narrate a test failure into a human-readable story.
  let narrateFailure
    (testName: string)
    (failure: TestFailure)
    (duration: TimeSpan)
    (narrative: FailureNarrative option) : string =
    let short = shortName testName
    let failureLine =
      match failure with
      | TestFailure.AssertionFailed msg ->
        sprintf "🔴 %s failed: %s" short msg
      | TestFailure.ExceptionThrown (msg, _trace) ->
        sprintf "💥 %s threw an exception: %s" short msg
      | TestFailure.TimedOut after ->
        sprintf "⏱️ %s timed out after %.1fs" short after.TotalSeconds

    let timingLine = sprintf "  (ran in %.0fms)" duration.TotalMilliseconds

    let causalLine =
      match narrative with
      | Some n when n.CausalChanges <> [] ->
        let changes =
          n.CausalChanges
          |> List.map (function
            | CausalChange.SymbolChanged s -> s
            | CausalChange.FileChanged f -> f
            | CausalChange.Unknown -> "unknown change")
          |> String.concat ", "
        Some (sprintf "  Likely caused by: %s" changes)
      | _ -> None

    let timeSinceLine =
      match narrative with
      | Some n ->
        match n.TimeSinceLastPass with
        | Some ts when ts.TotalHours >= 1.0 ->
          Some (sprintf "  Was passing %.0f hours ago" ts.TotalHours)
        | Some ts ->
          Some (sprintf "  Was passing %.0f minutes ago" ts.TotalMinutes)
        | None -> None
      | None -> None

    let propertyLine =
      match narrative with
      | Some n ->
        match n.PropertyViolation with
        | Some pv ->
          let cat = pv.AlgebraicCategory |> Option.defaultValue "property"
          Some (sprintf "  Property violation (%s): counterexample %s" cat pv.ShrunkCounterexample)
        | None -> None
      | None -> None

    [ Some failureLine
      Some timingLine
      causalLine
      timeSinceLine
      propertyLine ]
    |> List.choose id
    |> String.concat "\n"

  /// Narrate any TestResult into a human-readable story.
  let narrateResult (testName: string) (result: TestResult) : string =
    let short = shortName testName
    match result with
    | TestResult.Passed dur ->
      sprintf "✅ %s passed in %.0fms" short dur.TotalMilliseconds
    | TestResult.Failed (failure, dur) ->
      narrateFailure testName failure dur None
    | TestResult.Skipped reason ->
      sprintf "⏭️ %s skipped: %s" short reason
    | TestResult.NotRun ->
      sprintf "⬜ %s has not yet run" short

  /// Narrate with density control: Minimal is brief, Normal/Full add detail.
  let narrateAtDensity
    (density: NarrationDetail)
    (testName: string)
    (result: TestResult)
    (narrative: FailureNarrative option) : string =
    match density, result with
    | NarrationDetail.Minimal, TestResult.Passed dur ->
      sprintf "✅ %.0fms" dur.TotalMilliseconds
    | NarrationDetail.Minimal, TestResult.Failed (failure, _dur) ->
      match failure with
      | TestFailure.AssertionFailed msg ->
        let truncated =
          match msg.Length > 50 with
          | true -> msg.Substring(0, 47) + "..."
          | false -> msg
        sprintf "🔴 %s" truncated
      | TestFailure.ExceptionThrown (msg, _) ->
        sprintf "💥 %s" (match msg.Length > 50 with true -> msg.Substring(0, 47) + "..." | false -> msg)
      | TestFailure.TimedOut after ->
        sprintf "⏱️ %.1fs" after.TotalSeconds
    | NarrationDetail.Minimal, TestResult.Skipped _ -> "⏭️ skipped"
    | NarrationDetail.Minimal, TestResult.NotRun -> "⬜ not run"
    | _, TestResult.Failed (failure, dur) ->
      narrateFailure testName failure dur narrative
    | _ -> narrateResult testName result
