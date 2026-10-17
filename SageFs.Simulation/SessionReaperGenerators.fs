namespace SageFs.Simulation

open System
open SageFs.Simulation.SessionReaperSim

/// Seeded, dependency-free generators for the reaper's histories. Chaos is data: `fromSeed n` replays identically forever.
module SessionReaperGenerators =

  /// Jumps either side of the bound (720 minutes), so a history crosses it, stops just short of it, and sits well past it.
  let private jumps = [| 1; 30; 180; 600; 719; 720; 721; 800; 1500 |]

  let private op (rnd: Random) : Op =
    let session = rnd.Next(1, 5)
    let owner = rnd.Next(1, 4)
    match rnd.Next 20 with
    | 0 | 1 | 2 | 3 | 4 | 5 -> Op.Advance jumps.[rnd.Next jumps.Length]
    | 6 | 7 -> Op.AgentCreates(session, owner)
    | 8 -> Op.PersonCreates session
    | 9 | 10 -> Op.OwnerArrives owner
    | 11 | 12 -> Op.OwnerLeaves owner
    | 13 | 14 -> Op.Use session
    | 15 -> Op.RestartDaemon
    | _ -> Op.Sweep

  /// A general history: 4-30 ops. Same seed, identical list.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let n = rnd.Next(4, 31)
    { Seed = seed; Ops = [ for _ in 1 .. n -> op rnd ] }

  // Named minimal scenarios (the exact replays the tests assert on)

  /// An agent makes a session and leaves; a bound and a minute later the sweep stops it.
  let abandonedIsStopped : Scenario =
    { Seed = 400; Ops = [ Op.AgentCreates(1, 1); Op.OwnerLeaves 1; Op.Sweep; Op.Advance 721; Op.Sweep ] }

  /// A person makes a session and nobody touches it for days.
  let aPersonsSessionIsNeverStopped : Scenario =
    { Seed = 401; Ops = [ Op.PersonCreates 1; Op.Advance 1500; Op.Sweep; Op.Advance 1500; Op.Sweep ] }

  /// The owner is here the whole time, though the session sits idle for days.
  let aConnectedOwnerKeepsItsSession : Scenario =
    { Seed = 402; Ops = [ Op.AgentCreates(1, 1); Op.Advance 1500; Op.Sweep; Op.Advance 1500; Op.Sweep ] }

  /// The owner leaves, the daemon restarts, and the sweep comes right after: a restart must not make the session look
  /// abandoned for days.
  let aRestartDoesNotMakeItLookAbandoned : Scenario =
    { Seed = 403; Ops = [ Op.AgentCreates(1, 1); Op.OwnerLeaves 1; Op.Advance 1500; Op.RestartDaemon; Op.Sweep ] }

  /// After the restart the bound passes in full, and then the session is stopped.
  let aRestartDelaysTheReapByTheBound : Scenario =
    { Seed = 404; Ops = [ Op.AgentCreates(1, 1); Op.OwnerLeaves 1; Op.Advance 1500; Op.RestartDaemon; Op.Advance 721; Op.Sweep ] }

  /// The owner is seen at a sweep, then leaves: the absence is counted from that sweep, not from the session's creation.
  let aVisitRestartsTheAbsence : Scenario =
    { Seed = 405; Ops = [ Op.AgentCreates(1, 1); Op.Advance 1500; Op.Sweep; Op.OwnerLeaves 1; Op.Advance 600; Op.Sweep ] }

  /// Used a minute before the sweep: gone for days, but not idle.
  let aSessionInUseIsKept : Scenario =
    { Seed = 406; Ops = [ Op.AgentCreates(1, 1); Op.OwnerLeaves 1; Op.Advance 1500; Op.Advance 1500; Op.Use 1; Op.Advance 1; Op.Sweep ] }
