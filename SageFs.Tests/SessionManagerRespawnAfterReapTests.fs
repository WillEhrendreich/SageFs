module SageFs.Tests.SessionManagerRespawnAfterReapTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

module OwnedChild = SageFs.Tests.SessionManagerRestartTombstoneTests.OwnedChild

// #179: a hard reset of a session whose startup profile (`.SageFs/init.fsx`) runs at load must not start the
// replacement worker while the old one still holds the app's port. The fake worker's stop waits on a gate the test
// opens, so "the old worker has not left yet" is a state the test holds, not a race it hopes to win.

type private Verb =
  | Start
  | Stop
  | Build

type private Rig = {
  Runtime: SessionManagerRuntime
  Verbs: unit -> Verb list
  /// Completes when the Nth StartWorkerProcess call happens (1 is the session's first worker).
  Started: int -> Task
  StartCalls: unit -> int
  /// Opens the gate every worker's stop is waiting on.
  LetOldWorkersLeave: unit -> unit
  Owned: ResizeArray<OwnedChild.T>
}

/// `killOnStop` false models an old worker that outlives its stop and its kill.
let private mkRig (killOnStop: bool) (spawnFails: int -> bool) : Rig =
  let verbs = ResizeArray<Verb>()
  let owned = ResizeArray<OwnedChild.T>()
  let signals = ConcurrentDictionary<int, TaskCompletionSource>()
  let signal (n: int) = signals.GetOrAdd(n, fun _ -> TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
  let gate = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
  let mutable startCalls = 0
  let record (verb: Verb) = lock verbs (fun () -> verbs.Add verb)
  let runtime : SessionManagerRuntime =
    { StartWorkerProcess =
        fun _ _ _ _ _ _ ->
          let call = Interlocked.Increment &startCalls
          record Verb.Start
          let result =
            match spawnFails call with
            | true -> Error (SageFsError.WorkerSpawnFailed "spawn boom")
            | false ->
              let child = OwnedChild.start ()
              lock owned (fun () -> owned.Add child)
              Ok ({ Process = child.Process; AdoptedCore = None } : SessionManager.SpawnedWorker)
          (signal call).TrySetResult() |> ignore
          result
      AwaitWorkerPort = fun _ _ _ _ _ -> ()
      Ledger = StartLedger.closed
      StopWorker =
        fun session ->
          async {
            record Verb.Stop
            do! gate.Task |> Async.AwaitTask
            match killOnStop with
            | true ->
              try session.Process.Kill(true) with _ -> ()
              try session.Process.WaitForExit(TestTimeouts.asMs TestTimeouts.childExit) |> ignore with _ -> ()
            | false -> ()
          }
      RunBuildAsync =
        fun _ _ -> async {
          record Verb.Build
          return Ok "build ok"
        } }
  { Runtime = runtime
    Verbs = fun () -> lock verbs (fun () -> List.ofSeq verbs)
    Started = fun n -> (signal n).Task
    StartCalls = fun () -> Volatile.Read &startCalls
    LetOldWorkersLeave = fun () -> gate.TrySetResult() |> ignore
    Owned = owned }

type private Harness = {
  Mailbox: MailboxProcessor<SessionCommand>
  FaultedEvents: ResizeArray<SessionId * string>
}

let private withHarness (rig: Rig) (run: Harness -> Task) : Task =
  task {
    use cancellation = new CancellationTokenSource()
    let faulted = ResizeArray<SessionId * string>()
    let mailbox, _ =
      createWith rig.Runtime cancellation.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ())
        (fun sid msg -> lock faulted (fun () -> faulted.Add(sid, msg))) (fun _ _ _ -> ())
    try
      do! run { Mailbox = mailbox; FaultedEvents = faulted }
    finally
      rig.LetOldWorkersLeave ()
      (try mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply) with _ -> ())
      cancellation.Cancel()
      for child in rig.Owned do child.Reap()
  }

/// A scratch project folder, with a startup profile when asked, removed afterwards.
let private withProject (withProfile: bool) (run: string -> Task) : Task =
  task {
    let root = Path.Combine(Path.GetTempPath(), "respawn-mgr-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, ".SageFs")) |> ignore
    match withProfile with
    | true -> File.WriteAllText(Path.Combine(root, ".SageFs", "init.fsx"), "let app = 1")
    | false -> ()
    try do! run root
    finally try Directory.Delete(root, true) with _ -> ()
  }

let private createSession (harness: Harness) (root: string) : SessionInfo =
  match harness.Mailbox.PostAndReply(fun reply ->
    SessionCommand.CreateSession([ SessionProjectTarget.Project (Path.Combine(root, "App.fsproj")) ], root, true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

/// A solution under a scratch folder, two projects deep. `profileIn` names the project folder that holds the startup
/// profile, or none. The working directory is the folder the solution is in, which has no profile of its own.
let private withSolution (profileIn: string option) (run: string -> Task) : Task =
  task {
    let root = Path.Combine(Path.GetTempPath(), "respawn-sln-" + Guid.NewGuid().ToString "N")
    for project in [ "First"; "Second" ] do
      Directory.CreateDirectory(Path.Combine(root, "src", project, ".SageFs")) |> ignore
    match profileIn with
    | Some project -> File.WriteAllText(Path.Combine(root, "src", project, ".SageFs", "init.fsx"), "let app = 1")
    | None -> ()
    try do! run root
    finally try Directory.Delete(root, true) with _ -> ()
  }

let private createSolutionSession (harness: Harness) (root: string) : SessionInfo =
  match harness.Mailbox.PostAndReply(fun reply ->
    SessionCommand.CreateSession([ SessionProjectTarget.Solution (Path.Combine(root, "All.slnx")) ], root, true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

let private managed (harness: Harness) (id: SessionId) : ManagedSession =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(id, reply)) with
  | Some session -> session
  | None -> failtestf "expected session %s to exist" (SessionId.value id)

let private readyProxy : SessionProxy =
  fun (msg: WorkerMessage) ->
    async {
      match msg with
      | WorkerMessage.GetStatus rid ->
        let snap : WorkerStatusSnapshot = {
          Status = SessionStatus.Ready
          StatusMessage = None
          EvalCount = 0
          AvgDurationMs = FixtureDurations.unmeasuredMs
          MinDurationMs = FixtureDurations.unmeasuredMs
          MaxDurationMs = FixtureDurations.unmeasuredMs; Projects = []; CoreVersion = "0.0.0-test"
        }
        return WorkerResponse.StatusResult(rid, snap)
      | WorkerMessage.GetTestDiscovery _ -> return WorkerResponse.InitialTestDiscovery([||], [])
      | _ -> return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "unexpected message")
    }

let private makeReady (harness: Harness) (info: SessionInfo) : unit =
  let pid = SessionLifecycleStatus.workerPid (managed harness info.Id).Info.Status |> Option.defaultWith (fun () -> failtest "expected worker pid")
  harness.Mailbox.Post(SessionCommand.WorkerReady(info.Id, pid, "http://localhost:4123", readyProxy))
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply)) |> ignore
  harness.Mailbox.Post(SessionCommand.UpdateSessionStatus(info.Id, SessionLifecycleStatus.Ready { Pid = pid; Port = Some 4123 }))
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply)) |> ignore

/// Ready the way a real worker gets there: its transport, then its own report of the projects it loaded, in order.
let private makeReadyReporting (harness: Harness) (info: SessionInfo) (loaded: string list) : unit =
  let pid = SessionLifecycleStatus.workerPid (managed harness info.Id).Info.Status |> Option.defaultWith (fun () -> failtest "expected worker pid")
  let roles : ProjectLoading.ClassifiedProject list =
    loaded
    |> List.map (fun path ->
      { Path = path
        Role = ProjectLoading.ProjectRole.Library
        PackageRefs = []
        LoadMode = ProjectLoading.LoadMode.Evaluated
        Build = BuildOptimization.Unknown "a fixture" })
  harness.Mailbox.Post(SessionCommand.WorkerReady(info.Id, pid, "http://localhost:4123", readyProxy))
  harness.Mailbox.Post(SessionCommand.WorkerReportedReady(info.Id, pid, roles))
  match (managed harness info.Id).ProjectRoles |> List.length = roles.Length with
  | true -> ()
  | false -> failtest "the worker's project list was not recorded on the session"

let private switchTo (harness: Harness) (id: SessionId) (workflow: WorkflowTypes.SessionWorkflow) : Result<string, SageFsError> =
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.SwitchWorkflow(id, workflow, reply))

let private hardReset (harness: Harness) (id: SessionId) (plan: RestartPlan) : Result<string, SageFsError> =
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(id, plan, reply))

let private isRestarting = function SessionLifecycleStatus.Restarting _ -> true | _ -> false
let private isFaulted = function SessionLifecycleStatus.Faulted _ -> true | _ -> false

/// The workers a stopped session left behind are all seen gone: the mailbox has handled every WorkerReaped by then.
let private workersGone (harness: Harness) (id: SessionId) : Task<WorkerReaper.Settled> =
  harness.Mailbox.PostAndAsyncReply(fun reply -> SessionCommand.AwaitWorkersGone(id, reply)) |> Async.StartAsTask

let private within (what: string) (waited: Task) : Task =
  task {
    try do! waited.WaitAsync(TestTimeouts.childExit)
    with :? TimeoutException -> failtestf "timed out waiting for %s" what
  }

[<Tests>]
let respawnAfterReapTests =
  testList "SessionManager respawn after reap (#179)" [

    testTask "WHY — a session with a startup profile does not start its replacement until the old worker has left" {
      let rig = mkRig true (fun _ -> false)
      do! withProject true (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSession harness root
            makeReady harness info
            match hardReset harness info.Id RestartPlan.RespawnOnly with
            | Ok _ -> ()
            | Error err -> failtestf "the reset was refused: %s" (SageFsError.describe err)
            rig.StartCalls () |> Expect.equal "the replacement is held back while the old worker is still leaving" 1
            (managed harness info.Id).Info.Status |> isRestarting |> Expect.isTrue "the session stays registered, Restarting"
            rig.LetOldWorkersLeave ()
            do! within "the replacement to start once the old worker left" (rig.Started 2)
            rig.Verbs () |> Expect.equal "the old worker is stopped, and only then is the replacement started" [ Verb.Start; Verb.Stop; Verb.Start ]
          }))
    }

    testTask "WHY — a session with no startup profile still spawns first, so its old worker serves until the new one is Ready" {
      let rig = mkRig true (fun _ -> false)
      do! withProject false (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSession harness root
            makeReady harness info
            match hardReset harness info.Id RestartPlan.RespawnOnly with
            | Ok _ -> ()
            | Error err -> failtestf "the reset was refused: %s" (SageFsError.describe err)
            rig.StartCalls () |> Expect.equal "the replacement starts at once" 2
            rig.Verbs () |> Expect.equal "and the old worker is not stopped" [ Verb.Start; Verb.Start ]
            return ()
          }))
    }

    testTask "WHY — a rebuilding reset of a session with a profile builds first, then waits for the old worker before it spawns" {
      let rig = mkRig true (fun _ -> false)
      do! withProject true (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSession harness root
            makeReady harness info
            match hardReset harness info.Id (RestartPlan.Rebuild GranularRestart.RestartSubject.Worker) with
            | Ok _ -> ()
            | Error err -> failtestf "the reset was refused: %s" (SageFsError.describe err)
            rig.StartCalls () |> Expect.equal "the replacement is held back after the build too" 1
            rig.LetOldWorkersLeave ()
            do! within "the replacement to start once the old worker left" (rig.Started 2)
            rig.Verbs () |> Expect.equal "build, then stop, then start" [ Verb.Start; Verb.Build; Verb.Stop; Verb.Start ]
          }))
    }

    testTask "WHY — a second hard reset while the replacement is parked is refused, so one reset cannot start two workers" {
      let rig = mkRig true (fun _ -> false)
      do! withProject true (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSession harness root
            makeReady harness info
            hardReset harness info.Id RestartPlan.RespawnOnly |> Result.isOk |> Expect.isTrue "the first reset is accepted"
            match hardReset harness info.Id RestartPlan.RespawnOnly with
            | Error err -> RestartRefusal.isAlreadyInProgress err |> Expect.isTrue "the second is refused as already in progress"
            | Ok message -> failtestf "the second reset was accepted: %s" message
            rig.LetOldWorkersLeave ()
            do! within "the one replacement" (rig.Started 2)
            let! _ = workersGone harness info.Id
            rig.StartCalls () |> Expect.equal "exactly one replacement was started" 2
          }))
    }

    testTask "WHY — stopping the session while its replacement is parked means nothing is started for it afterwards" {
      let rig = mkRig true (fun _ -> false)
      do! withProject true (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSession harness root
            makeReady harness info
            hardReset harness info.Id RestartPlan.RespawnOnly |> Result.isOk |> Expect.isTrue "accepted"
            match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.StopSession(info.Id, reply)) with
            | Ok () -> ()
            | Error err -> failtestf "stop failed: %s" (SageFsError.describe err)
            rig.LetOldWorkersLeave ()
            let! _ = workersGone harness info.Id
            rig.StartCalls () |> Expect.equal "no worker was started for a stopped session" 1
          }))
    }

    testTask "WHY — a replacement that fails to start after the wait faults the session and says why" {
      let rig = mkRig true (fun call -> call = 2)
      do! withProject true (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSession harness root
            makeReady harness info
            hardReset harness info.Id RestartPlan.RespawnOnly |> Result.isOk |> Expect.isTrue "accepted"
            rig.LetOldWorkersLeave ()
            do! within "the failed start" (rig.Started 2)
            let! _ = workersGone harness info.Id
            (managed harness info.Id).Info.Status |> isFaulted |> Expect.isTrue "the session is Faulted, not stuck Restarting"
            lock harness.FaultedEvents (fun () -> harness.FaultedEvents |> Seq.map snd |> List.ofSeq)
            |> List.exists (fun reason -> reason.Contains "spawn boom")
            |> Expect.isTrue "the fault callback carries the spawn failure"
          }))
    }

    testTask "WHY — a solution session whose profile is only in the first project's folder waits for the old worker, as the worker's own lookup would find it" {
      let rig = mkRig true (fun _ -> false)
      do! withSolution (Some "First") (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSolutionSession harness root
            makeReadyReporting harness info [ Path.Combine(root, "src", "First", "First.fsproj"); Path.Combine(root, "src", "Second", "Second.fsproj") ]
            hardReset harness info.Id RestartPlan.RespawnOnly |> Result.isOk |> Expect.isTrue "accepted"
            rig.StartCalls () |> Expect.equal "the replacement is held back" 1
            rig.LetOldWorkersLeave ()
            do! within "the replacement to start once the old worker left" (rig.Started 2)
            rig.Verbs () |> Expect.equal "the old worker is stopped, and only then is the replacement started" [ Verb.Start; Verb.Stop; Verb.Start ]
          }))
    }

    testTask "WHY — a solution session with no profile anywhere keeps spawn-first" {
      let rig = mkRig true (fun _ -> false)
      do! withSolution None (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSolutionSession harness root
            makeReadyReporting harness info [ Path.Combine(root, "src", "First", "First.fsproj"); Path.Combine(root, "src", "Second", "Second.fsproj") ]
            hardReset harness info.Id RestartPlan.RespawnOnly |> Result.isOk |> Expect.isTrue "accepted"
            rig.StartCalls () |> Expect.equal "the replacement starts at once" 2
            rig.Verbs () |> Expect.equal "and the old worker is not stopped" [ Verb.Start; Verb.Start ]
          }))
    }

    testTask "WHY — a solution session whose profile is only in a later project's folder keeps spawn-first, since the worker never loads it" {
      let rig = mkRig true (fun _ -> false)
      do! withSolution (Some "Second") (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSolutionSession harness root
            makeReadyReporting harness info [ Path.Combine(root, "src", "First", "First.fsproj"); Path.Combine(root, "src", "Second", "Second.fsproj") ]
            hardReset harness info.Id RestartPlan.RespawnOnly |> Result.isOk |> Expect.isTrue "accepted"
            rig.StartCalls () |> Expect.equal "the replacement starts at once" 2
          }))
    }

    testTask "WHY — switch_workflow on a session with a startup profile does not start the replacement until the old worker is reaped, then starts it in the new workflow" {
      let rig = mkRig true (fun _ -> false)
      do! withProject true (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSession harness root
            makeReady harness info
            match switchTo harness info.Id WorkflowTypes.SessionWorkflow.LiveTesting with
            | Ok _ -> ()
            | Error err -> failtestf "the switch was refused: %s" (SageFsError.describe err)
            rig.StartCalls () |> Expect.equal "the replacement is held back while the old worker is still leaving" 1
            (managed harness info.Id).Info.Status |> isRestarting |> Expect.isTrue "the session stays registered, Restarting"
            rig.LetOldWorkersLeave ()
            do! within "the replacement to start once the old worker left" (rig.Started 2)
            rig.Verbs () |> Expect.equal "the old worker is stopped, and only then is the replacement started" [ Verb.Start; Verb.Stop; Verb.Start ]
            let! _ = workersGone harness info.Id
            (managed harness info.Id).Workflow |> Expect.equal "the replacement is in the workflow that was asked for" WorkflowTypes.SessionWorkflow.LiveTesting
          }))
    }

    testTask "WHY — switch_workflow on a session with no startup profile starts the replacement at once and leaves the old worker serving" {
      let rig = mkRig true (fun _ -> false)
      do! withProject false (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSession harness root
            makeReady harness info
            match switchTo harness info.Id WorkflowTypes.SessionWorkflow.LiveTesting with
            | Ok _ -> ()
            | Error err -> failtestf "the switch was refused: %s" (SageFsError.describe err)
            rig.StartCalls () |> Expect.equal "the replacement starts at once" 2
            rig.Verbs () |> Expect.equal "and the old worker is not stopped" [ Verb.Start; Verb.Start ]
          }))
    }

    testTask "WHY — an old worker that outlives its kill does not leave the session Restarting forever" {
      let rig = mkRig false (fun _ -> false)
      do! withProject true (fun root ->
        withHarness rig (fun harness ->
          task {
            let info = createSession harness root
            makeReady harness info
            hardReset harness info.Id RestartPlan.RespawnOnly |> Result.isOk |> Expect.isTrue "accepted"
            rig.LetOldWorkersLeave ()
            do! within "the replacement to start despite the survivor" (rig.Started 2)
            rig.Verbs () |> Expect.equal "the replacement starts after the stop ran" [ Verb.Start; Verb.Stop; Verb.Start ]
          }))
    }
  ]
