module SageFs.Tests.WorkerLivenessTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs
open SageFs.Tests.SharedGenerators

let private now = DateTimeOffset(2026, 2, 14, 12, 0, 0, TimeSpan.Zero)
let private cfg = WorkerLivenessMonitor.defaultConfig
let private init = WorkerLivenessMonitor.initialState

// ── State transition tests ──

let stateTransitionTests = testList "state transitions" [
  test "initial state has 0 consecutive misses" {
    init.ConsecutiveMisses
    |> Expect.equal "zero misses" 0
  }

  test "healthy result resets consecutive misses to 0" {
    let state = { init with ConsecutiveMisses = 2 }
    let newState, _ =
      WorkerLivenessMonitor.processCheckResult cfg state (HealthCheckResult.Healthy 5.0) now
    newState.ConsecutiveMisses
    |> Expect.equal "reset to 0" 0
  }

  test "healthy result updates lastHealthy timestamp" {
    let newState, _ =
      WorkerLivenessMonitor.processCheckResult cfg init (HealthCheckResult.Healthy 3.0) now
    newState.LastHealthy
    |> Expect.equal "set to now" (Some now)
  }

  test "unhealthy result increments consecutive misses" {
    let newState, _ =
      WorkerLivenessMonitor.processCheckResult cfg init (HealthCheckResult.Unhealthy "err") now
    newState.ConsecutiveMisses
    |> Expect.equal "incremented to 1" 1
  }

  test "timeout result increments consecutive misses" {
    let newState, _ =
      WorkerLivenessMonitor.processCheckResult cfg init HealthCheckResult.Timeout now
    newState.ConsecutiveMisses
    |> Expect.equal "incremented to 1" 1
  }

  test "consecutive misses below threshold produce Alive verdict" {
    let state = { init with ConsecutiveMisses = 1 }
    let _, verdict =
      WorkerLivenessMonitor.processCheckResult cfg state HealthCheckResult.Timeout now
    match verdict with
    | LivenessVerdict.Alive -> ()
    | LivenessVerdict.Hung _ -> failtest "should be Alive"
  }

  test "consecutive misses at threshold produce Hung verdict" {
    let state = { init with ConsecutiveMisses = cfg.MissThreshold - 1 }
    let _, verdict =
      WorkerLivenessMonitor.processCheckResult cfg state HealthCheckResult.Timeout now
    match verdict with
    | LivenessVerdict.Hung _ -> ()
    | LivenessVerdict.Alive -> failtest "should be Hung"
  }

  test "consecutive misses above threshold stay Hung" {
    let state = { init with ConsecutiveMisses = cfg.MissThreshold + 5 }
    let _, verdict =
      WorkerLivenessMonitor.processCheckResult cfg state HealthCheckResult.Timeout now
    match verdict with
    | LivenessVerdict.Hung _ -> ()
    | LivenessVerdict.Alive -> failtest "should be Hung"
  }

  test "healthy after 2 misses resets to Alive" {
    let state = { init with ConsecutiveMisses = 2 }
    let newState, verdict =
      WorkerLivenessMonitor.processCheckResult cfg state (HealthCheckResult.Healthy 1.0) now
    newState.ConsecutiveMisses |> Expect.equal "reset to 0" 0
    match verdict with
    | LivenessVerdict.Alive -> ()
    | LivenessVerdict.Hung _ -> failtest "should be Alive after healthy"
  }

  test "healthy after threshold-1 misses resets to Alive" {
    let state = { init with ConsecutiveMisses = cfg.MissThreshold - 1 }
    let newState, verdict =
      WorkerLivenessMonitor.processCheckResult cfg state (HealthCheckResult.Healthy 1.0) now
    newState.ConsecutiveMisses |> Expect.equal "reset to 0" 0
    match verdict with
    | LivenessVerdict.Alive -> ()
    | LivenessVerdict.Hung _ -> failtest "should be Alive after healthy"
  }
]

// ── Verdict detail tests ──

let verdictDetailTests = testList "verdict details" [
  test "Hung verdict includes correct miss count" {
    let state = { init with ConsecutiveMisses = cfg.MissThreshold - 1 }
    let _, verdict =
      WorkerLivenessMonitor.processCheckResult cfg state HealthCheckResult.Timeout now
    match verdict with
    | LivenessVerdict.Hung(missCount, _) ->
      missCount |> Expect.equal "miss count" cfg.MissThreshold
    | LivenessVerdict.Alive -> failtest "should be Hung"
  }

  test "Hung verdict includes None lastHealthy when never healthy" {
    let state = { init with ConsecutiveMisses = cfg.MissThreshold - 1 }
    let _, verdict =
      WorkerLivenessMonitor.processCheckResult cfg state HealthCheckResult.Timeout now
    match verdict with
    | LivenessVerdict.Hung(_, lastHealthy) ->
      lastHealthy |> Expect.isNone "never was healthy"
    | LivenessVerdict.Alive -> failtest "should be Hung"
  }

  test "Hung verdict includes Some lastHealthy when previously healthy" {
    let earlier = now.AddSeconds(-60.0)
    let state = {
      ConsecutiveMisses = cfg.MissThreshold - 1
      LastHealthy = Some earlier
      LastCheckAt = Some (now.AddSeconds(-10.0))
    }
    let _, verdict =
      WorkerLivenessMonitor.processCheckResult cfg state HealthCheckResult.Timeout now
    match verdict with
    | LivenessVerdict.Hung(_, lastHealthy) ->
      lastHealthy |> Expect.equal "last healthy time" (Some earlier)
    | LivenessVerdict.Alive -> failtest "should be Hung"
  }
]

// ── shouldCheck timing tests ──

let shouldCheckTests = testList "shouldCheck timing" [
  test "returns true when no previous check" {
    WorkerLivenessMonitor.shouldCheck cfg init now
    |> Expect.isTrue "first check always needed"
  }

  test "returns false when checked recently" {
    let state = { init with LastCheckAt = Some (now.AddSeconds(-5.0)) }
    WorkerLivenessMonitor.shouldCheck cfg state now
    |> Expect.isFalse "too soon"
  }

  test "returns true when interval elapsed" {
    let state = { init with LastCheckAt = Some (now.AddSeconds(-15.0)) }
    WorkerLivenessMonitor.shouldCheck cfg state now
    |> Expect.isTrue "interval passed"
  }

  test "returns true at exact interval boundary" {
    let state = { init with LastCheckAt = Some (now.AddSeconds(-10.0)) }
    WorkerLivenessMonitor.shouldCheck cfg state now
    |> Expect.isTrue "exactly at boundary"
  }
]

// ── Configuration tests ──

let configTests = testList "configuration" [
  test "default config has 10s interval" {
    cfg.CheckInterval
    |> Expect.equal "10 seconds" (TimeSpan.FromSeconds 10.0)
  }

  test "default config has 3 miss threshold" {
    cfg.MissThreshold
    |> Expect.equal "3 misses" 3
  }

  test "custom config with 1 miss threshold detects hung immediately" {
    let custom = { cfg with MissThreshold = 1 }
    let _, verdict =
      WorkerLivenessMonitor.processCheckResult custom init HealthCheckResult.Timeout now
    match verdict with
    | LivenessVerdict.Hung(missCount, _) ->
      missCount |> Expect.equal "single miss" 1
    | LivenessVerdict.Alive -> failtest "should be Hung with threshold 1"
  }

  test "custom config with 5s interval checks more frequently" {
    let custom = { cfg with CheckInterval = TimeSpan.FromSeconds 5.0 }
    let state = { init with LastCheckAt = Some (now.AddSeconds(-6.0)) }
    WorkerLivenessMonitor.shouldCheck custom state now
    |> Expect.isTrue "5s elapsed > 5s interval"
  }
]

// ── Property-based tests ──

let propertyTests = testList "properties" [
  testPropertyWithConfig propConfig "N consecutive Healthy results always produce Alive" <|
    fun (PositiveInt n) ->
      let steps = min n 50
      let mutable state = init
      for i in 1..steps do
        let t = now.AddSeconds(float i * 10.0)
        let s, verdict = WorkerLivenessMonitor.processCheckResult cfg state (HealthCheckResult.Healthy 1.0) t
        match verdict with
        | LivenessVerdict.Alive -> ()
        | LivenessVerdict.Hung _ -> failtest "Healthy should always produce Alive"
        state <- s

  testPropertyWithConfig propConfig "exactly threshold consecutive misses produce Hung" <|
    fun () ->
      let mutable state = init
      for i in 1..cfg.MissThreshold do
        let t = now.AddSeconds(float i * 10.0)
        let s, verdict = WorkerLivenessMonitor.processCheckResult cfg state HealthCheckResult.Timeout t
        match i < cfg.MissThreshold with
        | true ->
          match verdict with
          | LivenessVerdict.Alive -> ()
          | LivenessVerdict.Hung _ -> failtest $"should be Alive at miss {i}"
        | false ->
          match verdict with
          | LivenessVerdict.Hung _ -> ()
          | LivenessVerdict.Alive -> failtest $"should be Hung at miss {i}"
        state <- s

  testPropertyWithConfig propConfig "Healthy after any number of misses resets to Alive" <|
    fun (PositiveInt n) ->
      let misses = min n 100
      let mutable state = init
      for i in 1..misses do
        let t = now.AddSeconds(float i * 10.0)
        let s, _ = WorkerLivenessMonitor.processCheckResult cfg state HealthCheckResult.Timeout t
        state <- s
      let finalT = now.AddSeconds(float (misses + 1) * 10.0)
      let newState, verdict =
        WorkerLivenessMonitor.processCheckResult cfg state (HealthCheckResult.Healthy 1.0) finalT
      newState.ConsecutiveMisses |> Expect.equal "reset to 0" 0
      match verdict with
      | LivenessVerdict.Alive -> ()
      | LivenessVerdict.Hung _ -> failtest "Healthy should always reset to Alive"

  testPropertyWithConfig propConfig "miss count in Hung verdict equals consecutive non-Healthy count" <|
    fun (PositiveInt extra) ->
      let totalMisses = cfg.MissThreshold + (extra % 20)
      let mutable state = init
      let mutable lastVerdict = LivenessVerdict.Alive
      for i in 1..totalMisses do
        let t = now.AddSeconds(float i * 10.0)
        let s, v = WorkerLivenessMonitor.processCheckResult cfg state HealthCheckResult.Timeout t
        state <- s
        lastVerdict <- v
      match lastVerdict with
      | LivenessVerdict.Hung(missCount, _) ->
        missCount |> Expect.equal "matches total misses" totalMisses
      | LivenessVerdict.Alive -> failtest "should be Hung"

  testPropertyWithConfig propConfig "shouldCheck is monotonic: true at T implies true at T' > T" <|
    fun (PositiveInt deltaSec) ->
      let lastCheck = now.AddSeconds(-20.0)
      let state = { init with LastCheckAt = Some lastCheck }
      let t1 = now
      let t2 = now.AddSeconds(float deltaSec)
      match WorkerLivenessMonitor.shouldCheck cfg state t1 with
      | true ->
        WorkerLivenessMonitor.shouldCheck cfg state t2
        |> Expect.isTrue "later time should also be true"
      | false -> ()
]

// ── Edge case tests ──

let edgeCaseTests = testList "edge cases" [
  test "mixed Healthy/Unhealthy/Timeout — non-consecutive misses reset" {
    let step1State, _ =
      WorkerLivenessMonitor.processCheckResult cfg init HealthCheckResult.Timeout (now.AddSeconds 10.0)
    step1State.ConsecutiveMisses |> Expect.equal "1 miss" 1

    let step2State, _ =
      WorkerLivenessMonitor.processCheckResult cfg step1State (HealthCheckResult.Unhealthy "bad") (now.AddSeconds 20.0)
    step2State.ConsecutiveMisses |> Expect.equal "2 misses" 2

    let step3State, step3Verdict =
      WorkerLivenessMonitor.processCheckResult cfg step2State (HealthCheckResult.Healthy 1.0) (now.AddSeconds 30.0)
    step3State.ConsecutiveMisses |> Expect.equal "reset by healthy" 0
    match step3Verdict with
    | LivenessVerdict.Alive -> ()
    | LivenessVerdict.Hung _ -> failtest "Healthy should reset"

    let step4State, _ =
      WorkerLivenessMonitor.processCheckResult cfg step3State HealthCheckResult.Timeout (now.AddSeconds 40.0)
    step4State.ConsecutiveMisses |> Expect.equal "back to 1 miss" 1
  }

  test "rapid succession of checks — sub-second intervals" {
    let mutable state = init
    for i in 1..5 do
      let t = now.AddMilliseconds(float i * 100.0)
      let s, _ = WorkerLivenessMonitor.processCheckResult cfg state HealthCheckResult.Timeout t
      state <- s
    state.ConsecutiveMisses |> Expect.equal "5 rapid misses" 5
  }

  test "DateTimeOffset.MinValue as initial timestamp" {
    let state = { init with LastCheckAt = Some DateTimeOffset.MinValue }
    WorkerLivenessMonitor.shouldCheck cfg state now
    |> Expect.isTrue "ancient check should trigger new check"
  }
]

[<Tests>]
let tests = testList "Worker Liveness" [
  stateTransitionTests
  verdictDetailTests
  shouldCheckTests
  configTests
  propertyTests
  edgeCaseTests
]
