module SageFs.Tests.TrunkFollowShellTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Cohort
open SageFs.WorkerProtocol
open SageFs.Features
open SageFs.Features.TrunkFollow

// The performer in a world of its own: the sessions the daemon has, what the worker answers, what git does, and the order things
// happened in. And the owner that runs it.

let private landing (n: int) : LandedLanding = { Landing = LandingId (sprintf "l-%d" n); Commit = sprintf "c-%d" n }

let private trunkPath = Path.Combine(Path.GetTempPath(), "cohort-trunk")

let private file (name: string) : SavedFile = { Path = Path.Combine(trunkPath, name); Kind = SaveKind.Changed }

let private session (id: string) (dir: string) (status: SessionLifecycleStatus) (app: AppRun.AppRunState) : SessionInfo =
  { Id = (match SessionId.validate id with Result.Ok s -> s | Result.Error e -> failwith e)
    Name = None
    Projects = []
    WorkingDirectory = dir
    SolutionRoot = None
    CreatedAt = DateTime.MinValue
    LastActivity = DateTime.MinValue
    Status = status
    Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None
    ProjectRoles = []
    App = app
    Rebuild = LastRebuild.NeverRebuilt
    Reload = SessionReload.NoReloadYet
    Freshness = ReplFreshness.InSync }

let private ready = SessionLifecycleStatus.Ready { Pid = 1; Port = None }

let private restarting = SessionLifecycleStatus.Restarting (PreviousWorker.ofPid (Some 1))

let private running : AppRun.AppRunState =
  AppRun.AppRunState.Running
    { RunId = "r1"; Project = "p"; EntryPoint = "e"; Endpoint = AppRun.AppEndpoint.NoServer; StartedAt = DateTime.MinValue }

type private World =
  { Log: ResizeArray<string>
    mutable Sessions: SessionInfo list
    mutable WorkerAnswer: WorkerMessage -> Result<WorkerResponse, SageFsError>
    mutable Head: Result<string, string>
    mutable Diff: Result<SavedFile list, string>
    mutable MoveResult: Result<unit, string>
    mutable BuildResult: Result<unit, string>
    mutable Settles: unit -> unit }

let private newWorld (sessions: SessionInfo list) : World =
  { Log = ResizeArray<string>()
    Sessions = sessions
    WorkerAnswer =
      function
      | WorkerMessage.SetSaveSource (source, rid) -> Result.Ok (WorkerResponse.SaveSourceSet (rid, source))
      | WorkerMessage.ApplySaves (_, rid) -> Result.Ok (WorkerResponse.SavesApplied (rid, SessionOutcome.Delivered []))
      | other -> Result.Error (SageFsError.WorkerCommunicationFailed ("s", sprintf "unexpected %A" other))
    Head = Result.Ok "c-0"
    Diff = Result.Ok [ file "Handlers.fs" ]
    MoveResult = Result.Ok ()
    BuildResult = Result.Ok ()
    Settles = ignore }

let private depsOf (w: World) : TrunkFollowShell.TrunkShellDeps =
  { TrunkPath = fun () -> Some trunkPath
    Sessions = fun () -> w.Sessions
    AwaitSettled =
      fun condition ->
        async {
          w.Log.Add "await-settled"
          w.Settles ()
          return condition ()
        }
    Ask =
      fun _ msg ->
        async {
          w.Log.Add (
            match msg with
            | WorkerMessage.SetSaveSource _ -> "ask:set-save-source"
            | WorkerMessage.ApplySaves _ -> "ask:apply-saves"
            | _ -> "ask:other")
          return w.WorkerAnswer msg
        }
    CurrentHead = fun _ -> async { w.Log.Add "git:head"; return w.Head }
    Diff = fun _ _ _ -> async { w.Log.Add "git:diff"; return w.Diff }
    MoveTo = fun _ _ -> async { w.Log.Add "git:move"; return w.MoveResult }
    Build = fun _ -> async { w.Log.Add "build"; return w.BuildResult }
    NewReplyId = fun () -> "rid" }

let private servingTrunkSession = session "aaaaaaa1" trunkPath ready running

let private moveOf (w: World) : Task<TrunkMove> =
  Async.StartAsTask ((TrunkFollowShell.performer (depsOf w)).Move (landing 1))

let private steps (w: World) : string list =
  List.ofSeq w.Log |> List.filter (fun entry -> entry <> "await-settled")

[<Tests>]
let trunkShellTests =
  testList "TrunkFollowShell" [
    testTask "the worker is told to take saves from landings only BEFORE the checkout moves, and the app is built after" {
      let w = newWorld [ servingTrunkSession ]
      let! move = moveOf w
      steps w |> Expect.equal "in order" [ "ask:set-save-source"; "git:head"; "git:diff"; "git:move"; "build" ]
      match move with
      | TrunkMove.Moved ([ f ], [ { Session = "aaaaaaa1"; State = TrunkSessionState.Serving } ]) ->
        f |> Expect.equal "the changed file" (file "Handlers.fs")
      | other -> failtestf "unexpected %A" other
    }

    testTask "no build for a session with no app" {
      let w = newWorld [ session "aaaaaaa1" trunkPath ready AppRun.AppRunState.NotRunning ]
      let! move = moveOf w
      steps w |> Expect.equal "the checkout moves and nothing is built or asked" [ "git:head"; "git:diff"; "git:move" ]
      match move with
      | TrunkMove.Moved (_, [ { State = TrunkSessionState.NoRunningApp } ]) -> ()
      | other -> failtestf "unexpected %A" other
    }

    testTask "no build for a landing that changes nothing a build reads" {
      let w = newWorld [ servingTrunkSession ]
      w.Diff <- Result.Ok [ { Path = Path.Combine(trunkPath, "notes.md"); Kind = SaveKind.Changed } ]
      let! _ = moveOf w
      List.contains "build" (List.ofSeq w.Log) |> Expect.isFalse "no build"
    }

    testTask "a session that is still restarting is waited on, then read again" {
      let w = newWorld [ session "aaaaaaa1" trunkPath restarting AppRun.AppRunState.NotRunning ]
      w.Settles <- fun () -> w.Sessions <- [ servingTrunkSession ]
      let! move = moveOf w
      List.ofSeq w.Log |> List.head |> Expect.equal "waited first" "await-settled"
      match move with
      | TrunkMove.Moved (_, [ { State = TrunkSessionState.Serving } ]) -> ()
      | other -> failtestf "expected the settled session to be served, got %A" other
    }

    testTask "a session that never settles is unavailable, not a session with no app" {
      let w = newWorld [ session "aaaaaaa1" trunkPath restarting AppRun.AppRunState.NotRunning ]
      let! move = moveOf w
      match move with
      | TrunkMove.Moved (_, [ { State = TrunkSessionState.Unavailable reason } ]) -> reason |> Expect.stringContains "says why" "had not settled"
      | other -> failtestf "unexpected %A" other
    }

    testTask "a worker that cannot be told is not read as serving, and the checkout still moves" {
      let w = newWorld [ servingTrunkSession ]
      w.WorkerAnswer <- fun _ -> Result.Error (SageFsError.WorkerCommunicationFailed ("aaaaaaa1", "gone"))
      let! move = moveOf w
      List.ofSeq w.Log |> Expect.contains "the checkout moved" "git:move"
      match move with
      | TrunkMove.Moved (_, [ { State = TrunkSessionState.Unavailable reason } ]) -> reason |> Expect.stringContains "says why" "landings only"
      | other -> failtestf "unexpected %A" other
    }

    testTask "a build that fails leaves the app untold, and says so" {
      let w = newWorld [ servingTrunkSession ]
      w.BuildResult <- Result.Error "FS0001 type mismatch"
      let! move = moveOf w
      match move with
      | TrunkMove.Moved (_, [ { State = TrunkSessionState.Unavailable reason } ]) ->
        reason |> Expect.stringContains "names the build failure" "FS0001 type mismatch"
      | other -> failtestf "unexpected %A" other
    }

    testTask "git failing at any step is a checkout that did not move, with the step named" {
      let headFails = newWorld [ servingTrunkSession ]
      headFails.Head <- Result.Error "no head"
      let! a = moveOf headFails
      let diffFails = newWorld [ servingTrunkSession ]
      diffFails.Diff <- Result.Error "bad diff"
      let! b = moveOf diffFails
      let moveFails = newWorld [ servingTrunkSession ]
      moveFails.MoveResult <- Result.Error "dirty"
      let! c = moveOf moveFails
      for move, expected in [ a, "head"; b, "bad diff"; c, "dirty" ] do
        match move with
        | TrunkMove.NotMoved reason -> reason |> Expect.stringContains "names what failed" expected
        | other -> failtestf "unexpected %A" other
    }

    testTask "with no integration configured nothing moves" {
      let w = newWorld []
      let deps = { depsOf w with TrunkPath = fun () -> None }
      let! move = Async.StartAsTask ((TrunkFollowShell.performer deps).Move (landing 1))
      match move with
      | TrunkMove.NotMoved reason -> reason |> Expect.stringContains "says what to do" "set_integration_ref"
      | other -> failtestf "unexpected %A" other
    }

    testTask "delivering hands the worker the files and returns what its pipeline said" {
      let w = newWorld [ servingTrunkSession ]
      let verdict : FileVerdict = { File = (file "Handlers.fs").Path; Outcome = FileOutcome.NotWatched }
      w.WorkerAnswer <-
        function
        | WorkerMessage.ApplySaves (files, rid) ->
          files |> Expect.equal "the files travel" [ file "Handlers.fs" ]
          Result.Ok (WorkerResponse.SavesApplied (rid, SessionOutcome.Delivered [ verdict ]))
        | other -> Result.Error (SageFsError.WorkerCommunicationFailed ("s", sprintf "%A" other))
      let! outcome = Async.StartAsTask ((TrunkFollowShell.performer (depsOf w)).Deliver (landing 1) "aaaaaaa1" [ file "Handlers.fs" ])
      outcome |> Expect.equal "what the pipeline said" (SessionOutcome.Delivered [ verdict ])
    }

    testTask "a worker that errors is recorded as unreachable" {
      let w = newWorld [ servingTrunkSession ]
      w.WorkerAnswer <- fun _ -> Result.Error (SageFsError.WorkerCommunicationFailed ("aaaaaaa1", "timed out"))
      let! outcome = Async.StartAsTask ((TrunkFollowShell.performer (depsOf w)).Deliver (landing 1) "aaaaaaa1" [ file "Handlers.fs" ])
      match outcome with
      | SessionOutcome.Unreachable reason -> reason |> Expect.isNotEmpty "a reason"
      | other -> failtestf "unexpected %A" other
    }
  ]

/// Wait until the owner's machine satisfies `condition`, woken by its own change event.
let private awaitMachine (handle: TrunkFollowOwner.Handle) (condition: TrunkMachine -> bool) : Task<bool> =
  let tcs = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
  let subscription = handle.Changes.Subscribe(fun machine -> if condition machine then tcs.TrySetResult true |> ignore)
  if condition (handle.Read ()) then tcs.TrySetResult true |> ignore
  task {
    let! winner = Task.WhenAny(tcs.Task, Task.Delay TestTimeouts.patienceBrief)
    subscription.Dispose()
    return obj.ReferenceEquals(winner, tcs.Task) && tcs.Task.Result
  }

let private servingSession : TrunkSession = { Session = "aaaaaaa1"; State = TrunkSessionState.Serving }

[<Tests>]
let trunkOwnerTests =
  testList "TrunkFollowOwner" [
    testTask "a landing that lands is moved to, delivered, and recorded, one landing at a time" {
      let calls = ResizeArray<string>()
      let performer : TrunkFollowOwner.TrunkPerformer =
        { Move =
            fun l ->
              async {
                lock calls (fun () -> calls.Add (sprintf "move %A" l.Landing))
                return TrunkMove.Moved ([ file "Handlers.fs" ], [ servingSession ])
              }
          Deliver =
            fun l s _ ->
              async {
                lock calls (fun () -> calls.Add (sprintf "deliver %A %s" l.Landing s))
                return SessionOutcome.Delivered []
              } }
      use handle = TrunkFollowOwner.start (Utils.Log.asILogger ()) performer
      handle.Post (TrunkEvent.Landed (landing 1))
      handle.Post (TrunkEvent.Landed (landing 2))
      let! both = awaitMachine handle (fun m -> List.length m.Records = 2)
      both |> Expect.isTrue "both landings recorded"
      lock calls (fun () -> List.ofSeq calls)
      |> Expect.equal
        "the second landing is not touched until the first is recorded"
        [ sprintf "move %A" (landing 1).Landing
          sprintf "deliver %A aaaaaaa1" (landing 1).Landing
          sprintf "move %A" (landing 2).Landing
          sprintf "deliver %A aaaaaaa1" (landing 2).Landing ]
    }

    testTask "a performer that throws is an answer, not a stuck landing" {
      let performer : TrunkFollowOwner.TrunkPerformer =
        { Move = fun _ -> async { return failwith "git is gone" }
          Deliver = fun _ _ _ -> async { return SessionOutcome.NoApp } }
      use handle = TrunkFollowOwner.start (Utils.Log.asILogger ()) performer
      handle.Post (TrunkEvent.Landed (landing 1))
      let! recorded = awaitMachine handle (fun m -> not (List.isEmpty m.Records))
      recorded |> Expect.isTrue "recorded"
      match ((handle.Read ()).Records |> List.head).Verdict with
      | TrunkVerdict.NotMoved reason -> reason |> Expect.stringContains "says why" "git is gone"
      | other -> failtestf "unexpected %A" other
    }

    testTask "the change event fires after Read already returns the new machine" {
      let performer : TrunkFollowOwner.TrunkPerformer =
        { Move = fun _ -> async { return TrunkMove.Moved ([], []) }
          Deliver = fun _ _ _ -> async { return SessionOutcome.NoApp } }
      use handle = TrunkFollowOwner.start (Utils.Log.asILogger ()) performer
      let agreed = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
      let subscription = handle.Changes.Subscribe(fun machine -> agreed.TrySetResult (handle.Read () = machine) |> ignore)
      handle.Post (TrunkEvent.Landed (landing 1))
      let! winner = Task.WhenAny(agreed.Task, Task.Delay TestTimeouts.patienceBrief)
      subscription.Dispose()
      obj.ReferenceEquals(winner, agreed.Task) |> Expect.isTrue "an event arrived"
      agreed.Task.Result |> Expect.isTrue "and Read agreed with it"
    }
  ]
