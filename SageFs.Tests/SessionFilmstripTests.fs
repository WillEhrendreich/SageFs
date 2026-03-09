module SageFs.Tests.SessionFilmstripTests

open System
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.SessionFilmstrip

[<Tests>]
let filmstripBuildTests =
  testList "SessionFilmstrip build" [

    testCase "empty events yield empty filmstrip" <| fun _ ->
      let frames = buildFilmstrip []
      frames |> Expect.hasLength "empty" 0

    testCase "events produce frames with indices" <| fun _ ->
      let events = [
        { Timestamp = DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero); Label = "let x = 1"; BindingCount = 1; TestSummary = None; EvalDurationMs = Some 5.0 }
        { Timestamp = DateTimeOffset(2024, 1, 1, 10, 0, 1, TimeSpan.Zero); Label = "let y = 2"; BindingCount = 2; TestSummary = None; EvalDurationMs = Some 3.0 }
      ]
      let frames = buildFilmstrip events
      frames |> Expect.hasLength "two frames" 2
      frames.[0].Index |> Expect.equal "first idx" 0
      frames.[1].Index |> Expect.equal "second idx" 1

    testCase "frames preserve timestamps" <| fun _ ->
      let ts = DateTimeOffset(2024, 6, 15, 12, 30, 0, TimeSpan.Zero)
      let events = [ { Timestamp = ts; Label = "eval"; BindingCount = 0; TestSummary = None; EvalDurationMs = None } ]
      let frames = buildFilmstrip events
      frames.[0].Timestamp |> Expect.equal "preserved" ts

    testCase "frames include test summary when present" <| fun _ ->
      let events = [
        { Timestamp = DateTimeOffset.UtcNow; Label = "test run"; BindingCount = 5; TestSummary = Some "3/3 passed"; EvalDurationMs = Some 100.0 }
      ]
      let frames = buildFilmstrip events
      frames.[0].TestSummary |> Expect.equal "has summary" (Some "3/3 passed")
  ]

[<Tests>]
let filmstripFilterTests =
  testList "SessionFilmstrip filter" [

    testCase "filter by label substring" <| fun _ ->
      let events = [
        { Timestamp = DateTimeOffset.UtcNow; Label = "let x = 1"; BindingCount = 1; TestSummary = None; EvalDurationMs = None }
        { Timestamp = DateTimeOffset.UtcNow; Label = "let y = 2"; BindingCount = 2; TestSummary = None; EvalDurationMs = None }
        { Timestamp = DateTimeOffset.UtcNow; Label = "open System"; BindingCount = 2; TestSummary = None; EvalDurationMs = None }
      ]
      let frames = buildFilmstrip events
      let filtered = filterFrames "let" frames
      filtered |> Expect.hasLength "two matches" 2

    testCase "filter case insensitive" <| fun _ ->
      let events = [
        { Timestamp = DateTimeOffset.UtcNow; Label = "List.map"; BindingCount = 1; TestSummary = None; EvalDurationMs = None }
      ]
      let frames = buildFilmstrip events
      let filtered = filterFrames "list" frames
      filtered |> Expect.hasLength "match" 1

    testCase "empty filter returns all" <| fun _ ->
      let events = [
        { Timestamp = DateTimeOffset.UtcNow; Label = "a"; BindingCount = 0; TestSummary = None; EvalDurationMs = None }
        { Timestamp = DateTimeOffset.UtcNow; Label = "b"; BindingCount = 0; TestSummary = None; EvalDurationMs = None }
      ]
      let frames = buildFilmstrip events
      let filtered = filterFrames "" frames
      filtered |> Expect.hasLength "all" 2
  ]

[<Tests>]
let filmstripRenderTests =
  testList "SessionFilmstrip render" [

    testCase "render frame as compact card" <| fun _ ->
      let frame = {
        Index = 0
        Timestamp = DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero)
        Label = "let x = 1"
        BindingCount = 1
        TestSummary = None
        EvalDurationMs = Some 5.0
      }
      let s = renderFrame frame
      s |> Expect.stringContains "has label" "let x = 1"
      s |> Expect.stringContains "has duration" "5"

    testCase "render frame with test summary" <| fun _ ->
      let frame = {
        Index = 3
        Timestamp = DateTimeOffset.UtcNow
        Label = "run tests"
        BindingCount = 10
        TestSummary = Some "5/5 passed"
        EvalDurationMs = Some 200.0
      }
      let s = renderFrame frame
      s |> Expect.stringContains "has tests" "5/5 passed"

    testCase "render filmstrip overview" <| fun _ ->
      let frames = [
        { Index = 0; Timestamp = DateTimeOffset.UtcNow; Label = "a"; BindingCount = 1; TestSummary = None; EvalDurationMs = Some 1.0 }
        { Index = 1; Timestamp = DateTimeOffset.UtcNow; Label = "b"; BindingCount = 2; TestSummary = None; EvalDurationMs = Some 2.0 }
        { Index = 2; Timestamp = DateTimeOffset.UtcNow; Label = "c"; BindingCount = 3; TestSummary = Some "ok"; EvalDurationMs = Some 3.0 }
      ]
      let s = renderOverview frames
      s |> Expect.stringContains "has count" "3 frames"

    testCase "sparkline from durations" <| fun _ ->
      let frames = [
        { Index = 0; Timestamp = DateTimeOffset.UtcNow; Label = "a"; BindingCount = 0; TestSummary = None; EvalDurationMs = Some 1.0 }
        { Index = 1; Timestamp = DateTimeOffset.UtcNow; Label = "b"; BindingCount = 0; TestSummary = None; EvalDurationMs = Some 5.0 }
        { Index = 2; Timestamp = DateTimeOffset.UtcNow; Label = "c"; BindingCount = 0; TestSummary = None; EvalDurationMs = Some 3.0 }
      ]
      let spark = sparkline frames
      spark.Length |> Expect.equal "three chars" 3
  ]
