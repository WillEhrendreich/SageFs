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

  let parseSummary (root: JsonElement) =
    { Total = tryInt root "Total" 0
      Passed = tryInt root "Passed" 0
      Failed = tryInt root "Failed" 0
      Running = tryInt root "Running" 0
      Stale = tryInt root "Stale" 0
      Disabled = tryInt root "Disabled" 0 }

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

  let parseResultsBatch (root: JsonElement) : LiveTestEvent list =
    let freshness = parseFreshness root
    match getProp root "Entries" with
    | Some entries when entries.ValueKind = JsonValueKind.Array ->
      let entryArray = [| for e in entries.EnumerateArray() -> e |]
      let testInfos = entryArray |> Array.map parseTestInfo
      let testResults = entryArray |> Array.map parseTestResult
      [ LiveTestEvent.TestsDiscovered testInfos
        LiveTestEvent.TestResultBatch (testResults, freshness) ]
    | _ -> []

  let parseSseEvent (eventType: string) (json: string) : LiveTestEvent list =
    try
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      match eventType with
      | "test_summary" -> [ LiveTestEvent.SummaryUpdated (parseSummary root) ]
      | "test_results_batch" -> parseResultsBatch root
      | _ -> []
    with _ -> []

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

  let parseFeatureSseEvent (eventType: string) (json: string) : FeatureEvent option =
    try
      use doc = JsonDocument.Parse(json)
      let root = doc.RootElement
      match eventType with
      | "eval_diff" -> Some (FeatureEvent.EvalDiff (parseEvalDiff root))
      | "cell_dependencies" -> Some (FeatureEvent.CellGraph (parseCellGraph root))
      | "binding_scope_map" -> Some (FeatureEvent.BindingScope (parseBindingScope root))
      | "eval_timeline" -> Some (FeatureEvent.Timeline (parseTimeline root))
      | _ -> None
    with _ -> None
