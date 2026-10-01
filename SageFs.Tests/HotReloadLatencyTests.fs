module SageFs.Tests.HotReloadLatencyTests

open System
open System.Diagnostics
open Expecto
open Expecto.Flip
open SageFs.Tests.LatencyStats
open SageFs.Tests.HotReloadLatency

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// How fast does a save reach the running app? Measured through the real path, the way a user
/// meets it: the daemon the `--integration-hr` runner started, the WebAppFixture app its
/// session runs, and the daemon's own `/events` stream plus the app's own HTTP route as the clocks.
/// Nothing here times a function: a sample is the wall-clock gap between the first byte of the save
/// and the first response that carries the new value.
///
/// Three series, each printed as one `LATENCY` line with the sample count, the percentiles and the
/// machine, and each gated on a bound taken from the first measurement (TestTimeouts):
///   * patch save-to-served: a save the session patches in place, to the first response with the new body.
///   * patch save-to-confirmed: the same save, to the verdict reaching `Patched` on the stream.
///   * restart save-to-served: a save to an app `run_app` runs, which SageFs rebuilds and relaunches.
///
/// The first half of this file is the pure part (reading a frame, turning stamps into stages), and it
/// runs in the default tier. The second half needs the daemon and runs under `--integration-hr`.

/// Stamps are stopwatch ticks, so a test builds one from an offset the same way the clock would.
let private at (offset: TimeSpan) : int64 =
  int64 (offset.TotalSeconds * float Stopwatch.Frequency)

let private ms (n: float) = TimeSpan.FromMilliseconds n

let private within (tolerance: TimeSpan) (name: string) (expected: TimeSpan) (actual: TimeSpan) =
  (actual - expected).Duration() <= tolerance
  |> Expect.isTrue (sprintf "%s: expected %gms, got %gms" name expected.TotalMilliseconds actual.TotalMilliseconds)

let private aMillisecond = ms 1.

let private frameOf (sessionId: string) (reload: string) =
  sprintf """{"reloadReported":%s,"sessionId":"%s"}""" reload sessionId

[<Tests>]
let pureTests =
  testList "Hot-reload latency, reading the stream and the stages" [
    testCase "a compiling frame for the session reads as compiling" <| fun _ ->
      frameOf "s1" """{"state":"compiling","file":"/tmp/Greeting.fs"}"""
      |> ReloadFrame.ofData "s1"
      |> Expect.equal "compiling" ReloadFrame.Compiling

    testCase "a finished frame carries the outcome the worker named" <| fun _ ->
      for case in SageFs.ReloadCase.all do
        frameOf "s1" (sprintf """{"state":"finished","outcome":"%s","patched":1,"considered":1,"message":"m","suggestedAction":""}""" (SageFs.ReloadCase.token case))
        |> ReloadFrame.ofData "s1"
        |> Expect.equal (sprintf "finished as %s" (SageFs.ReloadCase.token case)) (ReloadFrame.Finished case)

    testCase "a frame about another session is not this session's" <| fun _ ->
      frameOf "s2" """{"state":"compiling","file":""}"""
      |> ReloadFrame.ofData "s1"
      |> Expect.equal "other session" ReloadFrame.OtherSession

    testCase "a frame with no reload on it, or a null one, is not a reload" <| fun _ ->
      """{"sessionId":"s1","state":"Ready"}"""
      |> ReloadFrame.ofData "s1"
      |> Expect.equal "no reloadReported" ReloadFrame.NotAReload
      frameOf "s1" "null"
      |> ReloadFrame.ofData "s1"
      |> Expect.equal "null reloadReported" ReloadFrame.NotAReload

    testCase "text that is not JSON, and an outcome nobody knows, are named and not guessed" <| fun _ ->
      match ReloadFrame.ofData "s1" "not json" with
      | ReloadFrame.Unreadable _ -> ()
      | other -> failtestf "not JSON should be unreadable, got %A" other
      match ReloadFrame.ofData "s1" (frameOf "s1" """{"state":"finished","outcome":"Exploded"}""") with
      | ReloadFrame.Unreadable detail -> detail |> Expect.stringContains "names the token" "Exploded"
      | other -> failtestf "an unknown outcome should be unreadable, got %A" other

    testCase "stamps become the time each stage was reached after the save" <| fun _ ->
      let stamps =
        { SavedAt = at (ms 100.)
          CompilingAt = Moment.Observed (at (ms 320.))
          AppliedAt = Moment.Observed (at (ms 410.))
          ServedAt = Moment.Observed (at (ms 450.))
          ConfirmedAt = Moment.Observed (at (ms 470.)) }
      match Sample.ofStamps stamps with
      | Result.Error refusal -> failtestf "should read: %A" refusal
      | Ok sample ->
        within aMillisecond "compiling" (ms 220.) (match sample.Compiling with | Elapsed.After d -> d | Elapsed.Never -> TimeSpan.MaxValue)
        within aMillisecond "applied" (ms 310.) (match sample.Applied with | Elapsed.After d -> d | Elapsed.Never -> TimeSpan.MaxValue)
        within aMillisecond "served" (ms 350.) sample.Served
        within aMillisecond "confirmed" (ms 370.) (match sample.Confirmed with | Elapsed.After d -> d | Elapsed.Never -> TimeSpan.MaxValue)

    testCase "a sample that was never served is refused, not read as zero" <| fun _ ->
      { SavedAt = at (ms 100.)
        CompilingAt = Moment.NotObserved
        AppliedAt = Moment.NotObserved
        ServedAt = Moment.NotObserved
        ConfirmedAt = Moment.NotObserved }
      |> Sample.ofStamps
      |> Expect.equal "never served" (Result.Error SampleRefusal.NeverServed)

    testCase "a stamp from before the save is refused, naming the stage" <| fun _ ->
      { SavedAt = at (ms 500.)
        CompilingAt = Moment.Observed (at (ms 100.))
        AppliedAt = Moment.NotObserved
        ServedAt = Moment.Observed (at (ms 900.))
        ConfirmedAt = Moment.NotObserved }
      |> Sample.ofStamps
      |> Expect.equal "before the save" (Result.Error (SampleRefusal.BeforeTheSave Stage.Compiling))

    testCase "a series is the stage's times over the samples, and a series that needs a stage nobody reached is refused" <| fun _ ->
      let sample served confirmed =
        { Compiling = Elapsed.Never; Applied = Elapsed.Never; Served = ms served; Confirmed = confirmed }
      let samples = [ sample 300. (Elapsed.After (ms 350.)); sample 310. (Elapsed.After (ms 360.)) ]
      Sample.series Series.PatchSaveToServed samples
      |> Expect.equal "served" (Ok [ ms 300.; ms 310. ])
      Sample.series Series.PatchSaveToConfirmed samples
      |> Expect.equal "confirmed" (Ok [ ms 350.; ms 360. ])
      Sample.series Series.PatchSaveToConfirmed (sample 300. Elapsed.Never :: samples)
      |> Expect.equal "a sample the verdict never reached" (Result.Error SampleRefusal.NeverConfirmed)

    testCase "every series has its own name, and the names say which path they measure" <| fun _ ->
      let names = Series.all |> List.map Series.name
      names |> List.distinct |> List.length |> Expect.equal "names are distinct" (List.length names)
      for name in names do
        name |> Expect.stringContains "says it is hot reload" "hr-"

    testCase "the stage line names the path, the count and the median of each stage" <| fun _ ->
      let sample served =
        { Compiling = Elapsed.After (ms 210.); Applied = Elapsed.After (ms 300.); Served = ms served; Confirmed = Elapsed.After (ms (served + 20.)) }
      let line = Sample.stageLine Series.PatchSaveToServed [ sample 320.; sample 340.; sample 360. ]
      line |> Expect.stringContains "starts the line" "STAGES hr-patch"
      line |> Expect.stringContains "the count" "n=3"
      line |> Expect.stringContains "compiling median" "compiling-p50=210.0ms"
      line |> Expect.stringContains "served median" "served-p50=340.0ms"
  ]

/// Report one series the way the LT tier does, then judge it against its bound.
let private reportAndGate (series: Series) (samples: Sample list) (bound: TimeSpan) =
  let name = Series.name series
  match Sample.series series samples |> Result.map summarize with
  | Result.Error refusal -> failtestf "%s: %A" name refusal
  | Ok (Result.Error refusal) -> failtestf "%s: nothing was measured (%A)" name refusal
  | Ok (Ok summary) ->
    let line = reportLine name (machine ()) summary
    eprintfn "%s" line
    printfn "%s" line
    let stages = Sample.stageLine series samples
    eprintfn "%s" stages
    printfn "%s" stages
    match gate bound summary with
    | GateVerdict.WithinBound _ -> ()
    | regressed -> failtest (sprintf "%s: %s" name (describeVerdict regressed))

[<Tests>]
let latencyTests =
  testList "Hot-reload latency" [
    Integration.dedicatedCaseTask
      "--integration-hr"
      "HR latency: save to served and save to confirmed on a patched save, p50 and p95 over many saves"
      (fun () ->
        task {
          let! samples = measurePatchedSaves ()
          reportAndGate Series.PatchSaveToServed samples TestTimeouts.hotReloadPatchServedP95Bound
          reportAndGate Series.PatchSaveToConfirmed samples TestTimeouts.hotReloadPatchConfirmedP95Bound
        })
    Integration.dedicatedCaseTask
      "--integration-hr"
      "HR latency: save to served on a save to an app run_app runs, which is restarted, p50 and p95 over many saves"
      (fun () ->
        task {
          let! samples = measureRestartedSaves ()
          reportAndGate Series.RestartSaveToServed samples TestTimeouts.hotReloadRestartServedP95Bound
        })
  ]
