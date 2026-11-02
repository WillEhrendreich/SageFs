namespace SageFs.Simulation

open System
open SageFs
open SageFs.WorkerProtocol

/// DST for #179: a hard reset of a session whose startup profile binds the app's port at load.
///
/// WHAT IS FOLDED: the real `RespawnAfterReap.sequencingOf` (does this session wait for its old workers?), the real
/// `RespawnAfterReap.verdict` (may the parked replacement start yet?) and the real `WorkerReaper.start/finish/standing`
/// (which old workers are still exiting?). WHAT IS MODELED: the port. Every old worker holds it until the reaper
/// reports it gone, a worker that outlives its kill holds it for good, and a replacement's startup profile binds it
/// at the instant the replacement is spawned. The mailbox loop around these functions is a closure over
/// `MailboxProcessor` and cannot be folded, so `SessionManagerRespawnAfterReapTests` covers the wiring and this sim
/// covers the schedules: every order the one or two old workers can leave in, with a Stop landing anywhere.
///
/// Chaos is data: a `Scenario` is a seed and the ordered reaper reports it produced, so the same seed replays the
/// identical run forever. No clock, no IO, no waits.
///
/// Three twins reintroduce the defects the invariants pin: `twinSpawnFirst` (the order before the fix, the replacement
/// starts while the old worker serves), `twinFirstLeave` (the replacement starts when ANY old worker has left) and
/// `twinIgnoresStop` (a stopped session's parked replacement still starts).
module RespawnAfterReapSim =

  [<RequireQualifiedAccess>]
  type Event =
    /// The reaper reports one old worker: its pid, and whether it outlived its stop and its kill.
    | WorkerLeft of pid: int * survived: bool
    /// The session is stopped (removed from the registry).
    | Stop

  type Scenario =
    { Seed: int
      /// The session has a startup profile that binds a fixed port.
      HasProfile: bool
      /// The workers holding the port when the reset is accepted: the registered one, and a pending swap's.
      OldWorkers: int list
      Events: Event list }

  /// One replacement worker started: after how many events, which pids held the port, and whether its profile bound it.
  type Spawn =
    { AfterEvents: int
      Holders: int list
      Bound: bool }

  type Trace =
    { Scenario: Scenario
      Spawns: Spawn list
      Reducer: string }

  type Strategy =
    { Name: string
      Sequencing: StartupProfile.InitScript option -> RespawnAfterReap.Sequencing
      Verdict: SessionId -> WorkerReaper.Standing -> RespawnAfterReap.Book<string> -> RespawnAfterReap.Verdict<string> * RespawnAfterReap.Book<string>
      StopDropsParked: bool }

  let real : Strategy =
    { Name = "real"
      Sequencing = RespawnAfterReap.sequencingOf
      Verdict = RespawnAfterReap.verdict
      StopDropsParked = true }

  /// The order before #179: the replacement starts at once, whatever the profile says.
  let twinSpawnFirst : Strategy =
    { real with Name = "twin-spawn-first"; Sequencing = fun _ -> RespawnAfterReap.Sequencing.SpawnFirst }

  /// Starts the replacement when the first old worker leaves, without asking whether any other is still exiting.
  let twinFirstLeave : Strategy =
    { real with
        Name = "twin-first-leave"
        Verdict =
          fun id _ book ->
            RespawnAfterReap.verdict id (WorkerReaper.Standing.Done WorkerReaper.Settled.AllGone) book }

  /// A Stop leaves the parked replacement on the book.
  let twinIgnoresStop : Strategy =
    { real with Name = "twin-ignores-stop"; StopDropsParked = false }

  let private session =
    match SessionId.validate "0badcafe" with
    | Result.Ok id -> id
    | Result.Error reason -> failwithf "the sim's fixed session id is invalid: %s" reason
  let private script : StartupProfile.InitScript = { Folder = "/sim"; Path = "/sim/.SageFs/init.fsx" }

  type private State =
    { Reaping: WorkerReaper.Book<unit>
      Parked: RespawnAfterReap.Book<string>
      /// Old workers still holding the port.
      Holders: Set<int>
      Spawns: Spawn list
      Seen: int }

  let private spawnNow (scenario: Scenario) (state: State) : State =
    let bound =
      match scenario.HasProfile with
      | true -> Set.isEmpty state.Holders
      | false -> true
    { state with Spawns = state.Spawns @ [ { AfterEvents = state.Seen; Holders = Set.toList state.Holders; Bound = bound } ] }

  let private step (strategy: Strategy) (scenario: Scenario) (state: State) (event: Event) : State =
    let state = { state with Seen = state.Seen + 1 }
    match event with
    | Event.Stop ->
      match strategy.StopDropsParked with
      | true -> { state with Parked = RespawnAfterReap.drop session state.Parked }
      | false -> state
    | Event.WorkerLeft (pid, survived) ->
      let fate = match survived with true -> WorkerReaper.Fate.Outlived "sim" | false -> WorkerReaper.Fate.Left
      let holders = match survived with true -> state.Holders | false -> Set.remove pid state.Holders
      let reaping, _ = WorkerReaper.finish session pid fate state.Reaping
      let verdict, parked = strategy.Verdict session (WorkerReaper.standing session reaping) state.Parked
      let next = { state with Reaping = reaping; Parked = parked; Holders = holders }
      match verdict with
      | RespawnAfterReap.Verdict.NothingParked
      | RespawnAfterReap.Verdict.StillWaiting _ -> next
      | RespawnAfterReap.Verdict.SpawnNow _
      | RespawnAfterReap.Verdict.SpawnDespiteSurvivors _ -> spawnNow scenario next

  let runWith (strategy: Strategy) (scenario: Scenario) : Trace =
    let startup = if scenario.HasProfile then Some script else None
    let opening =
      { Reaping = WorkerReaper.empty
        Parked = RespawnAfterReap.empty
        Holders = Set.ofList scenario.OldWorkers
        Spawns = []
        Seen = 0 }
    let accepted =
      match strategy.Sequencing startup with
      | RespawnAfterReap.Sequencing.ReapThenSpawn _ ->
        { opening with
            Reaping = WorkerReaper.start session scenario.OldWorkers opening.Reaping
            Parked = RespawnAfterReap.park session "replacement" opening.Parked }
      | RespawnAfterReap.Sequencing.SpawnFirst -> spawnNow scenario opening
    let final = scenario.Events |> List.fold (step strategy scenario) accepted
    { Scenario = scenario; Spawns = final.Spawns; Reducer = strategy.Name }

  let run (scenario: Scenario) : Trace = runWith real scenario

module RespawnAfterReapInvariants =
  open RespawnAfterReapSim

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private survivors (scenario: Scenario) : bool =
    scenario.Events |> List.exists (function Event.WorkerLeft (_, true) -> true | Event.WorkerLeft _ | Event.Stop -> false)

  let private stopIndex (scenario: Scenario) : int option =
    scenario.Events |> List.tryFindIndex (function Event.Stop -> true | Event.WorkerLeft _ -> false)

  /// The index, in events, after which every old worker has been reported (None if some report never comes).
  let private allReportedAfter (scenario: Scenario) : int option =
    let reported =
      scenario.Events
      |> List.mapi (fun i e -> i, e)
      |> List.choose (function i, Event.WorkerLeft (pid, _) -> Some (pid, i + 1) | _, Event.Stop -> None)
    match scenario.OldWorkers |> List.forall (fun pid -> reported |> List.exists (fun (p, _) -> p = pid)) with
    | true -> Some (reported |> List.map snd |> List.max)
    | false -> None

  let private verdictOf (trace: Trace) (what: string) (failed: bool) : Outcome =
    match failed with
    | true -> Outcome.Violated (sprintf "%s (seed=%d, reducer=%s, scenario=%A, spawns=%A)" what trace.Scenario.Seed trace.Reducer trace.Scenario trace.Spawns)
    | false -> Outcome.Holds

  /// The replacement's profile binds the port. With every old worker gone it binds, which is the whole point of #179.
  let bindSucceeds : Invariant =
    { Id = "bind-succeeds"
      Description = "A replacement started for a session with a startup profile finds the app's port free, when no old worker outlived its kill."
      Check = fun t ->
        let failed = t.Scenario.HasProfile && not (survivors t.Scenario) && t.Spawns |> List.exists (fun s -> not s.Bound)
        verdictOf t "a replacement's profile lost the bind to an old worker" failed }

  /// Independent of the fold: the replacement may not start before the last old worker has been reported.
  let noSpawnWhileAnOldWorkerLives : Invariant =
    { Id = "no-spawn-while-an-old-worker-lives"
      Description = "With a startup profile, the replacement starts only after every old worker has been reported gone."
      Check = fun t ->
        let failed =
          match t.Scenario.HasProfile, allReportedAfter t.Scenario with
          | true, Some last -> t.Spawns |> List.exists (fun s -> s.AfterEvents < last)
          | true, None -> not (List.isEmpty t.Spawns)
          | false, _ -> false
        verdictOf t "the replacement started while an old worker was still exiting" failed }

  /// One reset starts one replacement, unless the session was stopped first, and never a second.
  let oneReplacementPerReset : Invariant =
    { Id = "one-replacement-per-reset"
      Description = "A reset starts exactly one replacement, or none when a Stop lands before the last old worker is reported."
      Check = fun t ->
        let expected =
          match t.Scenario.HasProfile, stopIndex t.Scenario, allReportedAfter t.Scenario with
          | false, _, _ -> 1
          | true, Some stopAt, Some last when stopAt < last -> 0
          | true, _, Some _ -> 1
          | true, _, None -> 0
        verdictOf t (sprintf "expected %d replacement(s)" expected) (List.length t.Spawns <> expected) }

  /// A session with no startup profile keeps spawn-first: the replacement starts at once, with the old workers serving.
  let noProfileKeepsSpawnFirst : Invariant =
    { Id = "no-profile-keeps-spawn-first"
      Description = "A session with no startup profile starts its replacement at the reset, before any old worker leaves."
      Check = fun t ->
        let failed =
          not t.Scenario.HasProfile
          && (match t.Spawns with [ s ] -> s.AfterEvents <> 0 | _ -> true)
        verdictOf t "a profile-less session did not spawn first" failed }

  let all : Invariant list = [ bindSucceeds; noSpawnWhileAnOldWorkerLives; oneReplacementPerReset; noProfileKeepsSpawnFirst ]

  let violations (trace: Trace) : (string * string) list =
    all |> List.choose (fun i -> match i.Check trace with Outcome.Violated m -> Some (i.Id, m) | Outcome.Holds -> None)

module RespawnAfterReapGenerators =
  open RespawnAfterReapSim

  /// A scenario from a seed: whether there is a profile, one or two old workers, the order they are reported in, one
  /// in eight reports being a worker that outlived its kill, and a Stop landing at a random place a quarter of the time.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let hasProfile = rnd.Next 4 <> 0
    let workers = [ 100 .. 100 + rnd.Next(0, 2) ]
    let order = workers |> List.sortBy (fun _ -> rnd.Next())
    let reports = order |> List.map (fun pid -> Event.WorkerLeft (pid, rnd.Next 8 = 0))
    let events =
      match rnd.Next 4 with
      | 0 -> reports |> List.insertAt (rnd.Next(0, reports.Length + 1)) Event.Stop
      | _ -> reports
    { Seed = seed; HasProfile = hasProfile; OldWorkers = workers; Events = events }
