module SageFs.Features.McpFrictionRecorder

open System
open System.Text.Json
open System.Threading.Tasks
open SageFs.EventStore
open SageFs.Features.FrictionTelemetryTypes
open SageFs.Features.FrictionTelemetry

type FrictionEnvelope = {
  Events: FrictionEvent list
  Feedback: ExplicitFeedback list
}

type private StoredEvent = {
  OccurredAtUtc: DateTimeOffset
  SessionId: string
  ToolName: string
  IntentKind: string
  OutcomeKind: string
  BlockerKind: string option
  ResolutionKind: string option
  ResolutionToolName: string option
  DurationMs: int
  FollowUpKind: string
  FollowUpToolName: string option
  ContextCostKind: string
}

type private StoredFeedback = {
  OccurredAtUtc: DateTimeOffset
  SessionId: string
  ToolName: string
  FeedbackKind: string
  ShortReason: string
  AlternativeKind: string
  AlternativeToolName: string option
}

type private StoredEnvelope = {
  Events: StoredEvent list
  Feedback: StoredFeedback list
}

module private Codec =
  let options = JsonSerializerOptions(WriteIndented = false)

  let empty : FrictionEnvelope = { Events = []; Feedback = [] }

  let private outcomeParts outcome =
    match outcome with
    | FrictionOutcome.CompletedCleanly -> "CompletedCleanly", None, None, None
    | FrictionOutcome.EncounteredBlocker blocker -> "EncounteredBlocker", Some (string blocker), None, None
    | FrictionOutcome.RecoveredVia ResolutionKind.SolvedWithRetry -> "RecoveredVia", None, Some "SolvedWithRetry", None
    | FrictionOutcome.RecoveredVia (ResolutionKind.SolvedWithDifferentTool tool) -> "RecoveredVia", None, Some "SolvedWithDifferentTool", Some (ToolName.value tool)
    | FrictionOutcome.RecoveredVia ResolutionKind.SolvedAfterReset -> "RecoveredVia", None, Some "SolvedAfterReset", None
    | FrictionOutcome.RecoveredVia ResolutionKind.SolvedAfterSessionSwitch -> "RecoveredVia", None, Some "SolvedAfterSessionSwitch", None
    | FrictionOutcome.RecoveredVia ResolutionKind.Unresolved -> "RecoveredVia", None, Some "Unresolved", None
    | FrictionOutcome.AbandonedWithoutResolution -> "AbandonedWithoutResolution", None, None, None

  let private followUpParts followUp =
    match followUp with
    | FollowUp.NoFollowUpYet -> "NoFollowUpYet", None
    | FollowUp.FollowedByTool tool -> "FollowedByTool", Some (ToolName.value tool)
    | FollowUp.SessionEnded -> "SessionEnded", None

  let private contextCostText = function
    | ContextCost.Tiny -> "Tiny"
    | ContextCost.Focused -> "Focused"
    | ContextCost.Heavy -> "Heavy"

  let private explicitFeedbackKindText = function
    | ExplicitFeedbackKind.ToolOutputWasTooLarge -> "ToolOutputWasTooLarge"
    | ExplicitFeedbackKind.ToolIntentWasUnclear -> "ToolIntentWasUnclear"
    | ExplicitFeedbackKind.ToolNameWasMisleading -> "ToolNameWasMisleading"
    | ExplicitFeedbackKind.NeededAnotherToolToFinish -> "NeededAnotherToolToFinish"
    | ExplicitFeedbackKind.ResultDidNotEstablishTrust -> "ResultDidNotEstablishTrust"

  let private alternativeParts = function
    | AlternativePath.NoAlternativeRecorded -> "NoAlternativeRecorded", None
    | AlternativePath.ResolvedWithTool tool -> "ResolvedWithTool", Some (ToolName.value tool)
    | AlternativePath.ResolvedOutsideMcp -> "ResolvedOutsideMcp", None

  let private parseIntent = function
    | "VerifyChangedBehavior" -> Ok IntentKind.VerifyChangedBehavior
    | "RunExactTest" -> Ok IntentKind.RunExactTest
    | "ExploreCode" -> Ok IntentKind.ExploreCode
    | "InspectFailure" -> Ok IntentKind.InspectFailure
    | "RecoverSession" -> Ok IntentKind.RecoverSession
    | "ManageSession" -> Ok IntentKind.ManageSession
    | other -> Error (sprintf "Unknown intent kind '%s'." other)

  let private parseBlocker = function
    | "SessionAmbiguous" -> Ok BlockerKind.SessionAmbiguous
    | "SessionMissing" -> Ok BlockerKind.SessionMissing
    | "SessionWarming" -> Ok BlockerKind.SessionWarming
    | "LoadedStateUnknown" -> Ok BlockerKind.LoadedStateUnknown
    | "LoadedStateStale" -> Ok BlockerKind.LoadedStateStale
    | "TypeIdentityCompromised" -> Ok BlockerKind.TypeIdentityCompromised
    | "ExactTestNotFound" -> Ok BlockerKind.ExactTestNotFound
    | "OutputTooLarge" -> Ok BlockerKind.OutputTooLarge
    | "AffordanceMismatch" -> Ok BlockerKind.AffordanceMismatch
    | "InvalidRequest" -> Ok BlockerKind.InvalidRequest
    | other -> Error (sprintf "Unknown blocker kind '%s'." other)

  let private parseResolution resolutionKind resolutionTool =
    match resolutionKind, resolutionTool with
    | Some "SolvedWithRetry", _ -> Ok ResolutionKind.SolvedWithRetry
    | Some "SolvedWithDifferentTool", Some tool -> ToolName.create tool |> Result.map ResolutionKind.SolvedWithDifferentTool
    | Some "SolvedAfterReset", _ -> Ok ResolutionKind.SolvedAfterReset
    | Some "SolvedAfterSessionSwitch", _ -> Ok ResolutionKind.SolvedAfterSessionSwitch
    | Some "Unresolved", _ -> Ok ResolutionKind.Unresolved
    | None, _ -> Error "Missing resolution kind."
    | Some other, _ -> Error (sprintf "Unknown resolution kind '%s'." other)

  let private parseOutcome outcomeKind blockerKind resolutionKind resolutionTool =
    match outcomeKind with
    | "CompletedCleanly" -> Ok FrictionOutcome.CompletedCleanly
    | "EncounteredBlocker" ->
      match blockerKind with
      | Some blocker -> parseBlocker blocker |> Result.map FrictionOutcome.EncounteredBlocker
      | None -> Error "Blocked outcome missing blocker kind."
    | "RecoveredVia" ->
      parseResolution resolutionKind resolutionTool |> Result.map FrictionOutcome.RecoveredVia
    | "AbandonedWithoutResolution" -> Ok FrictionOutcome.AbandonedWithoutResolution
    | other -> Error (sprintf "Unknown outcome kind '%s'." other)

  let private parseFollowUp followUpKind followUpTool =
    match followUpKind with
    | "NoFollowUpYet" -> Ok FollowUp.NoFollowUpYet
    | "FollowedByTool" ->
      match followUpTool with
      | Some tool -> ToolName.create tool |> Result.map FollowUp.FollowedByTool
      | None -> Error "Follow-up tool missing tool name."
    | "SessionEnded" -> Ok FollowUp.SessionEnded
    | other -> Error (sprintf "Unknown follow-up kind '%s'." other)

  let private parseContextCost = function
    | "Tiny" -> Ok ContextCost.Tiny
    | "Focused" -> Ok ContextCost.Focused
    | "Heavy" -> Ok ContextCost.Heavy
    | other -> Error (sprintf "Unknown context cost '%s'." other)

  let private parseFeedbackKind = function
    | "ToolOutputWasTooLarge" -> Ok ExplicitFeedbackKind.ToolOutputWasTooLarge
    | "ToolIntentWasUnclear" -> Ok ExplicitFeedbackKind.ToolIntentWasUnclear
    | "ToolNameWasMisleading" -> Ok ExplicitFeedbackKind.ToolNameWasMisleading
    | "NeededAnotherToolToFinish" -> Ok ExplicitFeedbackKind.NeededAnotherToolToFinish
    | "ResultDidNotEstablishTrust" -> Ok ExplicitFeedbackKind.ResultDidNotEstablishTrust
    | other -> Error (sprintf "Unknown explicit feedback kind '%s'." other)

  let private parseAlternative kind toolName =
    match kind with
    | "NoAlternativeRecorded" -> Ok AlternativePath.NoAlternativeRecorded
    | "ResolvedWithTool" ->
      match toolName with
      | Some tool -> ToolName.create tool |> Result.map AlternativePath.ResolvedWithTool
      | None -> Error "Alternative tool missing name."
    | "ResolvedOutsideMcp" -> Ok AlternativePath.ResolvedOutsideMcp
    | other -> Error (sprintf "Unknown alternative kind '%s'." other)

  let private encodeEvent (event: FrictionEvent) : StoredEvent =
    let outcomeKind, blockerKind, resolutionKind, resolutionTool = outcomeParts event.Outcome
    let followUpKind, followUpTool = followUpParts event.FollowUp
    { OccurredAtUtc = event.OccurredAtUtc
      SessionId = SessionRef.value event.Session
      ToolName = ToolName.value event.Tool
      IntentKind = string event.Intent
      OutcomeKind = outcomeKind
      BlockerKind = blockerKind
      ResolutionKind = resolutionKind
      ResolutionToolName = resolutionTool
      DurationMs = DurationMs.value event.Duration
      FollowUpKind = followUpKind
      FollowUpToolName = followUpTool
      ContextCostKind = contextCostText event.ContextCost }

  let private decodeEvent (event: StoredEvent) : Result<FrictionEvent, string> =
    match SessionRef.create event.SessionId with
    | Error err -> Error err
    | Ok session ->
      match ToolName.create event.ToolName with
      | Error err -> Error err
      | Ok tool ->
        match parseIntent event.IntentKind with
        | Error err -> Error err
        | Ok intent ->
          match parseOutcome event.OutcomeKind event.BlockerKind event.ResolutionKind event.ResolutionToolName with
          | Error err -> Error err
          | Ok outcome ->
            match DurationMs.create event.DurationMs with
            | Error err -> Error err
            | Ok duration ->
              match parseFollowUp event.FollowUpKind event.FollowUpToolName with
              | Error err -> Error err
              | Ok followUp ->
                match parseContextCost event.ContextCostKind with
                | Error err -> Error err
                | Ok contextCost ->
                  Ok
                    { OccurredAtUtc = event.OccurredAtUtc
                      Session = session
                      Tool = tool
                      Intent = intent
                      Outcome = outcome
                      Duration = duration
                      FollowUp = followUp
                      ContextCost = contextCost }

  let private encodeFeedback (feedback: ExplicitFeedback) : StoredFeedback =
    let alternativeKind, alternativeTool = alternativeParts feedback.AlternativeUsed
    { OccurredAtUtc = feedback.OccurredAtUtc
      SessionId = SessionRef.value feedback.Session
      ToolName = ToolName.value feedback.Tool
      FeedbackKind = explicitFeedbackKindText feedback.Kind
      ShortReason = feedback.ShortReason
      AlternativeKind = alternativeKind
      AlternativeToolName = alternativeTool }

  let private decodeFeedback (feedback: StoredFeedback) : Result<ExplicitFeedback, string> =
    match SessionRef.create feedback.SessionId with
    | Error err -> Error err
    | Ok session ->
      match ToolName.create feedback.ToolName with
      | Error err -> Error err
      | Ok tool ->
        match parseFeedbackKind feedback.FeedbackKind with
        | Error err -> Error err
        | Ok kind ->
          match parseAlternative feedback.AlternativeKind feedback.AlternativeToolName with
          | Error err -> Error err
          | Ok alternative ->
            Ok
              { OccurredAtUtc = feedback.OccurredAtUtc
                Session = session
                Tool = tool
                Kind = kind
                ShortReason = feedback.ShortReason
                AlternativeUsed = alternative }

  let deserialize (text: string) =
    match String.IsNullOrWhiteSpace text with
    | true -> empty
    | false ->
      let stored = JsonSerializer.Deserialize<StoredEnvelope>(text, options)
      let events =
        stored.Events
        |> List.map decodeEvent
        |> List.choose (function | Ok value -> Some value | Error _ -> None)
      let feedback =
        stored.Feedback
        |> List.map decodeFeedback
        |> List.choose (function | Ok value -> Some value | Error _ -> None)
      { Events = events; Feedback = feedback }

  let serialize (value: FrictionEnvelope) =
    let stored : StoredEnvelope =
      { Events = value.Events |> List.map encodeEvent
        Feedback = value.Feedback |> List.map encodeFeedback }
    JsonSerializer.Serialize(stored, options)

module Recorder =
  [<Literal>]
  let private StoreKey = "mcp-friction"

  let appendEvent (persistence: EventPersistence) (event: FrictionEvent) =
    task {
      let! existing = persistence.GetValue StoreKey
      let envelope = existing |> Option.map Codec.deserialize |> Option.defaultValue Codec.empty
      let updated = { envelope with Events = envelope.Events @ [ event ] }
      return! persistence.SetValue StoreKey (Codec.serialize updated)
    }

  let appendFeedback (persistence: EventPersistence) (feedback: ExplicitFeedback) =
    task {
      let! existing = persistence.GetValue StoreKey
      let envelope = existing |> Option.map Codec.deserialize |> Option.defaultValue Codec.empty
      let updated = { envelope with Feedback = envelope.Feedback @ [ feedback ] }
      return! persistence.SetValue StoreKey (Codec.serialize updated)
    }

  let readEnvelope (persistence: EventPersistence) =
    task {
      let! existing = persistence.GetValue StoreKey
      return existing |> Option.map Codec.deserialize |> Option.defaultValue Codec.empty
    }

  let summarize (persistence: EventPersistence) =
    task {
      let! envelope = readEnvelope persistence
      return
        [ yield sprintf "Top blockers: %d" (Summaries.topBlockers envelope.Events |> List.length)
          yield sprintf "Tracked tools: %d" (Summaries.toolSummaries envelope.Events |> List.length)
          yield sprintf "Explicit feedback items: %d" envelope.Feedback.Length ]
        |> String.concat "\n"
    }
