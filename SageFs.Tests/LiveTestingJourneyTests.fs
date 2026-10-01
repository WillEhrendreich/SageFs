module SageFs.Tests.LiveTestingJourneyTests

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs.Tests.LtStream

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// Live testing through the real path, end to end, on the FromCSharp sample the `--integration-lt`
/// runner started a daemon and a session for. Each journey is an OUTCOME gate: what an editor user
/// would see on the stream and the HTTP API, not what a function returns.
///
///   * per-test coverage: which tests cover a line, exactly, and an edit to a line only one test runs
///     selects only that test;
///   * provenance: a result says whether it ran against evaluated code or a real build, and a real
///     build that disagrees says so loudly;
///   * scale controls: pause and an include/exclude set, against what Visual Studio offers.

let private circleLine () = lineOf "| Circle r"
let private rectangleLine () = lineOf "| Rectangle (w, h)"
let private rectangleArm = "| Rectangle (w, h)  -> w * h"

let private circleName = "circle area"
let private rectangleName = "rectangle area"

let private client () = new HttpClient(BaseAddress = baseUrl (), Timeout = TestTimeouts.httpRequest)

/// The tests a file annotation says cover `line`, by display name.
let private coveringTests (annotationsJson: string) (line: int) : string list =
  use doc = JsonDocument.Parse annotationsJson
  doc.RootElement.GetProperty("CoverageAnnotations").EnumerateArray()
  |> Seq.filter (fun a -> a.GetProperty("Line").GetInt32() = line)
  |> Seq.collect (fun a ->
    match a.TryGetProperty "CoveringTests" with
    | true, tests -> tests.EnumerateArray() |> Seq.map (fun t -> t.GetProperty("DisplayName").GetString()) |> Seq.toList
    | false, _ -> [])
  |> Seq.sort
  |> Seq.toList

/// The session is settled and green, so a journey starts from rest whatever ran before it.
let private settleGreen (feed: SseFeed) (http: HttpClient) : Task<unit> =
  task {
    do! awaitSettled feed http 0 TestTimeouts.liveTestingVerdictCeiling
    // The journey before this one restored the file, which evaluates and then asks a build to confirm. Its
    // answer must not arrive in the middle of this journey.
    do! awaitConfirmed feed http TestTimeouts.buildConfirmation
  }

let private connect (feed: SseFeed) : Task<unit> =
  task {
    let! _ = expectFrame feed "SSE feed connecting" (fun f -> f.Event = "session") TestTimeouts.patience
    return ()
  }

/// Run a journey with a feed, putting Hello.fs and the daemon's buffer back however it ends.
let private journey (body: HttpClient -> string -> SseFeed -> string -> Task<unit>) : Task<unit> =
  task {
    use http = client ()
    let! sid = sessionId http
    let feed = openFeed ()
    let original = File.ReadAllText(helloPath ())
    let cleanup () : Task =
      task {
        drain feed
        File.WriteAllText(helloPath (), original)
        let! _ = postJson http "/api/live-testing/resume" {||}
        let! _ = postJson http "/api/live-testing/scope" {| mode = "every"; patterns = Array.empty<string> |}
        do! postBuffer http sid original
        // Putting the text back is itself an edit (the file on disk and the buffer each are one): the daemon
        // evaluates it, runs what it touches, and asks a build to confirm. The next journey must not start in
        // the middle of that, or the build's answer lands in its rows. So wait for the restored text to be
        // judged and its confirmation to end, and go on until a whole probe passes with nothing new being
        // judged (a restore that touches nothing says nothing, and only waits that probe out).
        // Whatever goes wrong here must not hide what the journey itself reported.
        try
          let mutable resting = false
          while not resting do
            match! awaitFrame feed "the restored text being judged" hasEvaluatedRow TestTimeouts.liveTestingRestoreProbe with
            | Ok _ ->
              do! awaitSettled feed http 0 TestTimeouts.liveTestingVerdictCeiling
              do! awaitConfirmed feed http TestTimeouts.buildConfirmation
              drain feed
            | Result.Error _ -> resting <- true
        with ex ->
          eprintfn "journey cleanup: the session did not come to rest (%s)" ex.Message
        do! feed.Stop()
      }
    return!
      ensuring cleanup (fun () ->
        task {
          do! connect feed
          do! settleGreen feed http
          do! body http sid feed original
        })
  }

let private perTestCoverageGate () : Task<unit> =
  journey (fun http _ _ _ ->
    task {
      let! body = getText http "/api/live-testing/file-annotations?file=Hello.fs"
      coveringTests body (circleLine ())
      |> Expect.equal "the circle arm is covered by the circle test and no other" [ circleName ]
      coveringTests body (rectangleLine ())
      |> Expect.equal "the rectangle arm is covered by the rectangle test and no other" [ rectangleName ]
      // `greet` is a real method in the compiled project (`add` is small enough for the compiler to inline into its
      // one caller, so its own probe is never hit, and the line says so honestly).
      coveringTests body (lineOf "| Some n")
      |> Expect.equal "the Some arm of `greet` is covered by the test that passes Some" [ "greet with Some" ]
      coveringTests body (lineOf "| None   ->")
      |> Expect.equal "the None arm of `greet` is covered by the test that passes None" [ "greet with None" ]
    })

let private lineEditSelectsOnlyItsTest () : Task<unit> =
  journey (fun http sid feed original ->
    task {
      Expect.stringContains "the fixture has the rectangle arm" rectangleArm original
      // The first buffer for a file has no earlier symbol snapshot, so it selects broadly. It only
      // establishes what "unchanged" means for the next one.
      drain feed
      do! postBuffer http sid original
      let! _ = expectFrame feed "the first buffer's decision" (fun f -> (decisionIn f).IsSome) TestTimeouts.liveTestingVerdictCeiling
      do! awaitSettled feed http 0 TestTimeouts.liveTestingVerdictCeiling
      // A body-only edit to the line only the rectangle test runs.
      drain feed
      do! postBuffer http sid (original.Replace(rectangleArm, rectangleArm + " + 0.0"))
      let! frame =
        expectFrame feed "a decision narrowed by line coverage"
          (fun f -> match decisionIn f with ValueSome (precision, _, _) -> precision = "line_coverage_narrowing" | ValueNone -> false)
          TestTimeouts.liveTestingVerdictCeiling
      match decisionIn frame with
      | ValueSome (_, _, selected) ->
        selected
        |> List.map (fun full -> full.Substring(full.LastIndexOf '/' + 1))
        |> Expect.equal "only the rectangle test is selected" [ rectangleName ]
      | ValueNone -> failtest "expected a decision"
    })

let private evaluatedThenVerifiedByBuild () : Task<unit> =
  journey (fun _ _ feed original ->
    task {
      drain feed
      // A save whose content compiles: the eval runs the tests first, a real build confirms them after.
      // A different edit from the one the line-narrowing journey made, so nothing the daemon remembers of that
      // one can stand in for this one.
      File.WriteAllText(helloPath (), original.Replace(rectangleArm, rectangleArm + " * 1.0"))
      let! _ =
        expectFrame feed "the rectangle test's verdict arriving as evaluated"
          (fun f -> provenanceIn rectangleName f = "Evaluated") TestTimeouts.liveTestingVerdictCeiling
      let! verified =
        expectFrame feed "the real build confirming it"
          (fun f -> provenanceIn rectangleName f = "VerifiedByBuild") TestTimeouts.buildConfirmation
      verdictIn rectangleName verified |> Expect.equal "still green, and now confirmed" Verdict.Passed
    })

/// A line the live session accepts and a real build rejects: the session has the whole project
/// loaded, so a file can reach a module that comes AFTER it in compile order, which the compiler refuses.
let private buildDisagrees () : Task<unit> =
  journey (fun _ _ feed original ->
    task {
      drain feed
      let reachesForward = original + "\nlet reachesForward = SageFs.Samples.FromCSharp.Program.main\n"
      File.WriteAllText(helloPath (), reachesForward)
      let! _ =
        expectFrame feed "the verdict arriving as evaluated"
          (fun f -> provenanceIn affectedTest f = "Evaluated") TestTimeouts.liveTestingVerdictCeiling
      let! disagreed =
        expectFrame feed "the real build contradicting it"
          (fun f -> provenanceIn affectedTest f = "BuildDisagrees") TestTimeouts.buildConfirmation
      provenanceDetailIn affectedTest disagreed
      |> Expect.stringContains "the row says the build failed, with the compiler's own words" "FS0039"
    })

let private pauseHoldsRunsBack () : Task<unit> =
  journey (fun http sid feed original ->
    task {
      let! status, _ = postJson http "/api/live-testing/pause" {||}
      status |> Expect.equal "pausing succeeds" 200
      drain feed
      do! postBuffer http sid (original.Replace(greenAdd, redAdd))
      let! _ =
        expectFrame feed "a decision saying the run was held back"
          (fun f ->
            match decisionIn f with
            | ValueSome (precision, reason, _) -> precision = "suppressed_by_policy" && reason.ToLowerInvariant().Contains "paused"
            | ValueNone -> false)
          TestTimeouts.liveTestingVerdictCeiling
      let! summary = getText http "/api/live-testing/status"
      use doc = JsonDocument.Parse summary
      doc.RootElement.GetProperty("Summary").GetProperty("Failed").GetInt32()
      |> Expect.equal "no test ran while paused, so nothing went red" 0
      doc.RootElement.GetProperty("Pause").GetString() |> Expect.equal "the status says paused" "paused"
      // Resuming catches up: the edit that was held back is now judged.
      let! resumed, _ = postJson http "/api/live-testing/resume" {||}
      resumed |> Expect.equal "resuming succeeds" 200
      let! _ =
        expectFrame feed "the held-back edit's verdict after resuming" (fun f -> verdictIn affectedTest f = Verdict.Failed) TestTimeouts.liveTestingVerdictCeiling
      ()
    })

let private scopeKeepsTestsOutOfAutomaticRuns () : Task<unit> =
  journey (fun http sid feed original ->
    task {
      let! status, _ = postJson http "/api/live-testing/scope" {| mode = "except"; patterns = [| affectedTest |] |}
      status |> Expect.equal "setting the scope succeeds" 200
      drain feed
      do! postBuffer http sid (original.Replace(greenAdd, redAdd))
      let! _ =
        expectFrame feed "a decision naming the excluded test as deferred, and running the others"
          (fun f ->
            match decisionIn f with
            | ValueSome (_, _, selected) ->
              List.exists (fun (s: string) -> s.EndsWith affectedTest) (deferredIn f)
              && not (List.exists (fun (s: string) -> s.EndsWith affectedTest) selected)
            | ValueNone -> false)
          TestTimeouts.liveTestingVerdictCeiling
      let! summary = getText http "/api/live-testing/status"
      use doc = JsonDocument.Parse summary
      doc.RootElement.GetProperty("Summary").GetProperty("Failed").GetInt32()
      |> Expect.equal "the excluded test did not run, so it did not go red" 0
      let generationBefore = doc.RootElement.GetProperty("Generation").GetInt64()
      // Asking for it by name always runs it. An explicit run is against what the session has built (it is not
      // an eval of the buffer), so what this shows is that the run happened: a run after the last one, with a
      // fresh verdict on the excluded test.
      drain feed
      let! ran, _ = postJson http "/api/live-testing/run" {| pattern = affectedTest; category = "" |}
      ran |> Expect.equal "an explicit run is accepted" 200
      let! _ =
        expectFrame feed "a later run giving the excluded test a verdict"
          (fun f -> generationIn f > generationBefore && verdictIn affectedTest f = Verdict.Passed)
          TestTimeouts.liveTestingVerdictCeiling
      ()
    })

[<Tests>]
let journeyTests =
  testList "Live-testing journeys" [
    Integration.dedicatedCaseTask "--integration-lt" "LT coverage: each line lists exactly the tests that cover it" perTestCoverageGate
    Integration.dedicatedCaseTask "--integration-lt" "LT coverage: an edit to a line only one test runs selects only that test" lineEditSelectsOnlyItsTest
    Integration.dedicatedCaseTask "--integration-lt" "LT provenance: an evaluated verdict is confirmed by a real build" evaluatedThenVerifiedByBuild
    Integration.dedicatedCaseTask "--integration-lt" "LT provenance: a real build that rejects what the session accepted says so on the row" buildDisagrees
    Integration.dedicatedCaseTask "--integration-lt" "LT scale: pausing holds test runs back and resuming catches up" pauseHoldsRunsBack
    Integration.dedicatedCaseTask "--integration-lt" "LT scale: an exclude set keeps a test out of automatic runs, an explicit run still runs it" scopeKeepsTestsOutOfAutomaticRuns
  ]
