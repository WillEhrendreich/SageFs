module SageFs.Tests.EventsCatchUpCancellationTests

// A connection used to be caught up on a session's annotations by projecting every source file and writing a
// `file_annotations` frame and a `coverage_view` frame per function for each. On the SageFs.Tests project that
// was minutes of CPU and megabytes per connect, whatever the editor had open, and a client that left did not
// stop it (Kestrel turns a write to a closed connection into a no-op that never throws).
//
// The catch-up now writes ONE `annotations_available` frame that names the files with annotations, and the
// editor asks for the ones it has open (GET /api/live-testing/file-annotations). What a connect costs no
// longer grows with the number of files.

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Server
open SageFs.Server.McpServer

let private sessionId = "aa11bb22"
let private fileOf (i: int) = sprintf "/repo/Tests/File%d.fs" i

let private entryIn (i: int) : TestStatusEntry =
  let name = sprintf "Tests.File%d.case" i
  { TestId = TestId.TestId name
    DisplayName = name
    FullName = name
    Origin = TestOrigin.SourceMapped (fileOf i, 10)
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit
    CurrentPolicy = RunPolicy.OnEveryChange
    Status = TestRunStatus.Passed TimeSpan.Zero
    PreviousStatus = TestRunStatus.Detected
    Provenance = ResultProvenance.Compiled }

let private coverageIn (i: int) : CoverageAnnotation =
  { Symbol = sprintf "File%d.f" i
    FilePath = fileOf i
    DefinitionLine = 10
    Status = CoverageStatus.Covered (1, CoverageHealth.AllPassing)
    BranchCoverage = BranchCoverage.Unknown }

/// A session whose live testing is on and which has `files` files, each with one test and one covered symbol.
let private modelOf (files: int) : SageFsModel =
  let initial = SageFsModel.initial ()
  let state =
    { LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        CoverageAnnotations = [| for i in 1 .. files -> coverageIn i |] }
    |> LiveTestState.withStatusEntries [| for i in 1 .. files -> entryIn i |]
  { initial with LiveTesting = { initial.LiveTesting with TestState = state } }

let private contextOver (model: SageFsModel) : SseContext =
  { GetElmModel = Some (fun () -> model)
    GetWarmupContext = None
    GetHotReloadState = None
    // The options the daemon serializes with. A bare `JsonSerializerOptions()` writes an F# union on .NET 11 and
    // throws on .NET 10, so a catch-up built on one wrote nothing there and these cases saw zero frames.
    SseJsonOpts = Json.optionsOf Json.standard
    TestEventBroadcast = Event<SseFrame>()
    SessionEventBroadcast = Event<SseFrame>()
    Log = SseLog.startNew ()
    ServerTracker = McpServerTracker()
    CohortOwner = None }

/// A stream that remembers what was written.
type private RecordingStream() =
  inherit MemoryStream()
  member val Frames = ResizeArray<string>()
  override this.WriteAsync(buffer: ReadOnlyMemory<byte>, ct: CancellationToken) =
    this.Frames.Add (Encoding.UTF8.GetString buffer.Span)
    ValueTask()

let private framesOf (kind: string) (stream: RecordingStream) : string list =
  stream.Frames |> Seq.filter (fun f -> f.StartsWith(sprintf "event: %s" kind)) |> List.ofSeq

let private caughtUp (files: int) (gone: CancellationToken) : Task<RecordingStream> =
  task {
    let stream = new RecordingStream()
    do! replayCachedTestState (contextOver (modelOf files)) (Some sessionId) gone stream
    return stream
  }

/// The `data:` of a frame, parsed: its session, its generation and its files.
let private availableIn (frame: string) : string * int * string list =
  let data = frame.Split('\n') |> Array.find (fun line -> line.StartsWith "data: ") |> fun line -> line.Substring 6
  use parsed = JsonDocument.Parse data
  let root = parsed.RootElement
  root.GetProperty("SessionId").GetString(),
  root.GetProperty("Generation").GetInt32(),
  [ for file in root.GetProperty("Files").EnumerateArray() -> file.GetString() ]

[<Tests>]
let tests =
  testList "events catch-up lists the files, it does not send them" [

    testTask "WHY — the catch-up writes one annotations_available frame naming every file, and no frame per file" {
      let! (stream: RecordingStream) = caughtUp 40 CancellationToken.None
      stream |> framesOf "file_annotations" |> Expect.isEmpty "no file's annotations are sent unasked"
      stream |> framesOf "coverage_view" |> Expect.isEmpty "and no view"
      let frames = stream |> framesOf "annotations_available"
      frames |> List.length |> Expect.equal "one frame" 1
      let session, _, files = availableIn frames.Head
      session |> Expect.equal "for the session being caught up" sessionId
      files |> List.sort |> Expect.equal "naming every file with annotations" ([ for i in 1 .. 40 -> fileOf i ] |> List.sort)
    }

    testTask "WHY — what a connect writes does not grow with the number of files" {
      let! (small: RecordingStream) = caughtUp 10 CancellationToken.None
      let! (big: RecordingStream) = caughtUp 10000 CancellationToken.None
      big.Frames.Count |> Expect.equal "the same number of frames for ten thousand files as for ten" small.Frames.Count
    }

    testTask "the frame carries the run generation, so an editor can tell a newer list from an older one" {
      let! (stream: RecordingStream) = caughtUp 3 CancellationToken.None
      let _, generation, _ = availableIn (stream |> framesOf "annotations_available").Head
      generation |> Expect.equal "the model's own generation" (RunGeneration.value (modelOf 3).LiveTesting.TestState.LastGeneration)
    }

    testTask "the summary and the batch are still sent, ahead of the list" {
      let! (stream: RecordingStream) = caughtUp 5 CancellationToken.None
      let order = stream.Frames |> Seq.map (fun f -> f.Split('\n').[0]) |> List.ofSeq
      order
      |> Expect.equal "summary, batch, then the list of files"
           [ "event: test_summary"; "event: test_results_batch"; "event: annotations_available" ]
    }

    testTask "a session with no annotated file lists none, and still says so" {
      let! (stream: RecordingStream) = caughtUp 0 CancellationToken.None
      match stream |> framesOf "annotations_available" with
      | [ frame ] ->
        let _, _, files = availableIn frame
        files |> Expect.isEmpty "an empty list"
      | other -> failtestf "expected one frame, got %d" other.Length
    }

    testTask "a client that left before the catch-up began costs nothing" {
      let! (stream: RecordingStream) = caughtUp 40 (new CancellationToken(true))
      stream.Frames |> Expect.isEmpty "nothing was written"
    }
  ]
