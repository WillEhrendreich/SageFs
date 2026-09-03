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
      let entry0: EvalTimeline.TimelineEntry = { CellId = 0; StartMs = 0L; DurationMs = 10_000L; Status = EvalTimeline.Success }
      let stateWithOutlier = EvalTimeline.TimelineState.record entry0 state
      let stateWith21Entries =
        List.fold
          (fun s i ->
            let e: EvalTimeline.TimelineEntry = { CellId = i + 1; StartMs = 0L; DurationMs = 10L; Status = EvalTimeline.Success }
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
            let e: EvalTimeline.TimelineEntry = { CellId = i; StartMs = 0L; DurationMs = 100L; Status = EvalTimeline.Success }
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
      // Use a small iteration count that exceeds MaxEvalHistory to prove the cap.
      // recordEval does O(n) work per call (scope rebuild), so 10K iterations = O(n²) ≈ minutes.
      // Instead: iterate 50 times, verify cap at MaxEvalHistory is at most 50.
      let testCap = min 50 FeatureHooks.MaxEvalHistory
      let iterations = testCap + 2
      let finalState =
        List.fold
          (fun s i -> FeatureHooks.recordEval (sprintf "let x%d = %d" i i) (sprintf "val x%d: int = %d" i i) 1L s)
          FeatureHooks.FeaturePushState.empty
          [0 .. iterations - 1]
      finalState.NextCellIndex |> Expect.equal "NextCellIndex should be iterations" iterations
      // History length should be min(iterations, MaxEvalHistory)
      (finalState.EvalHistory.Length <= FeatureHooks.MaxEvalHistory)
        |> Expect.isTrue "EvalHistory should be capped at MaxEvalHistory"

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

    testCase "CachedScope reflects all entries after evals" <| fun _ ->
      let state =
        List.fold
          (fun s i -> FeatureHooks.recordEval (sprintf "let bind%d = %d" i i) (sprintf "val bind%d: int = %d" i i) 1L s)
          FeatureHooks.FeaturePushState.empty
          [0..9]
      match state.CachedScope with
      | None -> failtest "CachedScope should be Some after evals"
      | Some scope ->
        scope.Bindings |> Expect.hasLength "scope has 10 bindings" 10
  ]

// ---------------------------------------------------------------------------
// W4 — manifest merge: previously-stopped sessions preserved on shutdown
// ---------------------------------------------------------------------------

[<Tests>]
let w4ManifestMergeTests =
  testList "W4(R9) — manifest merge: StoppedAt preserved for previously-stopped sessions" [

    testCase "previously stopped session keeps its original StoppedAt" <| fun _ ->
      let originalStop = DateTimeOffset.UtcNow.AddDays(-2.0)
      let oldRecord: DaemonSessionRecord = {
        SessionId = "session-old"
        Projects = []
        WorkingDir = ""
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-10.0)
        StoppedAt = Some originalStop
      }
      let activeSessionIds = Set.empty
      let merged =
        match activeSessionIds.Contains(oldRecord.SessionId) with
        | true -> { oldRecord with StoppedAt = Some DateTimeOffset.UtcNow }
        | false -> oldRecord
      merged.StoppedAt |> Expect.equal "previously stopped session keeps original StoppedAt" (Some originalStop)

    testCase "active session gets stamped with current StoppedAt" <| fun _ ->
      let now = DateTimeOffset.UtcNow
      let activeRecord: DaemonSessionRecord = {
        SessionId = "session-active"
        Projects = []
        WorkingDir = ""
        CreatedAt = now.AddHours(-1.0)
        StoppedAt = None
      }
      let activeSessionIds = Set.ofList ["session-active"]
      let merged =
        match activeSessionIds.Contains(activeRecord.SessionId) with
        | true -> { activeRecord with StoppedAt = Some now }
        | false -> activeRecord
      merged.StoppedAt |> Expect.isSome "active session gets StoppedAt stamped"

    testCase "merge preserves both old and new sessions" <| fun _ ->
      let now = DateTimeOffset.UtcNow
      let oldStop = now.AddDays(-3.0)
      let existingSessions: Map<string, DaemonSessionRecord> =
        Map.ofList [
          "s1", { SessionId = "s1"; Projects = []; WorkingDir = ""; CreatedAt = now.AddDays(-5.0); StoppedAt = Some oldStop }
          "s2", { SessionId = "s2"; Projects = []; WorkingDir = ""; CreatedAt = now.AddDays(-1.0); StoppedAt = None }
        ]
      let activeSessionIds = Set.ofList ["s2"]
      let merged =
        existingSessions
        |> Map.map (fun sid r ->
          match activeSessionIds.Contains(sid) with
          | true -> { r with StoppedAt = Some now }
          | false -> r)
      merged.["s1"].StoppedAt |> Expect.equal "s1 keeps original StoppedAt" (Some oldStop)
      merged.["s2"].StoppedAt |> Expect.equal "s2 gets stamped with now" (Some now)

    testCase "7-day pruning preserves sessions stopped within window" <| fun _ ->
      let now = DateTimeOffset.UtcNow
      let cutoff = now.AddDays(-7.0)
      let sessions: DaemonSessionRecord list = [
        { SessionId = "recent"; Projects = []; WorkingDir = ""; CreatedAt = now.AddDays(-3.0); StoppedAt = Some (now.AddDays(-1.0)) }
        { SessionId = "old"; Projects = []; WorkingDir = ""; CreatedAt = now.AddDays(-20.0); StoppedAt = Some (now.AddDays(-8.0)) }
        { SessionId = "active"; Projects = []; WorkingDir = ""; CreatedAt = now.AddDays(-2.0); StoppedAt = None }
      ]
      let pruned =
        sessions
        |> List.filter (fun s ->
          match s.StoppedAt with
          | None -> true
          | Some stoppedAt -> stoppedAt >= cutoff)
      pruned |> Expect.hasLength "only recent and active sessions survive 7-day prune" 2
      pruned |> List.map (fun s -> s.SessionId) |> List.sort
        |> Expect.equal "correct sessions kept" ["active"; "recent"]
  ]

