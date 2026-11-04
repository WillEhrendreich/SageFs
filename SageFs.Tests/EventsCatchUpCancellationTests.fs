module SageFs.Tests.EventsCatchUpCancellationTests

// A connection's catch-up projects every source file of the session: one `file_annotations` frame and
// the `coverage_view` frames behind it. On the SageFs.Tests project that is minutes of CPU. Kestrel
// turns a write to a closed connection into a no-op that never throws, so a client that left (a
// dashboard tab closed, an editor restarted, a `curl` timed out) did not stop its own catch-up: five
// of them were still running, at three cores, minutes after the last client had gone.
//
// The catch-up is now told when the client has left (`HttpContext.RequestAborted`) and stops at the
// next file.

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
let private fileCount = 40
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

/// A session whose live testing is on and which has `fileCount` files, each with one test and one
/// covered symbol, so every file has something to project.
let private model : SageFsModel =
  let initial = SageFsModel.initial ()
  let state =
    { LiveTestState.empty with
        Activation = LiveTestingActivation.Active
        CoverageAnnotations = [| for i in 1 .. fileCount -> coverageIn i |] }
    |> LiveTestState.withStatusEntries [| for i in 1 .. fileCount -> entryIn i |]
  { initial with LiveTesting = { initial.LiveTesting with TestState = state } }

let private context : SseContext =
  { GetElmModel = Some (fun () -> model)
    GetWarmupContext = None
    GetHotReloadState = None
    SseJsonOpts = JsonSerializerOptions()
    TestEventBroadcast = Event<SseFrame>()
    SessionEventBroadcast = Event<SseFrame>()
    Log = SseLog.startNew ()
    ServerTracker = McpServerTracker()
    CohortOwner = None }

/// A stream that remembers what was written, and runs `onFrame` on each frame as it arrives.
type private RecordingStream(onFrame: string -> unit) =
  inherit MemoryStream()
  member val Frames = ResizeArray<string>()
  override this.WriteAsync(buffer: ReadOnlyMemory<byte>, ct: CancellationToken) =
    let text = Encoding.UTF8.GetString buffer.Span
    this.Frames.Add text
    onFrame text
    ValueTask()

let private framesOf (kind: string) (stream: RecordingStream) : int =
  stream.Frames |> Seq.filter (fun f -> f.StartsWith(sprintf "event: %s" kind)) |> Seq.length

/// Runs the catch-up for a client that leaves the moment a frame of `kind` has been written.
let private caughtUpUntilLeavingAt (kind: string) : Task<RecordingStream> =
  task {
    let gone = new CancellationTokenSource()
    let stream = new RecordingStream(fun frame -> if frame.StartsWith(sprintf "event: %s" kind) then gone.Cancel())
    do! replayCachedTestState context (Some sessionId) gone.Token stream
    return stream
  }

[<Tests>]
let tests =
  testList "events catch-up when the client leaves" [

    testTask "a client that stays is caught up on every file, with its views" {
      let stream = new RecordingStream(ignore)
      do! replayCachedTestState context (Some sessionId) CancellationToken.None stream
      stream |> framesOf "file_annotations" |> Expect.equal "every file was sent" fileCount
      stream |> framesOf "coverage_view" |> fun views -> Expect.isTrue "the views were sent" (views > 0)
    }

    testTask "a client that left before the catch-up began costs no file at all" {
      let stream = new RecordingStream(ignore)
      do! replayCachedTestState context (Some sessionId) (new CancellationToken(true)) stream
      stream |> framesOf "file_annotations" |> Expect.equal "no file was projected" 0
    }

    testTask "a client that leaves while the summary goes out is sent no file" {
      let! stream = caughtUpUntilLeavingAt "test_summary"
      stream |> framesOf "file_annotations" |> Expect.equal "the loop stopped before its first file" 0
    }

    testTask "a client that leaves after its first file is sent none of that file's views" {
      let! stream = caughtUpUntilLeavingAt "file_annotations"
      stream |> framesOf "file_annotations" |> Expect.equal "one file went out" 1
      stream |> framesOf "coverage_view" |> Expect.equal "and no view behind it" 0
    }
  ]
