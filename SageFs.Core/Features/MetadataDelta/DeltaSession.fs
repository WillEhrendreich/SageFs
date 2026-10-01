/// What a process's chain of deltas is good for, and what a save does with it.
///
/// A delta is computed against the module the process loaded and every delta after it against the one
/// before, so a chain is only worth anything for the process it started in. Three things can make it worth
/// nothing: the process is not the one the chain was started for, a save was prepared from a generation
/// that is no longer the process's, and a delta the runtime failed on (which may have changed some of the
/// process and not the rest). This module is the whole decision about all three, pure, so the worker calls
/// it and the simulation folds it through every order the events can arrive in.
///
/// It decides; it does not do. The chain (`DeltaChain`), the runtime call (`DeltaApply`) and the build are
/// the caller's, and what they answered comes back in as a value.
namespace SageFs.Features.MetadataDelta

open System

/// The standing of one process's delta route.
[<RequireQualifiedAccess>]
type Standing =
  /// Nothing has been captured yet.
  | NoBaseline
  /// Deltas can be applied: the chain began at the module with this `Mvid` and `generation` of them have landed.
  | Tracking of baseline: Guid * generation: int
  /// The route cannot be used again in this process, and why. A restart is what clears it.
  | Unusable of why: string

/// What preparing a save came to, without the delta itself.
[<RequireQualifiedAccess>]
type Preparation =
  /// The build is the one the process already runs.
  | NothingChanged
  /// The emitter cannot carry this edit.
  | Refused of causes: RudeCause list
  /// A delta is ready, prepared from this generation of the chain.
  | Ready of from: int

/// Why a save restarts instead of patching.
[<RequireQualifiedAccess>]
type Refusal =
  /// The edit is one a delta cannot carry.
  | Rude of RudeCause
  /// The route cannot be used right now, and the edit is not why.
  | Unavailable of why: string

/// What the save does.
[<RequireQualifiedAccess>]
type SaveDecision =
  /// Nothing differs from what the process runs.
  | Unchanged
  /// Hand the delta to the runtime.
  | Apply
  /// Restart the app, for these reasons (at least one).
  | Restart of first: Refusal * rest: Refusal list

/// What the runtime did with the delta, as far as the chain is concerned.
[<RequireQualifiedAccess>]
type Landing =
  /// The runtime took it: the chain advances.
  | Landed
  /// The runtime took it and a metadata-update handler threw. The code is patched, so the chain advances too.
  | LandedWithHandlerFailures of failures: string list
  /// The runtime did not take it, or its answer cannot be trusted.
  | DidNotLand of why: string

[<RequireQualifiedAccess>]
module DeltaSession =

  /// What a process starts with: the module it loaded, and whatever stops the runtime editing it.
  let start (loaded: Guid) (gaps: CapabilityGap list) : Standing =
    match gaps with
    | [] -> Standing.Tracking(loaded, 0)
    | _ -> Standing.Unusable(gaps |> List.map DeltaApply.describeGap |> String.concat "; ")

  /// Whether a save can be a delta at all, asked before the project is built: `running` is the module's `Mvid` as
  /// the process reports it NOW, never the one remembered, and `gaps` is what stands between the process and an
  /// edit now (a debugger can attach after the app started). A standing that is not `Tracking` refuses every
  /// save, a no-change one included: the process may hold part of a delta.
  let precheck (standing: Standing) (running: Guid) (gaps: CapabilityGap list) : Result<int, Refusal> =
    match standing with
    | Standing.NoBaseline -> Result.Error(Refusal.Unavailable "no baseline was captured when the app started")
    | Standing.Unusable why -> Result.Error(Refusal.Unavailable why)
    | Standing.Tracking(baseline, _) when baseline <> running ->
      Result.Error(Refusal.Unavailable "the module the chain was started from is not the one this process is running")
    | Standing.Tracking(_, generation) ->
      match gaps with
      | [] -> Result.Ok generation
      | _ -> Result.Error(Refusal.Unavailable(gaps |> List.map DeltaApply.describeGap |> String.concat "; "))

  /// What a save does, given the precheck's inputs and what preparing the build came to.
  let decide (standing: Standing) (running: Guid) (gaps: CapabilityGap list) (preparation: Preparation) : SaveDecision =
    match precheck standing running gaps with
    | Result.Error refusal -> SaveDecision.Restart(refusal, [])
    | Result.Ok generation ->
      match preparation with
      | Preparation.NothingChanged -> SaveDecision.Unchanged
      | Preparation.Refused(first :: rest) -> SaveDecision.Restart(Refusal.Rude first, rest |> List.map Refusal.Rude)
      | Preparation.Refused [] ->
        SaveDecision.Restart(Refusal.Unavailable "the emitter refused the edit and named no cause", [])
      | Preparation.Ready from when from = generation -> SaveDecision.Apply
      | Preparation.Ready from ->
        SaveDecision.Restart(Refusal.Unavailable(sprintf "the delta was prepared from generation %d and the process is at %d" from generation), [])

  /// The standing after the runtime answered. The chain advances exactly when the code changed; a delta the
  /// runtime did not take leaves the route unusable, because what the process holds is no longer known.
  let settle (standing: Standing) (landing: Landing) : Standing =
    match standing, landing with
    | Standing.Tracking(baseline, generation), (Landing.Landed | Landing.LandedWithHandlerFailures _) ->
      Standing.Tracking(baseline, generation + 1)
    | _, Landing.DidNotLand why -> Standing.Unusable why
    | other, (Landing.Landed | Landing.LandedWithHandlerFailures _) -> other

  /// How the runtime's answer reads to the chain.
  let landingOf (outcome: ApplyOutcome) : Landing =
    match outcome with
    | ApplyOutcome.Applied -> Landing.Landed
    | ApplyOutcome.AppliedHandlersFailed failures -> Landing.LandedWithHandlerFailures failures
    | ApplyOutcome.RuntimeNotModifiable reason -> Landing.DidNotLand(sprintf "the runtime will not edit this assembly: %s" reason)
    | ApplyOutcome.DebuggerAttached -> Landing.DidNotLand "a debugger is attached, and the runtime does not apply an update under one"
    | ApplyOutcome.NotSupported reason -> Landing.DidNotLand(sprintf "the runtime does not support this update: %s" reason)
    | ApplyOutcome.Rejected reason -> Landing.DidNotLand(sprintf "the runtime rejected the delta: %s" reason)

  /// The planner's words for a refusal.
  let describeRefusal (refusal: Refusal) : string =
    match refusal with
    | Refusal.Rude cause -> RudeCause.describe cause
    | Refusal.Unavailable why -> why
