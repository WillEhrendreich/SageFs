namespace SageFs.Simulation

open System
open SageFs.SupervisorWatchdog

/// Deterministic Simulation Testing (DST) for the session manager's wedge
/// watchdog (`SageFs.Core/SupervisorWatchdog.fs`).
///
/// THE FAILURE: a handler that blocks stops the mailbox draining while reads
/// still answer from the lock-free snapshot, so callers of PostAndAsyncReply
/// hang and status looks fine. Nothing noticed.
///
/// THE SUBJECT is the REAL `SupervisorWatchdog.decide`, folded tick by tick
/// against a virtual clock. There is no sleeping and no thread: a scenario is a
/// seed, a bound, a check interval and the serial list of commands the loop was
/// given, each with a start time and a run length (`None` = never returns, the
/// wedged StopWorker). Same seed, same trace, forever.
///
/// Two TWINS prove the invariants can fail. `runNeverAlarms` is the status quo
/// (nobody is told). `runAlarmEveryTick` is the noisy opposite: it ignores the
/// bound and the dedup, so it pages on a healthy loop and again on every tick.
module SupervisorWatchdogSim =

  /// One command the loop was handed. The loop is serial, so commands never
  /// overlap; `RunsFor = None` is a handler that never returns.
  type Command =
    { Name: string
      StartsAt: TimeSpan
      RunsFor: TimeSpan option }

  type Scenario =
    { Seed: int
      Bound: TimeSpan
      CheckEvery: TimeSpan
      Horizon: TimeSpan
      Commands: Command list }

  /// One alarm the fold raised: when, and what.
  type Raised =
    { At: TimeSpan
      Alarm: SupervisorAlarm
      Sequence: int64 }

  type Trace =
    { Scenario: Scenario
      Raised: Raised list
      Reducer: string }

  let epoch = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

  /// What the loop is doing at `t`: the command whose run covers it, if any.
  let activityAt (commands: Command list) (t: TimeSpan) : Activity =
    commands
    |> List.indexed
    |> List.tryPick (fun (i, c) ->
      let stillRunning =
        match c.RunsFor with
        | None -> true
        | Some length -> t < c.StartsAt + length
      match t >= c.StartsAt && stillRunning with
      | true -> Some (Activity.Running (int64 (i + 1), c.Name, epoch + c.StartsAt))
      | false -> None)
    |> Option.defaultValue Activity.Idle

  /// The instants the watchdog looks at the loop.
  let tickTimes (s: Scenario) : TimeSpan list =
    [ for i in 1 .. int (s.Horizon / s.CheckEvery) -> s.CheckEvery * float i ]

  /// A decision function shaped like `SupervisorWatchdog.decide`.
  type Decide = TimeSpan -> DateTime -> Watch -> Activity -> Tick * Watch

  let private runWith (name: string) (decide: Decide) (s: Scenario) : Trace =
    let step (watch, raised) t =
      let activity = activityAt s.Commands t
      let tick, watch' = decide s.Bound (epoch + t) watch activity
      match tick, activity with
      | Tick.Raise alarm, Activity.Running(sequence, _, _) ->
        watch', { At = t; Alarm = alarm; Sequence = sequence } :: raised
      | Tick.Raise alarm, Activity.Idle -> watch', { At = t; Alarm = alarm; Sequence = 0L } :: raised
      | Tick.Quiet, _ -> watch', raised
    let _, raised = tickTimes s |> List.fold step (Watch.NotAlarmed, [])
    { Scenario = s; Raised = List.rev raised; Reducer = name }

  /// Run through the real decision.
  let run (s: Scenario) : Trace = runWith "real SupervisorWatchdog.decide" decide s

  /// TWIN: the status quo. Nobody is ever told.
  let runNeverAlarms (s: Scenario) : Trace =
    runWith "twin: never alarms" (fun _ _ watch _ -> Tick.Quiet, watch) s

  /// TWIN: pages on every tick the loop is busy, bound and dedup ignored.
  let runAlarmEveryTick (s: Scenario) : Trace =
    let noisy : Decide =
      fun _ _ watch activity ->
        match activity with
        | Activity.Idle -> Tick.Quiet, watch
        | Activity.Running(_, command, since) -> Tick.Raise (SupervisorAlarm.Wedged (command, since)), watch
    runWith "twin: alarms on every busy tick" noisy s

  // ── Seeded scenarios ──────────────────────────────────────────────────

  /// Commands laid end to end with gaps. Short and medium runs finish; the last
  /// one may never return. A pure function of the seed.
  let fromSeed (seed: int) : Scenario =
    let rnd = Random(seed)
    let bound = TimeSpan.FromSeconds 30.0
    let checkEvery = TimeSpan.FromSeconds (float (rnd.Next(1, 6)))
    let count = rnd.Next(1, 8)
    let mutable clock = TimeSpan.FromSeconds (float (rnd.Next(0, 10)))
    let commands = ResizeArray<Command>()
    for i in 1 .. count do
      let length =
        match rnd.Next(0, 10) with
        | 0 | 1 | 2 | 3 | 4 -> Some (TimeSpan.FromMilliseconds (float (rnd.Next(1, 5000))))
        | 5 | 6 | 7 -> Some (TimeSpan.FromSeconds (float (rnd.Next(5, 29))))
        | 8 -> Some (TimeSpan.FromSeconds (float (rnd.Next(31, 120))))
        | _ when i = count -> None
        | _ -> Some (TimeSpan.FromSeconds (float (rnd.Next(31, 120))))
      let start = clock
      commands.Add { Name = sprintf "Cmd%d" i; StartsAt = start; RunsFor = length }
      clock <-
        match length with
        | Some l -> start + l + TimeSpan.FromSeconds (float (rnd.Next(0, 10)))
        | None -> start
    { Seed = seed
      Bound = bound
      CheckEvery = checkEvery
      Horizon = clock + TimeSpan.FromSeconds 200.0
      Commands = List.ofSeq commands }

  // ── Named worked scenarios ────────────────────────────────────────────

  let private secs (n: float) = TimeSpan.FromSeconds n

  /// StopWorker never returns: the canonical wedge.
  let stopWorkerNeverReturns : Scenario =
    { Seed = -501
      Bound = secs 30.0
      CheckEvery = secs 2.0
      Horizon = secs 120.0
      Commands = [ { Name = "StopSession"; StartsAt = secs 5.0; RunsFor = None } ] }

  /// Busy, but nothing ever runs longer than the bound: the control that shows
  /// a healthy loop is not paged.
  let busyButHealthy : Scenario =
    { Seed = -502
      Bound = secs 30.0
      CheckEvery = secs 2.0
      Horizon = secs 120.0
      Commands =
        [ { Name = "CreateSession"; StartsAt = secs 1.0; RunsFor = Some (secs 20.0) }
          { Name = "RestartSession"; StartsAt = secs 25.0; RunsFor = Some (secs 25.0) }
          { Name = "StopAll"; StartsAt = secs 60.0; RunsFor = Some (secs 29.0) } ] }

  /// A wedge that clears, then a second, different wedge: two alarms, one each.
  let twoSeparateWedges : Scenario =
    { Seed = -503
      Bound = secs 30.0
      CheckEvery = secs 3.0
      Horizon = secs 200.0
      Commands =
        [ { Name = "StopSession"; StartsAt = secs 2.0; RunsFor = Some (secs 50.0) }
          { Name = "RestartSession"; StartsAt = secs 60.0; RunsFor = Some (secs 70.0) } ] }
