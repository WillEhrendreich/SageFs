module SageFs.Tests.Round9HardeningTests

open System
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open SageFs.Features
open SageFs.Features.ManifestTypes
open SageFs.Features.DaemonManifest

// ---------------------------------------------------------------------------
// W3 — sparkline maxDur uses visible window only (not all 1000 entries)
// ---------------------------------------------------------------------------

[<Tests>]
let w3SparklineWindowTests =
  testList "W3(R9) — sparkline: maxDur from visible window, not entire history" [

    testCase "outlier in old entries does not compress visible bars to minimum" <| fun _ ->
      let state = EvalTimeline.TimelineState.empty
      // Simulate an old outlier at 10_000 ms followed by many small evals at ~10 ms
      let entry0: EvalTimeline.TimelineEntry = { CellId = 0; StartMs = 0L; DurationMs = 10_000L; Status = EvalTimeline.Succeeded }
      let stateWithOutlier = EvalTimeline.TimelineState.record entry0 state
      let stateWith21Entries =
        List.fold
          (fun s i ->
            let e: EvalTimeline.TimelineEntry = { CellId = i + 1; StartMs = 0L; DurationMs = 10L; Status = EvalTimeline.Succeeded }
            EvalTimeline.TimelineState.record e s)
          stateWithOutlier
          [1..20]
      // The outlier is now at the tail (oldest). sparkline width=20 shows only the 20 recent 10ms bars.
      // maxDur from visible window = 10.0 ms, not 10_000.0 ms → bars should be tall, not ▁.
      let sparkline = EvalTimeline.sparkline 20 stateWith21Entries
      let lastBar = string sparkline.[sparkline.Length - 1]
      lastBar |> Expect.notEqual "most recent bar should not be collapsed to minimum" "▁"

    testCase "sparkline with uniform durations fills to the same bar height" <| fun _ ->
      let state =
        List.fold
          (fun s i ->
            let e: EvalTimeline.TimelineEntry = { CellId = i; StartMs = 0L; DurationMs = 100L; Status = EvalTimeline.Succeeded }
            EvalTimeline.TimelineState.record e s)
          EvalTimeline.TimelineState.empty
          [0..9]
      let sparkline = EvalTimeline.sparkline 10 state
      // All bars identical duration → all should be the same character
      let chars = sparkline |> Seq.toList |> List.distinct
      chars |> Expect.hasLength "all uniform bars should be same char" 1

    testCase "empty timeline produces empty sparkline" <| fun _ ->
      let sparkline = EvalTimeline.sparkline 20 EvalTimeline.TimelineState.empty
      sparkline |> Expect.equal "empty timeline → empty sparkline" ""
  ]

// ---------------------------------------------------------------------------
// W6 — ReferencedIn uses word-boundary regex, not substring Contains
// ---------------------------------------------------------------------------

[<Tests>]
let w6WordBoundaryRefTests =
  testList "W6(R9) — ReferencedIn: word-boundary regex prevents false positives" [

    testCase "short binding name does not match as substring inside longer identifier" <| fun _ ->
      let cells: BindingExplorer.CellInput list = [
        { CellIndex = 0; FsiOutput = "val x: int = 1"; Source = "let x = 1" }
        { CellIndex = 1; FsiOutput = "val maxValue: int = 100"; Source = "let maxValue = 100" }
        { CellIndex = 2; FsiOutput = ""; Source = "printfn \"%d\" maxValue" }
      ]
      let snapshot = BindingExplorer.buildScopeSnapshot cells
      // 'x' binding at cell 0 should NOT reference cell 1 (maxValue contains 'x' as substring)
      // Cell 2 source has 'maxValue' which contains 'x' as substring in "maxValue" — no standalone \bx\b
      let xBinding = snapshot.Bindings |> List.find (fun b -> b.Name = "x")
      xBinding.ReferencedIn |> Expect.isEmpty "x binding should have no references (only appears as substring)"

    testCase "binding name matches when used as standalone word" <| fun _ ->
      let cells: BindingExplorer.CellInput list = [
        { CellIndex = 0; FsiOutput = "val count: int = 5"; Source = "let count = 5" }
        { CellIndex = 1; FsiOutput = ""; Source = "let doubled = count * 2" }
        { CellIndex = 2; FsiOutput = ""; Source = "let discounted = price - 1" }  // 'count' in 'discounted' — substring
      ]
      let snapshot = BindingExplorer.buildScopeSnapshot cells
      let countBinding = snapshot.Bindings |> List.find (fun b -> b.Name = "count")
      // Cell 1 uses 'count' as a word — should match
      // Cell 2 has 'discounted' which contains 'count' as substring — should NOT match
      countBinding.ReferencedIn |> Expect.equal "count should only be referenced in cell 1" [1]

    testCase "single-letter binding only matches standalone word not every occurrence" <| fun _ ->
      let cells: BindingExplorer.CellInput list = [
        { CellIndex = 0; FsiOutput = "val i: int = 42"; Source = "let i = 42" }
        { CellIndex = 1; FsiOutput = ""; Source = "printfn \"result\" " }  // no 'i' standalone
        { CellIndex = 2; FsiOutput = ""; Source = "let j = i + 1" }        // 'i' as standalone
      ]
      let snapshot = BindingExplorer.buildScopeSnapshot cells
      let iBinding = snapshot.Bindings |> List.find (fun b -> b.Name = "i")
      iBinding.ReferencedIn |> Expect.equal "i referenced only in cell 2" [2]
  ]

// ---------------------------------------------------------------------------
// W1+W5 — EvalHistory cap + NextCellIndex monotonic counter
// ---------------------------------------------------------------------------

[<Tests>]
let w1w5EvalHistoryCapTests =
  testList "W1+W5(R9) — EvalHistory: cap at MaxEvalHistory, NextCellIndex monotonic" [

    testCase "CellIndex increments from 0 regardless of EvalHistory length" <| fun _ ->
      let state0 = FeatureHooks.FeaturePushState.empty
      let state1 = FeatureHooks.recordEval "let x = 1" "val x: int = 1" 5L state0
      let state2 = FeatureHooks.recordEval "let y = 2" "val y: int = 2" 5L state1
      let state3 = FeatureHooks.recordEval "let z = 3" "val z: int = 3" 5L state2
      state1.EvalHistory.Head.CellIndex |> Expect.equal "first eval gets CellIndex 0" 0
      state2.EvalHistory.Head.CellIndex |> Expect.equal "second eval gets CellIndex 1" 1
      state3.EvalHistory.Head.CellIndex |> Expect.equal "third eval gets CellIndex 2" 2

    testCase "NextCellIndex advances even when EvalHistory is at cap" <| fun _ ->
      // An injected 50-cell cap, exceeded by 2 evals, so the cap is really hit.
      let testCap = 50
      let iterations = testCap + 2
      let cap =
        match EvalStore.HistoryCap.tryCreate testCap with
        | Ok c -> c
        | Error reason -> failtest reason
      let finalState =
        List.fold
          (fun s i -> FeatureHooks.recordEval (sprintf "let x%d = %d" i i) (sprintf "val x%d: int = %d" i i) 1L s)
          (FeatureHooks.FeaturePushState.withCap cap)
          [0 .. iterations - 1]
      finalState.NextCellIndex |> Expect.equal "NextCellIndex should be iterations" iterations
      finalState.EvalHistory.Length |> Expect.equal "EvalHistory holds exactly the cap" testCap

    testCase "no duplicate CellIndex values after many evals" <| fun _ ->
      let testCap = min 50 FeatureHooks.MaxEvalHistory
      let iterations = testCap + 5
      let finalState =
        List.fold
          (fun s i -> FeatureHooks.recordEval (sprintf "let v%d = %d" i i) (sprintf "val v%d: int = %d" i i) 1L s)
          FeatureHooks.FeaturePushState.empty
          [0 .. iterations - 1]
      let indices = finalState.EvalHistory |> List.map (fun e -> e.CellIndex)
      let distinctIndices = indices |> List.distinct
      distinctIndices |> Expect.hasLength "all CellIndex values in history should be unique" indices.Length

    testCase "EvalHistory is newest-first (head = most recent)" <| fun _ ->
      let state =
        List.fold
          (fun s i -> FeatureHooks.recordEval (sprintf "let q%d = %d" i i) (sprintf "val q%d: int = %d" i i) 1L s)
          FeatureHooks.FeaturePushState.empty
          [0..4]
      state.EvalHistory.Head.CellIndex |> Expect.equal "head of history is most recent" 4

    testCase "the scope reflects all entries after evals" <| fun _ ->
      let state =
        List.fold
          (fun s i -> FeatureHooks.recordEval (sprintf "let bind%d = %d" i i) (sprintf "val bind%d: int = %d" i i) 1L s)
          FeatureHooks.FeaturePushState.empty
          [0..9]
      (FeatureHooks.scope state).Bindings |> Expect.hasLength "scope has 10 bindings" 10
  ]


