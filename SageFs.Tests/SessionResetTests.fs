module SageFs.Tests.SessionResetTests

open Expecto
open Expecto.Flip
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open SageFs
open SageFs.AppState
open SageFs.McpTools
open SageFs.SessionManager
open SageFs.Features.Events
open SageFs.Tests.TestInfrastructure
open SageFs.WorkerProtocol

[<Tests>]
let sessionManagerBuildPathTests =
  testList "SessionManager build path resolution" [
    testCase "relative project path resolves under working directory" <| fun _ ->
      let workingDir = @"C:\Code\Repos\SageFs\viz-output\code-city"
      let project = "CodeCity.fsproj"
      resolveBuildProjectPath workingDir project
      |> Expect.equal "relative project should resolve under the session working directory" (Path.Combine(workingDir, project))

    testCase "absolute project path is preserved" <| fun _ ->
      let workingDir = Path.GetTempPath()
      let project = Path.Combine(Path.GetTempPath(), "code-city-tests", "CodeCity.Tests.fsproj")
      resolveBuildProjectPath workingDir project
      |> Expect.equal "absolute project should not be rewritten" project
  ]

[<Tests>]
let sessionResetTests =
  testSequenced <| Integration.hostList "Session reset" [

    testCase "eval → reset → value is gone"
    <| fun _ ->
      task {
        let ctx = sharedCtxWith (SessionId.newId())

        // Define a value
        let! defineResult = sendFSharpCode ctx "test" "let resetTestVal = 99;;" OutputFormat.Text None None None None None None
        defineResult
        |> Expect.stringContains
          "Definition should succeed"
          "val resetTestVal"

        // Reset the session
        let! resetResult = resetSession ctx "test" None None
        resetResult
        |> Expect.stringContains
          "Reset should report success"
          "reset"

        // Try to use the value — should fail
        let! afterReset = sendFSharpCode ctx "test" "resetTestVal;;" OutputFormat.Text None None None None None None
        afterReset
        |> Expect.stringContains
          "Value should not exist after reset"
          "Error"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "reset → eval 1+1 succeeds (session works)"
    <| fun _ ->
      task {
        let ctx = sharedCtxWith (SessionId.newId())

        let! resetResult = resetSession ctx "test" None None
        resetResult
        |> Expect.stringContains
          "Reset should succeed"
          "reset"

        let! result = sendFSharpCode ctx "test" "1 + 1;;" OutputFormat.Text None None None None None None
        result
        |> Expect.stringContains
          "Should evaluate after reset"
          "2"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

  ]

/// A McpContext whose RestartSession is fully controlled by the test: it
/// records every (sessionId, rebuild) call and returns whatever Result the
/// test configures, so assertions confirm hardResetSession's actual
/// behavior (does it route through the owner? does it preserve the
/// owner's own outcome message verbatim? does it tell Elm the session is
/// Running?) instead of coincidentally matching a hardcoded literal that
/// happens to be the same string some other layer produces today.
let private mkPushbackCtx (restartResult: Result<string, SageFsError>) =
  let sid = SessionId.newId()
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["test"] <- SessionId.value sid
  let restartCalls = ResizeArray<SessionId * bool>()
  let statusEvents = ResizeArray<SessionDisplayStatus>()
  let dummyProxy : SessionProxy = fun _ -> async { return WorkerResponse.WorkerReady }
  let ops =
    { SessionManagementOps.stub with
        GetProxy = fun _ -> Task.FromResult(Some dummyProxy)
        RestartSession = fun sessionId rebuild ->
          restartCalls.Add(sessionId, rebuild)
          Task.FromResult restartResult }
  let ctx : McpContext =
    { FrictionStore = None
      DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
      StateChanged = None
      SessionOps = ops
      SessionMap = sessionMap
      McpPort = 0
      Dispatch = Some (fun msg ->
        match msg with
        | SageFsMsg.Event (SageFsEvent.SessionStatusChanged (_, display)) -> statusEvents.Add display
        | _ -> ())
      GetElmModel = None
      GetElmRegions = None
      GetWarmupContext = None
      GetFeatureState = None
      ActivityTracker = AgentActivityTracker.create()
      LiveSnapshotSink = None }
  ctx, restartCalls, statusEvents

[<Tests>]
let resetPushbackTests =
  Integration.hostList "Reset pushback warnings" [

    testCase "hard reset on healthy session routes through the owner's restart, preserves its outcome message, and includes the definitions-cleared warning"
    <| fun _ ->
      task {
        let sentinel = sprintf "owner-restart-outcome-%O" (System.Guid.NewGuid())
        let ctx, restartCalls, statusEvents = mkPushbackCtx (Ok sentinel)

        let! result = hardResetSession ctx "test" false None None

        result
        |> Expect.stringContains
          "A hard reset without a rebuild clears REPL definitions, so it must warn about that"
          "⚠️ NOTE:"

        result
        |> Expect.stringContains
          "The owner's own restart-outcome message must be preserved verbatim, not replaced"
          sentinel

        restartCalls.Count
        |> Expect.equal
          "the session's owner (not an in-process rebuild) is asked to restart the worker process, exactly once"
          1
        snd restartCalls.[0]
        |> Expect.isFalse "rebuild=false must be passed through unchanged"

        statusEvents |> Seq.toList
        |> Expect.contains
          "a successful restart tells Elm the session is Running again"
          SessionDisplayStatus.Running
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "hard reset failure surfaces the owner's error and never claims success"
    <| fun _ ->
      task {
        let failure = SageFsError.HardResetFailed "worker refused to restart"
        let ctx, restartCalls, statusEvents = mkPushbackCtx (Error failure)

        let! result = hardResetSession ctx "test" false None None

        result
        |> Expect.stringContains
          "a failed restart must be reported as an error, not silently swallowed"
          "Error:"

        restartCalls.Count
        |> Expect.equal "the owner's restart is still attempted exactly once" 1

        statusEvents |> Seq.toList
        |> Expect.all
          "no restart failure may be reported as the session becoming Running"
          (fun s -> s <> SessionDisplayStatus.Running)
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

    testCase "soft reset on healthy session includes warning"
    <| fun _ ->
      task {
        let ctx = sharedCtx ()
        let! result = resetSession ctx "test" None None
        result
        |> Expect.stringContains
          "Should include pushback warning for healthy session"
          "⚠️ NOTE:"
        result
        |> Expect.stringContains
          "Should still include success message"
          "reset"
      }
      |> Async.AwaitTask
      |> Async.RunSynchronously

  ]
