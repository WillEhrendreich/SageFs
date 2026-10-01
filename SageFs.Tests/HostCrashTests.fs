/// A session whose FSI host died on its own must leave Ready, say why, and say the truth about what is lost.
///
/// Found live on 0.6.865: status stayed Ready/Healthy, every later eval answered "the FSI host exited (code 134)"
/// and the guidance said "Do NOT reset the session. previous definitions are still valid", which was false.
/// The pure decisions are pinned here; the client side is in FsiHostLostTests, the interleavings in the eval-actor
/// simulation, and the whole thing against a real host in HostCrashRecoveryTests.
module SageFs.Tests.HostCrashTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.EvalActorDecision
open SageFs.McpTools
open SageFs.WorkerProtocol

let private crash : HostCrash =
  { Exit = ExitedWith 134
    Output = "Unhandled exception. System.OverflowException: Arithmetic operation resulted in an overflow.\n   at FSI_0004.spike@65.Invoke()" }

let private handle : WorkerHandle = { Pid = 4711; Port = Some 6001 }

let private crashed = SessionStatus.HostCrashed crash

[<Tests>]
let tests =
  testList "a host that crashed" [

    testList "the eval actor's decision" [
      let next = SessionGeneration.next SessionGeneration.initial

      testCase "a crash of the current host moves an idle or evaluating session out of service" <| fun _ ->
        for activity in [ SessionActivity.Idle; SessionActivity.Evaluating ] do
          decide SessionGeneration.initial (EvalPhase.Active activity) (EvalInput.HostEnded(SessionGeneration.initial, Crashed crash))
          |> Expect.equal (sprintf "a crash while %A" activity) (EvalDecision.MarkHostCrashed crash)

      testCase "a purposeful end is never a crash" <| fun _ ->
        decide SessionGeneration.initial (EvalPhase.Active SessionActivity.Idle) (EvalInput.HostEnded(SessionGeneration.initial, Retired))
        |> Expect.equal "Retired is ignored" EvalDecision.IgnoreHostEnd

      testCase "the crash of a host a reset already replaced is ignored" <| fun _ ->
        decide next (EvalPhase.Active SessionActivity.Idle) (EvalInput.HostEnded(SessionGeneration.initial, Crashed crash))
        |> Expect.equal "the old host's crash does not touch the new session" EvalDecision.IgnoreHostEnd

      testCase "a crash is reported once" <| fun _ ->
        decide SessionGeneration.initial (EvalPhase.Active(SessionActivity.HostCrashed crash)) (EvalInput.HostEnded(SessionGeneration.initial, Crashed crash))
        |> Expect.equal "a second report of the same crash is ignored" EvalDecision.IgnoreHostEnd

      testCase "an eval submitted to a crashed session is refused with the crash, not run" <| fun _ ->
        decide SessionGeneration.initial (EvalPhase.Active(SessionActivity.HostCrashed crash)) EvalInput.Submit
        |> Expect.equal "the refusal is the typed crash" (EvalDecision.RejectEval(SageFsError.FsiHostCrashed crash))

      testCase "a reset is how a crashed session recovers" <| fun _ ->
        decide SessionGeneration.initial (EvalPhase.Active(SessionActivity.HostCrashed crash)) EvalInput.Reset
        |> Expect.equal "the reset advances the generation" EvalDecision.AdvanceGenerationAndReset

      testCase "an eval that finishes after the crash does not bring the session back to Idle" <| fun _ ->
        activityAfterFinished (SessionActivity.HostCrashed crash)
        |> Expect.equal "still crashed" (SessionActivity.HostCrashed crash)
        for activity in [ SessionActivity.Evaluating; SessionActivity.Cancelling; SessionActivity.Idle ] do
          activityAfterFinished activity |> Expect.equal (sprintf "%A settles to Idle" activity) SessionActivity.Idle

      testCase "a cancel does not clear a crash" <| fun _ ->
        applyCancelAck (SessionActivity.HostCrashed crash)
        |> Expect.equal "still crashed" (SessionActivity.HostCrashed crash)
    ]

    testList "what the user is told" [
      testCase "the crash says what failed, why, and what is lost" <| fun _ ->
        let text = SageFsError.describe (SageFsError.FsiHostCrashed crash)
        text |> Expect.stringContains "that the host crashed" "crashed"
        text |> Expect.stringContains "the exit code" "134"
        text |> Expect.stringContains "the crash text" "System.OverflowException"
        text |> Expect.stringContains "the frame" "FSI_0004.spike@65"
        text |> Expect.stringContains "that the state is gone" "definitions"

      testCase "the advice is to reset, and never to leave the session alone" <| fun _ ->
        SageFsError.suggestedAction (SageFsError.FsiHostCrashed crash)
        |> Expect.stringContains "reset" "hard_reset_fsi_session"
        let full = SageFsError.describeForAgent (SageFsError.FsiHostCrashed crash)
        Expect.isFalse "no false reassurance" (full.Contains "Do NOT reset")
        Expect.isFalse "no false reassurance about definitions" (full.Contains "still valid")

      testCase "an eval that crashed the host reads as a crash through the tool's formatter" <| fun _ ->
        let response = WorkerResponse.EvalResult("r", Result.Error(SageFsError.FsiHostCrashed crash), [], Map.empty)
        let text = formatWorkerEvalResult WorkflowTypes.SessionWorkflow.Interactive response
        text |> Expect.stringContains "the crash text" "System.OverflowException"
        text |> Expect.stringContains "the way out" "hard_reset_fsi_session"
        Expect.isFalse "no false reassurance" (text.Contains "Do NOT reset")
        Expect.isFalse "no generic guess at what the error text meant" (text.Contains "This error is in YOUR submitted code")

      testCase "a crash with no output still says it crashed and how" <| fun _ ->
        let bare = { Exit = ConnectionClosed; Output = "" }
        let text = SageFsError.describe (SageFsError.FsiHostCrashed bare)
        text |> Expect.stringContains "that the host crashed" "crashed"
        text |> Expect.stringContains "the connection closing" "connection"
    ]

    testList "the session's own status" [
      testCase "a worker that reports the crash moves the registry out of Ready, keeping the worker it still has" <| fun _ ->
        SessionLifecycleStatus.ofWorkerReport (SessionLifecycleStatus.Ready handle) crashed
        |> Expect.equal "HostCrashed carries the handle and the crash" (SessionLifecycleStatus.HostCrashed(handle, crash))

      testCase "the crash is a state that needs a reset: not operational, not dead, reset reachable" <| fun _ ->
        let status = SessionLifecycleStatus.HostCrashed(handle, crash)
        SessionLifecycleStatus.toSessionState status |> Expect.equal "gates like Faulted, which keeps reset available" SessionState.Faulted
        SessionLifecycleStatus.isOperational status |> Expect.isFalse "cannot take work"
        SessionLifecycleStatus.isDead status |> Expect.isFalse "its worker is alive and a reset is how it comes back"
        SessionLifecycleStatus.workerPid status |> Expect.equal "the worker is still there" (Some handle.Pid)
        SessionLifecycleStatus.label status |> Expect.equal "named for what happened" "HostCrashed"
        Affordances.availableTools SessionState.Faulted |> Expect.contains "reset is offered" "hard_reset_fsi_session"

      testCase "a reset that brings the host back is reported as Ready again" <| fun _ ->
        SessionLifecycleStatus.ofWorkerReport (SessionLifecycleStatus.HostCrashed(handle, crash)) SessionStatus.Ready
        |> Expect.equal "Ready replaces the crash" (SessionLifecycleStatus.Ready handle)

      testCase "a terminal Faulted is not undone by a worker that happens to report a crash" <| fun _ ->
        let faulted = SessionLifecycleStatus.Faulted(FaultReason.Reported "gone")
        SessionLifecycleStatus.ofWorkerReport faulted crashed |> Expect.equal "sticky" faulted

      testCase "the worker's own report maps to Faulted state and says it is not alive" <| fun _ ->
        SessionStatus.toSessionState crashed |> Expect.equal "gates like Faulted" SessionState.Faulted
        SessionStatus.isAlive crashed |> Expect.isFalse "a session that cannot take work is not alive"
        SessionStatus.label crashed |> Expect.equal "named for what happened" "HostCrashed"

      testCase "health is Failed with the crash and the way out, not Healthy" <| fun _ ->
        match SessionHealth.classify (SessionLifecycleStatus.HostCrashed(handle, crash)) [] None with
        | SessionHealth.Failed reason ->
          reason |> Expect.stringContains "the crash" "System.OverflowException"
          reason |> Expect.stringContains "the way out" "hard_reset_fsi_session"
        | other -> failtestf "expected Failed, got %A" other
    ]

    testList "the registry follows the worker's report" [
      let sync = WorkerHealthProbe.registryUpdate

      testCase "a Ready registry learns of the crash" <| fun _ ->
        sync (SessionLifecycleStatus.Ready handle) crashed
        |> Expect.equal "replaced" (WorkerHealthProbe.RegistryUpdate.Replace(SessionLifecycleStatus.HostCrashed(handle, crash)))

      testCase "a registry that already knows does not hear it again" <| fun _ ->
        sync (SessionLifecycleStatus.HostCrashed(handle, crash)) crashed
        |> Expect.equal "no change" WorkerHealthProbe.RegistryUpdate.NoChange

      testCase "a registry that knows learns the host came back" <| fun _ ->
        sync (SessionLifecycleStatus.HostCrashed(handle, crash)) SessionStatus.Ready
        |> Expect.equal "Ready again" (WorkerHealthProbe.RegistryUpdate.Replace(SessionLifecycleStatus.Ready handle))

      testCase "an ordinary report changes nothing: the probe is not a second status writer" <| fun _ ->
        for reported in [ SessionStatus.Ready; SessionStatus.Evaluating; SessionStatus.Starting; SessionStatus.Building "dotnet build" ] do
          sync (SessionLifecycleStatus.Ready handle) reported
          |> Expect.equal (sprintf "%A while Ready" reported) WorkerHealthProbe.RegistryUpdate.NoChange

      testCase "a session the daemon already marked Faulted or Stopped is left alone" <| fun _ ->
        for status in [ SessionLifecycleStatus.Faulted(FaultReason.Reported "x"); SessionLifecycleStatus.Stopped ] do
          sync status crashed |> Expect.equal "terminal" WorkerHealthProbe.RegistryUpdate.NoChange
    ]

    testList "the tool gate" [
      let workingDirectory = "/work/host-crash"

      let mkCtx (answer: WorkerMessage -> Async<WorkerResponse>) : McpContext =
        let info : SessionInfo =
          { Id = SessionId.newId ()
            Name = None
            Projects = []
            WorkingDirectory = workingDirectory
            SolutionRoot = None
            Status = SessionLifecycleStatus.Ready handle
            Workflow = WorkflowTypes.SessionWorkflow.Interactive
            CreatedAt = DateTime.UtcNow
            LastActivity = DateTime.UtcNow
            ActiveProject = None
            ProjectRoles = []
            App = AppRun.AppRunState.NotRunning
            Rebuild = LastRebuild.NeverRebuilt
            Reload = SessionReload.NoReloadYet }
        let ops : SessionManagementOps =
          { SessionManagementOps.stub with
              GetProxy = fun _ -> Task.FromResult(Some answer)
              GetSessionInfo = fun _ -> Task.FromResult(Some info)
              GetAllSessions = fun () -> Task.FromResult [ info ]
              UpdateSessionStatus = fun _ _ -> Task.FromResult () }
        { FrictionStore = None
          DiagnosticsChanged = (Event<Features.DiagnosticsStore.T>()).Publish
          StateChanged = None
          SessionOps = ops
          SessionMap = ConcurrentDictionary<string, string>()
          McpPort = 0
          Dispatch = None
          GetElmModel = None
          GetElmRegions = None
          GetWarmupContext = None
          GetFeatureState = None
          RecordEval = None
          ActivityTracker = AgentActivityTracker.create ()
          LiveSnapshotSink = None
          CohortOwner = None
          GetDaemonHealth = fun () -> None
          GetProcessTelemetry = fun () -> None }

      let crashedWorker (msg: WorkerMessage) : Async<WorkerResponse> =
        async {
          match msg with
          | WorkerMessage.GetStatus replyId ->
            let snapshot : WorkerStatusSnapshot =
              { Status = crashed
                StatusMessage = Some(HostCrash.describe crash)
                EvalCount = 0
                AvgDurationMs = FixtureDurations.notRunMsInt64
                MinDurationMs = FixtureDurations.notRunMsInt64
                MaxDurationMs = FixtureDurations.notRunMsInt64
                Projects = []
                CoreVersion = "0.0.0-test" }
            return WorkerResponse.StatusResult(replyId, snapshot)
          | other -> return failwithf "unexpected worker message: %A" other
        }

      testTask "an eval is refused with the crash and the way out, not with 'wait for Ready'" {
        let! verdict = admitToolCallWithin Timeouts.gateStatusProbe (mkCtx crashedWorker) "mcp" None (Some workingDirectory) "send_fsharp_code"
        match verdict with
        | Result.Error refusal ->
          refusal |> Expect.stringContains "the crash" "System.OverflowException"
          refusal |> Expect.stringContains "the way out" "hard_reset_fsi_session"
          Expect.isFalse "not the generic wait advice" (refusal.Contains "Wait for session to reach Ready")
        | Result.Ok _ -> failtest "a session whose host crashed must not admit an eval"
      }

      testTask "a reset is admitted, because it is how the session comes back" {
        for tool in [ "hard_reset_fsi_session"; "reset_fsi_session" ] do
          let! verdict = admitToolCallWithin Timeouts.gateStatusProbe (mkCtx crashedWorker) "mcp" None (Some workingDirectory) tool
          match verdict with
          | Result.Ok _ -> ()
          | Result.Error refusal -> failtestf "%s was refused on a crashed session: %s" tool refusal
      }
    ]
  ]
