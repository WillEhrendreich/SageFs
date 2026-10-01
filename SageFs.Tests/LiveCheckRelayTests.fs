module SageFs.Tests.LiveCheckRelayTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.LiveTesting
open SageFs.Tests.SharedGenerators

/// The shell around `LiveCheckPump`: what the session manager says becomes a view, a request that finds no worker
/// waits for one, and an answer counts only from the worker that was asked. The decision itself is proven by
/// `LiveCheckPumpSimTests`; these are the seams between it and the world, and the effect handler that uses it.

let private sessionId = testSessionId "a1b2c3d4"

let private infoWith (status: SessionLifecycleStatus) : SessionInfo =
  { Id = sessionId
    Name = None
    Projects = [ "Test.fsproj" ]
    WorkingDirectory = "."
    SolutionRoot = None
    CreatedAt = DateTime.UtcNow
    LastActivity = DateTime.UtcNow
    Status = status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = SageFs.AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet }

/// Wait for a task, failing the test rather than hanging when it does not finish.
let private within (what: string) (task: Task<'a>) : Task<'a> =
  backgroundTask {
    let! winner = Task.WhenAny(task :> Task, Task.Delay TestTimeouts.patienceBrief)
    match obj.ReferenceEquals(winner, task) with
    | true -> return! task
    | false -> return failwithf "%s: nothing happened within %O" what TestTimeouts.patienceBrief
  }

let private signal () = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

/// A world the relay can be pointed at: a view that tests move, the calls made, the requests that ended, and a wait
/// that tests release.
type private World() =
  member val View : WorkerView = WorkerView.Arriving with get, set
  member val Asked = ConcurrentQueue<string * int>()
  member val Ended = ConcurrentQueue<string * PumpOutcome<string>>()
  member val Replaced = ConcurrentQueue<string>()
  member val Reply : string -> int -> WorkerReply<string> = (fun request _ -> WorkerReply.Replied (request + "!")) with get, set
  /// Set each time the relay parks on the session becoming Ready.
  member val Parked = ConcurrentQueue<TaskCompletionSource<LiveCheckRelay.WaitEnded>>()
  member val Parking = signal () with get, set
  member val EndedSignal = signal () with get, set

let private relayOver (world: World) (supersedes: string -> string -> bool) : LiveCheckRelay.Relay<string, string> =
  let ports : LiveCheckRelay.Ports<string, string> =
    { ReadView = (fun () -> async { return world.View })
      Ask =
        (fun request pid ->
          async {
            world.Asked.Enqueue((request, pid))
            return world.Reply request pid
          })
      AwaitReady =
        async {
          let wake = TaskCompletionSource<LiveCheckRelay.WaitEnded>(TaskCreationOptions.RunContinuationsAsynchronously)
          world.Parked.Enqueue wake
          world.Parking.TrySetResult() |> ignore
          return! wake.Task |> Async.AwaitTask
        }
      Deliver =
        (fun request outcome ->
          world.Ended.Enqueue((request, outcome))
          world.EndedSignal.TrySetResult() |> ignore)
      Superseded = (fun request -> world.Replaced.Enqueue request)
      Say = ignore }
  LiveCheckRelay.Relay(supersedes, ports)

/// The next wait the relay parked on, once it has.
let private nextParked (world: World) : Task<TaskCompletionSource<LiveCheckRelay.WaitEnded>> =
  backgroundTask {
    do! within "the relay parking on the session becoming Ready" world.Parking.Task
    world.Parking <- signal ()
    match world.Parked.TryDequeue() with
    | true, wake -> return wake
    | false, _ -> return failwith "the relay signalled a wait but none was parked"
  }

let private nextEnd (world: World) : Task<string * PumpOutcome<string>> =
  backgroundTask {
    do! within "a request ending" world.EndedSignal.Task
    world.EndedSignal <- signal ()
    match world.Ended.TryDequeue() with
    | true, ended -> return ended
    | false, _ -> return failwith "a request was signalled as ended but none was"
  }

[<Tests>]
let liveCheckRelayTests =
  testList "LiveCheckRelay" [

    testList "viewOf: what the session manager says becomes what can be asked" [
      testCase "a Ready session with a proxy is Serving its worker" <| fun _ ->
        LiveCheckRelay.viewOf (Some (infoWith (SessionLifecycleStatus.Ready { Pid = 7; Port = None }))) true
        |> Expect.equal "serving pid 7" (WorkerView.Serving 7)

      testCase "an Evaluating session with a proxy is Serving too: a check queues behind the eval" <| fun _ ->
        LiveCheckRelay.viewOf (Some (infoWith (SessionLifecycleStatus.Evaluating { Pid = 8; Port = None }))) true
        |> Expect.equal "serving pid 8" (WorkerView.Serving 8)

      testCase "a Starting session is Arriving even though its proxy exists: a worker still warming says nothing true" <| fun _ ->
        LiveCheckRelay.viewOf (Some (infoWith (SessionLifecycleStatus.Starting { Pid = 9; Port = Some 1234 }))) true
        |> Expect.equal "arriving" WorkerView.Arriving

      testCase "a Restarting session is Arriving" <| fun _ ->
        LiveCheckRelay.viewOf (Some (infoWith (SessionLifecycleStatus.Restarting PreviousWorker.ColdStart))) false
        |> Expect.equal "arriving" WorkerView.Arriving

      testCase "a session building is Arriving" <| fun _ ->
        LiveCheckRelay.viewOf (Some (infoWith (SessionLifecycleStatus.Building ("dotnet build", { Pid = 3; Port = None })))) true
        |> Expect.equal "arriving" WorkerView.Arriving

      testCase "a Ready session with no proxy is Gone, so a wait for Ready can never spin on an answer that comes at once" <| fun _ ->
        match LiveCheckRelay.viewOf (Some (infoWith (SessionLifecycleStatus.Ready { Pid = 7; Port = None }))) false with
        | WorkerView.Gone reason -> reason |> Expect.isNotEmpty "the reason is named"
        | other -> failtestf "expected Gone, got %A" other

      testCase "a faulted session is Gone, with the reason" <| fun _ ->
        match LiveCheckRelay.viewOf (Some (infoWith (SessionLifecycleStatus.Faulted (FaultReason.report "the host crashed")))) false with
        | WorkerView.Gone reason -> reason |> Expect.stringContains "the fault's own words" "the host crashed"
        | other -> failtestf "expected Gone, got %A" other

      testCase "a stopped session and an unknown one are Gone" <| fun _ ->
        match LiveCheckRelay.viewOf (Some (infoWith SessionLifecycleStatus.Stopped)) false, LiveCheckRelay.viewOf None false with
        | WorkerView.Gone _, WorkerView.Gone _ -> ()
        | other -> failtestf "expected two Gone, got %A" other
    ]

    testList "the relay asks a worker only when one is Ready" [
      testTask "a request that finds no worker waits for one, and is asked of it the moment it is Ready" {
        let world = World()
        let relay = relayOver world (fun _ _ -> true)
        do! relay.Submit "a" |> Async.StartAsTask
        world.Asked |> Seq.isEmpty |> Expect.isTrue "nothing was asked while no worker was Ready"
        let! (wake: TaskCompletionSource<LiveCheckRelay.WaitEnded>) = nextParked world
        world.View <- WorkerView.Serving 7
        wake.SetResult LiveCheckRelay.WaitEnded.SessionAnswered
        let! (request, outcome) = nextEnd world
        request |> Expect.equal "the request that waited" "a"
        outcome |> Expect.equal "answered by the worker that was Ready" (PumpOutcome.Answered "a!")
        world.Asked |> Seq.toList |> Expect.equal "asked once, of pid 7" [ "a", 7 ]
      }

      testTask "a worker retired under the call said nothing that counts: the request waits and is asked of the replacement" {
        let world = World()
        world.View <- WorkerView.Serving 1
        // The first worker is retired while it answers: the call comes back silent and the manager now says Arriving.
        world.Reply <-
          (fun request pid ->
            match pid with
            | 1 ->
              world.View <- WorkerView.Arriving
              WorkerReply.Silent "the worker was retired"
            | _ -> WorkerReply.Replied (request + "!"))
        let relay = relayOver world (fun _ _ -> true)
        do! relay.Submit "a" |> Async.StartAsTask
        let! (wake: TaskCompletionSource<LiveCheckRelay.WaitEnded>) = nextParked world
        world.View <- WorkerView.Serving 2
        wake.SetResult LiveCheckRelay.WaitEnded.SessionAnswered
        let! (_, outcome) = nextEnd world
        outcome |> Expect.equal "answered once, by the replacement" (PumpOutcome.Answered "a!")
        world.Asked |> Seq.toList |> Expect.equal "asked of the first worker, then of the replacement" [ "a", 1; "a", 2 ]
      }

      testTask "a worker that is Ready and does not answer ends the request unanswered, and is not asked again" {
        let world = World()
        world.View <- WorkerView.Serving 3
        world.Reply <- (fun _ _ -> WorkerReply.Silent "no answer")
        let relay = relayOver world (fun _ _ -> true)
        do! relay.Submit "a" |> Async.StartAsTask
        let! (_, outcome) = nextEnd world
        outcome |> Expect.equal "unanswered, with why" (PumpOutcome.Unanswered (UnansweredWhy.WorkerSilent "no answer"))
        world.Asked |> Seq.length |> Expect.equal "one call, not a loop" 1
      }

      testTask "a newer request replaces an older one that waited, and only the newer is asked" {
        let world = World()
        let relay = relayOver world (fun _ _ -> true)
        do! relay.Submit "old" |> Async.StartAsTask
        let! (wake: TaskCompletionSource<LiveCheckRelay.WaitEnded>) = nextParked world
        do! relay.Submit "new" |> Async.StartAsTask
        world.Replaced |> Seq.toList |> Expect.equal "the older one was closed, not reported" [ "old" ]
        world.View <- WorkerView.Serving 4
        wake.SetResult LiveCheckRelay.WaitEnded.SessionAnswered
        let! (request, _) = nextEnd world
        request |> Expect.equal "the newer request is the one that ended" "new"
        world.Asked |> Seq.toList |> Expect.equal "the older request was never asked" [ "new", 4 ]
      }

      testTask "a wait that reaches its deadline ends the request unanswered, saying no worker came in time" {
        let world = World()
        let relay = relayOver world (fun _ _ -> true)
        do! relay.Submit "a" |> Async.StartAsTask
        let! (wake: TaskCompletionSource<LiveCheckRelay.WaitEnded>) = nextParked world
        wake.SetResult LiveCheckRelay.WaitEnded.DeadlineReached
        let! (_, outcome) = nextEnd world
        outcome |> Expect.equal "no worker in time" (PumpOutcome.Unanswered UnansweredWhy.NoWorkerInTime)
        world.Asked |> Seq.isEmpty |> Expect.isTrue "nothing was asked of a worker that never came"
      }

      testTask "a session that faults while a request waits ends it unanswered, with the reason" {
        let world = World()
        let relay = relayOver world (fun _ _ -> true)
        do! relay.Submit "a" |> Async.StartAsTask
        let! (wake: TaskCompletionSource<LiveCheckRelay.WaitEnded>) = nextParked world
        world.View <- WorkerView.Gone "the session faulted"
        wake.SetResult LiveCheckRelay.WaitEnded.SessionAnswered
        let! (_, outcome) = nextEnd world
        outcome |> Expect.equal "gone" (PumpOutcome.Unanswered (UnansweredWhy.WorkerGone "the session faulted"))
      }
    ]

    testList "a check typed while the worker is replaced (the effect handler)" [
      /// A session whose worker the test replaces: a status the test moves, a proxy that exists only when it says so,
      /// and an AwaitReady that parks until the test says the session is Ready.
      let makeDeps (status: SessionLifecycleStatus ref) (proxy: SessionProxy option ref) (ready: TaskCompletionSource<unit>) : EffectDeps =
        { ResolveSession = fun _ -> Result.Ok (SessionOperations.SessionResolution.DefaultSingle sessionId)
          GetProxy = fun _ -> proxy.Value
          GetStreamingTestProxy = fun _ -> None
          CreateSession = fun _ _ _ -> async { return Result.Error SageFsError.NoActiveSessions }
          ConfigureWarmupAutoOpen = fun _ -> async { return Result.Error "not used" }
          StopSession = fun _ -> async { return Result.Ok () }
          RestartSession = fun _ _ -> async { return Result.Ok "restarted" }
          ListSessions = fun () -> async { return [ infoWith status.Value ] }
          AwaitReady =
            fun _ ->
              async {
                do! ready.Task |> Async.AwaitTask
                return Result.Ok ()
              }
          ReadyDeadline = Timeouts.rebuildReadyWait
          GetWarmupContext = None
          TestCycleCancellation = TestCycleCancellation.create ()
          RegisterFileWatcher = fun _ _ -> ()
          DisposeFileWatcher = fun _ _ -> () }

      let blockingDiagnostic : WorkerDiagnostic =
        { Severity = Features.Diagnostics.DiagnosticSeverity.Blocking
          Message = "The value, namespace, type or module 'Hello' is not defined."
          StartLine = 1; StartColumn = 0; EndLine = 1; EndColumn = 5
          ErrorNumber = 39 }

      let checkEffect =
        TestCycleEffect.RequestFcsTypeCheck
          { SessionId = None
            FilePath = "/src/Math.fs"
            Content = Some "module Math\nlet x = 1"
            AnalysisIdentity = Some (AnalysisIdentity.ofContent "buffer-v1")
            TreeSitterElapsed = TimeSpan.Zero }

      testTask "WHY: nothing can answer while the replacement spawns, so the check waits, and the replacement answers it" {
        let status = ref (SessionLifecycleStatus.Restarting PreviousWorker.ColdStart)
        let proxy : SessionProxy option ref = ref None
        let ready = signal ()
        let calls = ConcurrentQueue<SessionLifecycleStatus>()
        let dispatched = ConcurrentQueue<SageFsMsg>()
        let completed = signal ()
        let deps = makeDeps status proxy ready
        let answering (msg: WorkerMessage) =
          async {
            calls.Enqueue status.Value
            match msg with
            | WorkerMessage.TypeCheckWithSymbols (_, _, replyId) -> return WorkerResponse.TypeCheckWithSymbolsResult (replyId, [], [])
            | _ -> return WorkerResponse.WorkerError (SageFsError.Unexpected (exn "unexpected worker message"))
          }
        let running =
          SageFsEffectHandler.execute deps
            (fun msg ->
              dispatched.Enqueue msg
              match msg with
              | SageFsMsg.FcsTypeCheckCompleted _ -> completed.TrySetResult() |> ignore
              | _ -> ())
            (SageFsEffect.TestCycle checkEffect)
          |> Async.StartAsTask
        // The replacement reports its port: a proxy exists, and the session is Starting. It must not be asked yet.
        status.Value <- SessionLifecycleStatus.Starting { Pid = 2; Port = Some 1234 }
        proxy.Value <- Some answering
        // The replacement finishes warming: the manager answers every AwaitReady.
        status.Value <- SessionLifecycleStatus.Ready { Pid = 2; Port = Some 1234 }
        ready.SetResult()
        do! within "the check being judged" completed.Task
        do! within "the effect ending" running
        calls |> Seq.toList |> Expect.equal "asked once, with the session Ready" [ SessionLifecycleStatus.Ready { Pid = 2; Port = Some 1234 } ]
        dispatched
        |> Seq.exists (function
          | SageFsMsg.FcsTypeCheckCompleted (_, _, FcsTypeCheckResult.Success ("/src/Math.fs", _)) -> true
          | _ -> false)
        |> Expect.isTrue "the check was judged: it passed"
      }

      testTask "WHY: a worker still warming answers a type-check with errors that are not there, so a warming worker is never asked and never makes the code look broken" {
        let status = ref (SessionLifecycleStatus.Starting { Pid = 2; Port = Some 1234 })
        let ready = signal ()
        let warmed = ref false
        let dispatched = ConcurrentQueue<SageFsMsg>()
        let completed = signal ()
        // A proxy exists, as it does from the moment the worker reports its port. Until it has warmed it answers
        // with an error about something the project defines.
        let proxy : SessionProxy option ref =
          ref (
            Some (fun msg ->
              async {
                match msg with
                | WorkerMessage.TypeCheckWithSymbols (_, _, replyId) ->
                  match warmed.Value with
                  | true -> return WorkerResponse.TypeCheckWithSymbolsResult (replyId, [], [])
                  | false -> return WorkerResponse.TypeCheckWithSymbolsResult (replyId, [ blockingDiagnostic ], [])
                | _ -> return WorkerResponse.WorkerError (SageFsError.Unexpected (exn "unexpected worker message"))
              }))
        let deps = makeDeps status proxy ready
        let running =
          SageFsEffectHandler.execute deps
            (fun msg ->
              dispatched.Enqueue msg
              match msg with
              | SageFsMsg.FcsTypeCheckCompleted _ -> completed.TrySetResult() |> ignore
              | _ -> ())
            (SageFsEffect.TestCycle checkEffect)
          |> Async.StartAsTask
        warmed.Value <- true
        status.Value <- SessionLifecycleStatus.Ready { Pid = 2; Port = Some 1234 }
        ready.SetResult()
        do! within "the check being judged" completed.Task
        do! within "the effect ending" running
        dispatched
        |> Seq.choose (function
          | SageFsMsg.FcsTypeCheckCompleted (_, _, result) -> Some result
          | _ -> None)
        |> Seq.toList
        |> Expect.equal "one verdict, from the warmed worker: the check passed" [ FcsTypeCheckResult.Success ("/src/Math.fs", []) ]
      }

      testTask "WHY: a session that faults while the check waits ends it cancelled, never as an error in the user's code" {
        let status = ref (SessionLifecycleStatus.Restarting PreviousWorker.ColdStart)
        let proxy : SessionProxy option ref = ref None
        let ready = signal ()
        let dispatched = ConcurrentQueue<SageFsMsg>()
        let completed = signal ()
        let deps = makeDeps status proxy ready
        let running =
          SageFsEffectHandler.execute deps
            (fun msg ->
              dispatched.Enqueue msg
              match msg with
              | SageFsMsg.FcsTypeCheckCompleted _ -> completed.TrySetResult() |> ignore
              | _ -> ())
            (SageFsEffect.TestCycle checkEffect)
          |> Async.StartAsTask
        status.Value <- SessionLifecycleStatus.Faulted (FaultReason.report "the host crashed")
        ready.SetResult()
        do! within "the check ending" completed.Task
        do! within "the effect ending" running
        dispatched
        |> Seq.choose (function
          | SageFsMsg.FcsTypeCheckCompleted (_, _, result) -> Some result
          | _ -> None)
        |> Seq.toList
        |> Expect.equal "cancelled, not failed" [ FcsTypeCheckResult.Cancelled "/src/Math.fs" ]
      }
    ]
  ]
