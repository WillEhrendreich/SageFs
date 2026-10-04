/// Pure planning for the pipeline's test tiers: how many run at once, in what
/// order, and how each one is wrapped so it cannot touch another's files.
///
/// Loaded by ci-pipeline.fsx (`#load`) and compiled into SageFs.Tests, so the
/// rules the pipeline runs on are the rules the tests check.
module SageFs.Build.TierPlan

/// A target framework the test assembly is built for and run on. One case per
/// entry of Directory.Build.props' SageFsTargetFrameworks (a test pins that), so
/// a framework the tool ships for cannot be left without a tier.
type Framework =
  | Net10
  | Net11

module Framework =
  let all = [ Net10; Net11 ]

  /// The framework the gate's own `dotnet build` targets (Directory.Build.props
  /// TargetFramework). Its tiers keep their unqualified names.
  let primary = Net11

  let tfm (framework: Framework) =
    match framework with
    | Net10 -> "net10.0"
    | Net11 -> "net11.0"

  /// The label a ledger row carries for a non-primary framework.
  let label (framework: Framework) =
    match framework with
    | Net10 -> "net10"
    | Net11 -> "net11"

  let ofTfm (name: string) : Result<Framework, string> =
    match all |> List.tryFind (fun f -> tfm f = name) with
    | Some f -> Result.Ok f
    | None -> Result.Error (sprintf "no test tier exists for the framework %s" name)

  /// From `AppContext.TargetFrameworkName` (".NETCoreApp,Version=v10.0"), which
  /// a process reads from its own entry assembly, so a test process names the
  /// framework it was built for rather than trusting what the pipeline said.
  let ofTargetFrameworkName (name: string | null) : Result<Framework, string> =
    match name with
    | null -> Result.Error "the assembly declares no target framework"
    | name ->
      match name.Split(',') with
      | [| ".NETCoreApp"; version |] when version.StartsWith "Version=v" ->
        ofTfm (sprintf "net%s" (version.Substring "Version=v".Length))
      | _ -> Result.Error (sprintf "not a .NET target framework name: %s" name)

/// One invocation of the test assembly (`dotnet <dll> <Args>`), on one framework.
type Tier = { Name: string; Args: string; Framework: Framework }

/// One slice of a sharded tier: shard `Index` (1-based) of `Count`.
type Shard = { Index: int; Count: int }

/// `--shard k/n` in an argument list, when present and well-formed.
let shardOfArgs (args: string array) : Shard option =
  match args |> Array.tryFindIndex ((=) "--shard") with
  | Some i when i + 1 < args.Length ->
    match args[i + 1].Split('/') with
    | [| k; n |] ->
      match System.Int32.TryParse k, System.Int32.TryParse n with
      | (true, k), (true, n) when n >= 1 && k >= 1 && k <= n -> Some { Index = k; Count = n }
      | _ -> None
    | _ -> None
  | _ -> None

/// Tier name of an argument string: its first token, with the bare
/// `--summary` default run named "default" (the trust ledger's Tier field),
/// and a shard suffix (`--integration-host[2/4]`) so every shard is its own row.
let nameOfArgs (args: string) =
  let tokens = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
  let baseName =
    match tokens[0] with
    | "--summary" -> "default"
    | flag -> flag
  match shardOfArgs tokens with
  | Some s -> sprintf "%s[%d/%d]" baseName s.Index s.Count
  | None -> baseName

/// A name safe to use as a file name (logs, ledgers, clones).
let fileNameOf (tierName: string) =
  tierName.TrimStart('-').Replace("/", "of").Replace("[", "-").Replace("]", "")

/// Partition `suites` into `count` shards, balanced by recorded duration
/// (longest first, each into the currently lightest shard). Pure and
/// deterministic: every shard process computes the SAME map from the same
/// inputs, so the shards together run every suite exactly once without any
/// coordination. A suite with no recorded duration counts as the mean of the
/// known ones (or 1s when nothing is known). Ties break on name.
let assign (count: int) (durations: Map<string, float>) (suites: string list) : Map<string, int> =
  let count = max 1 count
  let suites = List.distinct suites
  let known = suites |> List.choose durations.TryFind
  let fallback = match known with [] -> 1.0 | xs -> List.average xs
  let weight s = durations.TryFind s |> Option.defaultValue fallback
  let load = Array.create count 0.0
  suites
  |> List.sortWith (fun a b ->
    match compare (weight b) (weight a) with
    | 0 -> compare a b
    | c -> c)
  |> List.fold (fun (acc: Map<string, int>) s ->
    let i = load |> Array.mapi (fun i l -> i, l) |> Array.minBy (fun (i, l) -> l, i) |> fst
    load[i] <- load[i] + weight s
    acc.Add(s, i + 1)) Map.empty

/// A tier's ledger name on `framework`: the plain name on the primary
/// framework (so recorded durations and every existing row keep matching), the
/// name plus the framework's label elsewhere. Distinct for every framework, so
/// two frameworks never share a ledger row, a log, a clone or a duration.
let qualify (framework: Framework) (name: string) =
  match framework = Framework.primary with
  | true -> name
  | false -> sprintf "%s-%s" name (Framework.label framework)

/// A tier running `args` on `framework`.
let tierOn (framework: Framework) (args: string) =
  { Name = qualify framework (nameOfArgs args); Args = args; Framework = framework }

/// A tier on the primary framework.
let tier (args: string) = tierOn Framework.primary args

/// Where a framework's test assembly is built, relative to the checkout.
let testBinDirOf (framework: Framework) =
  sprintf "SageFs.Tests/bin/Release/%s" (Framework.tfm framework)

let dllOf (framework: Framework) = sprintf "%s/SageFs.Tests.dll" (testBinDirOf framework)

/// The commands that build the test assembly for `framework`, for a framework
/// the whole-solution `dotnet build` did not already build it for: a restore,
/// then a build that does not restore again.
///
/// The restore is its own step because a build's implicit restore ignores the
/// `TargetFramework` property and restores the project's default (net11.0), which
/// then fails the build with NETSDK1005.
///
/// `-p:TargetFramework=` picks the framework (the test project is single-target;
/// Core, Host and Simulation multi-target and follow it). Three more properties
/// keep it from disturbing the gate's own tree:
///  * NuGetLockFilePath: the restore would otherwise REWRITE the tracked
///    packages.lock.json files with only this framework's sections. (Turning the
///    lock file off instead is NU1005 while a tracked one exists.)
///  * RestoreLockedMode=false: CI turns locked restore on, which refuses a
///    graph that differs from the committed (all-frameworks) lock file.
///  * BaseIntermediateOutputPath: a restore writes obj/project.assets.json, and
///    the primary build's copy lists net11.0 only; sharing it would leave every
///    later `--no-restore` net11 build failing NETSDK1005. A private obj
///    directory per framework keeps the two restores apart. bin/ is already
///    per-framework.
let testBuildCommands (framework: Framework) : string list =
  let tfm = Framework.tfm framework
  let isolation =
    sprintf
      "-p:TargetFramework=%s -p:NuGetLockFilePath=obj/tier-%s/packages.lock.json -p:RestoreLockedMode=false -p:BaseIntermediateOutputPath=obj/tier-%s/"
      tfm tfm tfm
  [ sprintf "dotnet restore SageFs.Tests %s" isolation
    sprintf "dotnet build SageFs.Tests -c Release --no-restore %s" isolation ]

/// Whether tiers can be given private copies of the checkout.
type Isolation =
  /// Copy-on-write clones (btrfs/xfs reflink) plus a private mount namespace
  /// that shows each tier ITS clone at the original checkout path. Tests
  /// locate fixtures through `__SOURCE_DIRECTORY__`, an absolute path baked
  /// into the compiled DLL, so a clone at a different path would still read
  /// and write the original. The mount is what makes the isolation real.
  | CopyOnWrite
  /// No private copies (a plain-ext4 hosted runner): tiers share one tree and
  /// must run one at a time.
  | Shared

/// The STATIC rule for how many tiers run at once, kept as what a machine that cannot be read falls back to
/// (`Admission.standard`'s FallbackConcurrency; `policyFor` decides everything else by pressure and memory).
/// Without isolation, one: sharing a checkout is exactly the conflict this module exists to rule out. With it, an
/// explicit request wins, else one tier per ~3 cores capped at 6, because each tier runs its own daemons, FSI hosts
/// and browsers, and contention fakes timing failures (the trust table is where a too-high setting shows up).
let parallelism (isolation: Isolation) (cores: int) (requested: int option) =
  match isolation, requested with
  | Shared, _ -> 1
  | CopyOnWrite, Some n when n >= 1 -> n
  | CopyOnWrite, _ -> max 1 (min 6 (cores / 3))

/// The static order, used when there is no cost history to read (`TierSchedule.orderByCriticalPath` is the one the
/// pipeline runs: it also weighs how much of its time a tier spends waiting).
/// Longest-expected-first (LPT) order: with a fixed number of slots, starting
/// the long tiers first minimises the wall clock. A tier with no recorded
/// duration sorts FIRST, since it may be long and starting it last is the
/// worst case. Stable for equal durations.
let order (durations: Map<string, float>) (tiers: Tier list) : Tier list =
  tiers
  |> List.sortByDescending (fun t ->
    match durations.TryFind t.Name with
    | Some seconds -> seconds
    | None -> infinity)

/// How long a tier may run before the pipeline kills it. A hung tier used to
/// sit silently until the whole stage's 90-minute budget ran out (2026-09-22,
/// a spinner deadlock in the default tier). Four times the tier's recorded
/// duration, never under ten minutes, and never over an hour. A tier with no
/// recorded duration gets the hour, since it might just be long.
let tierTimeoutFloorSeconds = 600.0
let tierTimeoutCeilingSeconds = 3600.0

let timeoutOf (durations: Map<string, float>) (tier: Tier) : System.TimeSpan =
  let seconds =
    match durations.TryFind tier.Name with
    | Some recorded -> max tierTimeoutFloorSeconds (min tierTimeoutCeilingSeconds (recorded * 4.0))
    | None -> tierTimeoutCeilingSeconds
  System.TimeSpan.FromSeconds seconds

/// Wall-clock seconds for running `ordered` greedily on `slots` slots (each
/// tier takes the earliest free slot, in order). Pure model of the scheduler,
/// used to report the expected speed-up and to test the ordering.
let makespan (slots: int) (durationOf: Tier -> float) (ordered: Tier list) =
  let free = Array.create (max 1 slots) 0.0
  for t in ordered do
    let i = free |> Array.mapi (fun i f -> i, f) |> Array.minBy snd |> fst
    free[i] <- free[i] + durationOf t
  Array.max free

/// Concurrent tiers share ONE network namespace (mount namespaces isolate
/// files, never ports), and every real-daemon-spawning test harness reserves
/// a port by binding it, reading it back, then RELEASING it before the
/// daemon itself binds — a window another tier's daemon can win in. Below,
/// the pool every tier's daemon ports are scanned from, and the disjoint
/// slice of it handed to one concurrently-running slot, so that race can
/// never cross a tier boundary: two tiers scanning disjoint slices cannot
/// both land on the same pair no matter how the race falls.
///
/// The pool sits entirely below `testPortPoolHigh` (32000): comfortably under
/// the Linux ephemeral-port floor observed on this machine (32768, from
/// `/proc/sys/net/ipv4/ip_local_port_range`) — so a client connection's own
/// randomly-assigned outbound port can never collide with a port a tier is
/// deliberately trying to bind — and nowhere near the user's own live daemon
/// (37749/37750). Both exclusions fall out of the bound; neither needs its
/// own case.
let testPortPoolLow = 20000
let testPortPoolHigh = 32000

/// The `slotIndex`-th (0-based) of `slots` equal-width, contiguous, disjoint
/// slices of the port pool — one per concurrently-running tier process (not
/// one per tier BY NAME: `runTiers` reuses a slot for every tier it pulls off
/// the queue for that slot, so the slot index is what a port range is keyed
/// to). Integer division may leave a remainder; the LAST slice absorbs it, so
/// the slices tile the whole pool with no gaps and no overlap for any
/// `slots >= 1`.
let portRangeOf (slots: int) (slotIndex: int) : int * int =
  let slots = max 1 slots
  let slotIndex = ((slotIndex % slots) + slots) % slots
  let width = (testPortPoolHigh - testPortPoolLow) / slots
  let start = testPortPoolLow + slotIndex * width
  let stop = if slotIndex = slots - 1 then testPortPoolHigh else start + width
  start, stop

/// The argv that runs `command` with `clone` mounted over `checkout` AND
/// `privateTmp` mounted over /tmp, visible only to that process tree, as the
/// calling user rather than root.
///
/// The private /tmp is not optional. Tools meet their helpers in /tmp at FIXED
/// paths that ignore TMPDIR: MSBuild's reusable build nodes listen on
/// /tmp/MSBuild<pid>. With a shared /tmp, a tier's `dotnet build` handed work
/// to a node from ANOTHER namespace, which saw the ORIGINAL checkout at the
/// checkout path. It built into the shared tree, and the tier's own compiler
/// then could not find the output in its clone (FS0078 on
/// obj/Debug/.../ref/SageFs.Core.dll, first parallel gate, 2026-09-21). A
/// private /tmp closes that whole class: X locks, NuGet scratch, any
/// rendezvous socket.
///
/// Mounting needs root inside a user namespace, so the outer namespace maps
/// root, mounts, then a nested namespace maps back to `uid`/`gid` before the
/// tier starts. Chromium refuses to run as root, and nothing else should
/// either.
let isolatedArgv
  (uid: int)
  (gid: int)
  (checkout: string)
  (clone: string)
  (privateTmp: string)
  (command: string)
  : string list =
  let quote (s: string) = "'" + s.Replace("'", "'\\''") + "'"
  [ "unshare"; "--user"; "--map-root-user"; "--mount"; "--"; "sh"; "-c"
    sprintf "mount --bind %s %s && mount --bind %s /tmp && cd %s && exec unshare --user --map-user=%d --map-group=%d -- %s"
      (quote clone) (quote checkout) (quote privateTmp) (quote checkout) uid gid command ]

/// Whether a failing-looking case in this tier's output means the tier has failed. The mutation gate runs each
/// mutant as a case that FAILS when the mutant is not killed, so its log is full of `[E]` lines by design and its
/// own verdict (the score against its bar) is the judgment. Every other tier fails when a case fails.
let caseFailureFailsTier (tier: Tier) =
  not (tier.Args.StartsWith("--mutation-score", System.StringComparison.Ordinal))

// ---- admission by pressure --------------------------------------------------------------------------
//
// A fixed `cores / 3` slots is a guess about CPU, and the order of the units is a guess about what ends the run.
// A unit now starts when the machine has room for it: CPU pressure under a bound, and available memory covering a
// reserve plus the unit's own recorded peak, with a hard cap on how many tier processes run at once.
//
// Everything here is pure over a snapshot of the machine. The pipeline reads the snapshot from /proc through a port
// (`MachinePort`), the simulation (SageFs.Simulation.TierSched) feeds it a fake machine, and both call the same
// `advanceWith`, so what the simulation proves is what the pipeline runs.

/// `some avg10` of /proc/pressure/cpu: the percent of the last ten seconds in which at least one runnable task
/// waited for a CPU. A reading that could not be taken says why, so no caller treats "unknown" as "idle".
type CpuPressure =
  | Measured of percent: float
  | NotMeasured of reason: string

/// MemAvailable of /proc/meminfo, in bytes: what the kernel says a new process can have without pushing anything
/// to swap, other tenants' memory included.
type MemoryReading =
  | AvailableBytes of bytes: int64
  | Unreadable of reason: string

/// One look at the machine.
type Reading = { Pressure: CpuPressure; Memory: MemoryReading }

/// Where a snapshot comes from. A port: the pipeline's adapter reads /proc, the simulation's is a fake machine.
type MachinePort = { Read: unit -> Reading }

/// A unit waiting to start. `PeakBytes` is what it used the last time it ran (or `Admission.unknownPeakBytes`).
type Candidate = { Label: string; PeakBytes: int64 }

/// A unit that started at `StartedAt` (seconds on whatever clock the caller keeps).
type RunningUnit = { Unit: Candidate; StartedAt: float }

/// The thresholds that decide whether the machine has room. Every number has a name in `Admission`.
type Limits =
  { /// Never more than this many tier processes, whatever the machine says.
    MaxTierProcesses: int
    /// A unit does not start while `some avg10` is at or over this (once `PressureFloorUnits` run).
    CpuPressureBound: float
    /// Memory that stays available after a unit starts, for the OS and the other tenants of the machine.
    MemoryReserveBytes: int64
    /// Pressure never holds a unit back while fewer than this many run, so a busy neighbour cannot stall the gate.
    PressureFloorUnits: int
    /// The least time between two starts. `avg10` and a unit's memory both lag a start, so without it the
    /// first look at an idle machine admits everything at once.
    SettleSeconds: float
    /// How long a started unit takes to reach its peak. Memory it has not reached yet is still reserved for it.
    RampSeconds: float
    /// How long the head of the line waits on memory with nothing of ours running before it starts anyway.
    PatienceSeconds: float
    /// Concurrency when the machine cannot be read: the old static rule.
    FallbackConcurrency: int }

/// Named values: what each wait is for, and why that long.
module Admission =
  let bytesPerGiB = 1073741824L
  /// What a unit with no recorded peak is assumed to need. Above the largest peak measured (3.6 GiB).
  let unknownPeakBytes = 4L * bytesPerGiB
  /// The hard cap. The port pool is cut into one slice per possible slot (`portRangeOf`), 12 slots leave 1,000 ports each.
  let maxTierProcesses = 12
  /// Measured (2026-10-04): the timing cases that failed under load failed at loads where `some avg10` was far above this,
  /// and the gate at a calm 14 load average ran clean.
  let cpuPressureBound = 25.0
  /// Other agents hold about 30 GiB of this machine; the desktop and the kernel's own caches need the rest of a margin.
  let memoryReserveBytes = 8L * bytesPerGiB
  /// Three units already ran together in every gate that was green.
  let pressureFloorUnits = 3
  /// `avg10` is a ten second average: a few seconds is enough for a start's first CPU burst to show in it.
  let settleSeconds = 3.0
  /// A hot-reload case reaches its peak in about a minute (host, FSI session and build, measured).
  let rampSeconds = 60.0
  /// Two minutes: longer than any transient spike, short enough that a neighbour holding memory costs a wait, not the gate.
  let patienceSeconds = 120.0
  /// How often a waiting scheduler looks again when no unit has finished: at most a third of the settle time, so a start
  /// is never late by more than the settle's own resolution (a 2 s look turned a 3 s settle into 4 s, measured).
  let reevaluateEverySeconds = 1.0

  let standard (fallbackConcurrency: int) : Limits =
    { MaxTierProcesses = maxTierProcesses
      CpuPressureBound = cpuPressureBound
      MemoryReserveBytes = memoryReserveBytes
      PressureFloorUnits = pressureFloorUnits
      SettleSeconds = settleSeconds
      RampSeconds = rampSeconds
      PatienceSeconds = patienceSeconds
      FallbackConcurrency = max 1 fallbackConcurrency }

/// How many units may run: by what the machine has, or an explicit number (SAGEFS_TIER_PARALLEL), which is not
/// second-guessed.
type Policy =
  | ByPressure of Limits
  | Fixed of concurrency: int

/// Why a unit started.
type AdmitBasis =
  /// The machine had room.
  | Fits
  /// Nothing of ours ran and the unit had waited `PatienceSeconds`: starting it is better than waiting for a neighbour
  /// that may never leave. Never taken while any of our units runs.
  | StarvationGuard

/// Why a unit did not start.
type WaitReason =
  | AtProcessCap of cap: int
  | Settling of remainingSeconds: float
  | CpuBusy of percent: float * bound: float
  | MemoryShort of neededBytes: int64 * availableBytes: int64

type Decision =
  | Admit of AdmitBasis
  | Wait of WaitReason

module WaitReason =
  let describe (reason: WaitReason) =
    let gib (bytes: int64) = float bytes / float Admission.bytesPerGiB
    match reason with
    | AtProcessCap cap -> sprintf "%d tier processes already run" cap
    | Settling remaining -> sprintf "the last start is still settling (%.1fs)" remaining
    | CpuBusy (percent, bound) -> sprintf "cpu pressure %.1f%% is at or over %.0f%%" percent bound
    | MemoryShort (needed, available) -> sprintf "needs %.1f GiB free, %.1f GiB available" (gib needed) (gib available)

/// When the last unit started. A case of its own, so "never" is not a number that happens to be zero.
type LastAdmit =
  | NeverAdmitted
  | AdmittedAt of seconds: float

/// The line of units, in the order they should start, and what runs now.
type Schedule =
  { Waiting: Candidate list
    /// When the unit at the head of the line became the head: its wait is counted from here.
    HeadSince: float
    Running: RunningUnit list
    LastAdmit: LastAdmit }

let startSchedule (now: float) (waiting: Candidate list) : Schedule =
  { Waiting = waiting; HeadSince = now; Running = []; LastAdmit = NeverAdmitted }

/// The unit has ended (passed, failed, died or was killed): it no longer holds a place.
let finishUnit (label: string) (schedule: Schedule) : Schedule =
  { schedule with Running = schedule.Running |> List.filter (fun r -> r.Unit.Label <> label) }

/// Memory the running units have not yet used but will, by the time they reach their peak.
let unramped (limits: Limits) (now: float) (running: RunningUnit list) : int64 =
  running
  |> List.sumBy (fun r ->
    let left = max 0.0 (1.0 - (now - r.StartedAt) / limits.RampSeconds)
    int64 (float r.Unit.PeakBytes * left))

/// Whether `head` may start now. Order of the rules: cap, then (with a readable machine) settle, CPU, memory.
let decide (policy: Policy) (reading: Reading) (now: float) (schedule: Schedule) (head: Candidate) : Decision =
  let running = schedule.Running.Length
  match policy with
  | Fixed concurrency ->
    match running >= concurrency with
    | true -> Wait (AtProcessCap concurrency)
    | false -> Admit Fits
  | ByPressure limits ->
    let sinceLast =
      match schedule.LastAdmit with
      | NeverAdmitted -> infinity
      | AdmittedAt at -> now - at
    // A reading that is missing costs the scheduler its right to the full cap: it runs the static fallback.
    let cap =
      match reading.Pressure, reading.Memory with
      | Measured _, AvailableBytes _ -> limits.MaxTierProcesses
      | NotMeasured _, _
      | _, Unreadable _ -> min limits.MaxTierProcesses limits.FallbackConcurrency
    match running >= cap with
    | true -> Wait (AtProcessCap cap)
    | false ->
      match running > 0 && sinceLast < limits.SettleSeconds with
      | true -> Wait (Settling (limits.SettleSeconds - sinceLast))
      | false ->
        let busy =
          match reading.Pressure with
          | Measured percent when running >= limits.PressureFloorUnits && percent >= limits.CpuPressureBound -> Some percent
          | Measured _
          | NotMeasured _ -> None
        match busy, reading.Memory with
        | Some percent, _ -> Wait (CpuBusy (percent, limits.CpuPressureBound))
        | None, Unreadable _ -> Admit Fits
        | None, AvailableBytes available ->
          let needed = limits.MemoryReserveBytes + head.PeakBytes + unramped limits now schedule.Running
          match available >= needed with
          | true -> Admit Fits
          | false ->
            match running = 0 && now - schedule.HeadSince >= limits.PatienceSeconds with
            | true -> Admit StarvationGuard
            | false -> Wait (MemoryShort (needed, available))

/// What one look at the head of the line came to.
type Advance =
  | Started of started: Candidate * basis: AdmitBasis * next: Schedule
  | Held of WaitReason
  | Drained

/// Look at the head of the line only: a unit never starts ahead of the one before it, so the order the caller chose is
/// the order they start in and nothing waits behind a smaller unit forever. `decider` is a parameter so the simulation
/// can run a twin that breaks one rule.
let advanceWith
  (decider: Policy -> Reading -> float -> Schedule -> Candidate -> Decision)
  (policy: Policy)
  (reading: Reading)
  (now: float)
  (schedule: Schedule)
  : Advance =
  match schedule.Waiting with
  | [] -> Drained
  | head :: rest ->
    match decider policy reading now schedule head with
    | Wait reason -> Held reason
    | Admit basis ->
      Started
        (head,
         basis,
         { Waiting = rest
           HeadSince = now
           Running = { Unit = head; StartedAt = now } :: schedule.Running
           LastAdmit = AdmittedAt now })

let advance = advanceWith decide

/// The policy a run gets. Without isolation tiers share one checkout, so one runs at a time. An explicit request
/// (SAGEFS_TIER_PARALLEL) is a fixed count. Otherwise the machine decides, and `parallelism` (the old static rule)
/// is only what an unreadable machine falls back to.
let policyFor (isolation: Isolation) (cores: int) (requested: int option) : Policy =
  match isolation, requested with
  | Shared, _ -> Fixed 1
  | CopyOnWrite, Some n when n >= 1 -> Fixed n
  | CopyOnWrite, _ -> ByPressure (Admission.standard (parallelism isolation cores None))

/// How many tiers can run at once under `policy`: the number of slots the port pool is cut into (`portRangeOf`).
let slotsOf (policy: Policy) : int =
  match policy with
  | Fixed n -> n
  | ByPressure limits -> limits.MaxTierProcesses

// ---- reading the machine ----------------------------------------------------------------------------

/// `some avg10=0.50 avg60=13.60 avg300=12.01 total=403518219`: the `some` line's avg10 (the `full` line is ignored).
let parseCpuPressure (text: string) : CpuPressure =
  let someLine =
    text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
    |> Array.tryFind (fun l -> l.StartsWith "some ")
  match someLine with
  | None -> NotMeasured "no `some` line in the pressure file"
  | Some line ->
    let avg10 =
      line.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
      |> Array.tryPick (fun f -> match f.StartsWith "avg10=" with true -> Some (f.Substring "avg10=".Length) | false -> None)
    match avg10 with
    | None -> NotMeasured "the `some` line has no avg10"
    | Some value ->
      match System.Double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
      | true, percent -> Measured percent
      | false, _ -> NotMeasured (sprintf "avg10 is not a number: %s" value)

let bytesPerKibibyte = 1024L

/// `MemAvailable:   45123456 kB` of /proc/meminfo, in bytes.
let parseMemAvailable (text: string) : MemoryReading =
  let line = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries) |> Array.tryFind (fun l -> l.StartsWith "MemAvailable:")
  match line with
  | None -> Unreadable "no MemAvailable line in meminfo"
  | Some l ->
    match l.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) with
    | [| _; kib; "kB" |] ->
      match System.Int64.TryParse kib with
      | true, n -> AvailableBytes (n * bytesPerKibibyte)
      | false, _ -> Unreadable (sprintf "MemAvailable is not a number: %s" kib)
    | _ -> Unreadable (sprintf "unexpected MemAvailable line: %s" l)
