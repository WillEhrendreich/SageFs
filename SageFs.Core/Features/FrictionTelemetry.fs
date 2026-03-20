module SageFs.Features.FrictionTelemetry

open SageFs.Features.FrictionTelemetryTypes

type ToolFrictionSummary = {
  Tool: ToolName
  Invocations: int
  BlockedCount: int
  AbandonedCount: int
}

type TopBlockerSummary = {
  Blocker: BlockerKind
  Count: int
  MostAffectedTools: ToolName list
}

type TransitionSummary = {
  FromTool: ToolName
  ToTool: ToolName
  Frequency: int
}

module Summaries =
  let toolSummaries (events: FrictionEvent list) =
    events
    |> List.groupBy (fun event -> event.Tool)
    |> List.map (fun (tool, grouped) ->
      { Tool = tool
        Invocations = grouped.Length
        BlockedCount = grouped |> List.filter (fun event -> match event.Outcome with | FrictionOutcome.EncounteredBlocker _ -> true | _ -> false) |> List.length
        AbandonedCount = grouped |> List.filter (fun event -> event.Outcome = FrictionOutcome.AbandonedWithoutResolution) |> List.length })

  let topBlockers (events: FrictionEvent list) =
    events
    |> List.choose (fun event ->
      match event.Outcome with
      | FrictionOutcome.EncounteredBlocker blocker -> Some (blocker, event.Tool)
      | _ -> None)
    |> List.groupBy fst
    |> List.map (fun (blocker, grouped) ->
      { Blocker = blocker
        Count = grouped.Length
        MostAffectedTools = grouped |> List.map snd |> List.distinct })

  let transitions (events: FrictionEvent list) =
    events
    |> List.choose (fun event ->
      match event.FollowUp with
      | FollowUp.FollowedByTool nextTool -> Some (event.Tool, nextTool)
      | _ -> None)
    |> List.groupBy id
    |> List.map (fun ((fromTool, toTool), grouped) ->
      { FromTool = fromTool
        ToTool = toTool
        Frequency = grouped.Length })
