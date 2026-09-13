module SageFs.Tests.McpHardResetRebuildTests

open System
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools

/// A hard reset against fake session ops that model the real registry
/// read-your-own-write: UpdateSessionStatus posts to the SessionManager, whose
/// snapshot GetSessionInfo reads back.
type private Probe = {
  SessionId: string
  Ctx: McpContext
  Restarts: ResizeArray<bool>
  StatusWrites: ResizeArray<WorkerProtocol.SessionLifecycleStatus>
  /// Every message routed to the session's worker.
  Routed: ResizeArray<WorkerProtocol.WorkerMessage>
  Finished: TaskCompletionSource<SessionDisplayStatus>
}

/// Each test owns its session id: rebuildOutcomes is daemon-global, and tests
/// run in parallel.
let private mkProbe (sessionId: string) (restartResult: Result<string, SageFsError>) (statusAfter: WorkerProtocol.SessionStatus) : Probe =
  let status = ref (WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 42; Port = Some 1 })
  let restarts = ResizeArray<bool>()
  let writes = ResizeArray<WorkerProtocol.SessionLifecycleStatus>()
  let routed = ResizeArray<WorkerProtocol.WorkerMessage>()
  let finished = TaskCompletionSource<SessionDisplayStatus>(TaskCreationOptions.RunContinuationsAsynchronously)
  let info (id: WorkerProtocol.SessionId) : WorkerProtocol.SessionInfo =
    { Id = id; Name = None; Projects = []; WorkingDirectory = ""; SolutionRoot = None
      Status = status.Value
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
      ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning }
  let ops =
    { SessionManagementOps.stub with
        GetProxy = fun _ ->
          Task.FromResult(Some (fun msg -> async {
            lock routed (fun () -> routed.Add msg)
            match msg with
            | WorkerProtocol.WorkerMessage.HardResetSession(_, rid) ->
              return WorkerProtocol.WorkerResponse.HardResetResult(rid, Ok "Hard reset complete. Fresh session with re-copied assemblies.")
            | _ -> return WorkerProtocol.WorkerResponse.WorkerReady }))
        GetSessionInfo = fun id -> Task.FromResult(Some (info id))
        UpdateSessionStatus = fun _ s ->
          writes.Add s
          status.Value <- s
          Task.FromResult(())
        RestartSession = fun _ rebuild ->
          restarts.Add rebuild
          status.Value <- WorkerProtocol.SessionLifecycleStatus.ofWorkerReport status.Value statusAfter
          Task.FromResult restartResult }
  let sessionMap = Collections.Concurrent.ConcurrentDictionary<string, string>()
  sessionMap.["agent1"] <- sessionId
  let ctx : McpContext =
    { FrictionStore = None
      DiagnosticsChanged = Event<Features.DiagnosticsStore.T>().Publish
      StateChanged = None; SessionOps = ops; SessionMap = sessionMap; McpPort = 0
      Dispatch = Some (fun msg ->
        match msg with
        | SageFsMsg.Event (TuiEvent.SessionStatusChanged (_, display)) -> finished.TrySetResult display |> ignore
        | _ -> ())
      GetElmModel = None; GetElmRegions = None; GetWarmupContext = None; GetFeatureState = None
      ActivityTracker = AgentActivityTracker.create (); LiveSnapshotSink = None }
  { SessionId = sessionId; Ctx = ctx; Restarts = restarts; StatusWrites = writes; Routed = routed; Finished = finished }

/// Waits for the background rebuild's final status notification.
let private awaitOutcome (p: Probe) = task {
  let! winner = Task.WhenAny(p.Finished.Task :> Task, Task.Delay 5000)
  obj.ReferenceEquals(winner, p.Finished.Task) |> Expect.isTrue "the background rebuild must report an outcome"
  return p.Finished.Task.Result
}

let private hardReset (p: Probe) = hardResetSession p.Ctx "agent1" true (Some p.SessionId) None

let private buildFailed =
  SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "Program.fs(3,5): error FS0039: The value 'x' is not defined" ])

[<Tests>]
let tests = testList "MCP hard reset rebuild" [
  testTask "WHY — hard_reset rebuild=true — the owner is actually asked to rebuild because the MCP pre-check read back its own Restarting marker and silently skipped the rebuild" {
    let p = mkProbe "aaa00001" (Ok "Hard reset complete") WorkerProtocol.SessionStatus.Ready
    let! _ = hardReset p
    let! _ = awaitOutcome p
    p.Restarts |> Seq.toList |> Expect.equal "RestartSession(rebuild=true) called exactly once" [ true ]
  }

  testTask "WHY — hard_reset rebuild=true — the MCP layer never writes session status because the SessionManager mailbox is the single owner of the registry" {
    let p = mkProbe "aaa00002" (Ok "Hard reset complete") WorkerProtocol.SessionStatus.Ready
    let! _ = hardReset p
    let! _ = awaitOutcome p
    p.StatusWrites |> Seq.toList |> Expect.isEmpty "no registry writes from the tool"
  }

  testTask "WHY — hard_reset rebuild=true — a failed build that keeps the worker serving shows Running, not Errored, because build-first leaves the last good build live" {
    let p = mkProbe "aaa00003" (Error buildFailed) WorkerProtocol.SessionStatus.Ready
    let! _ = hardReset p
    let! display = awaitOutcome p
    display |> Expect.equal "the session is still serving" SessionDisplayStatus.Running
    match rebuildOutcomes.TryGetValue p.SessionId with
    | true, RebuildOutcome.FailedStillServing (error, _) -> error |> Expect.equal "the compiler error is kept for get_fsi_status" buildFailed
    | _, other -> failtestf "expected FailedStillServing, got %A" other
  }

  testTask "WHY — hard_reset rebuild=true — a failed cold rebuild shows the build error because no worker is left serving" {
    let p = mkProbe "aaa00004" (Error buildFailed) WorkerProtocol.SessionStatus.Faulted
    let! _ = hardReset p
    let! display = awaitOutcome p
    display |> Expect.equal "the display carries the real reason" (SessionDisplayStatus.Faulted (SageFsError.describe buildFailed))
  }

  testTask "WHY — hard_reset rebuild=false — the owner recycles the worker process, because an in-process FSI rebuild keeps the project assemblies already loaded in the worker's default load context and never sees new code" {
    let p = mkProbe "aaa00005" (Ok "Hard reset accepted — replacement worker spawning.") WorkerProtocol.SessionStatus.Ready
    let! _ = hardResetSession p.Ctx "agent1" false (Some p.SessionId) None
    p.Restarts |> Seq.toList |> Expect.equal "RestartSession(rebuild=false) called exactly once" [ false ]
    p.Routed
    |> Seq.exists (function WorkerProtocol.WorkerMessage.HardResetSession _ -> true | _ -> false)
    |> Expect.isFalse "no in-process hard reset is routed to the old worker"
  }

  testTask "WHY — hard_reset rebuild=false — the MCP layer never writes session status because the SessionManager mailbox is the single owner of the registry" {
    let p = mkProbe "aaa00006" (Ok "Hard reset accepted — replacement worker spawning.") WorkerProtocol.SessionStatus.Ready
    let! _ = hardResetSession p.Ctx "agent1" false (Some p.SessionId) None
    p.StatusWrites |> Seq.toList |> Expect.isEmpty "no registry writes from the tool"
  }

  testTask "WHY — hard_reset rebuild=false — a refused restart reaches the agent as an error with a next step, because a silent success would hide that nothing was reset" {
    let refused = SageFsError.HardResetFailed "Hard reset already in progress for this session"
    let p = mkProbe "aaa00007" (Error refused) WorkerProtocol.SessionStatus.Ready
    let! reply = hardResetSession p.Ctx "agent1" false (Some p.SessionId) None
    reply |> Expect.equal "the owner's refusal, with its next step" (sprintf "Error: %s" (SageFsError.describeForAgent refused))
  }

  testProperty "WHY — RebuildOutcome.ofResult — a failure counts as still serving exactly when the owner left the session routable, because only the owner knows whether a worker survived" <|
    fun (status: WorkerProtocol.SessionStatus) ->
      let at = DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc)
      let lifecycleStatus =
        WorkerProtocol.SessionLifecycleStatus.ofWorkerReport
          (WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None })
          status
      let serving =
        match lifecycleStatus with
        | WorkerProtocol.SessionLifecycleStatus.Ready _
        | WorkerProtocol.SessionLifecycleStatus.Evaluating _
        | WorkerProtocol.SessionLifecycleStatus.Building _ -> true
        | _ -> false
      let expected =
        match serving with
        | true -> RebuildOutcome.FailedStillServing (buildFailed, at)
        | false -> RebuildOutcome.FailedNotServing (buildFailed, at)
      RebuildOutcome.ofResult at (Error buildFailed) (Some lifecycleStatus) = expected
      && RebuildOutcome.ofResult at (Ok "done") (Some lifecycleStatus) = RebuildOutcome.Succeeded at
]
