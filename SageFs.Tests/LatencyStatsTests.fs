module SageFs.Tests.LatencyStatsTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Tests.LatencyStats

let private cfg = { FsCheckConfig.defaultConfig with maxTest = 200 }

/// A made-up latency of `n` milliseconds, built from ticks so the number stays in the test.
let private ofMillis (n: int) = TimeSpan.FromTicks(int64 n * TimeSpan.TicksPerMillisecond)

let private summaryOf (samples: int list) =
  match summarize (samples |> List.map ofMillis) with
  | Ok s -> s
  | Result.Error refusal -> failtestf "expected a summary, was refused: %A" refusal

let private aMachine : Machine =
  { Os = "TestOS 1"; Cpu = "TestCPU x1"; LogicalCores = 8; MemoryGiB = 16; Runtime = ".NET Test" }

[<Tests>]
let latencyStatsTests =
  testList "LatencyStats" [

    testCase "a summary of nothing is refused, not reported as zero latency" <| fun _ ->
      summarize []
      |> Expect.equal "no samples is its own refusal" (Result.Error SummaryRefusal.NoSamples)

    testCase "nearest-rank percentiles over 20 samples are the 10th and the 19th smallest" <| fun _ ->
      let s = summaryOf [ 1 .. 20 ]
      s.Count |> Expect.equal "count" 20
      s.Min |> Expect.equal "min" (ofMillis 1)
      s.P50 |> Expect.equal "p50 is the ceil(0.50 * 20) = 10th smallest" (ofMillis 10)
      s.P95 |> Expect.equal "p95 is the ceil(0.95 * 20) = 19th smallest" (ofMillis 19)
      s.Max |> Expect.equal "max" (ofMillis 20)

    testCase "one sample is every percentile" <| fun _ ->
      let s = summaryOf [ 7 ]
      [ s.Min; s.P50; s.P95; s.Max ]
      |> List.distinct
      |> Expect.equal "all four are the one sample" [ ofMillis 7 ]

    testPropertyWithConfig cfg "min <= p50 <= p95 <= max, and every one is a measured sample" <|
      fun (samples: NonEmptyArray<PositiveInt>) ->
        let measured = samples.Get |> Array.map (fun (PositiveInt n) -> n) |> Array.toList
        let s = summaryOf measured
        let asSpans = measured |> List.map ofMillis
        (s.Min <= s.P50 && s.P50 <= s.P95 && s.P95 <= s.Max)
        |> Expect.isTrue "percentiles are ordered"
        [ s.Min; s.P50; s.P95; s.Max ]
        |> List.forall (fun v -> List.contains v asSpans)
        |> Expect.isTrue "nearest rank reports a value that was measured, never an interpolation"

    testPropertyWithConfig cfg "the order the samples arrived in does not change the summary" <|
      fun (samples: NonEmptyArray<PositiveInt>) ->
        let measured = samples.Get |> Array.map (fun (PositiveInt n) -> n) |> Array.toList
        summaryOf measured
        |> Expect.equal "a reversed run summarises the same" (summaryOf (List.rev measured))

    testCase "the gate is judged on p95: a bound between p50 and p95 is a regression" <| fun _ ->
      let s = summaryOf [ 1 .. 20 ]
      match gate (ofMillis 15) s with
      | GateVerdict.Regressed (measured, bound) ->
        measured |> Expect.equal "measured is the p95" s.P95
        bound |> Expect.equal "bound is echoed back" (ofMillis 15)
      | other -> failtestf "a p95 over the bound must regress, got %A" other

    testCase "a p95 exactly on the bound is within it" <| fun _ ->
      let s = summaryOf [ 1 .. 20 ]
      match gate s.P95 s with
      | GateVerdict.WithinBound _ -> ()
      | other -> failtestf "a p95 equal to the bound is within it, got %A" other

    testCase "a regression names the measured value and the bound" <| fun _ ->
      let s = summaryOf [ 1 .. 20 ]
      let text = describeVerdict (gate (ofMillis 15) s)
      text |> Expect.stringContains "the measured p95 is in the message" (sprintf "%g" s.P95.TotalMilliseconds)
      text |> Expect.stringContains "the bound is in the message" (sprintf "%g" (ofMillis 15).TotalMilliseconds)

    testCase "a report line carries the name, the sample count, p50, p95 and the machine" <| fun _ ->
      let s = summaryOf [ 1 .. 20 ]
      let line = reportLine "keystroke-to-first-result" aMachine s
      for expected in [ "keystroke-to-first-result"; "n=20"; "p50="; "p95="; aMachine.Os; aMachine.Cpu; string aMachine.LogicalCores; aMachine.Runtime ] do
        line |> Expect.stringContains (sprintf "the line says %s" expected) expected
      line.Contains "\n" |> Expect.isFalse "one line, so a log grep finds it whole"

    testCase "the machine this process runs on names an OS, a CPU and a positive core count" <| fun _ ->
      let m = machine ()
      m.Os |> Expect.isNotEmpty "an OS is named"
      m.Cpu |> Expect.isNotEmpty "a CPU is named"
      m.Runtime |> Expect.isNotEmpty "a runtime is named"
      (m.LogicalCores > 0 && m.MemoryGiB > 0) |> Expect.isTrue "cores and memory are real"
  ]
