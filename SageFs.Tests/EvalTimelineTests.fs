module SageFs.Tests.EvalTimelineTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.EvalTimeline

let mkEntry cellId durationMs =
  { CellId = cellId; StartMs = FixtureDurations.timelineOrigin; DurationMs = durationMs; Status = Succeeded }

[<Tests>]
let evalTimelineTests = testList "EvalTimeline" [

  testList "Property-based" [
    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
      "sparkline length is at most width"
      (fun (width: PositiveInt) ->
        let w = width.Get |> min 50
        let state =
          { Entries = [ for i in 1 .. 10 -> mkEntry i (int64 (i * 10)) ] }
        let s = sparkline w state
        (s.Length, w) |> Expect.isLessThanOrEqual "length <= width")

    testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 100 }
      "timeline is always time-ordered after sequential records"
      (fun (durations: PositiveInt list) ->
        let entries =
          durations
          |> List.mapi (fun i d -> mkEntry i (int64 d.Get))
        let state =
          entries |> List.fold (fun s e -> TimelineState.record e s) TimelineState.empty
        // Entries are newest-first (prepend order), so CellIds should be descending
        let ids = state.Entries |> List.map (fun e -> e.CellId)
        let n = ids.Length
        ids |> Expect.equal "should preserve newest-first order"
          (List.init n (fun i -> n - 1 - i)))
  ]

  testList "Examples" [
    testCase "empty timeline produces empty sparkline" <| fun () ->
      sparkline 20 TimelineState.empty
      |> Expect.equal "should be empty" ""

    testCase "empty timeline produces None percentile" <| fun () ->
      percentile 50.0 TimelineState.empty
      |> Expect.isNone "should be None"

    testCase "single entry produces 1-char sparkline" <| fun () ->
      let state = TimelineState.record (mkEntry 0 100L) TimelineState.empty
      let s = sparkline 20 state
      s.Length |> Expect.equal "should be 1 char" 1

    testCase "WHY — EvalTimeline.recentChronological — the most RECENT entries survive truncation, in chronological order, because entries are stored newest-first and truncating after reversing would silently keep the oldest ones instead" <| fun () ->
      // 30 evals, recorded oldest (cell 0) to newest (cell 29); Entries end up
      // newest-first: [29; 28; ...; 1; 0].
      let state =
        [ 0 .. 29 ]
        |> List.fold (fun s i -> TimelineState.record (mkEntry i 10L) s) TimelineState.empty
      let recent = recentChronological 20 state
      recent |> List.map (fun e -> e.CellId)
      |> Expect.equal "the 20 most recent cells (10..29), oldest-first for display"
        [ 10 .. 29 ]

    testCase "WHY — EvalTimeline.recentChronological — asking for more than exist returns everything, still chronological" <| fun () ->
      let state =
        [ 0 .. 4 ]
        |> List.fold (fun s i -> TimelineState.record (mkEntry i 10L) s) TimelineState.empty
      recentChronological 20 state
      |> List.map (fun e -> e.CellId)
      |> Expect.equal "all 5, oldest-first" [ 0 .. 4 ]

    testCase "full range bars" <| fun () ->
      let entries = [ for i in 1 .. 8 -> mkEntry i (int64 (i * 10)) ]
      let state = entries |> List.fold (fun s e -> TimelineState.record e s) TimelineState.empty
      let s = sparkline 8 state
      s.Length |> Expect.equal "should have 8 chars" 8

    testCase "p50 calculation" <| fun () ->
      let entries = [ for i in 1 .. 100 -> mkEntry i (int64 i) ]
      let state = entries |> List.fold (fun s e -> TimelineState.record e s) TimelineState.empty
      let p50 = percentile 50.0 state
      p50 |> Expect.isSome "should have p50"

    testCase "mean calculation" <| fun () ->
      let entries = [ mkEntry 0 10L; mkEntry 1 20L; mkEntry 2 30L ]
      let state = entries |> List.fold (fun s e -> TimelineState.record e s) TimelineState.empty
      let stats = timelineStats 20 state
      stats.MeanMs |> Expect.equal "mean should be 20" (Some 20.0)

    testCase "width truncation" <| fun () ->
      let entries = [ for i in 1 .. 20 -> mkEntry i (int64 (i * 5)) ]
      let state = entries |> List.fold (fun s e -> TimelineState.record e s) TimelineState.empty
      let s = sparkline 10 state
      s.Length |> Expect.equal "should truncate to width" 10
  ]
]

/// A timeline built by recording `entries` in order, oldest first.
let recordAll (entries: TimelineEntry list) : TimelineState =
  entries |> List.fold (fun state entry -> TimelineState.record entry state) TimelineState.empty

/// A succeeded entry for `cellId` that took `durationMs`.
let succeededEntry (cellId: int) (durationMs: int64) : TimelineEntry =
  FixtureDurations.timelineEntry cellId durationMs Succeeded

[<Tests>]
let timelineRecordTests = testList "EvalTimeline.record keeps the newest entries up to MaxEntries" [

  testCase "record adds one entry" <| fun _ ->
    (recordAll [ succeededEntry 0 100L ]).Entries |> Expect.hasLength "one entry after one record" 1

  testCase "the newest entry is at the head" <| fun _ ->
    let state = recordAll [ succeededEntry 0 100L; succeededEntry 1 200L ]
    state.Entries.[0].CellId |> Expect.equal "the head is the most recent entry" 1

  testCase "entries are capped at MaxEntries" <| fun _ ->
    let state = recordAll [ for i in 0 .. TimelineState.MaxEntries + 49 -> succeededEntry i (int64 i) ]
    state.Entries |> Expect.hasLength "entries are capped at MaxEntries" TimelineState.MaxEntries

  testCase "the oldest entries are dropped when the cap is reached" <| fun _ ->
    let state = recordAll [ for i in 0 .. TimelineState.MaxEntries + 4 -> succeededEntry i (int64 i) ]
    state.Entries |> List.map (fun e -> e.CellId) |> List.contains 0
    |> Expect.isFalse "the oldest entry is dropped once the cap is exceeded"

  testCase "timelineStats Count matches the number of recorded entries" <| fun _ ->
    let state = recordAll [ for i in 0 .. 4 -> succeededEntry i (int64 (i + 1) * 10L) ]
    (timelineStats 20 state).Count |> Expect.equal "Count matches 5 entries" 5

  testCase "the sparkline of a non-empty timeline is not empty" <| fun _ ->
    let state = recordAll [ for i in 0 .. 9 -> succeededEntry i (int64 (i + 1) * 50L) ]
    (timelineStats 20 state).Sparkline |> Expect.isNotEmpty "the sparkline is non-empty for a non-empty timeline"

  testCase "the sparkline is no wider than the width asked for" <| fun _ ->
    let state = recordAll [ for i in 0 .. 49 -> succeededEntry i (int64 (i + 1) * 10L) ]
    (timelineStats 10 state).Sparkline.Length |> Expect.equal "the sparkline is limited to width 10" 10
]

[<Tests>]
let timelineSparklineWindowTests = testList "EvalTimeline.sparkline scales to the visible window" [

  testCase "an outlier in the old entries does not flatten the visible bars" <| fun _ ->
    // One old outlier, then 20 evals of 10 ms. With width 20 the outlier is out of view, so the
    // tallest visible bar is 10 ms and the newest bar must not be the minimum glyph.
    let state =
      recordAll (succeededEntry 0 FixtureDurations.outlierEvalMs :: [ for i in 1 .. 20 -> succeededEntry i 10L ])
    let sparkline = sparkline 20 state
    string sparkline.[sparkline.Length - 1]
    |> Expect.notEqual "the most recent bar is not collapsed to the minimum" "▁"

  testCase "uniform durations fill to the same bar height" <| fun _ ->
    let state = recordAll [ for i in 0 .. 9 -> succeededEntry i 100L ]
    sparkline 10 state |> Seq.toList |> List.distinct
    |> Expect.hasLength "all uniform bars are the same glyph" 1
]

[<Tests>]
let timelinePercentileRoundingTests = testList "EvalTimeline.percentile rounds to the nearest rank, it does not truncate" [

  testCase "P99 of two samples is the maximum, not the minimum" <| fun _ ->
    // int (1 * 99 / 100) truncates to 0 and would return the 50 ms sample; rounding gives the 2000 ms one.
    let state = recordAll [ succeededEntry 0 50L; succeededEntry 1 2000L ]
    percentile 99.0 state |> Expect.equal "P99 of [50 ms, 2000 ms] is 2000 ms" (Some 2000.0)

  testCase "P95 of two samples is the maximum" <| fun _ ->
    let state = recordAll [ succeededEntry 0 10L; succeededEntry 1 500L ]
    percentile 95.0 state |> Expect.equal "P95 of [10 ms, 500 ms] is 500 ms" (Some 500.0)

  testCase "P50 of three samples is the median" <| fun _ ->
    let state = recordAll [ succeededEntry 0 10L; succeededEntry 1 50L; succeededEntry 2 100L ]
    percentile 50.0 state |> Expect.equal "P50 of [10, 50, 100] is 50" (Some 50.0)

  testCase "P99 is never the minimum for any sample count from 2 to 10" <| fun _ ->
    for sampleCount in 2 .. 10 do
      let entries = [ for i in 0 .. sampleCount - 1 -> succeededEntry i (int64 (i + 1) * 100L) ]
      let minimum = entries |> List.map (fun e -> float e.DurationMs) |> List.min
      match percentile 99.0 (recordAll entries) with
      | None -> failtestf "P99 should exist for n=%d" sampleCount
      | Some value -> value |> Expect.notEqual (sprintf "P99 of n=%d is not the minimum %g" sampleCount minimum) minimum

  testCase "P100 is the maximum" <| fun _ ->
    let state = recordAll [ for i in 0 .. 4 -> succeededEntry i (int64 (i + 1) * 10L) ]
    percentile 100.0 state |> Expect.equal "P100 is the maximum" (Some 50.0)
]
