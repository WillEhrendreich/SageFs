module SageFs.Server.McpTools

open System.ComponentModel
open System.Threading.Tasks
open ModelContextProtocol.Server
open Microsoft.Extensions.Logging
open SageFs.AppState
open SageFs.McpTools

/// Emoji per tool category — printed once as a header, not per-line
/// Echo MCP tool results to the SageFs console for visibility
let private withEcho (toolName: string) (t: Task<string>) : Task<string> =
  task {
    let! result = t
    let normalized = result.Replace("\r\n", "\n").Replace("\n", "\r\n")
    eprintfn "\u001b[90m>> %s\u001b[0m" toolName
    eprintfn "\u001b[90m%s\u001b[0m" normalized
    return result
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

ERROR HANDLING (CRITICAL):
- If you get an error, it is almost certainly YOUR code that has the bug. Read the diagnostics carefully and fix your code.
- 'Operation could not be completed due to earlier error' means a PREVIOUS statement had a compile error, so definitions from it were never created. Fix the original error and resubmit that code first.
- The session is NOT corrupted by errors. Do NOT call reset_fsi_session or hard_reset_fsi_session because of eval errors. Fix your code instead.
- Submit smaller pieces (one definition per call) to isolate which part has the error.

WORKFLOW: Use this tool instead of dotnet build or dotnet run. SageFs IS your compiler and runtime.""")>]
    member _.send_fsharp_code(agentName: string, code: string) : Task<string> =
        logger.LogDebug("MCP-TOOL: send_fsharp_code called by {AgentName}: {Code}", agentName, code)
        sendFSharpCode ctx agentName code OutputFormat.Text None
    
    [<McpServerTool>]
    [<Description("""Load and execute an F# script file (.fsx). The file is parsed into individual statements and each is sent to FSI separately, so partial progress is preserved if a statement fails.""")>]
    member _.load_fsharp_script(agentName: string, filePath: string) : Task<string> = 
        logger.LogDebug("MCP-TOOL: load_fsharp_script called: {FilePath}", filePath)
        loadFSharpScript ctx agentName filePath None |> withEcho "load_fsharp_script"
    
    [<McpServerTool>]
    [<Description("Get recent FSI events including evaluations, errors, and script loads. Returns the most recent N events (default 10) with timestamps and sources.")>]
    member _.get_recent_fsi_events(count: int option) : Task<string> = 
        let eventCount = defaultArg count 10
        logger.LogDebug("MCP-TOOL: get_recent_fsi_events called: count={Count}", eventCount)
        getRecentEvents ctx eventCount |> withEcho "get_recent_fsi_events"
    
    [<McpServerTool>]
    [<Description("Get the current FSI session status: startup configuration, loaded projects, session statistics, and available capabilities. Use to verify session health or discover what is loaded.")>]
    member _.get_fsi_status() : Task<string> =
        logger.LogDebug("MCP-TOOL: get_fsi_status called")
        getStatus ctx |> withEcho "get_fsi_status"

    [<McpServerTool>]
    [<Description("Get detailed startup information: loaded projects, enabled features, and command-line arguments. Use to understand what capabilities are available in the current session.")>]
    member _.get_startup_info() : Task<string> =
        logger.LogDebug("MCP-TOOL: get_startup_info called")
        getStartupInfo ctx |> withEcho "get_startup_info"

    [<McpServerTool>]
    [<Description("DiscoverF# projects (.fsproj) and solutions (.sln/.slnx) in the current working directory. Useful for determining what projects can be loaded with 'SageFs --proj'.")>]
    member _.get_available_projects() : Task<string> =
        logger.LogDebug("MCP-TOOL: get_available_projects called")
        getAvailableProjects ctx |> withEcho "get_available_projects"

    [<McpServerTool>]
    [<Description("""Soft-reset the FSI session. All previously defined types, values, and bindings will be lost. The session is re-warmed with project namespaces.

WHEN TO USE (rare):
- The session warm-up itself failed (you see cascade errors on EVERY submission, even trivial ones like '1+1;;')
- You intentionally want to clear all definitions and start fresh

WHEN NOT TO USE (common mistake):
- You got an eval error — that means YOUR code has a bug. Fix your code and resubmit instead.
- 'Operation could not be completed due to earlier error' — this is NOT session corruption. A previous submission failed. Fix and resubmit that code.
- You're not sure what went wrong — read the error diagnostics first, they tell you exactly what's wrong.

This is a SOFT reset — DLL locks are retained. Use hard_reset_fsi_session only if modules failed to load during warm-up.""")>]
    member _.reset_fsi_session() : Task<string> =
        logger.LogDebug("MCP-TOOL: reset_fsi_session called")
        resetSession ctx None |> withEcho "reset_fsi_session"

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
    member _.hard_reset_fsi_session(rebuild: bool option) : Task<string> =
        let doRebuild = defaultArg rebuild false
        logger.LogDebug("MCP-TOOL: hard_reset_fsi_session called, rebuild={Rebuild}", doRebuild)
        hardResetSession ctx doRebuild None |> withEcho "hard_reset_fsi_session"

    [<McpServerTool>]
    [<Description("""Check F#code for errors without executing it. Returns diagnostics (errors, warnings) from the F# compiler.
Use this to pre-validate code before sending it with send_fsharp_code, or to check syntax and types without side effects.

WORKFLOW: Use this instead of dotnet build for type-checking. SageFs IS your compiler.""")>]
    member _.check_fsharp_code(code: string) : Task<string> =
        logger.LogDebug("MCP-TOOL: check_fsharp_code called")
        checkFSharpCode ctx code None |> withEcho "check_fsharp_code"

    [<McpServerTool>]
    [<Description("Cancela running evaluation. Use when an eval is stuck or taking too long. Returns whether a cancellation was performed.")>]
    member _.cancel_eval() : Task<string> =
        logger.LogDebug("MCP-TOOL: cancel_eval called")
        cancelEval ctx |> withEcho "cancel_eval"

    [<McpServerTool>]
    [<Description("Get codecompletions at a cursor position. Returns available completions (types, functions, members) for the code at the given position. Useful for discovering APIs before writing code.")>]
    member _.get_completions(
        [<Description("The F# code to get completions for")>] code: string,
        [<Description("Cursor position (0-based character offset) where completions are requested")>] cursor_position: int
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: get_completions called")
        getCompletions ctx code cursor_position |> withEcho "get_completions"

    // ── Package Explorer Tools ──────────────────────────────────────

    [<McpServerTool>]
    [<Description("""Retrieves the types, functions, and sub-namespaces available in a given namespace.
Use this to explore .NET and F# APIs without documentation. Provide the fully-qualified namespace name.
Examples: 'System.Collections.Generic', 'Microsoft.FSharp.Collections', 'FSharp.Control'.""")>]
    member _.explore_namespace(
        [<Description("Fully-qualified namespace to explore (e.g. 'System.IO', 'Microsoft.FSharp.Collections')")>] namespaceName: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: explore_namespace called: {Namespace}", namespaceName)
        exploreNamespace ctx namespaceName |> withEcho "explore_namespace"

    [<McpServerTool>]
    [<Description("""Retrieves the members, constructors, and properties of a specific type.
Use this to discover what methods and properties are available on a type. Provide the fully-qualified type name.
Examples: 'System.String', 'System.Collections.Generic.List', 'Microsoft.FSharp.Collections.List'.""")>]
    member _.explore_type(
        [<Description("Fully-qualified type name to explore (e.g. 'System.String', 'System.IO.File')")>] typeName: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: explore_type called: {Type}", typeName)
        exploreType ctx typeName |> withEcho "explore_type"

    // ── Session Management Tools (daemon mode only) ──────────────

    [<McpServerTool>]
    [<Description("""Create a new FSI session with the specified project(s). Each session runs in its own worker process with full isolation.

Returns session info including the session ID needed for routing commands to this session.
Only available in daemon mode (SageFs -d). In single-session mode, the session is managed automatically.""")>]
    member _.create_session(
        [<Description("Comma-separated list of .fsproj files to load")>] projects: string,
        [<Description("Working directory for the session")>] working_directory: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: create_session called: projects={Projects}, dir={Dir}", projects, working_directory)
        let projectList = projects.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.toList
        createSession ctx projectList working_directory |> withEcho "create_session"

    [<McpServerTool>]
    [<Description("""List all active FSI sessions with their metadata: session ID, project names, state, working directory, and last activity.
Only available in daemon mode (SageFs -d).""")>]
    member _.list_sessions() : Task<string> =
        logger.LogDebug("MCP-TOOL: list_sessions called")
        listSessions ctx |> withEcho "list_sessions"

    [<McpServerTool>]
    [<Description("""Stop an active FSI session by its ID. The worker process is gracefully shut down.
Use list_sessions to see available session IDs. Only available in daemon mode (SageFs -d).""")>]
    member _.stop_session(
        [<Description("The session ID to stop (from list_sessions)")>] session_id: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: stop_session called: id={Id}", session_id)
        stopSession ctx session_id |> withEcho "stop_session"
