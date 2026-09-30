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

/// The widest spread the jitter is allowed, as a fraction of the delay.
let private fraction = RestartPolicy.MaxJitterFraction

[<Tests>]
let tests =
  testList "RestartPolicy jitter" [

    testProperty "WHY — a jittered delay never leaves 0 and the cap, for any seed"
    <| fun (seed: int64) (ms: int) ->
      let jittered = RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) (delayOfMs ms)
      Expect.isTrue "not below zero" (jittered >= TimeSpan.Zero)
      Expect.isTrue "not above the cap" (jittered <= policy.BackoffMax)

    testProperty "WHY — a jittered delay stays within the stated fraction of the base delay, for any seed"
    <| fun (seed: int64) (ms: int) ->
      let delay = delayOfMs ms
      let jittered = RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) delay
      let slackMs = delay.TotalMilliseconds * fraction + 1.0
      Expect.isTrue
        (sprintf "%A vs %A" jittered delay)
        (abs (jittered.TotalMilliseconds - delay.TotalMilliseconds) <= slackMs)

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
      let delay = TimeSpan.FromSeconds 8.0
      let delays =
        [ 1L .. 1000L ]
        |> List.map (fun seed -> RestartPolicy.withJitter policy (RestartPolicy.JitterSeed seed) delay)
      let distinct = delays |> List.distinct |> List.length
      let spread = (List.max delays - List.min delays).TotalMilliseconds
      let wantedSpread = delay.TotalMilliseconds * fraction
      Expect.isGreaterThan "many distinct delays" (distinct, 500)
      // The band is +/- fraction, so the full width is 2 * fraction. Most of it must be used.
      Expect.isGreaterThan "most of the band is used" (spread, wantedSpread)

    testCase "WHY — adjacent seeds are not adjacent delays" <| fun _ ->
      let delay = TimeSpan.FromSeconds 8.0
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
        let slackMs = plain.TotalMilliseconds * fraction + 1.0
        Expect.isTrue "within the fraction" (abs (jittered.TotalMilliseconds - plain.TotalMilliseconds) <= slackMs)
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
        Expect.isTrue "delay within the fraction"
          (abs (delay.TotalMilliseconds - plainDelay.TotalMilliseconds) <= plainDelay.TotalMilliseconds * fraction + 1.0)
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
  ]
