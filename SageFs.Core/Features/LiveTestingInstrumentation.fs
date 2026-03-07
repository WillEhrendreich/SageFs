namespace SageFs.Features.LiveTesting

open System.Diagnostics

/// OTEL instrumentation for the live testing cycle.
/// ActivitySource + Meter are BCL types (System.Diagnostics).
/// When no collector is attached, StartActivity returns null (~50ns).
/// Histograms no-op when no listener is registered.
module LiveTestingInstrumentation =

  let activitySource = new ActivitySource("SageFs.LiveTesting")
  let meter = new System.Diagnostics.Metrics.Meter("SageFs.LiveTesting")

  let treeSitterHistogram =
    meter.CreateHistogram<float>("sagefs.live_testing.treesitter_ms")
  let fcsHistogram =
    meter.CreateHistogram<float>("sagefs.live_testing.fcs_ms")
  let executionHistogram =
    meter.CreateHistogram<float>("sagefs.live_testing.execution_ms")

  // Per-test execution metrics
  let perTestDurationMs =
    meter.CreateHistogram<float>("sagefs.live_testing.per_test_ms", unit = "ms", description = "Individual test execution duration")
  let testsCompleted =
    meter.CreateCounter<int64>("sagefs.live_testing.tests_completed_total", description = "Total tests completed (any outcome)")
  let testsTimedOut =
    meter.CreateCounter<int64>("sagefs.live_testing.tests_timed_out_total", description = "Tests that hit the per-test timeout")
  let testsFailed =
    meter.CreateCounter<int64>("sagefs.live_testing.tests_failed_total", description = "Tests that failed with assertion or exception")
  let testsPassed =
    meter.CreateCounter<int64>("sagefs.live_testing.tests_passed_total", description = "Tests that passed")

  // Chunk-level metrics
  let chunkDurationMs =
    meter.CreateHistogram<float>("sagefs.live_testing.chunk_ms", unit = "ms", description = "Per-chunk execution duration")
  let chunksCompleted =
    meter.CreateCounter<int64>("sagefs.live_testing.chunks_completed_total", description = "Total chunks completed")

  // Stream-level metrics
  let streamDurationMs =
    meter.CreateHistogram<float>("sagefs.live_testing.stream_ms", unit = "ms", description = "Full test stream duration")
  let streamResultsEmitted =
    meter.CreateCounter<int64>("sagefs.live_testing.stream_results_emitted_total", description = "Total results emitted via SSE stream")

  // Dep graph decision metrics (panel priority: measure fallback rate)
  let depGraphMatchTotal =
    meter.CreateCounter<int64>("sagefs.live_testing.depgraph_match_total", description = "Dep graph found affected tests for changed symbols")
  let depGraphFallbackTotal =
    meter.CreateCounter<int64>("sagefs.live_testing.depgraph_fallback_total", description = "Dep graph returned empty — conservative fallback to all tests")
  let depGraphAffectedCount =
    meter.CreateHistogram<int>("sagefs.live_testing.depgraph_affected_count", description = "Number of tests affected by a symbol change")

  /// Wrap a unit of work with Activity tracing and Stopwatch timing.
  /// Returns the same value as the wrapped function.
  /// When no OTEL collector is attached, cost is ~50ns (null check).
  let traced (name: string) (tags: (string * obj) list) (f: unit -> 'a) : 'a =
    use activity = activitySource.StartActivity(name)
    let sw = Stopwatch.StartNew()
    let result = f ()
    sw.Stop()
    match activity <> null with
    | true ->
      for (k, v) in tags do
        activity.SetTag(k, v) |> ignore
      activity.SetTag("duration_ms", sw.Elapsed.TotalMilliseconds) |> ignore
    | false -> ()
    result
