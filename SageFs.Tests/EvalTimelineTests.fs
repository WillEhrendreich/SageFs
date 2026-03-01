module SageFs.Tests.EvalTimelineTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.EvalTimeline

let mkEntry cellId durationMs =
  { CellId = cellId; StartMs = 0L; DurationMs = durationMs; Status = Success }

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
        let ids = state.Entries |> List.map (fun e -> e.CellId)
        ids |> Expect.equal "should preserve insertion order"
          (List.init ids.Length id))
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
