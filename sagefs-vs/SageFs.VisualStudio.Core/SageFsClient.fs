namespace SageFs.VisualStudio.Core

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

[<AutoOpen>]
module JsonHelpers =
  let tryStr (el: JsonElement) (prop: string) (fallback: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.String then
      v.GetString()
    else fallback

  let tryInt (el: JsonElement) (prop: string) (fallback: int) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.Number then
      v.GetInt32()
    else fallback

  let tryBool (el: JsonElement) (prop: string) (fallback: bool) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) then v.GetBoolean()
    else fallback

  let tryArr (el: JsonElement) (prop: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.Array then
      Some v
    else None

  let hasProp (el: JsonElement) (prop: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    el.TryGetProperty(prop, &v)

  let getProp (el: JsonElement) (prop: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) then Some v else None

  let tryFloat (el: JsonElement) (prop: string) =
    let mutable v = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(prop, &v) && v.ValueKind = JsonValueKind.Number then
      Some (v.GetDouble())
    else None

[<AutoOpen>]
module FeatureParsers =
  let parseDiffLineInfo (el: JsonElement) : DiffLineInfo =
    let kind =
      match tryStr el "kind" "unchanged" with
      | "added" -> DiffLineKind.Added
      | "removed" -> DiffLineKind.Removed
      | "modified" -> DiffLineKind.Modified
      | _ -> DiffLineKind.Unchanged
    let oldText =
      match getProp el "oldText" with
      | Some v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
      | _ -> None
    { Kind = kind; Text = tryStr el "text" ""; OldText = oldText }

  let parseDiffSummaryInfo (el: JsonElement) : DiffSummaryInfo =
    { Added = tryInt el "added" 0
      Removed = tryInt el "removed" 0
      Modified = tryInt el "modified" 0
      Unchanged = tryInt el "unchanged" 0 }

  let parseEvalDiffInfo (el: JsonElement) : EvalDiffInfo =
    let lines =
      match tryArr el "lines" with
      | Some arr -> [ for l in arr.EnumerateArray() -> parseDiffLineInfo l ]
      | None -> []
    let summary =
      match getProp el "summary" with
      | Some s -> parseDiffSummaryInfo s
      | None -> { Added = 0; Removed = 0; Modified = 0; Unchanged = 0 }
    { Lines = lines; Summary = summary; HasDiff = tryBool el "hasDiff" false }

  let parseCellNodeInfo (el: JsonElement) : CellNodeInfo =
    let produces =
      match tryArr el "produces" with
      | Some arr -> [ for p in arr.EnumerateArray() -> p.GetString() ]
      | None -> []
    let consumes =
      match tryArr el "consumes" with
      | Some arr -> [ for c in arr.EnumerateArray() -> c.GetString() ]
      | None -> []
    { CellId = tryInt el "cellId" 0
      Source = tryStr el "source" ""
      Produces = produces
      Consumes = consumes
      IsStale = tryBool el "isStale" false }

  let parseCellEdgeInfo (el: JsonElement) : CellEdgeInfo =
    { From = tryInt el "from" 0; To = tryInt el "to" 0 }

  let parseCellGraphInfo (el: JsonElement) : CellGraphInfo =
    let cells =
      match tryArr el "cells" with
      | Some arr -> [ for c in arr.EnumerateArray() -> parseCellNodeInfo c ]
      | None -> []
    let edges =
      match tryArr el "edges" with
      | Some arr -> [ for e in arr.EnumerateArray() -> parseCellEdgeInfo e ]
      | None -> []
    { Cells = cells; Edges = edges }

  let parseBindingDetailInfo (el: JsonElement) : BindingDetailInfo =
    let shadowedBy =
      match tryArr el "shadowedBy" with
      | Some arr -> [ for s in arr.EnumerateArray() -> s.GetInt32() ]
      | None -> []
    let referencedIn =
      match tryArr el "referencedIn" with
      | Some arr -> [ for r in arr.EnumerateArray() -> r.GetInt32() ]
      | None -> []
    { Name = tryStr el "name" ""
      TypeSig = tryStr el "typeSig" ""
      CellIndex = tryInt el "cellIndex" 0
      IsShadowed = tryBool el "isShadowed" false
      ShadowedBy = shadowedBy
      ReferencedIn = referencedIn }

  let parseBindingScopeInfo (el: JsonElement) : BindingScopeInfo =
    let bindings =
      match tryArr el "bindings" with
      | Some arr -> [ for b in arr.EnumerateArray() -> parseBindingDetailInfo b ]
      | None -> []
    { Bindings = bindings
      ActiveCount = tryInt el "activeCount" 0
      ShadowedCount = tryInt el "shadowedCount" 0 }

  let parseTimelineStatsInfo (el: JsonElement) : TimelineStatsInfo =
    { Count = tryInt el "count" 0
      P50Ms = tryFloat el "p50Ms"
      P95Ms = tryFloat el "p95Ms"
      P99Ms = tryFloat el "p99Ms"
      MeanMs = tryFloat el "meanMs"
      Sparkline = tryStr el "sparkline" "" }

  let parseNotebookCellInfo (el: JsonElement) : NotebookCellInfo =
    let deps =
      match tryArr el "deps" with
      | Some arr -> [ for d in arr.EnumerateArray() -> d.GetInt32() ]
      | None -> []
    let bindings =
      match tryArr el "bindings" with
      | Some arr -> [ for b in arr.EnumerateArray() -> b.GetString() ]
      | None -> []
    { Index = tryInt el "index" 0
      Label =
        match getProp el "label" with
        | Some v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
        | _ -> None
      Code = tryStr el "code" ""
      Output =
        match getProp el "output" with
        | Some v when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
        | _ -> None
      Deps = deps
      Bindings = bindings }

/// HTTP client for communicating with the SageFs daemon.
/// Registered as a singleton via DI in the extension entry point.
type SageFsClient() =
  let mutable mcpPort = 37749
  let mutable dashboardPort = 37750
  let http = new HttpClient()

  member _.McpPort
    with get () = mcpPort
    and set v = mcpPort <- v

  member _.DashboardPort
    with get () = dashboardPort
    and set v = dashboardPort <- v

  member _.BaseUrl = sprintf "http://localhost:%d" mcpPort
  member _.DashUrl = sprintf "http://localhost:%d" dashboardPort

  /// Check if the daemon is reachable.
  member this.PingAsync(ct: CancellationToken) = task {
    try
      let! resp = http.GetAsync(sprintf "%s/api/sessions" this.BaseUrl, ct)
      return resp.IsSuccessStatusCode
    with _ ->
      return false
  }

  /// Evaluate F# code via the daemon's exec endpoint.
  member this.EvalAsync(code: string, ct: CancellationToken) = task {
    try
      let json = sprintf """{"code":%s}""" (JsonSerializer.Serialize(code))
      use content = new StringContent(json, Encoding.UTF8, "application/json")
      let! resp = http.PostAsync(sprintf "%s/exec" this.BaseUrl, content, ct)
      let! body = resp.Content.ReadAsStringAsync(ct)
      try
        use doc = JsonDocument.Parse(body)
        let root = doc.RootElement
        let output =
          match getProp root "result" with
          | Some r when r.ValueKind = JsonValueKind.String -> r.GetString()
          | Some r -> r.GetRawText()
          | None ->
            match getProp root "message" with
            | Some m when m.ValueKind = JsonValueKind.String -> m.GetString()
            | Some m -> m.GetRawText()
            | None -> body
        let error =
          match getProp root "error" with
          | Some e when e.ValueKind = JsonValueKind.String -> Some (e.GetString())
          | _ -> None
        let success = tryBool root "success" true
        let exitCode = if success then 0 else 1
        let diagnostics = match error with Some e -> [e] | None -> []
        return { Output = output; Diagnostics = diagnostics; ExitCode = exitCode }
      with _ ->
        return { Output = body; Diagnostics = []; ExitCode = 0 }
    with ex ->
      return { Output = ""; Diagnostics = [sprintf "Error: %s" ex.Message]; ExitCode = -1 }
  }

  /// Get list of active sessions as parsed SessionInfo.
  member this.GetSessionsAsync(ct: CancellationToken) = task {
    try
      let! body = http.GetStringAsync(sprintf "%s/api/sessions" this.BaseUrl, ct)
      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement
      let sessions =
        match tryArr root "sessions" with
        | Some arr ->
          [for s in arr.EnumerateArray() do
            let projects =
              match tryArr s "projects" with
              | Some ps -> [for p in ps.EnumerateArray() -> p.GetString()]
              | None -> []
            { Id = tryStr s "id" ""
              ProjectNames = projects
              State = tryStr s "status" "Unknown"
              WorkingDirectory = tryStr s "workingDirectory" ""
              EvalCount = tryInt s "evalCount" 0 }]
        | None -> []
      return sessions
    with _ ->
      return []
  }

  /// Get warmup context for a session.
  member this.GetWarmupContextAsync(sessionId: string, ct: CancellationToken) = task {
    try
      let! body =
        http.GetStringAsync(
          sprintf "%s/api/sessions/%s/warmup-context" this.BaseUrl sessionId, ct)
      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement
      let assemblies =
        match tryArr root "assembliesLoaded" with
        | Some arr ->
          [for a in arr.EnumerateArray() do
            { Name = tryStr a "name" ""
              Path = tryStr a "path" ""
              NamespaceCount = tryInt a "namespaceCount" 0
              ModuleCount = tryInt a "moduleCount" 0 }]
        | None -> []
      let namespaces =
        match tryArr root "namespacesOpened" with
        | Some arr ->
          [for n in arr.EnumerateArray() do
            { Name = tryStr n "name" ""
              IsModule = tryBool n "isModule" false
              Source = tryStr n "source" "" }]
        | None -> []
      let failedOpens =
        match tryArr root "failedOpens" with
        | Some arr ->
          [for f in arr.EnumerateArray() ->
            [for s in f.EnumerateArray() -> s.GetString()]]
        | None -> []
      return Some
        { SourceFilesScanned = tryInt root "sourceFilesScanned" 0
          AssembliesLoaded = assemblies
          NamespacesOpened = namespaces
          FailedOpens = failedOpens
          WarmupDurationMs = tryInt root "warmupDurationMs" 0 }
    with _ ->
      return None
  }

  /// Get hot reload state for a session.
  member this.GetHotReloadStateAsync(sessionId: string, ct: CancellationToken) = task {
    try
      let! body =
        http.GetStringAsync(
          sprintf "%s/api/sessions/%s/hotreload" this.BaseUrl sessionId, ct)
      use doc = JsonDocument.Parse(body)
      let root = doc.RootElement
      let files =
        match tryArr root "files" with
        | Some arr ->
          [for f in arr.EnumerateArray() do
            let path = tryStr f "path" null
            if path <> null then
              { Path = path
                Watched = tryBool f "watched" false }]
        | None -> []
      return Some
        { Files = files
          WatchedCount = tryInt root "watchedCount" 0 }
    with _ ->
      return None
  }

  /// Toggle hot reload for a file.
  member this.ToggleHotReloadAsync(sessionId: string, path: string, ct: CancellationToken) = task {
    try
      use content =
        new StringContent(
          sprintf """{"path":%s}""" (JsonSerializer.Serialize(path)),
          Encoding.UTF8, "application/json")
      let! _ =
        http.PostAsync(
          sprintf "%s/api/sessions/%s/hotreload/toggle" this.BaseUrl sessionId,
          content, ct)
      return ()
    with _ -> return ()
  }

  /// Watch all F# files for hot reload.
  member this.WatchAllAsync(sessionId: string, ct: CancellationToken) = task {
    try
      let! _ =
        http.PostAsync(
          sprintf "%s/api/sessions/%s/hotreload/watch-all" this.BaseUrl sessionId,
          new StringContent("", Encoding.UTF8), ct)
      return ()
    with _ -> return ()
  }

  /// Unwatch all F# files for hot reload.
  member this.UnwatchAllAsync(sessionId: string, ct: CancellationToken) = task {
    try
      let! _ =
        http.PostAsync(
          sprintf "%s/api/sessions/%s/hotreload/unwatch-all" this.BaseUrl sessionId,
          new StringContent("", Encoding.UTF8), ct)
      return ()
    with _ -> return ()
  }

  /// Refresh hot reload (re-evaluate watched files).
  member this.RefreshHotReloadAsync(sessionId: string, ct: CancellationToken) = task {
    try
      let! _ =
        http.PostAsync(
          sprintf "%s/api/sessions/%s/hotreload/refresh" this.BaseUrl sessionId,
          new StringContent("", Encoding.UTF8), ct)
      return ()
    with _ -> return ()
  }

  member this.WatchDirectoryAsync(sessionId: string, directory: string, ct: CancellationToken) = task {
    try
      let json = sprintf """{"directory":%s}""" (JsonSerializer.Serialize(directory))
      let! _ =
        http.PostAsync(
          sprintf "%s/api/sessions/%s/hotreload/watch-directory" this.BaseUrl sessionId,
          new StringContent(json, Encoding.UTF8, "application/json"), ct)
      return ()
    with _ -> return ()
  }

  member this.UnwatchDirectoryAsync(sessionId: string, directory: string, ct: CancellationToken) = task {
    try
      let json = sprintf """{"directory":%s}""" (JsonSerializer.Serialize(directory))
      let! _ =
        http.PostAsync(
          sprintf "%s/api/sessions/%s/hotreload/unwatch-directory" this.BaseUrl sessionId,
          new StringContent(json, Encoding.UTF8, "application/json"), ct)
      return ()
    with _ -> return ()
  }

  /// Start the daemon process.
  member _.StartDaemonAsync(_ct: CancellationToken) = task {
    return ()
  }

  /// Stop the daemon process.
  member this.StopDaemonAsync(ct: CancellationToken) = task {
    try
      let! _resp =
        http.PostAsync(
          sprintf "%s/api/shutdown" this.BaseUrl,
          new StringContent("", Encoding.UTF8), ct)
      return ()
    with _ ->
      return ()
  }

  /// Create a new FSI session.
  member this.CreateSessionAsync(ct: CancellationToken) = task {
    try
      let! _resp =
        http.PostAsync(
          sprintf "%s/api/sessions/create" this.BaseUrl,
          new StringContent("", Encoding.UTF8), ct)
      return ()
    with _ ->
      return ()
  }

  /// Switch active session — returns list of sessions for picker.
  member this.GetSessionChoicesAsync(ct: CancellationToken) = task {
    let! sessions = this.GetSessionsAsync(ct)
    return
      sessions
      |> List.map (fun s ->
        let label =
          s.ProjectNames
          |> List.map (fun p ->
            let name = IO.Path.GetFileNameWithoutExtension(p)
            if String.IsNullOrEmpty(name) then p else name)
          |> String.concat ", "
        let label = if String.IsNullOrEmpty(label) then s.Id else label
        sprintf "%s (%s) [%s]" label s.Id s.State, s.Id)
  }

  /// Switch to a specific session by ID.
  member this.SwitchToSessionAsync(sessionId: string, ct: CancellationToken) = task {
    try
      use content =
        new StringContent(
          sprintf """{"sessionId":"%s"}""" sessionId,
          Encoding.UTF8, "application/json")
      let! _ =
        http.PostAsync(
          sprintf "%s/api/sessions/switch" this.BaseUrl, content, ct)
      return true
    with _ ->
      return false
  }

  /// Stop a session by ID.
  member this.StopSessionAsync(sessionId: string, ct: CancellationToken) = task {
    try
      use content =
        new StringContent(
          sprintf """{"sessionId":"%s"}""" sessionId,
          Encoding.UTF8, "application/json")
      let! _ =
        http.PostAsync(
          sprintf "%s/api/sessions/stop" this.BaseUrl, content, ct)
      return true
    with _ ->
      return false
  }

  /// Reset the active session.
  member this.ResetSessionAsync(hard: bool, ct: CancellationToken) = task {
    let endpoint =
      if hard then "api/sessions/hard-reset"
      else "api/sessions/reset"
    try
      let! _resp =
        http.PostAsync(
          sprintf "%s/%s" this.BaseUrl endpoint,
          new StringContent("", Encoding.UTF8), ct)
      return ()
    with _ ->
      return ()
  }

  // ── Live Testing API ──────────────────────────────────────

  /// Enable live testing.
  member this.EnableLiveTestingAsync(ct: CancellationToken) = task {
    try
      let! resp =
        http.PostAsync(
          sprintf "%s/api/live-testing/enable" this.BaseUrl,
          new StringContent("{}", Encoding.UTF8, "application/json"), ct)
      let! body = resp.Content.ReadAsStringAsync(ct)
      use doc = JsonDocument.Parse(body)
      return tryBool doc.RootElement "enabled" false
    with _ ->
      return false
  }

  /// Disable live testing.
  member this.DisableLiveTestingAsync(ct: CancellationToken) = task {
    try
      let! resp =
        http.PostAsync(
          sprintf "%s/api/live-testing/disable" this.BaseUrl,
          new StringContent("{}", Encoding.UTF8, "application/json"), ct)
      let! body = resp.Content.ReadAsStringAsync(ct)
      use doc = JsonDocument.Parse(body)
      return tryBool doc.RootElement "enabled" false
    with _ ->
      return true
  }

  /// Run all tests (or filtered by pattern).
  member this.RunTestsAsync(pattern: string, ct: CancellationToken) = task {
    try
      let json = sprintf """{"pattern":%s}""" (JsonSerializer.Serialize(pattern))
      let! _ =
        http.PostAsync(
          sprintf "%s/api/live-testing/run" this.BaseUrl,
          new StringContent(json, Encoding.UTF8, "application/json"), ct)
      return ()
    with _ -> return ()
  }

  /// Set run policy for a test category.
  member this.SetRunPolicyAsync(category: string, policy: string, ct: CancellationToken) = task {
    try
      let json = sprintf """{"category":"%s","policy":"%s"}""" category policy
      let! _ =
        http.PostAsync(
          sprintf "%s/api/live-testing/run-policy" this.BaseUrl,
          new StringContent(json, Encoding.UTF8, "application/json"), ct)
      return ()
    with _ -> return ()
  }

  /// Get recent FSI events.
  member this.GetRecentEventsAsync(count: int, ct: CancellationToken) = task {
    try
      let! body =
        http.GetStringAsync(
          sprintf "%s/api/recent-events?count=%d" this.BaseUrl count, ct)
      return body
    with _ ->
      return "[]"
  }

  // ── Completions ──────────────────────────────────────────

  member this.GetCompletionsAsync(code: string, cursorPosition: int, ct: CancellationToken) = task {
    try
      let json =
        sprintf """{"code":%s,"cursor_position":%d,"working_directory":""}"""
          (JsonSerializer.Serialize code) cursorPosition
      let content = new StringContent(json, Encoding.UTF8, "application/json")
      let! resp =
        http.PostAsync(
          sprintf "%s/dashboard/completions" this.DashUrl,
          content, ct)
      let! body = resp.Content.ReadAsStringAsync(ct)
      let doc = JsonDocument.Parse(body)
      let root = doc.RootElement
      let items = tryArr root "completions"
      return
        match items with
        | Some arr ->
          [| for el in arr.EnumerateArray() ->
               {| Label = tryStr el "label" ""
                  Kind = tryStr el "kind" "Variable"
                  InsertText = tryStr el "insertText" "" |} |]
        | None -> [||]
    with _ ->
      return [||]
  }

  /// Get the cell dependency graph from the daemon.
  member this.GetCellGraphAsync(ct: CancellationToken) = task {
    try
      let! body = http.GetStringAsync(sprintf "%s/api/dependency-graph" this.BaseUrl, ct)
      use doc = JsonDocument.Parse(body)
      return Some (parseCellGraphInfo doc.RootElement)
    with _ -> return None
  }

  /// Get binding scope snapshot for a session.
  member this.GetBindingScopeAsync(sessionId: string, ct: CancellationToken) = task {
    try
      let! body =
        http.GetStringAsync(
          sprintf "%s/api/sessions/%s/binding-scope" this.BaseUrl sessionId, ct)
      use doc = JsonDocument.Parse(body)
      return Some (parseBindingScopeInfo doc.RootElement)
    with _ -> return None
  }

  /// Get eval timeline statistics for a session.
  member this.GetTimelineStatsAsync(sessionId: string, ct: CancellationToken) = task {
    try
      let! body =
        http.GetStringAsync(
          sprintf "%s/api/sessions/%s/timeline" this.BaseUrl sessionId, ct)
      use doc = JsonDocument.Parse(body)
      return Some (parseTimelineStatsInfo doc.RootElement)
    with _ -> return None
  }

  /// Export a session as an FSX script.
  member this.ExportSessionAsync(sessionId: string, ct: CancellationToken) = task {
    try
      let! body =
        http.GetStringAsync(
          sprintf "%s/api/sessions/%s/export-fsx" this.BaseUrl sessionId, ct)
      return Some body
    with _ -> return None
  }

  /// Explore a type's members.
  member this.ExploreAsync(typeName: string, ct: CancellationToken) = task {
    try
      let! body =
        http.GetStringAsync(
          sprintf "%s/api/explore?type=%s" this.BaseUrl (Uri.EscapeDataString typeName), ct)
      return Some body
    with _ -> return None
  }

  /// Cancel the current evaluation.
  member this.CancelEvalAsync(ct: CancellationToken) = task {
    try
      let! resp = http.PostAsync(sprintf "%s/api/cancel" this.BaseUrl, null, ct)
      return resp.IsSuccessStatusCode
    with _ -> return false
  }

  /// Get the live testing pipeline trace.
  member this.GetPipelineTraceAsync(ct: CancellationToken) = task {
    try
      let! body =
        http.GetStringAsync(
          sprintf "%s/api/live-testing/pipeline-trace" this.BaseUrl, ct)
      return Some body
    with _ -> return None
  }

  interface IDisposable with
    member _.Dispose() = http.Dispose()
