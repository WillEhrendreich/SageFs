module SageFs.Tests.LiveTestingLatencyTests

open System
open System.IO
open System.Diagnostics
open System.Net.Http
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.LatencyStats
open SageFs.Tests.LtStream

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// How fast does a verdict reach you? Measured through the real path, the way an editor drives it:
/// the daemon the `--integration-lt` runner started, a session on the FromCSharp sample, and the
/// daemon's own `/events` stream as the clock. Nothing here times a function: a sample is the
/// wall-clock gap between the edit leaving the client and the verdict arriving on the stream.
///
/// Two paths are measured:
///   * keystroke-to-verdict: the whole unsaved buffer is posted to `buffer-changed` (what the VS
///     Code client does 300ms after the last edit, a pause that is NOT in these numbers), and the
///     clock stops when the test the edit affects changes verdict on the stream.
///   * save-to-green: the file is written to disk and the clock stops when the suite is back to
///     all green with nothing running.
///
/// Each prints one `LATENCY` line with the sample count, the percentiles and the machine, and gates
/// on a bound taken from the first measurement (see `TestTimeouts.liveTestingKeystrokeP95Bound`).

let private reportAndGate (name: string) (samples: TimeSpan list) (bound: TimeSpan) =
  match summarize samples with
  | Result.Error refusal -> failtestf "%s: nothing was measured (%A)" name refusal
  | Ok summary ->
    let line = reportLine name (machine ()) summary
    eprintfn "%s" line
    printfn "%s" line
    match gate bound summary with
    | GateVerdict.WithinBound _ -> ()
    | regressed -> failtest (sprintf "%s: %s" name (describeVerdict regressed))

/// The feed replays the daemon's current state on connect: wait for it, so every later wait starts
/// from a connected stream.
let private connect (feed: SseFeed) : Task<unit> =
  task {
    let! _ = expectFrame feed "SSE feed connecting" (fun f -> f.Event = "session") TestTimeouts.patience
    return ()
  }

let private keystrokeToVerdict () : Task<unit> =
  task {
    use http = new HttpClient(BaseAddress = baseUrl (), Timeout = TestTimeouts.httpRequest)
    let! sid = sessionId http
    let feed = openFeed ()
    let original = File.ReadAllText(helloPath ())
    let cleanup () : Task =
      task {
        // The buffer is virtual: the daemon's idea of the file returns to the saved text.
        do! postBuffer http sid original
        do! feed.Stop()
      }
    return!
      ensuring cleanup (fun () ->
        task {
          Expect.stringContains "the fixture carries the line the edits flip" greenAdd original
          let buffer (line: string) = original.Replace(greenAdd, line)
          do! connect feed
          let samples = ResizeArray<TimeSpan>()
          for i in 1 .. samplesPerPath + warmupEdits do
            let toRed = i % 2 = 1
            let content = buffer (match toRed with | true -> redAdd | false -> greenAdd)
            let expected = match toRed with | true -> Verdict.Failed | false -> Verdict.Passed
            drain feed
            let startedAt = Stopwatch.GetTimestamp()
            do! postBuffer http sid content
            let! frame =
              expectFrame feed (sprintf "keystroke %d" i) (fun f -> verdictIn affectedTest f = expected) TestTimeouts.liveTestingVerdictCeiling
            match i > warmupEdits with
            | true -> samples.Add(elapsed startedAt frame.ReceivedAt)
            | false -> ()
            // Let the run settle before the next edit, so each sample starts from rest.
            let! _ =
              expectFrame feed (sprintf "settling after keystroke %d" i) (settledWith (match toRed with | true -> 1 | false -> 0)) TestTimeouts.liveTestingVerdictCeiling
            ()
          reportAndGate "keystroke-to-verdict" (List.ofSeq samples) TestTimeouts.liveTestingKeystrokeP95Bound
        })
  }

let private saveToGreen () : Task<unit> =
  task {
    let feed = openFeed ()
    let original = File.ReadAllText(helloPath ())
    let cleanup () : Task =
      task {
        File.WriteAllText(helloPath (), original)
        do! feed.Stop()
      }
    return!
      ensuring cleanup (fun () ->
        task {
          Expect.stringContains "the fixture carries the line the saves flip" greenAdd original
          do! connect feed
          let samples = ResizeArray<TimeSpan>()
          for i in 1 .. samplesPerPath + warmupEdits do
            // Red first (not timed), then the fix save (timed).
            drain feed
            File.WriteAllText(helloPath (), original.Replace(greenAdd, redAdd))
            let! _ = expectFrame feed (sprintf "save %d to red" i) (settledWith 1) TestTimeouts.liveTestingVerdictCeiling
            drain feed
            let startedAt = Stopwatch.GetTimestamp()
            File.WriteAllText(helloPath (), original)
            let! frame = expectFrame feed (sprintf "save %d to green" i) (settledWith 0) TestTimeouts.liveTestingVerdictCeiling
            match i > warmupEdits with
            | true -> samples.Add(elapsed startedAt frame.ReceivedAt)
            | false -> ()
          reportAndGate "save-to-green" (List.ofSeq samples) TestTimeouts.liveTestingSaveToGreenP95Bound
        })
  }

[<Tests>]
let latencyTests =
  testList "Live-testing latency" [
    Integration.dedicatedCaseTask
      "--integration-lt"
      "LT latency: keystroke to the verdict on the edited function's test, p50 and p95 over many edits"
      keystrokeToVerdict
    Integration.dedicatedCaseTask
      "--integration-lt"
      "LT latency: save to green, p50 and p95 over many saves"
      saveToGreen
  ]
