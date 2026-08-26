module SageFs.Tests.RestartPolicyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.RestartPolicy
open SageFs
open SageFs.Tests.SharedGenerators

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
    // Restarts spaced beyond the startup-crash window (10s) are steady-state
    // crashes: backoff grows 2x (1s -> 2s). (Rapid restarts within the window
    // hit the circuit breaker and back off 4x — covered by
    // RestartPolicyCircuitBreakerTests.)
    let _, s1 = decide defaultPolicy emptyState now
    let d2, s2 = decide defaultPolicy s1 (now.AddSeconds(30.0))
    match d2 with
    | Decision.Restart delay ->
      Expect.equal "2s delay" (TimeSpan.FromSeconds 2.0) delay
    | Decision.GiveUp _ ->
      failtest "should restart"
    Expect.equal "count is 2" 2 s2.RestartCount
  }

  test "gives up after max restarts" {
    // Steady-state restarts (spaced beyond the startup window) hit the full
    // MaxRestarts ceiling of 5.
    let mutable state = emptyState
    for i in 1..5 do
      let _, s = decide defaultPolicy state (now.AddSeconds(float i * 30.0))
      state <- s
    let decision, _ = decide defaultPolicy state (now.AddSeconds(6.0 * 30.0))
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
      let _, s = decide defaultPolicy state (now.AddSeconds(float i * 30.0))
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

let propertyTests = testList "properties" [
  testPropertyWithConfig propConfig "nextBackoff is monotonically non-decreasing" <|
    fun (PositiveInt a) (PositiveInt b) ->
      let lo = min a b
      let hi = max a b
      let loDelay = nextBackoff defaultPolicy lo
      let hiDelay = nextBackoff defaultPolicy hi
      (hiDelay, loDelay) |> Expect.isGreaterThanOrEqual "higher count => higher or equal delay"

  testPropertyWithConfig propConfig "nextBackoff never exceeds BackoffMax" <|
    fun (NonNegativeInt count) ->
      let delay = nextBackoff defaultPolicy count
      (defaultPolicy.BackoffMax, delay) |> Expect.isGreaterThanOrEqual "capped at max"

  testPropertyWithConfig propConfig "nextBackoff is always positive" <|
    fun (NonNegativeInt count) ->
      let delay = nextBackoff defaultPolicy count
      (delay.TotalMilliseconds, 0.0) |> Expect.isGreaterThan "positive delay"

  testPropertyWithConfig propConfig "decide: restart count increases monotonically" <|
    fun (PositiveInt n) ->
      // Cap at the startup-crash ceiling (3) — beyond that the circuit
      // breaker gives up (count stops increasing), which is correct.
      let steps = min n 3
      let mutable state = emptyState
      for i in 1..steps do
        let _, s = decide defaultPolicy state (now.AddSeconds(float i))
        (s.RestartCount, state.RestartCount) |> Expect.isGreaterThan "count increases"
        state <- s

  testPropertyWithConfig propConfig "decide: window reset allows fresh restarts" <|
    fun (PositiveInt extraMinutes) ->
      let mutable state = emptyState
      for i in 1..defaultPolicy.MaxRestarts do
        let _, s = decide defaultPolicy state (now.AddSeconds(float i))
        state <- s
      let afterWindow = now.AddMinutes(float (defaultPolicy.ResetWindow.TotalMinutes + float extraMinutes))
      let decision, newState = decide defaultPolicy state afterWindow
      match decision with
      | Decision.Restart _ ->
        newState.RestartCount |> Expect.equal "reset to 1" 1
      | Decision.GiveUp _ ->
        failtest "should have reset window"
]

[<Tests>]
let tests = testList "RestartPolicy" [
  nextBackoffTests
  decideTests
  propertyTests
]
