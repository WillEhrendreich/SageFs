namespace SageFs

open SageFs.Features.LiveTesting

/// Where a failure happened in the user's own code.
type FailureLocation = {
  FilePath: string
  Line: int
}

module FailureLocationParser =
  let private linePattern =
    System.Text.RegularExpressions.Regex(
      @"in\s+(.+?):line\s+(\d+)",
      System.Text.RegularExpressions.RegexOptions.Compiled)

  let private frameworkPrefixes = [| "Expecto"; "FSharp.Core"; "System."; "Microsoft." |]

  /// Parse the first user-code location from a .NET stack trace.
  let tryParse (stackTrace: string) : FailureLocation option =
    match System.String.IsNullOrWhiteSpace stackTrace with
    | true -> None
    | false ->
      stackTrace.Split([| '\n'; '\r' |], System.StringSplitOptions.RemoveEmptyEntries)
      |> Array.tryPick (fun line ->
        let m = linePattern.Match(line)
        match m.Success with
        | true ->
          let filePath = m.Groups.[1].Value.Trim()
          let isFramework =
            frameworkPrefixes |> Array.exists (fun prefix -> filePath.Contains(prefix))
          match isFramework with
          | true -> None
          | false -> Some { FilePath = filePath; Line = int m.Groups.[2].Value }
        | false -> None)

/// What `GET /api/live-testing/status` and the `get_live_test_status` tool say about one session's live
/// testing. Pure: the session's state in, one JSON document out.
module LiveTestStatusView =

  let render (activeId: string) (state: LiveTestState) (fileFilter: string option) : string =
    let discoveryState = LiveTestState.discoveryState state
    let discoveryRequiresEval = LiveTestState.requiresPrimingEval state
    let sessionEntries = LiveTestState.statusEntriesForSession activeId state
    let summary =
      TestSummary.fromStatuses state.Activation (sessionEntries |> Array.map (fun e -> e.Status))
    let tests =
      match fileFilter with
      | Some f ->
        let normalizedFilter = f.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar)
        sessionEntries |> Array.filter (fun e ->
          match e.Origin with
          | TestOrigin.SourceMapped (file, _) ->
            file = normalizedFilter
            || file.EndsWith(normalizedFilter, System.StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString() + normalizedFilter, System.StringComparison.OrdinalIgnoreCase)
          | TestOrigin.ReflectionOnly -> false)
        |> Some
      | None -> None
    let resp = System.Collections.Generic.Dictionary<string, obj>()
    resp["Enabled"] <- box (state.Activation = LiveTestingActivation.Active)
    resp["Summary"] <- box summary
    // How many rows ran against evaluated code that no real build has confirmed yet. Zero means every row
    // says what a build made of it.
    resp["Unconfirmed"] <-
      box (
        sessionEntries
        |> Array.filter (fun e -> match e.Provenance with ResultProvenance.Evaluated -> true | _ -> false)
        |> Array.length)
    // The run the rows are from: it advances with every run, so a client (or a test) can tell that a run
    // happened after the one it last saw.
    resp["Generation"] <- box (RunGeneration.value state.LastGeneration)
    resp["Pause"] <- box (LivePause.toWireValue state.Pause)
    resp["Scope"] <- box (TestScope.toWire state.Scope)
    resp["DiscoveryState"] <- box (LiveTestDiscoveryState.toWireValue discoveryState)
    resp["DiscoveryHint"] <- box (LiveTestState.discoveryHint state)
    resp["DiscoveryRequiresEval"] <- box discoveryRequiresEval
    match state.LastDecision with
    | Some decision -> resp["LastDecision"] <- box (LiveTestingDecision.toWireModel decision)
    | None -> ()
    match state.LastDiscoveryTime > System.DateTimeOffset.MinValue with
    | true -> resp["LastDiscoveryTime"] <- box state.LastDiscoveryTime
    | false -> ()
    match tests with
    | Some t -> resp["Tests"] <- box t
    | None -> ()
    let bitmapCount = Map.count state.TestCoverageBitmaps
    match bitmapCount > 0 with
    | true ->
      let avgProbes =
        state.TestCoverageBitmaps
        |> Map.toSeq
        |> Seq.map (fun (_, bm) -> CoverageBitmap.popCount bm)
        |> Seq.averageBy float
      resp["CoverageBitmapStats"] <- box {| TestsWithCoverage = bitmapCount; AvgHitProbes = avgProbes |}
    | false -> ()
    let failedEntries = match tests with | Some t -> t | None -> sessionEntries
    let failedTests =
      failedEntries
      |> Array.choose (fun e ->
        match e.Status with
        | TestRunStatus.Failed (failure, duration) ->
          let msg =
            match failure with
            | TestFailure.AssertionFailed m -> m
            | TestFailure.ExceptionThrown (m, _) -> m
            | TestFailure.TimedOut after -> sprintf "Timed out after %dms" (int after.TotalMilliseconds)
          let location =
            match failure with
            | TestFailure.ExceptionThrown (_, st) ->
              FailureLocationParser.tryParse st
              |> Option.map (fun fl -> {| File = fl.FilePath; Line = fl.Line |})
            | _ -> None
          Some {| Name = e.DisplayName; Message = msg; DurationMs = int duration.TotalMilliseconds; Location = location |}
        | _ -> None)
      |> Array.truncate 20
    match failedTests.Length > 0 with
    | true -> resp["FailedTests"] <- box failedTests
    | false -> ()
    Json.serialize Json.standard resp
