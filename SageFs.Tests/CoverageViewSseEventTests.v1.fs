module SageFs.Tests.CoverageViewSseEventTests

open System.Text.Json
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SseWriter
open SageFs.Features.LiveTesting

/// WHY bullets up front (one per test, in test names).
/// 1. SSE — emits a 'coverage_view' event because Neovim/VSCode/VS each
///    render the per-function aggregate natively and they need a stable
///    event name to subscribe to.
/// 2. SSE — payload includes symbol/filePath/line so the editor places
///    the badge without a second lookup.
/// 3. SSE — payload serializes the inline badge so editors render the
///    compact "✓ 97 ✗ 3" without recomputing.
/// 4. SSE — payload includes HasOverflow so the editor renders "▾ +N more"
///    indicator without doing arithmetic.
/// 5. SSE — payload's TotalCount is emitted so editors show a tooltip with
///    the absolute number even when the badge is collapsed.

let private makeOpts () =
  let o = JsonSerializerOptions()
  o.Converters.Add(System.Text.Json.Serialization.JsonFSharpConverter())
  o

let private sampleView =
  { CoverageView.Symbol = "Module.add"
    FilePath = "Prod.fs"
    DefinitionLine = 42
    TotalCount = 100
    InlineBadge = [ CoverageBadge.Pass 97; CoverageBadge.Fail 3 ]
    FailingTests = [||]
    HasOverflow = false
    Health = CoverageHealth.SomeFailing }

let private overflowView =
  { sampleView with HasOverflow = true; TotalCount = 200 }

let private allPassingView =
  { sampleView with
      Health = CoverageHealth.AllPassing
      InlineBadge = [ CoverageBadge.Pass 100 ] }

[<Tests>]
let sseCoverageViewEventTests = testList "SSE CoverageView event" [

  testCase "WHY — SSE — emits a 'coverage_view' event because Neovim/VSCode/VS each render the per-function aggregate natively and they need a stable event name to subscribe to" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringStarts "must name the event" "event: coverage_view\n"

  testCase "WHY — SSE — payload includes the symbol so the editor can place the badge without a second lookup" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringContains "symbol present" "Module.add"

  testCase "WHY — SSE — payload includes the file path so the editor can match annotations to the buffer" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringContains "filePath present" "Prod.fs"

  testCase "WHY — SSE — payload includes the definition line so the editor places the badge at the right position" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringContains "DefinitionLine key present" "DefinitionLine"

  testCase "WHY — SSE — payload serializes the inline badge so editors render the compact text '✓ 97 ✗ 3' without recomputing" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringContains "InlineBadge field present" "InlineBadge"

  testCase "WHY — SSE — payload includes HasOverflow so the editor renders '▾ +N more' indicator without doing arithmetic" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None overflowView
    sse |> Expect.stringContains "HasOverflow field present" "HasOverflow"
    sse |> Expect.stringContains "HasOverflow value is true" "true"

  testCase "WHY — SSE — payload's TotalCount is emitted so editors show a tooltip with the absolute number even when the badge is collapsed" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None sampleView
    sse |> Expect.stringContains "TotalCount field present" "TotalCount"
    sse |> Expect.stringContains "TotalCount value present" "100"

  testCase "WHY — SSE — includes session id when provided so per-session rendering targets the right window" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) (Some "sess-1") sampleView
    sse |> Expect.stringContains "session id injected" "sess-1"

  testCase "WHY — SSE — empty view serializes without error so a temporarily-empty buffer is still valid JSON" <| fun _ ->
    let emptyView =
      { CoverageView.Symbol = ""
        FilePath = ""
        DefinitionLine = 0
        TotalCount = 0
        InlineBadge = []
        FailingTests = [||]
        HasOverflow = false
        Health = CoverageHealth.AllPassing }
    let sse = formatCoverageViewEvent (makeOpts()) None emptyView
    sse |> Expect.stringStarts "empty view still emits the event" "event: coverage_view\n"

  testCase "WHY — SSE — all-passing view with one Pass badge serializes without error so a clean codebase can rely on the event" <| fun _ ->
    let sse = formatCoverageViewEvent (makeOpts()) None allPassingView
    sse |> Expect.stringContains "Pass variant serialised" "Pass"
    sse |> Expect.stringContains "count present" "100"
  ]

