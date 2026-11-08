namespace SageFs

open System
open System.Globalization
open SageFs.Features.MetadataDelta

/// A setting whose consumer reads a variable once, when its process starts (through `SettingsInForce.read`). The
/// Settings page saves the value to the global layer file; `SettingsBootstrap.resolveSaved` runs first thing in `main`
/// and puts each saved value (when the person set no variable) into the settings snapshot, so `Timeouts`, which
/// freezes its values the first time it is read, and every worker, which is handed the snapshot, agree with what the
/// page saved. The environment is not written.
///
/// This file is compiled before `SettingsCatalog` and touches nothing in `Timeouts` or `SageFsConfig` when it
/// loads. A closure that reads them runs only when a row is drawn. That matters: the resolution has to run before
/// `Timeouts` is first read, and loading this file must not be what reads it.
type EnvBacked = {
  Descriptor: SettingDescriptor
  /// The variable the consumer reads. The same text as `Descriptor.External`'s variable.
  Variable: string
  /// The text the variable gets for a saved value. `None` means the saved value says "leave the variable unset"
  /// (the machine tier's `auto`).
  EnvText: SettingValue -> string option
}

module EnvBackedSettings =

  let parseAmount (unit: DurationUnit) (raw: string) : Result<SettingValue, ConfigError> =
    match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | false, _ -> Error (Malformed (sprintf "expected a number of %s, got '%s'" (DurationUnit.label unit) raw))
    | true, n ->
      // Bounded before it becomes a span: an infinite or enormous number of seconds throws in `TimeSpan`.
      let most = DurationUnit.amountOf unit WaitLimit.longest
      match Double.IsFinite n && n > 0.0 && n <= most with
      | false -> Error (OutOfRange (sprintf "expected more than zero and at most %g %s, got %s" most (DurationUnit.label unit) raw))
      | true -> WaitLimit.create (DurationUnit.toSpan unit n) |> Result.map VDuration

  let renderAmount (unit: DurationUnit) (span: TimeSpan) : string =
    DurationUnit.amountOf unit span |> sprintf "%g"

  let legal (result: Result<WaitLimit, ConfigError>) : WaitLimit =
    match result with
    | Ok limit -> limit
    | Error _ -> failwith "a built-in wait default must be within a second and a day"

  let durationDefault (unit: DurationUnit) (amount: float) : SettingValue =
    VDuration (legal (WaitLimit.create (DurationUnit.toSpan unit amount)))

  /// The wait a subsystem holds, as a value. One outside the legal range (a variable can hold any positive number)
  /// is shown at the nearest end of it, so the row never reads a number the daemon is not near.
  let asDuration (span: TimeSpan) : SettingValue =
    let shortest = TimeSpan.FromSeconds Timeouts.settingWaitShortestSeconds
    match WaitLimit.create (max shortest (min span WaitLimit.longest)) with
    | Ok limit -> VDuration limit
    | Error _ -> durationDefault Seconds Timeouts.settingWaitShortestSeconds

  /// One wait, in the unit its variable is written in. `inForce` reads the live value from `Timeouts`.
  let duration
    (key: string)
    (name: string)
    (description: string)
    (variable: string)
    (unit: DurationUnit)
    (defaultAmount: float)
    (inForce: unit -> TimeSpan)
    : EnvBacked =
    { Descriptor =
        { Key = key
          Name = name
          Description = description
          Category = WaitsAndLimits
          Scope = Global
          Applicability = RestartRequired
          Default = durationDefault unit defaultAmount
          Parse = parseAmount unit
          Render =
            (fun v ->
              SettingValue.duration v
              |> Option.map (fun limit -> renderAmount unit (WaitLimit.value limit))
              |> Option.defaultValue "")
          Apply = ignore
          External = FromEnvironment variable
          Current = InUseFromConsumer (fun () -> asDuration (inForce ())) }
      Variable = variable
      EnvText =
        (fun v ->
          SettingValue.duration v |> Option.map (fun limit -> renderAmount unit (WaitLimit.value limit))) }

  let warmupInactivity : EnvBacked =
    duration
      "waits.warmupInactivitySeconds"
      "Warm-up inactivity limit"
      "How long a starting session may show no progress before SageFs calls it stuck (seconds). Raise it on a big repository or a slow CI machine. The default is 30 seconds on a fast machine and is scaled for slower ones."
      "SAGEFS_WARMUP_INACTIVITY_SECONDS"
      Seconds
      30.0
      (fun () -> Timeouts.warmupInactivityLimit)

  let warmupMax : EnvBacked =
    duration
      "waits.warmupMaxMinutes"
      "Warm-up ceiling"
      "The longest a session may take to warm up, however much progress it shows (minutes). The default is 10 minutes on a fast machine and is scaled for slower ones."
      "SAGEFS_WARMUP_MAX_MINUTES"
      Minutes
      10.0
      (fun () -> Timeouts.warmupAbsoluteMax)

  let buildTimeout : EnvBacked =
    duration
      "waits.buildTimeoutMinutes"
      "Build timeout"
      "The longest one project build may run before SageFs kills it (minutes). The default is 10 minutes on a fast machine and is scaled for slower ones."
      "SAGEFS_BUILD_TIMEOUT_MINUTES"
      Minutes
      10.0
      (fun () -> Timeouts.buildCompletion)

  let hostBuildTimeout : EnvBacked =
    duration
      "waits.hostBuildMinutes"
      "FSI host build timeout"
      "The longest the build of the isolated FSI host may run (minutes). The default is 5 minutes on a fast machine and is scaled for slower ones."
      "SAGEFS_HOST_BUILD_MINUTES"
      Minutes
      5.0
      (fun () -> Timeouts.hostBuildRun)

  /// The tiers, plus `auto`: let SageFs measure the machine. A saved `auto` and no saved value mean the same, so the
  /// import leaves the variable unset for it.
  /// Lower case on the page; the variable's reader (`MachineTier.tryParse`) ignores case.
  let private tierText (tier: MachineTier) : string = (MachineTier.toString tier).ToLowerInvariant()

  let private tierNames : string list =
    "auto" :: (MachineTier.all |> List.map tierText)

  let private tierOf (raw: string) : Result<SettingValue, ConfigError> =
    EnumValue.create tierNames (raw.Trim().ToLowerInvariant()) |> Result.map VEnum

  let machineTier : EnvBacked =
    let auto =
      match tierOf "auto" with
      | Ok v -> v
      | Error _ -> failwith "auto is one of the tier choices"
    { Descriptor =
        { Key = "waits.machineTier"
          Name = "Machine tier"
          Description =
            "How slow SageFs assumes this machine is. Every wait for the machine (a start, a build, a warm-up) is scaled by it. auto measures the machine once and remembers the answer."
          Category = WaitsAndLimits
          Scope = Global
          Applicability = RestartRequired
          Default = auto
          Parse = tierOf
          Render = (fun v -> SettingValue.enum v |> Option.map EnumValue.value |> Option.defaultValue "")
          Apply = ignore
          External = FromEnvironment MachineTier.envVar
          Current =
            InUseFromConsumer (fun () ->
              match tierOf (tierText Timeouts.machineTier) with
              | Ok v -> v
              | Error _ -> auto) }
      Variable = MachineTier.envVar
      EnvText = (fun v -> SettingValue.enum v |> Option.map EnumValue.value |> Option.filter (fun tier -> tier <> "auto")) }

  let private limitHours (raw: string) : Result<SettingValue, ConfigError> =
    match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | false, _ -> Error (Malformed (sprintf "expected a number of hours (0 turns it off), got '%s'" raw))
    | true, 0.0 -> Ok (VLimit LimitOff)
    | true, n when not (Double.IsFinite n) || n < 0.0 || n > 24.0 ->
      Error (OutOfRange (sprintf "expected 0 (off) or at most 24 hours, got %s" raw))
    | true, n -> WaitLimit.create (TimeSpan.FromHours n) |> Result.map (fun limit -> VLimit (LimitAfter limit))

  let private renderLimitHours (v: SettingValue) : string =
    match SettingValue.limit v with
    | Some LimitOff -> "0"
    | Some (LimitAfter limit) -> renderAmount Hours (WaitLimit.value limit)
    | None -> ""

  let abandonedSessionHours : EnvBacked =
    { Descriptor =
        { Key = "waits.abandonedSessionHours"
          Name = "Abandoned agent sessions"
          Description =
            "How many hours an agent's session may sit idle with its connection gone before the daemon stops it. 0 turns the reaper off. Sessions made from the dashboard, the CLI or an editor are never stopped this way."
          Category = WaitsAndLimits
          Scope = Global
          Applicability = RestartRequired
          Default = VLimit (LimitAfter (legal (WaitLimit.create (DurationUnit.toSpan Hours 12.0))))
          Parse = limitHours
          Render = renderLimitHours
          Apply = ignore
          External = FromEnvironment "SAGEFS_ABANDONED_SESSION_HOURS"
          Current =
            InUseFromConsumer (fun () ->
              match Timeouts.abandonedSessionBound = TimeSpan.Zero with
              | true -> VLimit LimitOff
              | false ->
                match WaitLimit.create Timeouts.abandonedSessionBound with
                | Ok limit -> VLimit (LimitAfter limit)
                | Error _ -> VLimit LimitOff) }
      Variable = "SAGEFS_ABANDONED_SESSION_HOURS"
      EnvText = (fun v -> SettingValue.limit v |> Option.map (fun _ -> renderLimitHours v)) }

  let toggleOf (raw: string) : Result<SettingValue, ConfigError> =
    match raw.Trim().ToLowerInvariant() with
    | "on" | "1" | "true" | "yes" -> Ok (VToggle On)
    | "off" | "0" | "false" | "no" -> Ok (VToggle Off)
    | other -> Error (NotAMember(other, [ "on"; "off" ]))

  let renderToggle (v: SettingValue) : string =
    match SettingValue.toggle v with
    | Some On -> "on"
    | Some Off -> "off"
    | None -> ""

  let updateCheckDisabled : EnvBacked =
    let variable = "SAGEFS_UPDATE_CHECK_DISABLED"
    let textOf (v: SettingValue) : string option =
      match SettingValue.toggle v with
      | Some On -> Some "1"
      | Some Off -> Some "0"
      | None -> None
    { Descriptor =
        { Key = "daemon.updateCheckDisabled"
          Name = "Stop asking NuGet for updates"
          Description = "On stops the daemon asking NuGet whether a newer SageFs exists. A daemon running from a local build never asks."
          Category = DaemonSettings
          Scope = Global
          Applicability = RestartRequired
          Default = VToggle Off
          Parse = toggleOf
          Render = renderToggle
          Apply = ignore
          External = FromEnvironment variable
          Current =
            // The rule `UpdateCheckService` applies to the variable: set, and not 0 or false, opts out.
            InUseFromConsumer (fun () ->
              match SettingsInForce.read variable with
              | null | "" -> VToggle Off
              | v ->
                match v = "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) with
                | true -> VToggle Off
                | false -> VToggle On) }
      Variable = variable
      EnvText = textOf }

  let metadataDelta : EnvBacked =
    let variable = MetadataDeltaMode.environmentVariable
    { Descriptor =
        { Key = "hotreload.metadataDelta"
          Name = "Metadata-delta hot reload"
          Description =
            "On patches a saved edit into the running app and keeps its state. Off restarts the app on every save. Read each time a worker starts, so it applies to the next session you start."
          Category = SessionSettings
          Scope = Global
          Applicability = RestartRequired
          Default = VToggle (match MetadataDeltaMode.defaultMode with MetadataDeltaMode.On -> On | MetadataDeltaMode.Off -> Off)
          Parse = toggleOf
          Render = renderToggle
          Apply = ignore
          External = FromEnvironment variable
          Current =
            InUseFromConsumer (fun () ->
              match MetadataDeltaMode.fromEnvironment () with
              | MetadataDeltaMode.On -> VToggle On
              | MetadataDeltaMode.Off -> VToggle Off) }
      Variable = variable
      EnvText =
        (fun v ->
          match SettingValue.toggle v with
          | Some On -> Some (MetadataDeltaMode.toText MetadataDeltaMode.On)
          | Some Off -> Some (MetadataDeltaMode.toText MetadataDeltaMode.Off)
          | None -> None) }

  /// The first batch of settings that are saved as a file value and resolved into the settings snapshot at start.
  /// `EnvBackedRows.all` is these and the rest.
  let firstBatch : EnvBacked list =
    [ warmupInactivity
      warmupMax
      buildTimeout
      hostBuildTimeout
      machineTier
      abandonedSessionHours
      updateCheckDisabled
      metadataDelta ]
