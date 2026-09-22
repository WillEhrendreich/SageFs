namespace SageFs

open Microsoft.Extensions.Logging
open Microsoft.FSharp.Reflection

[<RequireQualifiedAccess>]
type BuildDiagnosticSeverity =
  | Blocking
  | Warning

/// One line of `dotnet build` output classified as a diagnostic. Location is
/// None when the line has no MSBuild `path(line,col):` shape — a build-tool
/// crash or a bare summary line has nothing to point at, and that absence is
/// a real fact about the diagnostic, not something to paper over with a
/// fabricated 0,0 location.
type BuildDiagnostic = {
  File: string option
  Line: int option
  Column: int option
  Severity: BuildDiagnosticSeverity
  /// The compiler/MSBuild diagnostic code (e.g. "FS0039"), when the line
  /// carried one — kept separate from Message so a surface can link or
  /// filter by code without re-parsing text.
  Code: string option
  Message: string
}

module BuildDiagnostic =
  let private msbuildLine =
    System.Text.RegularExpressions.Regex(
      @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s+(?<code>\S+):\s*(?<message>.*?)\s*(?:\[.*\])?$",
      System.Text.RegularExpressions.RegexOptions.Compiled)

  /// Parse one line of `dotnet build` output. A line that doesn't match
  /// MSBuild's "path(line,col): severity CODE: message [project]" shape
  /// becomes a location-less Error diagnostic carrying the raw text verbatim.
  let ofLine (line: string) : BuildDiagnostic =
    let trimmed = line.Trim()
    let m = msbuildLine.Match(trimmed)
    match m.Success with
    | true ->
      { File = Some m.Groups.["file"].Value
        Line = Some (int m.Groups.["line"].Value)
        Column = Some (int m.Groups.["col"].Value)
        Severity =
          match m.Groups.["severity"].Value with
          | "warning" -> BuildDiagnosticSeverity.Warning
          | _ -> BuildDiagnosticSeverity.Blocking
        Code = Some m.Groups.["code"].Value
        Message = m.Groups.["message"].Value }
    | false ->
      { File = None; Line = None; Column = None; Severity = BuildDiagnosticSeverity.Blocking; Code = None; Message = trimmed }

  /// Plain factual text — no call to action. Every surface (dashboard card,
  /// MCP tool response, HTTP body) words its own hint from this data instead
  /// of inheriting one baked into the domain error. Faithfully reconstructs
  /// MSBuild's own line shape for a parsed diagnostic.
  let describe (diagnostics: BuildDiagnostic list) : string =
    let severityWord = function
      | BuildDiagnosticSeverity.Blocking -> "error"
      | BuildDiagnosticSeverity.Warning -> "warning"
    diagnostics
    |> List.map (fun d ->
      match d.File, d.Line, d.Column, d.Code with
      | Some f, Some l, Some c, Some code -> sprintf "%s(%d,%d): %s %s: %s" f l c (severityWord d.Severity) code d.Message
      | Some f, Some l, Some c, None -> sprintf "%s(%d,%d): %s" f l c d.Message
      | _ -> d.Message)
    |> String.concat "\n"

/// Unified error type for the entire SageFs system.
/// Every Result<..., SageFsError> across all layers uses this single DU.
/// NO wildcard matches in module functions — compiler catches missing cases.
[<RequireQualifiedAccess>]
type SageFsError =
  // ── Tool availability ──
  | ToolNotAvailable of toolName: string * currentState: SessionState * availableTools: string list
  // ── Session operations ──
  | SessionNotFound of sessionId: string
  | NoActiveSessions
  | AmbiguousSessions of sessionDescriptions: string list
  | SessionCreationFailed of reason: string
  | DuplicateSession of existingSessionId: string * workingDirectory: string
  /// A session-create request's `workingDirectory` or a `projects` entry
  /// failed path-safety validation — missing/non-existent directory, a UNC
  /// path, or a project path that canonicalizes to somewhere outside
  /// `workingDirectory`. `path` is the exact offending value so the caller
  /// can see precisely what was refused (see McpServer.fs's
  /// `validateSessionCreateRequest`, mirroring the containment discipline
  /// `DashboardTypes.resolveSessionProjects` already applies on the
  /// dashboard's own session-create path — sagefs-roast.md Finding #13).
  | UnsafeSessionPath of path: string * reason: string
  /// A session-create request named a project whose target framework the
  /// FSI host cannot load — the classic case is .NET Framework (net48,
  /// net472, ...). Computed once, at the single owner of session creation
  /// (SessionManager's CreateSession handling), from
  /// `ProjectCompatibility.findUnhostable` — every on-ramp (MCP, HTTP,
  /// dashboard) inherits the refusal for free instead of each one guessing
  /// from a build-detection failure the way RuntimeCompat's "no bin/
  /// directory" message used to be for .NET Framework projects, which was
  /// false: the project builds fine, it just produces output the FSI host
  /// cannot load. `targetFrameworks` keeps every declared TFM (not just the
  /// unsupported one) so multi-targeting isn't hidden from the message.
  | ProjectFrameworkNotHostable of
      project: string *
      targetFrameworks: string list *
      reason: ProjectCompatibility.UnsupportedTfmReason
  | SessionStopFailed of sessionId: string * reason: string
  | SessionSwitchFailed of sessionId: string * reason: string
  /// The target session could not be routed to at all — gone, still warming
  /// up, faulted, or otherwise unroutable. `reason` is the resolution's own
  /// description (SessionResolution/RouteError already computed it; this
  /// case just carries it instead of it being formatted straight to a
  /// display string with no structure for a caller to branch on).
  | SessionNotRoutable of reason: string
  // ── Worker communication ──
  | WorkerCommunicationFailed of sessionId: string * reason: string
  | WorkerSpawnFailed of reason: string
  | WorkerTimeout of sessionId: string * operation: string * timeoutSec: float
  | WorkerHttpError of sessionId: string * endpoint: string * statusCode: int
  | PipeClosed
  // ── Eval/reset/check operations ──
  | EvalFailed of reason: string
  | ResetFailed of reason: string
  | HardResetFailed of reason: string
  /// `dotnet build` failed. Structured diagnostics, not prose with a baked-in
  /// UI hint — a surface words its own call to action from `suggestedAction`
  /// or its own display logic (see AppRun.fs's dashboard-card describe).
  | BuildFailed of exitCode: int * diagnostics: BuildDiagnostic list
  | ScriptLoadFailed of reason: string
  | CheckFailed of reason: string
  | CompletionFailed of sessionId: string * reason: string
  | CancelFailed of reason: string
  /// A reset replaced the session while this evaluation was still running, so
  /// its result belongs to a session that no longer exists and was discarded.
  | EvalSupersededByReset
  // ── Warm-up ──
  | WarmupOpenFailed of name: string * reason: string
  | WarmupContextFailed of sessionId: string * reason: string
  // ── Hot reload ──
  | HotReloadFailed of path: string * reason: string
  | HotReloadStateError of sessionId: string * reason: string
  // ── Running apps ──
  | AppRunFailed of project: string * reason: string
  // ── Restart policy ──
  | RestartLimitExceeded of restartCount: int * windowMinutes: float
  // ── Infrastructure ──
  | DaemonStartFailed of reason: string
  | DaemonNotRunning
  | PortInUse of port: int
  | SseConnectionError of reason: string
  | JsonParseError of context: string * reason: string
  // ── Cohort coordination (multi-agent vision) ──
  /// A cohort command was refused by the pure `Cohort.decide` core — the one
  /// boundary that maps `Cohort.CohortError` into this algebra. `reason` and
  /// `suggestion` are built per `CohortError` case at the MCP boundary
  /// (`Mcp.fs`'s `cohortErrorToSageFsError`), where the caller's `MemberId`
  /// display is available, so the agent gets an accurate what + actionable next
  /// step instead of a mismatched session/worker error's advice.
  | CohortActionFailed of reason: string * suggestion: string
  | Unexpected of exn

module SageFsError =
  let describe = function
    | SageFsError.ToolNotAvailable(toolName, state, available) ->
      sprintf "Cannot %s: session is %s. Available: %s"
        toolName (SessionState.label state) (available |> String.concat ", ")
    | SageFsError.SessionNotFound id ->
      sprintf "Session '%s' not found. Use list_sessions to see available sessions." id
    | SageFsError.NoActiveSessions ->
      "No active sessions. Use create_session to start one."
    | SageFsError.AmbiguousSessions descriptions ->
      sprintf "Multiple sessions active. Specify sessionId:\n%s" (descriptions |> String.concat "\n")
    | SageFsError.SessionCreationFailed reason ->
      sprintf "Failed to create session: %s. Check the project path exists and contains a valid .fsproj." reason
    | SageFsError.DuplicateSession(existingId, dir) ->
      sprintf "A session for this project already exists (session %s, working directory %s). Use switch_session to select it instead of creating a duplicate." existingId dir
    | SageFsError.UnsafeSessionPath(path, reason) ->
      sprintf "Refused session path '%s': %s" path reason
    | SageFsError.ProjectFrameworkNotHostable(project, targetFrameworks, reason) ->
      ProjectCompatibility.describeUnhostable project targetFrameworks reason
    | SageFsError.SessionStopFailed(id, reason) ->
      sprintf "Failed to stop session '%s': %s" id reason
    | SageFsError.SessionSwitchFailed(id, reason) ->
      sprintf "Failed to switch to session '%s': %s. Use list_sessions to check available sessions." id reason
    | SageFsError.SessionNotRoutable reason ->
      sprintf "Session not reachable: %s" reason
    | SageFsError.WorkerCommunicationFailed(id, reason) ->
      sprintf "Cannot reach session '%s': %s. The worker may have crashed — try hard_reset_fsi_session." id reason
    | SageFsError.WorkerSpawnFailed reason ->
      sprintf "Failed to start worker: %s. Ensure the .NET SDK is installed and the project builds with 'dotnet build'." reason
    | SageFsError.WorkerTimeout(id, operation, sec) ->
      sprintf "Session '%s' timed out during %s after %.1fs. Try again or use hard_reset_fsi_session." id operation sec
    | SageFsError.WorkerHttpError(id, endpoint, status) ->
      sprintf "Session '%s' returned HTTP %d for %s" id status endpoint
    | SageFsError.PipeClosed ->
      "Pipe closed unexpectedly. The worker process may have crashed — try hard_reset_fsi_session to recover."
    | SageFsError.EvalFailed reason ->
      sprintf "Evaluation failed: %s" reason
    | SageFsError.ResetFailed reason ->
      sprintf "Reset failed: %s. Try hard_reset_fsi_session for a full restart." reason
    | SageFsError.HardResetFailed reason ->
      sprintf "Hard reset failed: %s. Check that the project builds with 'dotnet build'." reason
    | SageFsError.BuildFailed(exitCode, diagnostics) ->
      sprintf "Build failed (exit %d):\n%s" exitCode (BuildDiagnostic.describe diagnostics)
    | SageFsError.ScriptLoadFailed reason ->
      sprintf "Script load failed: %s. Check that the file exists and has valid F# syntax." reason
    | SageFsError.CheckFailed reason ->
      sprintf "Type check failed: %s" reason
    | SageFsError.CompletionFailed(id, reason) ->
      sprintf "Code completion failed for session '%s': %s" id reason
    | SageFsError.CancelFailed reason ->
      sprintf "Cancel failed: %s" reason
    | SageFsError.EvalSupersededByReset ->
      "The session was reset while this evaluation was running, so its result was discarded: it ran against the session the reset replaced, and none of its definitions exist in the fresh session."
    | SageFsError.WarmupOpenFailed(name, reason) ->
      sprintf "Failed to open '%s' during warm-up: %s" name reason
    | SageFsError.WarmupContextFailed(id, reason) ->
      sprintf "Failed to get warmup context for session '%s': %s" id reason
    | SageFsError.HotReloadFailed(path, reason) ->
      sprintf "Hot reload failed for '%s': %s. Check the file for syntax errors." path reason
    | SageFsError.HotReloadStateError(id, reason) ->
      sprintf "Hot reload state error in session '%s': %s" id reason
    | SageFsError.AppRunFailed("", reason) ->
      sprintf "Could not run the app: %s" reason
    | SageFsError.AppRunFailed(project, reason) ->
      sprintf "Could not run '%s': %s" project reason
    | SageFsError.RestartLimitExceeded(count, windowMin) ->
      sprintf "Worker restarted %d times within %.0f minutes — giving up. Check the log file for crash details and restart SageFs." count windowMin
    | SageFsError.DaemonStartFailed reason ->
      sprintf "Failed to start daemon: %s" reason
    | SageFsError.DaemonNotRunning ->
      "SageFs daemon is not running. Start it with 'sagefs' in your project directory."
    | SageFsError.PortInUse port ->
      sprintf "Port %d is already in use. Another SageFs instance may be running — try 'sagefs status' or use --mcp-port to pick a different port." port
    | SageFsError.SseConnectionError reason ->
      sprintf "SSE connection failed: %s" reason
    | SageFsError.JsonParseError(context, reason) ->
      sprintf "JSON parse error in %s: %s" context reason
    | SageFsError.CohortActionFailed(reason, _) -> reason
    | SageFsError.Unexpected exn ->
      sprintf "Unexpected error: %s" exn.Message

  let toLogLevel = function
    // Critical — system-level failures, daemon can't operate
    | SageFsError.DaemonStartFailed _ -> LogLevel.Critical
    | SageFsError.PortInUse _ -> LogLevel.Critical
    | SageFsError.RestartLimitExceeded _ -> LogLevel.Critical
    // Error — operation failed, user action needed
    | SageFsError.WorkerSpawnFailed _ -> LogLevel.Error
    | SageFsError.WorkerCommunicationFailed _ -> LogLevel.Error
    | SageFsError.WorkerTimeout _ -> LogLevel.Error
    | SageFsError.WorkerHttpError _ -> LogLevel.Error
    | SageFsError.PipeClosed -> LogLevel.Error
    | SageFsError.SessionCreationFailed _ -> LogLevel.Error
    | SageFsError.DuplicateSession _ -> LogLevel.Information
    | SageFsError.UnsafeSessionPath _ -> LogLevel.Warning
    | SageFsError.ProjectFrameworkNotHostable _ -> LogLevel.Information
    | SageFsError.EvalFailed _ -> LogLevel.Error
    | SageFsError.ResetFailed _ -> LogLevel.Error
    | SageFsError.HardResetFailed _ -> LogLevel.Error
    | SageFsError.BuildFailed _ -> LogLevel.Error
    | SageFsError.ScriptLoadFailed _ -> LogLevel.Error
    | SageFsError.HotReloadFailed _ -> LogLevel.Error
    | SageFsError.AppRunFailed _ -> LogLevel.Error
    | SageFsError.SseConnectionError _ -> LogLevel.Error
    | SageFsError.Unexpected _ -> LogLevel.Error
    // Warning — degraded but recoverable
    | SageFsError.SessionStopFailed _ -> LogLevel.Warning
    | SageFsError.SessionSwitchFailed _ -> LogLevel.Warning
    | SageFsError.CheckFailed _ -> LogLevel.Warning
    | SageFsError.CompletionFailed _ -> LogLevel.Warning
    | SageFsError.CancelFailed _ -> LogLevel.Warning
    | SageFsError.EvalSupersededByReset -> LogLevel.Warning
    | SageFsError.WarmupOpenFailed _ -> LogLevel.Warning
    | SageFsError.WarmupContextFailed _ -> LogLevel.Warning
    | SageFsError.HotReloadStateError _ -> LogLevel.Warning
    | SageFsError.JsonParseError _ -> LogLevel.Warning
    // Information — expected conditions, not bugs
    | SageFsError.ToolNotAvailable _ -> LogLevel.Information
    | SageFsError.SessionNotFound _ -> LogLevel.Information
    | SageFsError.SessionNotRoutable _ -> LogLevel.Information
    | SageFsError.NoActiveSessions -> LogLevel.Information
    | SageFsError.AmbiguousSessions _ -> LogLevel.Information
    | SageFsError.DaemonNotRunning -> LogLevel.Information
    | SageFsError.CohortActionFailed _ -> LogLevel.Information

  let toHttpStatus = function
    // 404 Not Found
    | SageFsError.SessionNotFound _ -> 404
    | SageFsError.SessionNotRoutable _ -> 404
    | SageFsError.NoActiveSessions -> 404
    | SageFsError.DaemonNotRunning -> 404
    // 400 Bad Request
    | SageFsError.AmbiguousSessions _ -> 400
    | SageFsError.JsonParseError _ -> 400
    | SageFsError.ToolNotAvailable _ -> 400
    | SageFsError.UnsafeSessionPath _ -> 400
    | SageFsError.ProjectFrameworkNotHostable _ -> 400
    // A cohort command invalid for the current cohort state/authority (not the
    // holder, not the conductor, scope already claimed, stale fence). 400 not
    // 409: 409 is reserved here for infrastructure conflicts (isInfraError).
    | SageFsError.CohortActionFailed _ -> 400
    // 409 Conflict
    | SageFsError.PortInUse _ -> 409
    | SageFsError.RestartLimitExceeded _ -> 409
    | SageFsError.DuplicateSession _ -> 409
    // 504 Gateway Timeout
    | SageFsError.WorkerTimeout _ -> 504
    // 502 Bad Gateway
    | SageFsError.WorkerHttpError _ -> 502
    | SageFsError.WorkerCommunicationFailed _ -> 502
    | SageFsError.PipeClosed -> 502
    | SageFsError.WorkerSpawnFailed _ -> 502
    | SageFsError.SseConnectionError _ -> 502
    // 500 Internal Server Error
    | SageFsError.SessionCreationFailed _ -> 500
    | SageFsError.SessionStopFailed _ -> 500
    | SageFsError.SessionSwitchFailed _ -> 500
    | SageFsError.EvalFailed _ -> 500
    | SageFsError.ResetFailed _ -> 500
    | SageFsError.HardResetFailed _ -> 500
    | SageFsError.BuildFailed _ -> 500
    | SageFsError.ScriptLoadFailed _ -> 500
    | SageFsError.CheckFailed _ -> 500
    | SageFsError.CompletionFailed _ -> 500
    | SageFsError.CancelFailed _ -> 500
    | SageFsError.EvalSupersededByReset -> 500
    | SageFsError.WarmupOpenFailed _ -> 500
    | SageFsError.WarmupContextFailed _ -> 500
    | SageFsError.HotReloadFailed _ -> 500
    | SageFsError.HotReloadStateError _ -> 500
    | SageFsError.AppRunFailed _ -> 500
    | SageFsError.DaemonStartFailed _ -> 500
    | SageFsError.Unexpected _ -> 500

  /// Client errors: 4xx — the request was malformed or referred to missing resources.
  let isClientError = function
    | SageFsError.SessionNotFound _ -> true
    | SageFsError.SessionNotRoutable _ -> true
    | SageFsError.NoActiveSessions -> true
    | SageFsError.DaemonNotRunning -> true
    | SageFsError.AmbiguousSessions _ -> true
    | SageFsError.JsonParseError _ -> true
    | SageFsError.ToolNotAvailable _ -> true
    | SageFsError.UnsafeSessionPath _ -> true
    | SageFsError.ProjectFrameworkNotHostable _ -> true
    | SageFsError.CohortActionFailed _ -> true
    | SageFsError.AppRunFailed _
    | SageFsError.DuplicateSession _
    | SageFsError.SessionCreationFailed _
    | SageFsError.SessionStopFailed _
    | SageFsError.SessionSwitchFailed _
    | SageFsError.WorkerCommunicationFailed _
    | SageFsError.WorkerSpawnFailed _
    | SageFsError.WorkerTimeout _
    | SageFsError.WorkerHttpError _
    | SageFsError.PipeClosed
    | SageFsError.EvalFailed _
    | SageFsError.ResetFailed _
    | SageFsError.HardResetFailed _
    | SageFsError.BuildFailed _
    | SageFsError.ScriptLoadFailed _
    | SageFsError.CheckFailed _
    | SageFsError.CompletionFailed _
    | SageFsError.CancelFailed _
    | SageFsError.EvalSupersededByReset
    | SageFsError.WarmupOpenFailed _
    | SageFsError.WarmupContextFailed _
    | SageFsError.HotReloadFailed _
    | SageFsError.HotReloadStateError _
    | SageFsError.RestartLimitExceeded _
    | SageFsError.DaemonStartFailed _
    | SageFsError.PortInUse _
    | SageFsError.SseConnectionError _
    | SageFsError.Unexpected _ -> false

  /// Server errors: 500 — internal failures not caused by the client.
  let isServerError = function
    | SageFsError.SessionCreationFailed _ -> true
    | SageFsError.SessionStopFailed _ -> true
    | SageFsError.SessionSwitchFailed _ -> true
    | SageFsError.EvalFailed _ -> true
    | SageFsError.ResetFailed _ -> true
    | SageFsError.HardResetFailed _ -> true
    | SageFsError.BuildFailed _ -> true
    | SageFsError.ScriptLoadFailed _ -> true
    | SageFsError.CheckFailed _ -> true
    | SageFsError.CompletionFailed _ -> true
    | SageFsError.CancelFailed _ -> true
    | SageFsError.EvalSupersededByReset -> true
    | SageFsError.WarmupOpenFailed _ -> true
    | SageFsError.WarmupContextFailed _ -> true
    | SageFsError.HotReloadFailed _ -> true
    | SageFsError.HotReloadStateError _ -> true
    | SageFsError.DaemonStartFailed _ -> true
    | SageFsError.AppRunFailed _ -> true
    | SageFsError.Unexpected _ -> true
    | SageFsError.CohortActionFailed _
    | SageFsError.ToolNotAvailable _
    | SageFsError.UnsafeSessionPath _
    | SageFsError.ProjectFrameworkNotHostable _
    | SageFsError.SessionNotFound _
    | SageFsError.SessionNotRoutable _
    | SageFsError.NoActiveSessions
    | SageFsError.AmbiguousSessions _
    | SageFsError.JsonParseError _
    | SageFsError.DaemonNotRunning
    | SageFsError.DuplicateSession _
    | SageFsError.WorkerCommunicationFailed _
    | SageFsError.WorkerSpawnFailed _
    | SageFsError.WorkerTimeout _
    | SageFsError.WorkerHttpError _
    | SageFsError.PipeClosed
    | SageFsError.SseConnectionError _
    | SageFsError.RestartLimitExceeded _
    | SageFsError.PortInUse _ -> false

  /// Gateway errors: 502/504 — the worker (upstream) is unreachable or timed out.
  let isGatewayError = function
    | SageFsError.WorkerCommunicationFailed _ -> true
    | SageFsError.WorkerSpawnFailed _ -> true
    | SageFsError.WorkerTimeout _ -> true
    | SageFsError.WorkerHttpError _ -> true
    | SageFsError.PipeClosed -> true
    | SageFsError.SseConnectionError _ -> true
    | SageFsError.CohortActionFailed _
    | SageFsError.AppRunFailed _
    | SageFsError.ToolNotAvailable _
    | SageFsError.UnsafeSessionPath _
    | SageFsError.ProjectFrameworkNotHostable _
    | SageFsError.SessionNotFound _
    | SageFsError.SessionNotRoutable _
    | SageFsError.NoActiveSessions
    | SageFsError.AmbiguousSessions _
    | SageFsError.JsonParseError _
    | SageFsError.DaemonNotRunning
    | SageFsError.DuplicateSession _
    | SageFsError.SessionCreationFailed _
    | SageFsError.SessionStopFailed _
    | SageFsError.SessionSwitchFailed _
    | SageFsError.EvalFailed _
    | SageFsError.ResetFailed _
    | SageFsError.HardResetFailed _
    | SageFsError.BuildFailed _
    | SageFsError.ScriptLoadFailed _
    | SageFsError.CheckFailed _
    | SageFsError.CompletionFailed _
    | SageFsError.CancelFailed _
    | SageFsError.EvalSupersededByReset
    | SageFsError.WarmupOpenFailed _
    | SageFsError.WarmupContextFailed _
    | SageFsError.HotReloadFailed _
    | SageFsError.HotReloadStateError _
    | SageFsError.RestartLimitExceeded _
    | SageFsError.DaemonStartFailed _
    | SageFsError.PortInUse _
    | SageFsError.Unexpected _ -> false

  /// Infrastructure errors: 409 — system-level conflicts (port in use, restart limit, duplicate session).
  let isInfraError = function
    | SageFsError.PortInUse _ -> true
    | SageFsError.RestartLimitExceeded _ -> true
    | SageFsError.DuplicateSession _ -> true
    | SageFsError.CohortActionFailed _
    | SageFsError.AppRunFailed _
    | SageFsError.ToolNotAvailable _
    | SageFsError.UnsafeSessionPath _
    | SageFsError.ProjectFrameworkNotHostable _
    | SageFsError.SessionNotFound _
    | SageFsError.SessionNotRoutable _
    | SageFsError.NoActiveSessions
    | SageFsError.AmbiguousSessions _
    | SageFsError.JsonParseError _
    | SageFsError.DaemonNotRunning
    | SageFsError.SessionCreationFailed _
    | SageFsError.SessionStopFailed _
    | SageFsError.SessionSwitchFailed _
    | SageFsError.WorkerCommunicationFailed _
    | SageFsError.WorkerSpawnFailed _
    | SageFsError.WorkerTimeout _
    | SageFsError.WorkerHttpError _
    | SageFsError.PipeClosed
    | SageFsError.EvalFailed _
    | SageFsError.ResetFailed _
    | SageFsError.HardResetFailed _
    | SageFsError.BuildFailed _
    | SageFsError.ScriptLoadFailed _
    | SageFsError.CheckFailed _
    | SageFsError.CompletionFailed _
    | SageFsError.CancelFailed _
    | SageFsError.EvalSupersededByReset
    | SageFsError.WarmupOpenFailed _
    | SageFsError.WarmupContextFailed _
    | SageFsError.HotReloadFailed _
    | SageFsError.HotReloadStateError _
    | SageFsError.DaemonStartFailed _
    | SageFsError.SseConnectionError _
    | SageFsError.Unexpected _ -> false

  /// Actionable suggestion for each error case.
  let suggestedAction = function
    | SageFsError.ToolNotAvailable _ -> "Wait for session to reach Ready state"
    | SageFsError.SessionNotFound _ -> "Run list_sessions to see available sessions"
    | SageFsError.NoActiveSessions -> "Run create_session to start one"
    | SageFsError.AmbiguousSessions _ -> "Specify a sessionId explicitly"
    | SageFsError.SessionCreationFailed _ -> "Check the project path and run 'dotnet build'"
    | SageFsError.DuplicateSession _ -> "Run switch_session to select the existing session"
    | SageFsError.UnsafeSessionPath _ -> "Use an existing directory and keep project paths inside it — no UNC paths or '..' escapes"
    | SageFsError.ProjectFrameworkNotHostable _ -> "Point SageFs at a .NET (Core) project (net5.0 or newer) for now — .NET Framework support is not shipped yet, and the message names the issue tracking it"
    | SageFsError.SessionStopFailed _ -> "Try hard_reset_fsi_session"
    | SageFsError.SessionSwitchFailed _ -> "Run list_sessions to check available sessions"
    | SageFsError.SessionNotRoutable _ -> "Run get_fsi_status or list_sessions to check session state"
    | SageFsError.WorkerCommunicationFailed _ -> "Run hard_reset_fsi_session"
    | SageFsError.WorkerSpawnFailed _ -> "Check .NET SDK installation with 'dotnet --info'"
    | SageFsError.WorkerTimeout _ -> "Retry or run hard_reset_fsi_session"
    | SageFsError.WorkerHttpError _ -> "Run hard_reset_fsi_session"
    | SageFsError.PipeClosed -> "Run hard_reset_fsi_session"
    | SageFsError.EvalFailed _ -> "Fix the code and resubmit"
    | SageFsError.ResetFailed _ -> "Run hard_reset_fsi_session"
    | SageFsError.HardResetFailed _ -> "Check that the project builds with 'dotnet build'"
    | SageFsError.BuildFailed _ -> "Fix the build errors, then run the app again"
    | SageFsError.ScriptLoadFailed _ -> "Check file exists and has valid F# syntax"
    | SageFsError.CheckFailed _ -> "Fix the code and resubmit"
    | SageFsError.CompletionFailed _ -> "Retry or run reset_fsi_session"
    | SageFsError.CancelFailed _ -> "Retry or run hard_reset_fsi_session"
    | SageFsError.EvalSupersededByReset -> "Run the code again in the fresh session"
    | SageFsError.WarmupOpenFailed _ -> "Run 'dotnet build' for the project, then hard_reset_fsi_session. If it still doesn't resolve, it is not a public, top-level, fully-qualified name in any loaded assembly — SageFs cannot auto-open a nested or private module by its short name."
    | SageFsError.WarmupContextFailed _ -> "Run hard_reset_fsi_session"
    | SageFsError.HotReloadFailed _ -> "Check the file for syntax errors"
    | SageFsError.HotReloadStateError _ -> "Run hard_reset_fsi_session"
    | SageFsError.AppRunFailed _ -> "Run list_runnable_projects to see which projects can run"
    | SageFsError.RestartLimitExceeded _ -> "Check the log file and restart SageFs"
    | SageFsError.DaemonStartFailed _ -> "Check port availability and .NET SDK"
    | SageFsError.DaemonNotRunning -> "Start SageFs with 'sagefs'"
    | SageFsError.PortInUse _ -> "Stop the other process or use --mcp-port"
    | SageFsError.SseConnectionError _ -> "Check daemon is running and retry"
    | SageFsError.JsonParseError _ -> "Check request payload format"
    | SageFsError.CohortActionFailed(_, suggestion) -> suggestion
    | SageFsError.Unexpected _ -> "Check the SageFs log for details"

  /// Agent-facing error description: compose describe + suggestedAction.
  /// Use at MCP boundary so every error an agent sees ends with an actionable next step.
  let describeForAgent (err: SageFsError) =
    sprintf "%s → Next: %s" (describe err) (suggestedAction err)

  /// True for a genuine F# union type that is NOT a list. F# lists
  /// (`'a list`) are themselves unions (Cons/Nil) at the CLR level but
  /// serialize fine as themselves; a "real" union like `SessionState` or
  /// `ProjectCompatibility.UnsupportedTfmReason` does not, without the
  /// `JsonFSharpConverter` the HTTP boundary's plain `JsonSerializer`
  /// doesn't register.
  let private isNonListUnion (t: System.Type) =
    FSharpType.IsUnion t
    && not (t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<int list>)

  /// Serialize a SageFsError to a JSON-friendly anonymous record.
  /// Returns { case, fields, message, suggestedAction }.
  let toJson (err: SageFsError) =
    let info, values = FSharpValue.GetUnionFields(err, typeof<SageFsError>)
    let fieldInfos = info.GetFields()
    let fieldMap = System.Collections.Generic.Dictionary<string, obj>()
    Array.zip fieldInfos values
    |> Array.iter (fun (fi, v) ->
      match v with
      | :? exn as ex -> fieldMap.[fi.Name] <- box ex.Message
      // A bare F# DU boxed as `obj` (e.g. ToolNotAvailable's SessionState,
      // or ProjectFrameworkNotHostable's UnsupportedTfmReason) is NOT
      // serializable by the plain `JsonSerializer.Serialize` the HTTP
      // boundary uses (McpServer.fs's jsonResponse has no
      // JsonFSharpConverter registered) — confirmed live via a real
      // `/api/sessions/create` call, which returned an "F# discriminated
      // union serialization is not supported" 500 instead of the intended
      // refusal. Reduce it to its case name, the same shape `case` above
      // already uses to expose a DU's identity over JSON. `string list`/
      // `BuildDiagnostic list` fields are also technically unions (F# lists
      // are Cons/Nil) but must NOT hit this branch — they serialize fine as
      // themselves — so `isNonListUnion` scopes this to non-list unions.
      | _ when isNonListUnion fi.PropertyType ->
        let caseInfo, _ = FSharpValue.GetUnionFields(v, fi.PropertyType)
        fieldMap.[fi.Name] <- box caseInfo.Name
      | _ -> fieldMap.[fi.Name] <- v)
    {| case = info.Name
       fields = fieldMap :> System.Collections.Generic.IDictionary<string, obj>
       message = describe err
       suggestedAction = suggestedAction err |}

/// Carries a SageFsError through an exception-typed error channel (the eval
/// actor's `EvalResponse.EvaluationResult`) so the worker boundary recovers
/// the structured case instead of flattening it to `ex.ToString()`.
type SageFsErrorException(error: SageFsError) =
  inherit System.Exception(SageFsError.describe error)
  member _.Error = error
