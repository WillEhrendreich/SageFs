namespace SageFs

open System
open SageFs.Utils

/// What this machine has taught SageFs about how long a start takes, kept between runs.
///
/// Every worker start that reaches its port, and every session that reaches Ready, is recorded, and the
/// profile in the data directory is rewritten, so the next daemon's first attempt is already as patient
/// as this machine has shown it needs to be. A record of two functions, like the rest of the session
/// manager's runtime, so a test hands the manager a ledger of its own and never touches the process's.
type Ledger =
  { /// What is known of a stage here.
    History: StartStage -> StageHistory
    /// Fold one observed duration in, and rewrite the profile.
    Record: StartStage -> TimeSpan -> unit }

module StartLedger =

  /// A ledger that knows nothing and writes nothing: a worker, a test, a process that is not the daemon.
  let closed : Ledger =
    { History = fun _ -> StageHistory.NeverSeen
      Record = fun _ _ -> () }

  /// The profile a daemon starts from: the one on disk if there is one (it holds what was learned),
  /// otherwise a fresh one built from the probe that settled the tier, and when there was no probe
  /// (the tier was forced) one taken now.
  let baseProfile (dataDir: string) (resolution: TierResolution) : MachineProfile =
    match MachineProbeReader.readProfile dataDir with
    | ProfileRead.Found (_, profile) -> profile
    | ProfileRead.NoProfile
    | ProfileRead.Unreadable _ ->
      match resolution.Source with
      | TierSource.Probed probe
      | TierSource.FromProfile (_, probe) -> MachineProfile.ofProbe probe
      | TierSource.Forced
      | TierSource.ProbeFailed _ ->
        match MachineProbeReader.read dataDir with
        | Ok probe -> MachineProfile.ofProbe probe
        | Error (MachineProbeError.CouldNotProbe reason) ->
          MachineProfile.ofProbe
            { LogicalCores = Environment.ProcessorCount
              CpuQuota = CpuQuota.Unlimited
              TotalMemoryMb = 0L
              AvailableMemoryMb = 0L
              Storage = StorageKind.Unknown
              Calibration = Calibration.NotMeasured reason }

  /// A ledger on `profile`, kept in `dataDir`. A write that fails is logged and does not matter: what
  /// was learned is still in memory, and the next run learns it again.
  let openAt (dataDir: string) (profile: MachineProfile) : Ledger =
    let gate = obj ()
    let current = ref profile
    { History = fun stage -> lock gate (fun () -> MachineProfile.stage stage current.Value)
      Record =
        fun stage took ->
          lock gate (fun () ->
            current.Value <- MachineProfile.observe stage took.TotalMilliseconds current.Value
            match MachineProbeReader.writeProfile dataDir current.Value with
            | Ok () -> ()
            | Error (ProfileWriteError.CouldNotWrite (path, reason)) -> Log.warn "[MachineProfile] could not write the machine profile %s: %s" path reason) }

  /// What this machine takes from spawning a worker to a session that can evaluate. A process that has already
  /// gone has no start time to read; then there is nothing to learn.
  let recordReady (ledger: Ledger) (worker: System.Diagnostics.Process) : unit =
    try ledger.Record StartStage.WorkerReady (DateTime.UtcNow - worker.StartTime.ToUniversalTime())
    with _ -> ()

  let mutable private installed : Ledger = closed

  /// Make `ledger` the process's own: what `shared` reads and writes. The daemon does this once, at start.
  let install (ledger: Ledger) : unit =
    System.Threading.Volatile.Write(&installed, ledger)

  /// The process's ledger: whatever was installed, or `closed`. Every default runtime holds this one value,
  /// which reads the installed ledger at the moment it is used, so installing after a manager exists works.
  let shared : Ledger =
    { History = fun stage -> (System.Threading.Volatile.Read(&installed)).History stage
      Record = fun stage took -> (System.Threading.Volatile.Read(&installed)).Record stage took }
