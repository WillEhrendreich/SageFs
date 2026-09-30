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
  Restarts: ResizeArray<SageFs.RestartPlan>
  StatusWrites: ResizeArray<WorkerProtocol.SessionLifecycleStatus>
  /// Every message routed to the session's worker.
  Routed: ResizeArray<WorkerProtocol.WorkerMessage>
  Finished: TaskCompletionSource<SessionDisplayStatus>
  /// What the registry says the last rebuild did. The SessionManager records
  /// it (see SessionManagerRebuildOutcomeTests); the tool only reads it.
  Rebuild: LastRebuild ref
}

let private mkProbe (sessionId: string) (restartResult: Result<string, SageFsError>) (statusAfter: WorkerProtocol.SessionStatus) : Probe =
  let status = ref (WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 42; Port = Some 1 })
  let rebuild = ref LastRebuild.NeverRebuilt
  let restarts = ResizeArray<SageFs.RestartPlan>()
  let writes = ResizeArray<WorkerProtocol.SessionLifecycleStatus>()
  let routed = ResizeArray<WorkerProtocol.WorkerMessage>()
  let finished = TaskCompletionSource<SessionDisplayStatus>(TaskCreationOptions.RunContinuationsAsynchronously)
  let info (id: WorkerProtocol.SessionId) : WorkerProtocol.SessionInfo =
    { Id = id; Name = None; Projects = []; WorkingDirectory = ""; SolutionRoot = None
      Status = status.Value
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow
      ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning; Rebuild = rebuild.Value; Reload = SessionReload.NoReloadYet }
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
        RestartSession = fun _ plan ->
          restarts.Add plan
          status.Value <- WorkerProtocol.SessionLifecycleStatus.ofWorkerReport status.Value statusAfter
          // This fake stands in for the owner, which records what a rebuild did
          // (and records nothing for a refusal). The tool only reads it back.
          let serving =
            match status.Value with
            | WorkerProtocol.SessionLifecycleStatus.Ready _ | WorkerProtocol.SessionLifecycleStatus.Evaluating _ | WorkerProtocol.SessionLifecycleStatus.Building _ -> true
            | _ -> false
          match plan, restartResult with
          | SageFs.RestartPlan.Rebuild _, Error error when RestartRefusal.isAlreadyInProgress error -> ()
          | SageFs.RestartPlan.Rebuild _, Error error ->
            rebuild.Value <-
              LastRebuild.Latest (
                match serving with
                | true -> RebuildOutcome.FailedStillServing (error, DateTime.UtcNow)
                | false -> RebuildOutcome.FailedNotServing (error, DateTime.UtcNow))
          | SageFs.RestartPlan.Rebuild _, Ok _ -> rebuild.Value <- LastRebuild.Latest (RebuildOutcome.Succeeded DateTime.UtcNow)
          | _ -> ()
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
      GetElmModel = None; GetElmRegions = None; GetWarmupContext = None; GetFeatureState = None; RecordEval = None
      ActivityTracker = AgentActivityTracker.create (); LiveSnapshotSink = None; CohortOwner = None
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None }
  { SessionId = sessionId; Ctx = ctx; Restarts = restarts; StatusWrites = writes; Routed = routed; Finished = finished; Rebuild = rebuild }

/// Waits for the background rebuild's final status notification.
let private awaitOutcome (p: Probe) = task {
  let! winner = Task.WhenAny(p.Finished.Task :> Task, Task.Delay 5000)
  obj.ReferenceEquals(winner, p.Finished.Task) |> Expect.isTrue "the background rebuild must report an outcome"
  return p.Finished.Task.Result
}

let private hardReset (p: Probe) = hardResetSession p.Ctx "agent1" true (Some p.SessionId) None

/// The probe's context as a session mid cold-restart sees it: Restarting, and
/// with NO proxy installed yet, which is the shape get_session_status reports as
/// warming (and the one that used to hide the rebuild behind it).
let private whileRestarting (p: Probe) : McpContext =
  let baseGet = p.Ctx.SessionOps.GetSessionInfo
  { p.Ctx with
      SessionOps =
        { p.Ctx.SessionOps with
            GetProxy = fun _ -> Task.FromResult None
            GetSessionInfo = fun id ->
              task {
                let! info = baseGet id
                return info |> Option.map (fun i -> { i with Status = WorkerProtocol.SessionLifecycleStatus.Restarting WorkerProtocol.PreviousWorker.ColdStart })
              } } }

/// `state` and `lastRestart.outcome` out of a get_session_status payload. A plain
/// function, because `use` on a JsonDocument inside a task builder picks an
/// async-disposable overload it does not satisfy.
let private stateAndLastRestartOutcome (json: string) : string * string =
  use doc = System.Text.Json.JsonDocument.Parse json
  let state = doc.RootElement.GetProperty("state").GetString()
  match doc.RootElement.TryGetProperty "lastRestart" with
  | true, restart when restart.ValueKind = System.Text.Json.JsonValueKind.Object ->
    state, restart.GetProperty("outcome").GetString()
  | _ -> failtestf "no lastRestart object in the payload: %s" json

/// `lastRestart.outcome` when the payload has one, and None when it is null. A
/// plain function for the same reason as above.
let private lastRestartLabel (json: string) : string option =
  use doc = System.Text.Json.JsonDocument.Parse json
  match doc.RootElement.TryGetProperty "lastRestart" with
  | true, restart when restart.ValueKind = System.Text.Json.JsonValueKind.Object -> Some (restart.GetProperty("outcome").GetString())
  | true, restart when restart.ValueKind = System.Text.Json.JsonValueKind.Null -> None
  | _ -> failtestf "no lastRestart in the payload: %s" json

let private buildFailed =
  SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "Program.fs(3,5): error FS0039: The value 'x' is not defined" ])

[<Tests>]
let tests = testList "MCP hard reset rebuild" [
  testTask "WHY — hard_reset rebuild=true — the owner is actually asked to rebuild because the MCP pre-check read back its own Restarting marker and silently skipped the rebuild" {
    let p = mkProbe "aaa00001" (Ok "Hard reset complete") WorkerProtocol.SessionStatus.Ready
    let! _ = hardReset p
    let! _ = awaitOutcome p
    p.Restarts |> Seq.toList |> Expect.equal "RestartSession(rebuild) called exactly once" [ SageFs.RestartPlan.Rebuild SageFs.GranularRestart.RestartSubject.Worker ]
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
  }

  testTask "WHY — hard_reset rebuild=true — a failed cold rebuild shows the build error because no worker is left serving" {
    let p = mkProbe "aaa00004" (Error buildFailed) WorkerProtocol.SessionStatus.Faulted
    let! _ = hardReset p
    let! display = awaitOutcome p
    display |> Expect.equal "the display carries the real reason" (SessionDisplayStatus.Faulted (SageFsError.describe buildFailed))
  }

  testTask "WHY — hard_reset rebuild=true — a second reset the owner REFUSES as already in progress leaves the running rebuild's state alone, because status must not report a rebuild as FAILED while it is still running" {
    let refused = SageFsError.HardResetFailed "Hard reset already in progress for this session"
    let p = mkProbe "aaa00040" (Error refused) WorkerProtocol.SessionStatus.Ready
    // A first rebuild is in flight: the registry says so.
    p.Rebuild.Value <- LastRebuild.Latest (RebuildOutcome.InProgress (DateTime.UtcNow.AddSeconds -5.0))
    let! _ = hardReset p
    let! _ = awaitOutcome p
    let! (json: string) = getSessionStatus (whileRestarting p) "agent1" (Some p.SessionId) None
    stateAndLastRestartOutcome json |> snd
    |> Expect.equal "still the FIRST rebuild, still in progress: the tool keeps no outcome, so a refusal cannot overwrite one" "InProgress"
  }

  testTask "WHY — get_session_status reports each thing the registry recorded about the last rebuild, and nothing when it recorded nothing" {
    let at = DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc)
    let cases =
      [ LastRebuild.Latest (RebuildOutcome.InProgress at), Some "InProgress"
        LastRebuild.Latest (RebuildOutcome.Succeeded at), Some "Succeeded"
        LastRebuild.Latest (RebuildOutcome.FailedStillServing (buildFailed, at)), Some "FailedStillServing"
        LastRebuild.Latest (RebuildOutcome.FailedNotServing (buildFailed, at)), Some "FailedNotServing"
        LastRebuild.NeverRebuilt, None ]
    for index, (recorded, expected) in List.indexed cases do
      let p = mkProbe (sprintf "aaa0005%d" index) (Ok "unused") WorkerProtocol.SessionStatus.Ready
      p.Rebuild.Value <- recorded
      let! (json: string) = getSessionStatus (whileRestarting p) "agent1" (Some p.SessionId) None
      lastRestartLabel json |> Expect.equal (sprintf "%A" recorded) expected
  }

  testTask "WHY — get_session_status on a session that is restarting still reports the rebuild behind it, because a cold restart is exactly the shape that hid a failing or running rebuild" {
    let p = mkProbe "aaa00041" (Ok "Hard reset complete") WorkerProtocol.SessionStatus.Ready
    p.Rebuild.Value <- LastRebuild.Latest (RebuildOutcome.InProgress DateTime.UtcNow)
    let! (json: string) = getSessionStatus (whileRestarting p) "agent1" (Some p.SessionId) None
    let state, outcome = stateAndLastRestartOutcome json
    state |> Expect.equal "this is the warming shape" "WarmingUp"
    outcome |> Expect.equal "and it says the rebuild is in progress" "InProgress"
  }

  testTask "WHY — hard_reset rebuild=false — the owner recycles the worker process, because an in-process FSI rebuild keeps the project assemblies already loaded in the worker's default load context and never sees new code" {
    let p = mkProbe "aaa00005" (Ok "Hard reset accepted — replacement worker spawning.") WorkerProtocol.SessionStatus.Ready
    let! _ = hardResetSession p.Ctx "agent1" false (Some p.SessionId) None
    p.Restarts |> Seq.toList |> Expect.equal "RestartSession(respawn-only) called exactly once" [ SageFs.RestartPlan.RespawnOnly ]
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
]
