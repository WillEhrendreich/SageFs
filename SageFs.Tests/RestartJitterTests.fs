module SageFs.Tests.RestartJitterTests

open System
open Expecto
open Expecto.Flip
open SageFs

let private policy = RestartPolicy.defaultPolicy
let private now = DateTime(2026, 3, 1, 12, 0, 0)

/// A delay a real policy can produce: zero up to the cap, in whole milliseconds.
let private delayOfMs (ms: int) : TimeSpan =
  let capMs = int policy.BackoffMax.TotalMilliseconds
  TimeSpan.FromMilliseconds(float (abs (ms % (capMs + 1))))

let private restartDelay (decision: RestartPolicy.Decision) : TimeSpan =
  match decision with
  | RestartPolicy.Decision.Restart delay -> delay
  | RestartPolicy.Decision.GiveUp error -> failtestf "expected Restart, got GiveUp %A" error

/// The lowest a jittered delay may fall, as a fraction of the computed delay. Jitter spreads a
/// delay over [fraction, 1] of itself, so it only ever shortens a wait and never lengthens one.
let private fraction = RestartPolicy.MinDelayFraction

/// Whether a jittered delay lies in [base * fraction, min(base, cap)], with a millisecond of slack
/// for the float round trip through TimeSpan.
let private withinBand (delay: TimeSpan) (jittered: TimeSpan) : bool =
  let capMs = policy.BackoffMax.TotalMilliseconds
  let upperMs = min delay.TotalMilliseconds capMs
  let lowerMs = min (delay.TotalMilliseconds * fraction) capMs
  jittered.TotalMilliseconds >= lowerMs - 1.0 && jittered.TotalMilliseconds <= upperMs + 1.0

/// A delay in the middle of the policy's range, below the cap: the fourth backoff step.
let private midBackoff = RestartPolicy.nextBackoff policy 4

/// The slack a delay that went through a float round trip gets when it is compared with the cap.
let private roundTripSlack = TimeSpan.FromMilliseconds 1.0

let private sessionIds =
  [ "a1b2c3d4"; "b1c2d3e4"; "c1d2e3f4"; "d1e2f3a4"; "e1f2a3b4"; "f1a2b3c4"; "0a1b2c3d"; "1b2c3d4e" ]

[<Tests>]
let tests =
  testList "RestartPolicy jitter" [

    testProperty "WHY — a jittered delay never leaves 0 and the cap, for any seed"
    <| fun (seed: int64) (ms: int) ->
      let jittered = RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) (delayOfMs ms)
      Expect.isTrue "not below zero" (jittered >= TimeSpan.Zero)
      Expect.isTrue "not above the cap" (jittered <= policy.BackoffMax)

    testProperty "WHY — a jittered delay lies in [base/2, min(base, cap)], for any seed and any delay"
    <| fun (seed: int64) (ms: int) ->
      let delay = delayOfMs ms
      let jittered = RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) delay
      Expect.isTrue (sprintf "%A vs %A" jittered delay) (withinBand delay jittered)

    testProperty "WHY — a jittered delay is within the band at every attempt of the default policy, for any seed"
    <| fun (seed: int64) (attempt: byte) ->
      let delay = RestartPolicy.nextBackoff policy (int attempt % 12 + 1)
      let jittered = RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) delay
      Expect.isTrue (sprintf "attempt %d: %A vs %A" attempt jittered delay) (withinBand delay jittered)

    testCase "WHY — a delay already above the cap is pulled down to the cap, never past it" <| fun _ ->
      let over = policy.BackoffMax + policy.BackoffBase * 10.0
      [ 1L .. 200L ]
      |> List.iter (fun seed ->
        let jittered = RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) over
        Expect.isTrue (sprintf "%A" jittered) (jittered <= policy.BackoffMax))

    testCase "WHY — at the cap the delays spread downward instead of piling up at the cap" <| fun _ ->
      let atCap = RestartPolicy.nextBackoff policy 20
      let delays =
        [ 1L .. 1000L ]
        |> List.map (fun seed -> RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) atCap)
      let atTheCap = delays |> List.filter (fun d -> d >= policy.BackoffMax - roundTripSlack) |> List.length
      Expect.isLessThan "almost none sit on the cap" (atTheCap, 20)
      Expect.isGreaterThan "they use most of the half band" ((List.max delays - List.min delays).TotalMilliseconds, atCap.TotalMilliseconds * 0.4)

    testProperty "WHY — the same seed always gives the same delay, so a replay is exact"
    <| fun (seed: int64) (ms: int) ->
      let delay = delayOfMs ms
      let first = RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) delay
      let second = RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) delay
      Expect.equal "same seed, same delay" first second

    testProperty "WHY — the sample is always in [0, 1) for any seed"
    <| fun (seed: int64) ->
      let sample = RestartPolicy.jitterSample (RestartPolicy.JitterSeed seed)
      Expect.isTrue (sprintf "sample %f" sample) (sample >= 0.0 && sample < 1.0)

    testCase "WHY — a thousand sessions dying together do not all retry at the same instant" <| fun _ ->
      let delay = midBackoff
      let delays =
        [ 1L .. 1000L ]
        |> List.map (fun seed -> RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) delay)
      let distinct = delays |> List.distinct |> List.length
      let spread = (List.max delays - List.min delays).TotalMilliseconds
      let bandMs = delay.TotalMilliseconds * (1.0 - fraction)
      Expect.isGreaterThan "many distinct delays" (distinct, 500)
      // The band is [fraction, 1] of the delay. A thousand seeds must cover most of it.
      Expect.isGreaterThan "most of the band is used" (spread, bandMs * 0.9)

    testProperty "WHY — two different seeds spread apart: across many seeds at one attempt, most pairs differ"
    <| fun (attempt: byte) (start: int64) ->
      let delay = RestartPolicy.nextBackoff policy (int attempt % 12 + 1)
      let at seed = RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) delay
      let delays = [ 0L .. 99L ] |> List.map (fun offset -> at (start + offset))
      let distinct = delays |> List.distinct |> List.length
      Expect.isGreaterThan (sprintf "attempt %d has a spread" attempt) (distinct, 90)

    testCase "WHY — adjacent seeds are not adjacent delays" <| fun _ ->
      let delay = midBackoff
      let at seed = RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) delay
      let steps = [ 0L .. 99L ] |> List.map (fun seed -> abs ((at (seed + 1L) - at seed).TotalMilliseconds))
      // A weak mixer (seed / N) would step by the same tiny amount every time.
      Expect.isGreaterThan "steps are not all tiny" (List.max steps, 100.0)

    testCase "WHY — a zero delay stays zero" <| fun _ ->
      RestartPolicy.withJitter policy (RestartPolicy.JitterSeed 7L) TimeSpan.Zero
      |> Expect.equal "zero in, zero out" TimeSpan.Zero

    testProperty "WHY — decideWithJitter changes only the delay, never the count, the window or the give-up"
    <| fun (seed: int64) (crashes: byte) ->
      let rec walk (n: int) (state: RestartPolicy.State) (at: DateTime) =
        match n with
        | 0 -> state, at
        | _ ->
          let _, next = RestartPolicy.decide policy state at
          walk (n - 1) next (at.AddSeconds 20.0)
      let state, at = walk (int crashes % 7) RestartPolicy.emptyState now
      let plainDecision, plainState = RestartPolicy.decide policy state at
      let jitterDecision, jitterState = RestartPolicy.decideWithJitter policy (RestartPolicy.JitterSeed seed) state at
      Expect.equal "same next state" plainState jitterState
      match plainDecision, jitterDecision with
      | RestartPolicy.Decision.GiveUp plain, RestartPolicy.Decision.GiveUp jittered ->
        Expect.equal "same give-up reason" plain jittered
      | RestartPolicy.Decision.Restart plain, RestartPolicy.Decision.Restart jittered ->
        Expect.isTrue "within the band" (withinBand plain jittered)
        Expect.isTrue "never above the cap" (jittered <= policy.BackoffMax)
      | plain, jittered -> failtestf "jitter flipped the decision: %A vs %A" plain jittered

    testProperty "WHY — onWorkerExitedJittered agrees with onWorkerExited on everything but the delay"
    <| fun (seed: int64) (code: int) ->
      let plain = SessionLifecycle.onWorkerExited policy RestartPolicy.emptyState code now
      let jittered =
        SessionLifecycle.onWorkerExitedJittered (RestartPolicy.JitterSeed seed) policy RestartPolicy.emptyState code now
      match plain, jittered with
      | SessionLifecycle.ExitOutcome.Graceful, SessionLifecycle.ExitOutcome.Graceful -> ()
      | SessionLifecycle.ExitOutcome.RestartAfter(plainDelay, plainState), SessionLifecycle.ExitOutcome.RestartAfter(delay, state) ->
        Expect.equal "same state" plainState state
        Expect.isTrue "delay within the band" (withinBand plainDelay delay)
      | SessionLifecycle.ExitOutcome.Abandoned plainError, SessionLifecycle.ExitOutcome.Abandoned error ->
        Expect.equal "same error" plainError error
      | plainOutcome, jitteredOutcome -> failtestf "outcome kind changed: %A vs %A" plainOutcome jitteredOutcome

    testCase "WHY — the first jittered restart of the default policy is around one second, not exactly one" <| fun _ ->
      let delays =
        [ 1L .. 50L ]
        |> List.map (fun seed ->
          RestartPolicy.decideWithJitter policy (RestartPolicy.JitterSeed seed) RestartPolicy.emptyState now
          |> fst
          |> restartDelay)
      Expect.isGreaterThan "not all identical" (delays |> List.distinct |> List.length, 1)

    testCase "WHY — a session's jitter seed is the same for the same session and time, and different across sessions" <| fun _ ->
      SessionLifecycle.jitterSeedFor sessionIds.Head now
      |> Expect.equal "stable, so a replay by seed gives the same delay" (SessionLifecycle.jitterSeedFor sessionIds.Head now)
      sessionIds
      |> List.map (fun id -> SessionLifecycle.jitterSeedFor id now)
      |> List.distinct
      |> List.length
      |> Expect.equal "every session gets its own seed" sessionIds.Length

    testCase "WHY — sessions whose workers die at the same instant do not all retry at the same instant" <| fun _ ->
      let delays =
        sessionIds
        |> List.map (fun id ->
          match SessionLifecycle.onWorkerExitedJittered (SessionLifecycle.jitterSeedFor id now) policy RestartPolicy.emptyState 1 now with
          | SessionLifecycle.ExitOutcome.RestartAfter (delay, _) -> delay
          | other -> failtestf "expected a restart, got %A" other)
      Expect.isGreaterThan "the retries are spread out" (delays |> List.distinct |> List.length, 1)
  ]
