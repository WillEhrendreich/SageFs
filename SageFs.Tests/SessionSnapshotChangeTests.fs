module SageFs.Tests.SessionSnapshotChangeTests

open System.Diagnostics
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

// The Elm model's session list is refreshed by dispatching `ListSessions`, and the only thing that did it when a
// session changed status on its own (Starting to Ready, a worker that faulted) was a 10 s poll. The manager publishes
// one snapshot per command and now says so when the sessions in it differ from the last one (`OnSessionsChanged`),
// so the daemon refreshes the list from the change. These cases drive the real mailbox with a fake runtime through
// random histories of commands and check the one promise the daemon relies on: a status transition is announced,
// exactly once, and a command that changed nothing announces nothing.

let runtime : SessionManagerRuntime =
  { StartWorkerProcess =
      fun _ _ _ _ _ _ -> Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SessionManager.SpawnedWorker)
    AwaitWorkerPort = fun _ _ _ _ _ -> ()
    Ledger = StartLedger.closed
    StopWorker = fun _ -> async { return () }
    RunBuildAsync = fun _ _ -> async { return Ok "build ok" } }

let patience = int TestTimeouts.patience.TotalMilliseconds

let ownPid : int = Process.GetCurrentProcess().Id

[<RequireQualifiedAccess>]
type StatusKind =
  | Ready
  | Evaluating
  | Faulted
  | Stopped

/// One thing the history asks of the manager. `pick` chooses among the sessions that exist when it runs, so a shrunk
/// history still names real sessions.
[<RequireQualifiedAccess>]
type Op =
  | CreateBare
  | CreateProject
  | Stop of pick: byte
  | Touch of pick: byte
  | SetStatus of pick: byte * status: StatusKind
  | WorkerReady of pick: byte
  | WorkerFaulted of pick: byte
  | WorkerExited of pick: byte
  | StopUnknown

let statusOf (kind: StatusKind) : SessionLifecycleStatus =
  match kind with
  | StatusKind.Ready -> SessionLifecycleStatus.Ready { Pid = ownPid; Port = None }
  | StatusKind.Evaluating -> SessionLifecycleStatus.Evaluating { Pid = ownPid; Port = None }
  | StatusKind.Faulted -> SessionLifecycleStatus.Faulted (FaultReason.report "history")
  | StatusKind.Stopped -> SessionLifecycleStatus.Stopped

let opOf (kind: byte) (pick: byte) : Op =
  match int kind % 11 with
  | 0 -> Op.CreateBare
  | 1 -> Op.CreateProject
  | 2 -> Op.Stop pick
  | 3 -> Op.Touch pick
  | 4 -> Op.SetStatus(pick, StatusKind.Ready)
  | 5 -> Op.SetStatus(pick, StatusKind.Evaluating)
  | 6 -> Op.SetStatus(pick, StatusKind.Faulted)
  | 7 -> Op.WorkerReady pick
  | 8 -> Op.WorkerFaulted pick
  | 9 -> Op.WorkerExited pick
  | _ -> Op.StopUnknown

/// Wait for the loop to finish everything posted before this: the loop answers in order, and a command's snapshot is
/// published (and announced) before the next command is taken.
let drained (mailbox: MailboxProcessor<SessionCommand>) =
  mailbox.PostAndReply((fun reply -> SessionCommand.ListSessions reply), patience) |> ignore

let statuses (snapshot: QuerySnapshot) : Map<SessionId, SessionLifecycleStatus> =
  snapshot.Sessions |> Map.map (fun _ info -> info.Status)

/// The sessions as a refresh would see them: every field but `LastActivity`, which each worker call stamps.
let sessionsIgnoringActivity (snapshot: QuerySnapshot) =
  snapshot.Sessions |> Map.map (fun _ info -> { info with LastActivity = System.DateTime.MinValue })

let pickOf (snapshot: QuerySnapshot) (pick: byte) : SessionId option =
  match QuerySnapshot.allSessions snapshot with
  | [] -> None
  | sessions -> Some (List.item (int pick % sessions.Length) sessions).Id

let create (mailbox: MailboxProcessor<SessionCommand>) (target: SessionProjectTarget) (dir: string) : Result<SessionInfo, SageFsError> =
  mailbox.PostAndReply(
    (fun reply -> SessionCommand.CreateSession([ target ], dir, true, WorkflowTypes.SessionWorkflow.Interactive, reply)),
    patience)

/// Runs the op against the manager. A command aimed at a session that does not exist is skipped, and a stop of an
/// unknown id is refused by the manager and must change nothing.
let apply (mailbox: MailboxProcessor<SessionCommand>) (read: unit -> QuerySnapshot) (created: int ref) (op: Op) : unit =
  let onSession (pick: byte) (command: SessionId -> SessionCommand) =
    match pickOf (read ()) pick with
    | Some id -> mailbox.Post(command id)
    | None -> ()
  let nextDir () =
    created.Value <- created.Value + 1
    sprintf "/history/%d" created.Value
  match op with
  | Op.CreateBare -> create mailbox SessionProjectTarget.Bare (nextDir ()) |> ignore
  | Op.CreateProject -> create mailbox (SessionProjectTarget.Project "Test.fsproj") (nextDir ()) |> ignore
  | Op.Stop pick ->
    match pickOf (read ()) pick with
    | Some id -> mailbox.PostAndReply((fun reply -> SessionCommand.StopSession(id, reply)), patience) |> ignore
    | None -> ()
  | Op.Touch pick -> onSession pick SessionCommand.TouchSession
  | Op.SetStatus(pick, kind) -> onSession pick (fun id -> SessionCommand.UpdateSessionStatus(id, statusOf kind))
  | Op.WorkerReady pick -> onSession pick (fun id -> SessionCommand.WorkerReportedReady(id, ownPid, []))
  | Op.WorkerFaulted pick -> onSession pick (fun id -> SessionCommand.WorkerReportedFaulted(id, ownPid, "history"))
  | Op.WorkerExited pick -> onSession pick (fun id -> SessionCommand.WorkerExited(id, ownPid, 0))
  | Op.StopUnknown ->
    mailbox.PostAndReply((fun reply -> SessionCommand.StopSession(SessionId.newId (), reply)), patience) |> ignore

/// What one op did, as the test judges it.
type Step =
  { Op: string
    SessionsChanged: bool
    StatusChanged: bool
    Announcements: int }

/// A manager with a fake runtime and a counter on `OnSessionsChanged`, handed to `run` and torn down after.
let withCountingManager (run: MailboxProcessor<SessionCommand> -> (unit -> QuerySnapshot) -> (unit -> int) -> 'a) : 'a =
  use cancellation = new CancellationTokenSource()
  let announced = ref 0
  let mailbox, read =
    createWithAlarm
      runtime
      cancellation.Token
      { SessionManagerCallbacks.silent with OnSessionsChanged = fun () -> Interlocked.Increment(&announced.contents) |> ignore }
  try run mailbox read (fun () -> announced.Value)
  finally cancellation.Cancel()

let runHistory (history: (byte * byte) list) : Step list =
  withCountingManager <| fun mailbox read announced ->
    let created = ref 0
    drained mailbox
    history
    |> List.map (fun (kind, pick) ->
      let op = opOf kind pick
      let before = read ()
      let heardBefore = announced ()
      apply mailbox read created op
      drained mailbox
      let after = read ()
      { Op = sprintf "%A" op
        SessionsChanged = sessionsIgnoringActivity before <> sessionsIgnoringActivity after
        StatusChanged = statuses before <> statuses after
        Announcements = announced () - heardBefore })

let stoppedInfo (id: SessionId) : SessionInfo =
  { Id = id; Name = None; Projects = []; WorkingDirectory = "/w"; SolutionRoot = None
    CreatedAt = System.DateTime.UtcNow; LastActivity = System.DateTime.UtcNow
    Status = SessionLifecycleStatus.Stopped; Workflow = WorkflowTypes.SessionWorkflow.Interactive
    ActiveProject = None; ProjectRoles = []; App = AppRun.AppRunState.NotRunning
    Rebuild = LastRebuild.NeverRebuilt; Reload = SessionReload.NoReloadYet; Freshness = ReplFreshness.InSync }

[<Tests>]
let sessionSnapshotChangeTests =
  testList "SessionManager announces a changed session list" [
    testProperty "WHY — every status transition is announced exactly once, and a command that changed nothing announces nothing" <| fun (history: (byte * byte) list) ->
      runHistory history
      |> List.iter (fun step ->
        match step.StatusChanged, step.SessionsChanged with
        | true, _ -> step.Announcements |> Expect.equal (sprintf "the status change by %s is announced once" step.Op) 1
        | false, true -> step.Announcements |> Expect.equal (sprintf "the session change by %s is announced once" step.Op) 1
        | false, false -> step.Announcements |> Expect.equal (sprintf "%s changed nothing, so it announces nothing" step.Op) 0)

    testCase "WHY — a subscriber that throws is logged and does not reach the loop that published" <| fun _ ->
      let id = SessionId.newId ()
      let one = Map.ofList [ id, stoppedInfo id ]
      SnapshotChange.announce (fun () -> failwith "the subscriber broke") Map.empty one
      SnapshotChange.between Map.empty one |> Expect.equal "and the change itself is still seen" SnapshotChange.SessionsChanged

    testCase "WHY — a worker call's activity stamp is not a change, because it would refresh the list on every eval and every as-you-type check" <| fun _ ->
      withCountingManager <| fun mailbox _ announced ->
        match create mailbox SessionProjectTarget.Bare "/touched" with
        | Error err -> failtestf "create failed: %s" (SageFsError.describe err)
        | Ok info ->
          drained mailbox
          let heard = announced ()
          for _ in 1..5 do
            mailbox.Post(SessionCommand.TouchSession info.Id)
          drained mailbox
          announced () - heard |> Expect.equal "five touches announce nothing" 0

    testCase "WHY — a session that becomes Ready on its own is announced, with no caller asking for the list" <| fun _ ->
      withCountingManager <| fun mailbox read announced ->
        match create mailbox SessionProjectTarget.Bare "/own" with
        | Error err -> failtestf "create failed: %s" (SageFsError.describe err)
        | Ok info ->
          drained mailbox
          let heard = announced ()
          mailbox.Post(SessionCommand.WorkerReportedReady(info.Id, ownPid, []))
          drained mailbox
          let session = (read ()).Sessions |> Map.find info.Id
          SessionLifecycleStatus.label session.Status |> Expect.equal "the worker's report made it Ready" "Ready"
          announced () - heard |> Expect.equal "and the change was announced once" 1
  ]
