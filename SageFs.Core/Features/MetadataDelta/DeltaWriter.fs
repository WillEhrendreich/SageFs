/// Writes the metadata delta that takes a running assembly from one build to the next.
///
/// This is the shape of the answer: a chain of deltas applied to one baseline, and a payload per delta.
/// The writing itself is not done yet: `Prepare` says nothing changed.
namespace SageFs.Features.MetadataDelta

open System

/// What the runtime has to report before it can take a delta. The names are the runtime's own capability
/// strings (`MetadataUpdater.GetCapabilities()`).
[<RequireQualifiedAccess>]
type RequiredFeature =
  | Baseline
  | AddMethodToExistingType
  | GenericUpdateMethod

[<RequireQualifiedAccess>]
module RequiredFeature =
  let capabilityName (feature: RequiredFeature) : string =
    match feature with
    | RequiredFeature.Baseline -> "Baseline"
    | RequiredFeature.AddMethodToExistingType -> "AddMethodToExistingType"
    | RequiredFeature.GenericUpdateMethod -> "GenericUpdateMethod"

/// The bytes of one delta and what it carries.
type DeltaPayload =
  { Generation: int
    Metadata: byte array
    Il: byte array
    Updated: MethodId list
    AddedMethods: MethodId list
    Requires: RequiredFeature list }

/// A delta ready to apply.
[<Sealed>]
type PreparedDelta internal (payload: DeltaPayload) =
  member _.Payload : DeltaPayload = payload

/// What preparing a delta came to.
[<RequireQualifiedAccess>]
type PrepareOutcome =
  /// Every method is the same as the build the process already runs.
  | NothingChanged
  | Ready of PreparedDelta
  | Refused of RudeCause list

/// The chain of deltas applied to one baseline assembly.
[<Sealed>]
type DeltaChain private (generation: int) =

  /// Start the chain at a baseline: the assembly the process loaded.
  static member Start(baseline: PeImage, probes: ProbeStripping) : DeltaChain =
    ignore (baseline, probes)
    DeltaChain 0

  /// How many deltas have been committed.
  member _.Generation : int = generation

  /// Compare `next` with what the process runs, and write the delta that takes it there.
  member this.Prepare(encId: Guid, next: PeImage) : PrepareOutcome =
    this.PrepareFrom(encId, next, { Methods = []; Refusals = [] })

  /// The writing half of `Prepare`, for a diff the caller already has.
  member internal _.PrepareFrom(encId: Guid, next: PeImage, diff: ImageDiff) : PrepareOutcome =
    ignore (encId, next, diff)
    PrepareOutcome.NothingChanged

  /// The chain after a prepared delta applied.
  member _.Commit(prepared: PreparedDelta) : DeltaChain =
    ignore prepared
    DeltaChain(generation + 1)
