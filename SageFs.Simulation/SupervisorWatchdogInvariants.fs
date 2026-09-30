namespace SageFs.Simulation

open System
open SageFs.SupervisorWatchdog
open SageFs.Simulation.SupervisorWatchdogSim

/// Named invariants over a wedge-watchdog `Trace`. The oracle never calls
/// `decide`: it only reads the scenario's own commands and the raised alarms,
/// so it is an external check and not a restatement of the subject. It HOLDS
/// for the real decision; `runNeverAlarms` violates the first invariant and
/// `runAlarmEveryTick` violates the second and third.
module SupervisorWatchdogInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// How long a command ran by `t` (its whole run if it had finished).
  let private ranBy (c: Command) (t: TimeSpan) : TimeSpan =
    match c.RunsFor with
    | Some length -> min length (t - c.StartsAt)
    | None -> t - c.StartsAt

  let private wedgedNames (t: Trace) : string list =
    t.Raised
    |> List.choose (fun r ->
      match r.Alarm with
      | SupervisorAlarm.Wedged(command, _) -> Some command
      | SupervisorAlarm.LoopRestarted _
      | SupervisorAlarm.CommandFailed _ -> None)

  /// wedge-detected-within-bound: a command that held the loop past the bound
  /// plus one check interval, with the horizon still ahead, was reported wedged
  /// no later than that.
  let wedgeDetectedWithinBound : Invariant =
    { Id = "wedge-detected-within-bound"
      Description = "Every command that runs longer than bound + one check interval is alarmed Wedged by then."
      Check = fun t ->
        let s = t.Scenario
        let deadline (c: Command) = c.StartsAt + s.Bound + s.CheckEvery
        t.Scenario.Commands
        |> List.tryPick (fun c ->
          let ranPastDeadline =
            match c.RunsFor with
            | None -> deadline c <= s.Horizon
            | Some length -> c.StartsAt + length > deadline c && deadline c <= s.Horizon
          let alarmedInTime =
            t.Raised
            |> List.exists (fun r ->
              match r.Alarm with
              | SupervisorAlarm.Wedged(command, _) -> command = c.Name && r.At <= deadline c
              | SupervisorAlarm.LoopRestarted _
              | SupervisorAlarm.CommandFailed _ -> false)
          match ranPastDeadline && not alarmedInTime with
          | true -> Some (sprintf "%s ran past %O (bound %O + check %O) and was not alarmed Wedged by then" c.Name (deadline c) s.Bound s.CheckEvery)
          | false -> None)
        |> function
           | Some message -> Outcome.Violated message
           | None -> Outcome.Holds }

  /// no-false-alarm: a Wedged alarm only ever names a command that really had
  /// held the loop longer than the bound when it was raised.
  let noFalseAlarm : Invariant =
    { Id = "no-false-alarm"
      Description = "A Wedged alarm is only raised for a command that has run longer than the bound."
      Check = fun t ->
        t.Raised
        |> List.tryPick (fun r ->
          match r.Alarm with
          | SupervisorAlarm.Wedged(command, _) ->
            let c = t.Scenario.Commands |> List.find (fun c -> c.Name = command)
            match ranBy c r.At > t.Scenario.Bound with
            | true -> None
            | false -> Some (sprintf "%s was alarmed Wedged at %O after running only %O (bound %O)" command r.At (ranBy c r.At) t.Scenario.Bound)
          | SupervisorAlarm.LoopRestarted _
          | SupervisorAlarm.CommandFailed _ -> None)
        |> function
           | Some message -> Outcome.Violated message
           | None -> Outcome.Holds }

  /// one-alarm-per-wedge: one stuck command is one alarm, not one per tick.
  let oneAlarmPerWedge : Invariant =
    { Id = "one-alarm-per-wedge"
      Description = "No command is alarmed Wedged more than once."
      Check = fun t ->
        wedgedNames t
        |> List.countBy id
        |> List.tryFind (fun (_, n) -> n > 1)
        |> function
           | Some (command, n) -> Outcome.Violated (sprintf "%s was alarmed Wedged %d times" command n)
           | None -> Outcome.Holds }

  let all : Invariant list = [ wedgeDetectedWithinBound; noFalseAlarm; oneAlarmPerWedge ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated message -> Some (inv.Id, message))
