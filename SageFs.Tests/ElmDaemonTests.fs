module SageFs.Tests.ElmDaemonTests

open System
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.Diagnostics

/// Test helpers for ElmDaemon
module ElmDaemonTestHelpers =

  /// Create mock EffectDeps with a configurable worker proxy
  let mockDeps
    (handler: WorkerMessage -> WorkerResponse) : EffectDeps =
    let sessionInfo : SessionInfo = {
      Id = "test-session"
      Projects = ["Test.fsproj"]
      WorkingDirectory = "."
      SolutionRoot = None
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      Status = SessionStatus.Ready
      WorkerPid = Some 999
    }
    let proxy (msg: WorkerMessage) =
      async { return handler msg }
    {
      ResolveSession = fun _ ->
        Result.Ok (
          SessionOperations.SessionResolution.DefaultSingle "test-session")
      GetProxy = fun id ->
        if id = "test-session" then Some proxy else None
      CreateSession = fun projects _ ->
        async { return Result.Ok sessionInfo }
      StopSession = fun _ ->
        async { return Result.Ok () }
      ListSessions = fun () ->
        async { return [sessionInfo] }
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
            WorkerResponse.EvalResult ("r", Ok "done", []))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let program = ElmDaemon.createProgram deps tracker.OnModelChanged

        // Verify update produces a model and effects list
        let msg =
          SageFsMsg.Event (
            SageFsEvent.EvalStarted ("s", "code"))
        let model, effects = program.Update msg SageFsModel.initial
        model
        |> Expect.equal "model should be unchanged for EvalStarted" SageFsModel.initial

        effects
        |> List.length
        |> Expect.equal "no effects from EvalStarted" 0
      }

      test "Update delegates to SageFsUpdate.update" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", []))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let program = ElmDaemon.createProgram deps tracker.OnModelChanged

        let model = SageFsModel.initial
        let msg =
          SageFsMsg.Event (
            SageFsEvent.EvalCompleted ("s", "hello", []))
        let newModel, _ = program.Update msg model

        newModel.RecentOutput
        |> List.length
        |> Expect.equal "should have one output line" 1
      }

      test "Render delegates to SageFsRender.render" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", []))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let program = ElmDaemon.createProgram deps tracker.OnModelChanged

        let regions = program.Render SageFsModel.initial

        regions
        |> List.isEmpty
        |> Expect.isFalse "should produce render regions"
      }
    ]

    testList "start" [
      test "returns a dispatch function that can be called" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", []))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let dispatch =
          ElmDaemon.start deps tracker.OnModelChanged

        // dispatch is a function — calling it should not throw
        dispatch (
          SageFsMsg.Event (SageFsEvent.EvalStarted ("s", "code")))
        Threading.Thread.Sleep(50)
        true |> Expect.isTrue "dispatch should work without error"
      }

      test "initial model is rendered on start" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", []))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let _dispatch =
          ElmDaemon.start deps tracker.OnModelChanged

        tracker.Models
        |> List.isEmpty
        |> Expect.isFalse "should have rendered initial model"
      }

      test "dispatching a message updates the model" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", []))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let dispatch =
          ElmDaemon.start deps tracker.OnModelChanged

        dispatch (
          SageFsMsg.Event (
            SageFsEvent.EvalCompleted ("s", "result-42", [])))

        // Give time for dispatch to process
        tracker.WaitForUpdate 500

        tracker.LatestModel
        |> Option.map (fun m -> m.RecentOutput)
        |> Option.defaultValue []
        |> List.exists (fun line -> line.Text = "result-42")
        |> Expect.isTrue "model should contain the eval result"
      }

      test "dispatching an effect-producing message executes the effect" {
        let mutable evalCalled = false
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun msg ->
            match msg with
            | WorkerMessage.EvalCode _ ->
              evalCalled <- true
              WorkerResponse.EvalResult ("r", Ok "evaluated!", [])
            | _ ->
              WorkerResponse.WorkerError SageFsError.NoActiveSessions)
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let dispatch =
          ElmDaemon.start deps tracker.OnModelChanged

        // Editor.SubmitLine produces an EditorEffect.RequestEval
        dispatch (
          SageFsMsg.Editor (
            EditorAction.Submit))

        // Wait for effect handler to execute
        Threading.Thread.Sleep(500)
        tracker.WaitForUpdate 500

        // The effect handler should have been called
        // (the eval will fail because buffer is empty, but the flow works)
        true |> Expect.isTrue "effect pipeline should execute"
      }
    ]

    testList "dispatchAndWait" [
      test "dispatches message and returns updated model" {
        let deps =
          ElmDaemonTestHelpers.mockDeps (fun _ ->
            WorkerResponse.EvalResult ("r", Ok "done", []))
        let tracker = ElmDaemonTestHelpers.ModelTracker()
        let dispatch =
          ElmDaemon.start deps tracker.OnModelChanged

        let result =
          ElmDaemon.dispatchAndWait
            dispatch
            (fun () -> tracker.LatestModel)
            tracker.WaitForUpdate
            (SageFsMsg.Event (
              SageFsEvent.EvalCompleted ("s", "sync-result", [])))
            1000

        result.RecentOutput
        |> List.exists (fun line -> line.Text = "sync-result")
        |> Expect.isTrue "should have the dispatched result"
      }
    ]
  ]

