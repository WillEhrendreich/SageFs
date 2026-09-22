module SageFs.Tests.StreamingProxySimTests

open Expecto
open Expecto.Flip
open SageFs.Simulation
open SageFs.Simulation.StreamingProxySim
open SageFs.Simulation.StreamingProxyInvariants

/// DST for the streaming-test-proxy cancellation contract fixed in
/// `SageFs.Core/HttpWorkerClient.fs` — see `StreamingProxySim.fs`'s header
/// comment for the bug, the root cause (confirmed empirically in the SageFs
/// REPL: 600/600 throws under the double-token composition, 0/400 under the
/// fix), and why this is a MODEL of the scheduling race rather than a
/// literal fold of the real async/Task code.
///
/// `StreamingProxyTests.fs` covers the real HTTP/SSE proxy end-to-end
/// (`streamingTestProxy` against a real loopback `HttpListener`, real
/// `CancellationTokenSource`s); this DST covers the ORDERING space —
/// every relative position of CallerCancels against BodyStarts/LineArrives/
/// StreamDone/ReadTimeoutFires/SchedulerTick — far more exhaustively than a
/// handful of real-clock integration tests ever could, in milliseconds.

let private simConfig = { FsCheckConfig.defaultConfig with maxTest = 500 }

let private assertHolds (t: Trace) =
  match violations t with
  | [] -> ()
  | vs ->
    failtestf
      "INVARIANT VIOLATION (%s) — replay this scenario:\n  Seed=%d\n  Ops=%A\n  Violations=%A"
      t.Reducer t.Scenario.Seed t.Scenario.Ops vs

[<Tests>]
let tests =
  testList "DST streaming-test-proxy cancellation contract" [

    testList "the fixed contract holds" [

      testPropertyWithConfig simConfig
        "seeded scenarios — the fix never throws, and a first-terminal cancel always says Cancelled"
        <| fun (seed: int) -> assertHolds (run (StreamingProxyGenerators.fromSeed seed))

      testCase "cancel before start (the reported bug's exact shape) resolves to Cancelled, never Threw" <| fun _ ->
        let t = run StreamingProxyGenerators.cancelBeforeStart
        t.Final.Ended |> Expect.equal "pre-start cancellation is a value, not an exception" (Some Outcome.Cancelled)
        assertHolds t

      testCase "cancel during the first read resolves to Cancelled" <| fun _ ->
        let t = run StreamingProxyGenerators.cancelDuringFirstRead
        t.Final.Ended |> Expect.equal "cancelled mid-first-read" (Some Outcome.Cancelled)
        assertHolds t

      testCase "cancel between reads resolves to Cancelled" <| fun _ ->
        let t = run StreamingProxyGenerators.cancelBetweenReads
        t.Final.Ended |> Expect.equal "cancelled after several lines" (Some Outcome.Cancelled)
        assertHolds t

      testCase "cancel racing completion does not override an already-decided Completed" <| fun _ ->
        let t = run StreamingProxyGenerators.cancelRacingCompletion
        t.Final.Ended |> Expect.equal "the stream already finished before the stale cancel arrived" (Some Outcome.Completed)
        assertHolds t

      testCase "a pool that starts the body late still answers Cancelled, never Threw" <| fun _ ->
        let t = run StreamingProxyGenerators.poolStartsBodyLate
        t.Final.Ended |> Expect.equal "thread-pool contention delaying BodyStarts changes nothing about the outcome" (Some Outcome.Cancelled)
        assertHolds t
    ]

    testList "the twin reproduces the real regression (teeth)" [

      testCase "REPRODUCED — the double-token-race twin throws on cancel-before-start, exactly the reported bug" <| fun _ ->
        let t = runDoubleTokenRace StreamingProxyGenerators.cancelBeforeStart
        match t.Final.Ended with
        | Some (Outcome.Threw _) -> ()
        | other -> failtestf "expected the twin to reproduce a Threw outcome, got %A" other
        violations t
        |> List.map fst
        |> Expect.contains "never-throws-at-the-caller must fire against the twin" "never-throws-at-the-caller"

      testCase "the twin does NOT throw once the body has genuinely started — the race is about pre-start ordering, not cancellation itself" <| fun _ ->
        let t = runDoubleTokenRace StreamingProxyGenerators.cancelBetweenReads
        t.Final.Ended |> Expect.equal "once started, the twin behaves like the real reducer" (Some Outcome.Cancelled)
        assertHolds t

      testPropertyWithConfig simConfig "the invariant has teeth: some seeded scenario throws under the twin" <|
        fun () ->
          let seeds = [ 1 .. 200 ]
          let anyThrows =
            seeds
            |> List.exists (fun seed ->
              let t = runDoubleTokenRace (StreamingProxyGenerators.fromSeed seed)
              violations t |> List.map fst |> List.contains "never-throws-at-the-caller")
          anyThrows |> Expect.isTrue "at least one seeded scenario must expose the double-token-race throw under the twin"
    ]

    testList "multi-run attribution — an outcome is never attributed to the wrong run" [

      testCase "a superseded run's cancel never bleeds into its successor's Completed" <| fun _ ->
        let t = runMulti StreamingProxyGenerators.twoRunsSupersedeThenComplete
        t.Interleaved |> Map.find 0 |> fun s -> s.Ended |> Expect.equal "run 0 was cancelled" (Some Outcome.Cancelled)
        t.Interleaved |> Map.find 1 |> fun s -> s.Ended |> Expect.equal "run 1 completed, unaffected by run 0's cancel" (Some Outcome.Completed)
        StreamingProxyInvariants.noCrossRunAttribution t
        |> List.iter (fun (runId, outcome) ->
          match outcome with
          | StreamingProxyInvariants.MultiOutcome.Holds -> ()
          | StreamingProxyInvariants.MultiOutcome.Violated msg -> failtestf "run #%d: %s" runId msg)

      testPropertyWithConfig simConfig "seeded interleavings — every run's outcome matches its own isolated replay" <|
        fun (seed: int) ->
          let t = runMulti (StreamingProxyGenerators.multiFromSeed seed)
          StreamingProxyInvariants.noCrossRunAttribution t
          |> List.iter (fun (runId, outcome) ->
            match outcome with
            | StreamingProxyInvariants.MultiOutcome.Holds -> ()
            | StreamingProxyInvariants.MultiOutcome.Violated msg -> failtestf "run #%d: %s" runId msg)
    ]

    testList "determinism / replay" [

      testProperty "same seed => identical trace (single-run)" <|
        fun (seed: int) ->
          let a = run (StreamingProxyGenerators.fromSeed seed)
          let b = run (StreamingProxyGenerators.fromSeed seed)
          a.Final |> Expect.equal "replaying the same seed yields the identical final fold state" b.Final

      testProperty "same seed => identical trace (multi-run)" <|
        fun (seed: int) ->
          let a = runMulti (StreamingProxyGenerators.multiFromSeed seed)
          let b = runMulti (StreamingProxyGenerators.multiFromSeed seed)
          a.Interleaved |> Expect.equal "replaying the same seed yields the identical interleaved fold" b.Interleaved
    ]
  ]
