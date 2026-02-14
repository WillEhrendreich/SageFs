module SageFs.Tests.RestartPolicyTests

open System
open Expecto
open Expecto.Flip
open SageFs.RestartPolicy
open SageFs

let now = DateTime(2026, 2, 14, 12, 0, 0)

let nextBackoffTests = testList "nextBackoff" [
  test "count 0 returns base" {
    nextBackoff defaultPolicy 0
    |> Expect.equal "base delay" (TimeSpan.FromSeconds 1.0)
  }
  test "count 1 returns base (2^0 = 1)" {
    nextBackoff defaultPolicy 1
    |> Expect.equal "1s" (TimeSpan.FromSeconds 1.0)
  }
  test "count 2 returns 2s (2^1)" {
    nextBackoff defaultPolicy 2
    |> Expect.equal "2s" (TimeSpan.FromSeconds 2.0)
  }
  test "count 3 returns 4s (2^2)" {
    nextBackoff defaultPolicy 3
    |> Expect.equal "4s" (TimeSpan.FromSeconds 4.0)
  }
  test "count 4 returns 8s (2^3)" {
    nextBackoff defaultPolicy 4
    |> Expect.equal "8s" (TimeSpan.FromSeconds 8.0)
  }
  test "count 5 returns 16s (2^4)" {
    nextBackoff defaultPolicy 5
    |> Expect.equal "16s" (TimeSpan.FromSeconds 16.0)
  }
  test "count 6 returns 30s (capped)" {
    nextBackoff defaultPolicy 6
    |> Expect.equal "capped at 30s" (TimeSpan.FromSeconds 30.0)
  }
  test "very high count still capped" {
    nextBackoff defaultPolicy 100
    |> Expect.equal "still 30s" (TimeSpan.FromSeconds 30.0)
  }
]

let decideTests = testList "decide" [
  test "first restart allowed with 1s delay" {
    let decision, newState = decide defaultPolicy emptyState now
    match decision with
    | Decision.Restart delay ->
      Expect.equal "1s delay" (TimeSpan.FromSeconds 1.0) delay
    | Decision.GiveUp _ ->
      failtest "should restart, not give up"
    Expect.equal "count is 1" 1 newState.RestartCount
    Expect.equal "window started" (Some now) newState.WindowStart
  }

  test "successive restarts increase backoff" {
    let _, s1 = decide defaultPolicy emptyState now
    let d2, s2 = decide defaultPolicy s1 (now.AddSeconds(2.0))
    match d2 with
    | Decision.Restart delay ->
      Expect.equal "2s delay" (TimeSpan.FromSeconds 2.0) delay
    | Decision.GiveUp _ ->
      failtest "should restart"
    Expect.equal "count is 2" 2 s2.RestartCount
  }

  test "gives up after max restarts" {
    let mutable state = emptyState
    for i in 1..5 do
      let _, s = decide defaultPolicy state (now.AddSeconds(float i))
      state <- s
    let decision, _ = decide defaultPolicy state (now.AddSeconds(6.0))
    match decision with
    | Decision.GiveUp error ->
      let desc = SageFsError.describe error
      Expect.stringContains "mentions count" "5" desc
    | Decision.Restart _ ->
      failtest "should give up after max restarts"
  }

  test "window reset allows restart after cooldown" {
    let mutable state = emptyState
    for i in 1..5 do
      let _, s = decide defaultPolicy state (now.AddSeconds(float i))
      state <- s
    // 6 minutes later — beyond the 5 minute reset window
    let decision, newState =
      decide defaultPolicy state (now.AddMinutes(6.0))
    match decision with
    | Decision.Restart _ ->
      Expect.equal "count reset to 1" 1 newState.RestartCount
    | Decision.GiveUp _ ->
      failtest "window should have reset"
  }

  test "window start is preserved across restarts" {
    let _, s1 = decide defaultPolicy emptyState now
    let _, s2 = decide defaultPolicy s1 (now.AddSeconds(5.0))
    Expect.equal "window start unchanged" (Some now) s2.WindowStart
  }
]

[<Tests>]
let tests = testList "RestartPolicy" [
  nextBackoffTests
  decideTests
]
