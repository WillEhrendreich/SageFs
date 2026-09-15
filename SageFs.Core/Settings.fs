namespace SageFs

open System

/// The unified runtime settings core (unified-settings-design.md, Phase A).
///
/// The governing rule is requirement #4: **illegal configuration is
/// unrepresentable**. A setting's value is a type whose only inhabitants are
/// legal — a bounded `Port`, a closed-set `EnumValue`, the reused
/// `ValidTimeout` (Timeouts.fs) and `LoopbackHost` (SageFsConfig.fs) — so no
/// code path downstream of `parse` ever holds a value it must re-check. The
/// only place a raw string becomes a typed value is a `parse` that returns
/// *why* on failure; `resolve`/`apply` operate on values that are legal by
/// construction.

/// A TCP port a user may bind. Only the unprivileged range is constructable,
/// so a `Port` can never name a privileged (<1024) or out-of-range port.
type Port = private Port of int

[<RequireQualifiedAccess>]
module Port =
  let create (n: int) : Result<Port, string> =
    match n >= 1024 && n <= 65535 with
    | true -> Ok (Port n)
    | false -> Error (sprintf "port must be between 1024 and 65535, got %d" n)

  let value (Port n) = n

/// A member of a closed set of strings (a theme name, a workflow, a run
/// policy). The chosen value is provably one of `allowed` — there is no way
/// to construct an `EnumValue` outside its own set. The descriptor supplies
/// the allowed set, so this one type serves every closed-string setting
/// without the core needing to know the specific sets.
type EnumValue = private EnumValue of allowed: string list * chosen: string

[<RequireQualifiedAccess>]
module EnumValue =
  let create (allowed: string list) (raw: string) : Result<EnumValue, string> =
    match List.contains raw allowed with
    | true -> Ok (EnumValue(allowed, raw))
    | false -> Error (sprintf "'%s' is not one of [%s]" raw (String.concat "; " allowed))

  let value (EnumValue(_, chosen)) = chosen
  let allowed (EnumValue(a, _)) = a

/// The unified value of any setting. Every case's payload is already a type
/// whose only inhabitants are legal, so a `SettingValue` cannot carry an
/// illegal configuration.
type SettingValue =
  | VBool of bool
  | VPort of Port
  | VTimeout of ValidTimeout
  | VBindHost of SageFsConfig.LoopbackHost
  | VEnum of EnumValue

/// The layers a value can come from, lowest precedence first. A higher layer
/// overrides a lower one; `LSession` is a transient in-memory override, the
/// two file layers persist, and `LDefault` is the descriptor's built-in.
type ConfigLayer =
  | LDefault
  | LGlobal
  | LRepo
  | LSession

[<RequireQualifiedAccess>]
module ConfigLayer =
  /// Higher rank wins. Total and injective over the four cases.
  let rank (layer: ConfigLayer) : int =
    match layer with
    | LDefault -> 0
    | LGlobal -> 1
    | LRepo -> 2
    | LSession -> 3

/// The resolved value plus its provenance: which layer supplied the effective
/// value, and the value present at every layer that set one (so the UI can
/// show base-vs-override explicitly — requirement #2).
type Provenance = {
  Effective: SettingValue
  Source: ConfigLayer
  PerLayer: (ConfigLayer * SettingValue) list
}

/// Where a setting may be set.
type SettingScope =
  /// Machine-wide only (never a per-repo override).
  | Global
  /// A global base that a repo may override.
  | RepoOverridable
  /// A transient per-session override that is never persisted to a file.
  | SessionOnly

/// When a change takes effect.
type SettingApplicability =
  /// Hot-swaps immediately via the descriptor's `apply`.
  | Live
  /// Persisted now, applied on the next restart.
  | RestartRequired
  /// Editable only through an explicit, explained confirm (e.g. the bind host
  /// RCE guard).
  | Guarded

/// The authority for one setting: how to parse/render its raw form, its
/// default (already a legal `SettingValue`), and how to apply it live. One
/// descriptor drives resolution, the editor, and (later) the UI row.
type SettingDescriptor = {
  Key: string
  Description: string
  Scope: SettingScope
  Applicability: SettingApplicability
  Default: SettingValue
  /// The ONLY boundary where a raw string becomes a typed value; returns why
  /// on failure. Nothing downstream re-validates.
  Parse: string -> Result<SettingValue, string>
  /// The inverse of `parse` for persistence.
  Render: SettingValue -> string
  /// Push an already-legal value into the live subsystem.
  Apply: SettingValue -> unit
}

[<RequireQualifiedAccess>]
module SettingsResolver =
  /// Pure: resolve the effective value across layers. `defaultValue` is always
  /// the `LDefault` layer; `set` carries the values explicitly present at
  /// higher layers (in any order). The highest-ranked layer wins; `PerLayer`
  /// records every layer that had a value, lowest-first, for the provenance UI.
  let resolve (defaultValue: SettingValue) (set: (ConfigLayer * SettingValue) list) : Provenance =
    let all = (LDefault, defaultValue) :: set
    let source, effective = all |> List.maxBy (fun (layer, _) -> ConfigLayer.rank layer)
    { Effective = effective
      Source = source
      PerLayer = all |> List.sortBy (fun (layer, _) -> ConfigLayer.rank layer) }
