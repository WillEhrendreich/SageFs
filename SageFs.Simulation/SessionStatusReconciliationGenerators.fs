namespace SageFs.Simulation

open System
open SageFs.WorkerProtocol
open SageFs.Simulation.SessionStatusReconciliationSim

/// Seeded, dependency-free generators for `SessionStatusReconciliationSim`
/// scenarios (mirrors `WarmupGenerators`). Chaos is data: `run (fromSeed n)`
/// replays identically forever.
module SessionStatusReconciliationGenerators =

  let private handle = { Pid = 4242; Port = Some 5000 }

  let private randomStatus (rnd: Random) =
    match rnd.Next(0, 7) with
    | 0 -> SessionStatus.Starting
    | 1 -> SessionStatus.Ready
    | 2 -> SessionStatus.Evaluating
    | 3 -> SessionStatus.Building "restoring packages"
    | 4 -> SessionStatus.Faulted
    | 5 -> SessionStatus.Restarting
    | _ -> SessionStatus.Stopped

  /// A general single-run scenario: a short, varied mix of worker polls, a
  /// possible daemon-side fault/stop, and a possible restart. Same seed =>
  /// identical scenario, forever.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(1, 14)
    let events =
      [ for _ in 1 .. n ->
          match rnd.Next(0, 6) with
          | 0 | 1 | 2 -> Event.Poll (randomStatus rnd)
          | 3 -> Event.DaemonFault (Some (sprintf "seed-%d fault" seed))
          | 4 -> Event.DaemonStop
          | _ -> Event.Restarted handle ]
    { Seed = seed; Initial = SessionLifecycleStatus.Starting handle; Events = events }

  // ── Named minimal scenarios — the shapes named in the brief ────────────

  /// A fault during warmup on the hot reload path: the session is Starting,
  /// the daemon learns of a fault (a channel other than a status poll —
  /// WorkerExited, a spawn failure), and a status poll then arrives with a
  /// live-sounding reply — the exact shape the real incident took.
  let faultDuringWarmup : Scenario =
    { Seed = 1
      Initial = SessionLifecycleStatus.Starting handle
      Events =
        [ Event.Poll SessionStatus.Starting
          Event.DaemonFault (Some "worker crashed loading the hot-reload workflow")
          Event.Poll SessionStatus.Starting ] }

  /// A save racing a fault: an eval reply that would normally reconcile to
  /// Evaluating/Ready lands in the SAME window as a fault the daemon
  /// recorded through another channel.
  let saveRacingFault : Scenario =
    { Seed = 2
      Initial = SessionLifecycleStatus.Ready handle
      Events =
        [ Event.Poll SessionStatus.Evaluating
          Event.DaemonFault (Some "worker exited mid-eval")
          Event.Poll SessionStatus.Evaluating
          Event.Poll SessionStatus.Ready ] }

  /// A status poll racing both: a fault and a stop land back-to-back with
  /// stray poll replies interleaved on every side — proves ordering within
  /// a tight window can't produce two different final answers depending on
  /// who asks when.
  let statusPollRacingBoth : Scenario =
    { Seed = 3
      Initial = SessionLifecycleStatus.Evaluating handle
      Events =
        [ Event.Poll SessionStatus.Ready
          Event.DaemonFault (Some "race")
          Event.Poll SessionStatus.Starting
          Event.DaemonStop
          Event.Poll SessionStatus.Ready
          Event.Poll SessionStatus.Faulted ] }

  /// A genuine restart after a fault: the ONE way a terminal status is
  /// allowed to move — an explicit new session, never a stale poll.
  let restartAfterFault : Scenario =
    { Seed = 4
      Initial = SessionLifecycleStatus.Starting handle
      Events =
        [ Event.DaemonFault (Some "boom")
          Event.Poll SessionStatus.Starting
          Event.Restarted { handle with Pid = 9999 }
          Event.Poll SessionStatus.Ready ] }
