namespace SageFs.Features.LiveTesting

open System.Diagnostics

/// OTEL instrumentation for the live testing pipeline.
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
