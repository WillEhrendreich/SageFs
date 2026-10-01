module SageFs.Tests.WatchdogTests

open System
open Expecto
open Expecto.Flip
open SageFs.Watchdog

module TestTimeouts = SageFs.Tests.TestInfrastructure.TestTimeouts

let now = DateTime(2026, 2, 15, 0, 0, 0)
let seed = SageFs.RestartPolicy.JitterSeed 1L

/// The restart delay the watchdog picks for a daemon that has crashed once, under a given seed.
let private firstRestartDelay (jitterSeed: SageFs.RestartPolicy.JitterSeed) : TimeSpan =
  let state = emptyState now |> recordStart 1234 now
  match decide defaultConfig jitterSeed state DaemonStatus.NotRunning (now.AddSeconds 60.0) with
  | Action.RestartDaemon delay, _ -> delay
  | other, _ -> failtestf "expected RestartDaemon, got %A" other

[<Tests>]
let watchdogDecisionTests = testList "Watchdog.decide" [
  test "no daemon running and never started → StartDaemon" {
    let state = emptyState now
    let action, _ = decide defaultConfig seed state DaemonStatus.NotRunning now
    match action with
    | Action.StartDaemon -> ()
    | other -> failtest (sprintf "expected StartDaemon, got %A" other)
  }

  test "daemon running → Wait" {
    let state = emptyState now |> recordStart 1234 now
    let action, _ = decide defaultConfig seed state DaemonStatus.Running (now.AddSeconds 10.0)
    match action with
    | Action.Wait -> ()
    | other -> failtest (sprintf "expected Wait, got %A" other)
  }

  test "daemon status unknown → Wait" {
    let state = emptyState now |> recordStart 1234 now
    let action, _ = decide defaultConfig seed state DaemonStatus.Unknown (now.AddSeconds 10.0)
    match action with
    | Action.Wait -> ()
    | other -> failtest (sprintf "expected Wait, got %A" other)
  }

  test "daemon died within grace period → Wait" {
    let state = emptyState now |> recordStart 1234 now
    let action, _ = decide defaultConfig seed state DaemonStatus.NotRunning (now.AddSeconds 10.0)
    match action with
    | Action.Wait -> ()
    | other -> failtest (sprintf "expected Wait during grace period, got %A" other)
  }

  test "daemon died after grace period → RestartDaemon with backoff" {
    let state = emptyState now |> recordStart 1234 now
    let action, newState = decide defaultConfig seed state DaemonStatus.NotRunning (now.AddSeconds 60.0)
    match action with
    | Action.RestartDaemon delay ->
      Expect.equal "first restart delay is the 1s backoff, jittered by the seed"
        (SageFs.RestartPolicy.withJitter defaultConfig.RestartPolicy seed defaultConfig.RestartPolicy.BackoffBase) delay
      Expect.equal "restart count is 1" 1 newState.RestartState.RestartCount
    | other -> failtest (sprintf "expected RestartDaemon, got %A" other)
  }

  test "successive crashes increase backoff" {
    let mutable state = emptyState now |> recordStart 1234 now
    let _action1, s1 = decide defaultConfig seed state DaemonStatus.NotRunning (now.AddSeconds 60.0)
    state <- s1 |> recordStart 5678 (now.AddSeconds 62.0)
    let action2, s2 = decide defaultConfig seed state DaemonStatus.NotRunning (now.AddSeconds 120.0)
    match action2 with
    | Action.RestartDaemon delay ->
      Expect.equal "second restart delay is the 2s backoff, jittered by the seed"
        (SageFs.RestartPolicy.withJitter defaultConfig.RestartPolicy seed (defaultConfig.RestartPolicy.BackoffBase * 2.0)) delay
      Expect.equal "restart count is 2" 2 s2.RestartState.RestartCount
    | other -> failtest (sprintf "expected RestartDaemon, got %A" other)
  }

  test "too many crashes → GiveUp" {
    let mutable state = emptyState now |> recordStart 1234 now
    for i in 1..5 do
      let _, s = decide defaultConfig seed state DaemonStatus.NotRunning (now.AddSeconds (float (i * 60)))
      state <- s |> recordStart (1000 + i) (now.AddSeconds (float (i * 60 + 2)))
    let action, _ = decide defaultConfig seed state DaemonStatus.NotRunning (now.AddSeconds 360.0)
    match action with
    | Action.GiveUp reason ->
      Expect.stringContains "mentions restart count" "5" reason
    | other -> failtest (sprintf "expected GiveUp, got %A" other)
  }

  testProperty "WHY — two watchdogs with different seeds do not restart at the same instant after a shared crash"
  <| fun (start: int64) ->
    let delays =
      [ 0L .. 49L ]
      |> List.map (fun offset -> firstRestartDelay (SageFs.RestartPolicy.JitterSeed (start + offset)))
    Expect.isGreaterThan "most seeds give their own delay" (delays |> List.distinct |> List.length, 40)

  testCase "WHY — two specific different seeds give different delays at the same state" <| fun _ ->
    firstRestartDelay (SageFs.RestartPolicy.JitterSeed 1L)
    |> Expect.notEqual "seed 1 and seed 2 restart at different times" (firstRestartDelay (SageFs.RestartPolicy.JitterSeed 2L))

  testProperty "WHY — the same seed at the same state always gives the same delay, so a replay is exact"
  <| fun (seedValue: int64) ->
    let jitterSeed = SageFs.RestartPolicy.JitterSeed seedValue
    Expect.equal "stable" (firstRestartDelay jitterSeed) (firstRestartDelay jitterSeed)

  testProperty "WHY — the jittered watchdog delay stays in [backoff/2, backoff], and the state is the plain policy's"
  <| fun (seedValue: int64) ->
    let state = emptyState now |> recordStart 1234 now
    let at = now.AddSeconds 60.0
    let action, newState = decide defaultConfig (SageFs.RestartPolicy.JitterSeed seedValue) state DaemonStatus.NotRunning at
    let _, plainState = SageFs.RestartPolicy.decide defaultConfig.RestartPolicy state.RestartState at
    Expect.equal "jitter moves only the delay" plainState newState.RestartState
    match action with
    | Action.RestartDaemon delay ->
      let backoff = defaultConfig.RestartPolicy.BackoffBase
      Expect.isTrue (sprintf "%A in band" delay)
        (delay >= backoff * 0.5 - TestTimeouts.boundaryMargin && delay <= backoff + TestTimeouts.boundaryMargin)
    | other -> failtestf "expected RestartDaemon, got %A" other

  test "recordStart updates PID and timestamp" {
    let state = emptyState now
    let updated = recordStart 9999 (now.AddSeconds 5.0) state
    Expect.equal "pid recorded" (Some 9999) updated.DaemonPid
    Expect.equal "start time recorded" (Some (now.AddSeconds 5.0)) updated.LastStartedAt
  }
]
