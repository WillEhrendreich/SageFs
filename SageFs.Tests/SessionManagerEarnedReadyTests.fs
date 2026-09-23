/// Outcome tests through the REAL SessionManager mailbox path — not a mock of
/// the decision, the actual `WorkerReady` -> `WorkerReportedReady` sequence
/// production drives, with only process spawn/stop faked (see `mkRuntime`).
/// This is the exact shape that fooled us twice: a worker can answer its own
/// `/status` with `SessionStatus.Ready` while having resolved zero of what it
/// was asked to load, and until now the daemon trusted that report verbatim.
module SageFs.Tests.SessionManagerEarnedReadyTests

open System.Diagnostics
open System.IO
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

type private Harness = {
  Mailbox: MailboxProcessor<SessionCommand>
  FaultedEvents: ResizeArray<SessionId * string>
}

let private mkRuntime () : SessionManagerRuntime =
  { StartWorkerProcess =
      fun _ _ _ _ _ _ -> Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SpawnedWorker)
    AwaitWorkerPort = fun _ _ _ _ -> ()
    StopWorker = fun _ -> async { return () }
    RunBuildAsync = fun _ _ -> async { return Ok "build ok" } }

let private withHarness (run: Harness -> unit) =
  use cancellation = new CancellationTokenSource()
  let faultedEvents = ResizeArray<SessionId * string>()
  let mailbox, _readSnapshot =
    createWith
      (mkRuntime ())
      cancellation.Token
      ignore
      (fun _ _ -> ())
      (fun _ _ -> ())
      ignore
      (fun _ _ -> ())
      (fun sid msg -> faultedEvents.Add(sid, msg))
      (fun _ _ -> ())
  try
    run { Mailbox = mailbox; FaultedEvents = faultedEvents }
  finally
    try mailbox.PostAndReply(fun reply -> SessionCommand.StopAll reply) with _ -> ()
    cancellation.Cancel()

let private createSession (harness: Harness) (projects: string list) (workingDir: string) =
  match harness.Mailbox.PostAndReply(fun reply ->
    SessionCommand.CreateSession(projects, workingDir, true, WorkflowTypes.SessionWorkflow.Interactive, reply)) with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

let private getManagedSession (harness: Harness) sessionId : ManagedSession =
  match harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(sessionId, reply)) with
  | Some session -> session
  | None -> failtestf "expected session %s to exist" (SessionId.value sessionId)

/// A proxy that answers GetStatus as Ready — good enough to install a valid
/// transport via WorkerReady; the actual readiness DECISION under test lives
/// entirely in the WorkerReportedReady handler, not in this proxy.
let private readyProxy : SessionProxy =
  fun (msg: WorkerMessage) ->
    async {
      match msg with
      | WorkerMessage.GetTestDiscovery rid -> return WorkerResponse.InitialTestDiscovery([||], [])
      | WorkerMessage.GetStatus rid ->
        let snap : WorkerStatusSnapshot =
          { Status = SessionStatus.Ready
            StatusMessage = None
            EvalCount = 0
            AvgDurationMs = 0L
            MinDurationMs = 0L
            MaxDurationMs = 0L
            Projects = []
            CoreVersion = "0.0.0-test" }
        return WorkerResponse.StatusResult(rid, snap)
      | _ -> return WorkerResponse.WorkerError (SageFsError.WorkerSpawnFailed "unexpected message")
    }

/// Install a live-looking transport (the step before the daemon's own
/// readiness gate runs) and hand back the worker pid the session was given.
let private installTransport (harness: Harness) (info: SessionInfo) : int =
  let pid = SessionLifecycleStatus.workerPid (getManagedSession harness info.Id).Info.Status |> Option.defaultWith (fun () -> failtest "expected a worker pid after CreateSession")
  harness.Mailbox.Post(SessionCommand.WorkerReady(info.Id, pid, "http://localhost:4123", readyProxy))
  harness.Mailbox.PostAndReply(fun reply -> SessionCommand.GetSession(info.Id, reply)) |> ignore
  pid

let private isReady = function SessionLifecycleStatus.Ready _ -> true | _ -> false
let private isFaulted = function SessionLifecycleStatus.Faulted _ -> true | _ -> false

let private withEmptyTempDir (run: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), "sagefs-earned-ready-" + System.Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  try run dir
  finally Directory.Delete(dir, true)

[<Tests>]
let earnedReadyTests =
  testList "SessionManager earned Ready (WorkerReportedReady)" [
    testCase "WHY — a session that named a project and resolved none of it is not Ready: this is the exact bug that shipped Ready with loadedProjects: []" <| fun _ ->
      withHarness <| fun harness ->
        let info = createSession harness [ "Foo.fsproj" ] "/nonexistent/does-not-matter"
        let pid = installTransport harness info
        harness.Mailbox.Post(SessionCommand.WorkerReportedReady(info.Id, pid, []))
        let session = getManagedSession harness info.Id
        session.Info.Status |> isFaulted |> Expect.isTrue "requested-but-unresolved must fault, not Ready"
        match session.Info.Status with
        | SessionLifecycleStatus.Faulted (Some reason) ->
          reason.Contains "Foo.fsproj" |> Expect.isTrue "the fault names what was requested"
        | other -> failtestf "expected Faulted(Some _), got %A" other
        harness.FaultedEvents
        |> Seq.exists (fun (sid, msg) -> sid = info.Id && msg.Contains "Foo.fsproj")
        |> Expect.isTrue "onSessionFaulted fires with the same reason, so every subscriber (dashboard SSE included) sees it"

    testCase "WHY — a session created with no projects asked for, in a directory with nothing to auto-discover, is a genuine scratch REPL and stays Ready" <| fun _ ->
      withEmptyTempDir <| fun dir ->
        withHarness <| fun harness ->
          let info = createSession harness [] dir
          let pid = installTransport harness info
          harness.Mailbox.Post(SessionCommand.WorkerReportedReady(info.Id, pid, []))
          let session = getManagedSession harness info.Id
          session.Info.Status |> isReady |> Expect.isTrue "nothing was asked for and nothing was there to find — this is not broken"

    testCase "WHY — a session that named a project and DID resolve it is unaffected by the gate" <| fun _ ->
      withHarness <| fun harness ->
        let info = createSession harness [ "Foo.fsproj" ] "/nonexistent/does-not-matter"
        let pid = installTransport harness info
        let role : ProjectLoading.ClassifiedProject =
          { Path = "/repo/Foo.fsproj"; Role = ProjectLoading.ProjectRole.Library; PackageRefs = [] }
        harness.Mailbox.Post(SessionCommand.WorkerReportedReady(info.Id, pid, [ role ]))
        let session = getManagedSession harness info.Id
        session.Info.Status |> isReady |> Expect.isTrue "resolved projects must still reach Ready"
        session.Info.ProjectRoles |> Expect.equal "the resolved role is recorded" [ role ]

    testCase "WHY — SessionHealth agrees with the gate: a Faulted-by-the-gate session classifies as Failed everywhere that reads SessionHealth.classify (get_fsi_status, /api/sessions, /health, the dashboard all derive from this one function)" <| fun _ ->
      withHarness <| fun harness ->
        let info = createSession harness [ "Foo.fsproj" ] "/nonexistent/does-not-matter"
        let pid = installTransport harness info
        harness.Mailbox.Post(SessionCommand.WorkerReportedReady(info.Id, pid, []))
        let session = getManagedSession harness info.Id
        SessionHealth.classify session.Info.Status session.Info.ProjectRoles None
        |> Expect.equal "the daemon's own status IS the input every surface classifies" (SessionHealth.Failed (ProjectResolution.unresolvedReason [ "Foo.fsproj" ]))
  ]
