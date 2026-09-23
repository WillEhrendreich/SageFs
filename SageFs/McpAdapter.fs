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

  /// Directory-name segments never worth surfacing as "available projects":
  /// build output, VCS/tooling metadata, restored packages, and — the one that
  /// actually bit us while dogfooding — the per-agent worktrees under
  /// `.claude/worktrees`, each a full repo checkout that multiplies every
  /// `.fsproj`. A recursive scan that does not prune these returned 1,600+ paths
  /// and overflowed the calling agent's context.
  ///
  /// One ignore list for the whole product — defined next to `discoverProjects`
  /// in DashboardTypes, re-exported here so MCP and the dashboard can never
  /// disagree about what counts as a project.
  let projectNoiseSegments : Set<string> = SageFs.Server.DashboardTypes.projectNoiseSegments

  /// True if any path segment is build/worktree/tooling noise.
  let isNoiseProjectPath (path: string) : bool = SageFs.Server.DashboardTypes.isNoiseProjectPath path

  /// Bound the available-projects list for an agent's context window: drop noise
  /// paths, sort deterministically, and cap. Returns the shown paths plus the
  /// TOTAL real project count (post-noise-filter) so the caller can honestly say
  /// "showing X of N" instead of dumping everything. Pure and testable.
  let selectProjectsForDisplay (cap: int) (relativePaths: string seq) : string[] * int =
    let real =
      relativePaths
      |> Seq.filter (isNoiseProjectPath >> not)
      |> Seq.sort
      |> Seq.toArray
    (real |> Array.truncate (max 0 cap)), real.Length

  /// Parse the `projects` argument of create_session tolerantly. The MCP tool
  /// contract historically took a COMMA-SEPARATED string, but every hint the
  /// daemon emits (the NoSession message, the tool description) tells agents to
  /// pass a JSON array — `projects=["a.fsproj"]`. An agent that followed the
  /// docs got the whole JSON-array TEXT taken as one bogus project path
  /// (`.Split(',')` finds no comma), so no project loaded and the session still
  /// reached a vacuous Ready. This accepts BOTH forms — JSON array (the
  /// documented shape), comma-separated (the legacy shape), or a single path —
  /// and drops empties so `[]`/`""` mean "no EXPLICIT project requested" —
  /// NOT a guaranteed-empty REPL: the worker still auto-discovers whatever
  /// project/solution sits directly in the working directory when the
  /// caller names none (sagefs-roast.md Finding #2; verified live). Pure
  /// and testable. Found by dogfooding: self-hosting a SageFs.Core session.
  let parseProjectsArg (raw: string) : string list =
    match System.String.IsNullOrWhiteSpace raw with
    | true -> []
    | false ->
      let trimmed = raw.Trim()
      let fromJsonArray () =
        try
          use doc = JsonDocument.Parse trimmed
          match doc.RootElement.ValueKind with
          | JsonValueKind.Array ->
            doc.RootElement.EnumerateArray()
            |> Seq.choose (fun e ->
                match e.ValueKind with
                | JsonValueKind.String -> Some(e.GetString())
                | _ -> None)
            |> Seq.toList
            |> Some
          | _ -> None
        with _ -> None
      let entries =
        match trimmed.StartsWith("[", System.StringComparison.Ordinal) with
        | true ->
          // Looks like the documented JSON-array form. Parse it; if it is
          // malformed, fall back to comma-split so a stray bracket never
          // swallows the paths whole.
          match fromJsonArray () with
          | Some xs -> xs
          | None -> trimmed.Split(',') |> Array.toList
        | false -> trimmed.Split(',') |> Array.toList
      entries
      |> List.map (fun s -> s.Trim())
      |> List.filter (System.String.IsNullOrWhiteSpace >> not)

  let formatAvailableProjects (workingDir: string) (projects: string array) (solutions: string array) (moreCount: int) =
    let projectList =
      match Array.isEmpty projects with
      | true -> "  (none found)"
      | false -> projects |> Array.map (sprintf "  - %s") |> String.concat "\n"
    let moreNote =
      match moreCount with
      | n when n > 0 -> sprintf "\n  …and %d more (pass working_directory to narrow the search)" n
      | _ -> ""
    let solutionList =
      match Array.isEmpty solutions with
      | true -> "  (none found)"
      | false -> solutions |> Array.map (sprintf "  - %s") |> String.concat "\n"
    // NOTE: no longer says "Start the daemon with: SageFs" — that hint can
    // never be true at the moment this text is printed: it is a tool call
    // the running daemon just served (sagefs-roast.md Finding #5).
    sprintf "Available Projects/Solutions in %s:\n\n📦 F# Projects (.fsproj):\n%s%s\n\n📂 Solutions (.sln/.slnx):\n%s\n\n💡 Create a session for ProjectName.fsproj or SolutionName.slnx via create_session\n💡 Sessions can also be created from connected editors or the dashboard" workingDir projectList moreNote solutionList

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
          match ex with
          | :? SageFsErrorException as se ->
            // SageFsErrorException is exactly how a SageFsError travels
            // through this exception-typed channel (see its own doc
            // comment) — the algebra already knows what happened and what
            // to do about it, so use its own suggestedAction instead of
            // re-deriving one via fragile substring matching over free-form
            // text (ErrorMessages.categorize).
            let errText = SageFsError.describe se.Error
            let suggestion = SageFsError.suggestedAction se.Error
            let enhanced = WorkflowErrorContext.enhance workflow errText suggestion
            sprintf "Error: %s\n%s%s" errText enhanced diagnosticsSection
          | _ ->
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
Aspire Detected: %s{aspireStr}
Startup Profile: %s{profileStr}
Started: %s{timestamp} UTC"""

  let formatStartupInfoJson (config: AppState.StartupConfig) : string =
    let data = {|
      commandLineArgs = config.CommandLineArgs
      loadedProjects = config.LoadedProjects |> List.toArray
      workingDirectory = config.WorkingDirectory
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
- Hot Reload: %s
- Aspire: %s
- File Watcher: %s""" config.WorkingDirectory hotReload aspire fileWatch

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
        sprintf ""","startup":{"workingDirectory":"%s","hotReloadEnabled":%b,"aspireDetected":%b,"workflow":"%s","workflowLabel":"%s","replCapability":"%s"}"""
          (escapeJson config.WorkingDirectory) config.HotReloadEnabled config.AspireDetected
          (escapeJson (sprintf "%A" config.Workflow)) workflowLabel replCap
    sprintf """{"sessionId":"%s","eventCount":%d,"state":"%s","projects":%s,"tools":[%s]%s%s}"""
      (escapeJson sessionId) eventCount (SessionState.label state) projectsJson toolsJson statsJson startupJson

  /// What the worker ACTUALLY resolved and loaded, as opposed to what a
  /// session was declared with — the two can legitimately differ, most
  /// visibly with `projects=[]`, which still auto-discovers whatever
  /// solution/project sits directly in the working directory
  /// (sagefs-roast.md Finding #2/#3). `formatProxyStatus`'s `Projects:`
  /// field reports the DECLARED list; this reports what `ProjectRoles` says
  /// was actually loaded, so "my project silently didn't load" — or "an
  /// unrequested project silently DID load" — is visible at the call site
  /// instead of requiring a separate investigation.
  let formatLoadedProjectsLine (roles: SageFs.ProjectLoading.ClassifiedProject list) : string =
    match roles with
    | [] -> "(none resolved yet — session may still be warming up, or nothing was found to load)"
    | rs -> rs |> List.map (fun p -> Path.GetFileName p.Path) |> String.concat ", "

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
    let loadedStr = formatLoadedProjectsLine info.ProjectRoles
    let statsSection =
      match snapshot.EvalCount > 0 with
      | true ->
        sprintf "\nEvals: %d | Avg: %dms | Min: %dms | Max: %dms"
          snapshot.EvalCount snapshot.AvgDurationMs snapshot.MinDurationMs snapshot.MaxDurationMs
      | false -> ""
    let tools = Affordances.availableTools state |> String.concat ", "
    let modeLabel = WorkflowTypes.SessionWorkflow.label info.Workflow
    sprintf """Session: %s | Mode: %s | Events: %d | State: %s | Projects: %s | Loaded: %s
Available: %s%s

📋 Startup Information:
- Working Directory: %s
- MCP Port: %d""" sessionId modeLabel eventCount (SessionState.label state) projectsStr loadedStr tools statsSection info.WorkingDirectory mcpPort

  /// Diagnostics as JSON array *items* (no enclosing brackets — callers
  /// interpolate into their own `"diagnostics":[%s]` field), spans included
  /// (`StartLine/StartColumn/EndLine/EndColumn`). Factored out once so
  /// `formatWorkerEvalResultJson` and `formatEvalStructuredSuccess` (the
  /// structured `send_fsharp_code` path, roast-7 §2) can never drift apart.
  let diagnosticsToJson (diags: WorkerProtocol.WorkerDiagnostic list) : string =
    diags
    |> List.map (fun (d: WorkerProtocol.WorkerDiagnostic) ->
      sprintf """{"severity":"%s","message":"%s","startLine":%d,"startColumn":%d,"endLine":%d,"endColumn":%d}"""
        (Features.Diagnostics.DiagnosticSeverity.label d.Severity)
        (escapeJson d.Message) d.StartLine d.StartColumn d.EndLine d.EndColumn)
    |> String.concat ","

  let formatWorkerEvalResultJson (response: WorkerProtocol.WorkerResponse) : string =
    match response with
    | WorkerProtocol.WorkerResponse.EvalResult(_, result, diags, _) ->
      let diagsJson = diagnosticsToJson diags
      match result with
      | Ok output ->
        // issue #143: same ANSI stripping as the TEXT path (Mcp.fs's
        // formatWorkerEvalResult) — this is the JSON sibling and must not
        // disagree about what a caller sees in "result".
        sprintf """{"success":true,"result":"%s","diagnostics":[%s]}"""
          (escapeJson (stripAnsi output)) diagsJson
      | Error err ->
        sprintf """{"success":false,"error":"%s","diagnostics":[%s]}"""
          (escapeJson (SageFsError.describeForAgent err)) diagsJson
    | WorkerProtocol.WorkerResponse.WorkerError err ->
      sprintf """{"success":false,"error":"%s","diagnostics":[]}"""
        (escapeJson (SageFsError.describeForAgent err))
    | other ->
      sprintf """{"success":false,"error":"%s","diagnostics":[]}"""
        (escapeJson (sprintf "Unexpected response: %A" other))

  /// Structured success payload for `send_fsharp_code` (roast-7 §2/§16 item
  /// 2): `{success, result, diagnostics[with spans]}`, the same shape
  /// `formatWorkerEvalResultJson`'s success branch already produces — kept
  /// as its own named function because the failure side (below) is
  /// deliberately NOT the same shape (it carries the full `SageFsError`
  /// algebra, not a flattened message).
  let formatEvalStructuredSuccess (result: string) (diags: WorkerProtocol.WorkerDiagnostic list) : string =
    sprintf """{"success":true,"result":"%s","diagnostics":[%s]}"""
      (escapeJson result) (diagnosticsToJson diags)

  /// Structured failure payload for `send_fsharp_code`: the full
  /// `SageFsError.toJson` triple (`case`/`message`/`suggestedAction`) —
  /// the same shape `McpServer.structuredToolErrorResult` already emits for
  /// every other tool that raises `SageFsErrorException`. `send_fsharp_code`
  /// was the one production call site that flattened this to a plain
  /// string instead (roast-7 §2, sagefs-roast.md Finding #2).
  let formatEvalStructuredError (err: SageFsError) : string =
    JsonSerializer.Serialize(SageFsError.toJson err)

  /// Outbound size cap for MCP tool text results (roast-7 §13/§16 item 13).
  /// The only enforced limit before this was INBOUND — 4 MiB on the request
  /// body (McpServer.maxRequestBodyBytes). A verbose eval result had no
  /// ceiling before landing in an agent's context window. 256 KiB is
  /// generous for a legitimate eval reply (large printed tables, full
  /// stack traces) while bounding runaway output (an accidental print loop,
  /// a megabyte-sized dump) before it eats the whole context budget.
  [<Literal>]
  let MaxOutboundResultBytes = 262_144

  /// Truncate `text` to at most `maxBytes` UTF-8 bytes, appending a marker
  /// naming how many bytes were cut. Never silently drops data without
  /// saying so (a bare cut would look like SageFs is just producing short
  /// output). A partial multi-byte sequence at the cut point decodes to the
  /// Unicode replacement character rather than throwing — acceptable at a
  /// boundary that only exists to protect an agent's context window.
  let truncateForOutbound (maxBytes: int) (text: string) : string =
    let totalBytes = System.Text.Encoding.UTF8.GetByteCount(text)
    match totalBytes <= maxBytes with
    | true -> text
    | false ->
      let marker = sprintf "…[truncated %d bytes]" (totalBytes - maxBytes)
      let markerBytes = System.Text.Encoding.UTF8.GetByteCount(marker)
      let budget = max 0 (maxBytes - markerBytes)
      let allBytes = System.Text.Encoding.UTF8.GetBytes(text)
      let cut = min budget allBytes.Length
      let kept = System.Text.Encoding.UTF8.GetString(allBytes, 0, cut)
      kept + marker

  /// A compact, single-line preview of `text` for event-log summaries:
  /// newlines collapsed to spaces, truncated to `maxChars` with an
  /// ellipsis. Distinct from `truncateForOutbound` (byte-budgeted, for a
  /// whole tool reply) — this is char-budgeted, for one line of many in a
  /// list.
  let previewLine (maxChars: int) (text: string) : string =
    let collapsed = text.Replace("\r\n", " ").Replace("\n", " ").Trim()
    match collapsed.Length <= maxChars with
    | true -> collapsed
    | false -> collapsed.Substring(0, maxChars) + "…"

