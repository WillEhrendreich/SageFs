namespace SageFs.VisualStudio.Core

open System
open System.Text.Json

/// Pure JSON parsers for SSE events from the /events endpoint.
[<RequireQualifiedAccess>]
module LiveTestingParser =
  let tryStr (el: JsonElement) (prop: string) (fb: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.String then v.GetString() else fb

  let tryInt (el: JsonElement) (prop: string) (fb: int) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.Number then v.GetInt32() else fb

  let getProp (el: JsonElement) (prop: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) then Some v else None

  let parseDurationToMs (dur: string) =
    let parts = dur.Split(':')
    if parts.Length = 3 then
      let h = float parts.[0]
      let m = float parts.[1]
      let s = float parts.[2]
      Some ((h * 3600.0 + m * 60.0 + s) * 1000.0)
    else None

  let parseTestId (el: JsonElement) =
    match getProp el "Fields" with
    | Some fields when fields.ValueKind = JsonValueKind.Array ->
      let first = fields.[0]
      if first.ValueKind = JsonValueKind.String then TestId.create (first.GetString())
      else TestId.create (first.GetRawText())
    | _ ->
      if el.ValueKind = JsonValueKind.String then TestId.create (el.GetString())
      else TestId.create (el.GetRawText())

  let parseTestInfo (entry: JsonElement) =
    let id =
      match getProp entry "TestId" with
      | Some tid -> parseTestId tid
      | None -> TestId.create ""
    let filePath, line =
      match getProp entry "Origin" with
      | Some origin ->
        let case = tryStr origin "Case" ""
        match case with
        | "SourceMapped" ->
          match getProp origin "Fields" with
          | Some fields when fields.ValueKind = JsonValueKind.Array && fields.GetArrayLength() >= 2 ->
            let fp =
              if fields.[0].ValueKind = JsonValueKind.String then Some(fields.[0].GetString())
              else None
            let ln =
              if fields.[1].ValueKind = JsonValueKind.Number then Some(fields.[1].GetInt32())
              else None
            fp, ln
          | _ -> None, None
        | _ -> None, None
      | None -> None, None
    { Id = id
      DisplayName = tryStr entry "DisplayName" ""
      FullName = tryStr entry "FullName" ""
      FilePath = filePath
      Line = line }

  let parseTestResult (entry: JsonElement) =
    let id =
      match getProp entry "TestId" with
      | Some tid -> parseTestId tid
      | None -> TestId.create ""
    let status = getProp entry "Status"
    let statusCase =
      match status with
      | Some s -> tryStr s "Case" "Detected"
      | None -> "Detected"
    let outcome, durationMs =
      match statusCase with
      | "Passed" ->
        match status with
        | Some s ->
          match getProp s "Fields" with
          | Some fields when fields.ValueKind = JsonValueKind.Array && fields.GetArrayLength() >= 1 ->
            let dur =
              if fields.[0].ValueKind = JsonValueKind.String then
                parseDurationToMs (fields.[0].GetString())
              else None
            TestOutcome.Passed (dur |> Option.defaultValue 0.0), dur
          | _ -> TestOutcome.Passed 0.0, None
        | None -> TestOutcome.Passed 0.0, None
      | "Failed" ->
        match status with
        | Some s ->
          match getProp s "Fields" with
          | Some fields when fields.ValueKind = JsonValueKind.Array && fields.GetArrayLength() >= 1 ->
            let failObj = fields.[0]
            let msg =
              match getProp failObj "Fields" with
              | Some flds when flds.ValueKind = JsonValueKind.Array && flds.GetArrayLength() >= 1 ->
                if flds.[0].ValueKind = JsonValueKind.String then flds.[0].GetString()
                else "test failed"
              | _ ->
                if failObj.ValueKind = JsonValueKind.String then failObj.GetString()
                else "test failed"
            let dur =
              if fields.GetArrayLength() >= 2 && fields.[1].ValueKind = JsonValueKind.String then
                parseDurationToMs (fields.[1].GetString())
              else None
            TestOutcome.Failed (msg, dur), dur
          | _ -> TestOutcome.Failed ("test failed", None), None
        | None -> TestOutcome.Failed ("test failed", None), None
      | "Skipped" ->
        match status with
        | Some s ->
          match getProp s "Fields" with
          | Some fields when fields.ValueKind = JsonValueKind.Array && fields.GetArrayLength() >= 1 ->
            let reason =
              if fields.[0].ValueKind = JsonValueKind.String then fields.[0].GetString()
              else ""
            TestOutcome.Skipped reason, None
          | _ -> TestOutcome.Skipped "", None
        | None -> TestOutcome.Skipped "", None
      | "Stale" -> TestOutcome.Stale, None
      | "PolicyDisabled" -> TestOutcome.PolicyDisabled, None
      | "Running" -> TestOutcome.Running, None
      | _ -> TestOutcome.Detected, None
    { Id = id; Outcome = outcome; DurationMs = durationMs; Output = None }

  let parseSelectionPrecision (value: string) =
    match value with
    | "exact_dependency_match" -> Some SelectionPrecision.ExactDependencyMatch
    | "coverage_approximation" -> Some SelectionPrecision.CoverageApproximation
    | "conservative_fallback" -> Some SelectionPrecision.ConservativeFallback
    | "no_impacted_tests" -> Some SelectionPrecision.NoImpactedTests
    | "suppressed_by_policy" -> Some SelectionPrecision.SuppressedByPolicy
    | _ -> None

  let parseFreshnessTrust (value: string) =
    match value with
    | "fresh_exact" -> Some FreshnessTrust.FreshExact
    | "fresh_approximate" -> Some FreshnessTrust.FreshApproximate
    | "stale_awaiting_rerun" -> Some FreshnessTrust.StaleAwaitingRerun
    | "suppressed" -> Some FreshnessTrust.Suppressed
    | _ -> None

  let parseRerunCause (value: string) =
    match value with
    | "keystroke_buffered" -> Some RerunCause.KeystrokeBuffered
    | "file_saved" -> Some RerunCause.FileSaved
    | "explicit_run_requested" -> Some RerunCause.ExplicitRunRequested
    | _ -> None

  let parseStringArray (el: JsonElement) =
    match el.ValueKind with
    | JsonValueKind.Array ->
      [| for item in el.EnumerateArray() do
           if item.ValueKind = JsonValueKind.String then
             yield item.GetString() |]
    | _ -> [||]

  let parseLastDecision (root: JsonElement) : LiveTestingDecision option =
    let readStringValue (element: JsonElement option) =
      match element with
      | Some el when el.ValueKind = JsonValueKind.String -> Some (el.GetString())
      | _ -> None
    let precision = getProp root "Precision" |> readStringValue |> Option.bind parseSelectionPrecision
    let trust = getProp root "Trust" |> readStringValue |> Option.bind parseFreshnessTrust
    let cause = getProp root "Cause" |> readStringValue |> Option.bind parseRerunCause
    match precision, trust, cause with
    | Some precision, Some trust, Some cause ->
      let readString prop =
        match getProp root prop with
        | Some el when el.ValueKind = JsonValueKind.String -> el.GetString()
        | _ -> ""
      let readArray prop =
        match getProp root prop with
        | Some el -> parseStringArray el
        | None -> [||]
      Some {
        Cause = cause
        FilePath = readString "FilePath"
        Precision = precision
        Trust = trust
        ChangedSymbols = readArray "ChangedSymbols"
        SelectedTests = readArray "SelectedTests"
        DeferredTests = readArray "DeferredTests"
        Reason = readString "Reason" }
    | _ -> None

  let parseSummary (root: JsonElement) =
    { Total = tryInt root "Total" 0
      Passed = tryInt root "Passed" 0
      Failed = tryInt root "Failed" 0
      Running = tryInt root "Running" 0
      Stale = tryInt root "Stale" 0
      Disabled = tryInt root "Disabled" 0
      DiscoveryState = tryStr root "DiscoveryState" "disabled"
      DiscoveryGeneration =
        match getProp root "DiscoveryGeneration" with
        | Some el when el.ValueKind = JsonValueKind.Number -> el.GetInt64()
        | _ -> 0L
      LastDecision = getProp root "LastDecision" |> Option.bind parseLastDecision }

  let parseFreshness (root: JsonElement) : ResultFreshness =
    match getProp root "Freshness" with
    | Some el when el.ValueKind = JsonValueKind.Object ->
      match tryStr el "Case" "Fresh" with
      | "StaleCodeEdited" -> ResultFreshness.StaleCodeEdited
      | "StaleWrongGeneration" -> ResultFreshness.StaleWrongGeneration
      | _ -> ResultFreshness.Fresh
    | Some el when el.ValueKind = JsonValueKind.String ->
      match el.GetString() with
      | "StaleCodeEdited" -> ResultFreshness.StaleCodeEdited
      | "StaleWrongGeneration" -> ResultFreshness.StaleWrongGeneration
      | _ -> ResultFreshness.Fresh
    | _ -> ResultFreshness.Fresh

  /// Completion DU (Complete | Partial | Superseded) from a batch payload.
  /// Only Complete marks the Entries as the authoritative discovery set.
  let parseIsComplete (root: JsonElement) : bool =
    match getProp root "Completion" with
    | Some el when el.ValueKind = JsonValueKind.Object ->
      tryStr el "Case" "" = "Complete"
    | _ -> false

  /// discoveryGeneration is supplied by the caller (the subscriber's last
  /// summary generation) — the batch itself carries only the RUN generation.
  let parseResultsBatch (discoveryGeneration: int64) (root: JsonElement) : LiveTestEvent list =
    let freshness = parseFreshness root
    let isComplete = parseIsComplete root
    match getProp root "Entries" with
    | Some entries when entries.ValueKind = JsonValueKind.Array ->
      let entryArray = [| for e in entries.EnumerateArray() -> e |]
      let testInfos = entryArray |> Array.map parseTestInfo
      let testResults = entryArray |> Array.map parseTestResult
      [ LiveTestEvent.TestsDiscovered (testInfos, isComplete, discoveryGeneration)
        LiveTestEvent.TestResultBatch (testResults, freshness) ]
    | _ -> []

  let parseTestSourceLocations (root: JsonElement) : TestSourceLocation array =
    match getProp root "Locations" with
    | Some arr when arr.ValueKind = JsonValueKind.Array ->
      [| for loc in arr.EnumerateArray() do
          let testName = tryStr loc "TestName" ""
          let filePath = tryStr loc "FilePath" ""
          let startLine = tryInt loc "StartLine" 0
          let endLine = tryInt loc "EndLine" 0
          yield { TestName = testName; FilePath = filePath; StartLine = startLine; EndLine = endLine } |]
    | _ -> [||]

  let parseSseEventWithGeneration (discoveryGeneration: int64) (eventType: string) (json: string) : LiveTestEvent list =
    try
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      match eventType with
      | "test_summary" -> [ LiveTestEvent.SummaryUpdated (parseSummary root) ]
      | "test_results_batch" -> parseResultsBatch discoveryGeneration root
      | "test_source_locations" -> [ LiveTestEvent.TestSourceLocationsReceived (parseTestSourceLocations root) ]
      | _ -> []
    with _ -> []

  /// Backwards-compatible entry: batches parsed with generation 0 (merge-only
  /// semantics, never sweep). The subscriber uses the generation-aware variant
  /// once a summary with a generation has arrived.
  let parseSseEvent (eventType: string) (json: string) : LiveTestEvent list =
    parseSseEventWithGeneration 0L eventType json

  let tryFloat (el: JsonElement) (prop: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.Number then Some (v.GetDouble()) else None

  let tryIntList (el: JsonElement) (prop: string) =
    match getProp el prop with
    | Some arr when arr.ValueKind = JsonValueKind.Array ->
      [ for e in arr.EnumerateArray() do
          if e.ValueKind = JsonValueKind.Number then yield e.GetInt32() ]
    | _ -> []

  let tryStrList (el: JsonElement) (prop: string) =
    match getProp el prop with
    | Some arr when arr.ValueKind = JsonValueKind.Array ->
      [ for e in arr.EnumerateArray() do
          if e.ValueKind = JsonValueKind.String then yield e.GetString() ]
    | _ -> []

  let parseDiffLine (el: JsonElement) : DiffLineInfo =
    let kindStr = tryStr el "Kind" "unchanged"
    let kind =
      match kindStr.ToLowerInvariant() with
      | "added" -> DiffLineKind.Added
      | "removed" -> DiffLineKind.Removed
      | "modified" -> DiffLineKind.Modified
      | _ -> DiffLineKind.Unchanged
    { Kind = kind
      Text = tryStr el "Text" ""
      OldText =
        match getProp el "OldText" with
        | Some v when v.ValueKind = JsonValueKind.String && v.GetString() <> "" -> Some (v.GetString())
        | _ -> None }

  let parseEvalDiff (root: JsonElement) : EvalDiffInfo =
    let lines =
      match getProp root "Lines" with
      | Some arr when arr.ValueKind = JsonValueKind.Array ->
        [ for e in arr.EnumerateArray() -> parseDiffLine e ]
      | _ -> []
    let hasDiff = lines |> List.exists (fun l -> l.Kind <> DiffLineKind.Unchanged)
    { Lines = lines
      Summary =
        { Added = tryInt root "Added" 0
          Removed = tryInt root "Removed" 0
          Modified = tryInt root "Modified" 0
          Unchanged = tryInt root "Unchanged" 0 }
      HasDiff = hasDiff }

  let parseCellGraph (root: JsonElement) : CellGraphInfo =
    let nodes =
      match getProp root "Nodes" with
      | Some arr when arr.ValueKind = JsonValueKind.Array ->
        [ for e in arr.EnumerateArray() ->
            { CellNodeInfo.CellId = tryInt e "Id" 0
              Source = tryStr e "Source" ""
              Produces = tryStrList e "Produces"
              Consumes = tryStrList e "Consumes"
              IsStale = false } ]
      | _ -> []
    let edges =
      match getProp root "Edges" with
      | Some arr when arr.ValueKind = JsonValueKind.Array ->
        [ for e in arr.EnumerateArray() ->
            { CellEdgeInfo.From = tryInt e "From" 0; To = tryInt e "To" 0 } ]
      | _ -> []
    { Cells = nodes; Edges = edges }

  let parseBindingScope (root: JsonElement) : BindingScopeInfo =
    let bindings =
      match getProp root "Bindings" with
      | Some arr when arr.ValueKind = JsonValueKind.Array ->
        [ for e in arr.EnumerateArray() ->
            { BindingDetailInfo.Name = tryStr e "Name" ""
              TypeSig = tryStr e "TypeSig" ""
              CellIndex = tryInt e "CellIndex" 0
              IsShadowed = (tryIntList e "ShadowedBy" |> List.isEmpty |> not)
              ShadowedBy = tryIntList e "ShadowedBy"
              ReferencedIn = tryIntList e "ReferencedIn" } ]
      | _ -> []
    { Bindings = bindings
      ActiveCount = tryInt root "ActiveCount" 0
      ShadowedCount = tryInt root "ShadowedCount" 0 }

  let parseTimeline (root: JsonElement) : TimelineStatsInfo =
    { Count = tryInt root "Count" 0
      P50Ms = tryFloat root "P50Ms"
      P95Ms = tryFloat root "P95Ms"
      P99Ms = tryFloat root "P99Ms"
      MeanMs = tryFloat root "MeanMs"
      Sparkline = tryStr root "Sparkline" "" }

  let parseFailureNarratives (json: string) : FailureNarrative array =
    try
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      match root.ValueKind with
      | JsonValueKind.Array ->
        [| for item in root.EnumerateArray() do
            let testId = tryStr item "TestId" ""
            let summary = tryStr item "Summary" ""
            let timeSince = tryStr item "TimeSinceLastPass" ""
            let changes =
              match getProp item "CausalChanges" with
              | Some arr when arr.ValueKind = JsonValueKind.Array ->
                [| for c in arr.EnumerateArray() do
                    yield { Kind = tryStr c "Kind" ""; Name = tryStr c "Name" "" } |]
              | _ -> [||]
            yield { TestId = testId; Summary = summary; TimeSinceLastPass = timeSince; CausalChanges = changes } |]
      | _ -> [||]
    with _ -> [||]

  let parseFeatureSseEvent (eventType: string) (json: string) : FeatureEvent option =
    try
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      match eventType with
      | "eval_diff" -> Some (FeatureEvent.EvalDiff (parseEvalDiff root))
      | "cell_dependencies" -> Some (FeatureEvent.CellGraph (parseCellGraph root))
      | "binding_scope_map" -> Some (FeatureEvent.BindingScope (parseBindingScope root))
      | "eval_timeline" -> Some (FeatureEvent.Timeline (parseTimeline root))
      | "warmup_context_snapshot" -> Some FeatureEvent.WarmupContextSnapshot
      | _ -> None
    with _ -> None
