namespace SageFs

open System

/// The unified runtime settings core (unified-settings-design.md, Phase A).
///
/// The governing rule is requirement #4: **illegal configuration is
/// unrepresentable**. A setting's value is a type whose only inhabitants are
/// legal — a bounded `Port`, a closed-set `EnumValue`, the reused
/// `ValidTimeout` (Timeouts.fs) and `LoopbackHost` (SageFsConfig.fs) — so no
/// code path downstream of `parse` ever holds a value it must re-check. The
/// only place a raw string becomes a typed value is a `parse` that returns a
/// typed `ConfigError` (never a bare string — the repo's error-algebra
/// discipline) explaining *why* on failure.

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

  let label (layer: ConfigLayer) : string =
    match layer with
    | LDefault -> "default"
    | LGlobal -> "global"
    | LRepo -> "repo"
    | LSession -> "session"

/// Why a setting operation failed — a typed error, never a bare string, so
/// consumers can dispatch on the cause and a boundary can map it to the
/// repo's SageFsError algebra. Every case carries the detail a user needs.
type ConfigError =
  /// A numeric value outside its allowed range (port, timeout).
  | OutOfRange of detail: string
  /// A value that is not a member of a closed set (an enum choice).
  | NotAMember of got: string * allowed: string list
  /// Input that could not be parsed into the value type at all.
  | Malformed of detail: string
  /// A bind host that is not a loopback address — the RCE guard.
  | NotLoopback of detail: string
  /// The chosen layer cannot be written (no repo checkout, or a
  /// non-persisted layer such as default/session).
  | LayerUnavailable of detail: string
  /// Persisting the value to its layer file failed.
  | PersistFailed of detail: string
  /// A value already persisted at a layer failed to parse back into its type.
  | LayerValueInvalid of key: string * layer: ConfigLayer * detail: string

[<RequireQualifiedAccess>]
module ConfigError =
  /// A single-line, user-facing description.
  let describe (e: ConfigError) : string =
    match e with
    | OutOfRange detail -> detail
    | NotAMember(got, allowed) -> sprintf "'%s' is not one of [%s]" got (String.concat "; " allowed)
    | Malformed detail -> detail
    | NotLoopback detail -> detail
    | LayerUnavailable detail -> detail
    | PersistFailed detail -> detail
    | LayerValueInvalid(key, layer, detail) ->
      sprintf "%s (%s layer): %s" key (ConfigLayer.label layer) detail

/// A TCP port a user may bind. Only the unprivileged range is constructable,
/// so a `Port` can never name a privileged (<1024) or out-of-range port.
type Port = private Port of int

[<RequireQualifiedAccess>]
module Port =
  let create (n: int) : Result<Port, ConfigError> =
    match n >= 1024 && n <= 65535 with
    | true -> Ok (Port n)
    | false -> Error (OutOfRange (sprintf "port must be between 1024 and 65535, got %d" n))

  let value (Port n) = n

/// A member of a closed set of strings (a theme name, a workflow, a run
/// policy). The chosen value is provably one of `allowed` — there is no way
/// to construct an `EnumValue` outside its own set. The descriptor supplies
/// the allowed set, so this one type serves every closed-string setting
/// without the core needing to know the specific sets.
type EnumValue = private EnumValue of allowed: string list * chosen: string

[<RequireQualifiedAccess>]
module EnumValue =
  let create (allowed: string list) (raw: string) : Result<EnumValue, ConfigError> =
    match List.contains raw allowed with
    | true -> Ok (EnumValue(allowed, raw))
    | false -> Error (NotAMember(raw, allowed))

  let value (EnumValue(_, chosen)) = chosen
  let allowed (EnumValue(a, _)) = a

/// A binary setting's state as a domain DU, not a `bool` — a flag that can be
/// "off" says why by its case name, and this stays exhaustive if a third state
/// is ever needed. (Domain-modeling rule: no `bool` for state.)
type Toggle =
  | On
  | Off

[<RequireQualifiedAccess>]
module Toggle =
  let isOn (t: Toggle) : bool =
    // The single boundary where the DU becomes a bool, for subsystems whose
    // API still takes one; the domain itself never stores a bool.
    match t with
    | On -> true
    | Off -> false

  let ofBool (b: bool) : Toggle =
    match b with
    | true -> On
    | false -> Off

/// The unified value of any setting. Every case's payload is already a type
/// whose only inhabitants are legal, so a `SettingValue` cannot carry an
/// illegal configuration.
type SettingValue =
  | VToggle of Toggle
  | VPort of Port
  | VTimeout of ValidTimeout
  | VBindHost of SageFsConfig.LoopbackHost
  | VEnum of EnumValue

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
  /// The ONLY boundary where a raw string becomes a typed value; returns a
  /// typed ConfigError on failure. Nothing downstream re-validates.
  Parse: string -> Result<SettingValue, ConfigError>
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
