module SageFs.Tests.AppRunOrchestrationTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.AppRun
open SageFs.ProjectLoading

let private sid =
  match SessionId.validate "0a0b0c0d" with
  | Ok id -> id
  | Error e -> failwith e

let private web = "/src/Web/Web.fsproj"
let private api = "/src/Api/Api.fsproj"
let private at = DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc)
let private clock () = at
let private readyTimeout = TimeSpan.FromSeconds 5.0

let private exe (path: string) : ClassifiedProject =
  { Path = path; Role = ProjectRole.Executable; PackageRefs = [] }

let private running (runId: string) : RunningApp =
  { RunId = runId
    Project = web
    EntryPoint = "Web.Program.main"
    Endpoint = AppEndpoint.Http ("http://127.0.0.1:5123", [])
    StartedAt = at }

let private session (workflow: WorkflowTypes.SessionWorkflow) (projects: ClassifiedProject list) (app: AppRunState) : SessionInfo =
  { Id = sid
    Name = None
    Projects = projects |> List.map (fun p -> p.Path)
    WorkingDirectory = "/src"
    SolutionRoot = None
    CreatedAt = at
    LastActivity = at
    Status = SessionStatus.Ready
    FaultReason = None
    WorkerPid = Some 1
    WorkerPort = Some 5555
    Workflow = workflow
    ActiveProject = None
    ProjectRoles = projects
    App = app }

let private webLive = WorkflowTypes.SessionWorkflow.WebLive WorkflowTypes.BrowserRefreshConfig.defaults
let private interactive = WorkflowTypes.SessionWorkflow.Interactive

type private Recorded = {
  States: ConcurrentQueue<AppRunState>
  Calls: ConcurrentQueue<string>
  Ended: TaskCompletionSource<string * AppRunState>
}

let private record () =
  { States = ConcurrentQueue(); Calls = ConcurrentQueue(); Ended = TaskCompletionSource<string * AppRunState>() }

let private never = TaskCompletionSource<WorkerResponse>()

/// A worker that starts apps successfully and never reports a later change
/// unless the test supplies one.
let private worker (appChange: Task<WorkerResponse>) (msg: WorkerMessage) : Async<WorkerResponse> =
  match msg with
  | WorkerMessage.RunApp (_, _, rid) -> async { return WorkerResponse.AppRunResult (rid, Ok (AppRunState.Running (running "run1"))) }
  | WorkerMessage.StopApp rid -> async { return WorkerResponse.AppRunResult (rid, Ok AppRunState.NotRunning) }
  | WorkerMessage.AwaitAppChange _ -> Async.AwaitTask appChange
  | other -> failwithf "unexpected worker message %A" other

let private messageName (msg: WorkerMessage) =
  match msg with
  | WorkerMessage.RunApp (project, PreviousAddress.NoPreviousAddress, _) -> sprintf "worker:run %s" project
  | WorkerMessage.RunApp (project, PreviousAddress.ReuseAddress url, _) -> sprintf "worker:run %s at %s" project url
  | WorkerMessage.StopApp _ -> "worker:stop"
  | WorkerMessage.AwaitAppChange (runId, _) -> sprintf "worker:await %s" runId
  | other -> sprintf "worker:%A" other

let private fakeOps (info: SessionInfo) (handle: WorkerMessage -> Async<WorkerResponse>) (r: Recorded) : SessionManagementOps =
  { SessionManagementOps.stub with
      GetSessionInfo = fun _ -> Task.FromResult(Some info)
      GetProxy = fun _ ->
        let proxy (msg: WorkerMessage) = async {
          r.Calls.Enqueue(messageName msg)
          return! handle msg }
        Task.FromResult(Some proxy)
      SwitchWorkflow = fun _ _ ->
        r.Calls.Enqueue "switch-to-weblive"
        Task.FromResult(Ok "restarting")
      AwaitReady = fun _ _ ->
        r.Calls.Enqueue "await-ready"
        Task.FromResult(Ok ())
      SetAppState = fun _ state ->
        r.States.Enqueue state
        Task.FromResult(())
      EndAppRun = fun _ runId state ->
        r.Ended.TrySetResult((runId, state)) |> ignore
        Task.FromResult(())
      UpdateActiveProject = fun _ _ -> Task.FromResult(()) }

let private calls (r: Recorded) = r.Calls.ToArray() |> Array.toList
let private states (r: Recorded) = r.States.ToArray() |> Array.toList

[<Tests>]
let runAppTests =
  testList "AppRunOrchestration runApp" [
    testTask "WHY — AppRunOrchestration.runApp — a WebLive session launches without restarting because hot reload is already installed" {
      let r = record ()
      let ops = fakeOps (session webLive [ exe web ] AppRunState.NotRunning) (worker never.Task) r
      let! result = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      result |> Expect.equal "the worker's running app" (Ok (AppRunState.Running (running "run1")))
      states r
      |> Expect.equal "launching, then running"
        [ AppRunState.Starting (web, StartPhase.LaunchingEntryPoint, at); AppRunState.Running (running "run1") ]
      calls r |> List.contains "switch-to-weblive" |> Expect.isFalse "no restart"
    }

    testTask "WHY — AppRunOrchestration.runApp — an Interactive session restarts into WebLive first because hot reload installs only at worker start" {
      let r = record ()
      let ops = fakeOps (session interactive [ exe web ] AppRunState.NotRunning) (worker never.Task) r
      let! _ = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      calls r
      |> List.take 3
      |> Expect.equal "restart, wait for Ready, then run" [ "switch-to-weblive"; "await-ready"; sprintf "worker:run %s" web ]
      states r
      |> Expect.equal "each step is visible"
        [ AppRunState.Starting (web, StartPhase.RestartingIntoWebLive, at)
          AppRunState.Starting (web, StartPhase.LaunchingEntryPoint, at)
          AppRunState.Running (running "run1") ]
    }

    testTask "WHY — AppRunOrchestration.runApp — an ambiguous target fails before touching the worker because guessing would run the wrong app" {
      let r = record ()
      let ops = fakeOps (session webLive [ exe web; exe api ] AppRunState.NotRunning) (worker never.Task) r
      let! result = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      match result with
      | Error (SageFsError.AppRunFailed (_, reason)) -> reason |> Expect.stringContains "tells the user what to do" "→"
      | other -> failtestf "expected AppRunFailed, got %A" other
      calls r |> Expect.isEmpty "the worker was never asked"
      states r |> Expect.isEmpty "no state was claimed"
    }

    testTask "WHY — AppRunOrchestration.runApp — a worker that cannot start the app leaves Crashed with its reason because the card must say why" {
      let r = record ()
      let refusing (msg: WorkerMessage) =
        match msg with
        | WorkerMessage.RunApp (project, _, rid) ->
          async { return WorkerResponse.AppRunResult (rid, Error (SageFsError.AppRunFailed (project, "Web has no entry point"))) }
        | other -> worker never.Task other
      let ops = fakeOps (session webLive [ exe web ] AppRunState.NotRunning) refusing r
      let! result = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      result |> Result.isError |> Expect.isTrue "reported as an error"
      match states r |> List.last with
      | AppRunState.Crashed (project, reason, _) ->
        project |> Expect.equal "the project" web
        reason |> Expect.stringContains "the worker's reason" "no entry point"
      | other -> failtestf "expected Crashed last, got %A" other
    }

    testTask "WHY — AppRunOrchestration.runApp — a failed restart into WebLive is Crashed, not left Starting, because a stuck spinner lies" {
      let r = record ()
      let ops =
        { fakeOps (session interactive [ exe web ] AppRunState.NotRunning) (worker never.Task) r with
            AwaitReady = fun _ _ -> Task.FromResult(Error (SageFsError.WorkerSpawnFailed "host exited")) }
      let! result = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      result |> Result.isError |> Expect.isTrue "reported as an error"
      match states r |> List.last with
      | AppRunState.Crashed (_, reason, _) -> reason |> Expect.stringContains "the restart failure" "host exited"
      | other -> failtestf "expected Crashed last, got %A" other
      calls r |> List.exists (fun c -> c.StartsWith("worker:run", StringComparison.Ordinal)) |> Expect.isFalse "never launched"
    }

    testTask "WHY — AppRunOrchestration.runApp — a running app is returned as-is because a second run would fight for the port" {
      let r = record ()
      let ops = fakeOps (session webLive [ exe web ] (AppRunState.Running (running "run0"))) (worker never.Task) r
      let! result = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      result |> Expect.equal "the running app" (Ok (AppRunState.Running (running "run0")))
      calls r |> Expect.isEmpty "the worker was never asked"
    }

    testTask "WHY — AppRunOrchestration.runApp — an app that exits later has its end recorded because a dead app must not show as running" {
      let r = record ()
      let change = TaskCompletionSource<WorkerResponse>()
      let ops = fakeOps (session webLive [ exe web ] AppRunState.NotRunning) (worker change.Task) r
      let! _ = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      change.SetResult(WorkerResponse.AppRunResult ("w", Ok (AppRunState.Exited (web, 1, at))))
      let! runId, final = r.Ended.Task
      runId |> Expect.equal "ends the run that was started" "run1"
      final |> Expect.equal "the exit the worker saw" (AppRunState.Exited (web, 1, at))
    }

    testTask "WHY — AppRunOrchestration.runApp — a session whose projects are not yet cached asks the worker because Ready can be seen before the ready poll reports them" {
      let r = record ()
      let reporting (msg: WorkerMessage) =
        match msg with
        | WorkerMessage.GetStatus rid ->
          async {
            return
              WorkerResponse.StatusResult(
                rid,
                { Status = SessionStatus.Ready; StatusMessage = None; EvalCount = 0
                  AvgDurationMs = 0L; MinDurationMs = 0L; MaxDurationMs = 0L; Projects = [ exe web ] })
          }
        | other -> worker never.Task other
      let uncached = { session webLive [ exe web ] AppRunState.NotRunning with ProjectRoles = [] }
      let ops = fakeOps uncached reporting r
      let! result = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      result |> Expect.equal "the worker's projects were used" (Ok (AppRunState.Running (running "run1")))
    }
  ]

[<Tests>]
let stopAppTests =
  testList "AppRunOrchestration stopApp" [
    testTask "WHY — AppRunOrchestration.stopApp — asks the worker and records NotRunning because the session must survive stop" {
      let r = record ()
      let ops = fakeOps (session webLive [ exe web ] (AppRunState.Running (running "run1"))) (worker never.Task) r
      let! result = AppRunOrchestration.stopApp ops sid
      result |> Expect.equal "stopped" (Ok AppRunState.NotRunning)
      calls r |> Expect.equal "one stop request" [ "worker:stop" ]
      states r |> Expect.equal "recorded" [ AppRunState.NotRunning ]
    }
  ]

[<Tests>]
let endAppRunTests =
  testList "AppRun applyEnd" [
    testCase "WHY — AppRun.applyEnd — ends only the run it was issued for because a stale long poll must not clobber a newer run" <| fun _ ->
      applyEnd (AppRunState.Running (running "run2")) "run1" (AppRunState.Exited (web, 0, at))
      |> Expect.equal "newer run untouched" (AppRunState.Running (running "run2"))

    testCase "WHY — AppRun.applyEnd — records the end of the current run because the app really stopped" <| fun _ ->
      applyEnd (AppRunState.Running (running "run1")) "run1" (AppRunState.Crashed (web, "boom", at))
      |> Expect.equal "ended" (AppRunState.Crashed (web, "boom", at))

    testCase "WHY — AppRun.applyEnd — ignores an end after the user already stopped the app because stop is final" <| fun _ ->
      applyEnd AppRunState.NotRunning "run1" (AppRunState.Exited (web, 0, at))
      |> Expect.equal "still stopped" AppRunState.NotRunning
  ]

[<Tests>]
let restartForChangesTests =
  let typeChange = SageFs.Features.ReloadPlanning.ReloadChange.TypeChanged "TodoItem"
  /// A worker whose first run ends RestartRequired and whose relaunch is run2.
  let restarting (msg: WorkerMessage) : Async<WorkerResponse> =
    match msg with
    | WorkerMessage.RunApp (_, PreviousAddress.NoPreviousAddress, rid) ->
      async { return WorkerResponse.AppRunResult (rid, Ok (AppRunState.Running (running "run1"))) }
    | WorkerMessage.RunApp (_, PreviousAddress.ReuseAddress _, rid) ->
      async { return WorkerResponse.AppRunResult (rid, Ok (AppRunState.Running (running "run2"))) }
    | WorkerMessage.AwaitAppChange ("run1", rid) ->
      async { return WorkerResponse.AppRunResult (rid, Ok (AppRunState.RestartRequired (web, typeChange, [], at))) }
    | other -> worker never.Task other
  let settleOn (pick: AppRunState -> bool) (r: Recorded) (ops: SessionManagementOps) =
    let settled = TaskCompletionSource<AppRunState>()
    { ops with
        SetAppState = fun _ state ->
          r.States.Enqueue state
          match pick state with
          | true -> settled.TrySetResult state |> ignore
          | false -> ()
          Task.FromResult(()) },
    settled
  let within (t: Task<AppRunState>) =
    task {
      let! first = Task.WhenAny(t :> Task, Task.Delay(TimeSpan.FromSeconds 10.))
      (first = (t :> Task)) |> Expect.isTrue "the restart reaches its final state instead of hanging"
      return t.Result
    }
  testList "AppRunOrchestration restart for changes" [
    testTask "WHY — AppRunOrchestration — a run that ends RestartRequired is rebuilt and relaunched at the same address because the user's open tab must keep working" {
      let r = record ()
      let baseOps =
        { fakeOps (session webLive [ exe web ] AppRunState.NotRunning) restarting r with
            RestartSession = fun _ rebuild ->
              r.Calls.Enqueue (sprintf "restart rebuild=%b" rebuild)
              Task.FromResult(Ok "restarted") }
      let ops, relaunched =
        settleOn (function AppRunState.Running app -> app.RunId = "run2" | _ -> false) r baseOps
      let! _ = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      let! _ = within relaunched.Task
      calls r
      |> List.filter (fun c -> not (c.StartsWith("worker:await", StringComparison.Ordinal)))
      |> Expect.equal "run, rebuild, wait for Ready, relaunch at the old address"
        [ sprintf "worker:run %s" web; "restart rebuild=true"; "await-ready"; sprintf "worker:run %s at http://127.0.0.1:5123" web ]
      states r
      |> List.exists (function
        | AppRunState.Starting (_, StartPhase.RebuildingForChanges (c, []), _) -> c = typeChange
        | _ -> false)
      |> Expect.isTrue "the card said it was rebuilding for the change"
    }

    testTask "WHY — AppRunOrchestration — a failed rebuild ends Crashed with the build error because the card must say why the app did not come back" {
      let r = record ()
      let baseOps =
        { fakeOps (session webLive [ exe web ] AppRunState.NotRunning) restarting r with
            RestartSession = fun _ _ -> Task.FromResult(Error (SageFsError.HardResetFailed "error FS0001: build broke")) }
      let ops, crashed = settleOn (function AppRunState.Crashed _ -> true | _ -> false) r baseOps
      let! _ = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      match! within crashed.Task with
      | AppRunState.Crashed (project, reason, _) ->
        project |> Expect.equal "the project" web
        reason |> Expect.stringContains "the build error" "build broke"
      | other -> failtestf "expected Crashed, got %A" other
    }
  ]
