namespace SageFs

open System

/// How fast this machine is for SageFs, as a closed set. A wait that is for the machine (a start, a
/// build, a warm-up, a restart) is scaled by `MachineTier.factor`; a wait that is for a person or a
/// protocol is not. See `Timeouts` for which is which, and `docs/TROUBLESHOOTING.md` for the
/// measurements the boundaries come from (`MachineProbe`, in MachineProfile.fs).
///
/// This file has no dependency on the rest of SageFs on purpose: `Timeouts.fs` reads it, and
/// `Timeouts.fs` is compiled into the FSI host too, which has none of SageFs.Core.
[<RequireQualifiedAccess>]
type MachineTier =
  /// A current desktop or laptop. The baseline every duration in `Timeouts` was written for.
  | Fast
  /// An older or smaller machine that is still comfortable: a first start takes a few times longer.
  | Standard
  /// A machine on which a first start takes tens of seconds: a 2009 four core with a spinning disk, a
  /// 2 core VM, a laptop on battery. Everything works; it is the 30 second waits that do not.
  | Constrained
  /// A machine that can run SageFs but takes minutes to start it.
  | Minimal

/// Why a text is not a tier. A closed set, so a caller reports it without parsing a message.
[<RequireQualifiedAccess>]
type TierParseError =
  | NotATier of value: string

module MachineTier =

  /// The environment variable that forces a tier. Also what the daemon passes to the workers it
  /// starts, so the daemon and every process under it agree on one tier. Unset means `Fast`: the
  /// durations in `Timeouts` as written.
  [<Literal>]
  let envVar = "SAGEFS_MACHINE_TIER"

  let all : MachineTier list =
    [ MachineTier.Fast; MachineTier.Standard; MachineTier.Constrained; MachineTier.Minimal ]

  let toString (tier: MachineTier) : string =
    match tier with
    | MachineTier.Fast -> "Fast"
    | MachineTier.Standard -> "Standard"
    | MachineTier.Constrained -> "Constrained"
    | MachineTier.Minimal -> "Minimal"

  /// Case-insensitive.
  let tryParse (text: string | null) : Result<MachineTier, TierParseError> =
    let wanted = (match text with | null -> "" | t -> t.Trim())
    match all |> List.tryFind (fun tier -> String.Equals(toString tier, wanted, StringComparison.OrdinalIgnoreCase)) with
    | Some tier -> Ok tier
    | None -> Error (TierParseError.NotATier wanted)

  /// The error in words. Names what was accepted, so a typo in the variable is not a mystery.
  let describeParseError (error: TierParseError) : string =
    match error with
    | TierParseError.NotATier value ->
      sprintf "'%s' is not a machine tier; use one of %s" value (all |> List.map toString |> String.concat ", ")

  /// 0 for the fastest tier, up by one per tier. The order every comparison uses.
  let rank (tier: MachineTier) : int =
    match tier with
    | MachineTier.Fast -> 0
    | MachineTier.Standard -> 1
    | MachineTier.Constrained -> 2
    | MachineTier.Minimal -> 3

  /// The slower of two tiers. A machine is as slow as its worst limit.
  let slowest (a: MachineTier) (b: MachineTier) : MachineTier =
    match rank a >= rank b with
    | true -> a
    | false -> b

  /// How many times longer a wait for the machine is allowed on this tier than on `Fast`, where it is
  /// the number written in `Timeouts`.
  let factor (tier: MachineTier) : float =
    match tier with
    | MachineTier.Fast -> 1.0
    | MachineTier.Standard -> 2.0
    | MachineTier.Constrained -> 5.0
    | MachineTier.Minimal -> 12.0

  /// A wait for the machine, scaled for a tier: `baseline` (what it is on `Fast`) times the tier's
  /// factor, never past `ceiling` (`Timeouts.scaledWaitCeiling`) and never below `baseline`. A baseline
  /// that is already longer than the ceiling is kept as written, never shortened.
  let scaleWait (ceiling: TimeSpan) (tier: MachineTier) (baseline: TimeSpan) : TimeSpan =
    let scaled = TimeSpan.FromTicks(int64 (float baseline.Ticks * factor tier))
    match scaled > ceiling with
    | true -> max baseline ceiling
    | false -> scaled

  /// The tier a process was told to use: `Fast` when nothing was set or what was set cannot be read
  /// (the process that reads the variable first reports a bad value; see `MachineStartup`).
  let ofEnvironmentValue (value: string | null) : MachineTier =
    match value with
    | null -> MachineTier.Fast
    | v when String.IsNullOrWhiteSpace v -> MachineTier.Fast
    | v ->
      match tryParse v with
      | Ok tier -> tier
      | Error _ -> MachineTier.Fast

  /// The tier in force for this process, from its environment.
  let current () : MachineTier =
    ofEnvironmentValue (Environment.GetEnvironmentVariable envVar)
