namespace SageFs

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open SageFs.AppState

/// Pure functions for MCP adapter (formatting responses)
module McpAdapter =

  let isSolutionFile (path: string) =
    path.EndsWith ".sln" || path.EndsWith ".slnx"

  let isProjectFile (path: string) =
    path.EndsWith ".fsproj"

  let formatAvailableProjects (workingDir: string) (projects: string array) (solutions: string array) =
    let projectList =
      if Array.isEmpty projects then "  (none found)"
      else projects |> Array.map (sprintf "  - %s") |> String.concat "\n"
    let solutionList =
      if Array.isEmpty solutions then "  (none found)"
      else solutions |> Array.map (sprintf "  - %s") |> String.concat "\n"
    sprintf "Available Projects/Solutions in %s:\n\n📦 F# Projects (.fsproj):\n%s\n\n📂 Solutions (.sln/.slnx):\n%s\n\n💡 To load a project: SageFs --proj ProjectName.fsproj\n💡 To load a solution: SageFs --sln SolutionName.sln\n💡 To auto-detect: SageFs (in directory with project/solution)" workingDir projectList solutionList

  let formatStartupBanner (version: string) (mcpPort: int option) =
    match mcpPort with
    | Some port -> sprintf "SageFs v%s | MCP on port %d" version port
    | None -> sprintf "SageFs v%s" version

  let formatEvalResult (result: EvalResponse) : string =
    let stdout = 
      match result.Metadata.TryFind "stdout" with
      | Some (s: obj) -> s.ToString()
      | None -> ""
    
    let diagnosticsSection =
      if Array.isEmpty result.Diagnostics then ""
      else
        let items =
          result.Diagnostics
          |> Array.map (fun d ->
            sprintf "  [%s] %s" (Features.Diagnostics.DiagnosticSeverity.label d.Severity) d.Message)
          |> String.concat "\n"
        sprintf "\nDiagnostics:\n%s" items

    let output =
      match result.EvaluationResult with
      | Ok output -> sprintf "Result: %s" output
      | Error ex ->
          let parsed = ErrorMessages.parseError ex.Message
          let suggestion = ErrorMessages.getSuggestion parsed
          sprintf "Error: %s\n%s%s" ex.Message suggestion diagnosticsSection
    
    if String.IsNullOrEmpty(stdout) then
      output
    else
      sprintf "%s\n%s" stdout output

  type StructuredDiagnostic = {
    [<JsonPropertyName("severity")>] Severity: string
    [<JsonPropertyName("message")>] Message: string
    [<JsonPropertyName("startLine")>] StartLine: int
    [<JsonPropertyName("startColumn")>] StartColumn: int
    [<JsonPropertyName("endLine")>] EndLine: int
    [<JsonPropertyName("endColumn")>] EndColumn: int
  }

  type StructuredEvalResult = {
    [<JsonPropertyName("success")>] Success: bool
    [<JsonPropertyName("result")>]
    [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
    Result: string
    [<JsonPropertyName("error")>]
    [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
    Error: string
    [<JsonPropertyName("stdout")>]
    [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
    Stdout: string
    [<JsonPropertyName("diagnostics")>] Diagnostics: StructuredDiagnostic array
    [<JsonPropertyName("code")>] Code: string
  }

  let formatEvalResultJson (response: EvalResponse) : string =
    let stdout =
      match response.Metadata.TryFind "stdout" with
      | Some (s: obj) ->
        let v = s.ToString()
        if String.IsNullOrEmpty v then null else v
      | None -> null

    let diagnostics =
      response.Diagnostics
      |> Array.map (fun d -> {
        Severity = Features.Diagnostics.DiagnosticSeverity.label d.Severity
        Message = d.Message
        StartLine = d.Range.StartLine
        StartColumn = d.Range.StartColumn
        EndLine = d.Range.EndLine
        EndColumn = d.Range.EndColumn
      })

    let result =
      match response.EvaluationResult with
      | Ok output ->
        { Success = true
          Result = output
          Error = null
          Stdout = stdout
          Diagnostics = diagnostics
          Code = response.EvaluatedCode }
      | Error ex ->
        { Success = false
          Result = null
          Error = ex.Message
          Stdout = stdout
          Diagnostics = diagnostics
          Code = response.EvaluatedCode }

    JsonSerializer.Serialize(result)

  let splitStatements (code: string) : string list =
    code.Split([| ";;" |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun s -> s.Trim())
    |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace s))
    |> Array.map (fun s -> s + ";;")
    |> Array.toList

  let echoStatement (writer: TextWriter) (statement: string) =
    let code =
      if statement.EndsWith(";;" ) then statement.[.. statement.Length - 3]
      else statement
    writer.WriteLine()
    writer.WriteLine(">")
    let lines = code.TrimEnd().Split([| '\n' |])
    for line in lines do
      writer.WriteLine(line.TrimEnd('\r'))

  let formatEvents (events: list<DateTime * string * string>) : string =
    events
    |> List.map (fun (timestamp, source, text) -> $"[{timestamp:O}] {source}: {text}")
    |> String.concat "\n"

  let parseScriptFile (filePath: string) : Result<list<string>, exn> =
    try
      let content = File.ReadAllText(filePath)
      let lines = content.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)

      let rec parseStatements (lines: string list) (currentStmt: string list) (statements: string list) =
        match lines with
        | [] ->
          if currentStmt.IsEmpty then
            statements
          else
            (String.concat "\n" (List.rev currentStmt)) :: statements
        | line :: rest ->
          let trimmed = line.TrimStart()

          if trimmed.StartsWith("//") then
            parseStatements rest currentStmt statements
          elif line.Contains(";;") then
            let stmt = String.concat "\n" (List.rev (line :: currentStmt))
            parseStatements rest [] (stmt :: statements)
          else
            parseStatements rest (line :: currentStmt) statements

      let statements = parseStatements (Array.toList lines) [] []
      Ok(List.rev statements)
    with ex ->
      Error ex

  let formatStatus (sessionId: string) (eventCount: int) (state: SessionState) (evalStats: Affordances.EvalStats option) : string =
    let tools = Affordances.availableTools state |> String.concat ", "
    let base' = sprintf "Session: %s | Events: %d | State: %s" sessionId eventCount (SessionState.label state)
    let statsLine =
      match evalStats with
      | Some s when s.EvalCount > 0 ->
        let avg = Affordances.EvalStats.averageDuration s
        sprintf "\nEvals: %d | Avg: %dms | Min: %dms | Max: %dms"
          s.EvalCount (int avg.TotalMilliseconds) (int s.MinDuration.TotalMilliseconds) (int s.MaxDuration.TotalMilliseconds)
      | _ -> ""
    sprintf "%s%s\nAvailable: %s" base' statsLine tools

  let formatCompletions (items: Features.AutoCompletion.CompletionItem list) : string =
    match items with
    | [] -> "No completions found."
    | items ->
      items
      |> List.map (fun item -> sprintf "%s (%s)" item.DisplayText (Features.AutoCompletion.CompletionKind.label item.Kind))
      |> String.concat "\n"

  let formatExplorationResult (qualifiedName: string) (items: Features.AutoCompletion.CompletionItem list) : string =
    match items with
    | [] -> sprintf "No items found in '%s'." qualifiedName
    | items ->
      let grouped =
        items
        |> List.groupBy (fun item -> Features.AutoCompletion.CompletionKind.label item.Kind)
        |> List.sortBy fst
      let sections =
        grouped
        |> List.map (fun (kind, members) ->
          let memberLines =
            members
            |> List.map (fun m -> sprintf "  %s" m.DisplayText)
            |> String.concat "\n"
          sprintf "### %s\n%s" kind memberLines)
        |> String.concat "\n\n"
      sprintf "## %s\n\n%s" qualifiedName sections

  let formatStartupInfo (config: AppState.StartupConfig) : string =
    // Filter out verbose -r: assembly references from args display
    let importantArgs = 
      config.CommandLineArgs 
      |> Array.filter (fun arg -> not (arg.StartsWith("-r:") || arg.StartsWith("--reference:")))
    let argsStr = 
      if importantArgs.Length = 0 then "(none)"
      else String.concat " " importantArgs
    
    let projectsStr = 
      if config.LoadedProjects.IsEmpty then "None"
      else String.concat ", " config.LoadedProjects
    let hotReloadStr = if config.HotReloadEnabled then "Enabled ✓" else "Disabled"
    let aspireStr = if config.AspireDetected then "Yes ✓" else "No"
    let timestamp = config.StartupTimestamp.ToString("yyyy-MM-dd HH:mm:ss")
    
    // Count assembly references for info
    let assemblyCount = 
      config.CommandLineArgs 
      |> Array.filter (fun arg -> arg.StartsWith("-r:") || arg.StartsWith("--reference:"))
      |> Array.length
    
    let profileStr =
      match config.StartupProfileLoaded with
      | Some path -> sprintf "Loaded (%s)" path
      | None -> "None"

    $"""SageFs Startup Information:

Args: {argsStr}
Working Directory: {config.WorkingDirectory}
Loaded Projects: {projectsStr}
Assemblies Loaded: {assemblyCount}
Hot Reload: {hotReloadStr}
MCP Port: {config.McpPort}
Aspire Detected: {aspireStr}
Startup Profile: {profileStr}
Started: {timestamp} UTC"""

  let formatStartupInfoJson (config: AppState.StartupConfig) : string =
    let data = {|
      commandLineArgs = config.CommandLineArgs
      loadedProjects = config.LoadedProjects |> List.toArray
      workingDirectory = config.WorkingDirectory
      mcpPort = config.McpPort
      hotReloadEnabled = config.HotReloadEnabled
      aspireDetected = config.AspireDetected
      startupProfileLoaded = config.StartupProfileLoaded |> Option.toObj
      startupTimestamp = config.StartupTimestamp.ToString("O")
    |}
    let opts = JsonSerializerOptions(WriteIndented = true)
    JsonSerializer.Serialize(data, opts)

  let formatDiagnosticsResult (diagnostics: Features.Diagnostics.Diagnostic array) : string =
    if Array.isEmpty diagnostics then
      "No issues found."
    else
      diagnostics
      |> Array.map (fun d ->
        let sev = Features.Diagnostics.DiagnosticSeverity.label d.Severity
        sprintf "(%d,%d): [%s] %s" d.Range.StartLine d.Range.StartColumn sev d.Message)
      |> String.concat "\n"

  let formatDiagnosticsStoreAsJson (store: Features.DiagnosticsStore.T) : string =
    let entries =
      store
      |> Features.DiagnosticsStore.all
      |> List.map (fun (codeHash, diags) ->
        {| codeHash = codeHash
           diagnostics =
             diags
             |> List.map (fun (d: Features.Diagnostics.Diagnostic) ->
               {| message = d.Message
                  severity = Features.Diagnostics.DiagnosticSeverity.label d.Severity
                  range =
                    {| startLine = d.Range.StartLine
                       startColumn = d.Range.StartColumn
                       endLine = d.Range.EndLine
                       endColumn = d.Range.EndColumn |} |}) |})
      |> List.toArray
    System.Text.Json.JsonSerializer.Serialize(entries)

  let formatEnhancedStatus(sessionId: string) (eventCount: int) (state: SessionState) (evalStats: Affordances.EvalStats option) (startupConfig: AppState.StartupConfig option) : string =
    let projectsStr = 
      match startupConfig with
      | None -> "Unknown"
      | Some config -> 
          if config.LoadedProjects.IsEmpty then "None"
          else String.concat ", " (config.LoadedProjects |> List.map Path.GetFileName)
    
    let startupSection =
      match startupConfig with
      | None -> ""
      | Some config ->
          let hotReload = if config.HotReloadEnabled then "✅" else "❌"
          let aspire = if config.AspireDetected then "✅" else "❌"
          let fileWatch = if config.HotReloadEnabled then "✅ (auto-reload .fs/.fsx via #load)" else "❌"
          sprintf """

📋 Startup Information:
- Working Directory: %s
- MCP Port: %d
- Hot Reload: %s
- Aspire: %s
- File Watcher: %s""" config.WorkingDirectory config.McpPort hotReload aspire fileWatch

    let statsSection =
      match evalStats with
      | Some s when s.EvalCount > 0 ->
        let avg = Affordances.EvalStats.averageDuration s
        sprintf "\nEvals: %d | Avg: %dms | Min: %dms | Max: %dms"
          s.EvalCount (int avg.TotalMilliseconds) (int s.MinDuration.TotalMilliseconds) (int s.MaxDuration.TotalMilliseconds)
      | _ -> ""

    let tools = Affordances.availableTools state |> String.concat ", "
    sprintf """Session: %s | Events: %d | State: %s | Projects: %s
Available: %s%s%s""" sessionId eventCount (SessionState.label state) projectsStr tools statsSection startupSection

  /// Format status from a worker proxy's StatusSnapshot + SessionInfo.
  let formatProxyStatus
    (sessionId: string)
    (eventCount: int)
    (snapshot: WorkerProtocol.WorkerStatusSnapshot)
    (info: WorkerProtocol.SessionInfo)
    (mcpPort: int)
    : string =
    let state = WorkerProtocol.SessionStatus.toSessionState snapshot.Status
    let projectsStr =
      if info.Projects.IsEmpty then "None"
      else String.concat ", " (info.Projects |> List.map Path.GetFileName)
    let statsSection =
      if snapshot.EvalCount > 0 then
        sprintf "\nEvals: %d | Avg: %dms | Min: %dms | Max: %dms"
          snapshot.EvalCount snapshot.AvgDurationMs snapshot.MinDurationMs snapshot.MaxDurationMs
      else ""
    let tools = Affordances.availableTools state |> String.concat ", "
    sprintf """Session: %s | Events: %d | State: %s | Projects: %s
Available: %s%s

📋 Startup Information:
- Working Directory: %s
- MCP Port: %d""" sessionId eventCount (SessionState.label state) projectsStr tools statsSection info.WorkingDirectory mcpPort

/// Event tracking for collaborative MCP mode — backed by Marten event store
module EventTracking =

  open SageFs.Features.Events

  /// Track an input event (code submitted by user/agent/file)
  let trackInput (store: Marten.IDocumentStore) (streamId: string) (source: EventSource) (content: string) =
    let evt = McpInputReceived {| Source = source; Content = content |}
    EventStore.appendEvents store streamId [evt]
    |> fun t -> t.ConfigureAwait(false).GetAwaiter().GetResult()

  /// Track an output event (result sent back to user/agent)
  let trackOutput (store: Marten.IDocumentStore) (streamId: string) (source: EventSource) (content: string) =
    let evt = McpOutputSent {| Source = source; Content = content |}
    EventStore.appendEvents store streamId [evt]
    |> fun t -> t.ConfigureAwait(false).GetAwaiter().GetResult()

  /// Format an event for display
  let private formatEvent (ts: DateTimeOffset, evt: SageFsEvent) =
    let source, content =
      match evt with
      | McpInputReceived e -> e.Source.ToString(), e.Content
      | McpOutputSent e -> e.Source.ToString(), e.Content
      | EvalRequested e -> e.Source.ToString(), e.Code
      | EvalCompleted e -> "eval", e.Result
      | EvalFailed e -> "eval", e.Error
      | DiagnosticsChecked e -> e.Source.ToString(), sprintf "%d diagnostics" e.Diagnostics.Length
      | ScriptLoaded e -> e.Source.ToString(), sprintf "loaded %s (%d statements)" e.FilePath e.StatementCount
      | ScriptLoadFailed e -> "system", sprintf "failed to load %s: %s" e.FilePath e.Error
      | SessionStarted _ -> "system", "session started"
      | SessionWarmUpCompleted _ -> "system", "warm-up completed"
      | SessionReady -> "system", "session ready"
      | SessionFaulted e -> "system", e.Error
      | SessionReset -> "system", "session reset"
      | SessionHardReset e -> "system", sprintf "hard reset (rebuild=%b)" e.Rebuild
      | DiagnosticsCleared -> "system", "diagnostics cleared"
      | DaemonSessionCreated e -> "daemon", sprintf "session %s created" e.SessionId
      | DaemonSessionStopped e -> "daemon", sprintf "session %s stopped" e.SessionId
      | DaemonSessionSwitched e -> "daemon", sprintf "switched to %s" e.ToId
    (ts.UtcDateTime, source, content)

  /// Get recent events from the session stream
  let getRecentEvents (store: Marten.IDocumentStore) (streamId: string) (count: int) =
    EventStore.fetchStream store streamId
    |> fun t -> t.ConfigureAwait(false).GetAwaiter().GetResult()
    |> List.map formatEvent
    |> List.rev
    |> List.truncate count
    |> List.rev

  /// Get all events from the session stream
  let getAllEvents (store: Marten.IDocumentStore) (streamId: string) =
    EventStore.fetchStream store streamId
    |> fun t -> t.ConfigureAwait(false).GetAwaiter().GetResult()
    |> List.map formatEvent

  /// Count events in the session stream
  let getEventCount (store: Marten.IDocumentStore) (streamId: string) =
    EventStore.countEvents store streamId
    |> fun t -> t.ConfigureAwait(false).GetAwaiter().GetResult()

/// MCP tool implementations — all tools route through SessionManager.
/// There is no "local embedded session" — every session is a worker.
module McpTools =

  open System.Threading

  type McpContext = {
    Store: Marten.IDocumentStore
    DiagnosticsChanged: IEvent<Features.DiagnosticsStore.T>
    /// Fires serialized JSON whenever the Elm model changes.
    StateChanged: IEvent<string> option
    SessionOps: SessionManagementOps
    /// Per-connection session tracking, keyed by agent/client name.
    SessionMap: Collections.Concurrent.ConcurrentDictionary<string, string>
    /// MCP port for status display.
    McpPort: int
    /// Elm loop dispatch function (daemon mode).
    Dispatch: (SageFsMsg -> unit) option
    /// Read the current Elm model (daemon mode).
    GetElmModel: (unit -> SageFsModel) option
    /// Read the current render regions (daemon mode).
    GetElmRegions: (unit -> RenderRegion list) option
  }

  /// Get the active session ID for a specific agent/client.
  let private activeSessionId (ctx: McpContext) (agent: string) =
    match ctx.SessionMap.TryGetValue(agent) with
    | true, sid -> sid
    | _ -> ""

  /// Set the active session ID for a specific agent/client.
  let private setActiveSessionId (ctx: McpContext) (agent: string) (sid: string) =
    ctx.SessionMap.[agent] <- sid

  /// Normalize a path for comparison: trim trailing separators, lowercase on Windows.
  let private normalizePath (p: string) =
    let trimmed = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    if Environment.OSVersion.Platform = PlatformID.Win32NT then
      trimmed.ToLowerInvariant()
    else
      trimmed

  /// Find a session whose WorkingDirectory matches the given path.
  /// Pure function — no side effects, no context mutation.
  let resolveSessionByWorkingDir (sessions: WorkerProtocol.SessionInfo list) (workingDir: string) : WorkerProtocol.SessionInfo option =
    let target = normalizePath workingDir
    sessions
    |> List.tryFind (fun s -> normalizePath s.WorkingDirectory = target)

  /// Notify the Elm loop of an event (fire-and-forget, no-op if no dispatch).
  let private notifyElm (ctx: McpContext) (event: SageFsEvent) =
    ctx.Dispatch
    |> Option.iter (fun dispatch ->
      dispatch (SageFsMsg.Event event))

  /// Route a WorkerMessage to a specific session via proxy.
  let private routeToSession
    (ctx: McpContext)
    (sessionId: string)
    (msg: WorkerProtocol.SessionId -> WorkerProtocol.WorkerMessage)
    : Task<Result<WorkerProtocol.WorkerResponse, string>> =
    task {
      let! proxy = ctx.SessionOps.GetProxy sessionId
      match proxy with
      | None ->
        return Result.Error (sprintf "Session '%s' not found" sessionId)
      | Some send ->
        let replyId = Guid.NewGuid().ToString("N").[..7]
        let! response = send (msg replyId) |> Async.StartAsTask
        return Result.Ok response
    }

  /// Route to the active session or the specified session.
  /// When no agent mapping exists, resolves by the caller's working directory.
  let private resolveSessionId (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    task {
      match sessionId with
      | Some sid -> return sid
      | None ->
        let current = activeSessionId ctx agent
        let! candidate =
          if current <> "" then task { return current }
          else
            // Resolve by caller's working directory
            match workingDirectory with
            | Some wd when not (System.String.IsNullOrWhiteSpace wd) ->
              task {
                let! sessions = ctx.SessionOps.GetAllSessions()
                match resolveSessionByWorkingDir sessions wd with
                | Some matched ->
                  setActiveSessionId ctx agent matched.Id
                  return matched.Id
                | None -> return ""
              }
            | _ -> task { return "" }
        if candidate <> "" then
          let! proxy = ctx.SessionOps.GetProxy candidate
          match proxy with
          | Some _ -> return candidate
          | None ->
            setActiveSessionId ctx agent ""
            return failwith "Session is no longer running. Use create_session to start a new one."
        else
          return failwith "No active session. Use create_session to create one first."
    }

  /// Get the session status via proxy, returning the SessionState.
  let private getSessionState (ctx: McpContext) (sessionId: string) : Task<SessionState> =
    task {
      let! routeResult =
        routeToSession ctx sessionId
          (fun replyId -> WorkerProtocol.WorkerMessage.GetStatus replyId)
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.StatusResult(_, snapshot)) ->
          WorkerProtocol.SessionStatus.toSessionState snapshot.Status
        | _ -> SessionState.Faulted
    }

  /// Check tool availability against the active session's state.
  let private requireTool (ctx: McpContext) (sessionId: string) (toolName: string) : Task<Result<unit, string>> =
    task {
      let! state = getSessionState ctx sessionId
      return
        Affordances.checkToolAvailability state toolName
        |> Result.mapError SageFsError.describe
    }

  /// Format a WorkerResponse.EvalResult for display.
  let private formatWorkerEvalResult (response: WorkerProtocol.WorkerResponse) : string =
    match response with
    | WorkerProtocol.WorkerResponse.EvalResult(_, result, diags) ->
      let diagStr =
        if List.isEmpty diags then ""
        else
          diags
          |> List.map (fun d ->
            sprintf "  [%s] %s"
              (Features.Diagnostics.DiagnosticSeverity.label d.Severity) d.Message)
          |> String.concat "\n"
          |> sprintf "\nDiagnostics:\n%s"
      match result with
      | Ok output -> sprintf "Result: %s%s" output diagStr
      | Error err -> sprintf "Error: %s%s" (SageFsError.describe err) diagStr
    | WorkerProtocol.WorkerResponse.WorkerError err ->
      sprintf "Error: %s" (SageFsError.describe err)
    | other ->
      sprintf "Unexpected response: %A" other

  type OutputFormat = Text | Json

  let sendFSharpCode (ctx: McpContext) (agentName: string) (code: string) (format: OutputFormat) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agentName sessionId workingDirectory
      let statements = McpAdapter.splitStatements code
      EventTracking.trackInput ctx.Store sid (Features.Events.McpAgent agentName) code

      let mutable allOutputs = []
      for statement in statements do
        notifyElm ctx (SageFsEvent.EvalStarted (sid, statement))
        let! routeResult =
          routeToSession ctx sid
            (fun replyId -> WorkerProtocol.WorkerMessage.EvalCode(statement, replyId))
        let output =
          match routeResult with
          | Ok response ->
            let formatted = formatWorkerEvalResult response
            match response with
            | WorkerProtocol.WorkerResponse.EvalResult(_, Ok _, diags) ->
              notifyElm ctx (
                SageFsEvent.EvalCompleted (sid, formatted, diags |> List.map WorkerProtocol.WorkerDiagnostic.toDiagnostic))
            | WorkerProtocol.WorkerResponse.EvalResult(_, Error err, _) ->
              notifyElm ctx (
                SageFsEvent.EvalFailed (sid, SageFsError.describe err))
            | _ -> ()
            formatted
          | Error msg ->
            notifyElm ctx (SageFsEvent.EvalFailed (sid, msg))
            sprintf "Error: %s" msg
        allOutputs <- output :: allOutputs

      let finalOutput =
        match format with
        | Json when statements.Length > 1 ->
          let items = List.rev allOutputs |> List.map (fun s -> s) |> String.concat ","
          sprintf "[%s]" items
        | _ when statements.Length > 1 ->
          String.concat "\n\n" (List.rev allOutputs)
        | _ -> allOutputs |> List.tryHead |> Option.defaultValue ""

      EventTracking.trackOutput ctx.Store sid (Features.Events.McpAgent agentName) finalOutput
      return finalOutput
    }

  let getRecentEvents (ctx: McpContext) (agent: string) (count: int) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent None workingDirectory
      let events = EventTracking.getRecentEvents ctx.Store sid count
      return McpAdapter.formatEvents events
    }

  let getStatus (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent sessionId workingDirectory
      let eventCount = EventTracking.getEventCount ctx.Store sid
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.GetStatus replyId)
      match routeResult with
      | Ok (WorkerProtocol.WorkerResponse.StatusResult(_, snapshot)) ->
        let! info = ctx.SessionOps.GetSessionInfo sid
        match info with
        | Some sessionInfo ->
          return McpAdapter.formatProxyStatus sid eventCount snapshot sessionInfo ctx.McpPort
        | None ->
          let state = WorkerProtocol.SessionStatus.toSessionState snapshot.Status
          return McpAdapter.formatEnhancedStatus sid eventCount state None None
      | Ok other ->
        return sprintf "Unexpected response: %A" other
      | Error msg ->
        return sprintf "Error getting status: %s" msg
    }

  let getStartupInfo (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent None workingDirectory
      let! info = ctx.SessionOps.GetSessionInfo sid
      match info with
      | Some sessionInfo ->
        return
          sprintf "📋 Startup Information:\n- Session: %s\n- Working Directory: %s\n- Projects: %s\n- MCP Port: %d\n- Status: %s"
            sid
            sessionInfo.WorkingDirectory
            (if sessionInfo.Projects.IsEmpty then "None"
             else String.concat ", " (sessionInfo.Projects |> List.map Path.GetFileName))
            ctx.McpPort
            (WorkerProtocol.SessionStatus.label sessionInfo.Status)
      | None ->
        return "SageFs startup information not available yet — session is still initializing"
    }

  let getStartupInfoJson (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent None workingDirectory
      let! info = ctx.SessionOps.GetSessionInfo sid
      match info with
      | Some sessionInfo ->
        return
          System.Text.Json.JsonSerializer.Serialize(
            {| sessionId = sid
               workingDirectory = sessionInfo.WorkingDirectory
               projects = sessionInfo.Projects
               mcpPort = ctx.McpPort
               status = WorkerProtocol.SessionStatus.label sessionInfo.Status |})
      | None ->
        return """{"status": "initializing", "message": "Session is still warming up"}"""
    }

  let getAvailableProjects (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent None workingDirectory
      let! info = ctx.SessionOps.GetSessionInfo sid
      let workingDir =
        match info with
        | Some sessionInfo -> sessionInfo.WorkingDirectory
        | None -> Environment.CurrentDirectory

      let projects =
        try
          Directory.EnumerateFiles(workingDir, "*.fsproj", SearchOption.AllDirectories)
          |> Seq.filter McpAdapter.isProjectFile
          |> Seq.map (fun p -> Path.GetRelativePath(workingDir, p))
          |> Seq.toArray
        with _ -> [||]

      let solutions =
        try
          Directory.EnumerateFiles workingDir
          |> Seq.filter McpAdapter.isSolutionFile
          |> Seq.map Path.GetFileName
          |> Seq.toArray
        with _ -> [||]

      return McpAdapter.formatAvailableProjects workingDir projects solutions
    }

  let loadFSharpScript (ctx: McpContext) (agentName: string) (filePath: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agentName sessionId workingDirectory
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.LoadScript(filePath, replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.ScriptLoaded(_, Ok msg)) -> msg
        | Ok (WorkerProtocol.WorkerResponse.ScriptLoaded(_, Error err)) ->
          sprintf "Error: %s" (SageFsError.describe err)
        | Ok (WorkerProtocol.WorkerResponse.WorkerError err) ->
          sprintf "Error: %s" (SageFsError.describe err)
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" msg
    }

  let resetSession (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent sessionId workingDirectory
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.ResetSession replyId)
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Ok ())) ->
          notifyElm ctx (
            SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Running))
          "Session reset successfully. All previous definitions have been cleared."
        | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Error err)) ->
          sprintf "Error: %s" (SageFsError.describe err)
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" msg
    }

  let checkFSharpCode (ctx: McpContext) (agent: string) (code: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent sessionId workingDirectory
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.CheckCode(code, replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.CheckResult(_, diags)) ->
          if List.isEmpty diags then "No errors found."
          else
            diags
            |> List.map (fun d ->
              sprintf "[%s] %s"
                (Features.Diagnostics.DiagnosticSeverity.label d.Severity) d.Message)
            |> String.concat "\n"
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" msg
    }

  let hardResetSession (ctx: McpContext) (agent: string) (rebuild: bool) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent sessionId workingDirectory
      notifyElm ctx (
        SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Restarting))
      if rebuild then
        // Respawn the worker process to get a fresh CLR assembly cache.
        // In-process FSI recreation reuses Default ALC cached assemblies,
        // so rebuilt DLLs are invisible without a process restart.
        let! result = ctx.SessionOps.RestartSession sid true
        return
          match result with
          | Ok msg -> msg
          | Error err -> sprintf "Error: %s" (SageFsError.describe err)
      else
        // No rebuild — in-process soft reset is fine (no assembly reloading)
        let! routeResult =
          routeToSession ctx sid
            (fun replyId -> WorkerProtocol.WorkerMessage.HardResetSession(false, replyId))
        return
          match routeResult with
          | Ok (WorkerProtocol.WorkerResponse.HardResetResult(_, Ok msg)) -> msg
          | Ok (WorkerProtocol.WorkerResponse.HardResetResult(_, Error err)) ->
            sprintf "Error: %s" (SageFsError.describe err)
          | Ok other -> sprintf "Unexpected response: %A" other
          | Error msg -> sprintf "Error: %s" msg
    }

  let cancelEval (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent None workingDirectory
      let! routeResult =
        routeToSession ctx sid
          (fun _ -> WorkerProtocol.WorkerMessage.CancelEval)
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.EvalCancelled true) ->
          notifyElm ctx (SageFsEvent.EvalCancelled sid)
          "Evaluation cancelled."
        | Ok (WorkerProtocol.WorkerResponse.EvalCancelled false) ->
          "No evaluation in progress."
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" msg
    }

  let getCompletions (ctx: McpContext) (agent: string) (code: string) (cursorPosition: int) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent None workingDirectory
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.GetCompletions(code, cursorPosition, replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.CompletionResult(_, completions)) ->
          if List.isEmpty completions then "No completions available."
          else String.concat "\n" completions
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" msg
    }

  let private exploreQualifiedName (ctx: McpContext) (agent: string) (qualifiedName: string) (workingDirectory: string option) : Task<string> =
    task {
      let! sid = resolveSessionId ctx agent None workingDirectory
      let code = sprintf "%s." qualifiedName
      let cursor = code.Length
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.GetCompletions(code, cursor, replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.CompletionResult(_, completions)) ->
          if List.isEmpty completions then
            sprintf "No members found for '%s'" qualifiedName
          else
            let header = sprintf "Members of %s:" qualifiedName
            let items = completions |> List.map (sprintf "  %s") |> String.concat "\n"
            sprintf "%s\n%s" header items
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" msg
    }

  let exploreNamespace (ctx: McpContext) (agent: string) (namespaceName: string) (workingDirectory: string option) : Task<string> =
    exploreQualifiedName ctx agent namespaceName workingDirectory

  let exploreType (ctx: McpContext) (agent: string) (typeName: string) (workingDirectory: string option) : Task<string> =
    exploreQualifiedName ctx agent typeName workingDirectory

  // ── Session Management Operations ──────────────────────────────

  /// Create a new session and bind it to the requesting agent.
  let createSession (ctx: McpContext) (agent: string) (projects: string list) (workingDir: string) : Task<string> =
    task {
      let! result = ctx.SessionOps.CreateSession projects workingDir
      // Refresh Elm model so dashboard SSE pushes updated session list
      ctx.Dispatch |> Option.iter (fun d -> d (SageFsMsg.Editor EditorAction.ListSessions))
      match result with
      | Result.Ok sid ->
        setActiveSessionId ctx agent sid
        return sid
      | Result.Error err -> return SageFsError.describe err
    }

  /// List all active sessions with occupancy information.
  let listSessions (ctx: McpContext) : Task<string> =
    task {
      let! sessions = ctx.SessionOps.GetAllSessions()
      let occupancyMap =
        sessions
        |> List.map (fun s ->
          s.Id, SessionOperations.SessionOccupancy.forSession ctx.SessionMap s.Id)
        |> Map.ofList
      return SessionOperations.formatSessionList System.DateTime.UtcNow (Some occupancyMap) sessions
    }

  /// Stop a session by ID.
  let stopSession (ctx: McpContext) (sessionId: string) : Task<string> =
    task {
      let! result = ctx.SessionOps.StopSession sessionId
      ctx.Dispatch |> Option.iter (fun d -> d (SageFsMsg.Editor EditorAction.ListSessions))
      match result with
      | Result.Ok msg -> return msg
      | Result.Error err -> return SageFsError.describe err
    }

  /// Switch the active session for a specific agent. Validates the target exists.
  let switchSession (ctx: McpContext) (agent: string) (sessionId: string) : Task<string> =
    task {
      let! info = ctx.SessionOps.GetSessionInfo sessionId
      match info with
      | Some _ ->
        let prev = activeSessionId ctx agent
        setActiveSessionId ctx agent sessionId
        // Persist switch to daemon stream
        do! EventStore.appendEvents ctx.Store "daemon-sessions" [
          Features.Events.SageFsEvent.DaemonSessionSwitched
            {| FromId = Some prev; ToId = sessionId; SwitchedAt = DateTimeOffset.UtcNow |}
        ]
        return sprintf "Switched to session '%s'" sessionId
      | None ->
        return sprintf "Error: Session '%s' not found" sessionId
    }

  // ── Elm State Query ──────────────────────────────────────────────

  let private formatRegionFlags (flags: RegionFlags) =
    [ if flags.HasFlag RegionFlags.Focusable then "focusable"
      if flags.HasFlag RegionFlags.Scrollable then "scrollable"
      if flags.HasFlag RegionFlags.LiveUpdate then "live"
      if flags.HasFlag RegionFlags.Clickable then "clickable"
      if flags.HasFlag RegionFlags.Collapsible then "collapsible" ]
    |> String.concat ", "

  /// Get current Elm render regions (daemon mode only).
  let getElmState (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetElmRegions with
      | None ->
        return "Elm state not available — Elm loop not started."
      | Some getRegions ->
        let regions = getRegions ()
        if regions.IsEmpty then
          return "No render regions available."
        else
          return
            regions
            |> List.map (fun r ->
              let header =
                sprintf "── %s [%s] ──" r.Id (formatRegionFlags r.Flags)
              if String.IsNullOrWhiteSpace r.Content then header
              else sprintf "%s\n%s" header r.Content)
            |> String.concat "\n\n"
    }
