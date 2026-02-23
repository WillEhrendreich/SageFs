namespace SageFs.VisualStudio.Core

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

[<AutoOpen>]
module private JsonHelpers =
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

  member private _.BaseUrl = sprintf "http://localhost:%d" mcpPort
  member private _.DashUrl = sprintf "http://localhost:%d" dashboardPort

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

  interface IDisposable with
    member _.Dispose() = http.Dispose()
