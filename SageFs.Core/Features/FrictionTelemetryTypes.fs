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
  /// Brief B9 (observed-friction-plan.md §B9) — the resolved, connection-bound
  /// routing identity (`McpTools.resolvedKey`/`currentTransportSessionId`),
  /// NOT the caller's self-declared `agentName`. Additive/back-compat: old
  /// rows (recorded before this column existed) decode with `""`, mirroring
  /// the `SageFsVersion` migration precedent — an empty AgentKey means
  /// "no transport session was bound when this event was recorded", never a
  /// parse failure.
  AgentKey: string
  /// Brief B9 — a short, PRE-SANITIZED (via `FrictionSanitize.sanitizeText`)
  /// summary of the failure that produced this event, for fine-grained
  /// RepeatedSameError/RetryLoop grouping beyond the coarse `BlockerKind`.
  /// NEVER raw user code, secrets, paths, emails, or session ids — those are
  /// scrubbed by the sanitizer before this field is ever populated. `""` for
  /// a clean completion and for every pre-B9 row (additive/back-compat,
  /// mirrors `SageFsVersion`).
  ErrorSignature: string
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

  /// How much of the source revision a person needs to tell two builds apart.
  let shortRevisionLength = 7

  /// The build's identity as a person reads it: the version, then the commit it
  /// was built from, in its short form (`0.6.892+b7ad4fd`). The SDK appends the
  /// full 40-character revision, which is too long for a statusline. A version
  /// with no revision is shown as it is.
  let display (informational: string) =
    match informational.IndexOf '+' with
    | -1 -> informational
    | cut ->
      let revision = informational.Substring(cut + 1)
      let shown =
        match revision.Length > shortRevisionLength with
        | true -> revision.Substring(0, shortRevisionLength)
        | false -> revision
      informational.Substring(0, cut) + "+" + shown

module FrictionEvent =
  let outcomeKind = function
    | { Outcome = FrictionOutcome.CompletedCleanly } -> OutcomeKind.Succeeded
    | { Outcome = FrictionOutcome.EncounteredBlocker _ } -> OutcomeKind.Blocked
    | { Outcome = FrictionOutcome.RecoveredVia ResolutionKind.SolvedWithRetry } -> OutcomeKind.Retried
    | { Outcome = FrictionOutcome.RecoveredVia _ } -> OutcomeKind.Escalated
    | { Outcome = FrictionOutcome.AbandonedWithoutResolution } -> OutcomeKind.Abandoned
