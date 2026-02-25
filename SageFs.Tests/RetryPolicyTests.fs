module SageFs.Tests.RetryPolicyTests

open System
open Expecto
open Expecto.Flip
open SageFs.RetryPolicy

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
        let config = { MaxRetries = 5; BaseDelayMs = 100 }
        shouldRetry config 4 |> Expect.isTrue "attempt 4 with max 5 should allow"
      }
    ]
    testList "backoffMs" [
      test "attempt 0 gives base delay range" {
        let config = { MaxRetries = 3; BaseDelayMs = 100 }
        let delay = backoffMs config 0
        (delay, 50) |> Expect.isGreaterThanOrEqual "should be at least 50"
        (delay, 150) |> Expect.isLessThan "should be less than 150"
      }
      test "attempt 1 gives higher delay range" {
        let config = { MaxRetries = 3; BaseDelayMs = 100 }
        let delay = backoffMs config 1
        (delay, 100) |> Expect.isGreaterThanOrEqual "should be at least 100"
        (delay, 300) |> Expect.isLessThan "should be less than 300"
      }
      test "attempt 2 gives even higher delay range" {
        let config = { MaxRetries = 3; BaseDelayMs = 100 }
        let delay = backoffMs config 2
        (delay, 150) |> Expect.isGreaterThanOrEqual "should be at least 150"
        (delay, 450) |> Expect.isLessThan "should be less than 450"
      }
      test "zero jitter range returns base delay" {
        let config = { MaxRetries = 3; BaseDelayMs = 1 }
        let delay = backoffMs config 0
        delay |> Expect.equal "should return exact base delay with no jitter" 1
      }
    ]
    testList "isVersionConflict" [
      test "regular exception is not version conflict" {
        isVersionConflict (exn "normal error") |> Expect.isFalse "should not be version conflict"
      }
      test "null ref exception is not version conflict" {
        isVersionConflict (NullReferenceException()) |> Expect.isFalse "should not be version conflict"
      }
    ]
    testList "decide" [
      test "gives up on non-version-conflict exception" {
        let ex = exn "normal error"
        match decide defaults 0 ex with
        | GiveUp _ -> ()
        | other -> failwithf "expected GiveUp but got %A" other
      }
      test "gives up even on attempt 0 for non-conflict" {
        let ex = InvalidOperationException("not a conflict")
        match decide defaults 0 (ex :> exn) with
        | GiveUp _ -> ()
        | other -> failwithf "expected GiveUp but got %A" other
      }
    ]
  ]
