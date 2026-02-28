namespace SageFs

#nowarn "3511"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open SageFs.AppState

/// Pure functions for MCP adapter (formatting responses)
module McpAdapter =

  let isSolutionFile (path: string) =
    path.EndsWith(".sln", System.StringComparison.Ordinal) || path.EndsWith(".slnx", System.StringComparison.Ordinal)

  let isProjectFile (path: string) =
    path.EndsWith(".fsproj", System.StringComparison.Ordinal)

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

  /// Full warmup detail for LLM startup info — shows loaded assemblies,
  /// opened namespaces/modules, failures. Included in get_startup_info only.
  let formatWarmupDetailForLlm (ctx: SessionContext) =
    let w = ctx.Warmup
    let opened = WarmupContext.totalOpenedCount w
    let failed = WarmupContext.totalFailedCount w
    let asmCount = w.AssembliesLoaded.Length
    let lines = Collections.Generic.List<string>()

    lines.Add(
      sprintf "🔧 Warmup: %d assemblies, %d/%d namespaces opened, %dms"
        asmCount opened (opened + failed) w.WarmupDurationMs)

    if asmCount > 0 then
      lines.Add(sprintf "  Assemblies (%d):" asmCount)
      for a in w.AssembliesLoaded do
        lines.Add(sprintf "    📦 %s (%d ns, %d modules)" a.Name a.NamespaceCount a.ModuleCount)

    if w.NamespacesOpened.Length > 0 then
      lines.Add(sprintf "  Opened (%d):" w.NamespacesOpened.Length)
      for b in w.NamespacesOpened do
        let kind = if b.IsModule then "module" else "namespace"
        lines.Add(sprintf "    open %s // %s" b.Name kind)

    if w.FailedOpens.Length > 0 then
      lines.Add(sprintf "  ⚠ Failed opens (%d):" w.FailedOpens.Length)
      for (name, err) in w.FailedOpens do
        lines.Add(sprintf "    ✖ %s — %s" name err)

    let files = ctx.FileStatuses
    if files.Length > 0 then
      let loaded = files |> List.filter (fun f -> f.Readiness = Loaded) |> List.length
      lines.Add(sprintf "  Files (%d/%d loaded):" loaded files.Length)
      for f in files do
        lines.Add(sprintf "    %s %s" (FileReadiness.icon f.Readiness) f.Path)

    lines |> Seq.toList |> String.concat "\n"

  let splitStatements (code: string) : string list =
    let mutable i = 0
    let len = code.Length
    let statements = ResizeArray<string>()
    let current = Text.StringBuilder()
    let inline peek offset = if i + offset < len then code.[i + offset] else '\000'
    while i < len do
      let c = code.[i]
      match c with
      | '"' when peek 1 = '"' && peek 2 = '"' ->
        current.Append("\"\"\"") |> ignore
        i <- i + 3
        let mutable inTriple = true
        while inTriple && i < len do
          if code.[i] = '"' && peek 1 = '"' && peek 2 = '"' then
            current.Append("\"\"\"") |> ignore
            i <- i + 3
            inTriple <- false
          else
            current.Append(code.[i]) |> ignore
            i <- i + 1
      | '@' when peek 1 = '"' ->
        current.Append("@\"") |> ignore
        i <- i + 2
        let mutable inVerbatim = true
        while inVerbatim && i < len do
          if code.[i] = '"' && peek 1 = '"' then
            current.Append("\"\"") |> ignore
            i <- i + 2
          elif code.[i] = '"' then
            current.Append('"') |> ignore
            i <- i + 1
            inVerbatim <- false
          else
            current.Append(code.[i]) |> ignore
            i <- i + 1
      | '"' ->
        current.Append('"') |> ignore
        i <- i + 1
        let mutable inStr = true
        while inStr && i < len do
          if code.[i] = '\\' then
            current.Append(code.[i]) |> ignore
            i <- i + 1
            if i < len then
              current.Append(code.[i]) |> ignore
              i <- i + 1
          elif code.[i] = '"' then
            current.Append('"') |> ignore
            i <- i + 1
            inStr <- false
          else
            current.Append(code.[i]) |> ignore
            i <- i + 1
      | '/' when peek 1 = '/' ->
        while i < len && code.[i] <> '\n' do
          current.Append(code.[i]) |> ignore
          i <- i + 1
      | '(' when peek 1 = '*' ->
        current.Append("(*") |> ignore
        i <- i + 2
        let mutable depth = 1
        while depth > 0 && i < len do
          if code.[i] = '(' && peek 1 = '*' then
            current.Append("(*") |> ignore
            i <- i + 2
            depth <- depth + 1
          elif code.[i] = '*' && peek 1 = ')' then
            current.Append("*)") |> ignore
            i <- i + 2
            depth <- depth - 1
          else
            current.Append(code.[i]) |> ignore
            i <- i + 1
      | ';' when peek 1 = ';' ->
        let stmt = current.ToString().Trim()
        if stmt.Length > 0 then
          statements.Add(stmt + ";;")
        current.Clear() |> ignore
        i <- i + 2
      | _ ->
        current.Append(c) |> ignore
        i <- i + 1
    let trailing = current.ToString().Trim()
    if trailing.Length > 0 then
      statements.Add(trailing)
    statements |> Seq.toList

  let echoStatement (writer: TextWriter) (statement: string) =
    let code =
      if statement.EndsWith(";;", System.StringComparison.Ordinal) then statement.[.. statement.Length - 3]
      else statement
    writer.WriteLine()
    writer.WriteLine(">")
    let lines = code.TrimEnd().Split([| '\n' |])
    for line in lines do
      writer.WriteLine(line.TrimEnd('\r'))

  let formatEvents (events: list<DateTime * string * string>) : string =
    events
    |> List.map (fun (timestamp, source, text) -> $"[{timestamp:O}] %s{source}: %s{text}")
    |> String.concat "\n"

  let escapeJson (s: string) =
    let sb = Text.StringBuilder(s.Length)
    for c in s do
      match c with
      | '\\' -> sb.Append("\\\\") |> ignore
      | '"' -> sb.Append("\\\"") |> ignore
      | '\n' -> sb.Append("\\n") |> ignore
      | '\r' -> sb.Append("\\r") |> ignore
      | '\t' -> sb.Append("\\t") |> ignore
      | '\b' -> sb.Append("\\b") |> ignore
      | '\u000C' -> sb.Append("\\f") |> ignore
      | c when c < '\u0020' -> sb.Append(sprintf "\\u%04X" (int c)) |> ignore
      | c -> sb.Append(c) |> ignore
    sb.ToString()

  let formatEventsJson (events: list<DateTime * string * string>) : string =
    let items =
      events
      |> List.map (fun (timestamp, source, text) ->
        sprintf """{"timestamp":"%s","source":"%s","text":"%s"}"""
          (timestamp.ToString("O")) (escapeJson source) (escapeJson text))
      |> String.concat ","
    sprintf """{"events":[%s],"count":%d}""" items (List.length events)

  let parseScriptFile (filePath: string) : Result<list<string>, exn> =
    try
      let content = File.ReadAllText(filePath)
      Ok(splitStatements content)
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

  let formatStatusJson (sessionId: string) (eventCount: int) (state: SessionState) (evalStats: Affordances.EvalStats option) : string =
    let tools = Affordances.availableTools state
    let toolsJson = tools |> List.map (sprintf "\"%s\"") |> String.concat ","
    let statsJson =
      match evalStats with
      | Some s when s.EvalCount > 0 ->
        let avg = Affordances.EvalStats.averageDuration s
        sprintf ""","evalStats":{"count":%d,"avgMs":%d,"minMs":%d,"maxMs":%d}"""
          s.EvalCount (int avg.TotalMilliseconds) (int s.MinDuration.TotalMilliseconds) (int s.MaxDuration.TotalMilliseconds)
      | _ -> ""
    sprintf """{"sessionId":"%s","eventCount":%d,"state":"%s","tools":[%s]%s}"""
      (escapeJson sessionId) eventCount (SessionState.label state) toolsJson statsJson

  let formatCompletions (items: Features.AutoCompletion.CompletionItem list) : string =
    match items with
    | [] -> "No completions found."
    | items ->
      items
      |> List.map (fun item -> sprintf "%s (%s)" item.DisplayText (Features.AutoCompletion.CompletionKind.label item.Kind))
      |> String.concat "\n"

  let formatCompletionsJson (items: Features.AutoCompletion.CompletionItem list) : string =
    let jsonItems =
      items
      |> List.map (fun item ->
        sprintf """{"label":"%s","kind":"%s","insertText":"%s"}"""
          (escapeJson item.DisplayText) (Features.AutoCompletion.CompletionKind.label item.Kind) (escapeJson item.ReplacementText))
      |> String.concat ","
    sprintf """{"completions":[%s],"count":%d}""" jsonItems (List.length items)

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

  let formatExplorationResultJson (qualifiedName: string) (items: Features.AutoCompletion.CompletionItem list) : string =
    match items with
    | [] -> sprintf """{"name":"%s","groups":[],"totalCount":0}""" (escapeJson qualifiedName)
    | items ->
      let grouped =
        items
        |> List.groupBy (fun item -> Features.AutoCompletion.CompletionKind.label item.Kind)
        |> List.sortBy fst
      let groupsJson =
        grouped
        |> List.map (fun (kind, members) ->
          let membersJson =
            members
            |> List.map (fun m -> sprintf "\"%s\"" (escapeJson m.DisplayText))
            |> String.concat ","
          sprintf """{"kind":"%s","members":[%s],"count":%d}""" kind membersJson (List.length members))
        |> String.concat ","
      sprintf """{"name":"%s","groups":[%s],"totalCount":%d}""" (escapeJson qualifiedName) groupsJson (List.length items)

  let formatStartupInfo (config: AppState.StartupConfig) : string =
    // Filter out verbose -r: assembly references from args display
    let importantArgs = 
      config.CommandLineArgs 
      |> Array.filter (fun arg -> not (arg.StartsWith("-r:", System.StringComparison.Ordinal) || arg.StartsWith("--reference:", System.StringComparison.Ordinal)))
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
      |> Array.filter (fun arg -> arg.StartsWith("-r:", System.StringComparison.Ordinal) || arg.StartsWith("--reference:", System.StringComparison.Ordinal))
      |> Array.length
    
    let profileStr =
      match config.StartupProfileLoaded with
      | Some path -> sprintf "Loaded (%s)" path
      | None -> "None"

    $"""SageFs Startup Information:

Args: %s{argsStr}
Working Directory: %s{config.WorkingDirectory}
Loaded Projects: %s{projectsStr}
Assemblies Loaded: %d{assemblyCount}
Hot Reload: %s{hotReloadStr}
MCP Port: %d{config.McpPort}
Aspire Detected: %s{aspireStr}
Startup Profile: %s{profileStr}
Started: %s{timestamp} UTC"""

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

  let formatDiagnosticsResultJson (diagnostics: Features.Diagnostics.Diagnostic array) : string =
    let items =
      diagnostics
      |> Array.map (fun d ->
        sprintf """{"severity":"%s","message":"%s","startLine":%d,"startColumn":%d,"endLine":%d,"endColumn":%d}"""
          (Features.Diagnostics.DiagnosticSeverity.label d.Severity) (escapeJson d.Message)
          d.Range.StartLine d.Range.StartColumn d.Range.EndLine d.Range.EndColumn)
      |> String.concat ","
    sprintf """{"diagnostics":[%s],"count":%d}""" items (Array.length diagnostics)

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

  let formatEnhancedStatusJson
    (sessionId: string)
    (eventCount: int)
    (state: SessionState)
    (evalStats: Affordances.EvalStats option)
    (startupConfig: AppState.StartupConfig option)
    : string =
    let tools = Affordances.availableTools state
    let toolsJson = tools |> List.map (sprintf "\"%s\"") |> String.concat ","
    let statsJson =
      match evalStats with
      | Some s when s.EvalCount > 0 ->
        let avg = Affordances.EvalStats.averageDuration s
        sprintf ""","evalStats":{"count":%d,"avgMs":%d,"minMs":%d,"maxMs":%d}"""
          s.EvalCount (int avg.TotalMilliseconds) (int s.MinDuration.TotalMilliseconds) (int s.MaxDuration.TotalMilliseconds)
      | _ -> ""
    let projectsJson =
      match startupConfig with
      | None -> "[]"
      | Some config ->
        config.LoadedProjects
        |> List.map (fun p -> sprintf "\"%s\"" (escapeJson (Path.GetFileName p)))
        |> String.concat ","
        |> sprintf "[%s]"
    let startupJson =
      match startupConfig with
      | None -> ""
      | Some config ->
        sprintf ""","startup":{"workingDirectory":"%s","mcpPort":%d,"hotReloadEnabled":%b,"aspireDetected":%b}"""
          (escapeJson config.WorkingDirectory) config.McpPort config.HotReloadEnabled config.AspireDetected
    sprintf """{"sessionId":"%s","eventCount":%d,"state":"%s","projects":%s,"tools":[%s]%s%s}"""
      (escapeJson sessionId) eventCount (SessionState.label state) projectsJson toolsJson statsJson startupJson

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

  let formatWorkerEvalResultJson (response: WorkerProtocol.WorkerResponse) : string =
    match response with
    | WorkerProtocol.WorkerResponse.EvalResult(_, result, diags, _) ->
      let diagsJson =
        diags
        |> List.map (fun (d: WorkerProtocol.WorkerDiagnostic) ->
          sprintf """{"severity":"%s","message":"%s","startLine":%d,"startColumn":%d,"endLine":%d,"endColumn":%d}"""
            (Features.Diagnostics.DiagnosticSeverity.label d.Severity)
            (escapeJson d.Message) d.StartLine d.StartColumn d.EndLine d.EndColumn)
        |> String.concat ","
      match result with
      | Ok output ->
        sprintf """{"success":true,"result":"%s","diagnostics":[%s]}"""
          (escapeJson output) diagsJson
      | Error err ->
        sprintf """{"success":false,"error":"%s","diagnostics":[%s]}"""
          (escapeJson (SageFsError.describe err)) diagsJson
    | WorkerProtocol.WorkerResponse.WorkerError err ->
      sprintf """{"success":false,"error":"%s","diagnostics":[]}"""
        (escapeJson (SageFsError.describe err))
    | other ->
      sprintf """{"success":false,"error":"%s","diagnostics":[]}"""
        (escapeJson (sprintf "Unexpected response: %A" other))

/// Event tracking for collaborative MCP mode — backed by Marten event store
module EventTracking =

  open SageFs.Features.Events

  /// Track an input event (code submitted by user/agent/file)
  let trackInput (p: EventStore.EventPersistence) (streamId: string) (source: EventSource) (content: string) =
    let evt = McpInputReceived {| Source = source; Content = content |}
    task {
      let! _ = p.AppendEvents streamId [evt]
      return ()
    }

  /// Track an output event (result sent back to user/agent)
  let trackOutput (p: EventStore.EventPersistence) (streamId: string) (source: EventSource) (content: string) =
    let evt = McpOutputSent {| Source = source; Content = content |}
    task {
      let! _ = p.AppendEvents streamId [evt]
      return ()
    }

  /// Format an event for display
  let formatEvent (ts: DateTimeOffset, evt: SageFsEvent) =
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
      | SessionWarmUpProgress e -> "system", sprintf "warm-up [%d/%d] %s" e.Step e.Total e.Message
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
  let getRecentEvents (p: EventStore.EventPersistence) (streamId: string) (count: int) =
    task {
      let! events = p.FetchStream streamId
      return
        events
        |> List.map formatEvent
        |> List.rev
        |> List.truncate count
        |> List.rev
    }

  /// Get all events from the session stream
  let getAllEvents (p: EventStore.EventPersistence) (streamId: string) =
    task {
      let! events = p.FetchStream streamId
      return events |> List.map formatEvent
    }

  /// Count events in the session stream
  let getEventCount (p: EventStore.EventPersistence) (streamId: string) =
    p.CountEvents streamId

/// MCP tool implementations — all tools route through SessionManager.
/// There is no "local embedded session" — every session is a worker.
module McpTools =

  open System.Threading

  type McpContext = {
    Persistence: EventStore.EventPersistence
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
    /// Fetch warmup context for a session (daemon mode).
    GetWarmupContext: (string -> Threading.Tasks.Task<WarmupContext option>) option
  }

  /// Get the active session ID for a specific agent/client.
  let activeSessionId (ctx: McpContext) (agent: string) =
    match ctx.SessionMap.TryGetValue(agent) with
    | true, sid -> sid
    | _ -> ""

  /// Set the active session ID for a specific agent/client.
  let setActiveSessionId (ctx: McpContext) (agent: string) (sid: string) =
    ctx.SessionMap.[agent] <- sid

  /// Normalize a path for comparison: trim trailing separators, lowercase on Windows.
  let normalizePath (p: string) =
    let trimmed = p.TrimEnd('/', '\\')
    if Environment.OSVersion.Platform = PlatformID.Win32NT then
      trimmed.Replace('/', '\\').ToLowerInvariant()
    else
      trimmed

  /// Find a session whose WorkingDirectory matches the given path.
  /// Pure function — no side effects, no context mutation.
  let resolveSessionByWorkingDir (sessions: WorkerProtocol.SessionInfo list) (workingDir: string) : WorkerProtocol.SessionInfo option =
    let target = normalizePath workingDir
    sessions
    |> List.tryFind (fun s -> normalizePath s.WorkingDirectory = target)

  /// Notify the Elm loop of an event (fire-and-forget, no-op if no dispatch).
  let notifyElm (ctx: McpContext) (event: SageFsEvent) =
    ctx.Dispatch
    |> Option.iter (fun dispatch ->
      dispatch (SageFsMsg.Event event))

  /// Route a WorkerMessage to a specific session via proxy.
  let routeToSession
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
  /// Returns Error with a user-friendly message when no session is available.
  let resolveSessionId (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<Result<string, string>> =
    task {
      match sessionId with
      | Some sid -> return Ok sid
      | None ->
        // Working directory takes priority over cached session
        let! candidate =
          match workingDirectory with
          | Some wd when not (System.String.IsNullOrWhiteSpace wd) ->
            task {
              let! sessions = ctx.SessionOps.GetAllSessions()
              match resolveSessionByWorkingDir sessions wd with
              | Some matched ->
                setActiveSessionId ctx agent matched.Id
                return matched.Id
              | None ->
                // No match for this directory — fall back to cached
                return activeSessionId ctx agent
            }
          | _ ->
            task { return activeSessionId ctx agent }
        if candidate <> "" then
          let! proxy = ctx.SessionOps.GetProxy candidate
          match proxy with
          | Some _ -> return Ok candidate
          | None ->
            setActiveSessionId ctx agent ""
            return Error "Session is no longer running. Use create_session to start a new one."
        else
          return Error "No active session. Use create_session to create one first."
    }

  /// Helper: run a function with the resolved session ID, or return the error message.
  let withSession (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) (f: string -> Task<string>) : Task<string> =
    task {
      let! resolved = resolveSessionId ctx agent sessionId workingDirectory
      match resolved with
      | Ok sid -> return! f sid
      | Error msg -> return sprintf "Error: %s" msg
    }

  /// Overload without sessionId parameter (uses None).
  let withSessionWd (ctx: McpContext) (agent: string) (workingDirectory: string option) (f: string -> Task<string>) : Task<string> =
    withSession ctx agent None workingDirectory f

  /// Get the session status via proxy, returning the SessionState.
  let getSessionState (ctx: McpContext) (sessionId: string) : Task<SessionState> =
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
  let requireTool (ctx: McpContext) (sessionId: string) (toolName: string) : Task<Result<unit, string>> =
    task {
      let! state = getSessionState ctx sessionId
      return
        Affordances.checkToolAvailability state toolName
        |> Result.mapError SageFsError.describe
    }

  /// Format a WorkerResponse.EvalResult for display.
  let formatWorkerEvalResult (response: WorkerProtocol.WorkerResponse) : string =
    match response with
    | WorkerProtocol.WorkerResponse.EvalResult(_, result, diags, _) ->
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
    withSession ctx agentName sessionId workingDirectory (fun sid -> task {
      let statements = McpAdapter.splitStatements code
      Instrumentation.fsiEvals.Add(1L)
      Instrumentation.fsiStatements.Add(int64 statements.Length)
      let span = Instrumentation.startSpan Instrumentation.mcpSource "fsi.eval"
                   ["fsi.agent.name", box agentName; "fsi.statement.count", box statements.Length; "fsi.session.id", box sid]
      do! EventTracking.trackInput ctx.Persistence sid (Features.Events.McpAgent agentName) code

      let mutable allOutputs = []
      for statement in statements do
        notifyElm ctx (SageFsEvent.EvalStarted (sid, statement))
        let! routeResult =
          routeToSession ctx sid
            (fun replyId -> WorkerProtocol.WorkerMessage.EvalCode(statement, replyId))
        let output =
          match routeResult with
          | Ok response ->
            let formatted =
              match format with
              | Json -> McpAdapter.formatWorkerEvalResultJson response
              | Text -> formatWorkerEvalResult response
            match response with
            | WorkerProtocol.WorkerResponse.EvalResult(_, Ok _, diags, metadata) ->
              notifyElm ctx (
                SageFsEvent.EvalCompleted (sid, formatted, diags |> List.map WorkerProtocol.WorkerDiagnostic.toDiagnostic))
              // Dispatch live testing events from hook metadata
              match metadata |> Map.tryFind "liveTestHookResult" with
              | Some json ->
                try
                  let hookResult =
                    WorkerProtocol.Serialization.deserialize<Features.LiveTesting.LiveTestHookResultDto> json
                  if not (List.isEmpty hookResult.DetectedProviders) then
                    notifyElm ctx (SageFsEvent.ProvidersDetected hookResult.DetectedProviders)
                  if not (Array.isEmpty hookResult.DiscoveredTests) then
                    notifyElm ctx (SageFsEvent.TestsDiscovered (sid, hookResult.DiscoveredTests))
                  if not (Array.isEmpty hookResult.AffectedTestIds) then
                    notifyElm ctx (SageFsEvent.AffectedTestsComputed hookResult.AffectedTestIds)
                with _ -> ()
              | None -> ()
              // Dispatch assembly load errors if present
              match metadata |> Map.tryFind "assemblyLoadErrors" with
              | Some json ->
                try
                  let errors =
                    WorkerProtocol.Serialization.deserialize<Features.LiveTesting.AssemblyLoadError list> json
                  if not (List.isEmpty errors) then
                    notifyElm ctx (SageFsEvent.AssemblyLoadFailed errors)
                with _ -> ()
              | None -> ()
            | WorkerProtocol.WorkerResponse.EvalResult(_, Error err, _, _) ->
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

      do! EventTracking.trackOutput ctx.Persistence sid (Features.Events.McpAgent agentName) finalOutput
      Instrumentation.succeedSpan span
      return finalOutput
    })

  let getRecentEvents (ctx: McpContext) (agent: string) (count: int) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let! events = EventTracking.getRecentEvents ctx.Persistence sid count
      return McpAdapter.formatEvents events
    })

  let getStatus (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSession ctx agent sessionId workingDirectory (fun sid -> task {
      let! eventCount = EventTracking.getEventCount ctx.Persistence sid
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
    })

  let getStartupInfo (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let! info = ctx.SessionOps.GetSessionInfo sid
      match info with
      | Some sessionInfo ->
        let header =
          sprintf "📋 Startup Information:\n- Session: %s\n- Working Directory: %s\n- Projects: %s\n- MCP Port: %d\n- Status: %s"
            sid
            sessionInfo.WorkingDirectory
            (if sessionInfo.Projects.IsEmpty then "None"
             else String.concat ", " (sessionInfo.Projects |> List.map Path.GetFileName))
            ctx.McpPort
            (WorkerProtocol.SessionStatus.label sessionInfo.Status)
        // Fetch and append warmup detail
        let! warmupDetail =
          match ctx.GetWarmupContext with
          | Some getCtx ->
            task {
              let! wCtx = getCtx sid
              match wCtx with
              | Some warmup ->
                let sessionCtx : SessionContext = {
                  SessionId = sid
                  ProjectNames = sessionInfo.Projects
                  WorkingDir = sessionInfo.WorkingDirectory
                  Status = WorkerProtocol.SessionStatus.label sessionInfo.Status
                  Warmup = warmup
                  FileStatuses = []
                }
                return sprintf "\n\n%s" (McpAdapter.formatWarmupDetailForLlm sessionCtx)
              | None -> return ""
            }
          | None -> Task.FromResult("")
        return header + warmupDetail
      | None ->
        return "SageFs startup information not available yet — session is still initializing"
    })

  let getStartupInfoJson (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
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
    })

  let getAvailableProjects (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
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
    })

  let loadFSharpScript (ctx: McpContext) (agentName: string) (filePath: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSession ctx agentName sessionId workingDirectory (fun sid -> task {
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
    })

  let resetSession (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSession ctx agent sessionId workingDirectory (fun sid -> task {
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
    })

  let checkFSharpCode (ctx: McpContext) (agent: string) (code: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSession ctx agent sessionId workingDirectory (fun sid -> task {
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
    })

  let hardResetSession (ctx: McpContext) (agent: string) (rebuild: bool) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSession ctx agent sessionId workingDirectory (fun sid -> task {
      notifyElm ctx (
        SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Restarting))
      if rebuild then
        notifyElm ctx (
          SageFsEvent.WarmupProgress (1, 4, "Building project..."))
        // Fire-and-forget: build + restart happens in background.
        // Return immediately so MCP tool call doesn't time out (~30s build).
        // Client polls get_fsi_status or list_sessions to check completion.
        task {
          let! result = ctx.SessionOps.RestartSession sid true
          match result with
          | Ok msg ->
            notifyElm ctx (
              SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Running))
          | Error err ->
            notifyElm ctx (
              SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Errored (SageFsError.describe err)))
        } |> ignore
        return "Hard reset initiated — rebuilding project. Use get_fsi_status to check when ready."
      else
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
    })

  let cancelEval (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
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
    })

  let getCompletions (ctx: McpContext) (agent: string) (code: string) (cursorPosition: int) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
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
    })

  let exploreQualifiedName (ctx: McpContext) (agent: string) (qualifiedName: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
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
    })

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
        let! _ = ctx.Persistence.AppendEvents "daemon-sessions" [
          Features.Events.SageFsEvent.DaemonSessionSwitched
            {| FromId = Some prev; ToId = sessionId; SwitchedAt = DateTimeOffset.UtcNow |}
        ]
        return sprintf "Switched to session '%s'" sessionId
      | None ->
        return sprintf "Error: Session '%s' not found" sessionId
    }

  // ── Elm State Query ──────────────────────────────────────────────

  let formatRegionFlags (flags: RegionFlags) =
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

  // ── Live Testing MCP Tools ──────────────────────────────────

  let liveTestJsonOpts =
    let o = JsonSerializerOptions(WriteIndented = false)
    o.Converters.Add(JsonFSharpConverter())
    o

  let getLiveTestStatus (ctx: McpContext) (fileFilter: string option) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Live testing not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let state = model.LiveTesting.TestState
        let activeId =
          ActiveSession.sessionId model.Sessions.ActiveSessionId
          |> Option.defaultValue ""
        let sessionEntries =
          Features.LiveTesting.LiveTestState.statusEntriesForSession activeId state
        let summary =
          Features.LiveTesting.TestSummary.fromStatuses
            state.Activation (sessionEntries |> Array.map (fun e -> e.Status))
        let tests =
          match fileFilter with
          | Some f ->
            let normalizedFilter = f.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar)
            sessionEntries |> Array.filter (fun e ->
              match e.Origin with
              | Features.LiveTesting.TestOrigin.SourceMapped (file, _) ->
                file = normalizedFilter
                || file.EndsWith(normalizedFilter, System.StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString() + normalizedFilter, System.StringComparison.OrdinalIgnoreCase)
              | Features.LiveTesting.TestOrigin.ReflectionOnly -> false)
            |> Some
          | None -> None
        let resp =
          let enabled = state.Activation = Features.LiveTesting.LiveTestingActivation.Active
          let bitmapStats =
            let count = Map.count state.TestCoverageBitmaps
            if count = 0 then None
            else
              let avgProbes =
                state.TestCoverageBitmaps
                |> Map.toSeq
                |> Seq.map (fun (_, bm) -> Features.LiveTesting.CoverageBitmap.popCount bm)
                |> Seq.averageBy float
              Some {| TestsWithCoverage = count; AvgHitProbes = avgProbes |}
          match tests, bitmapStats with
          | Some t, Some bs -> {| Enabled = enabled; Summary = summary; Tests = t; CoverageBitmapStats = bs |} |> box
          | Some t, None -> {| Enabled = enabled; Summary = summary; Tests = t |} |> box
          | None, Some bs -> {| Enabled = enabled; Summary = summary; CoverageBitmapStats = bs |} |> box
          | None, None -> {| Enabled = enabled; Summary = summary |} |> box
        return JsonSerializer.Serialize(resp, liveTestJsonOpts)
    }

  let setLiveTesting (ctx: McpContext) (enabled: bool) : Task<string> =
    task {
      match ctx.Dispatch with
      | None -> return "Cannot set live testing — Elm loop not started."
      | Some dispatch ->
        let msg = if enabled then SageFsMsg.EnableLiveTesting else SageFsMsg.DisableLiveTesting
        dispatch msg
        match ctx.GetElmModel with
        | Some getModel ->
          let state = (getModel ()).LiveTesting.TestState
          let activationLabel =
            match state.Activation with
            | Features.LiveTesting.LiveTestingActivation.Active -> "enabled"
            | Features.LiveTesting.LiveTestingActivation.Inactive -> "disabled"
          return sprintf "Live testing %s." activationLabel
        | None ->
          return sprintf "Live testing %s." (if enabled then "enabled" else "disabled")
    }

  let setRunPolicy (ctx: McpContext) (category: string) (policy: string) : Task<string> =
    let cat =
      match category.ToLowerInvariant() with
      | "unit" -> Some Features.LiveTesting.TestCategory.Unit
      | "integration" -> Some Features.LiveTesting.TestCategory.Integration
      | "browser" -> Some Features.LiveTesting.TestCategory.Browser
      | "benchmark" -> Some Features.LiveTesting.TestCategory.Benchmark
      | "architecture" -> Some Features.LiveTesting.TestCategory.Architecture
      | "property" -> Some Features.LiveTesting.TestCategory.Property
      | other -> Some (Features.LiveTesting.TestCategory.Custom other)
    let pol =
      match policy.ToLowerInvariant() with
      | "oneverychange" | "every" -> Some Features.LiveTesting.RunPolicy.OnEveryChange
      | "onsaveonly" | "save" -> Some Features.LiveTesting.RunPolicy.OnSaveOnly
      | "ondemand" | "demand" -> Some Features.LiveTesting.RunPolicy.OnDemand
      | "disabled" | "off" -> Some Features.LiveTesting.RunPolicy.Disabled
      | _ -> None
    task {
      match ctx.Dispatch with
      | None -> return "Cannot set policy — Elm loop not started."
      | Some dispatch ->
        match cat, pol with
        | Some c, Some p ->
          dispatch (SageFsMsg.Event (SageFsEvent.RunPolicyChanged (c, p)))
          return sprintf "Set %s policy to %A." category p
        | None, _ -> return sprintf "Unknown category: %s. Valid: unit, integration, browser, benchmark, architecture, property." category
        | _, None -> return sprintf "Unknown policy: %s. Valid: every, save, demand, disabled." policy
    }

  let getPipelineTrace (ctx: McpContext) : Task<string> =
    match ctx.GetElmModel with
    | None -> Task.FromResult "Pipeline trace not available — Elm loop not started."
    | Some getModel ->
      let model = getModel ()
      let state = model.LiveTesting.TestState
      let activeId =
        ActiveSession.sessionId model.Sessions.ActiveSessionId
        |> Option.defaultValue ""
      let sessionEntries =
        Features.LiveTesting.LiveTestState.statusEntriesForSession activeId state
      let summary =
        Features.LiveTesting.TestSummary.fromStatuses
          state.Activation (sessionEntries |> Array.map (fun e -> e.Status))
      let timing = model.LiveTesting.LastTiming
      let resp = {|
        Enabled = state.Activation = Features.LiveTesting.LiveTestingActivation.Active
        IsRunning = Features.LiveTesting.TestRunPhase.isAnyRunning state.RunPhases
        History = state.History
        Summary = summary
        Timing = timing |> Option.map Features.LiveTesting.PipelineTiming.toStatusBar |> Option.defaultValue "no timing yet"
        Providers = state.DetectedProviders |> List.map (fun p ->
          match p with
          | Features.LiveTesting.ProviderDescription.AttributeBased a -> a.Name
          | Features.LiveTesting.ProviderDescription.Custom c -> c.Name)
        Policies = state.RunPolicies |> Map.toList |> List.map (fun (c, p) -> sprintf "%A: %A" c p)
      |}
      Task.FromResult (JsonSerializer.Serialize(resp, liveTestJsonOpts))

  type RunTestsResult =
    | Completed of passed: int * failed: int * total: int
    | TimedOut of passed: int * failed: int * running: int * total: int
    | Disabled
    | NoTestsMatched of totalDiscovered: int

  module RunTestsResult =
    let format result =
      match result with
      | Completed (p, f, total) ->
        if f = 0 then sprintf "✅ All %d tests passed." total
        else sprintf "❌ %d passed, %d failed out of %d tests." p f total
      | TimedOut (p, f, running, total) ->
        sprintf "⏱️ Timed out: %d passed, %d failed, %d still running out of %d tests. Use get_live_test_status for updates." p f running total
      | Disabled ->
        "Live testing is disabled. Toggle it on first."
      | NoTestsMatched totalDiscovered ->
        sprintf "No tests matched. Total discovered: %d." totalDiscovered

  let countStatuses
    (entries: Features.LiveTesting.TestStatusEntry array)
    (triggeredSet: Set<Features.LiveTesting.TestId>)
    : int * int * int =
    let mutable passed = 0
    let mutable failed = 0
    let mutable running = 0
    for e in entries do
      if Set.contains e.TestId triggeredSet then
        match e.Status with
        | Features.LiveTesting.TestRunStatus.Passed _ -> passed <- passed + 1
        | Features.LiveTesting.TestRunStatus.Failed _ -> failed <- failed + 1
        | Features.LiveTesting.TestRunStatus.Running -> running <- running + 1
        | _ -> ()
    (passed, failed, running)

  let pollForTestCompletion
    (getModel: unit -> SageFsModel)
    (triggeredTestIds: Features.LiveTesting.TestId array)
    (timeoutSeconds: int)
    : Task<RunTestsResult> =
    let total = triggeredTestIds.Length
    if timeoutSeconds = 0 then
      let model = getModel ()
      let entries = model.LiveTesting.TestState.StatusEntries
      let triggeredSet = Set.ofArray triggeredTestIds
      let (p, f, _) = countStatuses entries triggeredSet
      Task.FromResult (Completed (p, f, total))
    else
      task {
        let deadline = DateTime.UtcNow.AddSeconds(float timeoutSeconds)
        let triggeredSet = Set.ofArray triggeredTestIds
        let mutable result = None
        while result.IsNone && DateTime.UtcNow < deadline do
          let model = getModel ()
          let entries = model.LiveTesting.TestState.StatusEntries
          let (p, f, r) = countStatuses entries triggeredSet
          if p + f >= total then
            result <- Some (Completed (p, f, total))
          else
            do! Task.Delay 200
        match result with
        | Some r -> return r
        | None ->
          let model = getModel ()
          let entries = model.LiveTesting.TestState.StatusEntries
          let (p, f, r) = countStatuses entries triggeredSet
          return TimedOut (p, f, r, total)
      }

  let runTests
    (ctx: McpContext)
    (patternFilter: string option)
    (categoryFilter: string option)
    (timeoutSeconds: int)
    : Task<string> =
    match ctx.GetElmModel, ctx.Dispatch with
    | None, _ -> Task.FromResult (RunTestsResult.format Disabled)
    | _, None -> Task.FromResult (RunTestsResult.format Disabled)
    | Some getModel, Some dispatch ->
      let model = getModel ()
      let state = model.LiveTesting.TestState
      if state.Activation = Features.LiveTesting.LiveTestingActivation.Inactive then
        Task.FromResult (RunTestsResult.format Disabled)
      else
        let category =
          match categoryFilter with
          | Some c ->
            match c.ToLowerInvariant() with
            | "unit" -> Some Features.LiveTesting.TestCategory.Unit
            | "integration" -> Some Features.LiveTesting.TestCategory.Integration
            | "browser" -> Some Features.LiveTesting.TestCategory.Browser
            | "benchmark" -> Some Features.LiveTesting.TestCategory.Benchmark
            | "architecture" -> Some Features.LiveTesting.TestCategory.Architecture
            | "property" -> Some Features.LiveTesting.TestCategory.Property
            | other -> Some (Features.LiveTesting.TestCategory.Custom other)
          | None -> None
        let tests =
          Features.LiveTesting.LiveTestPipelineState.filterTestsForExplicitRun
            state.DiscoveredTests None patternFilter category
        if Array.isEmpty tests then
          Task.FromResult (RunTestsResult.format (NoTestsMatched state.DiscoveredTests.Length))
        else
          let testIds = tests |> Array.map (fun tc -> tc.Id)
          dispatch (SageFsMsg.Event (SageFsEvent.RunTestsRequested tests))
          if timeoutSeconds = 0 then
            Task.FromResult (sprintf "Triggered %d tests for execution." tests.Length)
          else
            task {
              let! result = pollForTestCompletion getModel testIds timeoutSeconds
              return RunTestsResult.format result
            }
