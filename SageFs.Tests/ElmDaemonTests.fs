module SageFs.Tests.ElmDaemonTests

open System
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.Diagnostics
open SageFs.Tests.SharedGenerators

/// Test helpers for ElmDaemon
module ElmDaemonTestHelpers =

  /// Create mock EffectDeps with a configurable worker proxy
  let mockDeps
    (handler: WorkerMessage -> WorkerResponse) : EffectDeps =
    let testSid = testSessionId "deadbeef"
    let sessionInfo : SessionInfo = {
      Id = testSid
      Name = None
      Projects = ["Test.fsproj"]
      WorkingDirectory = "."
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = SessionLifecycleStatus.Ready { Pid = 999; Port = None }
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      ActiveProject = None

      ProjectRoles = []

      App = SageFs.AppRun.AppRunState.NotRunning

    }
    let proxy (msg: WorkerMessage) =
      async { return handler msg }
    {
      ResolveSession = fun _ ->
        Result.Ok (
          SessionOperations.SessionResolution.DefaultSingle testSid)
      GetProxy = fun id ->
        if id = testSid then Some proxy else None
      GetStreamingTestProxy = fun _ -> None
      CreateSession = fun projects _ _ ->
        async { return Result.Ok sessionInfo }
      ConfigureWarmupAutoOpen = fun _ ->
        async {
          return Result.Ok {
            Kind = OutputKind.System
            Text = "Disabled warmup auto-open"
            Timestamp = DateTime.UtcNow
            SessionId = "" }
        }
      StopSession = fun _ ->
        async { return Result.Ok () }
      RestartSession = fun _ _ ->
        async { return Result.Ok "restarted" }
      ListSessions = fun () ->
        async { return [sessionInfo] }
      SleepMs = fun _ -> async { return () }
      GetWarmupContext = None
      RegisterFileWatcher = fun _ _ -> ()
      DisposeFileWatcher = fun _ _ -> ()
      TestCycleCancellation = Features.LiveTesting.TestCycleCancellation.create ()
    }

  /// Track model changes from OnModelChanged callback
  type ModelTracker() =
    let mutable models : SageFsModel list = []
    let mutable regionHistory : RenderRegion list list = []
    let evt = new ManualResetEventSlim(false)

    member _.OnModelChanged (model: SageFsModel) (r: RenderRegion list) =
      models <- model :: models
      regionHistory <- r :: regionHistory
      evt.Set()

    member _.Models = models |> List.rev
    member _.LatestModel =
      match models with
      | [] -> None
      | m :: _ -> Some m
    member _.Regions = regionHistory |> List.rev
    member _.WaitForUpdate(timeout: int) =
      evt.Wait(timeout) |> ignore
      evt.Reset()

[<Tests>]
let elmDaemonTests =
  testList "ElmDaemon" [

    testList "createProgram" [
      test "creates a valid ElmProgram with all wired components" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let program = ElmDaemon.createProgram deps tracker.OnModelChanged (fun _ _ -> ())

        // Verify update produces a model and effects list
        let msg =
          SageFsMsg.Event (
            SageFsEvent.EvalStarted ("s", "code"))
        let model, effects = program.Update msg (SageFsModel.initial())
        model.RecentOutput.GetBuffer("s")
        |> Seq.length
        |> Expect.equal "EvalStarted should add one output entry" 1

        effects
        |> List.length
        |> Expect.equal "no effects from EvalStarted" 0
      }

      test "Update delegates to SageFsUpdate.update" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let program = ElmDaemon.createProgram deps tracker.OnModelChanged (fun _ _ -> ())

        let model = (SageFsModel.initial())
        let msg =
          SageFsMsg.Event (
            SageFsEvent.EvalCompleted ("s", "hello", []))
        let newModel, _ = program.Update msg model

        newModel.RecentOutput.GetBuffer("s")
        |> Seq.length
        |> Expect.equal "should have one output line" 1
      }

      test "Render delegates to SageFsRender.render" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let program = ElmDaemon.createProgram deps tracker.OnModelChanged (fun _ _ -> ())

        let regions = program.Render(SageFsModel.initial())

        regions
        |> List.isEmpty
        |> Expect.isFalse "should produce render regions"
      }
    ]

    testList "createHeadlessProgram" [
      test "defers region materialization until a client asks for it" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let program = ElmDaemon.createHeadlessProgram deps tracker.OnModelChanged (fun _ _ -> ())

        let regions = program.Render(SageFsModel.initial())

        regions
        |> Expect.isEmpty "headless daemon should skip eager render work"
      }
    ]

    testList "start" [
      testTask "returns a runtime with dispatch that can be called" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let reached = Tasks.TaskCompletionSource<unit>(Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
        // EvalStarted echoes the submitted code into the session's output.
        let onModelChanged (model: SageFsModel) (_: RenderRegion list) =
          match model.RecentOutput.GetBuffer("s").Exists(fun line -> line.Text = "dispatched-code") with
          | true -> reached.TrySetResult () |> ignore
          | false -> ()
        let runtime =
          ElmDaemon.start deps onModelChanged (fun _ _ -> ()) System.Threading.CancellationToken.None

        runtime.Dispatch (
          SageFsMsg.Event (SageFsEvent.EvalStarted ("s", "dispatched-code")))
        let! _ = Tasks.Task.WhenAny(reached.Task, Tasks.Task.Delay 5000)
        reached.Task.IsCompleted
        |> Expect.isTrue "the dispatched message should reach the model"
      }

      test "initial model is rendered on start" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let _runtime =
          ElmDaemon.start deps tracker.OnModelChanged (fun _ _ -> ()) System.Threading.CancellationToken.None

        tracker.Models
        |> List.isEmpty
        |> Expect.isFalse "should have rendered initial model"
      }

      test "dispatching a message updates the model" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let runtime =
          ElmDaemon.start deps tracker.OnModelChanged (fun _ _ -> ()) System.Threading.CancellationToken.None

        // consume initial render signal
        tracker.WaitForUpdate 500

        runtime.Dispatch (
          SageFsMsg.Event (
            SageFsEvent.EvalCompleted ("s", "result-42", [])))

        // Give time for dispatch to process
        tracker.WaitForUpdate 500

        tracker.LatestModel
        |> Option.bind (fun m ->
          m.RecentOutput.GetBuffer("s").Exists(fun line -> line.Text = "result-42")
          |> Some)
        |> Option.defaultValue false
        |> Expect.isTrue "model should contain the eval result"
      }

      testTask "dispatching an effect-producing message executes the effect" {
        let evalRequested = Tasks.TaskCompletionSource<string>(Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun msg ->
            match msg with
            | WorkerMessage.EvalCode (code, _) ->
              evalRequested.TrySetResult code |> ignore
              WorkerResponse.EvalResult ("r", Ok "evaluated!", [], Map.empty)
            | _ ->
              WorkerResponse.WorkerError SageFsError.NoActiveSessions)
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let runtime =
          ElmDaemon.start deps tracker.OnModelChanged (fun _ _ -> ()) System.Threading.CancellationToken.None

        // Submit produces EditorEffect.RequestEval, which the effect handler
        // sends to the worker as EvalCode (with the empty buffer's text).
        runtime.Dispatch (
          SageFsMsg.Editor (
            EditorAction.Submit))

        let! _ = Tasks.Task.WhenAny(evalRequested.Task, Tasks.Task.Delay 5000)
        evalRequested.Task.IsCompleted
        |> Expect.isTrue "the effect handler should send the eval to the worker"
      }

      test "GetModel returns current model state" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let runtime =
          ElmDaemon.start deps tracker.OnModelChanged (fun _ _ -> ()) System.Threading.CancellationToken.None

        let model = runtime.GetModel()
        model.RecentOutput.GetActiveBuffer(model.Sessions.ActiveSessionId)
        |> Seq.length
        |> Expect.equal "initial model has no output" 0
      }

      test "GetRegions returns current render regions" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let runtime =
          ElmDaemon.start deps tracker.OnModelChanged (fun _ _ -> ()) System.Threading.CancellationToken.None

        let regions = runtime.GetRegions()
        regions
        |> List.map (fun r -> r.Id)
        |> Expect.contains "should have editor region" "editor"
      }
    ]

    testList "startHeadless" [
      test "keeps callbacks light while still rendering current regions on demand" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let runtime =
          ElmDaemon.startHeadless deps tracker.OnModelChanged (fun _ _ -> ()) System.Threading.CancellationToken.None

        tracker.WaitForUpdate 500

        runtime.Dispatch (
          SageFsMsg.Event (
            SageFsEvent.EvalCompleted ("s", "result-42", [])))

        tracker.WaitForUpdate 500

        tracker.Regions
        |> List.last
        |> Expect.isEmpty "headless callbacks should not eagerly materialize render regions"

        let rendered : RenderRegion list = ElmDaemon.renderRegionsOnDemand runtime
        let canonical = runtime.GetModel() |> SageFsRender.render

        runtime.GetModel().RecentOutput.GetBuffer("s")
        |> Seq.exists (fun line -> line.Text = "result-42")
        |> Expect.isTrue "the model should still contain the latest output"

        rendered
        |> List.map (fun (r: RenderRegion) -> r.Id)
        |> Expect.contains "clients can still render the current regions on demand" "output"

        rendered
        |> Expect.equal "on-demand render should match the canonical region projection" canonical
      }
    ]

    testList "dispatchAndWait" [
      test "dispatches message and returns updated model" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", [], Map.empty))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let runtime =
          ElmDaemon.start deps tracker.OnModelChanged (fun _ _ -> ()) System.Threading.CancellationToken.None

        // consume initial render signal
        tracker.WaitForUpdate 500

        let result =
          ElmDaemon.dispatchAndWait
            runtime.Dispatch
            (fun () -> tracker.LatestModel)
            tracker.WaitForUpdate
            (SageFsMsg.Event (
              SageFsEvent.EvalCompleted ("s", "sync-result", [])))
            1000

        result.RecentOutput.GetBuffer("s")
        |> Seq.exists (fun line -> line.Text = "sync-result")
        |> Expect.isTrue "should have the dispatched result"
      }
    ]
  ]

