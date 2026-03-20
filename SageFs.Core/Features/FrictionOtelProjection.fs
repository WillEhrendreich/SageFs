module SageFs.Features.FrictionOtelProjection

open System.Diagnostics
open SageFs
open SageFs.Features.FrictionTelemetryTypes

module Projection =
  let tags (event: FrictionEvent) =
    let baseTags = [
      "sagefs.mcp.tool_name", box (ToolName.value event.Tool)
      "sagefs.mcp.intent_kind", box (string event.Intent)
      "sagefs.mcp.outcome_kind", box (string (FrictionEvent.outcomeKind event))
      "sagefs.session.id", box (SessionRef.value event.Session)
      "sagefs.mcp.context_cost", box (string event.ContextCost)
      "sagefs.mcp.duration_ms", box (DurationMs.value event.Duration)
    ]
    match event.Outcome with
    | FrictionOutcome.EncounteredBlocker blocker ->
      ("sagefs.mcp.blocker_kind", box (string blocker)) :: baseTags
    | FrictionOutcome.RecoveredVia (ResolutionKind.SolvedWithDifferentTool tool) ->
      ("sagefs.mcp.resolution_kind", box "SolvedWithDifferentTool")
      :: ("sagefs.mcp.resolution_tool", box (ToolName.value tool))
      :: baseTags
    | FrictionOutcome.RecoveredVia resolution ->
      ("sagefs.mcp.resolution_kind", box (string resolution)) :: baseTags
    | _ -> baseTags

  let emit (event: FrictionEvent) =
    use span = Instrumentation.startSpan Instrumentation.mcpSource "mcp.friction" (tags event)
    Instrumentation.succeedSpan span
