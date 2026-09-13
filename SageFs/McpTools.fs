module SageFs.Server.McpTools

open System.ComponentModel
open System.Runtime.InteropServices
open System.Threading.Tasks
open ModelContextProtocol.Server
open Microsoft.Extensions.Logging
open SageFs.AppState
open SageFs.McpTools
open SageFs.Utils
open System.Text.Json
open System.Text.Json.Nodes

/// Emoji per tool category — printed once as a header, not per-line
/// Echo MCP tool results to the SageFs console for visibility

/// Global audit tracker for MCP tool usage analysis (synthesis 3.3).
let auditTracker = SageFs.McpToolAudit.AuditTracker()

let ok = function
  | Ok value -> value
  | Error err -> failwith err

/// Exhaustive, compiler-checked mapping from the `SageFsError` algebra to a
/// friction `BlockerKind`. This REPLACES `classifyFrictionOutcome`'s
/// substring matching (which scanned a tool's rendered "Blocked:"/"Error:"
/// text for keywords like "warm", "Multiple sessions match", "stale" — with
/// a real precedence bug: `Contains("affordance") || (Contains("not
/// available") && Contains("tool"))` let "not available" alone win the
/// `&&` before the `tool` check would have narrowed it). A tool's own typed
/// result (a `SageFsError` case, or `None` for success) is classified
/// directly here — no text is parsed, and the compiler refuses this module
/// to build if a new `SageFsError` case is added without a matching arm.
let blockerKindOf : SageFs.SageFsError -> SageFs.Features.FrictionTelemetryTypes.BlockerKind = function
  | SageFs.SageFsError.ToolNotAvailable _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.AffordanceMismatch
  | SageFs.SageFsError.SessionNotFound _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.SessionMissing
  | SageFs.SageFsError.NoActiveSessions -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.SessionMissing
  | SageFs.SageFsError.AmbiguousSessions _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.SessionAmbiguous
  | SageFs.SageFsError.SessionCreationFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.DuplicateSession _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.InvalidRequest
  | SageFs.SageFsError.SessionStopFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.SessionSwitchFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  // SessionNotRoutable is the generic "not ready right now" case used while
  // a session is starting/restarting/mid-transition — SessionWarming is its
  // most common and most actionable meaning (see sessionRoutingError, which
  // constructs it specifically for the WarmingUp/Unroutable resolutions).
  | SageFs.SageFsError.SessionNotRoutable _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.SessionWarming
  | SageFs.SageFsError.WorkerCommunicationFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.TransportFailure
  | SageFs.SageFsError.WorkerSpawnFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.WorkerTimeout _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.TransportFailure
  | SageFs.SageFsError.WorkerHttpError _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.TransportFailure
  | SageFs.SageFsError.PipeClosed -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.TransportFailure
  | SageFs.SageFsError.EvalFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.ResetFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.HardResetFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.BuildFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.ScriptLoadFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.CheckFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.CompletionFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.CancelFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.EvalSupersededByReset -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.WarmupOpenFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.SessionWarming
  | SageFs.SageFsError.WarmupContextFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.SessionWarming
  | SageFs.SageFsError.HotReloadFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  // A confirmed-stale loaded definition (targeted_verify) is reported as
  // HotReloadStateError — see targetedVerifyResult in Mcp.fs.
  | SageFs.SageFsError.HotReloadStateError _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.LoadedStateStale
  | SageFs.SageFsError.AppRunFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.RestartLimitExceeded _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.DaemonStartFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.DaemonNotRunning -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.PortInUse _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed
  | SageFs.SageFsError.SseConnectionError _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.TransportFailure
  | SageFs.SageFsError.JsonParseError _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.InvalidRequest
  // A cohort command the caller wasn't permitted to run given the current
  // cohort state/authority (not the claim holder, not the conductor, scope
  // already claimed, stale fence): the request was invalid for the state, not
  // an internal failure.
  | SageFs.SageFsError.CohortActionFailed _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.InvalidRequest
  // An exception the algebra had no specific case for. OperationFailed (not
  // the old fallback's InvalidRequest — the caller's request was not
  // necessarily invalid) is the honest "something broke" bucket.
  | SageFs.SageFsError.Unexpected _ -> SageFs.Features.FrictionTelemetryTypes.BlockerKind.OperationFailed

/// Build the friction outcome directly from a tool's own typed result —
/// `Ok` is a clean completion, `Error err` is classified via `blockerKindOf`.
/// Never re-parses display text.
let frictionOutcomeOf (outcome: Result<string, SageFs.SageFsError>) : SageFs.Features.FrictionTelemetryTypes.FrictionOutcome =
  match outcome with
  | Ok _ -> SageFs.Features.FrictionTelemetryTypes.FrictionOutcome.CompletedCleanly
  | Error err -> SageFs.Features.FrictionTelemetryTypes.FrictionOutcome.EncounteredBlocker (blockerKindOf err)

let recordToolResult (ctx: McpContext) (toolName: string) (outcome: Result<string, SageFs.SageFsError>) (elapsedMs: int) =
  let event : SageFs.Features.FrictionTelemetryTypes.FrictionEvent =
    { SageFs.Features.FrictionTelemetryTypes.FrictionEvent.OccurredAtUtc = System.DateTimeOffset.UtcNow
      Session = SageFs.Features.FrictionTelemetryTypes.SessionRef.create "mcp" |> ok
      Tool = SageFs.Features.FrictionTelemetryTypes.ToolName.create toolName |> ok
      Intent = SageFs.Features.FrictionTelemetryTypes.IntentKind.ExploreCode
      Outcome = frictionOutcomeOf outcome
      Duration = SageFs.Features.FrictionTelemetryTypes.DurationMs.create elapsedMs |> ok
      FollowUp = SageFs.Features.FrictionTelemetryTypes.FollowUp.NoFollowUpYet
      ContextCost = SageFs.Features.FrictionTelemetryTypes.ContextCost.Focused
      SageFsVersion = SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current () }
  match ctx.FrictionStore with
  | Some store ->
    task {
      let! _ = SageFs.Features.McpFrictionRecorder.Recorder.appendEventDirect store event
      return ()
    }
  | None ->
    task { return () }  // FrictionStore is required — no fallback

let feedbackKindText = function
  | SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.ToolOutputWasTooLarge -> "ToolOutputWasTooLarge"
  | SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.ToolIntentWasUnclear -> "ToolIntentWasUnclear"
  | SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.ToolNameWasMisleading -> "ToolNameWasMisleading"
  | SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.NeededAnotherToolToFinish -> "NeededAnotherToolToFinish"
  | SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.ResultDidNotEstablishTrust -> "ResultDidNotEstablishTrust"

let frictionReportJson (report: SageFs.Features.FrictionTelemetry.FrictionReport) =
  let blockerText = function | Some blocker -> Some (string blocker) | None -> None
  let toolText = function | Some tool -> Some (SageFs.Features.FrictionTelemetryTypes.ToolName.value tool) | None -> None

  let recentFeedbackByTool =
    report.RecentFeedback
    |> List.map (fun item -> SageFs.Features.FrictionTelemetryTypes.ToolName.value item.Tool, item)
    |> Map.ofList

  let topTools = JsonArray()
  report.HighestPriorityTools
  |> List.filter (fun item -> SageFs.Features.FrictionTelemetry.Summaries.isActionableTool item.Tool)
  |> List.iter (fun item ->
    let toolName = SageFs.Features.FrictionTelemetryTypes.ToolName.value item.Tool
    let fallbackAlternative =
      recentFeedbackByTool
      |> Map.tryFind toolName
      |> Option.bind (fun item -> item.LatestAlternative)
    let mostCommonAlternative =
      match toolText item.MostCommonAlternative, fallbackAlternative with
      | Some tool, _ -> Some tool
      | None, alt -> alt
    let suggestedFixTarget =
      match mostCommonAlternative, item.SuggestedFixTarget.StartsWith("Clarify", System.StringComparison.OrdinalIgnoreCase) with
      | Some tool, true -> sprintf "Agents keep resolving this via %s; merge or cross-link that path directly." tool
      | _ -> item.SuggestedFixTarget
    let node = JsonObject()
    node["Tool"] <- JsonValue.Create(toolName)
    node["TotalInvocations"] <- JsonValue.Create(item.TotalInvocations)
    node["BlockedCount"] <- JsonValue.Create(item.BlockedCount)
    node["AbandonedCount"] <- JsonValue.Create(item.AbandonedCount)
    node["ExplicitFeedbackCount"] <- JsonValue.Create(item.ExplicitFeedbackCount)
    node["MostCommonBlocker"] <- JsonValue.Create(blockerText item.MostCommonBlocker)
    node["MostCommonFollowUp"] <- JsonValue.Create(toolText item.MostCommonFollowUp)
    node["MostCommonAlternative"] <- JsonValue.Create(mostCommonAlternative)
    node["SuggestedFixTarget"] <- JsonValue.Create(suggestedFixTarget)
    topTools.Add(node))

  let topBlockers = JsonArray()
  report.TopBlockers
  |> List.iter (fun item ->
    let tools = JsonArray()
    item.MostAffectedTools
    |> List.iter (fun tool -> tools.Add(JsonValue.Create(SageFs.Features.FrictionTelemetryTypes.ToolName.value tool)))
    let node = JsonObject()
    node["Blocker"] <- JsonValue.Create(string item.Blocker)
    node["Count"] <- JsonValue.Create(item.Count)
    node["MostAffectedTools"] <- tools
    topBlockers.Add(node))

  let transitions = JsonArray()
  report.FrequentTransitions
  |> List.iter (fun item ->
    let node = JsonObject()
    node["FromTool"] <- JsonValue.Create(SageFs.Features.FrictionTelemetryTypes.ToolName.value item.FromTool)
    node["ToTool"] <- JsonValue.Create(SageFs.Features.FrictionTelemetryTypes.ToolName.value item.ToTool)
    node["Frequency"] <- JsonValue.Create(item.Frequency)
    transitions.Add(node))

  let recentFeedback = JsonArray()
  report.RecentFeedback
  |> List.iter (fun item ->
    let node = JsonObject()
    node["Tool"] <- JsonValue.Create(SageFs.Features.FrictionTelemetryTypes.ToolName.value item.Tool)
    node["Kind"] <- JsonValue.Create(feedbackKindText item.Kind)
    node["Count"] <- JsonValue.Create(item.Count)
    node["LatestReason"] <- JsonValue.Create(item.LatestReason)
    node["LatestAlternative"] <- JsonValue.Create(item.LatestAlternative)
    recentFeedback.Add(node))

  let payload = JsonObject()
  let recommendedWorkItems = JsonArray()
  report.RecommendedWorkItems
  |> List.iter (fun item ->
    let node = JsonObject()
    node["Title"] <- JsonValue.Create(item.Title)
    node["TargetTool"] <- JsonValue.Create(item.TargetTool |> Option.map SageFs.Features.FrictionTelemetryTypes.ToolName.value)
    node["LikelyFixType"] <- JsonValue.Create(item.LikelyFixType)
    node["Reason"] <- JsonValue.Create(item.Reason)
    node["SuggestedAction"] <- JsonValue.Create(item.SuggestedAction)
    recommendedWorkItems.Add(node))

  payload["TotalEvents"] <- JsonValue.Create(report.TotalEvents)
  payload["TotalFeedbackItems"] <- JsonValue.Create(report.TotalFeedbackItems)
  payload["SageFsVersion"] <- JsonValue.Create(SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current ())
  payload["HighestPriorityTools"] <- topTools
  payload["TopBlockers"] <- topBlockers
  payload["FrequentTransitions"] <- transitions
  payload["RecentFeedback"] <- recentFeedback
  payload["RecommendedWorkItems"] <- recommendedWorkItems
  payload.ToJsonString()

let withEcho (ctx: McpContext) (toolName: string) (t: Task<string>) : Task<string> =
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
      // The tool body only handed back display text here — no structured
      // outcome — so a body that reached this line without throwing is
      // recorded as a clean completion. Tools that can fail without
      // throwing (session routing, reset/cancel/verify, ...) go through
      // withEchoOutcome instead, which carries the real SageFsError.
      let! _ = recordToolResult ctx toolName (Ok result) (int sw.Elapsed.TotalMilliseconds)
      SageFs.Instrumentation.succeedSpan span
      return result
    with ex ->
      sw.Stop()
      SageFs.Instrumentation.mcpToolFailures.Add(1L, System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName))
      auditTracker.Record(toolName, sw.Elapsed.TotalMilliseconds, SageFs.McpToolAudit.Failure)
      SageFs.Instrumentation.failSpan span ex.Message
      // The exception itself IS the SageFsError case — record it directly
      // instead of formatting it to text and having classifyFrictionOutcome
      // re-parse the formatted string back out.
      let! _ = recordToolResult ctx toolName (Error (SageFs.SageFsError.Unexpected ex)) (int sw.Elapsed.TotalMilliseconds)
      return raise ex
  }

let withEchoNoAwaitRecord (ctx: McpContext) (toolName: string) (t: Task<string>) : Task<string> =
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
      let! _ = recordToolResult ctx toolName (Ok result) (int sw.Elapsed.TotalMilliseconds)
      return result
    with ex ->
      sw.Stop()
      SageFs.Instrumentation.mcpToolFailures.Add(1L, System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName))
      auditTracker.Record(toolName, sw.Elapsed.TotalMilliseconds, SageFs.McpToolAudit.Failure)
      SageFs.Instrumentation.failSpan span ex.Message
      // See withEcho above — the exception IS the typed case.
      let! _ = recordToolResult ctx toolName (Error (SageFs.SageFsError.Unexpected ex)) (int sw.Elapsed.TotalMilliseconds)
      return raise ex
  }

/// Like `withEcho`, but for a tool body that ALSO reports a structured
/// `SageFsError` when it did not complete cleanly — independent of the
/// display text, which is returned to the agent EXACTLY as the tool body
/// produced it (the "genuinely still surfaces a human string" case: the
/// presentation is unchanged, only the internal friction classification
/// stops re-parsing that string). `None` means the tool completed cleanly.
let withEchoOutcome (ctx: McpContext) (toolName: string) (t: Task<string * SageFs.SageFsError option>) : Task<string> =
  task {
    SageFs.Instrumentation.mcpToolInvocations.Add(1L)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let span = SageFs.Instrumentation.startSpanWithKind SageFs.Instrumentation.mcpSource "mcp.tool.invoke" System.Diagnostics.ActivityKind.Server
                 ["mcp.tool.name", box toolName; "rpc.system", box "mcp"; "rpc.service", box "sagefs"; "rpc.method", box toolName]
    try
      let! result, blocker = t
      sw.Stop()
      SageFs.Instrumentation.mcpToolSuccesses.Add(1L, System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName))
      auditTracker.Record(toolName, sw.Elapsed.TotalMilliseconds, SageFs.McpToolAudit.Success)
      let normalized = result.Replace("\r\n", "\n").Replace("\n", "\r\n")
      Log.info ">> %s" toolName
      Log.debug "%s" normalized
      let outcome = match blocker with Some err -> Error err | None -> Ok result
      let! _ = recordToolResult ctx toolName outcome (int sw.Elapsed.TotalMilliseconds)
      SageFs.Instrumentation.succeedSpan span
      return result
    with ex ->
      sw.Stop()
      SageFs.Instrumentation.mcpToolFailures.Add(1L, System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName))
      auditTracker.Record(toolName, sw.Elapsed.TotalMilliseconds, SageFs.McpToolAudit.Failure)
      SageFs.Instrumentation.failSpan span ex.Message
      let! _ = recordToolResult ctx toolName (Error (SageFs.SageFsError.Unexpected ex)) (int sw.Elapsed.TotalMilliseconds)
      return raise ex
  }

/// `withEchoOutcome` sibling matching `withEchoNoAwaitRecord`'s statement
/// order (succeedSpan before the friction record await) for the one caller
/// (hard_reset_fsi_session, rebuild=true) that used that ordering.
let withEchoOutcomeNoAwaitRecord (ctx: McpContext) (toolName: string) (t: Task<string * SageFs.SageFsError option>) : Task<string> =
  task {
    SageFs.Instrumentation.mcpToolInvocations.Add(1L)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let span = SageFs.Instrumentation.startSpanWithKind SageFs.Instrumentation.mcpSource "mcp.tool.invoke" System.Diagnostics.ActivityKind.Server
                 ["mcp.tool.name", box toolName; "rpc.system", box "mcp"; "rpc.service", box "sagefs"; "rpc.method", box toolName]
    try
      let! result, blocker = t
      sw.Stop()
      SageFs.Instrumentation.mcpToolSuccesses.Add(1L, System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName))
      auditTracker.Record(toolName, sw.Elapsed.TotalMilliseconds, SageFs.McpToolAudit.Success)
      let normalized = result.Replace("\r\n", "\n").Replace("\n", "\r\n")
      Log.info ">> %s" toolName
      Log.debug "%s" normalized
      SageFs.Instrumentation.succeedSpan span
      let outcome = match blocker with Some err -> Error err | None -> Ok result
      let! _ = recordToolResult ctx toolName outcome (int sw.Elapsed.TotalMilliseconds)
      return result
    with ex ->
      sw.Stop()
      SageFs.Instrumentation.mcpToolFailures.Add(1L, System.Collections.Generic.KeyValuePair("mcp.tool.name", box toolName))
      auditTracker.Record(toolName, sw.Elapsed.TotalMilliseconds, SageFs.McpToolAudit.Failure)
      SageFs.Instrumentation.failSpan span ex.Message
      let! _ = recordToolResult ctx toolName (Error (SageFs.SageFsError.Unexpected ex)) (int sw.Elapsed.TotalMilliseconds)
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
- NEVER use '#r' for assemblies loaded via '--proj'. Call get_startup_info to see which assembly names are already loaded. Using '#r' on a loaded assembly creates a duplicate .NET load context causing TypeLoadException on ALL subsequent evals — this is not a session bug, it is your '#r' directive that must be removed.
- RUNTIME FileNotFoundException for Microsoft.AspNetCore.* or other shared-framework assemblies means FSI cannot execute them (installed ref packs are metadata-only; framework version mismatches surface as manifest errors). This is an environment limit, not a code bug — run that scenario externally instead (dotnet test / dotnet run) and bring results back into the REPL.

RETURN VALUE:
- On success: the printed output of the evaluated code (stdout, printfn output, or the auto-printed value).
- On failure: the F# compiler diagnostic message with file/line info pointing to the error.
- Use get_recent_fsi_events afterward to see the full event log if the return value is ambiguous.

WORKFLOW: Use this tool instead of dotnet build or dotnet run. SageFs IS your compiler and runtime.""")>]
    member _.send_fsharp_code(
        [<Description("Your agent or model name (e.g. 'claude', 'copilot', 'cursor'). Shown in event logs and get_recent_fsi_events output so you can trace which agent submitted which code. Use a short, stable identifier.")>]
        agentName: string,
        code: string,
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string,
        [<Description("Absolute path to the source file this code came from. Enables module context detection — SageFs wraps the code in the correct module/namespace for FSI evaluation.")>]
        [<Optional; DefaultParameterValue("")>]
        file_path: string,
        [<Description("How the code is being evaluated: 'file' for whole-file send, 'block' for a selected region. When omitted, auto-detected from code content.")>]
        [<Optional; DefaultParameterValue("")>]
        eval_mode: string,
        [<Description("1-based line number where the selected block starts in the source file. Helps resolve which module the block belongs to in multi-module files. Omit or pass 0 when unknown.")>]
        [<Optional; DefaultParameterValue(0)>]
        block_start_line: int,
        [<Description("Optional description of what this code is for (e.g. 'refactoring warmup pipeline', 'writing property tests'). Shown in the dashboard so humans and other agents can see what you're working on. Preserved across calls until overwritten by a new non-empty value.")>]
        [<Optional; DefaultParameterValue("")>]
        intent: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        let fp = match System.String.IsNullOrWhiteSpace file_path with | true -> None | false -> Some file_path
        let em = match System.String.IsNullOrWhiteSpace eval_mode with | true -> None | false -> Some eval_mode
        let bsl = match block_start_line with | 0 -> None | n -> Some n
        let intentOpt = match System.String.IsNullOrWhiteSpace intent with | true -> None | false -> Some intent
        logger.LogDebug("MCP-TOOL: send_fsharp_code called by {AgentName}: {Code}", agentName, code)
        SageFs.Instrumentation.mcpToolInvocations.Add(1L)
        sendFSharpCode ctx agentName code OutputFormat.Text None wd fp em bsl intentOpt
    
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
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: load_fsharp_script called: {FilePath}", filePath)
        task {
          let! result = loadFSharpScriptResult ctx agentName filePath None wd
          return
            match result with
            | Ok text -> text, None
            | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
        }
        |> withEchoOutcome ctx "load_fsharp_script"
    
    [<McpServerTool>]
    [<Description("""Get recent FSI events including evaluations, errors, and script loads. Returns the most recent N events (default 10) with timestamps and sources.

WHEN TO USE:
- After an unexpected error to understand what just happened and in what order.
- To audit which code was evaluated and by which agent (MCP, editor plugin, etc.).
- As a lightweight alternative to get_fsi_status when you only want the recent activity log.

OUTPUT FORMAT: Each event shows timestamp, event type (Eval, Error, Load, Reset), source agent name, and a brief description. Events are newest-last.""")>]
    member _.get_recent_fsi_events(
        [<Description("Number of recent events to return (default 10)")>]
        [<Optional; DefaultParameterValue(10)>]
        count: int,
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> = 
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        let eventCount = count
        logger.LogDebug("MCP-TOOL: get_recent_fsi_events called: count={Count}", eventCount)
        getRecentEvents ctx "mcp" eventCount wd |> withEcho ctx "get_recent_fsi_events"
    
    [<McpServerTool>]
    [<Description("""Get the current FSI session status: live worker readiness, loaded projects, session statistics, and active affordances. Use this to verify whether you can route new work to a session right now.

WHAT THIS TOOL REPRESENTS:
- This reports the FSI worker session state, NOT the live-testing subsystem state.
- Session registry states like Starting / Restarting collapse to 'WarmingUp' here.
- Worker status 'Building (...)' collapses to 'Evaluating' here because the worker is alive but busy.

WHEN TO USE:
- First thing to call when setting up a new session — confirms what projects are loaded and what capabilities are active.
- After hard_reset_fsi_session with rebuild=true, re-check this until it reports State='Ready'. During the restart window it may instead return an error saying the session is still warming up — that is normal.
- When you get unexpected 'type not defined' errors — check that the expected project is loaded and the session is warmed up.
- To discover the active session ID needed for routing commands when multiple sessions exist.

KEY SIGNALS IN OUTPUT:
- State: WarmingUp | Ready | Evaluating | Faulted.
  - WarmingUp = session exists but the worker proxy is not routable yet.
  - Ready = safe to submit code.
  - Evaluating = worker is alive but busy (this also covers worker-side 'Building (...)' states).
  - Faulted = investigate warmup/runtime errors before proceeding.
- Projects: the loaded .fsproj files for this session.
- Available: which MCP tools/affordances are currently active for this session.
- Session: the stable session ID shown at the start of the response.

IMPORTANT:
- This is the MCP-facing worker/session readiness tool.
- Use this to decide whether explicit MCP actions like send_fsharp_code or targeted_verify are safe to route right now.""")>]
    member _.get_fsi_status(
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: get_fsi_status called: workingDir={Dir}", working_directory)
        // get_fsi_status deliberately reports a missing/warming/faulted
        // session as informational JSON, not an error (see its own "This
        // prevents SessionMissing friction on the most-called tool" comment)
        // — so it keeps plain withEcho rather than classifying that as a
        // blocker, which would undo that deliberate choice.
        getStatus ctx "mcp" None wd |> withEcho ctx "get_fsi_status"

    [<Description("""Get detailed startup information: loaded projects, enabled features, and command-line arguments. Use to understand what capabilities are available in the current session.

DIFFERENCE FROM get_fsi_status:
- get_fsi_status gives you the LIVE runtime state (current status, session health, active affordances).
- get_startup_info gives you STATIC configuration — how SageFs was launched (CLI flags, ports, TUI/GUI mode, etc.).

WHEN TO USE:
- To find out which projects were specified on the CLI vs loaded dynamically.
- To understand which features were enabled at startup (e.g., live testing, TUI, GUI).
- When investigating environment differences ("was live testing enabled when this was launched?").""")>]
    member _.get_startup_info(
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: get_startup_info called")
        getStartupInfo ctx "mcp" wd |> withEcho ctx "get_startup_info"

    [<McpServerTool>]
    [<Description("""Discover F# projects (.fsproj) and solutions (.sln/.slnx) in the current working directory. Useful for determining what projects can be loaded into a new SageFs session.

WHEN TO USE:
- When you want to know which projects exist in this repo before creating a session for one of them.
- To find a test project to pass to create_session so you can run tests in an isolated session.
- As a discovery step when the user opens a new workspace and you need to understand the project structure.

NOTE: This does NOT load any projects — it only lists what is available on disk. Use create_session or hard_reset_fsi_session with rebuild=true to actually load a project.""")>]
    member _.get_available_projects(
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: get_available_projects called")
        getAvailableProjects ctx "mcp" wd |> withEcho ctx "get_available_projects"

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
- Re-check get_fsi_status until it reports State='Ready' before sending new code. A temporary warming-up message before that is normal.

WHEN TO USE (rare):
- The session warm-up itself failed and you see cascade errors on EVERY submission, even trivial ones like '1+1;;'.
- You intentionally want to clear all your interactive definitions and start fresh.

WHEN NOT TO USE (common mistake):
- You got an eval error — that means YOUR code has a bug. Fix your code and resubmit instead.
- 'Operation could not be completed due to earlier error' — this is NOT session corruption. A previous submission failed. Fix and resubmit that code.
- You're not sure what went wrong — read the error diagnostics first, they tell you exactly what's wrong.

This is a SOFT reset — DLL locks are retained. Use hard_reset_fsi_session only if modules failed to load during warm-up.""")>]
    member _.reset_fsi_session(
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: reset_fsi_session called")
        task {
          let! result = resetSessionResult ctx "mcp" None wd
          return
            match result with
            | Ok text -> text, None
            | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
        }
        |> withEchoOutcome ctx "reset_fsi_session"

    [<McpServerTool>]
    [<Description("""Hard reset: dispose the FSI session, release DLL locks via shadow-copy refresh,
optionally rebuild the project, and create a fresh session. ALL definitions are lost.

⚠️ THIS IS ALMOST NEVER WHAT YOU WANT. Before calling this, ask yourself:
- "Did I get an eval error?" → That's YOUR code's bug. Fix your code. Do NOT hard reset.
- "Did I get 'earlier error'?" → A previous submission failed. Fix and resubmit it. Do NOT hard reset.
- "I want to pick up code changes in .fs files" → Use rebuild=true ONLY if you need the project rebuilt (e.g., new file added to .fsproj, package reference changed).
- "The warm-up itself failed with module load errors on session start?" → Then yes, hard reset may help.

VALID REASONS (rare):
- New files added to .fsproj or package references changed (rebuild=true needed)
- Module opens failed during warm-up (cascade of errors on EVERY eval, even '1+1;;')
- Soft reset (reset_fsi_session) didn't fix a genuine session-level problem

INVALID REASONS (common mistakes):
- Your code had a syntax error or type error → fix your code
- You got 'Operation could not be completed due to earlier error' → fix the earlier code
- You're 'not sure' what's wrong → read the diagnostics, they tell you
- You want to 'start fresh' → soft reset is sufficient if truly needed

Set rebuild=true to run 'dotnet build' before reloading.

IMPORTANT:
- rebuild=true returns immediately after scheduling the rebuild/restart.
- During that restart window, get_fsi_status may temporarily report that the session is still warming up instead of returning a full status snapshot.

WORKFLOW: For test-only changes, use this with rebuild=true instead of the full pack/reinstall cycle.
The full pack/reinstall cycle is only needed when SageFs's own source code changes (SageFs\ or SageFs.Server\).""")>]
    member _.hard_reset_fsi_session(
        [<Description("Set rebuild=true to run 'dotnet build' before reloading (default false)")>]
        [<Optional; DefaultParameterValue(false)>]
        rebuild: bool,
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        let doRebuild = rebuild
        logger.LogDebug("MCP-TOOL: hard_reset_fsi_session called, rebuild={Rebuild}", doRebuild)
        let execute =
          task {
            let! result = hardResetSessionResult ctx "mcp" doRebuild None wd
            return
              match result with
              | Ok text -> text, None
              | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
          }
        match doRebuild with
        | true -> execute |> withEchoOutcomeNoAwaitRecord ctx "hard_reset_fsi_session"
        | false -> execute |> withEchoOutcome ctx "hard_reset_fsi_session"

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
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: check_fsharp_code called")
        task {
          let! text = checkFSharpCode ctx "mcp" code None wd
          // checkFSharpCode's own routing failure is plain "Error: <text>"
          // with no structured case behind it — resolve the session again
          // (read-only registry lookups; no side effect on the eval path)
          // to classify a genuine session-routing blocker (warming up,
          // multiple sessions, missing) from the SAME typed resolution
          // resolveSessionId already computes, instead of leaving it
          // unclassified or re-parsing the "Error: ..." text.
          let! resolution = resolveSessionId ctx "mcp" None wd
          let! blocker = sessionRoutingError ctx None wd resolution
          return text, blocker
        }
        |> withEchoOutcome ctx "check_fsharp_code"

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
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: cancel_eval called")
        task {
          let! result = cancelEvalResult ctx "mcp" wd
          return
            match result with
            | Ok text -> text, None
            | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
        }
        |> withEchoOutcome ctx "cancel_eval"

    [<McpServerTool>]
    [<Description("""Enable hot reload (browser auto-refresh) for this session.

This installs Harmony patches on WebApplication.Run/RunAsync() so that when a
.fs file changes, the browser auto-refreshes. Designed for use after the
session was created in Interactive workflow and the user later wants to run
a Falco/Datastar webapp with live reload — without destroying the session.

WORKFLOW:
1. Call this tool. It returns a JSON with `patched`, `workerPort`, `health`,
   `disabledByEnvVar`, and `nextSteps`.
2. If a webapp is already running (webapp.Run() is blocking an eval), call
   `cancel_eval` to stop it.
3. Re-evaluate your `webapp.Run()` call. The Harmony patches now fire on
   this Run() call, the DevReloadMiddleware is injected, and subsequent file
   changes will hot-reload the browser.

Idempotent: safe to call multiple times. The patches only apply to future
webapp.Run() calls; existing in-flight webapps are unaffected.

If the env var SAGEFS_DEVRELOAD=0 is set, the tool returns `disabledByEnvVar:
true` and does NOT install patches. This is intentional — the env var is the
process-wide kill switch for hot reload.""")>]
    member _.enable_hot_reload(
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        let resultJson (patched: bool) (workerPort: int) (health: string) (disabledByEnvVar: bool) (nextSteps: string[]) =
            let result = JsonObject()
            result.["patched"] <- JsonValue.Create(patched)
            result.["workerPort"] <- JsonValue.Create(workerPort)
            result.["health"] <- JsonValue.Create(health)
            result.["disabledByEnvVar"] <- JsonValue.Create(disabledByEnvVar)
            result.["nextSteps"] <- JsonValue.Create(nextSteps)
            result.ToJsonString()
        let disabledByEnvVar =
            match System.Environment.GetEnvironmentVariable("SAGEFS_DEVRELOAD") with
            | "0" | "false" -> true
            | _ -> false
        let healthToString (h: SageFs.DevReload.DevReloadHealth) =
            match h with
            | SageFs.DevReload.PatchPending -> "PatchPending"
            | SageFs.DevReload.PatchFailed r -> sprintf "PatchFailed: %s" r
            | SageFs.DevReload.Injected -> "Injected"
            | SageFs.DevReload.Active _ -> "Active"
            | SageFs.DevReload.Degraded r -> sprintf "Degraded: %s" r
            | SageFs.DevReload.Disabled -> "Disabled"
        // The DevReloadInjector module lives in the SageFs.Host assembly, which
        // is a different assembly than SageFs.dll. We can't `open` it directly
        // because it's a higher-level project (SageFs references SageFs.Host, not
        // vice versa). The F# compiler treats SageFs.Host's modules as
        // unreachable from SageFs.dll, even though the assembly is referenced.
        // Workaround: load the type via reflection.
        let devReloadType : System.Type option =
          System.AppDomain.CurrentDomain.GetAssemblies()
          |> Array.tryPick (fun a ->
            a.GetType("SageFs.DevReloadInjector") |> Option.ofObj)
        let invokeStatic (name: string) (args: obj[]) =
          match devReloadType with
          | Some t ->
            t.GetMethod(name, System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Static)
            |> Option.ofObj
            |> Option.iter (fun m -> m.Invoke(null, args) |> ignore)
          | None -> ()
        // Resolve the session via working directory, then look up its worker port.
        // We need the port to wire the DevReload SSE URL; if it's missing the
        // user hasn't finished warm-up, so surface PatchFailed and tell them.
        logger.LogDebug("MCP-TOOL: enable_hot_reload called")
        withSessionWd ctx "mcp" wd (fun sidStr -> task {
            let sid = match SageFs.WorkerProtocol.SessionId.validate sidStr with
                       | Ok s -> s
                       | Error _ -> SageFs.WorkerProtocol.SessionId.newId ()
            let! infoOpt = ctx.SessionOps.GetSessionInfo sid
            let workerPort = infoOpt |> Option.bind (fun i -> SageFs.WorkerProtocol.SessionLifecycleStatus.workerPort i.Status) |> Option.defaultValue 0
            if disabledByEnvVar then
                SageFs.DevReload.DevReloadHealthTracker.transition SageFs.DevReload.Disabled
                return resultJson false workerPort "Disabled (SAGEFS_DEVRELOAD env var)" true
                    [| "unset SAGEFS_DEVRELOAD or set it to '1' and call enable_hot_reload again" |]
            elif workerPort <= 0 then
                return resultJson false 0 "PatchFailed: worker port not yet set" false
                    [| "Wait for the worker to report WORKER_PORT= on stdout (happens during warm-up), then retry." |]
            elif Option.isNone devReloadType then
                return resultJson false workerPort "PatchFailed: SageFs.DevReloadInjector type not loaded" false
                    [| "This tool runs in the SageFs daemon process. SageFs.Host is normally loaded by the worker. If you see this, the architecture is broken." |]
            else
                invokeStatic "setWorkerPort" [| box workerPort |]
                invokeStatic "install" [||]
                return resultJson true workerPort (healthToString (SageFs.DevReload.DevReloadHealthTracker.current ())) false
                    [| "If a webapp is currently running (webapp.Run() blocking), call cancel_eval to stop it."
                       "Re-evaluate webapp.Run() — Harmony will inject DevReloadMiddleware on this call."
                       "Edit any .fs file under the watched root; browser auto-refresh should fire." |]
        })
        |> withEcho ctx "enable_hot_reload"

    [<McpServerTool>]
    [<Description("""Disable hot reload for this session.

This transitions the DevReload health state to Disabled. The Harmony patches
remain installed in the process but the prefix check will refuse to inject
the DevReloadMiddleware into future webapp.Run() calls.

WORKFLOW:
1. Call this tool. It returns a JSON with `disabled: true` and `health`.
2. If a webapp is already running, you do NOT need to restart it — the
   middleware is already injected and the SSE channel is established.
   However, future webapp.Run() calls will not get the middleware.
3. To re-enable, call `enable_hot_reload` and then re-evaluate webapp.Run().

This is the runtime analog of the SAGEFS_DEVRELOAD=0 env var. Unlike the env
var, this is per-session and reversible. The env var, if set, takes precedence.""")>]
    member _.disable_hot_reload(
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: disable_hot_reload called")
        withSessionWd ctx "mcp" wd (fun _ -> task {
            // Reflection-based call to disableForSession (SageFs.Host module).
            let devReloadType : System.Type option =
              System.AppDomain.CurrentDomain.GetAssemblies()
              |> Array.tryPick (fun a ->
                a.GetType("SageFs.DevReloadInjector") |> Option.ofObj)
            match devReloadType with
            | Some t ->
              t.GetMethod("disableForSession", System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Static)
              |> Option.ofObj
              |> Option.iter (fun m -> m.Invoke(null, [||]) |> ignore)
            | None -> ()
            let health =
                match SageFs.DevReload.DevReloadHealthTracker.current () with
                | SageFs.DevReload.Disabled -> "Disabled"
                | SageFs.DevReload.PatchPending -> "PatchPending (unexpected — disable should transition to Disabled)"
                | SageFs.DevReload.PatchFailed r -> sprintf "PatchFailed: %s" r
                | SageFs.DevReload.Injected -> "Injected (unexpected)"
                | SageFs.DevReload.Active _ -> "Active (unexpected)"
                | SageFs.DevReload.Degraded r -> sprintf "Degraded: %s" r
            let result = JsonObject()
            result.["disabled"] <- JsonValue.Create(true)
            result.["health"] <- JsonValue.Create(health)
            return result.ToJsonString()
        })
        |> withEcho ctx "disable_hot_reload"

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
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: get_completions called")
        getCompletions ctx "mcp" code cursor_position wd |> withEcho ctx "get_completions"

    // ── Package Explorer Tools ──────────────────────────────────────

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
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: explore_namespace called: {Namespace}", namespaceName)
        exploreNamespace ctx "mcp" namespaceName wd |> withEcho ctx "explore_namespace"

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
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first (or pass session_id explicitly) — the daemon will not guess.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: explore_type called: {Type}", typeName)
        exploreType ctx "mcp" typeName wd |> withEcho ctx "explore_type"

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
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: visualize_domain_model called: {Type}", typeName)
        visualizeDomainModel ctx "mcp" typeName wd |> withEcho ctx "visualize_domain_model"

    // ── Session Management Tools ──────────────

    [<McpServerTool>]
    [<Description("""Create a new isolated FSI session with the specified project(s). Each session runs in its own worker process with full type isolation.

⚠️ WARNING: Do NOT create a new session if one already exists for the same project. Use list_sessions first to check. Creating duplicate sessions causes resource starvation — sessions compete for CPU/memory during warmup, making ALL sessions slower or causing them to crash.

WHEN TO USE:
- When you need to load a different project than the current session has loaded.
- When you want to test something in a clean environment without affecting the shared session.
- For running tests that require specific project DLLs (e.g., a test project with Expecto or xUnit).
- Multi-project workflows where isolation between session contexts is important.

AFTER CREATION:
- The session warms up asynchronously (typically 15-30s for test projects).
- Re-check get_fsi_status until it reports State='Ready'. Before that it may return a warming-up message instead of a full status snapshot. Do NOT create another session while waiting.
- Use the returned session ID with switch_session to route subsequent tool calls to the new session.
- Use stop_session when finished to free the worker process.

WORKTREES: a session's working directory is checkout-aware. If working_directory sits inside a git worktree (e.g. `.claude/worktrees/agent-x`), the session is bound to THAT worktree, not to the main checkout, and list_sessions/the dashboard show its branch. A request from inside a worktree never silently routes into the main checkout's session — create a session for the worktree instead of assuming one exists.

projects: Comma-separated list of absolute or relative .fsproj file paths.""")>]
    member _.create_session(
        [<Description("Comma-separated list of .fsproj files to load")>] projects: string,
        [<Description("Working directory for the session")>] working_directory: string,
        [<Description("Your agent or model name (e.g. 'claude', 'copilot', 'cursor'). Used for session routing and multi-agent coordination. Defaults to 'mcp' if omitted.")>]
        [<Optional; DefaultParameterValue("")>]
        agentName: string
    ) : Task<string> =
        let agent = match System.String.IsNullOrWhiteSpace agentName with | true -> "mcp" | false -> agentName
        logger.LogDebug("MCP-TOOL: create_session called: projects={Projects}, dir={Dir}, agent={Agent}", projects, working_directory, agent)
        let projectList = projects.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.toList
        createSession ctx agent projectList working_directory SageFs.WorkflowTypes.SessionWorkflow.Interactive |> withEcho ctx "create_session"

    [<McpServerTool>]
    [<Description("""List all active FSI sessions with their metadata: session ID, project names, current status, working directory, and last activity timestamp.

WHEN TO USE:
- To find session IDs for use with switch_session, stop_session, or get_fsi_status.
- To check whether multiple sessions are running (each session is an isolated worker process).
- To see which session is currently active (tool calls without an explicit session route to the active one).
- After SageFs restarts or after create_session to confirm the session is registered.""")>]
    member _.list_sessions() : Task<string> =
        logger.LogDebug("MCP-TOOL: list_sessions called")
        listSessions ctx |> withEcho ctx "list_sessions"

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
        stopSession ctx session_id |> withEcho ctx "stop_session"

    [<McpServerTool>]
    [<Description("""Switch the active FSI session. All subsequent tool calls that accept working_directory will route to this session.

WHEN TO USE:
- After create_session to make the new session the active target for tool calls.
- In multi-project workflows when switching context between two loaded sessions.
- REQUIRED when multiple sessions share the same working directory — the daemon will not guess which one you want. Call switch_session first, then proceed with other tools.

ROUTING BEHAVIOR:
- working_directory auto-routes only when exactly ONE session matches that directory.
- When multiple sessions match the same directory, the daemon returns an error listing the matches. Call switch_session with the session_id you want, then retry.
- If you provide session_id directly on any tool call, that always wins over working_directory routing.
- Use list_sessions to see available session IDs.""")>]
    member _.switch_session(
        [<Description("Session ID to switch to (from list_sessions)")>] session_id: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: switch_session called: id={Id}", session_id)
        switchSession ctx "mcp" session_id |> withEcho ctx "switch_session"

    [<Description("""Switch the workflow mode for a session.
Workflows control the tradeoff between REPL capability and browser hot reload:
- REPL (Interactive): Full type redefinition, interactive exploration
- Live (WebLive): Browser hot reload on save, expression-only REPL

Set dryRun=true to preview the transition cost without executing.
Switching creates a new session — REPL definitions and cell state are lost.""")>]
    member _.switch_workflow(
        [<Description("Target workflow: 'interactive' or 'weblive' (aliases: 'repl', 'live')")>]
        target: string,
        [<Description("Working directory of the MCP client.")>]
        working_directory: string,
        [<Description("Preview only — returns transition cost without switching. Default: false")>]
        dryRun: System.Nullable<bool>
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        let dry = match dryRun.HasValue with | true -> dryRun.Value | false -> false
        logger.LogDebug("MCP-TOOL: switch_workflow called: target={Target}, dir={Dir}, dryRun={DryRun}", target, working_directory, dry)
        switchWorkflow ctx "mcp" wd target dry |> withEcho ctx "switch_workflow"

    // ── Elm State Tools ──────────────────────────────────────────

    [<Description("""Get the current Elm model state rendered as regions. Shows editor content, recent output, diagnostics, and sessions. Useful for understanding what SageFs is currently displaying.

WHEN TO USE:
- To understand the current state of the SageFs TUI/GUI without taking a screenshot.
- To read the most recently displayed eval output or error message as shown in the UI.
- To check what the user is currently viewing in their SageFs terminal (active pane, cursor position, etc.).
- As a debugging tool to verify the UI model reflects the expected state after an operation.

OUTPUT: A text rendering of each named UI region (header, editor, output, test panel, status bar, etc.) showing what is currently displayed.""")>]
    member _.get_elm_state() : Task<string> =
        logger.LogDebug("MCP-TOOL: get_elm_state called")
        getElmState ctx |> withEcho ctx "get_elm_state"

    [<McpServerTool>]
    [<Description("""Plan a trustworthy targeted verification pass for one changed behavior.

USE CASE:
- When you changed one behavior and want SageFs to tell you the safest next verification step.
- This prefers local snippet-first proof and only escalates when a named exact guard is provided.

INPUTS:
- behavior: the symbol or behavior under change.
- exact_guard: optional exact full test name to run after local proof.

TRUST MODEL:
- Refuses to claim green when session trust is ambiguous or loaded code is stale.
- Uses warmup file status when available to detect stale definitions.
- Does not run tests; it returns the next trustworthy verification move.""")>]
    member _.targeted_verify(
        [<Description("Behavior or symbol under change (for example 'UserPreferences.loadFromFile').")>]
        behavior: string,
        [<Description("Optional exact full test name to use as the regression guard after local proof.")>]
        [<Optional; DefaultParameterValue("")>]
        exact_guard: string,
        [<Description("Working directory of the MCP client. When provided, routes to the matching session if exactly one session uses this directory. If multiple sessions share the directory, you must call switch_session first.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        let guard = match System.String.IsNullOrWhiteSpace exact_guard with | true -> None | false -> Some exact_guard
        logger.LogDebug("MCP-TOOL: targeted_verify called, behavior={Behavior}, exact_guard={ExactGuard}", behavior, exact_guard)
        targetedVerifyResult ctx "mcp" wd behavior guard |> withEchoOutcome ctx "targeted_verify"

    [<McpServerTool>]
    [<Description("""Get a compact local summary of MCP friction recorded by SageFs.

USE CASE:
- Find recurring blockers without copying complaints into chat.
- See whether tools are being abandoned or chained too often.

OUTPUT:
- number of blocker families currently present
- number of tracked tools in the friction log
- number of explicit feedback items

This reads only local friction intelligence. It does not phone home.""")>]
    member _.get_friction_summary() : Task<string> =
        logger.LogDebug("MCP-TOOL: get_friction_summary called")
        let version = SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current ()
        match ctx.FrictionStore with
         | Some store ->
           task {
             let! result = SageFs.Features.McpFrictionRecorder.Recorder.summarizeDirect store (Some version)
             return
               match result with
               | Ok summary -> summary
               | Error err -> sprintf "Error: Friction store read failed: %s" err
           } |> withEcho ctx "get_friction_summary"
         | None ->
           task { return "No friction store configured" } |> withEcho ctx "get_friction_summary"

    [<McpServerTool>]
    [<Description("""Get a structured local MCP friction report that an agent can act on.

USE CASE:
- Point an agent at recurring MCP pain and ask it to reduce the top issue.
- Review which tools, blockers, follow-up chains, and explicit complaints are wasting time.

OUTPUT:
- JSON report with ranked problematic tools, top blockers, common tool chains, and recent explicit feedback
- each ranked tool includes a suggested remediation target

This is a local read model over recorded friction, not a self-healing system.""")>]
     member _.get_friction_report() : Task<string> =
         logger.LogDebug("MCP-TOOL: get_friction_report called")
         let version = SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current ()
         match ctx.FrictionStore with
         | Some store ->
           task {
             let! result = SageFs.Features.McpFrictionRecorder.Recorder.reportDirect store (Some version)
             return
               match result with
               | Ok report -> frictionReportJson report
               | Error err -> sprintf "Error: Friction store read failed: %s" err
           } |> withEcho ctx "get_friction_report"
         | None ->
           task {
             return "{}"  // No friction store configured
           } |> withEcho ctx "get_friction_report"

    [<McpServerTool>]
    [<Description("""Report structured local MCP friction so SageFs can learn what is confusing.

USE CASE:
- Record that a tool was unclear, too large, or insufficient.
- Preserve which alternative tool resolved the issue.

INPUTS:
- tool_name: the tool that caused friction
- feedback_kind: one of output_too_large, intent_unclear, name_misleading, needed_another_tool, trust_not_established
- short_reason: compact explanation of the pain
- alternative_tool: optional tool that actually resolved the task

This stores local feedback only.""")>]
    member _.report_friction(
        [<Description("Tool name that caused friction.")>] tool_name: string,
        [<Description("Feedback kind: output_too_large, intent_unclear, name_misleading, needed_another_tool, trust_not_established")>] feedback_kind: string,
        [<Description("Short human-readable explanation of the friction.")>] short_reason: string,
        [<Description("Optional alternative tool that resolved the issue.")>]
        [<Optional; DefaultParameterValue("")>]
        alternative_tool: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: report_friction called, tool={Tool}, kind={Kind}", tool_name, feedback_kind)
        let kind =
          match feedback_kind with
          | "output_too_large" -> SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.ToolOutputWasTooLarge
          | "intent_unclear" -> SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.ToolIntentWasUnclear
          | "name_misleading" -> SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.ToolNameWasMisleading
          | "needed_another_tool" -> SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.NeededAnotherToolToFinish
          | _ -> SageFs.Features.FrictionTelemetryTypes.ExplicitFeedbackKind.ResultDidNotEstablishTrust
        let alternative =
          match System.String.IsNullOrWhiteSpace alternative_tool with
          | true -> SageFs.Features.FrictionTelemetryTypes.AlternativePath.NoAlternativeRecorded
          | false ->
            SageFs.Features.FrictionTelemetryTypes.ToolName.create alternative_tool
            |> Result.map SageFs.Features.FrictionTelemetryTypes.AlternativePath.ResolvedWithTool
            |> Result.defaultValue SageFs.Features.FrictionTelemetryTypes.AlternativePath.ResolvedOutsideMcp
        let feedback : SageFs.Features.FrictionTelemetryTypes.ExplicitFeedback =
          { SageFs.Features.FrictionTelemetryTypes.ExplicitFeedback.OccurredAtUtc = System.DateTimeOffset.UtcNow
            Session = SageFs.Features.FrictionTelemetryTypes.SessionRef.create "mcp" |> ok
            Tool = SageFs.Features.FrictionTelemetryTypes.ToolName.create tool_name |> ok
            Kind = kind
            ShortReason = short_reason
            AlternativeUsed = alternative
            SageFsVersion = SageFs.Features.FrictionTelemetryTypes.SageFsVersion.current () }
        task {
          match ctx.FrictionStore with
          | Some store ->
            let! result = SageFs.Features.McpFrictionRecorder.Recorder.appendFeedbackDirect store feedback
            return
              match result with
              | Ok () -> "Recorded local friction feedback."
              | Error err -> sprintf "Error: Failed to persist friction feedback: %s" err
          | None ->
            return "No friction store configured — feedback not recorded."
        } |> withEcho ctx "report_friction"

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
        explainTestRun ctx test_name |> withEcho ctx "explain_test_run"

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
        queryTestCoverage ctx symbol |> withEcho ctx "query_test_coverage"

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
        getFileCoverage ctx file |> withEcho ctx "get_file_coverage"

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

WORKFLOW: When live testing reports a failure, call explain_test_failure with the test name to see what changed and why it broke — without manually diffing recent commits.""")>]
    member _.explain_test_failure(
        [<Description("Test name or substring to match against FullName or DisplayName")>]
        test_name: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: explain_test_failure called, test={Test}", test_name)
        explainTestFailure ctx test_name |> withEcho ctx "explain_test_failure"

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
        decomposePipeline code |> withEcho ctx "decompose_pipeline"

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
        diagnose ctx |> withEcho ctx "diagnose"

    [<McpServerTool>]
    [<Description("""Analyze test coverage quality — find blind spots, correlate failures, assess diagnostic power.

Composes failure narratives + IL instrumentation bitmaps + test dependency graph into per-failure coverage intelligence.

OUTPUT: Per-failing-test analysis with:
- Coverage verdict: WellCovered (>80% branches hit), PartialBlindSpot (40-80%), or DiagnosticBlindSpot (<40%)
- Branch coverage percentage and uncovered branch locations
- Causal symbols that changed before the failure
- Correlated failures (other tests covering the same code paths)
- Blind spot details: files, lines, and branch IDs with zero coverage

WORKFLOW: Call after test failures to understand whether your tests actually cover the code that broke.
Complements 'diagnose' (which tells you what failed) by telling you how well your tests can detect the failure.""")>]
    member _.coverage_intel() : Task<string> =
        logger.LogDebug("MCP-TOOL: coverage_intel called")
        coverageIntel ctx |> withEcho ctx "coverage_intel"

    [<McpServerTool>]
    [<Description("""Forecast performance impact for evaluated cells — detect regressions, measure downstream blast radius.

Analyzes eval timeline statistics (P50/P95), cell dependency graph, and duration trends to predict whether
a cell's performance trajectory is healthy, needs investigation, or requires refactoring.

INPUT (optional): cellId — analyze a specific cell. If omitted, analyzes all cells in the dependency graph.

OUTPUT: Per-cell analysis with:
- P50/P95 latency percentiles
- Duration trend slope (positive = getting slower)
- Downstream cell count (blast radius of a regression)
- Recommendation: Acceptable, Investigate, or Refactor
- Regression causes: DependencyGrowth (downstream >15 cells), LatencySpike (P95 >2000ms), Unknown

WORKFLOW: Call periodically or after slow evaluations to catch regressions before they compound.
Pairs with 'suggest_next_action' which folds impact data into a prioritized action queue.""")>]
    member _.impact_forecast(
        [<Description("Optional cell ID to analyze. Omit to analyze all cells.")>]
        [<Optional; DefaultParameterValue(0)>]
        cellId: int
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: impact_forecast called for cell {cellId}", cellId)
        let cellOpt = match cellId with | 0 -> None | n -> Some n
        impactForecast ctx cellOpt |> withEcho ctx "impact_forecast"

    [<McpServerTool>]
    [<Description("""Get a prioritized action queue — the intelligent "what should I do next?" recommendation.

Composes coverage intelligence + impact forecasts + stale cell detection into a ranked queue of actions,
sorted by priority (lowest number = most urgent). Also computes a session health grade.

OUTPUT:
- Session health grade: Healthy, NeedsAttention (with reason), or Critical (with reason)
- Ranked action list (top 10) with:
  - Kind: InvestigateFailure, WriteTest, InvestigatePerformance, ReEvaluateCell, RunTests
  - Priority score (lower = more urgent): failures at 10, blind spots at 30, perf at 50, stale at 70, tests at 90
  - Human-readable reason explaining why this action matters
- Aggregate counts: total failures, blind spots, regressions

WORKFLOW: This is the top-level intelligence tool — call it when you want ONE answer about what to do next.
It internally calls coverage_intel and impact_forecast, so you don't need to call those separately.
Replaces manual triage of test results, coverage, and performance data.""")>]
    member _.suggest_next_action() : Task<string> =
        logger.LogDebug("MCP-TOOL: suggest_next_action called")
        suggestNextAction ctx |> withEcho ctx "suggest_next_action"

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
        planRipple ctx changed_cells |> withEcho ctx "plan_ripple"

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
        previewWhatIf ctx binding_name new_code |> withEcho ctx "preview_what_if"

    [<McpServerTool>]
    [<Description("""Get type-directed suggestions for what to evaluate next.

Analyzes all bindings currently in scope and generates contextually-appropriate suggestions
based on their types. For example, a list binding gets List.length, List.head, List.sort suggestions;
an option binding gets Option.defaultValue, Option.map suggestions.

OUTPUT: Ranked suggestions with confidence scores, code snippets, and explanations.

WORKFLOW: When you're not sure what to try next in the REPL, call this for intelligent suggestions.""")>]
    member _.suggest_next_cell() : Task<string> =
        logger.LogDebug("MCP-TOOL: suggest_next_cell called")
        suggestNextCell ctx |> withEcho ctx "suggest_next_cell"

    [<McpServerTool>]
    [<Description("""Get the session filmstrip — a visual history of all evaluations in the current session.

Shows each evaluation as a "frame" with its index, code snippet, binding count, duration, and test summary.
Use the optional filter parameter to search for specific frames by label substring.

OUTPUT: Overview statistics followed by individual frame cards.

WORKFLOW: Use to review what happened in a session, find when a binding was introduced, or understand the session timeline.""")>]
    member _.get_session_filmstrip(
        [<Description("Optional filter string to search frames by label (case-insensitive substring match)")>]
        [<Optional; DefaultParameterValue("")>]
        filter: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: get_session_filmstrip called, filter={Filter}", filter)
        let filterOpt = match System.String.IsNullOrWhiteSpace filter with | true -> None | false -> Some filter
        getSessionFilmstrip ctx filterOpt |> withEcho ctx "get_session_filmstrip"

    // ── Phase 1b: Orphaned module MCP tools ──

    [<McpServerTool>]
    [<Description("""Export the current session as a notebook-style .fsx file with cell metadata.

Each cell preserves its code, output, index, and dependencies as structured comments.
The exported format can be re-imported with importNotebook.

OUTPUT: A complete .fsx file string with cell boundaries and metadata.

WORKFLOW: Use this to save your interactive session as a portable, re-runnable notebook.""")>]
    member _.export_notebook(
        [<Description("Optional project name for the notebook header (defaults to 'SageFs Session')")>]
        [<Optional; DefaultParameterValue("")>]
        project_name: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: export_notebook called, project={Name}", project_name)
        let nameOpt = match System.String.IsNullOrWhiteSpace project_name with | true -> None | false -> Some project_name
        exportNotebook ctx nameOpt |> withEcho ctx "export_notebook"

    [<McpServerTool>]
    [<Description("""Export the current session as a clean, topologically-sorted .fsx transcript.

Uses the cell dependency graph to order cells so that dependencies come before dependents.
Deduplicates cells and strips metadata — the result is a minimal, runnable .fsx script.

OUTPUT: A clean .fsx file string ready to run with `dotnet fsi`.

WORKFLOW: Use this to extract a clean, reproducible script from an exploratory session.""")>]
    member _.export_session_transcript(
        [<Description("Optional project name for the transcript header (defaults to 'SageFs Session')")>]
        [<Optional; DefaultParameterValue("")>]
        project_name: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: export_session_transcript called, project={Name}", project_name)
        let nameOpt = match System.String.IsNullOrWhiteSpace project_name with | true -> None | false -> Some project_name
        exportSessionTranscript ctx nameOpt |> withEcho ctx "export_session_transcript"

    [<McpServerTool>]
    [<Description("""Get the message journal — a structured audit log of eval events.

Synthesizes journal entries from eval history, classifying successful evals as Info
and failed evals as Error. Supports filtering by minimum severity level and source.

OUTPUT: Journal summary statistics followed by timestamped, level-tagged entries.

WORKFLOW: Use this for observability — review what happened, filter to errors only, or trace eval activity.""")>]
    member _.get_message_journal(
        [<Description("Minimum severity level to include: 'debug', 'info', 'warn', or 'error' (defaults to all)")>]
        [<Optional; DefaultParameterValue("")>]
        min_level: string,
        [<Description("Optional source filter — only show entries from matching sources (case-insensitive)")>]
        [<Optional; DefaultParameterValue("")>]
        source: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: get_message_journal called, level={Level}, source={Source}", min_level, source)
        let levelOpt = match System.String.IsNullOrWhiteSpace min_level with | true -> None | false -> Some min_level
        let sourceOpt = match System.String.IsNullOrWhiteSpace source with | true -> None | false -> Some source
        getMessageJournal ctx levelOpt sourceOpt |> withEcho ctx "get_message_journal"

    [<McpServerTool>]
    [<Description("""Get eval timeline with performance sparkline and percentile statistics.

Shows a visual sparkline of recent eval durations, plus P50/P95/P99 and mean latency.
Also lists the most recent evaluations with their cell IDs, status icons, and durations.

OUTPUT: Sparkline visualization, percentile statistics, and recent eval entries.

WORKFLOW: Use this to monitor eval performance trends and identify slow cells.""")>]
    member _.get_eval_timeline(
        [<Description("Width of the sparkline in characters (defaults to 20)")>]
        [<Optional; DefaultParameterValue(0)>]
        sparkline_width: int
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: get_eval_timeline called, width={Width}", sparkline_width)
        let widthOpt = match sparkline_width with | 0 -> None | w -> Some w
        getEvalTimeline ctx widthOpt |> withEcho ctx "get_eval_timeline"

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
        manageScratchPad ctx action None None |> withEcho ctx "manage_scratch_pad"

    [<McpServerTool>]
    [<Description("""Get a diff between recent eval outputs — before vs after comparison.

Compares the two most recent eval outputs (or outputs for a specific cell) and shows
a line-by-line diff with added/removed/modified/unchanged classifications.

OUTPUT: Diff summary with counts of changes and line-by-line breakdown.

WORKFLOW: Use after re-evaluating a cell to see exactly what changed in the output.""")>]
    member _.get_eval_diff(
        [<Description("Optional cell index to diff (defaults to comparing the two most recent evals)")>]
        [<Optional; DefaultParameterValue(0)>]
        cell_index: int
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: get_eval_diff called, cellIndex={Idx}", cell_index)
        let idxOpt = match cell_index with | 0 -> None | i -> Some i
        getEvalDiff ctx idxOpt |> withEcho ctx "get_eval_diff"

    [<McpServerTool>]
    [<Description("""List all discovered tests in the current session, optionally filtered by name pattern or file path.

Returned data includes total count, grouping by source file, and per-test line numbers for editor navigation.

Parameters:
- pattern: substring filter on test name (empty = all tests)
- file_path: filter by source file path (empty = all files)

OUTPUT: JSON with TotalCount, Returned, FilterApplied, Summary, and GroupedByFile with StartLine/EndLine for each test.

WORKFLOW: Use this to discover what tests exist, or to build a pattern for send_fsharp_code-based test runs.""")>]
    member _.list_tests(
        [<Description("Optional substring filter on test name (empty for all tests)")>]
        [<Optional; DefaultParameterValue("")>]
        pattern: string,
        [<Description("Optional file path filter (empty for all files)")>]
        [<Optional; DefaultParameterValue("")>]
        file_path: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: list_tests called, pattern={Pattern}, file={File}", pattern, file_path)
        let patOpt = match pattern with | "" | null -> None | s -> Some s
        let fileOpt = match file_path with | "" | null -> None | s -> Some s
        listTests ctx patOpt fileOpt |> withEcho ctx "list_tests"

    [<McpServerTool>]
    [<Description("""Get the cell dependency graph annotated with staleness information.

Shows which cells are stale (their dependencies changed but they haven't re-evaluated), what each cell produces/consumes, and the full upstream/downstream wiring.

OUTPUT: JSON with TotalCells, TotalStale, TotalEdges, StaleCellIds, Summary, and per-node details (Produces, Consumes, UpstreamIds, DownstreamIds, IsStale, StaleCauses).

WORKFLOW: After editing code, use this to understand the ripple impact before deciding which cells to re-evaluate. Pair with plan_ripple for the full re-eval plan.""")>]
    member _.get_cell_dependencies() : Task<string> =
        logger.LogDebug("MCP-TOOL: get_cell_dependencies called")
        getCellDependencies ctx |> withEcho ctx "get_cell_dependencies"

    [<McpServerTool>]
    [<Description("""Discover and rank all SageFs features by relevance to your current session state.

Acts as a built-in "tour guide" — analyzes your session context (failing tests, stale cells, eval count, discovered tests) and surfaces the most useful features first.

Parameters:
- topic: optional focus keyword (e.g. "testing", "performance", "export") to narrow suggestions

OUTPUT: JSON with ContextSummary, TotalKnownFeatures, Returned count, and Suggestions ranked Essential > Recommended > Optional. Each suggestion includes ToolName, ShortDescription, ExampleUsage, and WhyNow explanation.

WORKFLOW: Call this at the start of a session to see what to do next, or any time you feel lost.""")>]
    member _.discover_features(
        [<Description("Optional topic keyword to focus suggestions (e.g. 'testing', 'performance', 'export'). Empty for all features.")>]
        [<Optional; DefaultParameterValue("")>]
        topic: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: discover_features called, topic={Topic}", topic)
        let topicOpt = match topic with | "" | null -> None | s -> Some s
        discoverFeatures ctx topicOpt |> withEcho ctx "discover_features"

    [<McpServerTool>]
    [<Description("""Given a failing test, compose explain_test_failure → extract causal symbol → preview ripple into a single repair plan.

V1 does NOT suggest a new value for the binding — it surfaces the causal symbol, its current code, and the ripple of cells that would re-evaluate on any change. The developer supplies the new value.

Steps performed automatically:
1. Find the failure narrative for the test (which symbols/files changed, when it last passed)
2. Extract the top causal symbol from CausalChanges
3. Look up the symbol's current binding value and type in the session
4. Build a what-if ripple plan for that symbol (how many downstream cells re-evaluate)
5. Return a structured suggestion: "Change X — call preview_what_if to test your fix"

INPUT: test_name — substring to match against test FullName or DisplayName.
OUTPUT: JSON with TestName, Summary, TimeSinceLastPass, CausalChanges, PrimarySymbol, RipplePlan (Symbol, CurrentCode, TypeSig, AffectedCellCount, RippleSteps), and Suggestion.

WORKFLOW: When run_tests shows a failure, call suggest_repair with the test name. Read PrimarySymbol and RipplePlan. Call preview_what_if with your candidate fix before applying it.""")>]
    member _.suggest_repair(
        [<Description("Test name or substring to match against FullName or DisplayName")>]
        test_name: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: suggest_repair called, test={Test}", test_name)
        suggestRepair ctx test_name |> withEcho ctx "suggest_repair"

    [<McpServerTool>]
    [<Description("""Run the session's executable project (OutputType=Exe) inside the session, the way `dotnet run` would, with hot reload.

Invokes the compiled entry point, applies the project's Properties/launchSettings.json (the first "Project" profile's environment variables and applicationUrl), and uses a free loopback port when the project configures no URL. An Interactive session is first restarted into WebLive so hot reload is installed — its REPL bindings are lost. Saving the project's source files then hot-patches the running app.

Parameters:
- project: project name, file name or path (optional — the active project, or the only executable)

OUTPUT: JSON with State (Running|Exited|Crashed), Message, Urls, EntryPoint and RunId — or an error that says what to do next.

WORKFLOW: list_runnable_projects to see what can run → run_app → edit and save to hot reload → stop_app. The dashboard's Run button does the same.""")>]
    member _.run_app(
        [<Description("Project name, file name or path (optional — the active project, or the only executable)")>]
        [<Optional; DefaultParameterValue("")>]
        project: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: run_app called, project={Project}", project)
        runApp ctx project |> withEcho ctx "run_app"

    [<McpServerTool>]
    [<Description("""Stop the app started by run_app. Its web host stops gracefully and frees its port; the session keeps running. A console app without a web host cannot be stopped in place — hard-reset the session instead.

OUTPUT: JSON with State (NotRunning) and Message — or an error that says what to do next.

WORKFLOW: Use after run_app. The dashboard's Stop App button does the same.""")>]
    member _.stop_app() : Task<string> =
        logger.LogDebug("MCP-TOOL: stop_app called")
        stopApp ctx |> withEcho ctx "stop_app"

    [<McpServerTool>]
    [<Description("""List the session's projects and which of them run_app can run (OutputType=Exe).

OUTPUT: JSON with TotalProjects, ExecutableCount, ActiveProject, App (the current app state) and Projects (Path, Role, PackageRefs).

WORKFLOW: Call before run_app to see what can run.""")>]
    member _.list_runnable_projects() : Task<string> =
        logger.LogDebug("MCP-TOOL: list_runnable_projects called")
        listRunnableProjects ctx |> withEcho ctx "list_runnable_projects"

    // ── Cohort tools v1 (cohort-integration-plan.md Slice 2, item 9) ──────
    //
    // One implicit cohort per daemon. Every tool resolves the caller's
    // identity from the BOUND MCP connection (memberIdFor agentName), never
    // from a self-declared argument — see Mcp.fs's memberIdFor doc. No
    // affordance gating yet (that is Slice 3): these tools are always listed.

    [<McpServerTool>]
    [<Description("""Join this daemon's cohort — the shared multi-agent coordination session that tracks who else is working here, file/project claims, and landing requests.

v1 has no separate "create cohort" step: the FIRST agent to join an empty cohort automatically becomes its conductor (the only member who can reassign an orphaned claim or delegate conductor to someone else).

WHEN TO USE: Once per agent, before acquiring claims or requesting a landing, when multiple agents/sub-agents may be touching this repo concurrently.

OUTPUT: Confirmation text, noting whether you became the conductor and which session (if any) you were bound to for the per-session test matrix.""")>]
    member _.join_cohort(
        [<Description("Your agent or model name (e.g. 'claude', 'copilot', 'cursor'). Identifies you in cohort membership and claim ownership.")>]
        agentName: string,
        [<Description("Your role: 'Implementer', 'Verifier', or 'Observer'.")>]
        role: string,
        [<Description("Working directory of the MCP client — resolved to a session id (the same routing send_fsharp_code uses) and recorded so the cohort's per-session test matrix can attribute that checkout's test outcomes to you. Optional: omit to fall back to your active session or the daemon's own working directory.")>]
        [<Optional; DefaultParameterValue("")>]
        working_directory: string
    ) : Task<string> =
        let wd = match System.String.IsNullOrWhiteSpace working_directory with | true -> None | false -> Some working_directory
        logger.LogDebug("MCP-TOOL: join_cohort called by {AgentName}, role={Role}, workingDir={Dir}", agentName, role, working_directory)
        task {
          let! result = SageFs.McpTools.joinCohort ctx agentName role wd
          return
            match result with
            | Ok text -> text, None
            | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
        }
        |> withEchoOutcome ctx "join_cohort"

    [<McpServerTool>]
    [<Description("""Leave this daemon's cohort. Every claim you still hold is orphaned (not released to anyone — the conductor must reassign it or it stays orphaned).

WHEN TO USE: When you are done working in this daemon for this session.

OUTPUT: Confirmation text.""")>]
    member _.leave_cohort(
        [<Description("Your agent or model name — must match the name you joined with.")>]
        agentName: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: leave_cohort called by {AgentName}", agentName)
        task {
          let! result = SageFs.McpTools.leaveCohort ctx agentName
          return
            match result with
            | Ok text -> text, None
            | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
        }
        |> withEchoOutcome ctx "leave_cohort"

    [<McpServerTool>]
    [<Description("""Acquire an exclusive claim over a file or project directory, so other cohort members know it is yours to edit right now.

Claims are exclusive within their scope: a File claim conflicts with any other Held claim over the same path or an overlapping Project claim; a Project claim conflicts with any Held claim on or under its directory.

WHEN TO USE: Before editing a file/project another cohort member might also touch. Present the returned fence when you later release or land it — presenting a stale fence is refused.

OUTPUT: Confirmation text with the new claim id and fence, or a conflict error naming the current holder.""")>]
    member _.acquire_claim(
        [<Description("Your agent or model name — must match the name you joined with.")>]
        agentName: string,
        [<Description("The scope to claim: 'file:<repo-relative-path>' or 'project:<repo-relative-.fsproj-path>'.")>]
        scope: string,
        [<Description("One-line reason for the claim (max 200 chars, no newlines) — shown to other cohort members.")>]
        purpose: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: acquire_claim called by {AgentName}, scope={Scope}", agentName, scope)
        task {
          let! result = SageFs.McpTools.acquireClaim ctx agentName scope purpose
          return
            match result with
            | Ok text -> text, None
            | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
        }
        |> withEchoOutcome ctx "acquire_claim"

    [<McpServerTool>]
    [<Description("""Release a claim you hold, freeing its scope for others.

The presented fence must match the claim's current fence exactly (from acquire_claim's or get_cohort_status's output) — a stale fence is refused, and only the current holder may release.

OUTPUT: Confirmation text, or an error naming why the release was refused.""")>]
    member _.release_claim(
        [<Description("Your agent or model name — must match the name you joined with.")>]
        agentName: string,
        [<Description("The claim id to release (from acquire_claim's output or get_cohort_status).")>]
        claimId: string,
        [<Description("The claim's current fence (from acquire_claim's output or get_cohort_status) — a stale value is refused.")>]
        fence: int64
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: release_claim called by {AgentName}, claim={ClaimId}", agentName, claimId)
        task {
          let! result = SageFs.McpTools.releaseClaim ctx agentName claimId fence
          return
            match result with
            | Ok text -> text, None
            | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
        }
        |> withEchoOutcome ctx "release_claim"

    [<McpServerTool>]
    [<Description("""Reassign an ORPHANED claim (its holder departed without releasing it) to another present cohort member. CONDUCTOR-ONLY — refused with a "not the cohort conductor" error for anyone else.

WHEN TO USE: You are the conductor, a claim shows as Orphaned in get_cohort_status, and someone needs to take it over.

OUTPUT: Confirmation text, or an error (not conductor / claim not orphaned / target not present / scope conflict).""")>]
    member _.reassign_claim(
        [<Description("Your agent or model name — must be the cohort's current conductor.")>]
        agentName: string,
        [<Description("The orphaned claim's id (from get_cohort_status).")>]
        claimId: string,
        [<Description("The recipient's display name exactly as get_cohort_status prints it (e.g. 'mcp:...' or a plain agent name).")>]
        toMember: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: reassign_claim called by {AgentName}, claim={ClaimId}, to={ToMember}", agentName, claimId, toMember)
        task {
          let! result = SageFs.McpTools.reassignClaim ctx agentName claimId toMember
          return
            match result with
            | Ok text -> text, None
            | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
        }
        |> withEchoOutcome ctx "reassign_claim"

    [<McpServerTool>]
    [<Description("""Queue a landing request: propose that your commits be rebased onto the integration head, verified, and fast-forwarded in. v1 landings are strictly serial (one FIFO queue) — this only queues the request; rebase/verify/land themselves are a later slice's wiring and are not yet performed.

OUTPUT: Confirmation text with the new landing id, or a validation error (invalid statement, unknown/stale claim).""")>]
    member _.request_landing(
        [<Description("Your agent or model name — must match the name you joined with.")>]
        agentName: string,
        [<Description("Comma-separated 'claimId:fence' pairs backing this landing (from acquire_claim's output). Empty string if none.")>]
        claims: string,
        [<Description("Comma-separated commit SHAs this landing would bring in.")>]
        commits: string,
        [<Description("Why this landing should happen (max 1000 chars) — becomes the merge/squash message body.")>]
        statement: string
    ) : Task<string> =
        logger.LogDebug("MCP-TOOL: request_landing called by {AgentName}", agentName)
        task {
          let! result = SageFs.McpTools.requestLanding ctx agentName claims commits statement
          return
            match result with
            | Ok text -> text, None
            | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
        }
        |> withEchoOutcome ctx "request_landing"

    [<McpServerTool>]
    [<Description("""Read this daemon's cohort: members and roles/presence, claims and their holders/fences, and the ledger head/conductor. Wait-free — reads a published snapshot, never waits on other cohort activity.

WHEN TO USE: Before acquiring a claim (check for conflicts), to see who else is in the cohort, or to find a claim/landing id to act on.

OUTPUT: Plain-text summary. The v1 read model does not yet include the landing queue's contents (Cohort.fs's CohortFrame is trimmed to members/claims/test data) — landing state is reported at request_landing time only.""")>]
    member _.get_cohort_status() : Task<string> =
        logger.LogDebug("MCP-TOOL: get_cohort_status called")
        task {
          let! result = SageFs.McpTools.getCohortStatus ctx
          return
            match result with
            | Ok text -> text, None
            | Error err -> sprintf "Error: %s" (SageFs.SageFsError.describeForAgent err), Some err
        }
        |> withEchoOutcome ctx "get_cohort_status"

