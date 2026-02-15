module SageFs.Tests.WatchdogTests

open System
open Expecto
open Expecto.Flip
open SageFs.Watchdog

let now = DateTime(2026, 2, 15, 0, 0, 0)

[<Tests>]
let watchdogDecisionTests = testList "Watchdog.decide" [
  test "no daemon running and never started → StartDaemon" {
    let state = emptyState now
    let action, _ = decide defaultConfig state DaemonStatus.NotRunning now
    match action with
    | Action.StartDaemon -> ()
    | other -> failtest (sprintf "expected StartDaemon, got %A" other)
  }

  test "daemon running → Wait" {
    let state = emptyState now |> recordStart 1234 now
    let action, _ = decide defaultConfig state DaemonStatus.Running (now.AddSeconds 10.0)
    match action with
    | Action.Wait -> ()
    | other -> failtest (sprintf "expected Wait, got %A" other)
  }

  test "daemon status unknown → Wait" {
    let state = emptyState now |> recordStart 1234 now
    let action, _ = decide defaultConfig state DaemonStatus.Unknown (now.AddSeconds 10.0)
    match action with
    | Action.Wait -> ()
    | other -> failtest (sprintf "expected Wait, got %A" other)
  }

  test "daemon died within grace period → Wait" {
    let state = emptyState now |> recordStart 1234 now
    let action, _ = decide defaultConfig state DaemonStatus.NotRunning (now.AddSeconds 10.0)
    match action with
    | Action.Wait -> ()
    | other -> failtest (sprintf "expected Wait during grace period, got %A" other)
  }

  test "daemon died after grace period → RestartDaemon with backoff" {
    let state = emptyState now |> recordStart 1234 now
    let action, newState = decide defaultConfig state DaemonStatus.NotRunning (now.AddSeconds 60.0)
    match action with
    | Action.RestartDaemon delay ->
      Expect.equal "first restart delay is 1s" (TimeSpan.FromSeconds 1.0) delay
      Expect.equal "restart count is 1" 1 newState.RestartState.RestartCount
    | other -> failtest (sprintf "expected RestartDaemon, got %A" other)
  }

  test "successive crashes increase backoff" {
    let mutable state = emptyState now |> recordStart 1234 now
    let _action1, s1 = decide defaultConfig state DaemonStatus.NotRunning (now.AddSeconds 60.0)
    state <- s1 |> recordStart 5678 (now.AddSeconds 62.0)
    let action2, s2 = decide defaultConfig state DaemonStatus.NotRunning (now.AddSeconds 120.0)
    match action2 with
    | Action.RestartDaemon delay ->
      Expect.equal "second restart delay is 2s" (TimeSpan.FromSeconds 2.0) delay
      Expect.equal "restart count is 2" 2 s2.RestartState.RestartCount
    | other -> failtest (sprintf "expected RestartDaemon, got %A" other)
  }

  test "too many crashes → GiveUp" {
    let mutable state = emptyState now |> recordStart 1234 now
    for i in 1..5 do
      let _, s = decide defaultConfig state DaemonStatus.NotRunning (now.AddSeconds (float (i * 60)))
      state <- s |> recordStart (1000 + i) (now.AddSeconds (float (i * 60 + 2)))
    let action, _ = decide defaultConfig state DaemonStatus.NotRunning (now.AddSeconds 360.0)
    match action with
    | Action.GiveUp reason ->
      Expect.stringContains "mentions restart count" "5" reason
    | other -> failtest (sprintf "expected GiveUp, got %A" other)
  }

  test "recordStart updates PID and timestamp" {
    let state = emptyState now
    let updated = recordStart 9999 (now.AddSeconds 5.0) state
    Expect.equal "pid recorded" (Some 9999) updated.DaemonPid
    Expect.equal "start time recorded" (Some (now.AddSeconds 5.0)) updated.LastStartedAt
  }
]
