module SageFs.Server.McpTools

open System.ComponentModel
open System.Threading.Tasks
open ModelContextProtocol.Server
open Microsoft.Extensions.Logging
open SageFs.AppState
open SageFs.McpTools
open SageFs.Utils

/// Emoji per tool category — printed once as a header, not per-line
/// Echo MCP tool results to the SageFs console for visibility

/// Global audit tracker for MCP tool usage analysis (synthesis 3.3).
let auditTracker = SageFs.McpToolAudit.AuditTracker()

let withEcho (toolName: string) (t: Task<string>) : Task<string> =
  task {
    SageFs.Instrumentation.mcpToolInvocations.Add(1L)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let span = SageFs.Instrumentation.startSpanWithKind SageFs.Instrumentation.mcpSource "mcp.tool.invoke" System.Diagnostics.ActivityKind.Server
                 ["mcp.tool.name", box toolName; "rpc.system", box "mcp"; "rpc.service", box "sagefs"; "rpc.method", box toolName]
    try
      let! result = t
      sw.Stop()
      SageFs.Instrumentation.mcpToolSuccesses.Add(1L, System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName))
      auditTracker.Record(toolName, sw.Elapsed.TotalMilliseconds, SageFs.McpToolAudit.Success)
      let normalized = result.Replace("\r\n", "\n").Replace("\n", "\r\n")
      Log.info ">> %s" toolName
      Log.debug "%s" normalized
      SageFs.Instrumentation.succeedSpan span
      return result
    with ex ->
      sw.Stop()
      SageFs.Instrumentation.mcpToolFailures.Add(1L, System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName))
      auditTracker.Record(toolName, sw.Elapsed.TotalMilliseconds, SageFs.McpToolAudit.Failure)
      SageFs.Instrumentation.failSpan span ex.Message
      return raise ex
  }

type SageFsTools(ctx: McpContext, logger: ILogger<SageFsTools>) =
    [<McpServerTool>]
    [<Description("""Send F# code to the FSI REPL session. Each ';;' marks a transaction boundary.

RULES:
- End every statement with ';;'
- Submit small, incremental blocks (one definition at a time)
- For '#r nuget:' directives, submit alone in their own call
- Errors are non-destructive: previous definitions survive a failed submission
- Use get_fsi_status to check session health if something seems wrong

TRANSACTION SEMANTICS:
- Each ';;' boundary is a separate transaction. Statements are evaluated sequentially.
- If a statement fails, that ENTIRE statement is discarded — nothing from it is kept in session state.
- Previously evaluated statements (from earlier calls or earlier ';;' boundaries that succeeded) remain valid.
- When a statement fails, subsequent statements in the SAME call are still attempted, but they may fail too if they depended on the failed one.
- NOTE: ';;' inside triple-quoted strings (\"\"\"...\"\"\") does NOT split — it is treated as string content.

ERROR HANDLING (CRITICAL):
- If you get an error, it is almost certainly YOUR code that has the bug. Read the diagnostics carefully and fix your code.
- 'Operation could not be completed due to earlier error' means a PREVIOUS statement had a compile error, so definitions from it were never created. Fix the original error and resubmit that code first.
- The session is NOT corrupted by errors. Do NOT call reset_fsi_session or hard_reset_fsi_session because of eval errors. Fix your code instead.
- Submit smaller pieces (one definition per call) to isolate which part has the error.

RETURN VALUE:
- On success: the printed output of the evaluated code (stdout, printfn output, or the auto-printed value).
- On failure: the F# compiler diagnostic message with file/line info pointing to the error.
- Use get_recent_fsi_events afterward to see the full event log if the return value is ambiguous.

WORKFLOW: Use this tool instead of dotnet build or dotnet run. SageFs IS your compiler and runtime.""")>]
    member _.send_fsharp_code(
        [<Description("Your agent or model name (e.g. 'claude', 'copilot', 'cursor'). Shown in event logs and get_recent_fsi_events output so you can trace which agent submitted which code. Use a short, stable identifier.")>]
        agentName: string,
        code: string,
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string,
        [<Description("Absolute path to the source file this code came from. Enables module context detection — SageFs wraps the code in the correct module/namespace for FSI evaluation.")>]
        file_path: string,
        [<Description("How the code is being evaluated: 'file' for whole-file send, 'block' for a selected region. When omitted, auto-detected from code content.")>]
        eval_mode: string,
        [<Description("1-based line number where the selected block starts in the source file. Helps resolve which module the block belongs to in multi-module files.")>]
        block_start_line: System.Nullable<int>
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        let fp = match System.String.IsNullOrWhiteSpace file_path with | true -> None | false -> Some file_path
        let em = match System.String.IsNullOrWhiteSpace eval_mode with | true -> None | false -> Some eval_mode
        let bsl = match block_start_line.HasValue with | true -> Some block_start_line.Value | false -> None
        logger.LogDebug("MCP-TOOL: send_fsharp_code called by {AgentName}: {Code}", agentName, code)
        SageFs.Instrumentation.mcpToolInvocations.Add(1L)
        sendFSharpCode ctx agentName code OutputFormat.Text None wd fp em bsl
    
    [<McpServerTool>]
    [<Description("""Load and execute an F# script file (.fsx). The file is parsed into individual statements and each statement is sent to the FSI session separately, so partial progress is preserved if one statement fails.

WHEN TO USE vs send_fsharp_code:
- Use load_fsharp_script when you have a complete .fsx file on disk that sets up state, runs a scenario, or installs packages via #r directives.
- Use send_fsharp_code when you are writing/iterating on small code snippets interactively.

BEHAVIOR:
- The file is split at ';;' boundaries and each block is evaluated in order.
- If block N fails, blocks N+1 and beyond are still attempted (unless they depend on failed definitions).
- '#r nuget:' directives in the file are handled as a special case and submitted alone before other blocks.
- '#load "other.fsx"' directives inside the script recursively load the referenced file through the same mechanism.
- Returns a summary of how many blocks succeeded and how many failed, with error details for failures.

PATH:
- filePath should be an absolute path. Relative paths are resolved against the session's working directory.
- The file must exist on disk at call time — it is read, not streamed.""")>]
    member _.load_fsharp_script(
        [<Description("Your agent or model name (e.g. 'claude', 'copilot', 'cursor'). Shown in event logs for attribution.")>]
        agentName: string,
        filePath: string,
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> = 
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: load_fsharp_script called: {FilePath}", filePath)
        loadFSharpScript ctx agentName filePath None wd |> withEcho "load_fsharp_script"
    
    [<McpServerTool>]
    [<Description("""Get recent FSI events including evaluations, errors, and script loads. Returns the most recent N events (default 10) with timestamps and sources.

WHEN TO USE:
- After an unexpected error to understand what just happened and in what order.
- To audit which code was evaluated and by which agent (MCP, editor plugin, etc.).
- To check whether a hot-reload occurred (look for 'FileChanged' or 'ScriptLoaded' events).
- As a lightweight alternative to get_fsi_status when you only want the recent activity log.

OUTPUT FORMAT: Each event shows timestamp, event type (Eval, Error, Load, Reset), source agent name, and a brief description. Events are newest-last.""")>]
    member _.get_recent_fsi_events(
        count: int option,
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> = 
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        let eventCount = defaultArg count 10
        logger.LogDebug("MCP-TOOL: get_recent_fsi_events called: count={Count}", eventCount)
        getRecentEvents ctx "mcp" eventCount wd |> withEcho "get_recent_fsi_events"
    
    [<McpServerTool>]
    [<Description("""Get the current FSI session status: startup configuration, loaded projects, session statistics, and available capabilities. Use to verify session health or discover what is loaded.

WHEN TO USE:
- First thing to call when setting up a new session — confirms what projects are loaded and what features are enabled.
- After hard_reset_fsi_session with rebuild=true, poll this until Status shows 'Ready' (the reset is async, typically 10-60s).
- When you get unexpected 'type not defined' errors — check that the expected project is loaded and the session is warmed up.
- To discover the active session ID needed for routing commands when multiple sessions exist.

KEY FIELDS IN OUTPUT:
- Status: Ready | Evaluating | Building — ONLY submit code when status is 'Ready'. If 'Evaluating', a previous send_fsharp_code is still running (use cancel_eval if stuck). If 'Building', a hard_reset rebuild is in progress.
- LoadedProjects: list of .fsproj files loaded into this session — if empty, no project is loaded and only BCL types are available.
- Affordances: which MCP tools are active for this session (e.g., live testing affordances are absent in sessions without test projects).
- SessionId: the stable ID for this session. Pass to switch_session if auto-routing is selecting the wrong session.""")>]
    member _.get_fsi_status(
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: get_fsi_status called: workingDir={Dir}", working_directory)
        getStatus ctx "mcp" None wd |> withEcho "get_fsi_status"

    [<McpServerTool>]
    [<Description("""Get detailed startup information: loaded projects, enabled features, and command-line arguments. Use to understand what capabilities are available in the current session.

DIFFERENCE FROM get_fsi_status:
- get_fsi_status gives you the LIVE runtime state (current status, session health, active affordances).
- get_startup_info gives you STATIC configuration — how SageFs was launched (CLI flags, --proj, --port, --tui, --gui, etc.).

WHEN TO USE:
- To find out which projects were specified on the CLI vs loaded dynamically.
- To understand which features were enabled at startup (e.g., live testing, TUI, GUI).
- When investigating environment differences ("was live testing enabled when this was launched?").""")>]
    member _.get_startup_info(
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: get_startup_info called")
        getStartupInfo ctx "mcp" wd |> withEcho "get_startup_info"

    [<McpServerTool>]
    [<Description("""Discover F# projects (.fsproj) and solutions (.sln/.slnx) in the current working directory. Useful for determining what projects can be loaded with 'SageFs --proj'.

WHEN TO USE:
- When you want to know which projects exist in this repo before creating a session or advising the user to restart SageFs with a different --proj.
- To find a test project to pass to create_session so you can run tests in an isolated session.
- As a discovery step when the user opens a new workspace and you need to understand the project structure.

NOTE: This does NOT load any projects — it only lists what is available on disk. Use create_session or hard_reset_fsi_session with rebuild=true to actually load a project.""")>]
    member _.get_available_projects(
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: get_available_projects called")
        getAvailableProjects ctx "mcp" wd |> withEcho "get_available_projects"

    [<McpServerTool>]
    [<Description("""Soft-reset the FSI session. All user-defined types, values, and bindings are cleared. The session is re-warmed by re-executing the project's startup #load scripts to restore base namespaces.

WHAT GETS CLEARED:
- Every `let`, `type`, `module`, `open`, and `do` binding you submitted via send_fsharp_code.
- Any NuGet packages loaded via '#r nuget:' in your interactive code.

WHAT SURVIVES:
- The loaded project assemblies (DLLs are still referenced — no DLL unlock/reload).
- The session's working directory and project association.

AFTER RESET:
- The session re-runs project warm-up scripts automatically (~1-3s for most projects).
- Check get_fsi_status until Status shows 'Ready' before sending new code.

WHEN TO USE (rare):
- The session warm-up itself failed and you see cascade errors on EVERY submission, even trivial ones like '1+1;;'.
- You intentionally want to clear all your interactive definitions and start fresh.

WHEN NOT TO USE (common mistake):
- You got an eval error — that means YOUR code has a bug. Fix your code and resubmit instead.
- 'Operation could not be completed due to earlier error' — this is NOT session corruption. A previous submission failed. Fix and resubmit that code.
- You're not sure what went wrong — read the error diagnostics first, they tell you exactly what's wrong.

This is a SOFT reset — DLL locks are retained. Use hard_reset_fsi_session only if modules failed to load during warm-up.""")>]
    member _.reset_fsi_session(
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: reset_fsi_session called")
        resetSession ctx "mcp" None wd |> withEcho "reset_fsi_session"

    [<McpServerTool>]
    [<Description("""Hard reset: dispose the FSI session, release DLL locks via shadow-copy refresh,
optionally rebuild the project, and create a fresh session. ALL definitions are lost.

⚠️ THIS IS ALMOST NEVER WHAT YOU WANT. Before calling this, ask yourself:
- "Did I get an eval error?" → That's YOUR code's bug. Fix your code. Do NOT hard reset.
- "Did I get 'earlier error'?" → A previous submission failed. Fix and resubmit it. Do NOT hard reset.
- "I want to pick up code changes in .fs files" → The file watcher auto-reloads .fs/.fsx changes via #load (~100ms). You probably don't need this. Use rebuild=true ONLY if you need the project rebuilt (e.g., new file added to .fsproj, package reference changed).
- "The warm-up itself failed with module load errors on session start?" → Then yes, hard reset may help.

VALID REASONS (rare):
- New files added to .fsproj or package references changed (rebuild=true needed)
- Module opens failed during warm-up (cascade of errors on EVERY eval, even '1+1;;')
- Soft reset (reset_fsi_session) didn't fix a genuine session-level problem

NOTE: The file watcher automatically detects .fs/.fsx changes and reloads them via FSI #load (~100ms).
You do NOT need hard_reset just because you edited a source file.

INVALID REASONS (common mistakes):
- Your code had a syntax error or type error → fix your code
- You got 'Operation could not be completed due to earlier error' → fix the earlier code
- You're 'not sure' what's wrong → read the diagnostics, they tell you
- You want to 'start fresh' → soft reset is sufficient if truly needed

Set rebuild=true to run 'dotnet build' before reloading.

WORKFLOW: For test-only changes, use this with rebuild=true instead of the full pack/reinstall cycle.
The full pack/reinstall cycle is only needed when SageFs's own source code changes (SageFs\ or SageFs.Server\).""")>]
    member _.hard_reset_fsi_session(
        rebuild: bool option,
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        let doRebuild = defaultArg rebuild false
        logger.LogDebug("MCP-TOOL: hard_reset_fsi_session called, rebuild={Rebuild}", doRebuild)
        hardResetSession ctx "mcp" doRebuild None wd |> withEcho "hard_reset_fsi_session"

    [<McpServerTool>]
    [<Description("""Check F# code for errors without executing it. Returns diagnostics (errors, warnings) from the F# compiler.

IMPORTANT SCOPE RULES — read before using:
- Code is checked as a SNIPPET in the current FSI session context, NOT as a full project file.
- Definitions you have previously sent via send_fsharp_code ARE in scope.
- Project namespaces are NOT automatically opened. If your code uses types from a loaded module (e.g. MyModule.MyType), include the required `open` statement at the top of the snippet, OR send `open MyModule;;` via send_fsharp_code first.
- Errors about "type X is not defined" usually mean you are missing an `open` statement — they do NOT indicate a real bug in the code.

WHEN TO USE:
- Validating pure F# logic, expressions, or helper functions that are self-contained or only depend on already-opened namespaces.
- Catching syntax errors or type mismatches before executing.

WHEN NOT TO USE:
- Checking a whole file that imports project-specific types without explicit `open` directives in the snippet — you will get false "not defined" errors.
- Full project type-checking: use hard_reset_fsi_session with rebuild=true for that.""")>]
    member _.check_fsharp_code(
        code: string,
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: check_fsharp_code called")
        checkFSharpCode ctx "mcp" code None wd |> withEcho "check_fsharp_code"

    [<McpServerTool>]
    [<Description("""Cancel a running evaluation. Use when an eval is stuck or taking too long. Returns whether a cancellation was performed.

WHEN TO USE:
- A send_fsharp_code call has not returned for an unexpectedly long time (e.g., infinite loop, blocking I/O).
- You submitted code by mistake and want to stop it before it completes.
- The session status shows 'Evaluating' and you need it back in 'Ready' state.

BEHAVIOR:
- Cancellation is cooperative — it requests a .NET CancellationToken cancellation. Most FSI code respects this promptly.
- Code that is stuck in unmanaged I/O or native calls may not cancel immediately.
- After cancellation, the session returns to 'Ready'. The cancelled code's definitions are NOT added to session state.
- Returns 'true' if a running eval was found and cancelled, 'false' if nothing was running.""")>]
    member _.cancel_eval(
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: cancel_eval called")
        cancelEval ctx "mcp" wd |> withEcho "cancel_eval"

    [<McpServerTool>]
    [<Description("""Get code completions at a cursor position. Returns available completions (types, functions, members) for the code at the given position. Useful for discovering APIs before writing code.

CURSOR POSITION:
- cursor_position is a 0-based CHARACTER OFFSET from the start of the code string (not a line/column pair).
- Place the cursor immediately after the partial identifier or the '.' you want completions for.
- Example: for code "System.IO.Fi" with cursor at 12 (after 'Fi'), you get File, FileInfo, FileStream, etc.

SCOPE:
- Completions use the current FSI session context, including all previously sent definitions.
- Project namespaces are available if they are open in the session. Send 'open MyModule;;' first if needed.

WHEN TO USE:
- Before writing a function call to discover what members a type has.
- As an alternative to explore_type when you want contextual completions for a specific code position.
- To check whether a function name exists before attempting to call it.""")>]
    member _.get_completions(
        [<Description("The F# code to get completions for")>] code: string,
        [<Description("Cursor position (0-based character offset) where completions are requested")>] cursor_position: int,
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: get_completions called")
        getCompletions ctx "mcp" code cursor_position wd |> withEcho "get_completions"

    // ── Package Explorer Tools ──────────────────────────────────────

    [<McpServerTool>]
    [<Description("""Retrieve the types, functions, and sub-namespaces available in a given namespace.
Use this to explore .NET and F# APIs without documentation. Provide the fully-qualified namespace name.
Examples: 'System.Collections.Generic', 'Microsoft.FSharp.Collections', 'FSharp.Control'.

WHEN TO USE vs explore_type:
- Use explore_namespace to browse what's inside a namespace (get a list of types and sub-namespaces).
- Use explore_type to drill into the members of a specific type you've already identified.

TIPS:
- If you're unsure of the full namespace, start broad ('System.IO') and drill down from the results.
- Works on both .NET BCL types and types from NuGet packages loaded into the project.
- Use get_completions for interactive code-position-aware completion instead.""")>]
    member _.explore_namespace(
        [<Description("Fully-qualified namespace to explore (e.g. 'System.IO', 'Microsoft.FSharp.Collections')")>] namespaceName: string,
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: explore_namespace called: {Namespace}", namespaceName)
        exploreNamespace ctx "mcp" namespaceName wd |> withEcho "explore_namespace"

    [<McpServerTool>]
    [<Description("""Retrieve the members, constructors, and properties of a specific type.
Use this to discover what methods and properties are available on a type. Provide the fully-qualified type name.
Examples: 'System.String', 'System.Collections.Generic.List', 'Microsoft.FSharp.Collections.List'.

WHEN TO USE vs explore_namespace:
- Use explore_type when you know the type and want its members (constructors, methods, properties, static members).
- Use explore_namespace when you don't know what types exist in a namespace yet.

TIPS:
- For generic types, provide the open form: 'System.Collections.Generic.Dictionary' (not Dictionary<K,V>).
- Works on both .NET BCL types and types from the loaded project's assemblies.
- Results include member signatures, so you can see parameter types and return types before writing code.""")>]
    member _.explore_type(
        [<Description("Fully-qualified type name to explore (e.g. 'System.String', 'System.IO.File')")>] typeName: string,
        [<Description("Working directory of the MCP client. When provided, automatically resolves the correct session for this directory without requiring manual switch_session calls.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: explore_type called: {Type}", typeName)
        exploreType ctx "mcp" typeName wd |> withEcho "explore_type"

    [<McpServerTool>]
    [<Description("""Visualize a discriminated union type as a state machine diagram. Returns JSON with case names, fields, entry/terminal state classification, and an ASCII art diagram. Useful for understanding DU-based domain models as state machines.

WHEN TO USE:
- When a domain model is expressed as a discriminated union and you want to reason about the valid state transitions.
- To generate a diagram for documentation or for understanding a complex DU before writing pattern-match logic.
- Works best with DUs where cases represent lifecycle stages (e.g., Order: Pending | Processing | Shipped | Delivered).

REQUIREMENTS:
- The type must be loaded in the current FSI session (send_fsharp_code its definition first, or it must be in a loaded project).
- Provide the fully-qualified type name (e.g., 'MyApp.Domain.OrderState', not just 'OrderState').

OUTPUT: JSON containing case names, fields per case, which cases are entry points, which are terminal, plus a text-based ASCII state machine diagram.""")>]
    member _.visualize_domain_model(
        [<Description("Fully-qualified DU type name to visualize (e.g. 'MyNamespace.OrderState')")>] typeName: string,
        [<Description("Working directory of the MCP client.")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: visualize_domain_model called: {Type}", typeName)
        visualizeDomainModel ctx "mcp" typeName wd |> withEcho "visualize_domain_model"

    // ── Session Management Tools ──────────────

    [<McpServerTool>]
    [<Description("""Create a new isolated FSI session with the specified project(s). Each session runs in its own worker process with full type isolation.

WHEN TO USE:
- When you need to load a different project than the current session has loaded.
- When you want to test something in a clean environment without affecting the shared session.
- For running tests that require specific project DLLs (e.g., a test project with Expecto or xUnit).
- Multi-project workflows where isolation between session contexts is important.

AFTER CREATION:
- Use the returned session ID with switch_session to route subsequent tool calls to the new session.
- The session warms up asynchronously. Call get_fsi_status (with the session ID) until status shows 'Ready'.
- Use stop_session when finished to free the worker process.

projects: Comma-separated list of absolute or relative .fsproj file paths.""")>]
    member _.create_session(
        [<Description("Comma-separated list of .fsproj files to load")>] projects: string,
        [<Description("Working directory for the session")>] working_directory: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: create_session called: projects={Projects}, dir={Dir}", projects, working_directory)
        let projectList = projects.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.toList
        createSession ctx "mcp" projectList working_directory |> withEcho "create_session"

    [<McpServerTool>]
    [<Description("""List all active FSI sessions with their metadata: session ID, project names, current status, working directory, and last activity timestamp.

WHEN TO USE:
- To find session IDs for use with switch_session, stop_session, or get_fsi_status.
- To check whether multiple sessions are running (each session is an isolated worker process).
- To see which session is currently active (tool calls without an explicit session route to the active one).
- After SageFs restarts or after create_session to confirm the session is registered.""")>]
    member _.list_sessions() : Task<string> =
        logger.LogDebug("MCP-TOOL: list_sessions called")
        listSessions ctx |> withEcho "list_sessions"

    [<McpServerTool>]
    [<Description("""Stop an active FSI session by its ID. The worker process is gracefully shut down and its resources are released.

WHEN TO USE:
- After finishing work in a session created with create_session to free the worker process.
- When a session is stuck and a hard_reset_fsi_session hasn't helped — stop it and create a fresh one.
- To clean up sessions that are no longer needed in multi-session workflows.

NOTE: Stopping the last (or only) session will leave no active session. You will need to create_session or restart SageFs. Use list_sessions to see available session IDs before stopping.""")>]
    member _.stop_session(
        [<Description("The session ID to stop (from list_sessions)")>] session_id: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: stop_session called: id={Id}", session_id)
        stopSession ctx session_id |> withEcho "stop_session"

    [<McpServerTool>]
    [<Description("""Switch the active FSI session. All subsequent tool calls that accept working_directory will route to this session.

WHEN TO USE:
- After create_session to make the new session the active target for tool calls.
- In multi-project workflows when switching context between two loaded sessions.
- When the working_directory auto-routing picks the wrong session and you need to explicitly override it.

ROUTING BEHAVIOR:
- Once switched, all tools that auto-route by working_directory will use this session unless the working_directory matches a different session.
- If you provide working_directory in a tool call, it overrides the active session for that call.
- Use list_sessions to see available session IDs.""")>]
    member _.switch_session(
        [<Description("Session ID to switch to (from list_sessions)")>] session_id: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: switch_session called: id={Id}", session_id)
        switchSession ctx "mcp" session_id |> withEcho "switch_session"

    // ── Elm State Tools ──────────────────────────────────────────

    [<McpServerTool>]
    [<Description("""Get the current Elm model state rendered as regions. Shows editor content, recent output, diagnostics, and sessions. Useful for understanding what SageFs is currently displaying.

WHEN TO USE:
- To understand the current state of the SageFs TUI/GUI without taking a screenshot.
- To read the most recently displayed eval output or error message as shown in the UI.
- To check what the user is currently viewing in their SageFs terminal (active pane, cursor position, etc.).
- As a debugging tool to verify the UI model reflects the expected state after an operation.

OUTPUT: A text rendering of each named UI region (header, editor, output, test panel, status bar, etc.) showing what is currently displayed.""")>]
    member _.get_elm_state() : Task<string> =
        logger.LogDebug("MCP-TOOL: get_elm_state called")
        getElmState ctx |> withEcho "get_elm_state"

    // ── Live Testing Tools ──────────────────────────────────────

    [<McpServerTool>]
    [<Description("""Get current live test status. Returns the enabled state, a summary (total/passed/failed/stale/running counts), and per-test status entries.

WHEN TO USE:
- To see which tests are currently passing or failing without running them explicitly.
- After editing code, to check whether the automatically-triggered test run has completed.
- Before calling run_tests, to understand the current baseline.
- With the optional file filter to focus on tests from a specific source file you're working on.

STATUS VALUES per test:
- Passed: last run succeeded.
- Failed: last run produced a test failure (not a compilation error).
- Stale: test was passing but the code it covers has changed and it hasn't re-run yet.
- Running: test is currently executing.
- NotRun: test has been discovered but never run.""")>]
    member _.get_live_test_status(
        [<Description("Optional file path to filter tests by source file.")>]
        file: string
    ) : Task<string> =
        let filter = match System.String.IsNullOrWhiteSpace file with | true -> None | false -> Some file
        logger.LogDebug("MCP-TOOL: get_live_test_status called, file={File}", file)
        getLiveTestStatus ctx filter |> withEcho "get_live_test_status"

    [<McpServerTool>]
    [<Description("""Enable live testing. When enabled, tests automatically re-run after each hot reload whenever the code they depend on changes.

BEHAVIOR:
- Only tests whose dependencies include the changed symbol are re-run (not the full suite).
- Run policies per category (set via set_run_policy) still apply: a category set to 'disabled' won't run even when live testing is on.
- Default run policies: 'unit' runs on every change; 'integration' and 'browser' default to 'demand' (explicit only).

WHEN TO USE:
- At the start of a TDD session to get instant feedback as you write code.
- Pair with get_live_test_status to poll results after edits.

NOTE: Live testing requires a test project to be loaded in the session. Check get_fsi_status to confirm the test project is loaded.""")>]
    member _.enable_live_testing() : Task<string> =
        logger.LogDebug("MCP-TOOL: enable_live_testing called")
        setLiveTesting ctx true |> withEcho "enable_live_testing"

    [<McpServerTool>]
    [<Description("""Disable live testing. Tests will not run automatically after hot reload.

WHEN TO USE:
- When you are making broad refactoring changes and don't want the test runner triggering on every intermediate edit.
- To reduce resource consumption during long code generation sessions.
- When working on test files themselves where partial test definitions would cause spurious failures.

NOTE: Disabling live testing does not prevent explicit test runs via run_tests. It only stops the automatic hot-reload-triggered runs.""")>]
    member _.disable_live_testing() : Task<string> =
        logger.LogDebug("MCP-TOOL: disable_live_testing called")
        setLiveTesting ctx false |> withEcho "disable_live_testing"

    [<McpServerTool>]
    [<Description("""Set run policy for a test category. Controls WHEN tests in that category are automatically triggered by the live testing engine.

CATEGORIES: unit | integration | browser | benchmark | architecture | property

POLICIES AND WHAT THEY MEAN:
- every: Tests re-run on EVERY hot reload that touches a symbol they depend on. Best for fast unit tests (< 1s each). This is the default for 'unit'.
- save: Tests run only when a source file is explicitly saved (not on every keystroke/hot reload). Good for tests that are fast enough to run frequently but not on every change.
- demand: Tests NEVER run automatically — only when explicitly triggered via run_tests. Use for slow integration/browser/benchmark tests that shouldn't interrupt a coding session.
- disabled: Tests are not run at all, not even via run_tests. Use to suppress a broken or irrelevant category without deleting tests.

DEFAULT POLICIES (on fresh session):
- unit: every
- integration: demand
- browser: demand
- benchmark: disabled
- architecture: demand
- property: every

INTERACTION WITH enable/disable_live_testing:
- disable_live_testing is a global off switch — NO automatic runs happen regardless of policies.
- enable_live_testing restores the per-category policies (e.g., 'unit: every' resumes).
- set_run_policy changes policies whether live testing is enabled or not; they take effect when re-enabled.

EXAMPLE WORKFLOW — reduce noise during broad refactoring:
  set_run_policy(category='unit', policy='save')   ← run unit tests on save only
  set_run_policy(category='property', policy='demand')  ← stop property tests auto-running

EXAMPLE WORKFLOW — restore defaults after focused work:
  set_run_policy(category='unit', policy='every')
  set_run_policy(category='property', policy='every')""")>]
    member _.set_run_policy(
        [<Description("Test category: unit, integration, browser, benchmark, architecture, property")>]
        category: string,
        [<Description("Run policy: every, save, demand, disabled")>]
        policy: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: set_run_policy called: category={Category}, policy={Policy}", category, policy)
        setRunPolicy ctx category policy |> withEcho "set_run_policy"

    [<McpServerTool>]
    [<Description("""Configure test execution timeouts. Affects both automatic (hot-reload-triggered) and explicit (run_tests) test runs.

PARAMETERS:
- per_test_seconds: Each individual test is cancelled if it exceeds this duration. Default: 5s. Increase for tests with real I/O or network calls.
- global_run_seconds: The entire batch of tests is cancelled if the total run time exceeds this. Default: 120s.

WHEN TO USE:
- When integration tests are being killed too early (increase per_test_seconds, e.g. to 30s).
- When a runaway test is hanging and you want a tighter global deadline (decrease global_run_seconds).
- Call with both values as 0 (or omit) to read the current timeout configuration without changing it.

NOTE: Timeout changes take effect immediately on the next test run.""")>]
    member _.set_test_timeouts(
        [<Description("Per-test timeout in seconds (default 5). Each individual test is cancelled if it exceeds this.")>]
        per_test_seconds: float,
        [<Description("Global run timeout in seconds (default 120). The entire test batch is cancelled if it exceeds this.")>]
        global_run_seconds: float
    ) : Task<string> =
        let pt = match per_test_seconds <= 0.0 with | true -> None | false -> Some per_test_seconds
        let gr = match global_run_seconds <= 0.0 with | true -> None | false -> Some global_run_seconds
        logger.LogDebug("MCP-TOOL: set_test_timeouts called: per_test={PerTest}, global={Global}", per_test_seconds, global_run_seconds)
        setTestTimeouts ctx pt gr |> withEcho "set_test_timeouts"

    [<McpServerTool>]
    [<Description("""Get test infrastructure state: enabled flag, currently-running status, provider list, per-category run policies, and a test summary.

WHEN TO USE:
- To see the overall health of the live testing subsystem (is it enabled? which providers are active? what are the run policies?).
- To diagnose why tests are or aren't running automatically (check run policies and enabled state).
- As a quick dashboard view: combine with get_live_test_status for a full picture.

DIFFERENCE FROM get_live_test_status:
- get_test_trace: infrastructure config — enabled state, providers, policies, timing metadata.
- get_live_test_status: per-test results — which individual tests passed, failed, or are stale.""")>]
    member _.get_test_trace() : Task<string> =
        logger.LogDebug("MCP-TOOL: get_test_trace called")
        getTestTrace ctx |> withEcho "get_test_trace"

    [<McpServerTool>]
    [<Description("""Run tests explicitly. Without parameters, runs all discovered unit tests.
Use pattern to filter by test name (substring match on FullName or DisplayName).
Use category to filter by test category: unit, integration, browser, benchmark, architecture, property.
Use timeout_seconds to wait for results (default 30). Set to 0 for fire-and-forget.

HOT RELOAD SAFETY:
- If a file edit / hot reload is still in progress when this is called, it automatically waits up to 15 seconds for the reload to finish before running tests. This ensures results always reflect the latest code. A '⏳ Waited Xms for hot reload' note is prepended to the result if a wait occurred.

PATTERN MATCHING:
- pattern is a SUBSTRING match — 'login' matches 'should login user', 'LoginService tests', etc.
- Empty or omitted pattern runs all tests in the selected category.
- If pattern matches nothing, returns a message saying no tests were found — it does NOT fall back to running all tests.
- To run all tests in a specific module, use the module name as the pattern.

CATEGORY FILTER:
- Omit to run only 'unit' tests (the default safe set).
- Use category='integration' for integration tests (slower, may have side effects).
- Use category='property' for FsCheck/FsCheck.Xunit property-based tests.
- Use category='benchmark' to run performance benchmarks (returns timing data, very slow).

TIMEOUT BEHAVIOR:
- timeout_seconds is how long THIS CALL waits for results — not a per-test execution limit.
- Per-test and global test timeouts are configured separately via set_test_timeouts.
- timeout_seconds=0: fires the run and returns immediately. Poll get_live_test_status for completion.
- Increase beyond 30 for integration or end-to-end tests that legitimately take longer.

RETURN VALUE:
- On completion: summary with pass/fail counts and names of any failing tests with failure messages.
- On timeout: a message indicating tests are still running. Use get_live_test_status to poll.""")>]
    member _.run_tests(
        [<Description("Optional test name substring to match (case-insensitive). Omit to run all tests in the category.")>]
        pattern: string,
        [<Description("Optional category filter: unit, integration, browser, benchmark, architecture, property. Omit for 'unit'.")>]
        category: string,
        [<Description("Seconds to wait for results before returning (default 30). Use 0 to fire-and-forget.")>]
        timeout_seconds: int
    ) : Task<string> =
        let p = match System.String.IsNullOrWhiteSpace pattern with | true -> None | false -> Some pattern
        let c = match System.String.IsNullOrWhiteSpace category with | true -> None | false -> Some category
        let t = match timeout_seconds <= 0 with | true -> 0 | false -> timeout_seconds
        logger.LogDebug("MCP-TOOL: run_tests called, pattern={Pattern}, category={Category}, timeout={Timeout}", pattern, category, t)
        runTests ctx p c t |> withEcho "run_tests"

    [<McpServerTool>]
    [<Description("""Explain why a test was selected to run. Shows the trigger reason, which changed symbols cover the test, duration from last run, and flaky status.
Matches by substring on FullName or DisplayName — returns explanations for all matching tests.

TRIGGER REASON TYPES:
- SymbolCoverage: One or more symbols that changed are covered by this test (the hot-path, most informative).
- NewTest: The test was newly discovered and has no prior run data.
- ExplicitRun: Test was triggered via run_tests, not automatically.
- DepGraphFallback: Dependency graph data was unavailable; the test ran as a precaution.

FLAKY STATUS:
- A test is flagged as 'flaky' when it has alternated between Passed and Failed in recent runs without any code changes to covered symbols. Use this to identify tests that need stabilization before relying on them as live guards.

USE CASE: When you see a test ran unexpectedly, call this to understand why. When live testing seems to be running too many tests, inspect a few with this tool to understand the dependency graph breadth.""")>]
    member _.explain_test_run(
        [<Description("Test name or substring to match against FullName or DisplayName")>]
        test_name: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: explain_test_run called, test={Test}", test_name)
        explainTestRun ctx test_name |> withEcho "explain_test_run"

    [<McpServerTool>]
    [<Description("""Query which tests cover a given symbol. Returns all tests that transitively depend on the symbol via the dependency graph, along with their last result status.

SYMBOL NAME FORMAT:
- Use the fully-qualified FCS name: 'MyModule.myFunction', 'MyNamespace.MyType', 'MyNamespace.MyType.myMethod'.
- For nested modules: 'Outer.Inner.functionName'.
- Exact match required — partial names and wildcards are not supported.
- To find the right fully-qualified name, check the source file's module/namespace declarations.

RETURN VALUE:
- List of test names + last status (Passed / Failed / NotRun) for each test that transitively covers the symbol.
- 'Transitively covers' means the test's dependency graph includes the symbol, not just direct calls.
- If no tests cover the symbol, returns an empty result — this is normal for new or unused functions.

USE CASE: After extracting or renaming a function, call this to see which tests protect it. Before deleting a function, call this to confirm no tests depend on it.""")>]
    member _.query_test_coverage(
        [<Description("Fully-qualified symbol name (e.g. 'MyModule.myFunction', 'MyNamespace.MyType.myMethod'). Exact match required.")>]
        symbol: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: query_test_coverage called, symbol={Symbol}", symbol)
        queryTestCoverage ctx symbol |> withEcho "query_test_coverage"

    [<McpServerTool>]
    [<Description("""Get per-line coverage data for a specific file. Returns JSON with line-level coverage annotations including which tests cover each line, coverage health status, and branch coverage detail.

FILE PATH:
- Accepts a full absolute path OR a partial filename (e.g. 'MyModule.fs' or just 'MyModule').
- Partial paths are matched against all files in the session's loaded projects.
- If multiple files match a partial path, returns data for the first match.

COVERAGE HEALTH VALUES (per line):
- AllPassing: all tests that cover this line are currently passing — this line is fully protected.
- SomeFailing: at least one test covering this line is currently failing — the line may be broken or the test is broken.
- NoData: no tests found for this line yet (line not reachable, or test project not loaded).

DATA SOURCE:
- Uses instrumentation bitmaps when available (precise, from a recent full test run).
- Falls back to dependency graph synthesis when bitmaps are stale or unavailable (approximate).
- The JSON includes a 'source' field indicating which mode was used.

USE CASE: Use to identify which lines of a file have no test coverage, or to see which tests protect a given line before editing it.""")>]
    member _.get_file_coverage(
        [<Description("File path to get coverage for. Accepts full absolute path or partial filename (e.g. 'MyModule.fs').")>]
        file: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: get_file_coverage called, file={File}", file)
        getFileCoverage ctx file |> withEcho "get_file_coverage"

    [<McpServerTool>]
    [<Description("""Get enriched failure context for a test that recently transitioned Passed→Failed. Shows time since last pass, causal changes (which symbols or files changed), property violation details, and a human-readable summary narrative.

LOOKUP WINDOW:
- Looks back through the last 20 run records for each matching test to find the most recent Passed→Failed transition.
- If a test has only ever failed (never passed), it has no transition record and is excluded from results.

CAUSAL CHANGES:
- Lists the symbols and files that changed in the interval between the last passing run and the first failing run.
- This is a correlation, not a proof of causation — but it narrows down the likely culprit significantly.

PROPERTY VIOLATION DETAILS:
- For FsCheck property tests, includes the failing counterexample (the specific inputs that violated the property).
- For assertion tests (Expecto/xUnit), includes the exception message and stack trace excerpt.

RETURN VALUE:
- Returns narratives for ALL matching tests that have a recent Passed→Failed transition.
- Returns a 'no recent failures found' message if none of the matched tests have transitioned recently.

WORKFLOW: When run_tests reports a failure, call explain_test_failure with the test name to see what changed and why it broke — without manually diffing recent commits.""")>]
    member _.explain_test_failure(
        [<Description("Test name or substring to match against FullName or DisplayName")>]
        test_name: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: explain_test_failure called, test={Test}", test_name)
        explainTestFailure ctx test_name |> withEcho "explain_test_failure"

    // ── Feature Analysis Tools (P15–P19) ───────────────────────

    [<McpServerTool>]
    [<Description("""Decompose an F# pipeline expression into individual stages and classify each stage's purity.

INPUT: A pipeline expression using |> operators (e.g., 'xs |> List.filter isEven |> List.map string |> String.concat ","').
OUTPUT: Numbered stages with purity classification: ● = pure, ⚡ = effectful, ? = unknown.

This is a stateless tool — it analyzes the code string directly without needing an active session.
Use this to understand complex pipelines before modifying them, or to identify effectful stages that need special handling.""")>]
    member _.decompose_pipeline(
        [<Description("F# pipeline expression to decompose (e.g., 'xs |> List.map f |> List.filter g')")>]
        code: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: decompose_pipeline called, code length={Len}", code.Length)
        decomposePipeline code |> withEcho "decompose_pipeline"

    [<McpServerTool>]
    [<Description("""Run a full diagnostic analysis of the current session.

Composes 6 feature modules into one coherent report: test failure narratives, cell dependency graph,
eval provenance (staleness), ripple re-evaluation plan, Ghostwriter suggestions, and performance timeline.

OUTPUT: A diagnostic report with:
- All currently failing tests with causal change analysis (which symbols/files caused the failure)
- Which cells are affected and their staleness (Fresh vs StaleUpstream)
- A topological ripple plan showing the re-evaluation order
- Ranked code suggestions from Ghostwriter based on current scope
- Performance context with sparkline and P50/P95 percentiles
- Severity classification: Info (no issues), Warning (perf anomaly), Critical (test failures)
- A ≤10 line human-readable summary

WORKFLOW: Call this after a test fails to get a complete picture of what happened, why, and what to do next.
This replaces calling explain_test_failure + plan_ripple + get_eval_timeline + suggest_next_cell separately.""")>]
    member _.diagnose() : Task<string> =
        logger.LogDebug("MCP-TOOL: diagnose called")
        diagnose ctx |> withEcho "diagnose"

    [<McpServerTool>]
    [<Description("""Plan a cascade re-evaluation (ripple) for changed cells.

Given a set of cell IDs that have changed, computes the topologically-ordered list of downstream cells
that need to be re-evaluated. Uses the live dependency graph built from the current session's eval history.

INPUT: Comma-separated cell IDs (integers) that have changed.
OUTPUT: Ordered list of cells to re-evaluate, with their code snippets and current status.

WORKFLOW: After editing a binding, use this tool to see which cells would be affected before re-evaluating them.""")>]
    member _.plan_ripple(
        [<Description("Comma-separated cell IDs that changed (e.g., '0,2,5')")>]
        changed_cells: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: plan_ripple called, cells={Cells}", changed_cells)
        planRipple ctx changed_cells |> withEcho "plan_ripple"

    [<McpServerTool>]
    [<Description("""Preview a "what if" scenario: what would change if a binding had a different value?

Identifies the cell that produces the named binding, then plans a ripple of all downstream cells
that would need re-evaluation. Shows the override and affected cells without actually executing anything.

INPUT: binding_name — the name of the binding to override; new_code — the replacement expression.
OUTPUT: Override summary, count of affected cells, and the ripple plan.

WORKFLOW: Use this to explore hypothetical changes safely before committing to them.""")>]
    member _.preview_what_if(
        [<Description("Name of the binding to override (e.g., 'threshold')")>]
        binding_name: string,
        [<Description("New F# expression for the binding (e.g., '0.75')")>]
        new_code: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: preview_what_if called, binding={Name}", binding_name)
        previewWhatIf ctx binding_name new_code |> withEcho "preview_what_if"

    [<McpServerTool>]
    [<Description("""Get type-directed suggestions for what to evaluate next.

Analyzes all bindings currently in scope and generates contextually-appropriate suggestions
based on their types. For example, a list binding gets List.length, List.head, List.sort suggestions;
an option binding gets Option.defaultValue, Option.map suggestions.

OUTPUT: Ranked suggestions with confidence scores, code snippets, and explanations.

WORKFLOW: When you're not sure what to try next in the REPL, call this for intelligent suggestions.""")>]
    member _.suggest_next_cell() : Task<string> =
        logger.LogDebug("MCP-TOOL: suggest_next_cell called")
        suggestNextCell ctx |> withEcho "suggest_next_cell"

    [<McpServerTool>]
    [<Description("""Get the session filmstrip — a visual history of all evaluations in the current session.

Shows each evaluation as a "frame" with its index, code snippet, binding count, duration, and test summary.
Use the optional filter parameter to search for specific frames by label substring.

OUTPUT: Overview statistics followed by individual frame cards.

WORKFLOW: Use to review what happened in a session, find when a binding was introduced, or understand the session timeline.""")>]
    member _.get_session_filmstrip(
        [<Description("Optional filter string to search frames by label (case-insensitive substring match)")>]
        filter: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: get_session_filmstrip called, filter={Filter}", filter)
        let filterOpt = match System.String.IsNullOrWhiteSpace filter with | true -> None | false -> Some filter
        getSessionFilmstrip ctx filterOpt |> withEcho "get_session_filmstrip"

    // ── Phase 1b: Orphaned module MCP tools ──

    [<McpServerTool>]
    [<Description("""Export the current session as a notebook-style .fsx file with cell metadata.

Each cell preserves its code, output, index, and dependencies as structured comments.
The exported format can be re-imported with importNotebook.

OUTPUT: A complete .fsx file string with cell boundaries and metadata.

WORKFLOW: Use this to save your interactive session as a portable, re-runnable notebook.""")>]
    member _.export_notebook(
        [<Description("Optional project name for the notebook header (defaults to 'SageFs Session')")>]
        project_name: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: export_notebook called, project={Name}", project_name)
        let nameOpt = match System.String.IsNullOrWhiteSpace project_name with | true -> None | false -> Some project_name
        exportNotebook ctx nameOpt |> withEcho "export_notebook"

    [<McpServerTool>]
    [<Description("""Export the current session as a clean, topologically-sorted .fsx transcript.

Uses the cell dependency graph to order cells so that dependencies come before dependents.
Deduplicates cells and strips metadata — the result is a minimal, runnable .fsx script.

OUTPUT: A clean .fsx file string ready to run with `dotnet fsi`.

WORKFLOW: Use this to extract a clean, reproducible script from an exploratory session.""")>]
    member _.export_session_transcript(
        [<Description("Optional project name for the transcript header (defaults to 'SageFs Session')")>]
        project_name: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: export_session_transcript called, project={Name}", project_name)
        let nameOpt = match System.String.IsNullOrWhiteSpace project_name with | true -> None | false -> Some project_name
        exportSessionTranscript ctx nameOpt |> withEcho "export_session_transcript"

    [<McpServerTool>]
    [<Description("""Get the message journal — a structured audit log of eval events.

Synthesizes journal entries from eval history, classifying successful evals as Info
and failed evals as Error. Supports filtering by minimum severity level and source.

OUTPUT: Journal summary statistics followed by timestamped, level-tagged entries.

WORKFLOW: Use this for observability — review what happened, filter to errors only, or trace eval activity.""")>]
    member _.get_message_journal(
        [<Description("Minimum severity level to include: 'debug', 'info', 'warn', or 'error' (defaults to all)")>]
        min_level: string,
        [<Description("Optional source filter — only show entries from matching sources (case-insensitive)")>]
        source: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: get_message_journal called, level={Level}, source={Source}", min_level, source)
        let levelOpt = match System.String.IsNullOrWhiteSpace min_level with | true -> None | false -> Some min_level
        let sourceOpt = match System.String.IsNullOrWhiteSpace source with | true -> None | false -> Some source
        getMessageJournal ctx levelOpt sourceOpt |> withEcho "get_message_journal"

    [<McpServerTool>]
    [<Description("""Get eval timeline with performance sparkline and percentile statistics.

Shows a visual sparkline of recent eval durations, plus P50/P95/P99 and mean latency.
Also lists the most recent evaluations with their cell IDs, status icons, and durations.

OUTPUT: Sparkline visualization, percentile statistics, and recent eval entries.

WORKFLOW: Use this to monitor eval performance trends and identify slow cells.""")>]
    member _.get_eval_timeline(
        [<Description("Width of the sparkline in characters (defaults to 20)")>]
        sparkline_width: int
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: get_eval_timeline called, width={Width}", sparkline_width)
        let widthOpt = match sparkline_width with | 0 -> None | w -> Some w
        getEvalTimeline ctx widthOpt |> withEcho "get_eval_timeline"

    [<McpServerTool>]
    [<Description("""Manage the session scratch pad — view, export, or promote ephemeral code snippets.

Actions:
- 'list': Show all snippets with their IDs, code, and results
- 'export': Export all snippets as a .fsx script
- 'promote': Extract only the successfully evaluated snippets as clean code

The scratch pad is built from the current session's eval history.

OUTPUT: Depends on action — list of snippets, .fsx script, or promoted code.

WORKFLOW: Use 'list' to review snippets, 'export' for a full dump, 'promote' to keep only working code.""")>]
    member _.manage_scratch_pad(
        [<Description("Action to perform: 'list', 'export', or 'promote'")>]
        action: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: manage_scratch_pad called, action={Action}", action)
        manageScratchPad ctx action None None |> withEcho "manage_scratch_pad"

    [<McpServerTool>]
    [<Description("""Get a diff between recent eval outputs — before vs after comparison.

Compares the two most recent eval outputs (or outputs for a specific cell) and shows
a line-by-line diff with added/removed/modified/unchanged classifications.

OUTPUT: Diff summary with counts of changes and line-by-line breakdown.

WORKFLOW: Use after re-evaluating a cell to see exactly what changed in the output.""")>]
    member _.get_eval_diff(
        [<Description("Optional cell index to diff (defaults to comparing the two most recent evals)")>]
        cell_index: int
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: get_eval_diff called, cellIndex={Idx}", cell_index)
        let idxOpt = match cell_index with | 0 -> None | i -> Some i
        getEvalDiff ctx idxOpt |> withEcho "get_eval_diff"
