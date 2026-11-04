module SageFs.Tests.ModelChangePushTests

// Every model change used to push the test summary, the results batch and the whole list of test source
// locations again, whether or not anything in them had changed. Each went into the replay log, so an idle
// session with live testing on spent the log's two thousand frames in a few hundred changes, and a client
// that reconnected was told to resync from snapshots instead of being replayed what it missed.
//
// A frame that says what the last one said is not pushed. A client that connects later is not left
// without it: the connect-time snapshot carries the same three.

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Server
open SageFs.Server.McpServer

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

let private locationIn (i: int) : TestSourceLocation =
  { CellId = i; TestName = sprintf "Tests.File%d.case" i; FilePath = fileOf i; StartLine = 10; EndLine = 12 }

/// A session whose live testing is on, with `tests` tests and `tests` source locations.
let private modelWith (tests: int) : SageFsModel =
  let initial = SageFsModel.initial ()
  let state =
    { LiveTestState.empty with Activation = LiveTestingActivation.Active }
    |> LiveTestState.withStatusEntries [| for i in 1 .. tests -> entryIn i |]
  { initial with
      LiveTesting = { initial.LiveTesting with TestState = state }
      ResolvedSourceLocations = [ for i in 1 .. tests -> locationIn i ] }

let private contextOver (current: SageFsModel ref) : SseContext =
  { GetElmModel = Some (fun () -> current.Value)
    GetWarmupContext = None
    GetHotReloadState = None
    SseJsonOpts = Json.optionsOf Json.standard
    TestEventBroadcast = Event<SseFrame>()
    SessionEventBroadcast = Event<SseFrame>()
    Log = SseLog.startNew ()
    ServerTracker = McpServerTracker()
    CohortOwner = None }

let private kindOf (wire: string) : string =
  wire.Split('\n').[0].Replace("event: ", "")

/// Wires the model-change handlers to a fresh event and collects the kinds of frame they push.
let private pushing (current: SageFsModel ref) =
  let ctx = contextOver current
  let pushed = ResizeArray<string>()
  ctx.TestEventBroadcast.Publish.Add(fun frame -> pushed.Add (kindOf frame.Wire))
  let changed = Event<SseEvent>()
  let subscription =
    wireModelChangeHandlers
      changed.Publish ctx (ref Map.empty) (ref SageFs.Features.FeatureHooks.FeaturePushState.empty)
      (ref 0) (ref None) (ref None) None
  let tick () = changed.Trigger (SseEvent.ModelChanged (0, 0))
  pushed, tick, subscription

let private count (kind: string) (pushed: ResizeArray<string>) : int =
  pushed |> Seq.filter ((=) kind) |> Seq.length

[<Tests>]
let tests =
  testList "model change pushes" [

    test "WHY — five model changes that change nothing push the summary, the batch and the locations once" {
      let current = ref (modelWith 3)
      let pushed, tick, _ = pushing current
      for _ in 1 .. 5 do tick ()
      pushed |> count "test_summary" |> Expect.equal "the summary went out once" 1
      pushed |> count "test_results_batch" |> Expect.equal "the batch went out once" 1
      pushed |> count "test_source_locations" |> Expect.equal "the locations went out once" 1
    }

    test "a result that changed is pushed again, and the locations that did not are not" {
      let current = ref (modelWith 3)
      let pushed, tick, _ = pushing current
      tick ()
      tick ()
      current.Value <- { modelWith 4 with ResolvedSourceLocations = (modelWith 3).ResolvedSourceLocations }
      tick ()
      tick ()
      pushed |> count "test_summary" |> Expect.equal "a fourth test changes the summary" 2
      pushed |> count "test_results_batch" |> Expect.equal "and the batch" 2
      pushed |> count "test_source_locations" |> Expect.equal "the same locations are not sent again" 1
    }

    test "locations that changed are pushed again" {
      let current = ref (modelWith 3)
      let pushed, tick, _ = pushing current
      tick ()
      current.Value <- { current.Value with ResolvedSourceLocations = [ locationIn 1 ] }
      tick ()
      pushed |> count "test_source_locations" |> Expect.equal "the shorter list is a change" 2
    }

    testTask "WHY — a client that connects after the locations were pushed is sent them in its snapshot" {
      let current = ref (modelWith 3)
      let ctx = contextOver current
      let stream = new IO.MemoryStream()
      do! replayCachedTestState ctx None CancellationToken.None (fun () -> Task.CompletedTask) stream
      let text = Text.Encoding.UTF8.GetString (stream.ToArray())
      text.Contains "event: test_source_locations" |> Expect.isTrue "the snapshot carries the locations"
    }

    testTask "a snapshot for a session other than the active one carries no locations, which belong to the active session" {
      let current = ref (modelWith 3)
      let ctx = contextOver current
      let stream = new IO.MemoryStream()
      do! replayCachedTestState ctx (Some "bbbbbbbb") CancellationToken.None (fun () -> Task.CompletedTask) stream
      let text = Text.Encoding.UTF8.GetString (stream.ToArray())
      text.Contains "event: test_source_locations" |> Expect.isFalse "another session's snapshot has none"
    }
  ]
