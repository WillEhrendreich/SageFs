/// Hands a delta to the runtime, and says what the runtime made of it.
///
/// This is the shape of the answer: the closed outcomes and the capability check. Asking the runtime is not
/// written yet: the process is reported unable, and an apply is rejected.
namespace SageFs.Features.MetadataDelta

open System.Reflection

[<RequireQualifiedAccess>]
type UpdateSupport =
  | Supported
  | Unsupported

[<RequireQualifiedAccess>]
type ModifiableEnvironment =
  /// `DOTNET_MODIFIABLE_ASSEMBLIES=debug`.
  | Debug
  | Unset
  | OtherValue of value: string

[<RequireQualifiedAccess>]
type DebuggerState =
  | Detached
  | Attached

/// What this process can do for a delta, read once.
type RuntimeCapability =
  { Support: UpdateSupport
    Environment: ModifiableEnvironment
    Debugger: DebuggerState
    /// The runtime's capability names (`MetadataUpdater.GetCapabilities()`). Empty when they could not be read.
    Features: string list }

/// A reason a delta should not be handed to the runtime.
[<RequireQualifiedAccess>]
type CapabilityGap =
  | RuntimeUpdateUnsupported
  | EnvironmentNotModifiable of current: string
  | DebuggerAttached
  | MissingFeature of RequiredFeature
  /// The module was built with optimizations, so the runtime will not edit it.
  | ModuleOptimized of assembly: string
  /// A reflection-emit module, which is what FSI's own assemblies are.
  | ModuleIsDynamic of assembly: string

[<RequireQualifiedAccess>]
type CapabilityCheck =
  | Capable
  | Incapable of CapabilityGap list

/// What the runtime did with an apply.
[<RequireQualifiedAccess>]
type ApplyOutcome =
  | Applied
  /// The runtime took the delta, and a metadata-update handler threw while telling the process. The code is
  /// patched; some cache may still describe the old one.
  | AppliedHandlersFailed of failures: string list
  /// The runtime said the assembly cannot be edited (no environment variable, or an optimized module).
  | RuntimeNotModifiable of reason: string
  | DebuggerAttached
  /// The runtime refused for a reason of its own, carried as it said it.
  | NotSupported of reason: string
  /// The delta was malformed, or something else threw.
  | Rejected of reason: string

[<RequireQualifiedAccess>]
module DeltaApply =

  /// Read what this process can do.
  let capability () : RuntimeCapability =
    { Support = UpdateSupport.Unsupported
      Environment = ModifiableEnvironment.Unset
      Debugger = DebuggerState.Detached
      Features = [] }

  let describeGap (gap: CapabilityGap) : string = sprintf "%A" gap

  /// Everything known before the call. A delta is handed to the runtime only when this says `Capable`.
  let check (capability: RuntimeCapability) (assembly: Assembly) (payload: DeltaPayload) : CapabilityCheck =
    ignore (capability, assembly, payload)
    CapabilityCheck.Incapable [ CapabilityGap.RuntimeUpdateUnsupported ]

  /// Hand the delta to the runtime. Irreversible when it answers `Applied`.
  let apply (assembly: Assembly) (payload: DeltaPayload) : ApplyOutcome =
    ignore (assembly, payload)
    ApplyOutcome.Rejected "not written yet"
