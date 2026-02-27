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
