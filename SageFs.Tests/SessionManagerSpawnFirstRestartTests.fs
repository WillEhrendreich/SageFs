module SageFs.Tests.SessionManagerSpawnFirstRestartTests

open System.Diagnostics
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

/// Shared verb log so ordering tests can assert spawn-before-stop.
type private Verb =
  | Start
  | Stop
  | Build

type private Harness = {
  Mailbox: MailboxProcessor<SessionCommand>
  ReadSnapshot: unit -> QuerySnapshot
  FaultedEvents: ResizeArray<SessionId * string>
  Cancellation: CancellationTokenSource
}

type private RuntimeHarness = {
  Runtime: SessionManagerRuntime
  Verbs: ResizeArray<Verb>
  GetBuildCalls: unit -> int
  GetStartCalls: unit -> int
}

let private mkRuntime
  (runBuild: int -> Result<string, SageFsError>)
  (startWorker: int -> Result<Process, SageFsError>) =
  let mutable buildCalls = 0
  let mutable startCalls = 0
  let verbs = ResizeArray<Verb>()

  {
    Runtime =
      {
        StartWorkerProcess =
          fun _ _ _ _ _ _ ->
            startCalls <- startCalls + 1
            verbs.Add Verb.Start
            startWorker startCalls
        AwaitWorkerPort = fun _ _ _ _ -> ()
        StopWorker =
          fun _ ->
            verbs.Add Verb.Stop
            async { return () }
        RunBuildAsync =
          fun _ _ -> async {
            buildCalls <- buildCalls + 1
            verbs.Add Verb.Build
            return runBuild buildCalls
          }
      }
    Verbs = verbs
    GetBuildCalls = fun () -> buildCalls
    GetStartCalls = fun () -> startCalls
  }

let private withHarness runtime run =
  use cancellation = new CancellationTokenSource()
  let faultedEvents = ResizeArray<SessionId * string>()
  let mailbox, readSnapshot =
    createWith
      runtime
      cancellation.Token
      ignore
      (fun _ _ -> ())
      (fun _ _ -> ())
      ignore
      (fun _ _ -> ())
      (fun sid msg -> faultedEvents.Add(sid, msg))

  let harness = {
    Mailbox = mailbox
    ReadSnapshot = readSnapshot
    FaultedEvents = faultedEvents
    Cancellation = cancellation
  }

  try
    run harness
  finally
    try
      mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply)
    with _ ->
      ()
    cancellation.Cancel()

let private createSession (harness: Harness) =
  match harness.Mailbox.PostAndReply(fun reply ->
    SessionCommand.CreateSession(["Test.fsproj"], @"C:\Test", true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

let private getManagedSession (harness: Harness) sessionId =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(sessionId, reply)) with
  | Some session -> session
  | None -> failtestf "expected session %s to exist" (SessionId.value sessionId)

let private getWorkerPid (session: ManagedSession) =
  SessionLifecycleStatus.workerPid session.Info.Status
  |> Option.defaultWith (fun () -> failtest "expected worker pid")

let private isReady = function SessionLifecycleStatus.Ready _ -> true | _ -> false
let private isRestarting = function SessionLifecycleStatus.Restarting _ -> true | _ -> false
let private isFaulted = function SessionLifecycleStatus.Faulted _ -> true | _ -> false

/// A proxy that reports Ready on status probe — simulates a live worker.
let private readyProxy =
  fun (msg: WorkerMessage) ->
    async {
      match msg with
      | WorkerMessage.GetStatus rid ->
        let snap : WorkerStatusSnapshot = {
          Status = SessionStatus.Ready
          StatusMessage = None
          EvalCount = 0
          AvgDurationMs = 0L
          MinDurationMs = 0L
          MaxDurationMs = 0L; Projects = []
        }
        return WorkerResponse.StatusResult(rid, snap)
      | WorkerMessage.GetTestDiscovery rid ->
        return WorkerResponse.InitialTestDiscovery([||], [])
      | _ ->
        return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "unexpected message")
    }

/// Drive a session to Ready by posting WorkerReady with a valid transport.
let private makeSessionReady (harness: Harness) (info: SessionInfo) =
  let session = getManagedSession harness info.Id
  let pid = getWorkerPid session
  harness.Mailbox.Post(
    SessionCommand.WorkerReady(
      info.Id,
      pid,
      "http://localhost:4123",
      readyProxy))
  // Mirror what the worker ready-poll does in production: flip the session to
  // Ready after the valid transport is installed.
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
  |> ignore
  harness.Mailbox.Post(SessionCommand.UpdateSessionStatus(info.Id, SessionLifecycleStatus.Ready { Pid = pid; Port = Some 4123 }))
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
  |> ignore

[<Tests>]
let sessionManagerSpawnFirstRestartTests =
  testList "SessionManager spawn-first restart" [
    testCase "T1 — a session that becomes ready spawns exactly one worker (no standby)" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info

        runtime.GetStartCalls()
        |> Expect.equal "becoming ready must not spawn a standby worker" 1

        let session = getManagedSession harness info.Id
        session.Info.Status |> isReady |> Expect.isTrue "session stays ready"

    testCase "T2 — non-rebuild hard reset spawns the replacement before stopping the old worker" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "restart failed: %s" (SageFsError.describe err)

        // At accept-time the replacement has been spawned but the old worker is
        // still serving (no Stop yet — the stop happens when the new worker
        // reports Ready, see T7). The order property P1 is: the Start for the
        // replacement precedes any Stop of the old worker.
        runtime.Verbs |> Seq.toList
        |> Expect.equal "restart must spawn the replacement without stopping the old worker yet" [ Verb.Start; Verb.Start ]
        runtime.GetStartCalls()
        |> Expect.equal "restart must spawn exactly one replacement" 2
        // The session is registered as Restarting through the swap (registry
        // continuity — never a missing-session window).
        let session = getManagedSession harness info.Id
        session.Info.Status
        |> isRestarting
        |> Expect.isTrue "session stays registered as Restarting during the swap"

    testCase "T3 — non-rebuild hard reset with a spawn failure leaves the session Ready and serving" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Error(SageFsError.WorkerSpawnFailed "spawn boom"))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info
        let originalPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Error (SageFsError.WorkerSpawnFailed reason) ->
          reason |> Expect.equal "spawn failure should bubble through" "spawn boom"
        | other ->
          failtestf "expected spawn failure, got %A" other

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> Expect.equal "spawn failure must leave the session Ready (old worker still serving)" (SessionLifecycleStatus.Ready { Pid = originalPid; Port = Some 4123 })
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.equal "spawn failure must not change the worker pid" (Some originalPid)

        // The failed spawn attempt did call StartWorkerProcess, but the old
        // worker was never stopped — the session is untouched and serving.
        runtime.Verbs |> Seq.toList
        |> Expect.equal "spawn failure must not stop the old worker" [ Verb.Start; Verb.Start ]

    testCase "T4 — rebuild hard reset builds while the old worker serves, then swaps spawn-first" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "rebuild restart failed: %s" (SageFsError.describe err)

        // The build runs first with the old worker untouched; only a good build
        // spawns the replacement, which retires the old worker on its Ready (T7).
        runtime.Verbs |> Seq.toList
        |> Expect.equal "rebuild restart must build before spawning, without stopping the serving worker" [ Verb.Start; Verb.Build; Verb.Start ]
        runtime.GetBuildCalls()
        |> Expect.equal "rebuild restart runs one build" 1
        let session = getManagedSession harness info.Id
        session.Info.Status
        |> isRestarting
        |> Expect.isTrue "the swap is in progress"

    testCase "T4b — a failed rebuild leaves the session Ready on its running worker because a compile error must not kill a working session" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Error (SageFsError.BuildFailed(1, [ BuildDiagnostic.ofLine "Hello.fs(3,5): error FS0001: expected int" ])))
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info
        let originalPid = getManagedSession harness info.Id |> getWorkerPid

        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, true, reply)) with
        | Error (SageFsError.BuildFailed(_, diagnostics)) ->
          BuildDiagnostic.describe diagnostics
          |> Expect.equal "the caller gets the build error" "Hello.fs(3,5): error FS0001: expected int"
        | other -> failtestf "expected BuildFailed, got %A" other

        let session = getManagedSession harness info.Id
        session.Info.Status |> Expect.equal "the session keeps serving" (SessionLifecycleStatus.Ready { Pid = originalPid; Port = Some 4123 })
        SessionLifecycleStatus.workerPid session.Info.Status |> Expect.equal "on the same worker" (Some originalPid)
        runtime.Verbs |> Seq.toList
        |> Expect.equal "the running worker is never stopped and nothing is spawned" [ Verb.Start; Verb.Build ]
        harness.FaultedEvents |> Seq.length
        |> Expect.equal "a failed build is not a session fault" 0

    testCase "T5 — the retired worker's exit during a swap is ignored" <| fun _ ->
      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun _ -> Ok(Process.GetCurrentProcess()))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info
        let oldPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        // Accept a non-rebuild restart: the new worker is spawning (Start
        // recorded), the old worker still registered.
        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "restart failed: %s" (SageFsError.describe err)

        // The old worker exits mid-swap — its exit must NOT be treated as a
        // real exit (no crash-recovery restart, no removal).
        harness.Mailbox.Post(SessionCommand.WorkerExited(info.Id, oldPid, 0))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> isFaulted
        |> Expect.isFalse "retired worker exit must not tombstone or remove the session"

        // The session should still be present and eventually complete when the
        // new worker reports ready.
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> Option.isSome
        |> Expect.isTrue "session must stay registered through the swap"

    testCase "T6 — new-worker spawn failure during a swap reverts to the old worker" <| fun _ ->
      let distinctProcesses =
        Process.GetProcesses()
        |> Array.filter (fun p -> p.Id <> Process.GetCurrentProcess().Id && p.Id > 0)
      if distinctProcesses.Length = 0 then
        skiptest "need a second live process to simulate distinct worker pids"
      let otherProcess = distinctProcesses[0]

      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Ok(otherProcess))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info
        let oldPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        // Accept the spawn-first restart (new worker warming).
        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "restart failed: %s" (SageFsError.describe err)

        // The NEW worker fails to come up (its pid differs from the registered
        // old pid). The swap must revert: old session restored, still Ready.
        let newPid = otherProcess.Id
        harness.Mailbox.Post(SessionCommand.WorkerSpawnFailed(info.Id, newPid, "replacement failed"))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        session.Info.Status
        |> Expect.equal "spawn failure during swap must revert to the old Ready worker" (SessionLifecycleStatus.Ready { Pid = oldPid; Port = Some 4123 })
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.equal "revert must restore the old worker pid" (Some oldPid)
        harness.FaultedEvents |> Seq.length
        |> Expect.equal "revert must not fire a fault callback" 0

    testCase "T7 — the new worker's ready commits the swap and retires the old worker" <| fun _ ->
      let distinctProcesses =
        Process.GetProcesses()
        |> Array.filter (fun p -> p.Id <> Process.GetCurrentProcess().Id && p.Id > 0)
      if distinctProcesses.Length = 0 then
        skiptest "need a second live process to simulate distinct worker pids"
      let otherProcess = distinctProcesses[0]

      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Ok(otherProcess))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info

        // Accept the spawn-first restart.
        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "restart failed: %s" (SageFsError.describe err)

        // New worker reports ready → the swap commits: pid updated, old worker
        // retired (Stop verb), session transport installed.
        harness.Mailbox.Post(
          SessionCommand.WorkerReady(
            info.Id,
            otherProcess.Id,
            "http://localhost:4124",
            readyProxy))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.equal "ready must commit the new worker pid" (Some otherProcess.Id)

        runtime.Verbs |> Seq.toList
        |> Expect.equal "ready must retire the old worker (one stop after the two starts)" [ Verb.Start; Verb.Start; Verb.Stop ]

    testCase "T8 — the retired worker's stale WorkerReady during a swap must not commit the old pid" <| fun _ ->
      let distinctProcesses =
        Process.GetProcesses()
        |> Array.filter (fun p -> p.Id <> Process.GetCurrentProcess().Id && p.Id > 0)
      if distinctProcesses.Length = 0 then
        skiptest "need a second live process to simulate distinct worker pids"
      let otherProcess = distinctProcesses[0]

      let runtime =
        mkRuntime
          (fun _ -> Ok "build ok")
          (fun call ->
            match call with
            | 1 -> Ok(Process.GetCurrentProcess())
            | _ -> Ok(otherProcess))

      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info
        let oldPid =
          getManagedSession harness info.Id
          |> getWorkerPid

        // Accept the spawn-first restart: the old session is parked in
        // PendingSwap (still registered with the OLD pid), the new worker is
        // warming.
        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.RestartSession(info.Id, false, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "restart failed: %s" (SageFsError.describe err)

        // The OLD worker's late ready arrives mid-swap carrying the OLD pid.
        // It must be treated as stale: committing it would point the registry
        // back at the dying worker and clear the pending swap, so the new
        // worker's eventual ready would then be ignored as "stale" and the
        // session would be left serving a dead process.
        harness.Mailbox.Post(
          SessionCommand.WorkerReady(
            info.Id,
            oldPid,
            "http://localhost:4123",
            readyProxy))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let sessionAfterStale = getManagedSession harness info.Id
        SessionLifecycleStatus.workerPid sessionAfterStale.Info.Status
        |> Expect.equal "stale ready must not overwrite the registered old pid mid-swap" (Some oldPid)
        sessionAfterStale.Info.Status
        |> isRestarting
        |> Expect.isTrue "stale ready must not flip the session out of Restarting"

        // The NEW worker's ready still commits the swap normally.
        harness.Mailbox.Post(
          SessionCommand.WorkerReady(
            info.Id,
            otherProcess.Id,
            "http://localhost:4124",
            readyProxy))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore

        let session = getManagedSession harness info.Id
        SessionLifecycleStatus.workerPid session.Info.Status
        |> Expect.equal "the new worker's ready must still commit the swap" (Some otherProcess.Id)

        // Mirror the worker ready-poll: once the swap commits, the poll probes
        // the new worker and flips the registry to Ready.
        harness.Mailbox.Post(SessionCommand.UpdateSessionStatus(info.Id, SessionLifecycleStatus.Ready { Pid = otherProcess.Id; Port = Some 4124 }))
        harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        |> ignore
        let sessionReady = getManagedSession harness info.Id
        sessionReady.Info.Status
        |> isReady
        |> Expect.isTrue "session must return to Ready after the swap commits"
  ]

[<Tests>]
let sessionManagerProjectRolesTests =
  testList "SessionManager project roles" [
    testTask "WHY — SessionManager — a Ready worker's classified projects reach the session because Run App picks its target from them" {
      let runtime = mkRuntime (fun _ -> Ok "build ok") (fun _ -> Ok(Process.GetCurrentProcess()))
      let app : SageFs.ProjectLoading.ClassifiedProject =
        { Path = "/src/App/App.fsproj"; Role = SageFs.ProjectLoading.ProjectRole.Executable; PackageRefs = [] }
      let proxy (msg: WorkerMessage) =
        async {
          match msg with
          | WorkerMessage.GetStatus rid ->
            return
              WorkerResponse.StatusResult(
                rid,
                { Status = SessionStatus.Ready; StatusMessage = None; EvalCount = 0
                  AvgDurationMs = 0L; MinDurationMs = 0L; MaxDurationMs = 0L; Projects = [ app ] })
          | _ -> return! readyProxy msg
        }
      let cancellation = new CancellationTokenSource()
      let mailbox, _ =
        createWith runtime.Runtime cancellation.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ())
      try
        let! created =
          mailbox.PostAndAsyncReply(fun reply ->
            SessionCommand.CreateSession([ app.Path ], "/src/App", true, WorkflowTypes.SessionWorkflow.Interactive, reply))
        let info =
          match created with
          | Ok info -> info
          | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)
        let! session = mailbox.PostAndAsyncReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        let pid =
          session
          |> Option.bind (fun s -> SessionLifecycleStatus.workerPid s.Info.Status)
          |> Option.defaultWith (fun () -> failtest "expected worker pid")
        mailbox.Post(SessionCommand.WorkerReady(info.Id, pid, "http://localhost:4123", proxy))
        let deadline = System.DateTime.UtcNow.AddSeconds 10.0
        let rec settledRoles () =
          task {
            let! current = mailbox.PostAndAsyncReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
            let roles = current |> Option.map (fun s -> s.Info.ProjectRoles) |> Option.defaultValue []
            match roles, System.DateTime.UtcNow > deadline with
            | [], false ->
              do! System.Threading.Tasks.Task.Delay 50
              return! settledRoles ()
            | roles, _ -> return roles
          }
        let! roles = settledRoles ()
        roles |> Expect.equal "the worker's executable project must be on the session" [ app ]
      finally
        cancellation.Cancel()
        cancellation.Dispose()
    }
  ]

[<Tests>]
let sessionManagerStaleReadyReportTests =
  testList "SessionManager stale ready report" [
    testCase "WHY — SessionManager — a Ready report from the worker being replaced leaves the session Restarting because a stale Ready would release AwaitReady into a session with no worker" <| fun _ ->
      let runtime = mkRuntime (fun _ -> Ok "build ok") (fun _ -> Ok(Process.GetCurrentProcess()))
      withHarness runtime.Runtime <| fun harness ->
        let info = createSession harness
        makeSessionReady harness info
        let oldPid = getWorkerPid (getManagedSession harness info.Id)
        let webLive = WorkflowTypes.SessionWorkflow.WebLive WorkflowTypes.BrowserRefreshConfig.defaults
        match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.SwitchWorkflow(info.Id, webLive, reply)) with
        | Ok _ -> ()
        | Error err -> failtestf "switch failed: %s" (SageFsError.describe err)
        harness.Mailbox.Post(SessionCommand.WorkerReportedReady(info.Id, oldPid, []))
        (getManagedSession harness info.Id).Info.Status
        |> isRestarting
        |> Expect.isTrue "the swap is still waiting for the new worker"
  ]

[<Tests>]
let buildDiagnosticsOfTests =
  let fs0433 =
    "/src/Web/Program.fs(160,1): error FS0433: A function labeled with the 'EntryPointAttribute' attribute must be the last declaration in the last file in the compilation sequence. [/src/Web/Web.fsproj]"
  testList "SessionManager build diagnostics" [
    testCase "WHY — SessionManager.buildDiagnosticsOf — names the compiler errors dotnet build printed, deduplicated and stripped of project-path noise, because the card must say why the app did not come back without repeating itself" <| fun _ ->
      let diagnostics = buildDiagnosticsOf [ "  Determining projects to restore..."; fs0433; fs0433; "Build FAILED." ] []
      diagnostics |> List.length |> Expect.equal "one distinct diagnostic despite the duplicate line" 1
      let d = diagnostics.[0]
      d.File |> Expect.equal "the source file" (Some "/src/Web/Program.fs")
      d.Line |> Expect.equal "the line" (Some 160)
      d.Column |> Expect.equal "the column" (Some 1)
      d.Code |> Expect.equal "the diagnostic code" (Some "FS0433")
      d.Severity |> Expect.equal "an error, not a warning" BuildDiagnosticSeverity.Error
      d.Message.Contains "[/src/Web/Web.fsproj]" |> Expect.isFalse "no project-path noise carried into the message"
      // No hint baked in here — that is a surface's job (SageFsError.suggestedAction,
      // or AppRun.fs's own dashboard-card wording), never the parsed diagnostic data.
      d.Message.Contains "→" |> Expect.isFalse "no call-to-action leaks into diagnostic data"

    testCase "WHY — SessionManager.buildDiagnosticsOf — falls back to the output tail as location-less diagnostics when no error line is found because reporting nothing tells the user nothing" <| fun _ ->
      let diagnostics = buildDiagnosticsOf [ "line a"; "something went wrong" ] [ "boom" ]
      diagnostics |> List.exists (fun d -> d.File.IsNone && d.Message = "something went wrong")
      |> Expect.isTrue "the stdout tail line survives as a location-less diagnostic"
      diagnostics |> List.exists (fun d -> d.File.IsNone && d.Message = "boom")
      |> Expect.isTrue "the stderr tail line survives as a location-less diagnostic"
  ]

[<Tests>]
let buildArgumentsTests =
  testList "SessionManager rebuild arguments" [
    testCase "WHY — SessionManager.buildArguments — a rebuild is incremental because a clean build deletes the last good output first and one typo would leave nothing to run" <| fun _ ->
      buildArguments "/src/Web/Web.fsproj"
      |> Expect.equal "build the project without cleaning it first" [ "build"; "/src/Web/Web.fsproj"; "--no-restore" ]
  ]

[<Tests>]
let workerFaultReportTests =
  testList "SessionManager worker fault report" [
    testTask "WHY — SessionManager — a worker that reports Faulted during warmup faults the session with its reason because Run and the card must not wait on a session that cannot become Ready" {
      let runtime = mkRuntime (fun _ -> Ok "build ok") (fun _ -> Ok(Process.GetCurrentProcess()))
      let reason = "Missing DLL /src/App/bin/Debug/net10.0/App.dll. Please build your project."
      let proxy (msg: WorkerMessage) =
        async {
          match msg with
          | WorkerMessage.GetStatus rid ->
            return
              WorkerResponse.StatusResult(
                rid,
                { Status = SessionStatus.Faulted; StatusMessage = Some reason; EvalCount = 0
                  AvgDurationMs = 0L; MinDurationMs = 0L; MaxDurationMs = 0L; Projects = [] })
          | _ -> return! readyProxy msg
        }
      let cancellation = new CancellationTokenSource()
      let mailbox, _ =
        createWith runtime.Runtime cancellation.Token ignore (fun _ _ -> ()) (fun _ _ -> ()) ignore (fun _ _ -> ()) (fun _ _ -> ())
      try
        let! created =
          mailbox.PostAndAsyncReply(fun reply ->
            SessionCommand.CreateSession([ "/src/App/App.fsproj" ], "/src/App", true, WorkflowTypes.SessionWorkflow.Interactive, reply))
        let info =
          match created with
          | Ok info -> info
          | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)
        let! session = mailbox.PostAndAsyncReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
        let pid =
          session
          |> Option.bind (fun s -> SessionLifecycleStatus.workerPid s.Info.Status)
          |> Option.defaultWith (fun () -> failtest "expected worker pid")
        mailbox.Post(SessionCommand.WorkerReady(info.Id, pid, "http://localhost:4123", proxy))
        let deadline = System.DateTime.UtcNow.AddSeconds 10.0
        let rec faultReason () =
          task {
            let! current = mailbox.PostAndAsyncReply(fun reply -> SessionCommand.GetSession(info.Id, reply))
            match current |> Option.map (fun s -> s.Info.Status), System.DateTime.UtcNow > deadline with
            | Some (SessionLifecycleStatus.Faulted why), _ -> return why
            | _, true -> return failtest "the session never became Faulted"
            | _, false ->
              do! System.Threading.Tasks.Task.Delay 50
              return! faultReason ()
          }
        let! why = faultReason ()
        why |> Expect.equal "the worker's own reason" (Some reason)
      finally
        cancellation.Cancel()
        cancellation.Dispose()
    }
  ]
