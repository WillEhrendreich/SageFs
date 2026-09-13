/// Roast-6 / multiagent-vision.md §3.4, §7.4, §10 Phase 0 item 1: the
/// eval-to-pixel latency chain and its budget RED tests.
///
/// The chain itself (AppState eval-requested -> Elm ModelChanged -> the
/// dashboard's push agent -> the SSE morph write) needs a live daemon and a
/// real browser stream to exercise end-to-end — that is covered by manual
/// live measurement against a throwaway daemon (see the task report), not by
/// the default suite. What CAN run fast and deterministically here, with
/// real numbers:
///   - the stamping/percentile math the statusline reads (EvalLatencyTrace);
///   - the pure burst-coalescing fold the push agent's back-pressure loop
///     applies before every render (StreamBurst, extended in
///     DashboardViewingTests.fs);
///   - the statusline actually rendering p50/p99 when they're present;
///   - the daemon-wide build semaphore's configured capacity.
module SageFs.Tests.EvalLatencyTraceTests

open System
open System.Diagnostics
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments

[<Tests>]
let evalLatencyTraceTests = testList "EvalLatencyTrace" [

  // Two DISJOINT usage domains, because the daemon and the FSI worker are
  // separate OS processes with their own `shared` tracker instance
  // (SessionManager spawns the worker as a subprocess; see the module doc
  // and AppState.fs's StampRequested comment). A single real chain never
  // sees both StampRequested and StampModelChanged on the same tracker.

  testCase "WHY (worker-side) — Requested/Finished alone never enters the ring — MorphWritten never fires inside the worker process, so this sample stays in flight forever on the worker's own tracker" <| fun _ ->
    let tracker = EvalLatencyTrace.Tracker(256)
    tracker.StampRequested() |> ignore
    System.Threading.Thread.Sleep(2)
    tracker.StampFinished()
    tracker.Snapshot() |> Expect.equal "nothing completed — the worker never reaches MorphWritten" []

  testCase "WHY (daemon-side) — a chain stamped ModelChanged through MorphWritten reports a positive total and enters the ring — because the statusline's p50/p99 read this ring, not the in-flight slot" <| fun _ ->
    let tracker = EvalLatencyTrace.Tracker(256)
    tracker.StampModelChanged()
    // A real, small, measurable gap — not a mock clock — so this proves the
    // Stopwatch-based math actually elapses time, not just that fields got set.
    System.Threading.Thread.Sleep(2)
    tracker.StampPushReceived()
    tracker.StampMorphWritten()
    let snapshot = tracker.Snapshot()
    snapshot.Length |> Expect.equal "one completed chain in the ring" 1
    let total = EvalLatencyTrace.Sample.totalMs snapshot.[0]
    total |> Expect.isSome "a chain that reached MorphWritten has a total"
    // Budget: vision §7.4 — "eval-to-pixel under 5ms once the sleep and the
    // render-then-compare are gone" for the daemon's OWN processing; the
    // instrumentation's overhead over a 2ms artificial gap must stay well
    // under any real render's budget, not balloon it. 200ms is a generous
    // CI-noise ceiling — the point is "close to the real ~2ms gap", not a
    // hard perf assertion on a shared CI runner.
    (total.Value, 200.0) |> Expect.isLessThan (sprintf "chain total %.3fms should track the ~2ms gap, not balloon" total.Value)
    (total.Value, 0.0) |> Expect.isGreaterThanOrEqual "chain total is never negative"

  testCase "WHY (daemon-side) — an incomplete chain never enters the ring — because a straggler mid-chain (a burst was still rendering when the next eval started) must not corrupt the p50/p99 window with a bogus tiny sample" <| fun _ ->
    let tracker = EvalLatencyTrace.Tracker(256)
    tracker.StampModelChanged()
    tracker.StampPushReceived()
    // No MorphWritten — the chain never reached the pixel.
    tracker.Snapshot() |> Expect.equal "nothing completed yet" []

  testCase "WHY (daemon-side) — every ModelChanged ALWAYS starts a fresh chain, discarding whatever was in flight — this was the actual bug found live: merging a late ModelChanged into an old un-completed sample produced a fabricated 'p50 51488.6ms' (the gap since a stale ModelChanged from long before any browser connected), not a real render latency" <| fun _ ->
    let tracker = EvalLatencyTrace.Tracker(256)
    // First ModelChanged: no SSE client connected, so it never reaches
    // Push/Morph — it would linger forever if ModelChanged merged instead
    // of restarting.
    tracker.StampModelChanged()
    System.Threading.Thread.Sleep(50)
    // Second ModelChanged: this is the one a now-connected client's push
    // agent will actually observe and render.
    tracker.StampModelChanged()
    tracker.StampPushReceived()
    tracker.StampMorphWritten()
    let snapshot = tracker.Snapshot()
    snapshot.Length |> Expect.equal "one completed chain" 1
    let total = (EvalLatencyTrace.Sample.totalMs snapshot.[0]).Value
    // If the first (stale) ModelChanged had leaked into this total, it would
    // be >= the 50ms sleep. It must instead track the SECOND ModelChanged,
    // which was immediately followed by Push/Morph with no sleep.
    (total, 50.0) |> Expect.isLessThan (sprintf "total %.3fms must reflect only the second ModelChanged, not the 50ms-stale first one" total)

  testCase "WHY — the ring caps at its capacity — because vision §3.4 specifies 'ring of the last 256', and an unbounded ring would grow forever over a long daemon session" <| fun _ ->
    let tracker = EvalLatencyTrace.Tracker(8)
    for _ in 1 .. 20 do
      tracker.StampRequested() |> ignore
      tracker.StampFinished()
      tracker.StampModelChanged()
      tracker.StampPushReceived()
      tracker.StampMorphWritten()
    tracker.Snapshot().Length |> Expect.equal "ring never exceeds its configured capacity" 8

  testCase "WHY — percentiles are nearest-rank over a known, hand-checked distribution — real numbers, not a mock" <| fun _ ->
    // 1..100 ms, nearest-rank: p50 -> ceil(0.50*100)=50th value (index 49) = 50.0;
    // p99 -> ceil(0.99*100)=99th value (index 98) = 99.0.
    let mkSample (ms: float) : EvalLatencyTrace.Sample =
      { EvalId = Guid.NewGuid()
        RequestedAt = 0L
        FinishedAt = Some 0L
        ModelChangedAt = Some 0L
        PushReceivedAt = Some 0L
        // totalMs = elapsedMs RequestedAt MorphWrittenAt, so encode `ms`
        // directly as a tick delta scaled by Stopwatch.Frequency.
        MorphWrittenAt = Some (int64 (ms * float Stopwatch.Frequency / 1000.0)) }
    let samples = [ 1.0 .. 100.0 ] |> List.map mkSample
    let p50, p99 = EvalLatencyTrace.percentiles samples
    p50 |> Expect.isSome "p50 present over 100 samples"
    p99 |> Expect.isSome "p99 present over 100 samples"
    (abs (p50.Value - 50.0), 0.01) |> Expect.isLessThan (sprintf "p50 should be ~50.0ms, was %.3f" p50.Value)
    (abs (p99.Value - 99.0), 0.01) |> Expect.isLessThan (sprintf "p99 should be ~99.0ms, was %.3f" p99.Value)

  testCase "WHY — percentiles is None over zero completed chains — because a freshly-started daemon has rendered nothing yet, and the statusline must render its absence, not a fabricated zero" <| fun _ ->
    EvalLatencyTrace.percentiles [] |> Expect.equal "no data, no percentiles" (None, None)

  testCase "WHY — the statusline renders p50/p99 when present — the RED test §10 item 1 names explicitly ('the statusline shows p50/p99')" <| fun _ ->
    let snap =
      { Version = "0.0.0"
        ConnectionState = DashboardConnectionState.Connected
        SessionState = "ready"; SessionId = "test-id"; WorkingDir = "/w"
        WarmupProgress = ""; WorkflowLabel = "REPL"
        EvalStats = { Count = 3; AvgMs = 1.0; MinMs = 1.0; MaxMs = 1.0; Sparkline = ""; P50Ms = None; P95Ms = None }
        AlarmPanel = Elem.div [] []; DaemonHealth = Elem.div [] []
        FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []
        FilmstripPanel = Elem.div [] []; ThemeName = "default"; ConnectionLabel = None
        HotReloadPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []
        SessionContextPanel = Elem.div [] []; OutputPanel = Elem.div [] []
        SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
        ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
        BindingsPanel = Elem.div [] []; FrictionPanel = Elem.div [] []; CohortPanel = Elem.div [] []
        ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning
        EvalToPixelP50Ms = Some 3.25
        EvalToPixelP99Ms = Some 18.75 }
    let html = renderMainContent snap |> renderNode
    html |> Expect.stringContains "statusline shows p50" "p50 3.2ms"
    html |> Expect.stringContains "statusline shows p99" "p99 18.8ms"

  testCase "WHY — the statusline renders nothing latency-shaped before any eval has completed the chain — an absent measurement must not masquerade as a fabricated 0ms" <| fun _ ->
    let snap =
      { Version = "0.0.0"
        ConnectionState = DashboardConnectionState.Connected
        SessionState = "ready"; SessionId = "test-id"; WorkingDir = "/w"
        WarmupProgress = ""; WorkflowLabel = "REPL"
        EvalStats = { Count = 0; AvgMs = 0.0; MinMs = 0.0; MaxMs = 0.0; Sparkline = ""; P50Ms = None; P95Ms = None }
        AlarmPanel = Elem.div [] []; DaemonHealth = Elem.div [] []
        FailureNarrativesPanel = Elem.div [] []; DiagnosticsPanel = Elem.div [] []
        FilmstripPanel = Elem.div [] []; ThemeName = "default"; ConnectionLabel = None
        HotReloadPanel = Elem.div [] []; LiveTestingPanel = Elem.div [] []
        SessionContextPanel = Elem.div [] []; OutputPanel = Elem.div [] []
        SessionsPanel = Elem.div [] []; SessionPicker = Elem.div [] []
        ThemePicker = Elem.div [] []; ThemeVars = Elem.div [] []
        BindingsPanel = Elem.div [] []; FrictionPanel = Elem.div [] []; CohortPanel = Elem.div [] []
        ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning
        EvalToPixelP50Ms = None
        EvalToPixelP99Ms = None }
    let html = renderMainContent snap |> renderNode
    (html.Contains "px p50") |> Expect.isFalse "no latency stat rendered when nothing has completed the chain yet"

  // ── §10 Phase 0 item 1's other named budgets: `decide`+`project` for ten
  // members/7,000 tests under 1ms p99, and the claim-probe under 10us. Those
  // types (`Cohort.decide`, `Cohort.project`, claims) are Phase 1 (§10 item
  // 7+) — they do not exist in this codebase yet. Pending placeholders keep
  // the budget visible in the suite (per §10's own instruction to gate only
  // "the parts that do" exist) without fabricating a benchmark against code
  // that isn't written.
  ptestCase "PENDING (Phase 1, §5.2/§7.1) — Cohort.decide + project for 10 members and 7,000 tests must stay under 1ms p99" <| fun _ ->
    failtest "Cohort.decide/project do not exist yet (vision §10 Phase 1 item 7)"

  ptestCase "PENDING (Phase 1, §5.1) — the save-observed claim probe must stay under 10us" <| fun _ ->
    failtest "Claims v1 do not exist yet (vision §10 Phase 1 item 9)"
]
