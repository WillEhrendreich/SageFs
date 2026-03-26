module SageFs.Features.FrictionTelemetryTypes

open System

type ToolName = private ToolName of string

module ToolName =
  let create (text: string) =
    match String.IsNullOrWhiteSpace text with
    | true -> Error "Tool name cannot be empty."
    | false -> Ok (ToolName (text.Trim()))

  let value (ToolName text) = text

type SessionRef = private SessionRef of string

module SessionRef =
  let create (text: string) =
    match String.IsNullOrWhiteSpace text with
    | true -> Error "Session reference cannot be empty."
    | false -> Ok (SessionRef (text.Trim()))

  let value (SessionRef text) = text

type DurationMs = private DurationMs of int

module DurationMs =
  let create (value: int) =
    match value < 0 with
    | true -> Error "Duration cannot be negative."
    | false -> Ok (DurationMs value)

  let value (DurationMs value) = value

[<RequireQualifiedAccess>]
type IntentKind =
  | VerifyChangedBehavior
  | RunExactTest
  | ExploreCode
  | InspectFailure
  | RecoverSession
  | ManageSession

[<RequireQualifiedAccess>]
type OutcomeKind =
  | Succeeded
  | Blocked
  | Abandoned
  | Retried
  | Escalated

[<RequireQualifiedAccess>]
type BlockerKind =
  | SessionAmbiguous
  | SessionMissing
  | SessionWarming
  | LoadedStateUnknown
  | LoadedStateStale
  | TypeIdentityCompromised
  | ExactTestNotFound
  | OutputTooLarge
  | AffordanceMismatch
  | TransportFailure
  | OperationFailed
  | InvalidRequest

[<RequireQualifiedAccess>]
type ResolutionKind =
  | SolvedWithRetry
  | SolvedWithDifferentTool of ToolName
  | SolvedAfterReset
  | SolvedAfterSessionSwitch
  | Unresolved

[<RequireQualifiedAccess>]
type FollowUp =
  | NoFollowUpYet
  | FollowedByTool of ToolName
  | SessionEnded

[<RequireQualifiedAccess>]
type ContextCost =
  | Tiny
  | Focused
  | Heavy

[<RequireQualifiedAccess>]
type FrictionOutcome =
  | CompletedCleanly
  | EncounteredBlocker of BlockerKind
  | RecoveredVia of ResolutionKind
  | AbandonedWithoutResolution

type FrictionEvent = {
  OccurredAtUtc: DateTimeOffset
  Session: SessionRef
  Tool: ToolName
  Intent: IntentKind
  Outcome: FrictionOutcome
  Duration: DurationMs
  FollowUp: FollowUp
  ContextCost: ContextCost
  SageFsVersion: string
}

[<RequireQualifiedAccess>]
type ExplicitFeedbackKind =
  | ToolOutputWasTooLarge
  | ToolIntentWasUnclear
  | ToolNameWasMisleading
  | NeededAnotherToolToFinish
  | ResultDidNotEstablishTrust

[<RequireQualifiedAccess>]
type AlternativePath =
  | NoAlternativeRecorded
  | ResolvedWithTool of ToolName
  | ResolvedOutsideMcp

type ExplicitFeedback = {
  OccurredAtUtc: DateTimeOffset
  Session: SessionRef
  Tool: ToolName
  Kind: ExplicitFeedbackKind
  ShortReason: string
  AlternativeUsed: AlternativePath
  SageFsVersion: string
}

module SageFsVersion =
  /// Read the InformationalVersion from the assembly containing FrictionEvent.
  /// Falls back to AssemblyVersion, then "unknown".
  let current () =
    let asm = typeof<FrictionEvent>.Assembly
    asm.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
    |> Array.tryHead
    |> Option.map (fun a -> (a :?> System.Reflection.AssemblyInformationalVersionAttribute).InformationalVersion)
    |> Option.defaultWith (fun () ->
      match asm.GetName().Version with
      | null -> "unknown"
      | v -> string v)

module FrictionEvent =
  let outcomeKind = function
    | { Outcome = FrictionOutcome.CompletedCleanly } -> OutcomeKind.Succeeded
    | { Outcome = FrictionOutcome.EncounteredBlocker _ } -> OutcomeKind.Blocked
    | { Outcome = FrictionOutcome.RecoveredVia ResolutionKind.SolvedWithRetry } -> OutcomeKind.Retried
    | { Outcome = FrictionOutcome.RecoveredVia _ } -> OutcomeKind.Escalated
    | { Outcome = FrictionOutcome.AbandonedWithoutResolution } -> OutcomeKind.Abandoned
