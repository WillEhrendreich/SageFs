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

/// A made-up latency of `n` milliseconds (a float, for the curves that need a fraction).
let private ms (n: float) = TimeSpan.FromMilliseconds n

/// `n` saves that each took `millis`.
let private steady (millis: float) (n: int) : TimeSpan list = List.replicate n (ms millis)

let private curveOrFail (window: int) (samples: TimeSpan list) : Curve =
  match curveOf window samples with
  | Ok curve -> curve
  | Result.Error refusal -> failtestf "expected a curve, was refused: %A" refusal

let private verdictOf (samples: TimeSpan list) : GrowthVerdict =
  growthGate growthBoundRatio (curveOrFail growthWindowSaves samples)

/// The curve the roast was handed for the patched path: 655 ms at the start, 4.8 s at the end, a straight line
/// between. Secondhand, so it is the shape the gate must refuse, not a claim about the product.
let private theRoastsCurve : TimeSpan list =
  List.init growthSeriesSaves (fun i -> ms (655. + (4800. - 655.) * float i / float (growthSeriesSaves - 1)))

/// The Readme's own figure (p50 542 ms) with a few milliseconds of jitter that repeats and does not trend.
let private theReadmesCurve : TimeSpan list =
  List.init growthSeriesSaves (fun i -> ms (542. + float (i % 7) * 4.))

[<Tests>]
let curveTests =
  testList "LatencyStats curve: does a save get slower as the session gets older" [

    testCase "a flat curve, at the Readme's pace, is flat" <| fun _ ->
      let curve = curveOrFail growthWindowSaves theReadmesCurve
      curve.Growth |> Expect.equal "the last window took as long as the first" 1.0
      match growthGate growthBoundRatio curve with
      | GrowthVerdict.Flat (measured, bound) ->
        measured |> Expect.equal "the measured growth is echoed" 1.0
        bound |> Expect.equal "the bound is echoed" growthBoundRatio
      | other -> failtestf "a flat curve must be within the bound, got %A" other

    testCase "the curve the roast was handed (655 ms to 4.8 s) is refused, and the message says by how much" <| fun _ ->
      let verdict = verdictOf theRoastsCurve
      match verdict with
      | GrowthVerdict.Growing (measured, bound) ->
        (measured > 4.0) |> Expect.isTrue (sprintf "the late window took several times the early one, %g" measured)
        bound |> Expect.equal "the bound is echoed" growthBoundRatio
      | other -> failtestf "a curve that grows 7 times must be refused, got %A" other
      describeGrowth verdict |> Expect.stringContains "says it is over the bound" "over the bound of 2.00"

    testCase "the twin: the same growing curve passes a bound that is not the stated one, so it is the bound that refuses it" <| fun _ ->
      let curve = curveOrFail growthWindowSaves theRoastsCurve
      match growthGate growthBoundRatio curve, growthGate (curve.Growth + 1.0) curve with
      | GrowthVerdict.Growing _, GrowthVerdict.Flat _ -> ()
      | other -> failtestf "the stated bound must refuse what a looser bound allows, got %A" other

    testCase "a growth of exactly the bound is within it, and a hair over is not" <| fun _ ->
      let early = 100.
      let atBound = steady early 135 @ steady (early * growthBoundRatio) 15
      let overBound = steady early 135 @ steady (early * growthBoundRatio + 1.) 15
      match verdictOf atBound, verdictOf overBound with
      | GrowthVerdict.Flat (measured, _), GrowthVerdict.Growing (over, _) ->
        measured |> Expect.equal "exactly the bound" growthBoundRatio
        (over > growthBoundRatio) |> Expect.isTrue "a hair over"
      | other -> failtestf "the bound is inclusive, got %A" other

    testCase "the stated bound sits between a flat curve and the curve the roast was handed" <| fun _ ->
      let roasts = (curveOrFail growthWindowSaves theRoastsCurve).Growth
      (growthBoundRatio > 1.0) |> Expect.isTrue "a flat curve (1.0) is within it"
      (growthBoundRatio < roasts) |> Expect.isTrue (sprintf "the roast's %.1f times is over it" roasts)

    testCase "the windows are the first, the middle and the last 15 saves, by position" <| fun _ ->
      // Save i took i milliseconds, so each median names the window it came from.
      let curve = curveOrFail growthWindowSaves (List.init growthSeriesSaves (fun i -> ms (float (i + 1))))
      curve.Early.TotalMilliseconds |> Expect.equal "saves 1 to 15 have the median 8" 8.0
      curve.Middle.TotalMilliseconds |> Expect.equal "saves 68 to 82 have the median 75" 75.0
      curve.Late.TotalMilliseconds |> Expect.equal "saves 136 to 150 have the median 143" 143.0

    testCase "the late window starts after the saves the Readme figure came from" <| fun _ ->
      (growthSeriesSaves - growthWindowSaves > warmupEdits + samplesPerPath)
      |> Expect.isTrue "a late window inside the first 22 saves would measure what the Readme already measured"
      (3 * growthWindowSaves <= growthSeriesSaves) |> Expect.isTrue "three windows fit in the series"

    testCase "a series that gets faster reads as growth below 1, because the windows are positions and not ranks" <| fun _ ->
      let curve = curveOrFail growthWindowSaves (steady 400. 15 @ steady 100. 135)
      curve.Growth |> Expect.equal "a quarter" 0.25
      match growthGate growthBoundRatio curve with
      | GrowthVerdict.Flat _ -> ()
      | other -> failtestf "a series that speeds up is within the bound, got %A" other

    testCase "growth that shows only in the last window is caught, and a bump in the middle alone is not a growth" <| fun _ ->
      match verdictOf (steady 100. 135 @ steady 1000. 15) with
      | GrowthVerdict.Growing _ -> ()
      | other -> failtestf "a slow last window must be refused, got %A" other
      let bump = steady 100. 60 @ steady 900. 30 @ steady 100. 60
      (curveOrFail growthWindowSaves bump).Middle |> Expect.equal "the middle window sees the bump" (ms 900.)
      match verdictOf bump with
      | GrowthVerdict.Flat _ -> ()
      | other -> failtestf "the gate reads the first and last windows, so a bump between is not growth: %A" other

    testCase "a median is not moved by a minority: 7 of the 15 late saves slow is flat, 8 is growth" <| fun _ ->
      let lateWith (slow: int) = steady 100. 135 @ steady 1000. slow @ steady 100. (growthWindowSaves - slow)
      match verdictOf (lateWith 1), verdictOf (lateWith 7), verdictOf (lateWith 8) with
      | GrowthVerdict.Flat _, GrowthVerdict.Flat _, GrowthVerdict.Growing _ -> ()
      | other -> failtestf "the median needs a majority to move, got %A" other

    testCase "a series too short for three windows, an empty window and a zero baseline are each refused by name" <| fun _ ->
      curveOf growthWindowSaves (steady 100. (3 * growthWindowSaves - 1))
      |> Expect.equal "one save short" (Result.Error (CurveRefusal.TooFewSamples (3 * growthWindowSaves - 1, 3 * growthWindowSaves)))
      curveOf 0 (steady 100. 9)
      |> Expect.equal "no window" (Result.Error CurveRefusal.WindowIsEmpty)
      curveOf 3 (steady 0. 9)
      |> Expect.equal "a zero baseline" (Result.Error CurveRefusal.NoBaseline)

    testPropertyWithConfig cfg "a series that never speeds up never reads as growth below 1" <|
      fun (start: PositiveInt) (steps: NonNegativeInt list) ->
        let times =
          steps
          |> List.scan (fun total (NonNegativeInt step) -> total + step) start.Get
          |> List.map (fun millis -> ms (float millis))
        match curveOf 1 times with
        | Ok curve -> curve.Growth >= 1.0
        | Result.Error _ -> true

    testPropertyWithConfig cfg "any steady series, whatever its pace and length, has a growth of exactly 1" <|
      fun (pace: PositiveInt) (extra: byte) ->
        let window = growthWindowSaves
        let curve = curveOrFail window (steady (float pace.Get) (3 * window + int extra))
        curve.Growth = 1.0

    testCase "the curve line carries the name, the count, the three medians and the growth, on one line" <| fun _ ->
      let line = curveLine "hr-patch-save-to-served" (curveOrFail growthWindowSaves (steady 100. 135 @ steady 200. 15))
      for expected in [ "CURVE hr-patch-save-to-served"; "n=150"; "window=15"; "early-p50=100.0ms"; "middle-p50=100.0ms"; "late-p50=200.0ms"; "growth=2.00x" ] do
        line |> Expect.stringContains (sprintf "the line says %s" expected) expected
      line.Contains "\n" |> Expect.isFalse "one line, so a log grep finds it whole"

    testCase "the times line lists every save in the order it was made, in whole milliseconds" <| fun _ ->
      timesLine "hr-patch-save-to-served" [ ms 258.4; ms 341.; ms 259.6 ]
      |> Expect.equal "in save order" "SAMPLES hr-patch-save-to-served ms=258,341,260"

    testCase "the full plan is the stated series and windows" <| fun _ ->
      GrowthPlan.full |> Expect.equal "150 saves in windows of 15" { Saves = growthSeriesSaves; Window = growthWindowSaves }

    testCase "a shortened plan keeps three windows in the series, and refuses what cannot hold them" <| fun _ ->
      GrowthPlan.ofText "10" |> Expect.equal "the smoke run" (Ok { Saves = 10; Window = 3 })
      GrowthPlan.ofText "9" |> Expect.equal "the shortest" (Ok { Saves = 9; Window = 3 })
      GrowthPlan.ofText "45" |> Expect.equal "three full windows" (Ok { Saves = 45; Window = 15 })
      GrowthPlan.ofText "150" |> Expect.equal "the full series spelled out" (Ok GrowthPlan.full)
      GrowthPlan.ofText "8" |> Expect.equal "one short of the shortest" (Result.Error (GrowthPlanRefusal.TooFewSaves (8, 9)))
      GrowthPlan.ofText "-5" |> Expect.equal "negative" (Result.Error (GrowthPlanRefusal.TooFewSaves (-5, 9)))
      GrowthPlan.ofText "many" |> Expect.equal "not a number" (Result.Error (GrowthPlanRefusal.NotANumber "many"))

    testPropertyWithConfig cfg "every plan can be read as a curve of its own length" <|
      fun (saves: PositiveInt) ->
        match GrowthPlan.ofSaves saves.Get with
        | Ok plan -> (curveOf plan.Window (steady 100. plan.Saves) |> Result.isOk) && plan.Window <= growthWindowSaves
        | Result.Error _ -> saves.Get < 3 * shortestWindowSaves
  ]
