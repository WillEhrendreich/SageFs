module SageFs.Tests.TimeoutsTests

open System
open Expecto
open Expecto.Flip
open SageFs

[<Tests>]
let timeoutsTests = testList "Timeouts" [

  testList "ValidTimeout.create" [
    testCase "rejects sub-second timeout" <| fun _ ->
      ValidTimeout.create (TimeSpan.FromMilliseconds(500.0))
      |> Expect.isError "should reject 500ms"

    testCase "rejects zero timeout" <| fun _ ->
      ValidTimeout.create TimeSpan.Zero
      |> Expect.isError "should reject zero"

    testCase "rejects negative timeout" <| fun _ ->
      ValidTimeout.create (TimeSpan.FromSeconds(-1.0))
      |> Expect.isError "should reject negative"

    testCase "rejects > 10min timeout" <| fun _ ->
      ValidTimeout.create (TimeSpan.FromMinutes(11.0))
      |> Expect.isError "should reject 11 minutes"

    testCase "accepts 1 second (lower bound)" <| fun _ ->
      ValidTimeout.create (TimeSpan.FromSeconds(1.0))
      |> Expect.isOk "should accept 1s"

    testCase "accepts 10 minutes (upper bound)" <| fun _ ->
      ValidTimeout.create (TimeSpan.FromMinutes(10.0))
      |> Expect.isOk "should accept 10min"

    testCase "accepts 30 seconds" <| fun _ ->
      ValidTimeout.create (TimeSpan.FromSeconds(30.0))
      |> Expect.isOk "should accept 30s"

    testCase "round-trips value" <| fun _ ->
      let ts = TimeSpan.FromSeconds(42.0)
      match ValidTimeout.create ts with
      | Ok vt -> ValidTimeout.value vt |> Expect.equal "should round-trip" ts
      | Error e -> failtestf "unexpected error: %s" e
  ]

  testSequenced <| testList "Thread-safe mutable timeouts" [
    testCase "setPerTestTimeout rejects invalid value" <| fun _ ->
      let before = Timeouts.perTestDefault ()
      Timeouts.setPerTestTimeout (TimeSpan.FromMilliseconds(100.0))
      Timeouts.perTestDefault ()
      |> Expect.equal "should remain unchanged" before

    testCase "setPerTestTimeout accepts valid value" <| fun _ ->
      let newVal = TimeSpan.FromSeconds(7.0)
      Timeouts.setPerTestTimeout newVal
      Timeouts.perTestDefault ()
      |> Expect.equal "should update" newVal
      // Restore default
      Timeouts.setPerTestTimeout (TimeSpan.FromSeconds(5.0))

    testCase "setGlobalTestRunTimeout rejects invalid value" <| fun _ ->
      let before = Timeouts.globalTestRun ()
      Timeouts.setGlobalTestRunTimeout TimeSpan.Zero
      Timeouts.globalTestRun ()
      |> Expect.equal "should remain unchanged" before

    testCase "setGlobalTestRunTimeout accepts valid value" <| fun _ ->
      let newVal = TimeSpan.FromMinutes(3.0)
      Timeouts.setGlobalTestRunTimeout newVal
      Timeouts.globalTestRun ()
      |> Expect.equal "should update" newVal
      // Restore default
      Timeouts.setGlobalTestRunTimeout (TimeSpan.FromMinutes(2.0))
  ]

  testList "Environment variable overrides" [
    testCase "warmupInactivityLimit is positive" <| fun _ ->
      Expect.isGreaterThan
        "should be positive"
        (Timeouts.warmupInactivityLimit.TotalSeconds, 0.0)

    testCase "workerHttpRead is positive" <| fun _ ->
      Expect.isGreaterThan
        "should be positive"
        (Timeouts.workerHttpRead.TotalSeconds, 0.0)

    testCase "warmupAbsoluteMax is positive" <| fun _ ->
      Expect.isGreaterThan
        "should be positive"
        (Timeouts.warmupAbsoluteMax.TotalMinutes, 0.0)

    testCase "buildCompletion is positive" <| fun _ ->
      Expect.isGreaterThan
        "should be positive"
        (Timeouts.buildCompletion.TotalMinutes, 0.0)
  ]

  testList "Timeouts module static values" [
    testCase "healthCheck is 2 seconds" <| fun _ ->
      Timeouts.healthCheck
      |> Expect.equal "should be 2s" (TimeSpan.FromSeconds(2.0))

    testCase "processNormalExit is 3 seconds" <| fun _ ->
      Timeouts.processNormalExit
      |> Expect.equal "should be 3s" (TimeSpan.FromSeconds(3.0))

    testCase "sseKeepAlive is 24 hours" <| fun _ ->
      Timeouts.sseKeepAlive
      |> Expect.equal "should be 24h" (TimeSpan.FromHours(24.0))
  ]
]