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
/// two file layers persist, `LDefault` is the descriptor's built-in, and the
/// last two are what the person set outside the settings files: an environment
/// variable, and a flag on the command line the daemon was started with.
type ConfigLayer =
  | LDefault
  | LGlobal
  | LRepo
  | LSession
  | LEnvironment
  | LCommandLine

[<RequireQualifiedAccess>]
module ConfigLayer =
  /// Higher rank wins. Total and injective over the six cases. The order is the one
  /// `SageFsConfig.chooseMcpPort` already uses: flag, environment, saved, default.
  let rank (layer: ConfigLayer) : int =
    match layer with
    | LDefault -> 0
    | LGlobal -> 1
    | LRepo -> 2
    | LSession -> 3
    | LEnvironment -> 4
    | LCommandLine -> 5

  let label (layer: ConfigLayer) : string =
    match layer with
    | LDefault -> "default"
    | LGlobal -> "global"
    | LRepo -> "repo"
    | LSession -> "session"
    | LEnvironment -> "environment"
    | LCommandLine -> "flag"

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

/// A working-directory path constrained to its legal inhabitants: either unset
/// (empty) or an absolute, rooted path. A relative path is rejected — it would
/// resolve against whatever the daemon's cwd happens to be, so it can never be a
/// dependable default. Existence is deliberately NOT required (the directory may
/// be created before it is first used).
type DirectoryPath = private DirectoryPath of string

[<RequireQualifiedAccess>]
module DirectoryPath =
  let create (raw: string) : Result<DirectoryPath, ConfigError> =
    let trimmed = (raw |> Option.ofObj |> Option.defaultValue "").Trim()
    match trimmed with
    | "" -> Ok (DirectoryPath "")
    | p when System.IO.Path.IsPathRooted p -> Ok (DirectoryPath p)
    | p -> Error (Malformed(sprintf "working directory must be an absolute path, got '%s'" p))

  let value (DirectoryPath p) = p
  /// Whether an actual directory is set (an empty path means "no default").
  let isSet (DirectoryPath p) = p <> ""

/// How long SageFs waits for something: at least a second and at most a day. `ValidTimeout` stops at ten
/// minutes, which is the default of the warm-up and build waits, so it cannot hold the number a person on a
/// big repository needs. A `WaitLimit` is never zero and never negative, so no consumer re-checks it.
type WaitLimit = private WaitLimit of TimeSpan

[<RequireQualifiedAccess>]
module WaitLimit =
  let private shortestWait = TimeSpan.FromSeconds Timeouts.settingWaitShortestSeconds

  /// The most a wait may be, for a parser that has to bound a number before it becomes a span.
  let longest : TimeSpan = TimeSpan.FromHours Timeouts.settingWaitLongestHours

  let create (t: TimeSpan) : Result<WaitLimit, ConfigError> =
    match t >= shortestWait && t <= longest with
    | true -> Ok (WaitLimit t)
    | false -> Error (OutOfRange (sprintf "a wait must be between 1 second and 24 hours, got %gs" t.TotalSeconds))

  let value (WaitLimit t) : TimeSpan = t

/// The unit a duration setting is written in. It is the unit of the environment variable behind the setting,
/// so the saved text and the variable are the same text.
type DurationUnit =
  | Seconds
  | Minutes
  | Hours

[<RequireQualifiedAccess>]
module DurationUnit =
  let label (unit: DurationUnit) : string =
    match unit with
    | Seconds -> "seconds"
    | Minutes -> "minutes"
    | Hours -> "hours"

  let toSpan (unit: DurationUnit) (amount: float) : TimeSpan =
    match unit with
    | Seconds -> TimeSpan.FromSeconds amount
    | Minutes -> TimeSpan.FromMinutes amount
    | Hours -> TimeSpan.FromHours amount

  let amountOf (unit: DurationUnit) (span: TimeSpan) : float =
    match unit with
    | Seconds -> span.TotalSeconds
    | Minutes -> span.TotalMinutes
    | Hours -> span.TotalHours

/// A bound that may be switched off: `0` is a real answer for the abandoned-session reaper, and it means "never".
/// A case name says so where a zero span would have to be remembered.
type OptionalLimit =
  | LimitOff
  | LimitAfter of WaitLimit

/// The unified value of any setting. Every case's payload is already a type
/// whose only inhabitants are legal, so a `SettingValue` cannot carry an
/// illegal configuration.
type SettingValue =
  | VToggle of Toggle
  | VPort of Port
  | VTimeout of ValidTimeout
  | VDuration of WaitLimit
  | VLimit of OptionalLimit
  | VBindHost of SageFsConfig.LoopbackHost
  | VEnum of EnumValue
  | VDir of DirectoryPath

/// What a `SettingValue` holds when it is a given kind. Each function names every other case, so a new kind of value
/// breaks the build here once and not at every descriptor that reads one kind.
[<RequireQualifiedAccess>]
module SettingValue =
  let duration (v: SettingValue) : WaitLimit option =
    match v with
    | VDuration limit -> Some limit
    | VToggle _ | VPort _ | VTimeout _ | VLimit _ | VBindHost _ | VEnum _ | VDir _ -> None

  let limit (v: SettingValue) : OptionalLimit option =
    match v with
    | VLimit limit -> Some limit
    | VToggle _ | VPort _ | VTimeout _ | VDuration _ | VBindHost _ | VEnum _ | VDir _ -> None

  let toggle (v: SettingValue) : Toggle option =
    match v with
    | VToggle t -> Some t
    | VPort _ | VTimeout _ | VDuration _ | VLimit _ | VBindHost _ | VEnum _ | VDir _ -> None

  let enum (v: SettingValue) : EnumValue option =
    match v with
    | VEnum e -> Some e
    | VToggle _ | VPort _ | VTimeout _ | VDuration _ | VLimit _ | VBindHost _ | VDir _ -> None

  let dir (v: SettingValue) : DirectoryPath option =
    match v with
    | VDir d -> Some d
    | VToggle _ | VPort _ | VTimeout _ | VDuration _ | VLimit _ | VBindHost _ | VEnum _ -> None

/// The resolved value plus its provenance: which layer supplied the effective
/// value, and the value present at every layer that set one (so the UI can
/// show base-vs-override explicitly — requirement #2).
type Provenance = {
  Effective: SettingValue
  Source: ConfigLayer
  PerLayer: (ConfigLayer * SettingValue) list
}

/// The category a setting is grouped under in the UI. A DU, not a free string,
/// so the group set is closed and the display order is total.
type SettingCategory =
  | Appearance
  | SessionSettings
  | LiveTesting
  /// The Live Bindings view: which columns, grouping, strip, inspector, value printing and row height, for each workflow.
  | LiveBindingsView
  /// How long SageFs waits for a warm-up, a build or a stopped agent, and how the waits scale for the machine.
  | WaitsAndLimits
  | DaemonSettings

[<RequireQualifiedAccess>]
module SettingCategory =
  let label (c: SettingCategory) : string =
    match c with
    | Appearance -> "Appearance"
    | SessionSettings -> "Session"
    | LiveTesting -> "Live testing"
    | LiveBindingsView -> "Live bindings view"
    | WaitsAndLimits -> "Waits and limits"
    | DaemonSettings -> "Daemon"

  /// Display order for the panel — exhaustive over the DU.
  let displayOrder : SettingCategory list =
    [ Appearance; SessionSettings; LiveTesting; LiveBindingsView; WaitsAndLimits; DaemonSettings ]

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
  /// Shown with its value and where the value came from, and never edited here.
  | ReadOnly of ReadOnlyReason

/// Why a row has no edit control. A closed set, so the page says which reason in words.
and ReadOnlyReason =
  /// Changing it from a page the daemon serves points the trust the wrong way (the bind host, the identity policy).
  | SecurityBoundary
  /// The settings file lives inside it, so a saved value could never be read back.
  | HoldsTheSettingsFile
  /// A credential: its value is never drawn.
  | Secret
  /// SageFs sets it for the processes it starts; a person's value would be overwritten.
  | SetBySageFs
  /// Only a flag on the command line sets it, so it is read when the daemon is started.
  | LaunchOnly

[<RequireQualifiedAccess>]
module ReadOnlyReason =
  let describe (reason: ReadOnlyReason) : string =
    match reason with
    | SecurityBoundary -> "read-only here: changing it from a page the daemon serves would point the trust the wrong way. Set it where you start the daemon."
    | HoldsTheSettingsFile -> "read-only here: the settings file lives inside it. Set it where you start the daemon."
    | Secret -> "read-only here: it is a credential, so its value is never shown."
    | SetBySageFs -> "read-only here: SageFs sets it for the processes it starts."
    | LaunchOnly -> "read-only here: only a flag on the command line sets it. Start the daemon with the flag."

/// Where a setting can also be set from outside the settings files. The environment variable and the flag are
/// the names a person types, and the row shows them when one of them wins.
type ExternalSource =
  | NoExternalSource
  | FromEnvironment of variable: string
  | FromFlag of flag: string
  | FromEnvironmentOrFlag of variable: string * flag: string

[<RequireQualifiedAccess>]
module ExternalSource =
  let variable (source: ExternalSource) : string option =
    match source with
    | FromEnvironment v
    | FromEnvironmentOrFlag(v, _) -> Some v
    | NoExternalSource
    | FromFlag _ -> None

  let flag (source: ExternalSource) : string option =
    match source with
    | FromFlag f
    | FromEnvironmentOrFlag(_, f) -> Some f
    | NoExternalSource
    | FromEnvironment _ -> None

/// What reading one environment variable found. A DU, so "unset" and "set to the empty string" are not the same
/// null; the reader is injected so a test never touches the process environment.
type EnvRead =
  | EnvUnset
  | EnvSet of raw: string

/// What the settings resolve against besides the two files: the environment, and the flags the daemon was started with.
type ExternalInputs = {
  Environment: string -> EnvRead
  /// Flag name (`--mcp-port`) to its raw value. A switch such as `--no-resume` maps to `on`.
  Flags: Map<string, string>
}

[<RequireQualifiedAccess>]
module ExternalInputs =
  /// Nothing outside the files: the inputs a caller that only cares about the saved layers passes.
  let none : ExternalInputs = { Environment = (fun _ -> EnvUnset); Flags = Map.empty }

  let ofEnvironment (read: string -> string | null) : string -> EnvRead =
    fun name ->
      match read name with
      | null -> EnvUnset
      | "" -> EnvUnset
      | raw -> EnvSet raw

/// Where the value the subsystem is using NOW comes from. A setting whose consumer reads the layers every time it
/// needs the value is in use as soon as it is saved; one that read it once at start has to be asked, and the answer
/// is read from the consumer, never from the file, so the in-use column is evidence and not an echo of what was saved.
type CurrentUse =
  | InUseReadEachTime
  | InUseFromConsumer of (unit -> SettingValue)

/// How the value in use compares with the value the layers resolve to.
type InUse =
  | InUseSameAsSaved
  | InUseDiffers of inUse: SettingValue

/// The authority for one setting: how to parse/render its raw form, its
/// default (already a legal `SettingValue`), and how to apply it live. One
/// descriptor drives resolution, the editor, and (later) the UI row.
type SettingDescriptor = {
  Key: string
  /// Short human-facing label for the row (the Key is the stable id).
  Name: string
  Description: string
  Category: SettingCategory
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
  /// The environment variable and flag that can set it from outside the files.
  External: ExternalSource
  /// How to read what the subsystem is using now.
  Current: CurrentUse
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
