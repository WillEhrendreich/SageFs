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
    Status = SessionLifecycleStatus.Ready { Pid = 1; Port = Some 5555 }
    Workflow = workflow
    ActiveProject = None
    ProjectRoles = projects
    App = app }

let private webLive = WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults
let private interactive = WorkflowTypes.SessionWorkflow.Interactive

type private Recorded = {
  States: ConcurrentQueue<AppRunState>
  Calls: ConcurrentQueue<string>
  Ended: TaskCompletionSource<string * AppRunState>
  /// Set when the owner rejects a step whose run no longer owns the app.
  Superseded: TaskCompletionSource<AppRunState>
  Watchers: ConcurrentQueue<(AppRunState -> bool) * TaskCompletionSource<AppRunState>>
}

let private record () =
  { States = ConcurrentQueue()
    Calls = ConcurrentQueue()
    Ended = TaskCompletionSource<string * AppRunState>()
    Superseded = TaskCompletionSource<AppRunState>()
    Watchers = ConcurrentQueue() }

/// Completes with the first recorded state `pick` accepts.
let private settleOn (pick: AppRunState -> bool) (r: Recorded) =
  let settled = TaskCompletionSource<AppRunState>()
  r.Watchers.Enqueue((pick, settled))
  settled

let private never = TaskCompletionSource<WorkerResponse>()

/// A worker that starts apps successfully and never reports a later change
/// unless the test supplies one.
let private worker (appChange: Task<WorkerResponse>) (msg: WorkerMessage) : Async<WorkerResponse> =
  match msg with
  | WorkerMessage.RunApp (_, _, rid) -> async { return WorkerResponse.AppRunResult (rid, Ok (AppRunState.Running (running "run1"))) }
  | WorkerMessage.StopApp (_, rid) -> async { return WorkerResponse.AppRunResult (rid, Ok AppRunState.NotRunning) }
  | WorkerMessage.AwaitAppChange _ -> Async.AwaitTask appChange
  | other -> failwithf "unexpected worker message %A" other

let private messageName (msg: WorkerMessage) =
  match msg with
  | WorkerMessage.RunApp (project, PreviousAddress.NoPreviousAddress, _) -> sprintf "worker:run %s" project
  | WorkerMessage.RunApp (project, PreviousAddress.ReuseAddress url, _) -> sprintf "worker:run %s at %s" project url
  | WorkerMessage.StopApp _ -> "worker:stop"
  | WorkerMessage.AwaitAppChange (runId, _) -> sprintf "worker:await %s" runId
  | other -> sprintf "worker:%A" other

/// The session's owner as the SessionManager mailbox is: one lock-guarded
/// AppSlot, changed only through the same AppRun.AppSlot decisions. Records
/// each state the user would see (a step that changes nothing is not a new state).
let private fakeOps (info: SessionInfo) (handle: WorkerMessage -> Async<WorkerResponse>) (r: Recorded) : SessionManagementOps =
  let gate = obj ()
  let slot = ref { AppSlot.initial with State = info.App }
  let change (decide: AppSlot -> 'a * AppSlot) : 'a =
    lock gate (fun () ->
      let before = slot.Value.State
      let answer, next = decide slot.Value
      slot.Value <- next
      match next.State = before with
      | true -> ()
      | false ->
        r.States.Enqueue next.State
        for (pick, settled) in r.Watchers do
          match pick next.State with
          | true -> settled.TrySetResult next.State |> ignore
          | false -> ()
      answer)
  { SessionManagementOps.stub with
      GetSessionInfo = fun _ -> Task.FromResult(Some { info with App = lock gate (fun () -> slot.Value.State) })
      ClaimRun = fun _ project ->
        let phase = AppRunOrchestration.startPhaseFor info.Status info.Workflow
        Task.FromResult(Ok (change (AppSlot.claimRun project phase at)))
      ClaimStop = fun _ -> Task.FromResult(Ok (change AppSlot.claimStop))
      AdvanceRun = fun _ generation next ->
        let outcome = change (AppSlot.advance generation next)
        match outcome with
        | StepOutcome.Stale current -> r.Superseded.TrySetResult current |> ignore
        | StepOutcome.Applied -> ()
        Task.FromResult outcome
      EndAppRun = fun _ generation runId final ->
        let ended = change (AppSlot.endRun generation runId final)
        r.Ended.TrySetResult((runId, final)) |> ignore
        Task.FromResult ended
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
      GetAdoptedCore = fun _ -> Task.FromResult(None) }

let private calls (r: Recorded) = r.Calls.ToArray() |> Array.toList
let private states (r: Recorded) = r.States.ToArray() |> Array.toList

[<Tests>]
let runAppTests =
  testList "AppRunOrchestration runApp" [
    testTask "WHY — AppRunOrchestration.runApp — a HotReload session launches without restarting because hot reload is already installed" {
      let r = record ()
      let ops = fakeOps (session webLive [ exe web ] AppRunState.NotRunning) (worker never.Task) r
      let! result = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      result |> Expect.equal "the worker's running app" (Ok (AppRunState.Running (running "run1")))
      states r
      |> Expect.equal "launching, then running"
        [ AppRunState.Starting (web, StartPhase.LaunchingEntryPoint, at); AppRunState.Running (running "run1") ]
      calls r |> List.contains "switch-to-weblive" |> Expect.isFalse "no restart"
    }

    testTask "WHY — AppRunOrchestration.runApp — an Interactive session restarts into HotReload first because hot reload installs only at worker start" {
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

    testTask "WHY — AppRunOrchestration.runApp — a worker that refuses to start the app leaves CouldNotStart with its reason, not Crashed, because nothing ran and the card must say why (roast-4 #3)" {
      let r = record ()
      let refusing (msg: WorkerMessage) =
        match msg with
        | WorkerMessage.RunApp (project, _, rid) ->
          async { return WorkerResponse.AppRunResult (rid, Error (SageFsError.AppRunFailed (project, "Web has no entry point"))) }
        | other -> worker never.Task other
      let ops = fakeOps (session webLive [ exe web ] AppRunState.NotRunning) refusing r
      let! result = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      result |> Result.isError |> Expect.isTrue "reported as an error"
      // The worker refused to start the app — it never ran, so this is
      // CouldNotStart, not Crashed (roast-4 #3).
      let last = states r |> List.last
      let case, fields = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(last, typeof<AppRunState>)
      case.Name |> Expect.equal "refused at start is CouldNotStart" "CouldNotStart"
      fields |> Array.exists (fun f -> match f with :? string as s -> s = web | _ -> false)
      |> Expect.isTrue "names the project"
      SageFs.AppRun.describeState last
      |> Expect.stringContains "the worker's reason" "no entry point"
    }

    testTask "WHY — AppRunOrchestration.runApp — a failed restart into HotReload is CouldNotStart, not Crashed and not left Starting, because the app never ran: 'crashed' would tell the user their code died when it was the worker that never came up (roast-4 #3)" {
      let r = record ()
      let ops =
        { fakeOps (session interactive [ exe web ] AppRunState.NotRunning) (worker never.Task) r with
            AwaitReady = fun _ _ -> Task.FromResult(Error (SageFsError.WorkerSpawnFailed "host exited")) }
      let! result = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      result |> Result.isError |> Expect.isTrue "reported as an error"
      let last = states r |> List.last
      let case, fields = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(last, typeof<AppRunState>)
      case.Name |> Expect.equal "the app never started, so it could not start" "CouldNotStart"
      // CouldNotStart of project * reason: SageFsError * at — the structured
      // error survives, it is not flattened to prose.
      fields |> Array.exists (fun f -> f :? SageFsError)
      |> Expect.isTrue "carries the SageFsError that stopped the start"
      SageFs.AppRun.describeState last
      |> Expect.stringContains "says it could not start, and why" "host exited"
      (SageFs.AppRun.describeState last).Contains("crashed", StringComparison.OrdinalIgnoreCase)
      |> Expect.isFalse "never says crashed for something that never ran"
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
    ops, settleOn pick r
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

    testTask "WHY — AppRunOrchestration — a failed rebuild ends BuildFailed with the compiler output because the card must say why the app did not come back" {
      let r = record ()
      let baseOps =
        { fakeOps (session webLive [ exe web ] AppRunState.NotRunning) restarting r with
            RestartSession = fun _ _ -> Task.FromResult(Error (SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "error FS0001: build broke" ]))) }
      let ops, failed = settleOn (function AppRunState.BuildFailed _ -> true | _ -> false) r baseOps
      let! _ = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      match! within failed.Task with
      | AppRunState.BuildFailed (project, reason, _, previous) ->
        previous |> Expect.equal "remembers where the app listened" (PreviousAddress.ReuseAddress "http://127.0.0.1:5123")
        project |> Expect.equal "the project" web
        reason |> Expect.stringContains "the build error" "build broke"
      | other -> failtestf "expected BuildFailed, got %A" other
    }

    testTask "WHY — AppRunOrchestration — a restart that fails for a non-build reason ends CouldNotStart because only a failed build is BuildFailed and nothing ever ran to crash (roast-4 #3)" {
      let r = record ()
      let baseOps =
        { fakeOps (session webLive [ exe web ] AppRunState.NotRunning) restarting r with
            RestartSession = fun _ _ -> Task.FromResult(Error (SageFsError.WorkerSpawnFailed "host exited")) }
      // A restart that never brought a worker up is a start that could not
      // happen — CouldNotStart, not Crashed (roast-4 #3). Matched by case name
      // so this test compiles (and fails) before the case exists.
      let isCouldNotStart (s: AppRunState) =
        (fst (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(s, typeof<AppRunState>))).Name = "CouldNotStart"
      let ops, couldNotStart = settleOn isCouldNotStart r baseOps
      let! _ = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      let! settled = within couldNotStart.Task
      SageFs.AppRun.describeState settled
      |> Expect.stringContains "the restart failure, on a CouldNotStart state" "host exited"
    }
  ]

[<Tests>]
let faultedSessionRunTests =
  testList "AppRunOrchestration run on a faulted session" [
    testTask "WHY — AppRunOrchestration.runApp — Run on a session whose rebuild failed rebuilds it first because after fixing the code Run is the one button the user presses" {
      let r = record ()
      let faulted = { session webLive [ exe web ] AppRunState.NotRunning with Status = SessionLifecycleStatus.Faulted None }
      let ops =
        { fakeOps faulted (worker never.Task) r with
            RestartSession = fun _ rebuild ->
              r.Calls.Enqueue (sprintf "restart rebuild=%b" rebuild)
              Task.FromResult(Ok "restarted") }
      let! result = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      result |> Expect.equal "the relaunched app" (Ok (AppRunState.Running (running "run1")))
      calls r
      |> List.filter (fun c -> not (c.StartsWith("worker:await", StringComparison.Ordinal)))
      |> Expect.equal "rebuild, wait for Ready, then run" [ "restart rebuild=true"; "await-ready"; sprintf "worker:run %s" web ]
      states r |> List.head
      |> Expect.equal "the card says it is rebuilding" (AppRunState.Starting (web, StartPhase.RebuildingSession, at))
    }
  ]

[<Tests>]
let runAfterFailedRebuildTests =
  testList "AppRunOrchestration run after a failed rebuild" [
    testTask "WHY — AppRunOrchestration.runApp — Run after a failed rebuild relaunches at the old address because the user's open tab must keep working once the typo is fixed" {
      let r = record ()
      let failed =
        { session webLive [ exe web ] (AppRunState.BuildFailed (web, "Build failed (exit 1)", at, PreviousAddress.ReuseAddress "http://127.0.0.1:5123")) with
            Status = SessionLifecycleStatus.Faulted None }
      let ops =
        { fakeOps failed (worker never.Task) r with
            RestartSession = fun _ rebuild ->
              r.Calls.Enqueue (sprintf "restart rebuild=%b" rebuild)
              Task.FromResult(Ok "restarted") }
      let! _ = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      calls r
      |> List.filter (fun c -> not (c.StartsWith("worker:await", StringComparison.Ordinal)))
      |> Expect.equal "rebuild, wait for Ready, relaunch at the old address"
        [ "restart rebuild=true"; "await-ready"; sprintf "worker:run %s at http://127.0.0.1:5123" web ]
    }
  ]

[<Tests>]
let runOwnershipTests =
  let typeChange = SageFs.Features.ReloadPlanning.ReloadChange.TypeChanged "TodoItem"
  let within (label: string) (t: Task) =
    task {
      let! first = Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds 10.))
      (first = t) |> Expect.isTrue label
    }
  testList "AppRunOrchestration run ownership" [
    testTask "WHY — AppRunOrchestration — Stop pressed while the app rebuilds for changes stays stopped because a lost Stop brings back an app the user ended" {
      let r = record ()
      let rebuilding = TaskCompletionSource()
      let rebuildGate = TaskCompletionSource<Result<string, SageFsError>>()
      let relaunched = TaskCompletionSource()
      let restarting (msg: WorkerMessage) : Async<WorkerResponse> =
        match msg with
        | WorkerMessage.RunApp (_, PreviousAddress.NoPreviousAddress, rid) ->
          async { return WorkerResponse.AppRunResult (rid, Ok (AppRunState.Running (running "run1"))) }
        | WorkerMessage.RunApp (_, PreviousAddress.ReuseAddress _, rid) ->
          relaunched.TrySetResult() |> ignore
          async { return WorkerResponse.AppRunResult (rid, Ok (AppRunState.Running (running "run2"))) }
        | WorkerMessage.AwaitAppChange ("run1", rid) ->
          async { return WorkerResponse.AppRunResult (rid, Ok (AppRunState.RestartRequired (web, typeChange, [], at))) }
        | other -> worker never.Task other
      let ops =
        { fakeOps (session webLive [ exe web ] AppRunState.NotRunning) restarting r with
            RestartSession = fun _ _ ->
              rebuilding.TrySetResult() |> ignore
              rebuildGate.Task }
      let! _ = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      do! within "the run ends for changes and starts rebuilding" rebuilding.Task
      let! stopped = AppRunOrchestration.stopApp ops sid
      stopped |> Expect.equal "Stop answers Not running" (Ok AppRunState.NotRunning)
      rebuildGate.SetResult(Ok "restarted")
      do! within "the rebuilt run finds the app no longer its own" r.Superseded.Task
      relaunched.Task.IsCompleted |> Expect.isFalse "the app the user stopped is not relaunched"
      let! info = ops.GetSessionInfo sid
      info |> Option.map _.App |> Expect.equal "the card still says Not running" (Some AppRunState.NotRunning)
    }

    testTask "WHY — AppRunOrchestration — Stop pressed while the worker starts the app ends that app because nobody owns it once Stop has claimed the session" {
      let r = record ()
      let launching = TaskCompletionSource()
      let launchGate = TaskCompletionSource()
      let stoppedRuns = ConcurrentQueue<StopScope>()
      let slowStart (msg: WorkerMessage) : Async<WorkerResponse> =
        match msg with
        | WorkerMessage.RunApp (_, _, rid) ->
          launching.TrySetResult() |> ignore
          async {
            do! Async.AwaitTask launchGate.Task
            return WorkerResponse.AppRunResult (rid, Ok (AppRunState.Running (running "run1"))) }
        | WorkerMessage.StopApp (scope, rid) ->
          stoppedRuns.Enqueue scope
          async { return WorkerResponse.AppRunResult (rid, Ok AppRunState.NotRunning) }
        | other -> worker never.Task other
      let ops = fakeOps (session webLive [ exe web ] AppRunState.NotRunning) slowStart r
      let run = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      do! within "the worker is starting the app" launching.Task
      let! stopped = AppRunOrchestration.stopApp ops sid
      stopped |> Expect.equal "Stop answers Not running at once" (Ok AppRunState.NotRunning)
      launchGate.SetResult()
      let! ran = run
      ran |> Expect.equal "the superseded Run answers with the owner's state" (Ok AppRunState.NotRunning)
      stoppedRuns.ToArray()
      |> Array.contains (StopScope.OnlyRun "run1")
      |> Expect.isTrue "the app the worker started after Stop is ended, and only it"
      let! info = ops.GetSessionInfo sid
      info |> Option.map _.App |> Expect.equal "the card says Not running" (Some AppRunState.NotRunning)
    }

    testTask "WHY — AppRunOrchestration — two Run presses at once start one app because two starts fight for the same port" {
      let r = record ()
      let started = ref 0
      let gate = TaskCompletionSource()
      let counting (msg: WorkerMessage) : Async<WorkerResponse> =
        match msg with
        | WorkerMessage.RunApp (_, _, rid) ->
          let n = lock started (fun () -> started.Value <- started.Value + 1; started.Value)
          async {
            do! Async.AwaitTask gate.Task
            return WorkerResponse.AppRunResult (rid, Ok (AppRunState.Running (running (sprintf "run%d" n)))) }
        | other -> worker never.Task other
      // Both presses read the session before either write lands, as two HTTP
      // requests reading the published snapshot do.
      let reads = ref 0
      let bothRead = TaskCompletionSource()
      let ops =
        { fakeOps (session webLive [ exe web ] AppRunState.NotRunning) counting r with
            GetSessionInfo = fun _ ->
              let snapshot = Some (session webLive [ exe web ] AppRunState.NotRunning)
              match lock reads (fun () -> reads.Value <- reads.Value + 1; reads.Value) with
              | 2 -> bothRead.TrySetResult() |> ignore
              | _ -> ()
              task {
                do! bothRead.Task
                return snapshot } }
      let one = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      let two = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      gate.SetResult()
      let! results = Task.WhenAll [| one; two |]
      started.Value |> Expect.equal "the worker was asked to start the app once" 1
      results |> Array.filter Result.isOk |> Array.length |> Expect.equal "one Run started it" 1
    }
  ]

[<Tests>]
let lostWatchTests =
  let within (label: string) (t: Task<AppRunState>) =
    task {
      let! first = Task.WhenAny(t :> Task, Task.Delay(TimeSpan.FromSeconds 10.))
      (first = (t :> Task)) |> Expect.isTrue label
      return t.Result
    }
  testList "AppRunOrchestration lost watch" [
    testTask "WHY — AppRunOrchestration — a long poll that fails ends the run as lost track, naming why and what to do, because a card saying Running forever lies" {
      let r = record ()
      let unreachable (msg: WorkerMessage) : Async<WorkerResponse> =
        match msg with
        | WorkerMessage.AwaitAppChange _ -> async { return failwith "Connection refused (127.0.0.1:5555)" }
        | other -> worker never.Task other
      let ops = fakeOps (session webLive [ exe web ] AppRunState.NotRunning) unreachable r
      let settled = settleOn (function AppRunState.Running _ | AppRunState.Starting _ -> false | _ -> true) r
      let! _ = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      let! final = within "the run stops claiming to be Running" settled.Task
      (toView final).State |> Expect.equal "a distinct state: the app's status is unknown" "LostTrack"
      let message = describeState final
      message |> Expect.stringContains "says why" "Connection refused"
      message |> Expect.stringContains "says what to do" "→"
    }

    testTask "WHY — AppRunOrchestration — a report that throws while ending the run still ends it as lost track because an unobserved failure leaves the card stale" {
      let r = record ()
      let change = TaskCompletionSource<WorkerResponse>()
      let baseOps = fakeOps (session webLive [ exe web ] AppRunState.NotRunning) (worker change.Task) r
      let reports = ref 0
      let ops =
        { baseOps with
            EndAppRun = fun id generation runId final ->
              match System.Threading.Interlocked.Increment reports with
              | 1 -> raise (InvalidOperationException "the owner could not be reached")
              | _ -> baseOps.EndAppRun id generation runId final }
      let settled = settleOn (function AppRunState.Running _ | AppRunState.Starting _ -> false | _ -> true) r
      let! _ = AppRunOrchestration.runApp ops clock readyTimeout sid RunRequest.DefaultTarget
      change.SetResult(WorkerResponse.AppRunResult ("w", Ok (AppRunState.Exited (web, 0, at))))
      let! final = within "the run stops claiming to be Running" settled.Task
      (toView final).State |> Expect.equal "recorded as lost track" "LostTrack"
      describeState final |> Expect.stringContains "says why" "the owner could not be reached"
    }
  ]

[<Tests>]
let appSlotTests =
  let runningSlot (generation: int64) = { Generation = RunGeneration generation; State = AppRunState.Running (running "run1") }
  testList "AppRun AppSlot owner decisions" [
    testProperty "WHY — AppSlot.advance — a step from any generation but the owner's changes nothing because a later Run or Stop owns the app" <| fun (owner: int64) (step: int64) ->
      let slot = { Generation = RunGeneration owner; State = AppRunState.NotRunning }
      match owner = step with
      | true -> true
      | false -> AppSlot.advance (RunGeneration step) (AppRunState.Running (running "run1")) slot = (StepOutcome.Stale AppRunState.NotRunning, slot)

    testProperty "WHY — AppSlot.claimStop — every step of the run before a Stop is stale because the Stop is the user's last word" <| fun (generation: int64) ->
      let _, stopped = AppSlot.claimStop (runningSlot generation)
      match AppSlot.advance (RunGeneration generation) AppRunState.NotRunning stopped with
      | StepOutcome.Stale _, unchanged -> unchanged = stopped
      | StepOutcome.Applied, _ -> false

    testCase "WHY — AppSlot.claimRun — a Run while one is starting is refused because two starts fight for the same port" <| fun _ ->
      let first, starting = AppSlot.claimRun web StartPhase.LaunchingEntryPoint at AppSlot.initial
      match first with
      | RunClaim.Begun _ -> ()
      | other -> failtestf "expected the first Run to begin, got %A" other
      AppSlot.claimRun web StartPhase.LaunchingEntryPoint at starting
      |> fst
      |> Expect.equal "the second is told it is already starting" (RunClaim.AlreadyStarting (web, StartPhase.LaunchingEntryPoint))

    testCase "WHY — AppSlot.claimStop — Stop during a start cancels it at once because the card must not keep saying Starting" <| fun _ ->
      let _, starting = AppSlot.claimRun web StartPhase.RebuildingSession at AppSlot.initial
      let claim, stopped = AppSlot.claimStop starting
      match claim with
      | StopClaim.CancelledStart (_, StartPhase.RebuildingSession) -> ()
      | other -> failtestf "expected the start to be cancelled, got %A" other
      stopped.State |> Expect.equal "not running from now on" AppRunState.NotRunning

    testCase "WHY — AppSlot.endRun — a run that ends for changes moves straight to rebuilding because a Run pressed in between would launch on the worker being retired" <| fun _ ->
      let change = SageFs.Features.ReloadPlanning.ReloadChange.TypeChanged "TodoItem"
      let ended, rebuilding = AppSlot.endRun (RunGeneration 1L) "run1" (AppRunState.RestartRequired (web, change, [], at)) (runningSlot 1L)
      ended |> Expect.equal "the watcher rebuilds where the app listened" (RunEnd.RebuildForChanges (web, PreviousAddress.ReuseAddress "http://127.0.0.1:5123"))
      AppSlot.claimRun web StartPhase.LaunchingEntryPoint at rebuilding
      |> fst
      |> Expect.equal "a Run in that moment is told the app is rebuilding" (RunClaim.AlreadyStarting (web, StartPhase.RebuildingForChanges (change, [])))

    testCase "WHY — AppSlot.endRun — a run ended for changes after Stop claimed it is recorded, not rebuilt, because the user stopped it" <| fun _ ->
      let change = SageFs.Features.ReloadPlanning.ReloadChange.TypeChanged "TodoItem"
      let final = AppRunState.RestartRequired (web, change, [], at)
      AppSlot.endRun (RunGeneration 1L) "run1" final (runningSlot 2L)
      |> Expect.equal "recorded as it ended" (RunEnd.Recorded, { runningSlot 2L with State = final })
  ]

[<Tests>]
let ownerMailboxTests =
  testList "SessionManager app-run owner" [
    testTask "WHY — SessionManager — the mailbox drops a run step from before a Stop because the owner, not the caller, decides what the app is doing" {
      let cancellation = new System.Threading.CancellationTokenSource()
      let runtime : SessionManager.SessionManagerRuntime =
        { StartWorkerProcess = fun _ _ _ _ _ _ -> Ok ({ Process = System.Diagnostics.Process.GetCurrentProcess(); AdoptedCore = None } : SessionManager.SpawnedWorker)
          AwaitWorkerPort = fun _ _ _ _ -> ()
          StopWorker = fun _ -> async { return () }
          RunBuildAsync = fun _ _ -> async { return Ok "built" } }
      let mailbox, _ =
        SessionManager.createWith runtime cancellation.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ())
      let ask (build: AsyncReplyChannel<'r> -> SessionManager.SessionCommand) = mailbox.PostAndAsyncReply build |> Async.StartAsTask
      let! created = ask (fun reply -> SessionManager.SessionCommand.CreateSession ([ web ], "/src", true, webLive, reply))
      let id =
        match created with
        | Ok info -> info.Id
        | Error e -> failtestf "create failed: %s" (SageFsError.describe e)
      let! claim = ask (fun reply -> SessionManager.SessionCommand.ClaimRun (id, web, reply))
      let generation =
        match claim with
        | Ok (RunClaim.Begun (g, _, _)) -> g
        | other -> failtestf "expected the run to begin, got %A" other
      let! second = ask (fun reply -> SessionManager.SessionCommand.ClaimRun (id, web, reply))
      second |> Expect.equal "a second Run is refused by the owner" (Ok (RunClaim.AlreadyStarting (web, StartPhase.LaunchingEntryPoint)))
      let! _ = ask (fun reply -> SessionManager.SessionCommand.ClaimStop (id, reply))
      let! step = ask (fun reply -> SessionManager.SessionCommand.AdvanceRun (id, generation, AppRunState.Running (running "run1"), reply))
      step |> Expect.equal "the old run's step is stale" (StepOutcome.Stale AppRunState.NotRunning)
      let! (session: SessionManager.ManagedSession option) = ask (fun reply -> SessionManager.SessionCommand.GetSession (id, reply))
      session |> Option.map (fun s -> s.Info.App) |> Expect.equal "the session still says Not running" (Some AppRunState.NotRunning)
      do! ask (fun reply -> SessionManager.SessionCommand.StopAll reply)
      cancellation.Cancel()
      cancellation.Dispose()
    }
  ]
