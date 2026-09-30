namespace SageFs

open System
open System.Threading
open SageFs.Utils

/// Tells somebody when the session manager's own loop is in trouble.
///
/// The mailbox loop supervises itself (per-command `superviseStep`, and an
/// outer `supervise` that restarts from the last good state), but until now
/// nothing alerted anyone: errors only bumped a counter, and a WEDGED loop was
/// invisible. A handler that blocks stops the mailbox draining while reads
/// keep answering from the lock-free snapshot, so callers of PostAndAsyncReply
/// hang and status looks fine.
///
/// `decide` is the pure core (what the DST in SageFs.Simulation folds). `Beat`
/// is the thin shell the loop stamps and a dedicated watchdog thread reads.
module SupervisorWatchdog =

  /// What the supervisor has to say. A closed set: the callback and the health
  /// text match on it exhaustively.
  [<RequireQualifiedAccess>]
  type SupervisorAlarm =
    /// The loop itself threw and was restarted from the last good state.
    /// `count` is how many times so far.
    | LoopRestarted of count: int
    /// A command's handler threw; the loop carried on without it.
    | CommandFailed of command: string
    /// A command has held the loop for longer than the bound.
    | Wedged of command: string * since: DateTime

  [<RequireQualifiedAccess>]
  type SupervisorHealth =
    | Healthy
    | Degraded of reason: string

  /// What the loop is doing right now, stamped by the loop itself.
  [<RequireQualifiedAccess>]
  type Activity =
    | Idle
    | Running of sequence: int64 * command: string * since: DateTime

  /// Which running command has already been reported wedged, so one wedge is
  /// one alarm, not one per tick.
  [<RequireQualifiedAccess>]
  type Watch =
    | NotAlarmed
    | Alarmed of sequence: int64

  [<RequireQualifiedAccess>]
  type Tick =
    | Quiet
    | Raise of SupervisorAlarm

  /// How long a command may hold the loop, and how often the watchdog looks.
  type Settings =
    { WedgeAfter: TimeSpan
      CheckEvery: TimeSpan }

  let defaultSettings : Settings =
    { WedgeAfter = Timeouts.supervisorWedgeAfter
      CheckEvery = Timeouts.supervisorCheckInterval }

  /// One watchdog tick. A command running longer than `bound` is reported once;
  /// detection is within `bound` plus the check interval. An idle loop is never
  /// wedged, however long it has been idle.
  let decide (bound: TimeSpan) (now: DateTime) (watch: Watch) (activity: Activity) : Tick * Watch =
    match activity with
    | Activity.Idle -> Tick.Quiet, Watch.NotAlarmed
    | Activity.Running(sequence, command, since) ->
      match watch with
      | Watch.Alarmed alarmed when alarmed = sequence -> Tick.Quiet, watch
      | Watch.Alarmed _
      | Watch.NotAlarmed ->
        match now - since > bound with
        | true -> Tick.Raise (SupervisorAlarm.Wedged (command, since)), Watch.Alarmed sequence
        | false -> Tick.Quiet, Watch.NotAlarmed

  /// What a person reading the dashboard or status is told.
  let describe (alarm: SupervisorAlarm) : string =
    match alarm with
    | SupervisorAlarm.LoopRestarted count ->
      sprintf "The session manager's loop has restarted %d time(s) after an unexpected fault. → Check the daemon log for 'Mailbox loop threw unexpectedly'." count
    | SupervisorAlarm.CommandFailed command ->
      sprintf "The session manager failed while handling %s. → Check the daemon log." command
    | SupervisorAlarm.Wedged(command, since) ->
      sprintf "The session manager has been stuck on %s since %s UTC, so session commands hang while reads still answer. → Restart the daemon if this does not clear." command (since.ToString "HH:mm:ss")

  /// The loop's stamp, shared with the watchdog thread. Every mutation is under
  /// one lock so a check sees the loop as it is at that instant; callbacks run
  /// outside the lock and a throwing callback never reaches the loop.
  type Beat(clock: unit -> DateTime, settings: Settings, onAlarm: SupervisorAlarm -> unit, onHealth: SupervisorHealth -> unit) =
    let gate = obj ()
    let mutable sequence = 0L
    let mutable activity = Activity.Idle
    let mutable watch = Watch.NotAlarmed
    let mutable health = SupervisorHealth.Healthy
    let mutable loopRestarts = 0

    let guarded (what: string) (action: unit -> unit) =
      try action ()
      with ex -> Log.warn "[SupervisorWatchdog] %s callback threw (ignored): %s" what ex.Message

    /// Tell the callbacks once the lock is released. Health goes first, so whoever
    /// hears an alarm and then reads the snapshot sees it already degraded.
    let publish (alarm: SupervisorAlarm option) (changed: SupervisorHealth option) =
      changed |> Option.iter (fun h -> guarded "health" (fun () -> onHealth h))
      alarm |> Option.iter (fun a -> guarded "alarm" (fun () -> onAlarm a))

    let degrade (alarm: SupervisorAlarm) : SupervisorHealth option =
      let next = SupervisorHealth.Degraded (describe alarm)
      match next = health with
      | true -> None
      | false ->
        health <- next
        Some next

    member _.Health : SupervisorHealth = lock gate (fun () -> health)

    /// The loop is about to run `command`.
    member _.BeginCommand(command: string) : unit =
      lock gate (fun () ->
        sequence <- sequence + 1L
        activity <- Activity.Running(sequence, command, clock ()))

    /// The command finished without throwing. The loop is healthy again.
    member _.CommandSucceeded() : unit =
      let changed =
        lock gate (fun () ->
          activity <- Activity.Idle
          match health with
          | SupervisorHealth.Healthy -> None
          | SupervisorHealth.Degraded _ ->
            health <- SupervisorHealth.Healthy
            Some SupervisorHealth.Healthy)
      publish None changed

    /// The command's handler threw and the loop carried on.
    member _.CommandFailed(command: string) : unit =
      let alarm = SupervisorAlarm.CommandFailed command
      let changed = lock gate (fun () -> activity <- Activity.Idle; degrade alarm)
      publish (Some alarm) changed

    /// The loop itself threw and is being restarted from the last good state.
    member _.LoopRestarted() : unit =
      let alarm, changed =
        lock gate (fun () ->
          activity <- Activity.Idle
          loopRestarts <- loopRestarts + 1
          let alarm = SupervisorAlarm.LoopRestarted loopRestarts
          alarm, degrade alarm)
      publish (Some alarm) changed

    /// One watchdog look at the loop.
    member _.Check() : unit =
      let alarm, changed =
        lock gate (fun () ->
          let tick, nextWatch = decide settings.WedgeAfter (clock ()) watch activity
          watch <- nextWatch
          match tick with
          | Tick.Quiet -> None, None
          | Tick.Raise alarm -> Some alarm, degrade alarm)
      publish alarm changed

    /// Look at the loop every `CheckEvery` until cancelled, on a dedicated
    /// thread: the watchdog must not queue behind the pool work a wedged
    /// handler may be starving.
    member this.Run(ct: CancellationToken) : unit =
      let watchLoop () =
        try
          while not (ct.WaitHandle.WaitOne settings.CheckEvery) do
            this.Check()
        // The token's source was disposed after cancelling: the daemon is gone.
        with :? ObjectDisposedException -> ()
      WorkerSpawn.runOnDedicatedThread "sagefs-supervisor-watchdog" watchLoop |> ignore
