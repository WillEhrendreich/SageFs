module SageFs.Tests.RetryPolicyTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.RetryPolicy
open SageFs.Tests.SharedGenerators
open SageFs.Measures

[<Tests>]
let retryPolicyTests =
  testList "RetryPolicy" [
    testList "shouldRetry" [
      test "allows retry when attempt below max" {
        shouldRetry defaults 0 |> Expect.isTrue "attempt 0 should allow retry"
      }
      test "allows retry at max minus one" {
        shouldRetry defaults 2 |> Expect.isTrue "attempt 2 should allow retry with max 3"
      }
      test "disallows retry at max" {
        shouldRetry defaults 3 |> Expect.isFalse "attempt 3 should not allow retry with max 3"
      }
      test "disallows retry above max" {
        shouldRetry defaults 10 |> Expect.isFalse "attempt 10 should not allow retry"
      }
      test "custom config with higher max" {
        let config = { MaxRetries = 5; BaseDelayMs = 100<ms> }
        shouldRetry config 4 |> Expect.isTrue "attempt 4 with max 5 should allow"
      }
    ]
    testList "backoffMs" [
      test "attempt 0 gives base delay range" {
        let config = { MaxRetries = 3; BaseDelayMs = 100<ms> }
        let delay = backoffMs config 0
        (delay, 50<ms>) |> Expect.isGreaterThanOrEqual "should be at least 50"
        (delay, 150<ms>) |> Expect.isLessThan "should be less than 150"
      }
      test "attempt 1 gives higher delay range" {
        let config = { MaxRetries = 3; BaseDelayMs = 100<ms> }
        let delay = backoffMs config 1
        (delay, 100<ms>) |> Expect.isGreaterThanOrEqual "should be at least 100"
        (delay, 300<ms>) |> Expect.isLessThan "should be less than 300"
      }
      test "attempt 2 gives even higher delay range" {
        let config = { MaxRetries = 3; BaseDelayMs = 100<ms> }
        let delay = backoffMs config 2
        (delay, 150<ms>) |> Expect.isGreaterThanOrEqual "should be at least 150"
        (delay, 450<ms>) |> Expect.isLessThan "should be less than 450"
      }
      test "zero jitter range returns base delay" {
        let config = { MaxRetries = 3; BaseDelayMs = 1<ms> }
        let delay = backoffMs config 0
        delay |> Expect.equal "should return exact base delay with no jitter" 1<ms>
      }
    ]
    testList "isVersionConflict" [
      // isVersionConflict removed — predicate is now caller-supplied
    ]
    testList "decide" [
      test "retryable predicate returns RetryAfter when attempts remain" {
        let ex = exn "transient error"
        match decide (fun _ -> true) defaults 0 ex with
        | RetryAfter _ -> ()
        | other -> failwithf "expected RetryAfter but got %A" other
      }
      test "non-retryable predicate always gives up regardless of attempt" {
        let ex = exn "permanent error"
        match decide (fun _ -> false) defaults 0 ex with
        | GiveUp _ -> ()
        | other -> failwithf "expected GiveUp but got %A" other
      }
      test "retryable predicate gives up when attempts exhausted" {
        let ex = exn "transient error"
        match decide (fun _ -> true) defaults defaults.MaxRetries ex with
        | GiveUp _ -> ()
        | other -> failwithf "expected GiveUp but got %A" other
      }
      test "predicate receives the exception" {
        let target = exn "specific"
        let mutable seen = None
        match decide (fun e -> seen <- Some e; false) defaults 0 target with
        | GiveUp _ ->
          seen |> Expect.equal "predicate should have seen the exception" (Some target)
        | other -> failwithf "expected GiveUp but got %A" other
      }
    ]

    testList "properties" [
      testPropertyWithConfig propConfig "shouldRetry is anti-monotone" <|
        fun (NonNegativeInt attempt) ->
          match attempt with
          | 0 -> ()
          | a ->
            match shouldRetry defaults a with
            | true -> shouldRetry defaults (a - 1) |> Expect.isTrue "anti-monotone"
            | false -> ()

      testPropertyWithConfig propConfig "attempt < MaxRetries always allowed" <|
        fun (NonNegativeInt raw) ->
          let config = { MaxRetries = 5; BaseDelayMs = 100<ms> }
          let attempt = raw % config.MaxRetries
          shouldRetry config attempt |> Expect.isTrue "below max"

      testPropertyWithConfig propConfig "attempt >= MaxRetries never allowed" <|
        fun (NonNegativeInt extra) ->
          let config = { MaxRetries = 3; BaseDelayMs = 100<ms> }
          shouldRetry config (config.MaxRetries + extra)
          |> Expect.isFalse "at or above max"

      testPropertyWithConfig propConfig "backoffMs is always positive" <|
        fun (NonNegativeInt attempt) ->
          let delay = backoffMs defaults attempt
          (delay, 0<ms>) |> Expect.isGreaterThan "positive"

      testPropertyWithConfig propConfig "non-retryable predicate always gives up" <|
        fun (NonNegativeInt attempt) ->
          let ex = exn "not retryable"
          match decide (fun _ -> false) defaults attempt ex with
          | GiveUp _ -> ()
          | other -> failwithf "expected GiveUp but got %A" other

      testPropertyWithConfig propConfig "retryable predicate retries while attempts remain" <|
        fun (NonNegativeInt attempt) ->
          let ex = exn "retryable"
          match decide (fun _ -> true) defaults attempt ex with
          | RetryAfter _ ->
            attempt < defaults.MaxRetries |> Expect.isTrue "should retry when attempts remain"
          | GiveUp _ ->
            attempt < defaults.MaxRetries |> Expect.isFalse "should give up when exhausted"
          | Success -> failwith "unexpected Success outcome"
    ]
  ]
