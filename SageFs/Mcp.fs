namespace SageFs

#nowarn "3511"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open System.Xml.Linq
open SageFs.AppState
open SageFs.WarmUp
open SageFs.Features.CellDependenciesReport
open SageFs.Utils

/// Pure functions for MCP adapter (formatting responses)
module McpAdapter =

  let isSolutionFile (path: string) =
    path.EndsWith(".sln", System.StringComparison.Ordinal) || path.EndsWith(".slnx", System.StringComparison.Ordinal)

  let isProjectFile (path: string) =
    path.EndsWith(".fsproj", System.StringComparison.Ordinal)

  let formatAvailableProjects (workingDir: string) (projects: string array) (solutions: string array) =
    let projectList =
      match Array.isEmpty projects with
      | true -> "  (none found)"
      | false -> projects |> Array.map (sprintf "  - %s") |> String.concat "\n"
    let solutionList =
      match Array.isEmpty solutions with
      | true -> "  (none found)"
      | false -> solutions |> Array.map (sprintf "  - %s") |> String.concat "\n"
    sprintf "Available Projects/Solutions in %s:\n\n📦 F# Projects (.fsproj):\n%s\n\n📂 Solutions (.sln/.slnx):\n%s\n\n💡 Start the daemon with: SageFs\n💡 Then create a session for ProjectName.fsproj or SolutionName.slnx via create_session\n💡 Sessions can also be created from connected editors or the dashboard" workingDir projectList solutionList

  let formatStartupBanner (version: string) (mcpPort: int option) =
    match mcpPort with
    | Some port -> sprintf "SageFs v%s | MCP on port %d" version port
    | None -> sprintf "SageFs v%s" version

  let formatEvalResult (workflow: WorkflowTypes.SessionWorkflow) (result: EvalResponse) : string =
    let stdout = 
      match result.Metadata.TryFind "stdout" with
      | Some (s: obj) -> s.ToString()
      | None -> ""
    
    let diagnosticsSection =
      match Array.isEmpty result.Diagnostics with
      | true -> ""
      | false ->
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
          let suggestion = ex.Message |> ErrorMessages.categorize |> ErrorMessages.getSuggestion
          let enhanced = WorkflowErrorContext.enhance workflow ex.Message suggestion
          sprintf "Error: %s\n%s%s" ex.Message enhanced diagnosticsSection
    
    match String.IsNullOrEmpty(stdout) with
    | true -> output
    | false -> sprintf "%s\n%s" stdout output

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
        match String.IsNullOrEmpty v with | true -> null | false -> v
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
        asmCount opened (opened + failed) (WarmupContext.totalDurationMs w))

    match asmCount > 0 with
    | true ->
      lines.Add(sprintf "  Assemblies (%d):" asmCount)
      for a in w.AssembliesLoaded do
        lines.Add(sprintf "    📦 %s (%d ns, %d modules)" a.Name a.NamespaceCount a.ModuleCount)
      lines.Add("  ⚠️ Do NOT '#r' any of the assemblies listed above — they are already loaded via the project graph.")
      lines.Add("     Using '#r' on them creates a second .NET load context causing TypeLoadException on ALL subsequent evals.")
      lines.Add("     Reference project types directly without '#r'. They are already in scope.")
    | false -> ()

    // Phase timing breakdown
    let t = w.PhaseTiming
    lines.Add(sprintf "  Timing: scan=%dms, asm=%dms, open=%dms, total=%dms"
      t.ScanSourceFilesMs t.ScanAssembliesMs t.OpenNamespacesMs t.TotalMs)

    match w.NamespacesOpened.Length > 0 with
    | true ->
      lines.Add(sprintf "  Opened (%d):" w.NamespacesOpened.Length)
      for b in w.NamespacesOpened do
        let kind = OpenableKind.label b.Kind
        lines.Add(sprintf "    open %s // %s (%.1fms)" b.Name kind b.DurationMs)
    | false -> ()

    match w.FailedOpens.Length > 0 with
    | true ->
      lines.Add(sprintf "  ⚠ Failed opens (%d):" w.FailedOpens.Length)
      for f in w.FailedOpens do
        let kind = OpenableKind.label f.Kind
        lines.Add(sprintf "    ✖ %s (%s) — %s" f.Name kind f.ErrorMessage)
        for d in f.Diagnostics do
          let loc =
            match d.FileName with
            | Some fn -> sprintf "%s:%d:%d" fn d.StartLine d.StartColumn
            | None -> "unknown"
          lines.Add(sprintf "      FS%04d %s — %s" d.ErrorNumber loc d.Message)
    | false -> ()

    let files = ctx.FileStatuses
    match files.Length > 0 with
    | true ->
      let loaded = files |> List.filter (fun f -> f.Readiness = Loaded) |> List.length
      lines.Add(sprintf "  Files (%d/%d loaded):" loaded files.Length)
      for f in files do
        lines.Add(sprintf "    %s %s" (FileReadiness.icon f.Readiness) f.Path)
    | false -> ()

    lines |> Seq.toList |> String.concat "\n"

  let splitStatements (code: string) : string list =
    let mutable i = 0
    let len = code.Length
    let statements = ResizeArray<string>()
    let current = Text.StringBuilder()
    let inline peek offset = match i + offset < len with | true -> code.[i + offset] | false -> '\000'
    while i < len do
      let c = code.[i]
      match c with
      | '"' when peek 1 = '"' && peek 2 = '"' ->
        current.Append("\"\"\"") |> ignore
        i <- i + 3
        let mutable inTriple = true
        while inTriple && i < len do
          match code.[i] = '"' && peek 1 = '"' && peek 2 = '"' with
          | true ->
            current.Append("\"\"\"") |> ignore
            i <- i + 3
            inTriple <- false
          | false ->
            current.Append(code.[i]) |> ignore
            i <- i + 1
      | '@' when peek 1 = '"' ->
        current.Append("@\"") |> ignore
        i <- i + 2
        let mutable inVerbatim = true
        while inVerbatim && i < len do
          match code.[i] = '"' && peek 1 = '"', code.[i] = '"' with
          | true, _ ->
            current.Append("\"\"") |> ignore
            i <- i + 2
          | _, true ->
            current.Append('"') |> ignore
            i <- i + 1
            inVerbatim <- false
          | _ ->
            current.Append(code.[i]) |> ignore
            i <- i + 1
      | '"' ->
        current.Append('"') |> ignore
        i <- i + 1
        let mutable inStr = true
        while inStr && i < len do
          match code.[i] = '\\', code.[i] = '"' with
          | true, _ ->
            current.Append(code.[i]) |> ignore
            i <- i + 1
            match i < len with
            | true ->
              current.Append(code.[i]) |> ignore
              i <- i + 1
            | false -> ()
          | _, true ->
            current.Append('"') |> ignore
            i <- i + 1
            inStr <- false
          | _ ->
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
          match code.[i] = '(' && peek 1 = '*', code.[i] = '*' && peek 1 = ')' with
          | true, _ ->
            current.Append("(*") |> ignore
            i <- i + 2
            depth <- depth + 1
          | _, true ->
            current.Append("*)") |> ignore
            i <- i + 2
            depth <- depth - 1
          | _ ->
            current.Append(code.[i]) |> ignore
            i <- i + 1
      | ';' when peek 1 = ';' ->
        let stmt = current.ToString().Trim()
        match stmt.Length > 0 with
        | true -> statements.Add(stmt + ";;")
        | false -> ()
        current.Clear() |> ignore
        i <- i + 2
      | _ ->
        current.Append(c) |> ignore
        i <- i + 1
    let trailing = current.ToString().Trim()
    match trailing.Length > 0 with
    | true -> statements.Add(trailing)
    | false -> ()
    statements |> Seq.toList

  let echoStatement (writer: TextWriter) (statement: string) =
    let code =
      match statement.EndsWith(";;", System.StringComparison.Ordinal) with
      | true -> statement.[.. statement.Length - 3]
      | false -> statement
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
        let detail =
          match item.GetDescription with
          | Some getDesc ->
            try
              let tags = getDesc ()
              let text = tags |> Array.map (fun t -> t.Text) |> String.concat ""
              match text.Length > 0 with
              | true -> sprintf ""","detail":"%s" """ (escapeJson text)
              | false -> ""
            with
            | :? System.OperationCanceledException -> reraise()
            | _ -> ""
          | None -> ""
        sprintf """{"label":"%s","kind":"%s","insertText":"%s"%s}"""
          (escapeJson item.DisplayText) (Features.AutoCompletion.CompletionKind.label item.Kind) (escapeJson item.ReplacementText) detail)
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
      match importantArgs.Length = 0 with
      | true -> "(none)"
      | false -> String.concat " " importantArgs
    
    let projectsStr = 
      match config.LoadedProjects.IsEmpty with
      | true -> "None"
      | false -> String.concat ", " config.LoadedProjects
    let hotReloadStr = match config.HotReloadEnabled with | true -> "Enabled ✓" | false -> "Disabled"
    let aspireStr = match config.AspireDetected with | true -> "Yes ✓" | false -> "No"
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
    match Array.isEmpty diagnostics with
    | true -> "No issues found."
    | false ->
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
          match config.LoadedProjects.IsEmpty with
          | true -> "None"
          | false -> String.concat ", " (config.LoadedProjects |> List.map Path.GetFileName)
    
    let startupSection =
      match startupConfig with
      | None -> ""
      | Some config ->
          let hotReload = match config.HotReloadEnabled with | true -> "✅" | false -> "❌"
          let aspire = match config.AspireDetected with | true -> "✅" | false -> "❌"
          let fileWatch = match config.HotReloadEnabled with | true -> "✅ (auto-reload .fs/.fsx via #load)" | false -> "❌"
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
        let workflowLabel = WorkflowTypes.SessionWorkflow.label config.Workflow
        let replCap =
          match WorkflowTypes.SessionWorkflow.replCapability config.Workflow with
          | WorkflowTypes.ReplCapability.Full -> "Full"
          | WorkflowTypes.ReplCapability.ExpressionOnly -> "ExpressionOnly"
        sprintf ""","startup":{"workingDirectory":"%s","mcpPort":%d,"hotReloadEnabled":%b,"aspireDetected":%b,"workflow":"%s","workflowLabel":"%s","replCapability":"%s"}"""
          (escapeJson config.WorkingDirectory) config.McpPort config.HotReloadEnabled config.AspireDetected
          (escapeJson (sprintf "%A" config.Workflow)) workflowLabel replCap
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
      match info.Projects.IsEmpty with
      | true -> "None"
      | false -> String.concat ", " (info.Projects |> List.map Path.GetFileName)
    let statsSection =
      match snapshot.EvalCount > 0 with
      | true ->
        sprintf "\nEvals: %d | Avg: %dms | Min: %dms | Max: %dms"
          snapshot.EvalCount snapshot.AvgDurationMs snapshot.MinDurationMs snapshot.MaxDurationMs
      | false -> ""
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
          (escapeJson (SageFsError.describeForAgent err)) diagsJson
    | WorkerProtocol.WorkerResponse.WorkerError err ->
      sprintf """{"success":false,"error":"%s","diagnostics":[]}"""
        (escapeJson (SageFsError.describeForAgent err))
    | other ->
      sprintf """{"success":false,"error":"%s","diagnostics":[]}"""
        (escapeJson (sprintf "Unexpected response: %A" other))

/// MCP tool implementations — all tools route through SessionManager.
/// There is no "local embedded session" — every session is a worker.
module McpTools =

  open System.Threading

  type McpContext = {
    /// SQLite-backed friction store for durable telemetry persistence.
    FrictionStore: Features.FrictionSqlite.FrictionStore option
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
    /// Read the current feature push state (eval history, bindings, timeline).
    GetFeatureState: (unit -> Features.FeatureHooks.FeaturePushState) option
    /// In-memory agent activity tracker for multi-agent coordination.
    ActivityTracker: AgentActivityTracker.Tracker
    /// Receives the live bound-value snapshot after each successful eval
    /// (daemon wires this to the adaptive live-bindings store; None in tests).
    LiveSnapshotSink: (string -> Features.LiveValueTree.LiveValueSnapshot -> unit) option
  }

  /// Get the active session ID for a specific agent/client.
  let activeSessionId (ctx: McpContext) (agent: string) =
    match ctx.SessionMap.TryGetValue(agent) with
    | true, sid -> sid
    | _ -> ""

  /// Set the active session ID for a specific agent/client.
  let setActiveSessionId (ctx: McpContext) (agent: string) (sid: string) =
    ctx.SessionMap.[agent] <- sid

  /// Per-session compilation context state (evaluated modules, file cache).
  let compilationStates =
    Collections.Concurrent.ConcurrentDictionary<string, Middleware.CompilationContext.CompilationState>()

  /// Per-session TypeLoadException diagnostic.
  /// When a TypeLoadException poisons an FSI session, the diagnostic string is stored here
  /// so that targeted_verify and get_fsi_status can report the compromised type identity.
  /// Cleared on hard_reset_fsi_session (which creates a fresh FSI session).
  let typeIdentityDiagnostics =
    Collections.Concurrent.ConcurrentDictionary<string, string>()

  /// Temporal dedup cache — prevents re-evaluating identical code within 2s window.
  let evalDedupCache = Features.EvalDedup.DedupCache.defaultCache ()

  /// Convert a resolved session ID string to SessionId for SessionOps calls.
  /// Pre-condition: sid came from resolveSessionId or session lookup (already valid format).
  let private toSessionId (sid: string) =
    match WorkerProtocol.SessionId.validate sid with
    | Ok id -> id
    | Error e -> failwithf "Invalid resolved session ID '%s': %s" sid e

  /// Normalize a path for comparison: trim trailing separators, lowercase on Windows.
  let normalizePath (p: string) =
    let trimmed = p.TrimEnd('/', '\\')
    match Environment.OSVersion.Platform = PlatformID.Win32NT with
    | true -> trimmed.Replace('/', '\\').ToLowerInvariant()
    | false -> trimmed

  let private sessionsMatchingWorkingDir (sessions: WorkerProtocol.SessionInfo list) (workingDir: string) =
    let target = normalizePath workingDir
    sessions
    |> List.filter (fun s -> normalizePath s.WorkingDirectory = target)

  /// WHY — agents call tools with the directory they are WORKING IN, which is
  /// often a subdirectory of the registered session root (e.g. repo\tests while
  /// the session is rooted at repo). Exact-only matching turned that into
  /// "No sessions match" while list_sessions showed the session plainly present;
  /// status and list disagreed and only an explicit switch_session recovered
  /// (friction report 2026-08). Because — matching falls back to sessions whose
  /// registered directory is a path-boundary ancestor of the requested one, so a
  /// request from inside a session's tree routes to that session instead of vanishing.
  let sessionsMatchingWorkingDirDeep (sessions: WorkerProtocol.SessionInfo list) (workingDir: string) =
    let target = normalizePath workingDir
    match sessionsMatchingWorkingDir sessions workingDir with
    | [] ->
      let isPathAncestorOf (ancestor: string) (candidate: string) =
        candidate.StartsWith(ancestor + "\\", StringComparison.Ordinal)
        || candidate.StartsWith(ancestor + "/", StringComparison.Ordinal)
      sessions
      |> List.filter (fun s ->
        let baseDir = normalizePath s.WorkingDirectory
        not (String.IsNullOrWhiteSpace baseDir) && isPathAncestorOf baseDir target)
    | matched -> matched

  /// Honest failure: when working-directory routing finds nothing, say what DOES
  /// exist so the agent can reconcile the disagreement without a second tool call.
  let formatExistingSessionsHint (sessions: WorkerProtocol.SessionInfo list) =
    match sessions with
    | [] -> "(none running)"
    | _ ->
      sessions
      |> List.map (fun s ->
        sprintf "%s (%s, dir: %s)"
          (WorkerProtocol.SessionId.value s.Id)
          (WorkerProtocol.SessionStatus.label s.Status)
          s.WorkingDirectory)
      |> String.concat "; "

  /// Find a session whose WorkingDirectory matches the given path.
  /// Convenience helper only — authoritative callers must detect ambiguity
  /// before selecting a single session.
  let resolveSessionByWorkingDir (sessions: WorkerProtocol.SessionInfo list) (workingDir: string) : WorkerProtocol.SessionInfo option =
    sessionsMatchingWorkingDir sessions workingDir
    |> List.tryHead

  let private formatSessionRoutingChoice (session: WorkerProtocol.SessionInfo) =
    sprintf "  %s  %s  %s"
      (WorkerProtocol.SessionId.value session.Id)
      (WorkerProtocol.SessionStatus.label session.Status)
      session.WorkingDirectory

  let private formatWorkingDirectoryAmbiguity (prefix: string) (workingDir: string) (sessions: WorkerProtocol.SessionInfo list) =
    let matches =
      sessions
      |> List.map formatSessionRoutingChoice
      |> String.concat "\n"
    sprintf "%s '%s'.\n\nUse switch_session to select one before calling other tools, or pass sessionId explicitly on every call.\n\nMatching sessions:\n%s"
      prefix workingDir matches

  /// Notify the Elm loop of an event (fire-and-forget, no-op if no dispatch).
  let notifyElm (ctx: McpContext) (event: SageFsEvent) =
    ctx.Dispatch
    |> Option.iter (fun dispatch ->
      dispatch (SageFsMsg.Event event))

  type RouteError =
    | Message of string
    | TransportFailure of string
    /// Session is deliberately starting/restarting; transport unavailability is
    /// expected and must not be treated as a crash.
    | RestartInProgress of string

  let routeErrorMessage = function
    | Message msg -> msg
    | TransportFailure msg -> msg
    | RestartInProgress msg -> msg

  let routeErrorIsTransportFailure = function
    | TransportFailure _ -> true
    | Message _ -> false
    | RestartInProgress _ -> false

  let innermostException (ex: exn) =
    let rec loop (current: exn) =
      match current.InnerException with
      | null -> current
      | inner -> loop inner
    loop ex

  let tryMapTransportFailure (sessionId: string) (ex: exn) =
    let rec unwrap (error: exn) =
      match error with
      | :? AggregateException as aggregate when not (isNull aggregate.InnerException) ->
        unwrap aggregate.InnerException
      | other -> other

    let transport = unwrap ex
    let describe reason =
      SageFsError.WorkerCommunicationFailed(sessionId, sprintf "Session transport closed — %s" reason)
      |> SageFsError.describeForAgent

    match transport with
    | :? OperationCanceledException -> None
    | :? System.Net.Http.HttpRequestException as httpError ->
      let reason =
        match httpError.InnerException with
        | null when String.IsNullOrWhiteSpace httpError.Message ->
          "HTTP request failed"
        | null ->
          httpError.Message
        | inner ->
          let root = innermostException inner
          match String.IsNullOrWhiteSpace root.Message with
          | true -> httpError.Message
          | false -> root.Message
      Some (TransportFailure (describe reason))
    | :? IOException as ioError ->
      Some (TransportFailure (describe ioError.Message))
    | :? ObjectDisposedException as disposed ->
      Some (TransportFailure (describe disposed.Message))
    | _ ->
      None

  /// Route a WorkerMessage to a specific session via proxy.
  let routeToSession
    (ctx: McpContext)
    (sessionId: string)
    (msg: WorkerProtocol.SessionId -> WorkerProtocol.WorkerMessage)
    : Task<Result<WorkerProtocol.WorkerResponse, RouteError>> =
    task {
      match WorkerProtocol.SessionId.validate sessionId with
      | Error e -> return Error (Message (sprintf "Invalid session ID: %s" e))
      | Ok validId ->
        let! proxy = ctx.SessionOps.GetProxy validId
        match proxy with
        | None ->
          let! info = ctx.SessionOps.GetSessionInfo validId
          match info with
          | Some i when i.Status = WorkerProtocol.SessionStatus.Starting
                     || i.Status = WorkerProtocol.SessionStatus.Restarting ->
            return Result.Error (Message (sprintf "Session '%s' is still warming up (%s). This typically takes 15-30s for test projects. Poll get_fsi_status every 5-10s to check readiness. Do NOT create a new session — it will compete for resources and make warmup slower." sessionId (WorkerProtocol.SessionStatus.label i.Status)))
          | _ ->
            return Result.Error (Message (sprintf "Session '%s' not found" sessionId))
        | Some send ->
          let replyId = WorkerProtocol.SessionId.newId()
          try
            let! response = send (msg replyId) |> Async.StartAsTask
            return Result.Ok response
          with
          | :? OperationCanceledException as cancellation ->
            return raise cancellation
          | ex ->
            match tryMapTransportFailure sessionId ex with
            | Some transportError ->
              // INVARIANT (reader cannot fault a starting/restarting session):
              // a transport failure observed while the SessionManager is
              // deliberately respawning the worker is expected — the worker is
              // being swapped. It must NOT NotifyWorkerDied (which posts a
              // synthetic WorkerExited(pid=-1) that the stale-pid guards do not
              // catch and which can schedule a second restart) and must NOT mark
              // the session Faulted. Only the restart owner faults a restarting
              // session.
              // Discriminator: a daemon-owned restart sets Status to
              // Starting/Restarting AND clears WorkerPid (SessionManager cold-
              // restart path). A caller-driven reset (resetSession / hardReset
              // rebuild=false) flips Status via UpdateSessionStatus, which
              // PRESERVES WorkerPid — so a transport failure there is a real
              // worker death and must trigger NotifyWorkerDied recovery.
              let! info = ctx.SessionOps.GetSessionInfo validId
              match info with
              | Some i when (i.Status = WorkerProtocol.SessionStatus.Starting
                            || i.Status = WorkerProtocol.SessionStatus.Restarting)
                          && i.WorkerPid.IsNone ->
                return Error (RestartInProgress (sprintf "Session '%s' is %s — transport is temporarily unavailable by design. Poll get_fsi_status every 5-10s; do NOT retry hard_reset_fsi_session or create a new session." sessionId (WorkerProtocol.SessionStatus.label i.Status)))
              | _ ->
                ctx.SessionOps.NotifyWorkerDied validId
                do! ctx.SessionOps.UpdateSessionStatus validId WorkerProtocol.SessionStatus.Faulted
                return Result.Error transportError
            | None ->
              return raise ex
    }

  /// Typed outcome of resolving which session a tool call should target.
  /// Guidance text is a pure function of this union: a session that exists in
  /// the registry is never reported as gone. `Gone` is produced only when the
  /// session is genuinely absent (never created, or explicitly stopped).
  type SessionResolution =
    | Routable of sessionId: string
    | WarmingUp of sessionId: string * status: WorkerProtocol.SessionStatus
    | Unroutable of sessionId: string * status: WorkerProtocol.SessionStatus
    | FaultedSession of sessionId: string
    | Gone of message: string

  /// Pure classification: decide the resolution from registry knowledge.
  /// INVARIANT: `Gone` is produced only when the session is absent from the
  /// registry; an existing session is always Routable, WarmingUp, Unroutable,
  /// or FaultedSession — never Gone.
  let classifySessionAvailability
    (info: WorkerProtocol.SessionInfo option)
    (proxyAvailable: bool)
    : SessionResolution =
    match info with
    | Some i when proxyAvailable -> Routable (WorkerProtocol.SessionId.value i.Id)
    | Some i ->
      match i.Status with
      | WorkerProtocol.SessionStatus.Starting
      | WorkerProtocol.SessionStatus.Restarting ->
        WarmingUp (WorkerProtocol.SessionId.value i.Id, i.Status)
      | WorkerProtocol.SessionStatus.Faulted
      | WorkerProtocol.SessionStatus.Stopped ->
        FaultedSession (WorkerProtocol.SessionId.value i.Id)
      | _ ->
        Unroutable (WorkerProtocol.SessionId.value i.Id, i.Status)
    | None ->
      Gone "Session is no longer running. Use create_session to start a new one."

  /// Pure guidance: the agent-facing message for a resolution.
  /// INVARIANT: "create_session" and "no longer running" appear only in the
  /// Gone case — an existing session is never presented as missing.
  let formatSessionResolution = function
    | Routable _ -> ""
    | WarmingUp (sid, status) ->
      sprintf "Session '%s' is still warming up (%s). This typically takes 15-30s for test projects. Poll get_fsi_status every 5-10s to check readiness. Do NOT create a new session — it will compete for resources and make warmup slower." sid (WorkerProtocol.SessionStatus.label status)
    | Unroutable (sid, status) ->
      sprintf "Session '%s' exists (status: %s) but its worker is not routable yet — it may be mid-restart. Check get_fsi_status or list_sessions and re-check shortly. Do NOT create a duplicate session." sid (WorkerProtocol.SessionStatus.label status)
    | FaultedSession sid ->
      sprintf "Session '%s' is faulted. Run reset_fsi_session or hard_reset_fsi_session to recover." sid
    | Gone msg -> msg

  /// Route to the active session or the specified session.
  /// When no agent mapping exists, resolves by the caller's working directory.
  /// Returns a typed SessionResolution — never a lying string.
  let resolveSessionId (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<SessionResolution> =
    task {
      match sessionId with
      | Some sid ->
        let validId = toSessionId sid
        let! proxy = ctx.SessionOps.GetProxy validId
        match proxy with
        | Some _ -> return Routable sid
        | None ->
          let! info = ctx.SessionOps.GetSessionInfo validId
          return classifySessionAvailability info false
      | None ->
        let! candidateResult =
          task {
            match workingDirectory with
            | Some wd when not (System.String.IsNullOrWhiteSpace wd) ->
              let! sessions = ctx.SessionOps.GetAllSessions()
              match sessionsMatchingWorkingDirDeep sessions wd with
              | [ matched ] ->
                let matchedId = WorkerProtocol.SessionId.value matched.Id
                setActiveSessionId ctx agent matchedId
                return Ok matchedId
              | [] ->
                return Error (sprintf "No sessions match workingDirectory '%s'. Running sessions: %s. Use create_session with that directory, or switch_session to an existing matching session." wd (formatExistingSessionsHint sessions))
              | matches ->
                return Error (formatWorkingDirectoryAmbiguity "Multiple sessions match workingDirectory" wd matches)
            | _ ->
              return Ok (activeSessionId ctx agent)
          }
        match candidateResult with
        | Error msg -> return Gone msg
        | Ok candidate when candidate <> "" ->
          let validCandidate = toSessionId candidate
          let! proxy = ctx.SessionOps.GetProxy validCandidate
          match proxy with
          | Some _ -> return Routable candidate
          | None ->
            let! info = ctx.SessionOps.GetSessionInfo validCandidate
            match info with
            | Some i when i.Status = WorkerProtocol.SessionStatus.Starting
                       || i.Status = WorkerProtocol.SessionStatus.Restarting ->
              setActiveSessionId ctx agent ""
              return WarmingUp (candidate, i.Status)
            | Some i when i.Status = WorkerProtocol.SessionStatus.Faulted
                       || i.Status = WorkerProtocol.SessionStatus.Stopped ->
              setActiveSessionId ctx agent ""
              return FaultedSession candidate
            | Some i ->
              setActiveSessionId ctx agent ""
              return Unroutable (candidate, i.Status)
            | None ->
              setActiveSessionId ctx agent ""
              return Gone "Session is no longer running. Use create_session to start a new one."
        | Ok _ ->
          let! sessions = ctx.SessionOps.GetAllSessions()
          let currentDir = Environment.CurrentDirectory
          let currentDirMatches = sessionsMatchingWorkingDir sessions currentDir
          match currentDirMatches with
          | [ currentDirSession ] ->
            let sid = WorkerProtocol.SessionId.value currentDirSession.Id
            setActiveSessionId ctx agent sid
            let! proxy = ctx.SessionOps.GetProxy (toSessionId sid)
            match proxy with
            | Some _ -> return Routable sid
            | None ->
              let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
              return classifySessionAvailability info false
          | _ :: _ :: _ as matches ->
            return Gone (formatWorkingDirectoryAmbiguity "Multiple sessions match the current working directory" currentDir matches)
          | [] ->
            match sessions with
            | [ singleSession ] ->
              let sid = WorkerProtocol.SessionId.value singleSession.Id
              setActiveSessionId ctx agent sid
              let! proxy = ctx.SessionOps.GetProxy (toSessionId sid)
              match proxy with
              | Some _ -> return Routable sid
              | None ->
                let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
                return classifySessionAvailability info false
            | _ ->
              return Gone "No active session. Use create_session to create one first."
    }

  /// Helper: run a function with the resolved session ID, or return the error message.
  let withSession (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) (f: string -> Task<string>) : Task<string> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Routable sid -> return! f sid
      | other -> return sprintf "Error: %s" (formatSessionResolution other)
    }

  /// Recovery variant for tools that must be able to reach a session that
  /// needs recovery even when its worker proxy is not installed:
  /// reset_fsi_session and hard_reset_fsi_session are exactly how an agent
  /// recovers from Faulted, so FaultedSession routes through to the handler
  /// (which re-spawns via SessionManager). Unroutable (status Ready/Evaluating/
  /// Building but no proxy — the transitional window where the registry is
  /// ahead of proxy installation) also routes through: the reset path goes via
  /// SessionManagementOps, not the worker proxy, so it does not need the proxy;
  /// a rebuild=false reset on a proxy-less session fails gracefully through the
  /// transport-error path.
  /// ONLY WarmingUp (Starting/Restarting) blocks — restarting a session that is
  /// already restarting is the bug we are preventing.
  let withSessionAllowFaulted (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) (f: string -> Task<string>) : Task<string> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Routable sid -> return! f sid
      | FaultedSession sid -> return! f sid
      | Unroutable (sid, _) -> return! f sid
      | other -> return sprintf "Error: %s" (formatSessionResolution other)
    }

  /// Overload without sessionId parameter (uses None).
  let withSessionWd (ctx: McpContext) (agent: string) (workingDirectory: string option) (f: string -> Task<string>) : Task<string> =
    withSession ctx agent None workingDirectory f

  let setSnapshotStatus (ctx: McpContext) (sid: string) (status: WorkerProtocol.SessionStatus) =
    ctx.SessionOps.UpdateSessionStatus (toSessionId sid) status

  /// Get the session status via proxy, returning the SessionState.
  let getSessionState (ctx: McpContext) (sessionId: string) : Task<SessionState> =
    task {
      let! routeResult =
        routeToSession ctx sessionId
          (fun replyId -> WorkerProtocol.WorkerMessage.GetStatus (WorkerProtocol.SessionId.value replyId))
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
        |> Result.mapError SageFsError.describeForAgent
    }

  /// ── Affordance call gate ─────────────────────────────────────────────────
  ///
  /// Structural enforcement point for the affordance model. The MCP server's
  /// CallToolFilter runs this BEFORE any `[<McpServerTool>]` body executes, so a
  /// tool that is not available in the CURRENT session state is rejected with a
  /// structured error instead of executing.
  ///
  /// Classification comes only from `Affordances.toolGate`:
  ///   - AlwaysAvailable — no session-state dependence; callable in every state
  ///     and before any session exists (monitoring, session listing, telemetry).
  ///   - StateGated      — availability is derived from `availableTools` for the
  ///     state of the session the call would actually act on. `sessionId` /
  ///     `workingDirectory` mirror the routing inputs the tool body will use, so
  ///     the gate evaluates the SAME session the tool would target.
  ///   - undeclared      — fails closed (ToolNotAvailable).
  let enforceToolCallGate
    (ctx: McpContext)
    (agent: string)
    (sessionId: string option)
    (workingDirectory: string option)
    (toolName: string)
    : Task<Result<unit, string>> =
    task {
      match Affordances.toolGate toolName with
      | Some Affordances.ToolGate.AlwaysAvailable ->
        return Ok ()
      | Some Affordances.ToolGate.StateGated ->
        let! resolution = resolveSessionId ctx agent sessionId workingDirectory
        match resolution with
        | Routable sid ->
          // Worker-authoritative state for the routable session.
          return! requireTool ctx sid toolName
        | WarmingUp (_, status) | Unroutable (_, status) ->
          let state = WorkerProtocol.SessionStatus.toSessionState status
          return
            Affordances.checkToolCallAllowed state toolName
            |> Result.mapError SageFsError.describeForAgent
        | FaultedSession _ ->
          return
            Affordances.checkToolCallAllowed SessionState.Faulted toolName
            |> Result.mapError SageFsError.describeForAgent
        | Gone _ ->
          // No session reachable — the pre-session policy applies: session
          // creation/status tools pass, code-execution tools fail closed.
          return
            Affordances.checkToolCallAllowed SessionState.Uninitialized toolName
            |> Result.mapError SageFsError.describeForAgent
      | None ->
        return
          Affordances.checkToolCallAllowed SessionState.Uninitialized toolName
          |> Result.mapError SageFsError.describeForAgent
    }

  /// Look up the workflow for a session from the Elm model.
  /// Falls back to Interactive (identity for enhancement) when the model is unavailable.
  let getWorkflowForSession (ctx: McpContext) (sid: string) : WorkflowTypes.SessionWorkflow =
    match ctx.GetElmModel with
    | Some getModel ->
      let model = getModel()
      match model.SessionContext with
      | Some sc when sc.SessionId = sid -> sc.Workflow
      | _ -> WorkflowTypes.SessionWorkflow.Interactive
    | None -> WorkflowTypes.SessionWorkflow.Interactive

  /// Format a WorkerResponse.EvalResult for display, with workflow-aware error enhancement.
  let formatWorkerEvalResult (workflow: WorkflowTypes.SessionWorkflow) (response: WorkerProtocol.WorkerResponse) : string =
    match response with
    | WorkerProtocol.WorkerResponse.EvalResult(_, result, diags, _) ->
      let diagStr =
        match List.isEmpty diags with
        | true -> ""
        | false ->
          diags
          |> List.map (fun d ->
            sprintf "  [%s] %s"
              (Features.Diagnostics.DiagnosticSeverity.label d.Severity) d.Message)
          |> String.concat "\n"
          |> sprintf "\nDiagnostics:\n%s"
      match result with
      | Ok output -> sprintf "Result: %s%s" output diagStr
      | Error err ->
        let errText = SageFsError.describeForAgent err
        let suggestion = errText |> ErrorMessages.categorize |> ErrorMessages.getSuggestion
        let enhanced = WorkflowErrorContext.enhance workflow errText suggestion
        match String.IsNullOrEmpty enhanced with
        | true -> sprintf "Error: %s%s" errText diagStr
        | false -> sprintf "Error: %s\n%s%s" errText enhanced diagStr
    | WorkerProtocol.WorkerResponse.WorkerError err ->
      sprintf "Error: %s" (SageFsError.describeForAgent err)
    | other ->
      sprintf "Unexpected response: %A" other

  type OutputFormat = Text | Json

  /// Adjust diagnostic line/column numbers in a WorkerResponse by preprocessing offsets.
  let adjustResponseDiagnostics (lineOffset: int) (colOffset: int) (response: WorkerProtocol.WorkerResponse) =
    match lineOffset, colOffset with
    | 0, 0 -> response
    | _ ->
      match response with
      | WorkerProtocol.WorkerResponse.EvalResult(rid, result, diags, meta) ->
        let adjusted =
          diags |> List.map (fun d ->
            { d with
                StartLine = Middleware.CompilationContext.mapDiagnosticLine lineOffset d.StartLine
                StartColumn = Middleware.CompilationContext.mapDiagnosticColumn colOffset d.StartColumn
                EndLine = Middleware.CompilationContext.mapDiagnosticLine lineOffset d.EndLine
                EndColumn = Middleware.CompilationContext.mapDiagnosticColumn colOffset d.EndColumn })
        WorkerProtocol.WorkerResponse.EvalResult(rid, result, adjusted, meta)
      | other -> other

  /// Evaluate a single FSI statement, dispatch Elm events, return formatted output.
  let private evalSingleStatement (ctx: McpContext) (sid: string) (format: OutputFormat) (lineOffset: int) (colOffset: int) (statement: string) : Task<string * bool> = task {
    notifyElm ctx (SageFsEvent.EvalStarted (sid, statement))
    let workflow = getWorkflowForSession ctx sid
    let! routeResult =
      routeToSession ctx sid
        (fun replyId -> WorkerProtocol.WorkerMessage.EvalCode(statement, WorkerProtocol.SessionId.value replyId))
    return
      match routeResult with
      | Ok rawResponse ->
        let response = adjustResponseDiagnostics lineOffset colOffset rawResponse
        let formatted =
          match format with
          | Json -> McpAdapter.formatWorkerEvalResultJson response
          | Text -> formatWorkerEvalResult workflow response
        match response with
        | WorkerProtocol.WorkerResponse.EvalResult(_, Ok _, diags, metadata) ->
          // A successful eval proves the session can still function.
          // If a previous TypeLoadException was recorded, clear it — the session recovered.
          match typeIdentityDiagnostics.TryRemove(sid) with
          | true, _ -> Log.info "Session %s recovered from TypeLoadException (successful eval cleared the diagnostic)" sid
          | false, _ -> ()
          notifyElm ctx (
            SageFsEvent.EvalCompleted (sid, formatted, diags |> List.map WorkerProtocol.WorkerDiagnostic.toDiagnostic))
          match metadata |> Map.tryFind "liveTestHookResult" with
          | Some json ->
            try
              let hookResult =
                WorkerProtocol.Serialization.deserialize<Features.LiveTesting.LiveTestHookResultDto> json
              match List.isEmpty hookResult.DetectedProviders with
              | false -> notifyElm ctx (SageFsEvent.ProvidersDetected hookResult.DetectedProviders)
              | true -> ()
              match Array.isEmpty hookResult.DiscoveredTests with
              | false -> notifyElm ctx (SageFsEvent.TestsDiscovered (sid, hookResult.DiscoveredTests))
              | true -> ()
              match Array.isEmpty hookResult.AffectedTestIds with
              | false -> notifyElm ctx (SageFsEvent.AffectedTestsComputed hookResult.AffectedTestIds)
              | true -> ()
            with ex -> Log.warn "Failed to deserialize hook result: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          | None -> ()
          // Live bound-value snapshot → adaptive live-bindings store (dashboard watch window).
          match metadata |> Map.tryFind "liveValueSnapshotError" with
          | Some err -> Log.warn "[Mcp.evalSingleStatement] liveValueSnapshot capture error: %s" err
          | None -> ()
          match metadata |> Map.tryFind "liveValueSnapshot" with
          | Some json ->
            try
              let snap =
                WorkerProtocol.Serialization.deserialize<Features.LiveValueTree.LiveValueSnapshot> json
              match ctx.LiveSnapshotSink with
              | Some sink ->
                sink sid { snap with SessionId = sid }
              | None -> ()
            with ex -> Log.warn "Failed to deserialize live value snapshot: %s" ex.Message
          | None -> ()
          match metadata |> Map.tryFind "assemblyLoadErrors" with
          | Some json ->
            try
              let errors =
                WorkerProtocol.Serialization.deserialize<Features.LiveTesting.AssemblyLoadError list> json
              match List.isEmpty errors with
              | false -> notifyElm ctx (SageFsEvent.AssemblyLoadFailed errors)
              | true -> ()
            with ex -> Log.warn "Failed to deserialize assembly load errors: %s\n%s" ex.Message (ex.StackTrace |> Option.ofObj |> Option.defaultValue "")
          | None -> ()
          // Success — the typed Ok outcome, not string sniffing, decides truth.
          (formatted, false)
        | WorkerProtocol.WorkerResponse.EvalResult(_, Error err, _, _) ->
          let errText = SageFsError.describe err
          // Track TypeLoadException so targeted_verify can flag the session as compromised.
          match ErrorMessages.categorize errText with
          | ErrorMessages.ErrorCategory.TypeLoad ->
            typeIdentityDiagnostics.[sid] <- errText
            Log.warn "TypeLoadException detected for session %s — type identity compromised" sid
          | _ -> ()
          notifyElm ctx (
            SageFsEvent.EvalFailed (sid, errText))
          (formatted, true)
        | _ -> (formatted, false)
      | Error msg ->
        let err = routeErrorMessage msg
        notifyElm ctx (SageFsEvent.EvalFailed (sid, err))
        (sprintf "Error: %s" err, true)
  }

  /// Evaluate F# code. Returns (formatted output, true when any statement
  /// failed) — the error flag comes from the typed worker outcome, never
  /// string sniffing. Most callers use `sendFSharpCode` (string-only view).
  let evalFSharpCodeWithOutcome
      (ctx: McpContext) (agentName: string) (code: string) (format: OutputFormat)
      (sessionId: string option) (workingDirectory: string option)
      (filePath: string option) (evalMode: string option) (blockStartLine: int option)
      (intent: string option)
      : Task<string * bool> =
    task {
      let! resolution = resolveSessionId ctx agentName sessionId workingDirectory
      match resolution with
      | Routable sid ->
        return! task {
          // Temporal dedup: skip re-evaluation if identical code was just evaluated
          let now = DateTimeOffset.UtcNow
          match Features.EvalDedup.DedupCache.tryGet evalDedupCache sid code now with
          | Some cached ->
            Log.debug "Eval dedup hit for session %s (code hash %08x)" sid (code.GetHashCode())
            Instrumentation.fsiEvals.Add(1L)
            return (cached, false)
          | None ->

          let state =
            compilationStates.GetOrAdd(sid, fun _ -> Middleware.CompilationContext.CompilationState.empty)

          let! fileStructure, updatedCache =
            match filePath with
            | Some fp -> task {
              try
                let! fs, cache =
                  Middleware.CompilationContext.parseFileStructureCached fp code state.FileCache
                return Some fs, cache
              with
              | :? System.OperationCanceledException as ex ->
                return raise ex
              | exn ->
                Log.debug "CompilationContext parse failed for %s: %s" fp exn.Message
                return None, state.FileCache
              }
            | None -> Task.FromResult(None, state.FileCache)

          let parsedMode = Middleware.CompilationContext.EvalMode.parse evalMode
          let preprocessed, updatedModules =
            Middleware.CompilationContext.preprocessForFsi
              fileStructure parsedMode blockStartLine state.EvaluatedModules code

          // Note: concurrent MCP calls for the same session could race here.
          // Blast radius is small — lost cache entry means one extra ~7ms parse,
          // lost EvaluatedModules entry means unnecessary `open` or duplicate module error.
          // Acceptable since evals are effectively serialized per session by the FSI lock.
          compilationStates.[sid] <- { state with EvaluatedModules = updatedModules; FileCache = updatedCache }

          let statements = McpAdapter.splitStatements preprocessed.Code
          Instrumentation.fsiEvals.Add(1L)
          Instrumentation.fsiStatements.Add(int64 statements.Length)
          let span = Instrumentation.startSpan Instrumentation.mcpSource "fsi.eval"
                       ["fsi.agent.name", box agentName; "fsi.statement.count", box statements.Length; "fsi.session.id", box sid]
          // Record agent activity for multi-agent coordination
          AgentActivityTracker.recordToolCall ctx.ActivityTracker agentName sid filePath intent DateTime.UtcNow

          let mutable allOutputs = []
          let mutable anyError = false
          for statement in statements do
            let! output, errored = evalSingleStatement ctx sid format preprocessed.LineOffset preprocessed.ColumnOffset statement
            allOutputs <- output :: allOutputs
            if errored then anyError <- true

          let finalOutput =
            match format with
            | Json when statements.Length > 1 ->
              let items = List.rev allOutputs |> List.map (fun s -> s) |> String.concat ","
              sprintf "[%s]" items
            | _ when statements.Length > 1 ->
              String.concat "\n\n" (List.rev allOutputs)
            | _ -> allOutputs |> List.tryHead |> Option.defaultValue ""

          Features.EvalDedup.DedupCache.record evalDedupCache sid code finalOutput (DateTimeOffset.UtcNow)
          Instrumentation.succeedSpan span
          // Compute file-overlap advisory AFTER caching raw output
          let enrichedOutput =
            match filePath with
            | Some fp ->
              let presences = AgentActivityTracker.getActivePresences ctx.ActivityTracker (Some sid) (TimeSpan.FromMinutes 5.0) DateTime.UtcNow
              let advisories = SessionOperations.FileOverlapAdvisory.compute agentName [fp] presences
              SessionOperations.CoordinationEnrichment.enrichEvalWithAdvisories advisories finalOutput
            | None -> finalOutput
          return (enrichedOutput, anyError)
        }
      | other ->
        return (sprintf "Error: %s" (formatSessionResolution other), true)
    }

  /// String-only view of evalFSharpCodeWithOutcome — keeps existing callers.
  let sendFSharpCode
      (ctx: McpContext) (agentName: string) (code: string) (format: OutputFormat)
      (sessionId: string option) (workingDirectory: string option)
      (filePath: string option) (evalMode: string option) (blockStartLine: int option)
      (intent: string option)
      : Task<string> =
    task {
      let! output, _ = evalFSharpCodeWithOutcome ctx agentName code format sessionId workingDirectory filePath evalMode blockStartLine intent
      return output
    }

  let getRecentEvents (ctx: McpContext) (agent: string) (count: int) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      return "Recent events: none recorded"
    })

  let getStatus (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    task {
      let! resolution = resolveSessionId ctx agent sessionId workingDirectory
      match resolution with
      | Gone msg ->
        // No session found — return useful status instead of an error.
        // This prevents SessionMissing friction on the most-called tool.
        let! sessions = ctx.SessionOps.GetAllSessions()
        let sessionCount = sessions |> List.length
        let availableTools = Affordances.availableTools SessionState.Uninitialized
        return
          System.Text.Json.JsonSerializer.Serialize(
            {| state = "NoSession"
               message =
                 match sessionCount with
                 | 0 -> "No sessions exist. Use create_session to load a project, or get_available_projects to discover .fsproj files."
                 | _ -> sprintf "%d session(s) exist but none matched the working directory. Use list_sessions to see them, or switch_session to select one." sessionCount
               available = availableTools |})
      | WarmingUp (sid, status) | Unroutable (sid, status) ->
        // INVARIANT (get_fsi_status is total): a session that exists but is
        // starting, restarting, or not yet routable is reported as a structured
        // "Rebuilding" state — never as a transport error and never as missing.
        let availableTools = Affordances.availableTools SessionState.WarmingUp
        return
          System.Text.Json.JsonSerializer.Serialize(
            {| state = "Rebuilding"
               sessionId = sid
               status = WorkerProtocol.SessionStatus.label status
               message = formatSessionResolution resolution
               available = availableTools |})
      | FaultedSession sid ->
        let availableTools = Affordances.availableTools SessionState.Faulted
        return
          System.Text.Json.JsonSerializer.Serialize(
            {| state = "Faulted"
               sessionId = sid
               message = formatSessionResolution resolution
               available = availableTools |})
      | Routable sid ->
        let eventCount = 0  // EventTracking removed — event count not tracked
        let! routeResult =
          routeToSession ctx sid
            (fun replyId -> WorkerProtocol.WorkerMessage.GetStatus (WorkerProtocol.SessionId.value replyId))
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.StatusResult(_, snapshot)) ->
          let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
          match info with
          | Some sessionInfo when sessionInfo.Status <> snapshot.Status ->
            do! ctx.SessionOps.UpdateSessionStatus (toSessionId sid) snapshot.Status
          | _ -> ()
          let baseStatus =
            match info with
            | Some sessionInfo ->
              let syncedInfo = { sessionInfo with Status = snapshot.Status }
              McpAdapter.formatProxyStatus sid eventCount snapshot syncedInfo ctx.McpPort
            | None ->
              let state = WorkerProtocol.SessionStatus.toSessionState snapshot.Status
              McpAdapter.formatEnhancedStatus sid eventCount state None None
          // Enrich with multi-agent coordination data
          let occupants = SessionOperations.SessionOccupancy.forSession ctx.SessionMap sid
          let guidance = SessionOperations.SessionGuidance.compute occupants snapshot.Status
          let presences = AgentActivityTracker.getActivePresences ctx.ActivityTracker (Some sid) (TimeSpan.FromMinutes 5.0) DateTime.UtcNow
          let enriched =
            baseStatus
            |> SessionOperations.CoordinationEnrichment.enrichStatusWithGuidance guidance
            |> SessionOperations.CoordinationEnrichment.enrichStatusWithPresences DateTime.UtcNow presences
          // Also record this status check as agent activity
          AgentActivityTracker.recordToolCall ctx.ActivityTracker agent sid None None DateTime.UtcNow
          return enriched
        | Ok other ->
          return sprintf "Unexpected response: %A" other
        | Error (RestartInProgress msg) ->
          // Session became unroutable mid-flight (e.g., worker swapped under us).
          // Report it as Rebuilding, not as a crash.
          let availableTools = Affordances.availableTools SessionState.WarmingUp
          return
            System.Text.Json.JsonSerializer.Serialize(
              {| state = "Rebuilding"
                 sessionId = sid
                 message = msg
                 available = availableTools |})
        | Error msg ->
          return sprintf "Error getting status: %s" (routeErrorMessage msg)
    }

  let getStartupInfo (ctx: McpContext) (agent: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      match info with
      | Some sessionInfo ->
        let header =
          sprintf "📋 Startup Information:\n- Session: %s\n- Working Directory: %s\n- Projects: %s\n- MCP Port: %d\n- Status: %s"
            sid
            sessionInfo.WorkingDirectory
            (match sessionInfo.Projects.IsEmpty with
             | true -> "None"
             | false -> String.concat ", " (sessionInfo.Projects |> List.map Path.GetFileName))
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
                  Workflow = WorkflowTypes.SessionWorkflow.Interactive
                  AutoOpenNamespaces = DirectoryConfig.autoOpenNamespacesForDirectory sessionInfo.WorkingDirectory
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
      let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
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
        return """{"status": "initializing", "message": "Session is still warming up. This typically takes 15-30s. Use get_recent_fsi_events to monitor warmup progress. Do NOT sleep-poll or create a new session."}"""
    })

  let getAvailableProjects (ctx: McpContext) (_agent: string) (workingDirectory: string option) : Task<string> =
    task {
      // Resolve working directory without requiring a session.
      // Try: explicit working_directory → active session's directory → Environment.CurrentDirectory
      let! workingDir = task {
        match workingDirectory with
        | Some wd when not (String.IsNullOrWhiteSpace wd) -> return wd
        | _ ->
          // Try to get the working directory from any active session, but don't fail if none exists
          let! sessions = ctx.SessionOps.GetAllSessions()
          match sessions with
          | [ single ] -> return single.WorkingDirectory
          | _ -> return Environment.CurrentDirectory
      }

      let projects =
        try
          Directory.EnumerateFiles(workingDir, "*.fsproj", SearchOption.AllDirectories)
          |> Seq.filter McpAdapter.isProjectFile
          |> Seq.map (fun p -> Path.GetRelativePath(workingDir, p))
          |> Seq.toArray
        with
        | :? System.OperationCanceledException -> reraise()
        | _ -> [||]

      let solutions =
        try
          Directory.EnumerateFiles workingDir
          |> Seq.filter McpAdapter.isSolutionFile
          |> Seq.map Path.GetFileName
          |> Seq.toArray
        with
        | :? System.OperationCanceledException -> reraise()
        | _ -> [||]

      return McpAdapter.formatAvailableProjects workingDir projects solutions
    }

  let loadFSharpScript (ctx: McpContext) (agentName: string) (filePath: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSession ctx agentName sessionId workingDirectory (fun sid -> task {
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.LoadScript(filePath, WorkerProtocol.SessionId.value replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.ScriptLoaded(_, Ok msg)) -> msg
        | Ok (WorkerProtocol.WorkerResponse.ScriptLoaded(_, Error err)) ->
          sprintf "Error: %s" (SageFsError.describeForAgent err)
        | Ok (WorkerProtocol.WorkerResponse.WorkerError err) ->
          sprintf "Error: %s" (SageFsError.describeForAgent err)
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  let resetSession (ctx: McpContext) (agent: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSessionAllowFaulted ctx agent sessionId workingDirectory (fun sid -> task {
      let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      let previousStatus =
        info
        |> Option.map (fun sessionInfo -> sessionInfo.Status)
        |> Option.defaultValue WorkerProtocol.SessionStatus.Ready
      do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Starting
      notifyElm ctx (
        SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Starting))
      let! routeResult =
        task {
          try
            let resetTask =
              routeToSession ctx sid
                (fun replyId -> WorkerProtocol.WorkerMessage.ResetSession (WorkerProtocol.SessionId.value replyId))
            return! resetTask.WaitAsync(Timeouts.softResetCancellation)
          with
          | :? OperationCanceledException ->
            return Result.Error (Message (sprintf "Session '%s' did not respond to reset after %A. The session may be stuck. Try recovery: use stop_session followed by create_session to force a fresh start." sid Timeouts.softResetCancellation))
        }
      match routeResult with
      | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Ok ())) ->
        do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Ready
        compilationStates.TryRemove(sid) |> ignore
        Features.EvalDedup.DedupCache.clearSession evalDedupCache sid
        notifyElm ctx (
          SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Running))
        return "Session reset successfully. All previous definitions have been cleared."
      | Ok (WorkerProtocol.WorkerResponse.ResetResult(_, Error err)) ->
        do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Faulted
        notifyElm ctx (
          SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Errored (SageFsError.describe err)))
        return sprintf "Error: %s" (SageFsError.describeForAgent err)
      | Ok other ->
        do! setSnapshotStatus ctx sid previousStatus
        return sprintf "Unexpected response: %A" other
      | Error msg ->
        let err = routeErrorMessage msg
        match routeErrorIsTransportFailure msg with
        | true ->
          do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Faulted
          notifyElm ctx (
            SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Errored err))
        | false ->
          do! setSnapshotStatus ctx sid previousStatus
        return sprintf "Error: %s" err
    })

  let checkFSharpCode (ctx: McpContext) (agent: string) (code: string) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSession ctx agent sessionId workingDirectory (fun sid -> task {
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.CheckCode(code, WorkerProtocol.SessionId.value replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.CheckResult(_, diags)) ->
          match List.isEmpty diags with
          | true -> "No errors found."
          | false ->
            let lines =
              diags
              |> List.map (fun d ->
                sprintf "[%s] (%d,%d) %s"
                  (Features.Diagnostics.DiagnosticSeverity.label d.Severity)
                  d.StartLine d.StartColumn
                  d.Message)
            let remediation =
              diags
              |> List.tryHead
              |> Option.map (fun d -> SageFs.ErrorMessages.getSuggestion (SageFs.ErrorMessages.categorize d.Message))
              |> Option.defaultValue ""
            String.concat "\n" lines + "\n\n" + remediation
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  let hardResetSession (ctx: McpContext) (agent: string) (rebuild: bool) (sessionId: string option) (workingDirectory: string option) : Task<string> =
    withSessionAllowFaulted ctx agent sessionId workingDirectory (fun sid -> task {
      notifyElm ctx (
        SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Restarting))
      match rebuild with
      | true ->
        do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Restarting
        compilationStates.TryRemove(sid) |> ignore
        typeIdentityDiagnostics.TryRemove(sid) |> ignore
        Features.EvalDedup.DedupCache.clearSession evalDedupCache sid
        notifyElm ctx (
          SageFsEvent.WarmupProgress (1, 4, "Building project..."))
        // Fire-and-forget: build + restart happens in background.
        // Return immediately so MCP tool call doesn't time out (~30s build).
        // Client polls get_fsi_status or list_sessions to check completion.
        // IMPORTANT: Always update snapshot status on both success and error,
        // and catch exceptions. Without this, a failed RestartSession silently
        // leaves the snapshot stuck in Restarting — causing subsequent tool calls
        // to fail with "still warming up" or SessionMissing friction.
        task {
          try
            // Second-line defense against competing restarts: the primary guard
            // (resolveSessionId → WarmingUp/Unroutable) stops a reader from even
            // entering hardReset while a session is starting or restarting. This
            // check covers the narrow race where this tool call was admitted just
            // before another restart marked the registry `Restarting`: if so, that
            // restart owns the recovery — do NOT schedule a competing RestartSession
            // (SessionManager serializes them, but a queued second restart would
            // discard the first one's brand-new worker for no reason).
            let! inFlightInfo = ctx.SessionOps.GetSessionInfo (toSessionId sid)
            match inFlightInfo with
            | Some i when i.Status = WorkerProtocol.SessionStatus.Restarting ->
              notifyElm ctx (
                SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Restarting))
              ()
            | _ ->
              let! result = ctx.SessionOps.RestartSession (toSessionId sid) true
              match result with
              | Ok msg ->
                do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Ready
                notifyElm ctx (
                  SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Running))
              | Error err ->
                do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Faulted
                notifyElm ctx (
                  SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Errored (SageFsError.describe err)))
          with ex ->
            do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Faulted
            notifyElm ctx (
              SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Errored (sprintf "Hard reset threw: %s" ex.Message)))
        } |> ignore
        return "Hard reset initiated — rebuilding project. Use get_fsi_status to check when ready."
      | false ->
        let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
        let previousStatus =
          info
          |> Option.map (fun sessionInfo -> sessionInfo.Status)
          |> Option.defaultValue WorkerProtocol.SessionStatus.Ready
        do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Restarting
        compilationStates.TryRemove(sid) |> ignore
        typeIdentityDiagnostics.TryRemove(sid) |> ignore
        Features.EvalDedup.DedupCache.clearSession evalDedupCache sid
        let! routeResult =
          routeToSession ctx sid
            (fun replyId -> WorkerProtocol.WorkerMessage.HardResetSession(false, WorkerProtocol.SessionId.value replyId))
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.HardResetResult(_, Ok msg)) ->
          do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Ready
          notifyElm ctx (
            SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Running))
          return msg
        | Ok (WorkerProtocol.WorkerResponse.HardResetResult(_, Error err)) ->
          do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Faulted
          notifyElm ctx (
            SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Errored (SageFsError.describe err)))
          return sprintf "Error: %s" (SageFsError.describeForAgent err)
        | Ok other ->
          do! setSnapshotStatus ctx sid previousStatus
          return sprintf "Unexpected response: %A" other
        | Error msg ->
          let err = routeErrorMessage msg
          match routeErrorIsTransportFailure msg with
          | true ->
            do! setSnapshotStatus ctx sid WorkerProtocol.SessionStatus.Faulted
            notifyElm ctx (
              SageFsEvent.SessionStatusChanged (sid, SessionDisplayStatus.Errored err))
          | false ->
            do! setSnapshotStatus ctx sid previousStatus
          return sprintf "Error: %s" err
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
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  let getCompletions (ctx: McpContext) (agent: string) (code: string) (cursorPosition: int) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.GetCompletions(code, cursorPosition, WorkerProtocol.SessionId.value replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.CompletionResult(_, completions)) ->
          match List.isEmpty completions with
          | true -> "No completions available."
          | false -> String.concat "\n" completions
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  /// Infer a conservative editor completion kind from the labels returned by
  /// the worker transport. The worker currently sends display labels only, so
  /// preserve useful VS Code metadata without pretending this is FCS glyph data.
  let completionKindForLabel (label: string) : Features.AutoCompletion.CompletionKind =
    match System.String.IsNullOrWhiteSpace label with
    | true -> Features.AutoCompletion.CompletionKind.Variable
    | false when label.Contains("(", System.StringComparison.Ordinal) ->
      Features.AutoCompletion.CompletionKind.Method
    | false when System.Char.IsUpper label[0] ->
      Features.AutoCompletion.CompletionKind.Class
    | false -> Features.AutoCompletion.CompletionKind.Variable

  /// Get structured completion items for HTTP/editor clients. Unlike the MCP
  /// tool's human-readable response, this path must always serialize as JSON.
  let getCompletionsItems
    (ctx: McpContext)
    (agent: string)
    (code: string)
    (cursorPosition: int)
    (workingDirectory: string option)
    : Task<Features.AutoCompletion.CompletionItem list> =
    task {
      let! resolution = resolveSessionId ctx agent None workingDirectory
      match resolution with
      | Routable sid ->
        let! routeResult =
          routeToSession ctx sid
            (fun replyId ->
              WorkerProtocol.WorkerMessage.GetCompletions(
                code,
                cursorPosition,
                WorkerProtocol.SessionId.value replyId))
        return
          match routeResult with
          | Ok (WorkerProtocol.WorkerResponse.CompletionResult(_, completions)) ->
            completions
            |> List.map (fun label ->
              let item : Features.AutoCompletion.CompletionItem =
                { DisplayText = label
                  ReplacementText = label
                  Kind = completionKindForLabel label
                  GetDescription = None }
              item)
          | _ -> []
      | _ -> return []
    }

  let exploreQualifiedName (ctx: McpContext) (agent: string) (qualifiedName: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let code = sprintf "%s." qualifiedName
      let cursor = code.Length
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.GetCompletions(code, cursor, WorkerProtocol.SessionId.value replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.CompletionResult(_, completions)) ->
          match List.isEmpty completions with
          | true ->
            sprintf "No members found for '%s'" qualifiedName
          | false ->
            let header = sprintf "Members of %s:" qualifiedName
            let items = completions |> List.map (sprintf "  %s") |> String.concat "\n"
            sprintf "%s\n%s" header items
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  let exploreNamespace (ctx: McpContext) (agent: string) (namespaceName: string) (workingDirectory: string option) : Task<string> =
    exploreQualifiedName ctx agent namespaceName workingDirectory

  let exploreType (ctx: McpContext) (agent: string) (typeName: string) (workingDirectory: string option) : Task<string> =
    exploreQualifiedName ctx agent typeName workingDirectory

  /// MCP tool: visualize a DU type as a state machine diagram.
  /// Sends F# code to the worker that uses reflection to extract DU cases,
  /// then renders an ASCII diagram plus JSON data.
  let visualizeDomainModel (ctx: McpContext) (agent: string) (typeName: string) (workingDirectory: string option) : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let code =
        sprintf "let _vizType = typeof<%s>\nmatch Microsoft.FSharp.Reflection.FSharpType.IsUnion(_vizType) with\n| true ->\n  let cases =\n    Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(_vizType)\n    |> Array.map (fun uc ->\n      let fields = uc.GetFields() |> Array.map (fun f -> sprintf \"%%s:%%s\" f.Name f.PropertyType.Name)\n      sprintf \"%%s|%%s\" uc.Name (String.concat \",\" fields))\n  printfn \"DUCASES:%%s\" (String.concat \";\" cases)\n| false -> printfn \"DUCASES:NOT_A_DU\"" typeName
      let! routeResult =
        routeToSession ctx sid
          (fun replyId -> WorkerProtocol.WorkerMessage.EvalCode(code, WorkerProtocol.SessionId.value replyId))
      return
        match routeResult with
        | Ok (WorkerProtocol.WorkerResponse.EvalResult(_, result, _, _)) ->
          let output =
            match result with
            | Ok s -> s
            | Error e -> sprintf "%A" e
          let lines = output.Split('\n') |> Array.map (fun s -> s.Trim())
          let duLine = lines |> Array.tryFind (fun l -> l.StartsWith("DUCASES:"))
          match duLine with
          | Some line ->
            let payload = line.Substring(8)
            match payload with
            | "NOT_A_DU" ->
              sprintf "'%s' is not a discriminated union type." typeName
            | casesStr ->
              let cases =
                casesStr.Split(';')
                |> Array.toList
                |> List.choose (fun caseStr ->
                  match caseStr.Split('|') with
                  | [| name; fieldsStr |] ->
                    let fields =
                      match fieldsStr with
                      | "" -> []
                      | fs ->
                        fs.Split(',')
                        |> Array.toList
                        |> List.choose (fun f ->
                          match f.Split(':') with
                          | [| fn; ft |] -> Some (fn, ft)
                          | _ -> None)
                    Some { Features.DomainModelViz.DUCaseInfo.Name = name; Features.DomainModelViz.DUCaseInfo.Fields = fields }
                  | _ -> None)
              let model : Features.DomainModelViz.StateMachineModel =
                { TypeName = typeName; Cases = cases; Transitions = [] }
              let data = Features.DomainModelViz.StateMachineRenderer.renderAsData model
              let opts = JsonSerializerOptions(WriteIndented = true)
              JsonSerializer.Serialize(data, opts)
          | None ->
            sprintf "Could not extract DU cases from '%s'. Output: %s" typeName output
        | Ok other -> sprintf "Unexpected response: %A" other
        | Error msg -> sprintf "Error: %s" (routeErrorMessage msg)
    })

  // ── Session Management Operations ──────────────────────────────

  /// Pure helper: given package references and the current workflow, format
  /// a non-blocking hint suggesting the user switch to WebLive if detection
  /// finds web packages. Returns None when no suggestion applies.
  /// Decoupled from .fsproj reading for testability — callers provide the list.
  let formatDetectionHint (packageRefs: string list) (currentWorkflow: WorkflowTypes.SessionWorkflow) : string option =
    match currentWorkflow with
    | WorkflowTypes.SessionWorkflow.Interactive ->
      match WorkflowTypes.WorkflowDetection.suggest packageRefs with
      | Some suggestion ->
        let pkgs = suggestion.DetectedPackages |> String.concat ", "
        Some (
          sprintf
            "💡 Detected web packages (%s). Consider switching to Live workflow for hot reload: use switch_workflow tool with target='live'"
            pkgs)
      | None -> None
    | WorkflowTypes.SessionWorkflow.WebLive _ -> None

  /// Read PackageReference Include values from a .fsproj file.
  /// Returns [] on any IO or parse error (non-blocking best-effort).
  let private readFsprojPackageRefs (path: string) : string list =
    try
      let doc = XDocument.Load(path)
      doc.Descendants(XName.Get("PackageReference"))
      |> Seq.choose (fun el ->
        match el.Attribute(XName.Get("Include")) with
        | null -> None
        | a -> Some a.Value)
      |> Seq.toList
    with _ -> []

  /// Create a new session and bind it to the requesting agent.
  let createSession (ctx: McpContext) (agent: string) (projects: string list) (workingDir: string) (workflow: WorkflowTypes.SessionWorkflow) : Task<string> =
    task {
      // Guard: warn if a session for the same project(s) already exists.
      // Compare full normalized paths so different repos with the same project
      // filename (e.g. two separate "Tests.fsproj" files) don't collide.
      let! existing = ctx.SessionOps.GetAllSessions()
      let normalizedProjects =
        projects
        |> List.map (fun p -> normalizePath (System.IO.Path.GetFullPath(p, workingDir)))
        |> Set.ofList
      let duplicates =
        existing
        |> List.filter (fun s ->
          let sessionProjects =
            s.Projects
            |> List.map (fun p -> normalizePath (System.IO.Path.GetFullPath(p, s.WorkingDirectory)))
            |> Set.ofList
          Set.intersect normalizedProjects sessionProjects |> Set.isEmpty |> not)
      match duplicates with
      | dup :: _ ->
        let sid = WorkerProtocol.SessionId.value dup.Id
        let status = WorkerProtocol.SessionStatus.label dup.Status
        return sprintf "⚠️ A session for this project already exists (session '%s', status: %s). Use switch_session to target it instead of creating a duplicate. Creating duplicate sessions causes resource starvation. If the existing session is stuck, use stop_session to remove it first, then retry create_session." sid status
      | [] ->
      let! result = ctx.SessionOps.CreateSession projects workingDir workflow
      // Refresh Elm model so dashboard SSE pushes updated session list
      ctx.Dispatch |> Option.iter (fun d -> d (SageFsMsg.Editor EditorAction.ListSessions))
      match result with
      | Result.Ok sid ->
        setActiveSessionId ctx agent sid
        // Surface workflow detection hint (non-blocking, informational only).
        let packageRefs =
          projects
          |> List.map readFsprojPackageRefs
          |> WorkflowTypes.WorkflowDetection.extractPackageNames
        match formatDetectionHint packageRefs workflow with
        | Some hint -> return sprintf "%s\n\n%s" sid hint
        | None -> return sid
      | Result.Error err -> return SageFsError.describeForAgent err
    }

  /// List all active sessions with occupancy information.
  let listSessions (ctx: McpContext) : Task<string> =
    task {
      let! sessions = ctx.SessionOps.GetAllSessions()
      let occupancyMap =
        sessions
        |> List.map (fun s ->
          WorkerProtocol.SessionId.value s.Id, SessionOperations.SessionOccupancy.forSession ctx.SessionMap (WorkerProtocol.SessionId.value s.Id))
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
      | Result.Error err -> return SageFsError.describeForAgent err
    }

  /// Switch the active session for a specific agent. Validates the target exists.
  let switchSession (ctx: McpContext) (agent: string) (sessionId: string) : Task<string> =
    task {
      match WorkerProtocol.SessionId.validate sessionId with
      | Error e -> return sprintf "Error: invalid session ID: %s" e
      | Ok validId ->
        let! info = ctx.SessionOps.GetSessionInfo validId
        match info with
        | Some _ ->
          let _prev = activeSessionId ctx agent
          setActiveSessionId ctx agent sessionId
          return sprintf "Switched to session '%s'" sessionId
        | None ->
          return sprintf "Error: Session '%s' not found" sessionId
    }

  // ── Workflow Switching ──────────────────────────────────────────

  /// Switch the workflow mode of a session (Interactive ↔ WebLive).
  /// Creates a new session with the target workflow and stops the old one.
  let switchWorkflow
    (ctx: McpContext)
    (agent: string)
    (workingDirectory: string option)
    (targetStr: string)
    (dryRun: bool)
    : Task<string> =
    task {
      // 1. Parse target workflow
      let targetOpt =
        match targetStr.ToLowerInvariant().Trim() with
        | "interactive" | "repl" -> Some WorkflowTypes.SessionWorkflow.Interactive
        | "weblive" | "live" ->
          Some (WorkflowTypes.SessionWorkflow.WebLive WorkflowTypes.BrowserRefreshConfig.defaults)
        | _ -> None
      match targetOpt with
      | None ->
        return sprintf "Error: unknown workflow '%s'. Valid values: 'interactive' (REPL), 'weblive' (Live)" targetStr
      | Some target ->
      // 2. Resolve session from working directory
      let! resolution = resolveSessionId ctx agent None workingDirectory
      match resolution with
      | WarmingUp _ | Unroutable _ | FaultedSession _ | Gone _ as other ->
        return sprintf "Error: %s" (formatSessionResolution other)
      | Routable sid ->
      // 3. Get session info for current workflow
      let validId = toSessionId sid
      let! info = ctx.SessionOps.GetSessionInfo validId
      match info with
      | None -> return sprintf "Error: session '%s' not found" sid
      | Some sessionInfo ->
      let current = sessionInfo.Workflow
      let cost = WorkflowTypes.TransitionCost.compute 0 0 false
      let opts = JsonSerializerOptions(WriteIndented = true)
      let prevLabel = WorkflowTypes.SessionWorkflow.label current
      let targetLabel = WorkflowTypes.SessionWorkflow.label target
      let serializeOutcome outcome =
        match outcome with
        | WorkflowTypes.WorkflowSwitchOutcome.AlreadyActive (c, msg) ->
          JsonSerializer.Serialize(
            {| Outcome = "alreadyActive"
               PreviousWorkflow = prevLabel
               TargetWorkflow = targetLabel
               Cost = c; Switched = false
               NewSessionId = (None: string option)
               Message = msg |}, opts)
        | WorkflowTypes.WorkflowSwitchOutcome.DryRunPreview (c, msg) ->
          JsonSerializer.Serialize(
            {| Outcome = "dryRunPreview"
               PreviousWorkflow = prevLabel
               TargetWorkflow = targetLabel
               Cost = c; Switched = false
               NewSessionId = (None: string option)
               Message = msg |}, opts)
        | WorkflowTypes.WorkflowSwitchOutcome.Executed (_, _, c, sid, msg) ->
          JsonSerializer.Serialize(
            {| Outcome = "executed"
               PreviousWorkflow = prevLabel
               TargetWorkflow = targetLabel
               Cost = c; Switched = true
               NewSessionId = Some sid
               Message = msg |}, opts)
      // 4. If same workflow kind, no-op
      match WorkflowTypes.SessionWorkflow.label current = WorkflowTypes.SessionWorkflow.label target with
      | true ->
        let outcome = WorkflowTypes.WorkflowSwitchOutcome.alreadyInWorkflow current cost
        return serializeOutcome outcome
      | false ->
      // 5. If dry run, return preview only
      match dryRun with
      | true ->
        let outcome = WorkflowTypes.WorkflowSwitchOutcome.preview current target cost
        return serializeOutcome outcome
      | false ->
      // 6. Execute: create new session with target workflow, stop old
      let! createResult =
        ctx.SessionOps.CreateSession sessionInfo.Projects sessionInfo.WorkingDirectory target
      match createResult with
      | Result.Error err ->
        return sprintf "Error switching workflow: %s" (SageFsError.describeForAgent err)
      | Result.Ok newSid ->
        let! _ = ctx.SessionOps.StopSession sid
        setActiveSessionId ctx agent newSid
        ctx.Dispatch |> Option.iter (fun d -> d (SageFsMsg.Editor EditorAction.ListSessions))
        let outcome = WorkflowTypes.WorkflowSwitchOutcome.switched current target cost newSid
        return serializeOutcome outcome
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
        match regions.IsEmpty with
        | true ->
          return "No render regions available."
        | false ->
          return
            regions
            |> List.map (fun r ->
              let header =
                sprintf "── %s [%s] ──" r.Id (formatRegionFlags r.Flags)
              match String.IsNullOrWhiteSpace r.Content with
              | true -> header
              | false -> sprintf "%s\n%s" header r.Content)
            |> String.concat "\n\n"
    }

  // ── Live Testing MCP Tools ──────────────────────────────────

  let liveTestJsonOpts =
    let o = JsonSerializerOptions(WriteIndented = false)
    o.Converters.Add(JsonFSharpConverter())
    o

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

  let getLiveTestStatus (ctx: McpContext) (agentName: string) (fileFilter: string option) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Live testing not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let state = model.LiveTesting.TestState
        let discoveryState = Features.LiveTesting.LiveTestState.discoveryState state
        let discoveryRequiresEval = Features.LiveTesting.LiveTestState.requiresPrimingEval state
        // Prefer per-client session from SessionMap; fall back to global active session.
        // This prevents session A's tests from bleeding into session B's view when the
        // daemon-global active session differs from the calling client's current session.
        let activeId =
          let perClient = activeSessionId ctx agentName
          match perClient <> "" with
          | true -> perClient
          | false ->
            ActiveSession.sessionId model.Sessions.ActiveSessionId
            |> Option.map WorkerProtocol.SessionId.value
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
        let resp = System.Collections.Generic.Dictionary<string, obj>()
        resp["Enabled"] <- box (state.Activation = Features.LiveTesting.LiveTestingActivation.Active)
        resp["Summary"] <- box summary
        resp["DiscoveryState"] <- box (Features.LiveTesting.LiveTestDiscoveryState.toWireValue discoveryState)
        resp["DiscoveryHint"] <- box (Features.LiveTesting.LiveTestState.discoveryHint state)
        resp["DiscoveryRequiresEval"] <- box discoveryRequiresEval
        match state.LastDecision with
        | Some decision -> resp["LastDecision"] <- box (Features.LiveTesting.LiveTestingDecision.toWireModel decision)
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
            |> Seq.map (fun (_, bm) -> Features.LiveTesting.CoverageBitmap.popCount bm)
            |> Seq.averageBy float
          resp["CoverageBitmapStats"] <- box {| TestsWithCoverage = bitmapCount; AvgHitProbes = avgProbes |}
        | false -> ()
        let failedEntries = match tests with | Some t -> t | None -> sessionEntries
        let failedTests =
          failedEntries
          |> Array.choose (fun e ->
            match e.Status with
            | Features.LiveTesting.TestRunStatus.Failed (failure, duration) ->
              let msg =
                match failure with
                | Features.LiveTesting.TestFailure.AssertionFailed m -> m
                | Features.LiveTesting.TestFailure.ExceptionThrown (m, _) -> m
                | Features.LiveTesting.TestFailure.TimedOut after -> sprintf "Timed out after %dms" (int after.TotalMilliseconds)
              let location =
                match failure with
                | Features.LiveTesting.TestFailure.ExceptionThrown (_, st) ->
                  FailureLocationParser.tryParse st
                  |> Option.map (fun fl -> {| File = fl.FilePath; Line = fl.Line |})
                | _ -> None
              Some {| Name = e.DisplayName; Message = msg; DurationMs = int duration.TotalMilliseconds; Location = location |}
            | _ -> None)
          |> Array.truncate 20
        match failedTests.Length > 0 with
        | true -> resp["FailedTests"] <- box failedTests
        | false -> ()
        return JsonSerializer.Serialize(resp, liveTestJsonOpts)
    }

  let setLiveTesting (ctx: McpContext) (enabled: bool) : Task<string> =
    task {
      match ctx.Dispatch with
      | None -> return "Cannot set live testing — Elm loop not started."
      | Some dispatch ->
        let msg = match enabled with | true -> SageFsMsg.EnableLiveTesting | false -> SageFsMsg.DisableLiveTesting
        dispatch msg
        match enabled with
        | false ->
          return "Live testing disabled."
        | true ->
          match ctx.GetElmModel with
          | Some getModel ->
            let state = (getModel ()).LiveTesting.TestState
            let discovered = state.DiscoveredTests.Length
            match discovered > 0 with
            | true ->
              return sprintf "Live testing enabled. %d tests already discovered. Use get_live_test_status to confirm current DiscoveryState." discovered
            | false ->
              return "Live testing enabled. Initial discovery now runs asynchronously; use get_live_test_status to confirm DiscoveryState."
          | None ->
            return "Live testing enabled. Initial discovery now runs asynchronously; use get_live_test_status to confirm DiscoveryState."
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

  let markAllTestsStale (ctx: McpContext) : Task<string> =
    task {
      match ctx.Dispatch with
      | None -> return "Cannot mark tests stale — Elm loop not started."
      | Some dispatch ->
        dispatch SageFsMsg.MarkAllTestsStale
        match ctx.GetElmModel with
        | Some getModel ->
          let count = (getModel ()).LiveTesting.TestState.DiscoveredTests.Length
          return sprintf "All %d test results marked stale." count
        | None ->
          return "All test results marked stale."
    }

  let setTestTimeouts(_ctx: McpContext) (perTestSeconds: float option) (globalRunSeconds: float option) : Task<string> =
    task {
      let mutable error = None
      let parts = System.Collections.Generic.List<string>()
      match perTestSeconds with
      | Some s when s > 0.0 ->
        Timeouts.setPerTestTimeout (TimeSpan.FromSeconds s)
        parts.Add (sprintf "Per-test timeout: %.1fs" s)
      | Some s -> error <- Some (sprintf "Invalid per-test timeout: %.1f (must be > 0)" s)
      | None -> ()
      match globalRunSeconds with
      | Some s when s > 0.0 ->
        Timeouts.setGlobalTestRunTimeout (TimeSpan.FromSeconds s)
        parts.Add (sprintf "Global run timeout: %.1fs" s)
      | Some s -> error <- Some (sprintf "Invalid global run timeout: %.1f (must be > 0)" s)
      | None -> ()
      match error with
      | Some e -> return e
      | None ->
        match parts.Count with
        | 0 ->
          return sprintf "Current timeouts — per-test: %.1fs, global run: %.1fs. Provide per_test_seconds and/or global_run_seconds to change."
            (Timeouts.perTestDefault().TotalSeconds) (Timeouts.globalTestRun().TotalSeconds)
        | _ ->
          parts.Add (sprintf "(effective immediately for next test run)")
          return parts |> Seq.toList |> String.concat ". "
    }

  let getTestTrace (ctx: McpContext) : Task<string> =
    match ctx.GetElmModel with
    | None -> Task.FromResult "Test trace not available — Elm loop not started."
    | Some getModel ->
      let model = getModel ()
      let state = model.LiveTesting.TestState
      let activeId =
        ActiveSession.sessionId model.Sessions.ActiveSessionId
        |> Option.map WorkerProtocol.SessionId.value
        |> Option.defaultValue ""
      let sessionEntries =
        Features.LiveTesting.LiveTestState.statusEntriesForSession activeId state
      let summary =
        Features.LiveTesting.TestSummary.fromStatuses
          state.Activation (sessionEntries |> Array.map (fun e -> e.Status))
      let timing = model.LiveTesting.LastTiming
      let isActive = state.Activation = Features.LiveTesting.LiveTestingActivation.Active
      let discoveryState = Features.LiveTesting.LiveTestState.discoveryState state
      let discoveryRequiresEval = Features.LiveTesting.LiveTestState.requiresPrimingEval state
      let lastDecision =
        state.LastDecision
        |> Option.map Features.LiveTesting.LiveTestingDecision.toWireModel
      let resp = {|
        Enabled = isActive
        IsRunning = Features.LiveTesting.TestRunPhase.isAnyRunning state.RunPhases
        History = state.History
        Summary = summary
        DiscoveryState = Features.LiveTesting.LiveTestDiscoveryState.toWireValue discoveryState
        DiscoveryHint = Features.LiveTesting.LiveTestState.discoveryHint state
        DiscoveryRequiresEval = discoveryRequiresEval
        LastDiscoveryTime =
          match state.LastDiscoveryTime > System.DateTimeOffset.MinValue with
          | true -> Some state.LastDiscoveryTime
          | false -> None
        LastDecision = lastDecision
        Timing = timing |> Option.map Features.LiveTesting.TestCycleTiming.toStatusBar |> Option.defaultValue "no timing yet"
        Providers = state.DetectedProviders |> List.map (fun p ->
          match p with
          | Features.LiveTesting.ProviderDescription.AttributeBased a -> Features.LiveTesting.TestFramework.toString a.Name
          | Features.LiveTesting.ProviderDescription.Custom c -> Features.LiveTesting.TestFramework.toString c.Name)
        Policies = state.RunPolicies |> Map.toList |> List.map (fun (c, p) -> sprintf "%A: %A" c p)
        Hint = match isActive with
               | true -> None
               | false -> Some "Live testing is not active. Call enable_live_testing to start test discovery and automatic re-runs."
      |}
      Task.FromResult (JsonSerializer.Serialize(resp, liveTestJsonOpts))

  let explainTestRun (ctx: McpContext) (testName: string) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Explain not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let graph = model.LiveTesting.DepGraph
        let testState = model.LiveTesting.TestState
        let trigger = model.LiveTesting.LastTrigger
        let changedSymbols = model.LiveTesting.ChangedSymbols
        let matchingTests =
          testState.DiscoveredTests
          |> Array.filter (fun tc ->
            tc.FullName.Contains(testName, StringComparison.OrdinalIgnoreCase)
            || tc.DisplayName.Contains(testName, StringComparison.OrdinalIgnoreCase))
        match matchingTests with
        | [||] -> return sprintf "No test found matching '%s'. Use get_live_test_status to list tests." testName
        | tests ->
          let explanations =
            tests
            |> Array.map (Features.LiveTesting.TestRunExplainer.explainTest
              graph testState.LastResults testState.FlakyHistory changedSymbols trigger)
          let resp = {|
            MatchCount = explanations.Length
            Explanations = explanations |> Array.map (fun e ->
              let reasonStr =
                match e.Reason with
                | Features.LiveTesting.TestTriggerReason.SymbolCoverage syms ->
                  sprintf "Symbol coverage: %s" (String.concat ", " syms)
                | Features.LiveTesting.TestTriggerReason.NewTest -> "New test (no prior results)"
                | Features.LiveTesting.TestTriggerReason.ExplicitRun -> "Explicitly triggered"
                | Features.LiveTesting.TestTriggerReason.UnknownCoverage -> "Unknown coverage (dep graph fallback)"
              {| TestId = Features.LiveTesting.TestId.value e.TestId
                 DisplayName = e.DisplayName
                 Reason = reasonStr
                 CoveringSymbols = e.CoveringSymbols
                 Trigger = sprintf "%A" e.Trigger
                 DurationMs = e.DurationMs
                 FlakyClassification =
                   match e.FlakyClassification with
                   | Features.LiveTesting.FlakyClassification.Insufficient -> "insufficient"
                   | Features.LiveTesting.FlakyClassification.Stable -> "stable"
                   | Features.LiveTesting.FlakyClassification.Environmental n -> sprintf "environmental(%d flips)" n
                   | Features.LiveTesting.FlakyClassification.PropertyCounterexample ce -> sprintf "property-counterexample: %s" ce
                 IsFlaky =
                   match e.FlakyClassification with
                   | Features.LiveTesting.FlakyClassification.Environmental _ -> true
                   | Features.LiveTesting.FlakyClassification.PropertyCounterexample _ -> true
                   | _ -> false |})
            ChangedSymbols = changedSymbols
          |}
          return JsonSerializer.Serialize(resp, liveTestJsonOpts)
    }

  let queryTestCoverage (ctx: McpContext) (symbol: string) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Coverage query not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let graph = model.LiveTesting.DepGraph
        let testState = model.LiveTesting.TestState
        let coveringTests =
          Features.LiveTesting.TestRunExplainer.queryTestCoverage
            graph testState.DiscoveredTests testState.LastResults symbol
        let resp = {|
          Symbol = symbol
          CoveringTestCount = coveringTests.Length
          Tests = coveringTests |> Array.map (fun ct ->
            let resultStr =
              match ct.Result with
              | Some (Features.LiveTesting.TestResult.Passed d) -> sprintf "Passed (%.0fms)" d.TotalMilliseconds
              | Some (Features.LiveTesting.TestResult.Failed (_, d)) -> sprintf "Failed (%.0fms)" d.TotalMilliseconds
              | Some (Features.LiveTesting.TestResult.Skipped r) -> sprintf "Skipped: %s" r
              | Some Features.LiveTesting.TestResult.NotRun -> "Not run"
              | None -> "No result"
            {| TestId = Features.LiveTesting.TestId.value ct.TestId
               DisplayName = ct.DisplayName
               LastResult = resultStr |})
        |}
        return JsonSerializer.Serialize(resp, liveTestJsonOpts)
    }

  /// Format file-level coverage annotations as JSON for the get_file_coverage MCP tool.
  /// Pure function: takes FileAnnotations + LiveTestState, returns JSON string.
  let formatFileCoverageResponse (annotations: Features.LiveTesting.FileAnnotations) (testState: Features.LiveTesting.LiveTestState) : string =
    let testNameFor (tid: Features.LiveTesting.TestId) =
      testState.DiscoveredTests
      |> Array.tryFind (fun dt -> dt.Id = tid)
      |> Option.map (fun dt -> dt.DisplayName)
      |> Option.defaultValue (Features.LiveTesting.TestId.value tid)
    let lines =
      annotations.CoverageAnnotations
      |> Array.map (fun ca ->
        let covered, testCount, health =
          match ca.Detail with
          | Features.LiveTesting.CoverageStatus.Covered (cnt, h) ->
            true, cnt,
            (match h with
             | Features.LiveTesting.CoverageHealth.AllPassing -> "AllPassing"
             | Features.LiveTesting.CoverageHealth.SomeFailing -> "SomeFailing")
          | Features.LiveTesting.CoverageStatus.NotCovered -> false, 0, "NotCovered"
          | Features.LiveTesting.CoverageStatus.Pending -> false, 0, "Pending"
        let branchObj : obj =
          match ca.BranchCoverage with
          | Some Features.LiveTesting.LineCoverage.FullyCovered ->
            {| Case = "FullyCovered" |} :> obj
          | Some (Features.LiveTesting.LineCoverage.PartiallyCovered (c, t)) ->
            {| Case = "PartiallyCovered"; Covered = c; Total = t |} :> obj
          | Some Features.LiveTesting.LineCoverage.NotCovered ->
            {| Case = "NotCovered" |} :> obj
          | None ->
            {| Case = "Unknown" |} :> obj
        let coveringTests = ca.CoveringTestIds |> Array.map testNameFor
        {| Line = ca.Line; EndLine = ca.EndLine; EndColumn = ca.EndColumn
           Covered = covered; TestCount = testCount; Health = health
           CoveringTests = coveringTests; BranchCoverage = branchObj |})
    let coveredCount = lines |> Array.filter (fun l -> l.Covered) |> Array.length
    let totalCount = lines.Length
    let pct =
      match totalCount with
      | 0 -> 0.0
      | n -> System.Math.Round(float coveredCount / float n * 100.0, 1)
    let resp = {|
      FilePath = annotations.FilePath
      Lines = lines
      Summary = {|
        CoveredLines = coveredCount
        TotalLines = totalCount
        CoveragePercent = pct
      |}
    |}
    JsonSerializer.Serialize(resp, liveTestJsonOpts)

  /// MCP tool: get per-line coverage data for a specific file.
  /// Resolves partial file paths, then computes line-level coverage from
  /// instrumentation bitmaps + dep graph fallback.
  let getFileCoverage (ctx: McpContext) (filePath: string) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "File coverage not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let cycleState = model.LiveTesting
        let testState = cycleState.TestState
        let entries =
          Features.LiveTesting.LiveTestState.statusEntriesForSession "" testState
        let resolvedPath =
          Features.LiveTesting.FileAnnotations.resolveFilePath
            filePath entries cycleState.InstrumentationMaps
        match resolvedPath with
        | None ->
          let resp = {| FilePath = filePath; Error = "File not found in test sources or instrumentation maps" |}
          return JsonSerializer.Serialize(resp, liveTestJsonOpts)
        | Some fullPath ->
          let annotations = Features.LiveTesting.FileAnnotations.projectWithCoverage fullPath cycleState
          return formatFileCoverageResponse annotations testState
    }

  /// Build a CellGraph from the current FeaturePushState.
  let private buildCellGraphFromState (state: Features.FeatureHooks.FeaturePushState) : Features.CellDependencyGraph.CellGraph =
    let cells =
      state.EvalHistory
      |> List.map (fun e ->
        Features.CellDependencyGraph.analyzeCell state.KnownBindings e.CellIndex e.Code e.Result)
    Features.CellDependencyGraph.buildGraph cells

  /// Convert BindingScopeSnapshot active bindings to Ghostwriter ScopeBinding list.
  let private toScopeBindings (snapshot: Features.BindingExplorer.BindingScopeSnapshot) : Features.ScopeBinding list =
    snapshot.ActiveBindings
    |> Map.toList
    |> List.map (fun (_key, info) ->
      { Features.ScopeBinding.Name = info.Name
        TypeSig = info.TypeSig
        Value = info.Value })

  /// Convert a CoverageVerdict to its JSON string representation.
  let private verdictString (v: Features.CoverageIntel.CoverageVerdict) =
    match v with
    | Features.CoverageIntel.WellCovered -> "WellCovered"
    | Features.CoverageIntel.PartialBlindSpot -> "PartialBlindSpot"
    | Features.CoverageIntel.DiagnosticBlindSpot -> "DiagnosticBlindSpot"

  /// Convert a CoverageIntelReport to a JSON-serializable anonymous record.
  let toCoverageIntelJson (report: Features.CoverageIntel.CoverageIntelReport) =
    {| CoveragePercent = report.CoveragePercent
       CoveredBranches = report.CoveredBranches
       TotalBranches = report.TotalBranches
       Verdict = verdictString report.Verdict
       BlindSpots =
         report.BlindSpots |> List.map (fun g ->
           {| FilePath = g.FilePath
              Line = g.Line
              EndLine = g.EndLine
              BranchId = g.BranchId
              NearestCoveredLine = g.NearestCoveredLine |})
       CorrelatedFailures =
         report.CorrelatedFailures |> List.map Features.LiveTesting.TestId.value
       Summary = Features.CoverageIntel.CoverageIntel.summarize report |}

  let explainTestFailure (ctx: McpContext) (testName: string) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Failure narrative not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let testState = model.LiveTesting.TestState
        let allMaps =
          model.LiveTesting.InstrumentationMaps
          |> Map.values
          |> Seq.collect id
          |> Array.ofSeq
        let bitmaps = testState.TestCoverageBitmaps
        let depGraph = model.LiveTesting.DepGraph
        let hasMaps = allMaps.Length > 0
        let matchingTests =
          testState.DiscoveredTests
          |> Array.filter (fun tc ->
            tc.FullName.Contains(testName, StringComparison.OrdinalIgnoreCase)
            || tc.DisplayName.Contains(testName, StringComparison.OrdinalIgnoreCase))
        match matchingTests with
        | [||] -> return sprintf "No test found matching '%s'." testName
        | tests ->
          let narratives =
            tests
            |> Array.choose (fun tc ->
              Map.tryFind tc.Id testState.Cached.FailureNarratives
              |> Option.map (fun (n: Features.LiveTesting.FailureNarrative) ->
                let changes =
                  n.CausalChanges |> List.map (fun c ->
                    match c with
                    | Features.LiveTesting.CausalChange.SymbolChanged s -> {| Kind = "symbol"; Name = s |}
                    | Features.LiveTesting.CausalChange.FileChanged f -> {| Kind = "file"; Name = f |}
                    | Features.LiveTesting.CausalChange.Unknown -> {| Kind = "unknown"; Name = "" |})
                let propViolation =
                  n.PropertyViolation |> Option.map (fun pv ->
                    {| PropertyName = pv.PropertyName
                       ShrunkCounterexample = pv.ShrunkCounterexample
                       AlgebraicCategory = pv.AlgebraicCategory |})
                let coverageIntel =
                  match hasMaps with
                  | false -> None
                  | true ->
                    let causalFiles =
                      n.CausalChanges
                      |> List.choose (fun c ->
                        match c with
                        | Features.LiveTesting.CausalChange.FileChanged f -> Some f
                        | _ -> None)
                    let report =
                      Features.CoverageIntel.CoverageIntel.composeForFailure
                        tc.Id tc.DisplayName n causalFiles allMaps bitmaps depGraph
                    Some (toCoverageIntelJson report)
                {| TestId = Features.LiveTesting.TestId.value tc.Id
                   DisplayName = tc.DisplayName
                   Summary = n.Summary
                   LastPassedAt = n.LastPassedAt
                   TimeSinceLastPass = n.TimeSinceLastPass |> Option.map (fun ts -> ts.TotalSeconds)
                   CausalChanges = changes
                   PropertyViolation = propViolation
                   CoverageIntel = coverageIntel |}))
          match narratives with
          | [||] ->
            let failingCount =
              tests |> Array.filter (fun tc ->
                match Map.tryFind tc.Id testState.LastResults with
                | Some r ->
                  match r.Result with
                  | Features.LiveTesting.TestResult.Failed _ -> true
                  | _ -> false
                | None -> false) |> Array.length
            match failingCount with
            | 0 -> return sprintf "Test(s) matching '%s' are not currently failing — no narrative available." testName
            | _ -> return sprintf "Test(s) matching '%s' are failing but no narrative was computed (may not have transitioned from passing)." testName
          | narrs ->
            // Enrich with diagnostic report if feature state is available
            let diagnostics =
              match ctx.GetFeatureState with
              | Some getState ->
                let state = getState ()
                let graph = buildCellGraphFromState state
                let failuresForDiag =
                  tests
                  |> Array.choose (fun tc ->
                    Map.tryFind tc.Id testState.Cached.FailureNarratives
                    |> Option.map (fun n -> (tc.Id, tc.DisplayName, n)))
                  |> Array.toList
                let scopeBindings =
                  match state.CachedScope with
                  | Some snapshot -> toScopeBindings snapshot
                  | None -> []
                let report =
                  Features.Diagnostician.Diagnostician.compose
                    graph failuresForDiag scopeBindings state.CachedTimeline
                Some {| Severity = report.Severity.ToString()
                        AffectedCells = report.AffectedCells
                        SuggestionCount = report.SuggestedFixes.Length
                        TopSuggestions =
                          report.SuggestedFixes
                          |> List.truncate 3
                          |> List.map (fun s -> {| Code = s.Code; Explanation = s.Explanation |})
                        Performance =
                          report.PerformanceContext
                          |> Option.map (fun s -> {| Sparkline = s.Sparkline; P50Ms = s.P50Ms; P95Ms = s.P95Ms |})
                        Summary = report.Summary |}
              | None -> None
            let resp = {| MatchCount = narrs.Length; Narratives = narrs; Diagnostics = diagnostics |}
            return JsonSerializer.Serialize(resp, liveTestJsonOpts)
    }



  let targetedVerify
    (ctx: McpContext)
    (agent: string)
    (workingDirectory: string option)
    (behavior: string)
    (exactGuard: string option)
    : Task<string> =
    withSessionWd ctx agent workingDirectory (fun sid -> task {
      let! info = ctx.SessionOps.GetSessionInfo (toSessionId sid)
      let status = info |> Option.map (fun session -> session.Status)
      let loadedState,
          sessionLoadedState =
        match ctx.GetElmModel |> Option.map (fun getModel -> (getModel ()).SessionContext) |> Option.flatten with
        | Some sessionCtx ->
          let statuses =
            match sessionCtx.SessionId = sid with
            | true -> sessionCtx.FileStatuses
            | false -> []
          match statuses |> List.tryFind (fun file -> file.Readiness = FileReadiness.Stale) with
          | Some stale ->
            let lastLoaded = stale.LastLoadedAt |> Option.map string |> Option.defaultValue "unknown-loaded-version"
            let state = Features.Verification.LoadedDefinitionState.ConfirmedStale (stale.Path, lastLoaded)
            state, Some state
          | None ->
            let artifact =
              statuses
              |> List.filter (fun file -> file.Readiness = FileReadiness.Loaded)
              |> List.map (fun file -> file.Path)
              |> function
                 | [] -> behavior
                 | files -> String.concat ", " files
            let state = Features.Verification.LoadedDefinitionState.ConfirmedCurrent artifact
            state, Some state
        | None ->
          let state = Features.Verification.LoadedDefinitionState.UnknownLoadState "warmup file status unavailable"
          state, None
      let exactGuardRef =
        exactGuard
        |> Option.bind (fun raw ->
          match Features.Verification.ExactTestRef.create raw with
          | Ok exact -> Some exact
          | Error _ -> None)
      let sessionObservation : Features.Verification.SessionTrust.SessionObservation =
        { MatchingSessionIds = [ sid ]
          SessionStatus = status
          LoadedState = sessionLoadedState
          TypeIdentityDiagnostic =
            match typeIdentityDiagnostics.TryGetValue(sid) with
            | true, diag -> Some diag
            | _ -> None }
      let request : Features.Verification.TargetedVerificationRequest =
        { Intent =
            Features.Verification.VerificationIntent.VerifyChangedBehavior (behavior, Features.Verification.RegressionRisk.SharedContract)
          NamedGuard = exactGuardRef
          SessionObservation = sessionObservation
          LoadedState = loadedState }
      let report =
        Features.Verification.TargetedVerification.createReport
          request
          None
          None
      return Features.Verification.TargetedVerification.summarize report
    })

  // ── Feature Analysis MCP Tools (P15–P19) ─────────────────────

  /// Convert EvalHistory to FilmstripEvent list.
  let private toFilmstripEvents (state: Features.FeatureHooks.FeaturePushState) : Features.FilmstripEvent list =
    state.EvalHistory
    |> List.rev
    |> List.map (fun e ->
      { Features.FilmstripEvent.Timestamp = e.Timestamp
        Label = e.Code |> Features.FsiOutputParser.detectBoundaryKind |> Features.FsiOutputParser.EvalBoundaryKind.toLabel
        BindingCount = state.KnownBindings.Count
        TestSummary = None
        EvalDurationMs = Some (float e.DurationMs) })

  let decomposePipeline (code: string) : Task<string> =
    task {
      let stages = Features.EvalLens.decomposePipeline code
      match stages with
      | [] -> return "No pipeline stages found in the provided code."
      | stages ->
        let classifications =
          stages |> List.map (fun s -> s, Features.EvalLens.classifyStage s.Code)
        return
          classifications
          |> List.map (fun (stage, classification) ->
            let icon =
              match classification with
              | Features.Pure -> "●"
              | Features.Effectful -> "⚡"
              | Features.Unknown -> "?"
            sprintf "  %d. %s %s" stage.StageIndex icon (stage.Code.Trim()))
          |> fun lines ->
            sprintf "Pipeline decomposition (%d stages):\n%s" stages.Length (String.concat "\n" lines)
    }

  let planRipple (ctx: McpContext) (changedCellIds: string) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Feature state not available — no active session."
      | Some getState ->
        let state = getState ()
        match state.EvalHistory with
        | [] -> return "No eval history — evaluate some cells first."
        | _ ->
          let graph = buildCellGraphFromState state
          let cellIds =
            changedCellIds.Split([| ','; ' ' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun s -> match System.Int32.TryParse(s) with | true, v -> Some v | _ -> None)
            |> Set.ofArray
          match cellIds.IsEmpty with
          | true -> return "No valid cell IDs provided. Use comma-separated integers (e.g., '0,2,5')."
          | false ->
            let plan = Features.EvalRipple.planRipple graph cellIds
            return
              plan.Steps
              |> List.map (fun step ->
                sprintf "  [%d] %s — %s"
                  step.CellId
                  (step.Code |> fun c -> match c.Length > 50 with | true -> c.[..47] + "..." | false -> c)
                  (match step.Status with
                   | Features.Pending -> "pending"
                   | Features.Evaluating -> "evaluating"
                   | Features.Succeeded o -> sprintf "ok: %s" o
                   | Features.Failed e -> sprintf "FAILED: %s" e
                   | Features.Skipped r -> sprintf "skipped: %s" r))
              |> fun lines ->
                sprintf "Ripple plan (%d steps, %d changed):\n%s"
                  plan.Steps.Length cellIds.Count (String.concat "\n" lines)
    }

  let previewWhatIf (ctx: McpContext) (bindingName: string) (newCode: string) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Feature state not available — no active session."
      | Some getState ->
        let state = getState ()
        match state.EvalHistory with
        | [] -> return "No eval history — evaluate some cells first."
        | _ ->
          let graph = buildCellGraphFromState state
          let scope =
            state.CachedScope
            |> Option.defaultWith (fun () -> Features.FeatureHooks.buildScopeFromState state)
          let existingBinding = scope.ActiveBindings |> Map.tryFind bindingName
          let original =
            existingBinding
            |> Option.map (fun b -> b.Value |> Option.defaultValue "?")
            |> Option.defaultValue "?"
          let typeSig =
            existingBinding
            |> Option.map (fun b -> b.TypeSig)
            |> Option.defaultValue "obj"
          let override' = Features.WhatIf.createOverride bindingName original newCode typeSig
          let plan = Features.WhatIf.planWhatIf graph override'
          return
            [ sprintf "What-If: %s" (Features.WhatIf.formatOverride override')
              sprintf "Affected cells: %d" plan.AffectedCells.Length
              yield!
                plan.RippleSteps
                |> List.map (fun step ->
                  sprintf "  [%d] %s" step.CellId
                    (step.Code |> fun c -> match c.Length > 50 with | true -> c.[..47] + "..." | false -> c)) ]
            |> String.concat "\n"
    }

  let suggestNextCell (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Feature state not available — no active session."
      | Some getState ->
        let state = getState ()
        let scope =
          state.CachedScope
          |> Option.defaultWith (fun () -> Features.FeatureHooks.buildScopeFromState state)
        let bindings = toScopeBindings scope
        match bindings with
        | [] -> return "No bindings in scope — evaluate some cells first."
        | _ ->
          let suggestions = Features.Ghostwriter.suggest bindings
          match suggestions with
          | [] -> return "No suggestions available for the current bindings."
          | _ ->
            return
              suggestions
              |> List.map (fun s ->
                sprintf "  %.0f%% %s — %s" (s.Confidence * 100.0) s.Code s.Explanation)
              |> fun lines ->
                sprintf "Ghostwriter suggestions (%d):\n%s" suggestions.Length (String.concat "\n" lines)
    }

  let getSessionFilmstrip (ctx: McpContext) (filter: string option) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Feature state not available — no active session."
      | Some getState ->
        let state = getState ()
        let events = toFilmstripEvents state
        match events with
        | [] -> return "No eval history — the session filmstrip is empty."
        | _ ->
          let frames = Features.SessionFilmstrip.buildFilmstrip events
          let filtered =
            match filter with
            | Some q when q <> "" -> Features.SessionFilmstrip.filterFrames q frames
            | _ -> frames
          let overview = Features.SessionFilmstrip.renderOverview filtered
          let cards =
            filtered
            |> List.map Features.SessionFilmstrip.renderFrame
          return
            [ overview; ""; yield! cards ]
            |> String.concat "\n"
    }

  // ── Phase 1b: Orphaned module MCP tools ──

  /// MCP Tool: Export session as notebook (.fsx with cell metadata)
  let exportNotebook (ctx: McpContext) (projectName: string option) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Feature state not available — no active session."
      | Some getState ->
        let state = getState ()
        match state.EvalHistory with
        | [] -> return "No eval history — nothing to export."
        | history ->
          let cells =
            history
            |> List.rev
            |> List.mapi (fun i e ->
              { Features.NotebookExport.Metadata =
                  { Index = i; Label = None; Deps = []; Bindings = [] }
                Code = e.Code
                Output = Some e.Result } : Features.NotebookExport.NotebookCell)
          let header : Features.NotebookExport.NotebookHeader =
            { Project = projectName |> Option.defaultValue "SageFs Session"
              CellCount = cells.Length
              Timestamp = System.DateTimeOffset.UtcNow.ToString("o") }
          return Features.NotebookExport.exportNotebook header cells
    }

  /// MCP Tool: Export session as clean .fsx transcript
  let exportSessionTranscript (ctx: McpContext) (projectName: string option) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Feature state not available — no active session."
      | Some getState ->
        let state = getState ()
        match state.EvalHistory with
        | [] -> return "No eval history — nothing to export."
        | _ ->
          let graph = buildCellGraphFromState state
          let entries = Features.SessionScribe.SessionScribe.fromGraph graph
          let name = projectName |> Option.defaultValue "SageFs Session"
          return Features.SessionScribe.SessionScribe.exportFsx name entries
    }

  /// MCP Tool: Get message journal (synthesized from eval history)
  let getMessageJournal (ctx: McpContext) (minLevel: string option) (source: string option) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Feature state not available — no active session."
      | Some getState ->
        let state = getState ()
        match state.EvalHistory with
        | [] -> return "No eval history — journal is empty."
        | history ->
          let journal =
            history
            |> List.rev
            |> List.fold (fun j e ->
              Features.MessageJournal.Journal.record
                Features.MessageJournal.JournalLevel.Info "eval" e.Code j)
              (Features.MessageJournal.Journal.create (max 256 history.Length))
          let filtered =
            match minLevel with
            | Some lvl ->
              let level =
                match lvl.ToLowerInvariant() with
                | "debug" -> Features.MessageJournal.JournalLevel.Debug
                | "warn" | "warning" -> Features.MessageJournal.JournalLevel.Warn
                | "error" -> Features.MessageJournal.JournalLevel.Error
                | _ -> Features.MessageJournal.JournalLevel.Info
              Features.MessageJournal.Journal.filterByMinLevel level journal
            | None -> Features.MessageJournal.Journal.entries journal
          let filtered =
            match source with
            | Some src when src <> "" ->
              filtered |> List.filter (fun e -> e.Source.Contains(src, System.StringComparison.OrdinalIgnoreCase))
            | _ -> filtered
          let stats = Features.MessageJournal.Journal.stats journal
          return
            [ sprintf "Journal: %d entries (%d info, %d warn, %d error)"
                stats.Total stats.InfoCount stats.WarnCount stats.ErrorCount
              ""
              yield!
                filtered
                |> List.map (fun e ->
                  sprintf "[%s] %s | %s: %s"
                    (e.Timestamp.ToString("HH:mm:ss"))
                    (match e.Level with
                     | Features.MessageJournal.JournalLevel.Debug -> "DBG"
                     | Features.MessageJournal.JournalLevel.Info -> "INF"
                     | Features.MessageJournal.JournalLevel.Warn -> "WRN"
                     | Features.MessageJournal.JournalLevel.Error -> "ERR")
                    e.Source
                    (e.Message |> fun m -> match m.Length > 80 with | true -> m.[..77] + "..." | false -> m)) ]
            |> String.concat "\n"
    }

  /// MCP Tool: Get eval timeline with sparkline and percentiles
  let getEvalTimeline (ctx: McpContext) (sparklineWidth: int option) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Feature state not available — no active session."
      | Some getState ->
        let state = getState ()
        let timeline = state.CachedTimeline
        match timeline.Entries with
        | [] -> return "No eval timeline — evaluate some cells first."
        | _ ->
          let width = sparklineWidth |> Option.defaultValue 20
          let stats = Features.EvalTimeline.timelineStats width timeline
          return
            [ sprintf "Eval Timeline (%d evals):" stats.Count
              sprintf "  Sparkline: %s" stats.Sparkline
              stats.P50Ms |> Option.map (sprintf "  P50: %.1fms") |> Option.defaultValue "  P50: —"
              stats.P95Ms |> Option.map (sprintf "  P95: %.1fms") |> Option.defaultValue "  P95: —"
              stats.P99Ms |> Option.map (sprintf "  P99: %.1fms") |> Option.defaultValue "  P99: —"
              stats.MeanMs |> Option.map (sprintf "  Mean: %.1fms") |> Option.defaultValue "  Mean: —"
              ""
              yield!
                timeline.Entries
                |> List.rev
                |> List.truncate 20
                |> List.map (fun e ->
                  let icon =
                    match e.Status with
                    | Features.EvalTimeline.Success -> "✓"
                    | Features.EvalTimeline.Error -> "✗"
                    | Features.EvalTimeline.Cancelled -> "○"
                  sprintf "  [%d] %s %dms" e.CellId icon e.DurationMs) ]
            |> String.concat "\n"
    }

  /// MCP Tool: Manage scratch pad (ephemeral code snippets)
  let manageScratchPad (ctx: McpContext) (action: string) (code: string option) (snippetId: int option) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Feature state not available — no active session."
      | Some getState ->
        let state = getState ()
        match action.ToLowerInvariant() with
        | "list" ->
          let pad = Features.ScratchPad.create "session"
          let pad =
            state.EvalHistory
            |> List.rev
            |> List.fold (fun p e -> Features.ScratchPad.addSnippet e.Code p) pad
          let snippets = Features.ScratchPad.snippets pad
          match snippets with
          | [] -> return "Scratch pad is empty."
          | _ ->
            return
              snippets
              |> List.map (fun s ->
                let status =
                  match s.Result with
                  | None -> "pending"
                  | Some (Ok v) -> sprintf "ok: %s" (v |> fun x -> match x.Length > 40 with | true -> x.[..37] + "..." | false -> x)
                  | Some (Error e) -> sprintf "err: %s" (e |> fun x -> match x.Length > 40 with | true -> x.[..37] + "..." | false -> x)
                sprintf "  [%d] %s | %s"
                  s.Id
                  (s.Code |> fun c -> match c.Length > 50 with | true -> c.[..47] + "..." | false -> c)
                  status)
              |> fun lines ->
                sprintf "Scratch pad (%d snippets):\n%s" snippets.Length (String.concat "\n" lines)
        | "export" ->
          let pad = Features.ScratchPad.create "session"
          let pad =
            state.EvalHistory
            |> List.rev
            |> List.fold (fun p e ->
              let p = Features.ScratchPad.addSnippet e.Code p
              let lastId = p.NextId - 1
              Features.ScratchPad.recordResult lastId (Ok e.Result) p) pad
          return Features.ScratchPad.exportFsx pad
        | "promote" ->
          let pad = Features.ScratchPad.create "session"
          let pad =
            state.EvalHistory
            |> List.rev
            |> List.fold (fun p e ->
              let p = Features.ScratchPad.addSnippet e.Code p
              let lastId = p.NextId - 1
              Features.ScratchPad.recordResult lastId (Ok e.Result) p) pad
          let successful = Features.ScratchPad.promoteSuccessful pad
          match successful with
          | [] -> return "No successful snippets to promote."
          | _ ->
            return
              sprintf "Promoted %d successful snippets:\n%s"
                successful.Length (String.concat "\n;;\n" successful)
        | _ -> return "Unknown action. Use 'list', 'export', or 'promote'."
    }

  /// MCP Tool: Get eval diff (before/after output comparison)
  let getEvalDiff (ctx: McpContext) (cellIndex: int option) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Feature state not available — no active session."
      | Some getState ->
        let state = getState ()
        match state.EvalHistory with
        | [] -> return "No eval history — nothing to diff."
        | [_single] -> return "Only one eval in history — nothing to diff against."
        | history ->
          let recent = history |> List.rev
          let (oldOutput, newOutput) =
            match cellIndex with
            | Some idx ->
              let matching = recent |> List.filter (fun e -> e.CellIndex = idx) |> List.rev
              match matching with
              | a :: b :: _ -> (Some a.Result, Some b.Result)
              | _ ->
                let last2 = recent |> List.rev |> List.truncate 2
                match last2 with
                | [a; b] -> (Some a.Result, Some b.Result)
                | _ -> (None, None)
            | None ->
              let last2 = recent |> List.rev |> List.truncate 2
              match last2 with
              | [a; b] -> (Some a.Result, Some b.Result)
              | _ -> (None, None)
          let lines = Features.EvalDiff.diffLines oldOutput newOutput
          let summary = Features.EvalDiff.summarize lines
          return Features.EvalDiff.formatSummary summary
    }

  /// Compose a full diagnostic report: joins test failures, cell graph,
  /// provenance, ripple plan, suggestions, and performance context.
  let diagnose (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetElmModel, ctx.GetFeatureState with
      | None, _ -> return "Diagnosis not available — Elm loop not started."
      | _, None -> return "Diagnosis not available — no active session."
      | Some getModel, Some getState ->
        let model = getModel ()
        let state = getState ()

        let graph = buildCellGraphFromState state

        // Collect all failing tests with their narratives
        let testState = model.LiveTesting.TestState
        let failuresWithNarratives =
          testState.DiscoveredTests
          |> Array.choose (fun tc ->
            match Map.tryFind tc.Id testState.LastResults with
            | Some r ->
              match r.Result with
              | Features.LiveTesting.TestResult.Failed _ ->
                let narrative =
                  match Map.tryFind tc.Id testState.Cached.FailureNarratives with
                  | Some n -> n
                  | None ->
                    { Features.LiveTesting.FailureNarrative.LastPassedAt = None
                      TimeSinceLastPass = None
                      CausalChanges = []
                      PropertyViolation = None
                      Summary = "No narrative available" }
                Some (tc.Id, tc.DisplayName, narrative)
              | _ -> None
            | None -> None)
          |> Array.toList

        // Get scope bindings for Ghostwriter suggestions
        let scopeBindings =
          match state.CachedScope with
          | Some snapshot -> toScopeBindings snapshot
          | None -> []

        let report =
          Features.Diagnostician.Diagnostician.compose
            graph
            failuresWithNarratives
            scopeBindings
            state.CachedTimeline

        // Format as both structured JSON and human summary
        let jsonData =
          {| FailureCount = report.Failures.Length
             Severity = report.Severity.ToString()
             AffectedCellCount = report.AffectedCells.Length
             RippleStepCount =
               match report.RipplePlan with
               | Some p -> p.Steps.Length
               | None -> 0
             SuggestionCount = report.SuggestedFixes.Length
             Failures =
               report.Failures
               |> List.map (fun f ->
                 {| TestName = f.TestName
                    CausalCells = f.CausalCells
                    CausalChanges =
                      f.Narrative.CausalChanges
                      |> List.map (fun c ->
                        match c with
                        | Features.LiveTesting.CausalChange.SymbolChanged s -> {| Kind = "symbol"; Name = s |}
                        | Features.LiveTesting.CausalChange.FileChanged p -> {| Kind = "file"; Name = p |}
                        | Features.LiveTesting.CausalChange.Unknown -> {| Kind = "unknown"; Name = "" |})
                    PropertyViolation =
                      f.Narrative.PropertyViolation
                      |> Option.map (fun pv ->
                        {| PropertyName = pv.PropertyName
                           ShrunkCounterexample = pv.ShrunkCounterexample
                           AlgebraicCategory = pv.AlgebraicCategory |}) |})
             Suggestions =
               report.SuggestedFixes
               |> List.truncate 5
               |> List.map (fun s ->
                 {| Code = s.Code; Explanation = s.Explanation; Confidence = s.Confidence |})
             Performance = report.PerformanceContext |> Option.map (fun s -> {| Sparkline = s.Sparkline; P50Ms = s.P50Ms; P95Ms = s.P95Ms |})
             Summary = report.Summary |}

        return JsonSerializer.Serialize(jsonData, liveTestJsonOpts)
    }

  /// Coverage intelligence: joins failure narratives + coverage bitmaps + dep graph
  /// into blind spot analysis and correlated failure discovery.
  let coverageIntel (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetElmModel with
      | None -> return "Coverage intel not available — Elm loop not started."
      | Some getModel ->
        let model = getModel ()
        let cycleState = model.LiveTesting
        let testState = cycleState.TestState

        let failuresWithNarratives =
          testState.DiscoveredTests
          |> Array.choose (fun tc ->
            match Map.tryFind tc.Id testState.LastResults with
            | Some r ->
              match r.Result with
              | Features.LiveTesting.TestResult.Failed _ ->
                let narrative =
                  match Map.tryFind tc.Id testState.Cached.FailureNarratives with
                  | Some n -> n
                  | None ->
                    { Features.LiveTesting.FailureNarrative.LastPassedAt = None
                      TimeSinceLastPass = None
                      CausalChanges = []
                      PropertyViolation = None
                      Summary = "No narrative available" }
                Some (tc.Id, tc.DisplayName, narrative)
              | _ -> None
            | None -> None)
          |> Array.toList

        let allMaps =
          cycleState.InstrumentationMaps
          |> Map.values |> Seq.collect id |> Seq.toArray

        let causalFileResolver (symbols: string list) =
          symbols
          |> List.collect (fun sym ->
            cycleState.DepGraph.PerFileIndex
            |> Map.toList
            |> List.choose (fun (file, symMap) ->
              match Map.containsKey sym symMap with
              | true -> Some file
              | false -> None))
          |> List.distinct

        let reports =
          Features.CoverageIntel.CoverageIntel.compose
            failuresWithNarratives causalFileResolver allMaps
            testState.TestCoverageBitmaps cycleState.DepGraph

        let jsonData =
          reports |> List.map (fun r ->
            {| TestId = r.TestId
               TestName = r.TestName
               Verdict = r.Verdict.ToString()
               CoveragePercent = r.CoveragePercent
               CoveredBranches = r.CoveredBranches
               TotalBranches = r.TotalBranches
               CausalSymbols = r.CausalSymbols
               BlindSpots = r.BlindSpots |> List.map (fun g ->
                 {| File = g.FilePath; Line = g.Line; Branch = g.BranchId |})
               CorrelatedFailures = r.CorrelatedFailures |> List.map string
               Summary = Features.CoverageIntel.CoverageIntel.summarize r |})

        return JsonSerializer.Serialize(jsonData, liveTestJsonOpts)
    }

  /// Impact forecast: joins eval timeline + cell dependency graph + performance data
  /// into regression detection and downstream impact analysis.
  let impactForecast (ctx: McpContext) (cellIdOpt: int option) : Task<string> =
    task {
      match ctx.GetElmModel, ctx.GetFeatureState with
      | None, _ -> return "Impact forecast not available — Elm loop not started."
      | _, None -> return "Impact forecast not available — no active session."
      | Some getModel, Some getState ->
        let model = getModel ()
        let state = getState ()
        let graph = buildCellGraphFromState state

        let targetCells =
          match cellIdOpt with
          | Some cid -> [ cid ]
          | None -> graph.Cells |> Map.toList |> List.map fst

        let reports =
          targetCells
          |> List.map (fun cellId ->
            let downstream = Features.CellDependencyGraph.transitiveStale graph cellId
            let timeline = state.CachedTimeline
            let timelineStats =
              Features.EvalTimeline.timelineStats 20 state.CachedTimeline
            let durations =
              timeline.Entries
              |> List.filter (fun e -> e.CellId = cellId)
              |> List.map (fun e -> float e.DurationMs)
              |> List.rev |> List.truncate 10
            let p50 = timelineStats.P50Ms |> Option.defaultValue 0.0
            let p95 = timelineStats.P95Ms |> Option.defaultValue 0.0
            Features.ImpactForecast.ImpactForecast.analyzeCell cellId p50 p95 durations downstream)

        let jsonData =
          reports |> List.map (fun r ->
            {| CellId = r.CellId
               P50Ms = r.P50Ms
               P95Ms = r.P95Ms
               DurationTrend = r.DurationTrendMs
               DownstreamCellCount = r.DownstreamCellCount
               Recommendation = r.Recommendation.ToString()
               RegressionCauses = r.RegressionCauses |> List.map (fun c -> c.ToString())
               Summary = Features.ImpactForecast.ImpactForecast.summarize r |})

        return JsonSerializer.Serialize(jsonData, liveTestJsonOpts)
    }

  /// Action prioritizer: merges all intelligence into a ranked "what to do next" queue.
  let suggestNextAction (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetElmModel, ctx.GetFeatureState with
      | None, _ -> return "Action suggestions not available — Elm loop not started."
      | _, None -> return "Action suggestions not available — no active session."
      | Some getModel, Some getState ->
        let model = getModel ()
        let state = getState ()

        let cycleState = model.LiveTesting
        let testState = cycleState.TestState

        // Build coverage intel reports
        let failuresWithNarratives =
          testState.DiscoveredTests
          |> Array.choose (fun tc ->
            match Map.tryFind tc.Id testState.LastResults with
            | Some r ->
              match r.Result with
              | Features.LiveTesting.TestResult.Failed _ ->
                let narrative =
                  match Map.tryFind tc.Id testState.Cached.FailureNarratives with
                  | Some n -> n
                  | None ->
                    { Features.LiveTesting.FailureNarrative.LastPassedAt = None
                      TimeSinceLastPass = None; CausalChanges = []; PropertyViolation = None
                      Summary = "No narrative available" }
                Some (tc.Id, tc.DisplayName, narrative)
              | _ -> None
            | None -> None)
          |> Array.toList

        let allMaps =
          cycleState.InstrumentationMaps
          |> Map.values |> Seq.collect id |> Seq.toArray

        let coverageReports =
          Features.CoverageIntel.CoverageIntel.compose
            failuresWithNarratives
            (fun _ -> [])
            allMaps testState.TestCoverageBitmaps cycleState.DepGraph

        // Build impact forecast reports
        let graph = buildCellGraphFromState state
        let impactReports =
          graph.Cells |> Map.toList |> List.map (fun (cellId, _) ->
            let downstream = Features.CellDependencyGraph.transitiveStale graph cellId
            let stats = Features.EvalTimeline.timelineStats 20 state.CachedTimeline
            let p50 = stats.P50Ms |> Option.defaultValue 0.0
            let p95 = stats.P95Ms |> Option.defaultValue 0.0
            Features.ImpactForecast.ImpactForecast.analyzeCell cellId p50 p95 [] downstream)

        // Stale cells: any cell in the graph (all are candidates for the action queue)
        let staleCellIds = graph.Cells |> Map.toList |> List.map fst

        let report =
          Features.ActionPrioritizer.ActionPrioritizer.compose
            coverageReports impactReports staleCellIds

        let jsonData =
          {| HealthGrade = report.HealthGrade.ToString()
             TotalFailures = report.TotalFailures
             TotalBlindSpots = report.TotalBlindSpots
             TotalRegressions = report.TotalRegressions
             Actions = report.Actions |> List.truncate 10 |> List.map (fun a ->
               {| Kind = a.Kind.ToString()
                  Priority = a.Priority
                  Reason = a.Reason |})
             Summary = Features.ActionPrioritizer.ActionPrioritizer.summarize report |}

        return JsonSerializer.Serialize(jsonData, liveTestJsonOpts)
    }

  /// List all discovered tests, optionally filtered by pattern or file path.
  let listTests (ctx: McpContext) (patternOpt: string option) (fileOpt: string option) : Task<string> =
    task {
      match ctx.GetElmModel, ctx.GetFeatureState with
      | None, _ -> return "Test list not available — Elm loop not started."
      | _, None -> return "Test list not available — no active session."
      | Some getModel, Some getState ->
        let model = getModel ()
        let state = getState ()
        let graph = buildCellGraphFromState state
        let locations =
          model.LiveTesting.TestState.DiscoveredTests
          |> Array.toList
          |> Features.TestSourceResolver.resolveTestLocations graph
        let query : Features.TestDiscovery.TestDiscoveryQuery = {
          Pattern    = patternOpt |> Option.filter (fun s -> s.Length > 0)
          FilePath   = fileOpt |> Option.filter (fun s -> s.Length > 0)
          MaxResults = 200
        }
        let result = Features.TestDiscovery.TestDiscovery.applyQuery query locations
        let jsonData =
          {| TotalCount    = result.TotalCount
             Returned      = result.Tests.Length
             FilterApplied = result.FilterApplied
             Summary       = Features.TestDiscovery.TestDiscovery.summarize result
             GroupedByFile = result.GroupedByFile |> List.map (fun (file, tests) ->
               {| File  = file
                  Tests = tests |> List.map (fun t ->
                    {| TestName  = t.TestName
                       StartLine = t.StartLine
                       EndLine   = t.EndLine |}) |}) |}
        return JsonSerializer.Serialize(jsonData, liveTestJsonOpts)
    }

  /// Expose the cell dependency graph with staleness annotations.
  let getCellDependencies (ctx: McpContext) : Task<string> =
    task {
      match ctx.GetFeatureState with
      | None -> return "Cell dependency graph not available — no active session."
      | Some getState ->
        let state = getState ()
        let graph = buildCellGraphFromState state
        // Pass empty changed set — graph structure and wiring is always useful.
        // Callers can use plan_ripple with a specific cell to see staleness impact.
        let report = Features.CellDependenciesReport.CellDependenciesReport.compose graph Set.empty
        let jsonData =
          {| TotalCells    = report.TotalCells
             TotalStale    = report.TotalStale
             TotalEdges    = report.TotalEdges
             StaleCellIds  = report.StaleCellIds
             Summary       = report.Summary
             Nodes         = report.Nodes |> List.map (fun n ->
               {| Id            = n.Id
                  Produces      = n.Produces
                  Consumes      = n.Consumes
                  DownstreamIds = n.DownstreamIds
                  UpstreamIds   = n.UpstreamIds
                  IsStale       = CellFreshness.isStale n.Staleness
                  StaleCauses   = CellFreshness.causes n.Staleness |}) |}
        return JsonSerializer.Serialize(jsonData, liveTestJsonOpts)
    }

  /// Discover and rank SageFs features relevant to the current session state.
  let discoverFeatures (ctx: McpContext) (topicOpt: string option) : Task<string> =
    task {
      let discoveryCtx =
        match ctx.GetElmModel, ctx.GetFeatureState with
        | None, _ | _, None -> Features.FeatureDiscovery.FeatureDiscovery.emptyContext
        | Some getModel, Some getState ->
          let model = getModel ()
          let state = getState ()
          let testState = model.LiveTesting.TestState
          let failingCount =
            testState.LastResults
            |> Map.values
            |> Seq.filter (fun r ->
              match r.Result with
              | Features.LiveTesting.TestResult.Failed _ -> true
              | _ -> false)
            |> Seq.length
          {
            Features.FeatureDiscovery.DiscoveryContext.FailingTestCount = failingCount
            StaleCellCount   = 0
            TotalEvals       = state.EvalHistory.Length
            TotalTests       = testState.DiscoveredTests.Length
            RequestedTopic   = topicOpt |> Option.filter (fun s -> s.Length > 0)
          }
      let report = Features.FeatureDiscovery.FeatureDiscovery.discover discoveryCtx
      let jsonData =
        {| ContextSummary     = report.ContextSummary
           TotalKnownFeatures = report.TotalKnownFeatures
           Returned           = report.Suggestions.Length
           Suggestions        = report.Suggestions |> List.map (fun s ->
             {| ToolName          = s.ToolName
                ShortDescription  = s.ShortDescription
                ExampleUsage      = s.ExampleUsage
                WhyNow            = s.WhyNow
                Relevance         = s.Relevance.ToString() |}) |}
      return JsonSerializer.Serialize(jsonData, liveTestJsonOpts)
    }

  /// suggest_repair: compose explain_test_failure → extract causal symbol → preview_what_if
  /// V1: surfaces the causal symbol + current binding + ripple plan without suggesting a new value.
  let suggestRepair (ctx: McpContext) (testName: string) : Task<string> =
    task {
      match ctx.GetElmModel, ctx.GetFeatureState with
      | None, _ | _, None ->
        return "suggest_repair requires an active session with live testing. Start SageFs with a test project first."
      | Some getModel, Some getState ->
        let model = getModel ()
        let testState = model.LiveTesting.TestState
        let state = getState ()
        let matchingTests =
          testState.DiscoveredTests
          |> Array.filter (fun tc ->
            tc.FullName.Contains(testName, StringComparison.OrdinalIgnoreCase)
            || tc.DisplayName.Contains(testName, StringComparison.OrdinalIgnoreCase))
        match matchingTests with
        | [||] ->
          return sprintf "No test found matching '%s'. Use run_tests to see available tests." testName
        | tests ->
          let narrativeOpt =
            tests |> Array.tryPick (fun tc -> Map.tryFind tc.Id testState.Cached.FailureNarratives)
          match narrativeOpt with
          | None ->
            let testNames = tests |> Array.map (fun tc -> tc.DisplayName) |> String.concat ", "
            return
              sprintf
                "No failure narrative for '%s' (%s). The test may not have transitioned Passed→Failed recently, or live testing may not be running. Call run_tests to trigger a run, then retry."
                testName testNames
          | Some narrative ->
            let allChanges =
              narrative.CausalChanges
              |> List.map (fun c ->
                match c with
                | Features.LiveTesting.CausalChange.SymbolChanged s -> {| Kind = "symbol"; Name = s |}
                | Features.LiveTesting.CausalChange.FileChanged f   -> {| Kind = "file";   Name = f |}
                | Features.LiveTesting.CausalChange.Unknown         -> {| Kind = "unknown"; Name = "" |})
            let primarySymbol =
              narrative.CausalChanges
              |> List.tryPick (fun c ->
                match c with
                | Features.LiveTesting.CausalChange.SymbolChanged s -> Some s
                | _ -> None)
            // Build ripple plan for primary symbol if it's in session bindings
            let ripplePlanOpt =
              match primarySymbol, state.EvalHistory with
              | None, _ | _, [] -> None
              | Some sym, _ ->
                let graph = buildCellGraphFromState state
                let scope =
                  state.CachedScope
                  |> Option.defaultWith (fun () -> Features.FeatureHooks.buildScopeFromState state)
                match scope.ActiveBindings |> Map.tryFind sym with
                | None -> None
                | Some binding ->
                  let currentCode = binding.Value |> Option.defaultValue "?"
                  let typeSig = binding.TypeSig
                  let override' = Features.WhatIf.createOverride sym currentCode "<your-fix>" typeSig
                  let plan = Features.WhatIf.planWhatIf graph override'
                  let steps =
                    plan.RippleSteps
                    |> List.map (fun step ->
                      {| CellId = step.CellId
                         Code = step.Code |> fun c -> if c.Length > 60 then c.[..57] + "..." else c
                         Status =
                           match step.Status with
                           | Features.Pending     -> "pending"
                           | Features.Evaluating  -> "evaluating"
                           | Features.Succeeded _ -> "succeeded"
                           | Features.Failed _    -> "failed"
                           | Features.Skipped _   -> "skipped" |})
                  Some {| Symbol = sym; CurrentCode = currentCode; TypeSig = typeSig; AffectedCellCount = plan.AffectedCells.Length; RippleSteps = steps |}
            let timeSince =
              narrative.TimeSinceLastPass
              |> Option.map (fun ts -> sprintf "%.0fs" ts.TotalSeconds)
              |> Option.defaultValue "unknown"
            let suggestion =
              match primarySymbol, ripplePlanOpt with
              | None, _ ->
                sprintf
                  "This test broke ~%s ago. No symbol-level causal changes were detected — review the file changes above and check recent edits manually."
                  timeSince
              | Some sym, None ->
                sprintf
                  "'%s' is the likely cause, but it's not in the current session bindings. Re-evaluate the cell that defines '%s', then retry suggest_repair."
                  sym sym
              | Some sym, Some plan ->
                sprintf
                  "'%s' (%s) is the likely cause. Call `preview_what_if \"%s\" \"<new-value>\"` to preview the ripple before applying. %d cells downstream will re-evaluate."
                  sym plan.TypeSig sym plan.AffectedCellCount
            let jsonData =
              {| TestName      = testName
                 Summary       = narrative.Summary
                 TimeSinceLastPass = timeSince
                 CausalChanges = allChanges
                 PrimarySymbol = primarySymbol |> Option.toObj
                 RipplePlan    = ripplePlanOpt |> Option.toObj
                 Suggestion    = suggestion |}
            return JsonSerializer.Serialize(jsonData, liveTestJsonOpts)
    }
