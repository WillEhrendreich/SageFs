module SageFs.Tests.CoverageViewSseEventTests

open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SseWriter
open SageFs.Features.LiveTesting

/// WHY bullets (one per test, in test names).
/// 1. SSE - emits a 'coverage_view' event so all three editors can
///    render the per-function aggregate natively without a second
///    lookup.
/// 2. SSE - payload's Overflow is a DU so the renderer gets the exact
///    hidden count, not just a flag (e.g. "47" not "true").
/// 3. SSE - payload includes Symbol + FilePath + DefinitionLine so
///    the editor can place the badge without a second lookup.

let private makeOpts () =
  let o = JsonSerializerOptions()
  o.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
  o

let private sampleView =
  { CoverageView.Symbol = "Module.add"
    FilePath = "Prod.fs"
    DefinitionLine = 42
    TotalCount = 100
    Overflow = Overflow.Overflow 3
    InlineBadgeText = "v 97 x 3"
    Health = CoverageViewState.Failing }

let private withinView =
  { sampleView with
      Overflow = Overflow.Within
      TotalCount = 1
      InlineBadgeText = "v 1" }

let private absentView =
  { CoverageView.Symbol = ""
    FilePath = ""
    DefinitionLine = 0
    TotalCount = 0
    Overflow = Overflow.Within
    InlineBadgeText = ""
    Health = CoverageViewState.Absent }

[<Tests>]
let sseCoverageViewEventTests = testList "SSE CoverageView event v2" [

  testCase "WHY - SSE - emits a 'coverage_view' event because all three editors subscribe to this event name to render the per-function aggregate" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringStarts "must name the event" "event: coverage_view\n"

  testCase "WHY - SSE - payload's Overflow is serialized as a DU with the hidden count so the renderer can show 'v +N more' exactly" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringContains "Overflow case present" "Overflow"
    sse |> Expect.stringContains "hidden count 3" "3"

  testCase "WHY - SSE - payload's Within overflow produces an empty indicator (no 'v +0 more')" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None withinView
    sse |> Expect.stringContains "Within case" "Within"

  testCase "WHY - SSE - payload includes Symbol so the editor can place the badge without a second lookup" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringContains "symbol present" "Module.add"

  testCase "WHY - SSE - payload includes FilePath so the editor matches the view to the buffer" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringContains "filePath present" "Prod.fs"

  testCase "WHY - SSE - payload includes DefinitionLine so the editor places the badge at the right position" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringContains "DefinitionLine key present" "DefinitionLine"

  testCase "WHY - SSE - payload includes session id when provided so per-session rendering targets the right window" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) (Some "sess-1") sampleView
    sse |> Expect.stringContains "session id injected" "sess-1"

  testCase "WHY - SSE - empty view (TotalCount=0) serializes without error so a temporarily-empty buffer is still valid JSON" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None absentView
    sse |> Expect.stringStarts "empty view still emits the event" "event: coverage_view\n"
  ]
