namespace SageFs

open System
open System.IO

/// What the machine's storage is, as far as the start time goes. A spinning disk reads the first
/// start's hundreds of megabytes of assemblies several times slower than a solid state one, whatever
/// the CPU is.
[<RequireQualifiedAccess>]
type StorageKind =
  | Rotational
  | SolidState
  /// Not Linux, or the device could not be traced from the data directory. Counts for nothing either way.
  | Unknown

/// Whether the single-thread timing was taken. A DU, not an option: "not measured" says why.
[<RequireQualifiedAccess>]
type Calibration =
  | Measured of steadyMs: float * firstMs: float
  | NotMeasured of reason: string

/// Why a probe of the machine could not be taken.
[<RequireQualifiedAccess>]
type MachineProbeError =
  | CouldNotProbe of reason: string

/// Why the machine profile could not be written.
[<RequireQualifiedAccess>]
type ProfileWriteError =
  | CouldNotWrite of path: string * reason: string

/// What was learned about a machine at one moment. Pure data; reading it off the machine is
/// `MachineProbeReader`.
type MachineProbe =
  { /// Logical cores .NET can use: affinity and a cgroup CPU quota already applied.
    LogicalCores: int
    TotalMemoryMb: int64
    AvailableMemoryMb: int64
    Storage: StorageKind
    Calibration: Calibration }

module MachineProbe =

  /// What `MachineCalibration` reads on the fast reference machine (a Ryzen 7 5800XT, PassMark single
  /// thread 3533): 22 to 24 ms across every run on 2026-10-01. The slowness of any other machine is
  /// its reading over this one.
  [<Literal>]
  let ReferenceCalibration = 22.0

  /// Slowness below this is `Fast`: up to a 1.75 times slower core than the reference.
  [<Literal>]
  let FastBelowSlowness = 1.75

  /// Slowness below this is `Standard`.
  [<Literal>]
  let StandardBelowSlowness = 3.5

  /// Slowness below this is `Constrained`; at or above it is `Minimal`.
  [<Literal>]
  let ConstrainedBelowSlowness = 8.0

  let slowness (steadyMs: float) : float = steadyMs / ReferenceCalibration

  /// The tier the CPU alone gives. A calibration that was not taken says nothing about the CPU, and
  /// `Fast` is the tier with no scaling, so the other limits decide.
  let speedTier (calibration: Calibration) : MachineTier =
    match calibration with
    | Calibration.NotMeasured _ -> MachineTier.Fast
    | Calibration.Measured (steadyMs, _) ->
      let s = slowness steadyMs
      match s with
      | _ when s < FastBelowSlowness -> MachineTier.Fast
      | _ when s < StandardBelowSlowness -> MachineTier.Standard
      | _ when s < ConstrainedBelowSlowness -> MachineTier.Constrained
      | _ -> MachineTier.Minimal

  /// Cores at or above this lose nothing to a lack of them.
  [<Literal>]
  let FastMinCores = 8

  [<Literal>]
  let StandardMinCores = 4

  [<Literal>]
  let ConstrainedMinCores = 2

  let coreTier (logicalCores: int) : MachineTier =
    match logicalCores with
    | n when n >= FastMinCores -> MachineTier.Fast
    | n when n >= StandardMinCores -> MachineTier.Standard
    | n when n >= ConstrainedMinCores -> MachineTier.Constrained
    | _ -> MachineTier.Minimal

  [<Literal>]
  let FastMinAvailableMb = 6144L

  [<Literal>]
  let StandardMinAvailableMb = 3072L

  [<Literal>]
  let ConstrainedMinAvailableMb = 1536L

  let memoryTier (availableMb: int64) : MachineTier =
    match availableMb with
    | n when n >= FastMinAvailableMb -> MachineTier.Fast
    | n when n >= StandardMinAvailableMb -> MachineTier.Standard
    | n when n >= ConstrainedMinAvailableMb -> MachineTier.Constrained
    | _ -> MachineTier.Minimal

  /// A spinning disk makes a first start at least a `Constrained` one.
  let storageTier (storage: StorageKind) : MachineTier =
    match storage with
    | StorageKind.Rotational -> MachineTier.Constrained
    | StorageKind.SolidState | StorageKind.Unknown -> MachineTier.Fast

  /// The tier a probe gives: the slowest of what the CPU, the cores, the memory and the disk each give.
  let tierOf (probe: MachineProbe) : MachineTier =
    [ speedTier probe.Calibration
      coreTier probe.LogicalCores
      memoryTier probe.AvailableMemoryMb
      storageTier probe.Storage ]
    |> List.fold MachineTier.slowest MachineTier.Fast

/// The waits of a start that the machine's speed decides. A closed set, so a learned duration is
/// stored under a name and a failure can say what it was waiting for.
[<RequireQualifiedAccess>]
type StartStage =
  /// From spawning a worker to its reporting the port it listens on: loading the project, building or
  /// starting the FSI host, creating the FSI session. What the daemon's inactivity guard waits through.
  | WorkerPort
  /// From spawning a worker to the session being Ready: the above plus warm-up and test discovery.
  | WorkerReady

module StartStage =
  let all : StartStage list = [ StartStage.WorkerPort; StartStage.WorkerReady ]

  let toString (stage: StartStage) : string =
    match stage with
    | StartStage.WorkerPort -> "WorkerPort"
    | StartStage.WorkerReady -> "WorkerReady"

  /// What a person is told it was waiting for.
  let describe (stage: StartStage) : string =
    match stage with
    | StartStage.WorkerPort -> "the worker to load the project and start the FSI host"
    | StartStage.WorkerReady -> "the session to finish warming up"

/// A smoothed estimate of how long a stage takes here, and how much it varies, in the way TCP
/// estimates a round trip (RFC 6298): a smoothed mean and a smoothed mean deviation, updated by each
/// observation, with a timeout of the mean plus four deviations.
type StageEstimate =
  { Stage: StartStage
    SmoothedMs: float
    DeviationMs: float
    Samples: int }

module StageEstimate =

  /// RFC 6298 alpha: how much of a new observation moves the mean.
  [<Literal>]
  let SmoothingGain = 0.125

  /// RFC 6298 beta: how much of a new observation's distance from the mean moves the deviation.
  [<Literal>]
  let DeviationGain = 0.25

  /// RFC 6298 K: how many deviations above the mean a timeout sits.
  [<Literal>]
  let DeviationMultiplier = 4.0

  /// The first observation: the mean is the observation, the deviation half of it (RFC 6298 2.2).
  let first (stage: StartStage) (observedMs: float) : StageEstimate =
    { Stage = stage; SmoothedMs = observedMs; DeviationMs = observedMs / 2.0; Samples = 1 }

  let observe (estimate: StageEstimate) (observedMs: float) : StageEstimate =
    let deviation = (1.0 - DeviationGain) * estimate.DeviationMs + DeviationGain * abs (estimate.SmoothedMs - observedMs)
    let smoothed = (1.0 - SmoothingGain) * estimate.SmoothedMs + SmoothingGain * observedMs
    { estimate with SmoothedMs = smoothed; DeviationMs = deviation; Samples = estimate.Samples + 1 }

  /// How long to wait for this stage: the mean plus four deviations.
  let timeout (estimate: StageEstimate) : TimeSpan =
    TimeSpan.FromMilliseconds(estimate.SmoothedMs + DeviationMultiplier * estimate.DeviationMs)

/// What is known of one stage here: its estimate, or that it has never been seen.
[<RequireQualifiedAccess>]
type StageHistory =
  | Seen of StageEstimate
  | NeverSeen

/// What SageFs remembers about this machine between runs: the probe, and how long the stages of a
/// start have actually taken. Stored in the data directory as `machine-profile.json`.
type MachineProfile =
  { Probe: MachineProbe
    Tier: MachineTier
    Stages: StageEstimate list }

module MachineProfile =

  [<Literal>]
  let FileName = "machine-profile.json"

  let ofProbe (probe: MachineProbe) : MachineProfile =
    { Probe = probe; Tier = MachineProbe.tierOf probe; Stages = [] }

  /// The estimate for a stage. A DU, not an option: a stage never seen is a stated case.
  let stage (stage: StartStage) (profile: MachineProfile) : StageHistory =
    match profile.Stages |> List.tryFind (fun e -> e.Stage = stage) with
    | Some estimate -> StageHistory.Seen estimate
    | None -> StageHistory.NeverSeen

  /// Fold one observed duration in. A stage seen for the first time gets its first estimate.
  let observe (stageSeen: StartStage) (observedMs: float) (profile: MachineProfile) : MachineProfile =
    let updated =
      match stage stageSeen profile with
      | StageHistory.Seen estimate -> StageEstimate.observe estimate observedMs
      | StageHistory.NeverSeen -> StageEstimate.first stageSeen observedMs
    { profile with Stages = updated :: (profile.Stages |> List.filter (fun e -> e.Stage <> stageSeen)) }

/// Where the tier in force came from, so a person who asks "why is it waiting this long" can be told.
[<RequireQualifiedAccess>]
type TierSource =
  /// `SAGEFS_MACHINE_TIER` said so (a person's override, or the daemon telling a worker).
  | Forced
  /// The profile file from an earlier run, and the probe it holds.
  | FromProfile of path: string * MachineProbe
  /// No profile yet: probed just now.
  | Probed of MachineProbe
  /// The probe itself failed; `Standard` is used and the reason is kept.
  | ProbeFailed of reason: string

/// What became of the override variable. A value that could not be read is carried, not swallowed:
/// it is reported along with the tier that was used instead.
[<RequireQualifiedAccess>]
type OverrideUse =
  | NotSet
  | Honoured
  | Rejected of value: string

/// The tier in force and how it was reached.
type TierResolution =
  { Tier: MachineTier
    Source: TierSource
    Override: OverrideUse }

/// The profile file as the resolution found it.
[<RequireQualifiedAccess>]
type ProfileRead =
  | NoProfile
  | Unreadable of reason: string
  | Found of path: string * MachineProfile

module TierResolution =

  /// Pure: which tier, from the override value, the profile file and a probe taken if needed. The
  /// override always wins; a readable profile wins over probing; an unreadable one is probed past.
  let resolve
    (overrideValue: string | null)
    (readProfile: unit -> ProfileRead)
    (probe: unit -> Result<MachineProbe, MachineProbeError>)
    : TierResolution =
    let fromMachine (overrideUse: OverrideUse) : TierResolution =
      match readProfile () with
      | ProfileRead.Found (path, profile) ->
        { Tier = profile.Tier; Source = TierSource.FromProfile (path, profile.Probe); Override = overrideUse }
      | ProfileRead.NoProfile
      | ProfileRead.Unreadable _ ->
        match probe () with
        | Ok p -> { Tier = MachineProbe.tierOf p; Source = TierSource.Probed p; Override = overrideUse }
        | Error (MachineProbeError.CouldNotProbe reason) -> { Tier = MachineTier.Standard; Source = TierSource.ProbeFailed reason; Override = overrideUse }
    match overrideValue with
    | null -> fromMachine OverrideUse.NotSet
    | v when String.IsNullOrWhiteSpace v -> fromMachine OverrideUse.NotSet
    | v ->
      match MachineTier.tryParse v with
      | Ok tier -> { Tier = tier; Source = TierSource.Forced; Override = OverrideUse.Honoured }
      | Error _ -> fromMachine (OverrideUse.Rejected v)

module MachineProbeDescription =

  /// What a probe saw, in a line: cores, memory, calibration, disk.
  let describe (probe: MachineProbe) : string =
    let calibration =
      match probe.Calibration with
      | Calibration.Measured (steadyMs, _) -> sprintf "calibration %.0f ms, %.1f times the reference" steadyMs (MachineProbe.slowness steadyMs)
      | Calibration.NotMeasured reason -> sprintf "calibration not taken (%s)" reason
    let storage =
      match probe.Storage with
      | StorageKind.Rotational -> "a spinning disk"
      | StorageKind.SolidState -> "a solid state disk"
      | StorageKind.Unknown -> "disk unknown"
    sprintf "%d cores, %.1f GB memory (%.1f GB available), %s, %s" probe.LogicalCores (float probe.TotalMemoryMb / 1024.0) (float probe.AvailableMemoryMb / 1024.0) calibration storage

module TierResolutionDescription =

  /// The tier in force and how it was reached, in words, for the daemon log and the status surfaces.
  let describe (resolution: TierResolution) : string =
    let how =
      match resolution.Source with
      | TierSource.Forced -> sprintf "set by %s" MachineTier.envVar
      | TierSource.FromProfile (path, probe) -> sprintf "from the profile in %s (%s)" path (MachineProbeDescription.describe probe)
      | TierSource.Probed probe -> sprintf "from a probe just taken (%s)" (MachineProbeDescription.describe probe)
      | TierSource.ProbeFailed reason -> sprintf "the probe failed (%s), so the neutral tier is used" reason
    let rejected =
      match resolution.Override with
      | OverrideUse.Rejected value -> sprintf " %s=%s is not a tier and was ignored." MachineTier.envVar value
      | OverrideUse.NotSet | OverrideUse.Honoured -> ""
    sprintf "Machine tier %s, %s.%s" (MachineTier.toString resolution.Tier) how rejected

/// Reading and writing the machine, which is the impure edge of everything above.
module MachineProbeReader =

  /// The device a path lives on, found from /proc/self/mountinfo, and whether the kernel says it
  /// spins. Linux only; anywhere else, or on any failure, `Unknown`.
  let private storageOf (path: string) : StorageKind =
    try
      match File.Exists "/proc/self/mountinfo" with
      | false -> StorageKind.Unknown
      | true ->
        let full = Path.GetFullPath path
        // The longest mount point that is a prefix of the path owns it.
        let owner =
          File.ReadAllLines "/proc/self/mountinfo"
          |> Array.choose (fun line ->
            let parts = line.Split(' ')
            match parts.Length > 4 with
            | false -> None
            | true ->
              let mount = parts.[4]
              let withSlash = (match mount.EndsWith "/" with | true -> mount | false -> mount + "/")
              match full = mount || full.StartsWith(withSlash, StringComparison.Ordinal) with
              | true -> Some (mount.Length, parts.[2])
              | false -> None)
          |> Array.sortByDescending fst
          |> Array.tryHead
        match owner with
        | None -> StorageKind.Unknown
        | Some (_, majorMinor) ->
          // /sys/dev/block/<maj:min> is the device. A partition keeps `queue/` on its parent disk; a
          // device-mapper volume keeps it on itself, inherited from what it sits on.
          match Directory.ResolveLinkTarget(Path.Combine("/sys/dev/block", majorMinor), true) with
          | null -> StorageKind.Unknown
          | target ->
            let parent = (match Path.GetDirectoryName target.FullName with | null -> "" | p -> p)
            let read (dir: string) =
              let file = Path.Combine(dir, "queue", "rotational")
              match File.Exists file with
              | true -> Some (File.ReadAllText(file).Trim())
              | false -> None
            match [ target.FullName; parent ] |> List.tryPick read with
            | Some "1" -> StorageKind.Rotational
            | Some "0" -> StorageKind.SolidState
            | _ -> StorageKind.Unknown
    with _ -> StorageKind.Unknown

  /// Take a probe now. The data directory is where the storage is read from, because that is the disk
  /// a start reads and writes.
  let read (dataDir: string) : Result<MachineProbe, MachineProbeError> =
    try
      let gc = GC.GetGCMemoryInfo()
      let totalMb = gc.TotalAvailableMemoryBytes / (1024L * 1024L)
      let loadedMb = gc.MemoryLoadBytes / (1024L * 1024L)
      let measurement = MachineCalibration.measure ()
      Ok
        { LogicalCores = Environment.ProcessorCount
          TotalMemoryMb = totalMb
          AvailableMemoryMb = max 0L (totalMb - loadedMb)
          Storage = storageOf dataDir
          Calibration = Calibration.Measured (measurement.SteadyMs, measurement.FirstMs) }
    with ex -> Error (MachineProbeError.CouldNotProbe ex.Message)

  let profilePath (dataDir: string) : string = Path.Combine(dataDir, MachineProfile.FileName)

  let readProfile (dataDir: string) : ProfileRead =
    let path = profilePath dataDir
    try
      match File.Exists path with
      | false -> ProfileRead.NoProfile
      | true ->
        match Json.deserialize<MachineProfile> Json.standard (File.ReadAllText path) with
        | Ok profile -> ProfileRead.Found (path, profile)
        | Error error -> ProfileRead.Unreadable (JsonError.describe error)
    with ex -> ProfileRead.Unreadable ex.Message

  /// Best effort: a profile that cannot be written is a tier that is probed again next time.
  let writeProfile (dataDir: string) (profile: MachineProfile) : Result<unit, ProfileWriteError> =
    try
      Directory.CreateDirectory dataDir |> ignore
      let path = profilePath dataDir
      // Written beside and moved over, so a reader never sees half a file.
      let temporary = path + ".tmp"
      File.WriteAllText(temporary, Json.serialize (Json.indented Json.standard) profile)
      File.Move(temporary, path, true)
      Ok ()
    with ex -> Error (ProfileWriteError.CouldNotWrite (profilePath dataDir, ex.Message))

/// The tier of this process, established once at the start of a process that works it out (the
/// daemon, a command line tool): from the override, the profile in the data directory, or a probe. It
/// sets `SAGEFS_MACHINE_TIER` in the process's own environment so `Timeouts` and every process this
/// one starts agree, and keeps how it got there for the surfaces that say so.
module MachineStartup =

  [<RequireQualifiedAccess>]
  type Established =
    | NotYet
    | Done of TierResolution

  let mutable private established = Established.NotYet
  let private gate = obj ()

  /// Work out this process's tier and publish it in its environment. Idempotent: the first call
  /// decides. Call it before anything reads `Timeouts`. `dataDir` is where the profile lives.
  let establish (dataDir: string) : TierResolution =
    lock gate (fun () ->
      match established with
      | Established.Done resolution -> resolution
      | Established.NotYet ->
        let dir = dataDir
        let resolution =
          TierResolution.resolve
            (Environment.GetEnvironmentVariable MachineTier.envVar)
            (fun () -> MachineProbeReader.readProfile dir)
            (fun () -> MachineProbeReader.read dir)
        Environment.SetEnvironmentVariable(MachineTier.envVar, MachineTier.toString resolution.Tier)
        established <- Established.Done resolution
        resolution)

  /// How this process's tier was reached; `NotYet` in a process that was handed its tier (a worker)
  /// and never established one itself.
  let current () : Established = established

  /// The environment of a daemon this process starts. This process worked its own tier out and published it so
  /// `Timeouts` would see it; a daemon it starts works out its own, from the same profile, and must not mistake
  /// the published value for a person's override. A person's own override is kept.
  let withoutDerivedTier (environment: System.Collections.Generic.IDictionary<string, string | null>) : unit =
    match established with
    | Established.Done { Override = OverrideUse.NotSet } -> environment.Remove MachineTier.envVar |> ignore
    | Established.Done _ | Established.NotYet -> ()
