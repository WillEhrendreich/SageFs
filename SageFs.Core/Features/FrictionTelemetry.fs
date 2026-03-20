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

type FeedbackSummary = {
  Tool: ToolName
  Kind: ExplicitFeedbackKind
  Count: int
  LatestReason: string
  LatestAlternative: string option
}

type ActionableToolReport = {
  Tool: ToolName
  TotalInvocations: int
  BlockedCount: int
  AbandonedCount: int
  ExplicitFeedbackCount: int
  MostCommonBlocker: BlockerKind option
  MostCommonFollowUp: ToolName option
  SuggestedFixTarget: string
}

type FrictionReport = {
  TotalEvents: int
  TotalFeedbackItems: int
  HighestPriorityTools: ActionableToolReport list
  TopBlockers: TopBlockerSummary list
  FrequentTransitions: TransitionSummary list
  RecentFeedback: FeedbackSummary list
}

module Summaries =
  let private blockerFromEvent event =
    match event.Outcome with
    | FrictionOutcome.EncounteredBlocker blocker -> Some blocker
    | _ -> None

  let private followedTool event =
    match event.FollowUp with
    | FollowUp.FollowedByTool tool -> Some tool
    | _ -> None

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

  let feedbackSummaries (feedback: ExplicitFeedback list) =
    feedback
    |> List.groupBy (fun item -> item.Tool, item.Kind)
    |> List.map (fun ((tool, kind), grouped) ->
      let latest = grouped |> List.maxBy (fun item -> item.OccurredAtUtc)
      let latestAlternative =
        match latest.AlternativeUsed with
        | AlternativePath.ResolvedWithTool tool -> Some (ToolName.value tool)
        | AlternativePath.ResolvedOutsideMcp -> Some "outside_mcp"
        | AlternativePath.NoAlternativeRecorded -> None
      { Tool = tool
        Kind = kind
        Count = grouped.Length
        LatestReason = latest.ShortReason
        LatestAlternative = latestAlternative })
    |> List.sortByDescending (fun item -> item.Count)

  let private suggestedFixTarget blocked abandoned explicitFeedback blocker followUp =
    match blocker, followUp, explicitFeedback > 0, blocked > 0, abandoned > 0 with
    | Some BlockerKind.ExactTestNotFound, Some tool, _, _, _ ->
      sprintf "Tighten exact-test workflow and point agents toward %s first." (ToolName.value tool)
    | Some BlockerKind.OutputTooLarge, _, _, _, _ ->
      "Reduce output volume or add a narrower report/query path."
    | Some blocker, _, _, true, _ ->
      sprintf "Remove recurring blocker %O from the tool's primary path." blocker
    | _, Some tool, true, _, _ ->
      sprintf "Merge or cross-link this workflow with %s so agents need fewer hops." (ToolName.value tool)
    | _, _, true, _, _ ->
      "Clarify the tool contract or improve its description so agents trust the result."
    | _, _, _, _, true ->
      "Investigate why agents abandon this tool before reaching a verified outcome."
    | _ ->
      "Keep watching this tool; no dominant remediation target yet."

  let actionableToolReports (events: FrictionEvent list) (feedback: ExplicitFeedback list) =
    let feedbackCounts =
      feedback
      |> List.groupBy (fun item -> item.Tool)
      |> List.map (fun (tool, items) -> tool, items.Length)
      |> Map.ofList

    events
    |> List.groupBy (fun event -> event.Tool)
    |> List.map (fun (tool, grouped) ->
      let blockedCount = grouped |> List.filter (fun event -> blockerFromEvent event |> Option.isSome) |> List.length
      let abandonedCount = grouped |> List.filter (fun event -> event.Outcome = FrictionOutcome.AbandonedWithoutResolution) |> List.length
      let explicitFeedbackCount = feedbackCounts |> Map.tryFind tool |> Option.defaultValue 0
      let mostCommonBlocker =
        grouped
        |> List.choose blockerFromEvent
        |> List.countBy id
        |> List.sortByDescending snd
        |> List.tryHead
        |> Option.map fst
      let mostCommonFollowUp =
        grouped
        |> List.choose followedTool
        |> List.countBy id
        |> List.sortByDescending snd
        |> List.tryHead
        |> Option.map fst
      { Tool = tool
        TotalInvocations = grouped.Length
        BlockedCount = blockedCount
        AbandonedCount = abandonedCount
        ExplicitFeedbackCount = explicitFeedbackCount
        MostCommonBlocker = mostCommonBlocker
        MostCommonFollowUp = mostCommonFollowUp
        SuggestedFixTarget = suggestedFixTarget blockedCount abandonedCount explicitFeedbackCount mostCommonBlocker mostCommonFollowUp })
    |> List.sortByDescending (fun item -> item.BlockedCount + item.AbandonedCount + item.ExplicitFeedbackCount)

  let frictionReport (events: FrictionEvent list) (feedback: ExplicitFeedback list) =
    { TotalEvents = events.Length
      TotalFeedbackItems = feedback.Length
      HighestPriorityTools = actionableToolReports events feedback |> List.truncate 5
      TopBlockers = topBlockers events |> List.sortByDescending (fun item -> item.Count) |> List.truncate 5
      FrequentTransitions = transitions events |> List.sortByDescending (fun item -> item.Frequency) |> List.truncate 5
      RecentFeedback = feedbackSummaries feedback |> List.truncate 5 }
