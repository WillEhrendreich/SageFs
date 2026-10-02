/// `wait_seconds` on get_session_status. Weak models could not wait for a
/// warming session, so they slept or polled: two or three sleeps a run and
/// 19 to 28 SageFs calls. The tool now parks on the SessionManager's
/// event-driven AwaitReady instead, and says what the wait did.
module SageFs.Tests.StatusWaitTests

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.McpTools
open SageFs.WorkerProtocol


/// A session whose registry status the test controls, with a fake AwaitReady
/// that records each call and lets the test decide when it answers.
type private Harness =
  { Ctx: McpContext
    Sid: string
    Status: SessionLifecycleStatus ref
    /// What the session's rebuild record says; a test moves it the way the manager does.
    Rebuild: LastRebuild ref
    AwaitReadyCalls: ConcurrentQueue<TimeSpan>
    AwaitReadyEntered: TaskCompletionSource<unit> }

let private isServing (status: SessionLifecycleStatus) : bool =
  match status with
  | SessionLifecycleStatus.Ready _
  | SessionLifecycleStatus.Evaluating _ -> true
  | SessionLifecycleStatus.Starting _
  | SessionLifecycleStatus.Building _
  | SessionLifecycleStatus.Faulted _
  | SessionLifecycleStatus.HostCrashed _
  | SessionLifecycleStatus.Restarting _
  | SessionLifecycleStatus.Stopped -> false

let private mkHarnessRebuilding
  (initial: SessionLifecycleStatus)
  (initialRebuild: LastRebuild)
  (awaitReady: SessionLifecycleStatus ref -> LastRebuild ref -> TimeSpan -> Task<Result<unit, SageFsError>>)
  : Harness =
  let sid = SessionId.newId ()
  let status = ref initial
  let rebuild = ref initialRebuild
  let calls = ConcurrentQueue<TimeSpan>()
  let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
  let info () : SessionInfo =
    { Id = sid
      Name = None
      Projects = []
      WorkingDirectory = "/work/status-wait"
      SolutionRoot = None
      Status = status.Value
      Workflow = WorkflowTypes.SessionWorkflow.Interactive
      CreatedAt = DateTime.UtcNow
      LastActivity = DateTime.UtcNow
      ActiveProject = None
      ProjectRoles = []
      App = AppRun.AppRunState.NotRunning
      Rebuild = rebuild.Value
      Reload = SessionReload.NoReloadYet; Freshness = SageFs.ReplFreshness.InSync }
  let proxy : SessionProxy =
    fun msg ->
      async {
        match msg with
        | WorkerMessage.GetStatus replyId ->
          return
            WorkerResponse.StatusResult(
              replyId,
              { Status = SessionStatus.Ready
                StatusMessage = None
                EvalCount = 0
                AvgDurationMs = FixtureDurations.unmeasuredMs
                MinDurationMs = FixtureDurations.unmeasuredMs
                MaxDurationMs = FixtureDurations.unmeasuredMs
                Projects = []
                CoreVersion = "0.0.0-test" })
        | other -> return failwithf "unexpected worker message in status wait test: %A" other
      }
  let ops : SessionManagementOps =
    { SessionManagementOps.stub with
        GetProxy = fun _ ->
          match isServing status.Value with
          | true -> Task.FromResult(Some proxy)
          | false -> Task.FromResult None
        GetSessionInfo = fun _ -> Task.FromResult(Some (info ()))
        GetAllSessions = fun () -> Task.FromResult [ info () ]
        AwaitReady = fun _ timeout ->
          calls.Enqueue timeout
          entered.TrySetResult(()) |> ignore
          awaitReady status rebuild timeout }
  let ctx : McpContext =
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
      LiveBindings = None
      CohortOwner = None
      GetDaemonHealth = fun () -> None
      GetProcessTelemetry = fun () -> None }
  { Ctx = ctx
    Sid = SessionId.value sid
    Status = status
    Rebuild = rebuild
    AwaitReadyCalls = calls
    AwaitReadyEntered = entered }

/// A session nobody has rebuilt.
let private mkHarness
  (initial: SessionLifecycleStatus)
  (awaitReady: SessionLifecycleStatus ref -> TimeSpan -> Task<Result<unit, SageFsError>>)
  : Harness =
  mkHarnessRebuilding initial LastRebuild.NeverRebuilt (fun status _ timeout -> awaitReady status timeout)

let private starting = SessionLifecycleStatus.Starting { Pid = 7; Port = None }
let private ready = SessionLifecycleStatus.Ready { Pid = 7; Port = Some 6000 }

/// Never answers: a fake for the cases that must not wait at all.
let private neverAwaited (_: SessionLifecycleStatus ref) (_: TimeSpan) : Task<Result<unit, SageFsError>> =
  Task.FromException<Result<unit, SageFsError>>(InvalidOperationException "AwaitReady must not be called")

let private read (json: string) : string * string * int64 =
  use doc = JsonDocument.Parse json
  let wait = doc.RootElement.GetProperty("wait")
  doc.RootElement.GetProperty("state").GetString(),
  wait.GetProperty("outcome").GetString(),
  wait.GetProperty("waitedMs").GetInt64()

/// The state, how the wait ended, how long it parked, and what the last rebuild did. A field the daemon
/// does not send reads as "absent", so a missing field is a failed expectation and not a crash.
let private readWithRebuild (json: string) : string * string * int64 * string =
  use doc = JsonDocument.Parse json
  let wait = doc.RootElement.GetProperty("wait")
  let lastRebuild =
    match wait.TryGetProperty "lastRebuild" with
    | true, value -> value.GetString()
    | false, _ -> "absent"
  doc.RootElement.GetProperty("state").GetString(),
  wait.GetProperty("outcome").GetString(),
  wait.GetProperty("waitedMs").GetInt64(),
  lastRebuild

[<Tests>]
let tests =
  testList "get_session_status wait_seconds" [

    testTask "a warming session parks on AwaitReady and answers the moment it is Ready" {
      let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      let h =
        mkHarness starting (fun status _ ->
          task {
            do! release.Task
            status.Value <- ready
            return Result.Ok ()
          })
      let statusTask = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 30
      let! winner = Task.WhenAny(h.AwaitReadyEntered.Task, Task.Delay TestTimeouts.shortPatience)
      Object.ReferenceEquals(winner, h.AwaitReadyEntered.Task)
      |> Expect.isTrue "the call reached AwaitReady instead of answering at once"
      statusTask.IsCompleted
      |> Expect.isFalse "the call is parked while the session is still warming"
      release.SetResult ()
      let! json = statusTask
      let state, outcome, _ = read json
      outcome |> Expect.equal "the payload says the wait ended in Ready" "BecameReady"
      state |> Expect.equal "and the status after it is the Ready one" "Ready"
    }

    testTask "an already Ready session never waits" {
      let h = mkHarness ready neverAwaited
      let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 30
      let state, outcome, waitedMs = read json
      outcome |> Expect.equal "no wait was needed" "NotNeeded"
      waitedMs |> Expect.equal "and none was spent" 0L
      state |> Expect.equal "the status is the normal one" "Ready"
      h.AwaitReadyCalls.Count |> Expect.equal "AwaitReady was not called" 0
    }

    testTask "a Faulted session never waits" {
      let h =
        mkHarness (SessionLifecycleStatus.Faulted (FaultReason.report "build failed")) neverAwaited
      let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 30
      let state, outcome, _ = read json
      outcome |> Expect.equal "a fault will not clear by waiting" "NotNeeded"
      state |> Expect.equal "and the payload still says Faulted" "Faulted"
      h.AwaitReadyCalls.Count |> Expect.equal "AwaitReady was not called" 0
    }

    testTask "a Stopped session never waits" {
      let h = mkHarness SessionLifecycleStatus.Stopped neverAwaited
      let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 30
      let _, outcome, _ = read json
      outcome |> Expect.equal "a stopped session will not become Ready" "NotNeeded"
      h.AwaitReadyCalls.Count |> Expect.equal "AwaitReady was not called" 0
    }

    testTask "wait_seconds of zero, the default, never waits on a warming session" {
      let h = mkHarness starting neverAwaited
      let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 0
      let state, outcome, _ = read json
      outcome |> Expect.equal "zero means the old immediate answer" "NotNeeded"
      state |> Expect.equal "and the session is reported warming" "WarmingUp"
      h.AwaitReadyCalls.Count |> Expect.equal "AwaitReady was not called" 0
    }

    testTask "a negative wait_seconds is clamped to no wait, not an error" {
      let h = mkHarness starting neverAwaited
      let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None (-5)
      let _, outcome, _ = read json
      outcome |> Expect.equal "clamped to zero" "NotNeeded"
      h.AwaitReadyCalls.Count |> Expect.equal "AwaitReady was not called" 0
    }

    testTask "a wait that runs out is reported as TimedOut with the session still warming" {
      let h =
        mkHarness starting (fun _ timeout ->
          Task.FromResult(Result.Error (SageFsError.WorkerTimeout ("s", "restart", timeout.TotalSeconds))))
      let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None (int TestTimeouts.statusWaitRequest.TotalSeconds)
      let state, outcome, _ = read json
      outcome |> Expect.equal "the timeout is named" "TimedOut"
      state |> Expect.equal "and the payload is the warming one" "WarmingUp"
      h.AwaitReadyCalls.ToArray() |> Expect.equal "it waited the time it was asked for" [| TestTimeouts.statusWaitRequest |]
    }

    testTask "a session that faults while we wait is reported as Faulted" {
      let h =
        mkHarness starting (fun status _ ->
          status.Value <- SessionLifecycleStatus.Faulted (FaultReason.report "warmup crashed")
          Task.FromResult(Result.Error (SageFsError.WorkerSpawnFailed "warmup crashed")))
      let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 30
      let state, outcome, _ = read json
      outcome |> Expect.equal "the wait ended in a fault" "Faulted"
      state |> Expect.equal "and the status after it says Faulted" "Faulted"
    }

    testTask "wait_seconds above the cap is clamped to 60 seconds" {
      let h =
        mkHarness starting (fun _ timeout ->
          Task.FromResult(Result.Error (SageFsError.WorkerTimeout ("s", "restart", timeout.TotalSeconds))))
      let! _ = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 100000
      h.AwaitReadyCalls.ToArray() |> Expect.equal "it parked for the cap, not the request" [| Timeouts.statusWaitCap |]
    }

    testTask "a Ready session whose rebuild is still running parks, and answers with what the rebuild did" {
      // The old worker keeps serving through a build-first rebuild, so the lifecycle says Ready the
      // whole time. The caller asked about the NEW build.
      let h =
        mkHarnessRebuilding ready (LastRebuild.Latest (RebuildOutcome.InProgress DateTime.UtcNow)) (fun _ rebuild _ ->
          rebuild.Value <- LastRebuild.Latest (RebuildOutcome.Succeeded DateTime.UtcNow)
          Task.FromResult(Result.Ok ()))
      let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 30
      let _, outcome, _, lastRebuild = readWithRebuild json
      outcome |> Expect.equal "a rebuild in progress is waited on, never NotNeeded" "BecameReady"
      lastRebuild |> Expect.equal "and the answer says what the rebuild did" "Succeeded"
      h.AwaitReadyCalls.Count |> Expect.equal "it parked on AwaitReady once" 1
    }

    testTask "a Ready session whose rebuild failed while we waited is Faulted, not BecameReady, with the failure named" {
      let failure = SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "Hello.fs(3,5): error FS0001: expected int" ])
      let h =
        mkHarnessRebuilding ready (LastRebuild.Latest (RebuildOutcome.InProgress DateTime.UtcNow)) (fun _ rebuild _ ->
          rebuild.Value <- LastRebuild.Latest (RebuildOutcome.FailedStillServing (failure, DateTime.UtcNow))
          Task.FromResult(Result.Error failure))
      let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 30
      let _, outcome, _, lastRebuild = readWithRebuild json
      outcome |> Expect.equal "the build the caller waited for does not exist, so the wait did not succeed" "Faulted"
      lastRebuild |> Expect.equal "and the answer says the old build is still serving" "FailedStillServing"
    }

    testTask "a rebuild asked for through hard_reset_fsi_session is waited on even before the manager has put it on the record" {
      // The manager records a rebuild as in progress a moment after the request is posted. A caller who asks
      // to wait straight after asking for the rebuild must not read a session with no rebuild running.
      let finish = TaskCompletionSource<Result<string, SageFsError>>(TaskCreationOptions.RunContinuationsAsynchronously)
      let h = mkHarness ready (fun _ _ -> Task.FromResult(Result.Ok ()))
      let ctx = { h.Ctx with SessionOps = { h.Ctx.SessionOps with RestartSession = fun _ _ -> finish.Task } }
      let! initiated = hardResetSession ctx "agent" true (Some h.Sid) None
      initiated |> Expect.stringContains "the rebuild was accepted" "Hard reset initiated"
      initiated |> Expect.stringContains "and the reply points at the wait" "wait_seconds"
      let! json = getSessionStatusAwaiting ctx "agent" (Some h.Sid) None 30
      let _, outcome, _, _ = readWithRebuild json
      outcome |> Expect.equal "the status shows no rebuild yet, and the wait still covers the one just asked for" "BecameReady"
      h.AwaitReadyCalls.Count |> Expect.equal "it parked on AwaitReady once" 1
      finish.SetResult (Result.Ok "done")
    }

    testTask "a rebuild that timed out while we waited is TimedOut and still says it is in progress" {
      let h =
        mkHarnessRebuilding ready (LastRebuild.Latest (RebuildOutcome.InProgress DateTime.UtcNow)) (fun _ _ timeout ->
          Task.FromResult(Result.Error (SageFsError.WorkerTimeout ("s", "rebuild", timeout.TotalSeconds))))
      let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 30
      let _, outcome, _, lastRebuild = readWithRebuild json
      outcome |> Expect.equal "the timeout is named" "TimedOut"
      lastRebuild |> Expect.equal "and the rebuild is still reported as running" "InProgress"
    }

    testTask "a Ready session whose last rebuild is over never waits, whichever way it ended" {
      let endings =
        [ RebuildOutcome.Succeeded DateTime.UtcNow
          RebuildOutcome.FailedStillServing (SageFsError.HardResetFailed "x", DateTime.UtcNow) ]
      for ending in endings do
        let h = mkHarnessRebuilding ready (LastRebuild.Latest ending) (fun status _ _ -> neverAwaited status TimeSpan.Zero)
        let! json = getSessionStatusAwaiting h.Ctx "agent" (Some h.Sid) None 30
        let _, outcome, _, _ = readWithRebuild json
        outcome |> Expect.equal (sprintf "a finished rebuild (%A) is not waited on" ending) "NotNeeded"
        h.AwaitReadyCalls.Count |> Expect.equal "AwaitReady was not called" 0
    }

    testProperty "clampSeconds always lands between zero and the cap" <| fun (seconds: int) ->
      let clamped = SessionStatusPayload.StatusWait.clampSeconds seconds
      clamped >= TimeSpan.Zero && clamped <= Timeouts.statusWaitCap
  ]
