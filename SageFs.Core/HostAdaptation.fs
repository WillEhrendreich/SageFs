namespace SageFs

open System
open System.IO
open System.Reflection

/// Automatic version adaptation at host start.
///
/// The host's closure contains libraries that a project may pin differently.
/// This module computes the adaptation plan PURELY from the project bin's
/// assembly identities, deciding for each colliding name:
///
/// - API-compatible (FSharp.Core, FCS, SystemTextJson, Adaptive): the host
///   loads the PROJECT's pinned version (same runtime => same FSharp.Core;
///   the others are API-stable).
/// - API-coupled (Fantomas, Cecil, Harmony): select the version-matched
///   VARIANT (VariantSelector); none -> pre-eval refusal (fail-closed).
/// - Feature deps: not in the host at all (daemon-side) — never a collision.
///
/// Guarantee: there is no code path where project code silently runs against
/// the wrong version of a host library — it loads the project's version,
/// swaps in a variant built for it, or refuses before any eval.
module HostAdaptation =

  /// The API-coupled library families (host code calls their APIs directly).
  /// Everything else in the host closure is API-compatible by policy.
  let apiCoupledLibraries =
    [ "Fantomas"; "Mono.Cecil"; "HarmonyLib" ]

  /// The API-compatible library families (host loads the project's version).
  let apiCompatibleLibraries =
    [ "FSharp.Core"; "FSharp.Compiler.Service"; "FSharp.SystemTextJson"; "FSharp.Data.Adaptive" ]

  /// Read the assembly identities (simple name + version) from a bin dir.
  /// Best-effort: a DLL that fails to load as an assembly identity is skipped
  /// (it can't be a pin source if it isn't a managed assembly).
  let readAssemblyIdentities (binDir: string) : (string * string) list =
    try
      Directory.GetFiles(binDir, "*.dll")
      |> Array.choose (fun dll ->
        try
          let name = AssemblyName.GetAssemblyName dll
          Some(name.Name, name.Version.ToString())
        with _ -> None)
      |> Array.toList
    with _ -> []

  /// One adaptation decision for a colliding assembly.
  type Adaptation =
    /// Load the project's pinned version (API-compatible).
    | LoadProjectVersion of library: string * version: string
    /// Load the version-matched variant assembly (API-coupled).
    | LoadVariant of library: string * version: string * variantName: string
    /// Refuse pre-eval: no variant exists for this pin (fail-closed).
    | Refuse of reason: string
    /// No collision — the host's copy is fine (same version or absent).
    | NoConflict of library: string

  /// Compute the adaptation plan for a project bin against the host's closure.
  /// Pure: given the bin's identities, returns the decisions.
  let plan (binIdentities: (string * string) list) : Adaptation list =
    binIdentities
    |> List.map (fun (lib, ver) ->
      match List.contains lib apiCoupledLibraries with
      | true ->
        // API-coupled: must have a version-matched variant.
        match VariantSelector.select VariantSelector.selectVariantFromAssemblyIdentity lib ver with
        | Ok (VariantSelector.Variant variantName) -> LoadVariant(lib, ver, variantName)
        | Error reason -> Refuse reason
      | false when List.contains lib apiCompatibleLibraries ->
        LoadProjectVersion(lib, ver)
      | false ->
        // Not in the host's closure (feature dep or project-only) — no conflict.
        NoConflict lib)

  /// Does the plan contain any refusal? (fail-closed gate)
  let hasRefusal (plan: Adaptation list) : bool =
    plan |> List.exists (function Refuse _ -> true | _ -> false)

  /// The refusal reasons, for surfacing to the user.
  let refusalReasons (plan: Adaptation list) : string list =
    plan
    |> List.choose (function Refuse r -> Some r | _ -> None)

  /// The variant assemblies the plan requires (to load into the host).
  let requiredVariants (plan: Adaptation list) : string list =
    plan
    |> List.choose (function LoadVariant(_, _, v) -> Some v | _ -> None)
    |> List.distinct

  /// The project-pinned versions the host must load for API-compatible libs.
  let projectVersionsToLoad (plan: Adaptation list) : (string * string) list =
    plan
    |> List.choose (function LoadProjectVersion(l, v) -> Some(l, v) | _ -> None)
