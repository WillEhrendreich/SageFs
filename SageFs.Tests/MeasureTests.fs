module SageFs.Tests.MeasureTests

open Expecto
open SageFs.Measures

/// Tests that F# units of measure enforce type-safe timing.
/// These tests verify compile-time guarantees at runtime by
/// asserting the measure types exist and conversions round-trip.
[<Tests>]
let measureTests =
  testList "Units of measure" [
    testList "Timing measures exist and work" [
      test "ms measure can annotate int" {
        let delay: int<ms> = 50<ms>
        delay |> Expect.equal "50ms literal" 50<ms>
      }

      test "ms measure can annotate float" {
        let duration: float<ms> = 300.0<ms>
        duration |> Expect.equal "300ms float literal" 300.0<ms>
      }

      test "ms arithmetic preserves units" {
        let a = 100<ms>
        let b = 200<ms>
        (a + b) |> Expect.equal "addition preserves ms" 300<ms>
      }

      test "ms multiplication strips units correctly" {
        let delay = 50.0<ms>
        let multiplier = 1.5
        let result: float<ms> = delay * multiplier
        result |> Expect.equal "multiplication by scalar preserves ms" 75.0<ms>
      }
    ]

    testList "RetryPolicy uses ms measure" [
      test "RetryConfig.BaseDelayMs is int<ms>" {
        let config = SageFs.RetryPolicy.defaults
        let v: int<ms> = config.BaseDelayMs
        v |> Expect.equal "default base delay is 50ms" 50<ms>
      }

      test "backoffMs returns int<ms>" {
        let config = SageFs.RetryPolicy.defaults
        let result: int<ms> = SageFs.RetryPolicy.backoffMs config 0
        result |> Expect.isGreaterThan "backoff must be positive" 0<ms>
      }

      test "RetryAfter carries int<ms>" {
        let outcome = SageFs.RetryPolicy.RetryOutcome.RetryAfter 100<ms>
        match outcome with
        | SageFs.RetryPolicy.RetryOutcome.RetryAfter d ->
          d |> Expect.equal "retry delay is 100ms" 100<ms>
        | _ -> failtest "expected RetryAfter"
      }
    ]

    testList "Debounce config uses ms measure" [
      test "AdaptiveDebounceConfig fields are float<ms>" {
        let cfg = SageFs.Features.LiveTesting.AdaptiveDebounceConfig.defaults
        let _: float<ms> = cfg.BaseTreeSitterMs
        let _: float<ms> = cfg.BaseFcsMs
        let _: float<ms> = cfg.MaxFcsMs
        cfg.BaseTreeSitterMs |> Expect.equal "default tree-sitter delay" 50.0<ms>
        cfg.BaseFcsMs |> Expect.equal "default FCS delay" 300.0<ms>
        cfg.MaxFcsMs |> Expect.equal "default max FCS delay" 2000.0<ms>
      }

      test "AdaptiveDebounce.CurrentFcsDelayMs is float<ms>" {
        let ad = SageFs.Features.LiveTesting.AdaptiveDebounce.createDefault ()
        let _: float<ms> = ad.CurrentFcsDelayMs
        ad.CurrentFcsDelayMs |> Expect.equal "starts at base" 300.0<ms>
      }

      test "DebouncedOp.DelayMs is int<ms>" {
        let op: SageFs.Features.LiveTesting.DebouncedOp<string> = {
          Payload = "test"
          RequestedAt = System.DateTimeOffset.UtcNow
          DelayMs = 50<ms>
          Generation = 1L
        }
        op.DelayMs |> Expect.equal "debounce delay" 50<ms>
      }
    ]

    testList "TestTreemapEntry.DurationMs is float<ms>" [
      test "treemap entry duration has ms measure" {
        let entry: SageFs.Features.LiveTesting.TestTreemapEntry = {
          DisplayName = "test"
          FullName = "test"
          DurationMs = 42.5<ms>
          Status = SageFs.Features.LiveTesting.TreemapStatus.Passed
        }
        entry.DurationMs |> Expect.equal "duration is 42.5ms" 42.5<ms>
      }
    ]
  ]
