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

/// What a mount line of /proc/self/mountinfo says is under it. A btrfs mount's own device number is anonymous
/// (it has no entry under /sys/dev/block), so the disk behind it is found from the source the line names.
[<RequireQualifiedAccess>]
type MountSource =
  | Device of path: string
  | NotADevice

module MountSource =

  /// The field that ends the optional fields of a mount line; the file system type and the source follow it.
  [<Literal>]
  let private Separator = "-"

  /// The source of a mount line, when it is a device node (`/dev/...`). The separator is found, not counted:
  /// the optional fields before it are any number.
  let ofMountinfoLine (line: string) : MountSource =
    let parts = line.Split(' ')
    match Array.tryFindIndex (fun p -> p = Separator) parts with
    | None -> MountSource.NotADevice
    | Some i ->
      match parts.Length > i + 2 && parts.[i + 2].StartsWith("/dev/", StringComparison.Ordinal) with
      | true -> MountSource.Device parts.[i + 2]
      | false -> MountSource.NotADevice

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

/// A limit on how much CPU the process may use, apart from the cores it can see: a container's `--cpus`, a
/// systemd scope's `CPUQuota`. .NET rounds such a quota UP to whole cores (half a core is one), but a start
/// inside it takes twice as long, not as long.
[<RequireQualifiedAccess>]
type CpuQuota =
  | Unlimited
  | Limited of cores: float

/// What was learned about a machine at one moment. Pure data; reading it off the machine is
/// `MachineProbeReader`.
type MachineProbe =
  { /// Logical cores .NET can use: affinity and a cgroup CPU quota already applied (and rounded up).
    LogicalCores: int
    CpuQuota: CpuQuota
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

  /// The CPU the process can actually use, in cores: the cores it can see, held down by a quota if it has one.
  let effectiveCores (probe: MachineProbe) : float =
    match probe.CpuQuota with
    | CpuQuota.Unlimited -> float probe.LogicalCores
    | CpuQuota.Limited cores -> min cores (float probe.LogicalCores)

  /// Four cores or more lose nothing to a lack of them: the measured start on 16, 8 and 4 cores differs by 10%
  /// (docs/TROUBLESHOOTING.md).
  [<Literal>]
  let FastMinCores = 4.0

  /// One to four cores: the start's parallel parts (the compiler building the FSI host) take about 1.6 times as
  /// long on two cores and 1.7 times on one. A start still fits well inside the `Standard` waits.
  [<Literal>]
  let StandardMinCores = 1.0

  /// Half a core to one: a start takes several times as long.
  [<Literal>]
  let ConstrainedMinCores = 0.5

  let coreTier (cores: float) : MachineTier =
    match cores with
    | n when n >= FastMinCores -> MachineTier.Fast
    | n when n >= StandardMinCores -> MachineTier.Standard
    | n when n >= ConstrainedMinCores -> MachineTier.Constrained
    | _ -> MachineTier.Minimal

  /// Measured on 2026-10-01 on the 5800XT with the cap on the daemon and everything it starts (`MemoryMax`, no
  /// swap, shipped 0.6.875, scripts/machine-bench.fsx --memory-cap): at 8, 4, 2 and 1.5 GB a start to Ready took
  /// as long as at 32 GB (warm 5.7 to 6.3 s, cold 14.2 to 15.7 s), so 2 GB available loses nothing. The daemon,
  /// the worker and the FSI host peak at about 720 MB together. At 1 GB the first start still finished
  /// (15.5 s cold, 5.8 s warm) and so did a plain hard reset, but a hard reset with a rebuild never returned:
  /// the build next to a live worker and host went over the cap and everything was killed. At 768 MB the first
  /// start did not finish in 300 s. So the floor is between 1 and 1.5 GB, and below it no wait helps.
  [<Literal>]
  let FastMinAvailableMb = 2048L

  /// 1.5 GB ran at full speed in every stage measured, so this tier is a margin and not a cost.
  [<Literal>]
  let StandardMinAvailableMb = 1536L

  /// A first start finishes down to 1 GB (above), slowly if anything, and that is what the longer waits are for.
  /// Under it the machine is `Minimal`: the longest waits, which cannot rescue a build that is killed (a rebuild
  /// already was at 1 GB) but do let a slow one finish.
  [<Literal>]
  let ConstrainedMinAvailableMb = 1024L

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
      coreTier (effectiveCores probe)
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
    sprintf "%.1f cores, %.1f GB memory (%.1f GB available), %s, %s" (MachineProbe.effectiveCores probe) (float probe.TotalMemoryMb / 1024.0) (float probe.AvailableMemoryMb / 1024.0) calibration storage

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
              | true -> Some (mount.Length, parts.[2], MountSource.ofMountinfoLine line)
              | false -> None)
          |> Array.sortByDescending (fun (length, _, _) -> length)
          |> Array.tryHead
        match owner with
        | None -> StorageKind.Unknown
        | Some (_, majorMinor, source) ->
          // /sys/dev/block/<maj:min> is the device. A partition keeps `queue/` on its parent disk; a
          // device-mapper volume keeps it on itself, inherited from what it sits on. A btrfs mount has no
          // such entry, so its device is found from the source the mount line names (/dev/mapper/root is a
          // link to /dev/dm-0, which /sys/class/block knows).
          let byNumber = Path.Combine("/sys/dev/block", majorMinor)
          let sysDevice =
            match Directory.Exists byNumber, source with
            | true, _ -> byNumber
            | false, MountSource.Device dev ->
              let real = (match File.ResolveLinkTarget(dev, true) with | null -> dev | t -> t.FullName)
              Path.Combine("/sys/class/block", Path.GetFileName real)
            | false, MountSource.NotADevice -> byNumber
          match Directory.ResolveLinkTarget(sysDevice, true) with
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

  /// What memory the machine has: total and available, in MB.
  type private Memory =
    { TotalMb: int64
      AvailableMb: int64 }

  let private kbPerMb = 1024L

  let private bytesPerMb = 1024L * 1024L

  /// MemTotal and MemAvailable from /proc/meminfo (in kB there). `MemAvailable` is the kernel's own estimate of what
  /// can be used without swapping, which is what decides whether a start is slow.
  let private fromProcMeminfo () : Memory option =
    try
      match File.Exists "/proc/meminfo" with
      | false -> None
      | true ->
        let kb (name: string) =
          File.ReadAllLines "/proc/meminfo"
          |> Array.tryPick (fun line ->
            match line.StartsWith(name + ":", StringComparison.Ordinal) with
            | true -> Some (Int64.Parse(line.Substring(name.Length + 1).Replace("kB", "").Trim()))
            | false -> None)
        match kb "MemTotal", kb "MemAvailable" with
        | Some total, Some available -> Some { TotalMb = total / kbPerMb; AvailableMb = available / kbPerMb }
        | _ -> None
    with _ -> None

  /// The memory limit this process's cgroup (v2) puts on it, and what is left under it, when there is one. A
  /// container or a systemd scope can hold a process to less than the machine has, and a start inside it
  /// swaps or is killed at that limit, not at the machine's.
  let private fromCgroup () : Memory option =
    try
      match File.Exists "/proc/self/cgroup" with
      | false -> None
      | true ->
        let line = File.ReadAllLines "/proc/self/cgroup" |> Array.tryFind (fun l -> l.StartsWith("0::", StringComparison.Ordinal))
        match line with
        | None -> None
        | Some l ->
          // The limit may sit on a parent of the group this process is in: walk up to the first that has one.
          let rec up (relative: string) : Memory option =
            let dir = Path.Combine("/sys/fs/cgroup", relative)
            let limit = Path.Combine(dir, "memory.max")
            match File.Exists limit with
            | true when File.ReadAllText(limit).Trim() <> "max" ->
              let max' = Int64.Parse(File.ReadAllText(limit).Trim())
              let used = Int64.Parse(File.ReadAllText(Path.Combine(dir, "memory.current")).Trim())
              Some { TotalMb = max' / bytesPerMb; AvailableMb = max 0L (max' - used) / bytesPerMb }
            | _ ->
              match relative with
              | "" -> None
              | _ -> up (match relative.LastIndexOf '/' with | -1 -> "" | i -> relative.Substring(0, i))
          up (l.Substring(3).Trim('/'))
    with _ -> None

  /// The CPU quota this process's cgroup (v2) puts on it, when there is one: `cpu.max` is `<quota> <period>`, in
  /// microseconds, or `max <period>` for none. Looked for up the tree like the memory limit.
  let private cpuQuota () : CpuQuota =
    try
      match File.Exists "/proc/self/cgroup" with
      | false -> CpuQuota.Unlimited
      | true ->
        let line = File.ReadAllLines "/proc/self/cgroup" |> Array.tryFind (fun l -> l.StartsWith("0::", StringComparison.Ordinal))
        match line with
        | None -> CpuQuota.Unlimited
        | Some l ->
          let rec up (relative: string) : CpuQuota =
            let file = Path.Combine("/sys/fs/cgroup", relative, "cpu.max")
            let parts = (match File.Exists file with | true -> File.ReadAllText(file).Trim().Split(' ') | false -> [||])
            match parts with
            | [| quota; period |] when quota <> "max" -> CpuQuota.Limited (float (Int64.Parse quota) / float (Int64.Parse period))
            | _ ->
              match relative with
              | "" -> CpuQuota.Unlimited
              | _ -> up (match relative.LastIndexOf '/' with | -1 -> "" | i -> relative.Substring(0, i))
          up (l.Substring(3).Trim('/'))
    with _ -> CpuQuota.Unlimited

  /// The machine's memory, with any cgroup limit applied. Where there is no /proc (not Linux), the
  /// runtime's own view of the memory it may use.
  let private readMemory () : Memory =
    match fromProcMeminfo (), fromCgroup () with
    | Some machine, Some group -> { TotalMb = min machine.TotalMb group.TotalMb; AvailableMb = min machine.AvailableMb group.AvailableMb }
    | Some machine, None -> machine
    | None, _ ->
      let gc = GC.GetGCMemoryInfo()
      let total = gc.TotalAvailableMemoryBytes / bytesPerMb
      { TotalMb = total; AvailableMb = max 0L (total - gc.MemoryLoadBytes / bytesPerMb) }

  /// Take a probe now. The data directory is where the storage is read from, because that is the disk
  /// a start reads and writes.
  let read (dataDir: string) : Result<MachineProbe, MachineProbeError> =
    try
      let memory = readMemory ()
      let measurement = MachineCalibration.measure ()
      Ok
        { LogicalCores = Environment.ProcessorCount
          CpuQuota = cpuQuota ()
          TotalMemoryMb = memory.TotalMb
          AvailableMemoryMb = memory.AvailableMb
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
