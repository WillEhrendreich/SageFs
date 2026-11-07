module SageFs.Tests.SessionManagerRemovedTests

open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open Expecto
open Expecto.Flip
open SageFs
open SageFs.SessionManager
open SageFs.WorkerProtocol

// `OnSessionRemoved` is how the daemon drops what it keeps per session (`SessionOwners`), so it has
// to fire on every way a session leaves the registry. Each route that stops a session ends in one of these commands. The
// real mailbox runs with a fake runtime whose stop returns at once. The hook runs once a command is done, so each case
// asks the loop one more thing first: the loop answers in order, so that answer means the hook has run.

let private runtime : SessionManagerRuntime =
  { StartWorkerProcess =
      fun _ _ _ _ _ _ -> Ok ({ Process = Process.GetCurrentProcess(); AdoptedCore = None } : SessionManager.SpawnedWorker)
    AwaitWorkerPort = fun _ _ _ _ _ -> ()
    Ledger = StartLedger.closed
    StopWorker = fun _ -> async { return () }
    RunBuildAsync = fun _ _ -> async { return Ok "build ok" } }

let private patience = int TestTimeouts.patience.TotalMilliseconds

let private withManager (run: MailboxProcessor<SessionCommand> -> ConcurrentQueue<SessionId> -> unit) =
  use cancellation = new CancellationTokenSource()
  let removed = ConcurrentQueue<SessionId>()
  let mailbox, _ =
    createWithAlarm runtime cancellation.Token { SessionManagerCallbacks.silent with OnSessionRemoved = removed.Enqueue }
  try run mailbox removed
  finally cancellation.Cancel()

let private createSession (mailbox: MailboxProcessor<SessionCommand>) : SessionInfo =
  let created =
    mailbox.PostAndReply(
      (fun reply ->
        SessionCommand.CreateSession([ SessionProjectTarget.Project "Test.fsproj" ], "/test", true, WorkflowTypes.SessionWorkflow.Interactive, reply)),
      patience)
  match created with
  | Ok info -> info
  | Error err -> failtestf "create session failed: %s" (SageFsError.describe err)

let private sid (n: byte) : SessionId =
  match SessionId.validate (sprintf "%08x" (int n)) with
  | Ok id -> id
  | Error reason -> failtestf "fixture session id: %s" reason

let private registry (ns: byte seq) : Map<SessionId, unit> = ns |> Seq.map (fun n -> sid n, ()) |> Map.ofSeq

/// Wait for the loop to finish everything posted before this.
let private drained (mailbox: MailboxProcessor<SessionCommand>) =
  mailbox.PostAndReply((fun reply -> SessionCommand.ListSessions reply), patience) |> ignore

[<Tests>]
let sessionManagerRemovedTests =
  testList "SessionManager session removed" [
    testProperty "WHY — the hook hears exactly the sessions a command took out, each once" <| fun (before: Set<byte>) (after: Set<byte>) ->
      let heard = ResizeArray<SessionId>()
      SessionRemoval.report heard.Add (registry before) (registry after)
      List.sort (List.ofSeq heard) = List.sort (Set.difference before after |> Set.toList |> List.map sid)

    testCase "WHY — a hook that throws is logged and the next removal is still told, so a bad subscriber cannot stop the loop" <| fun _ ->
      let heard = ResizeArray<SessionId>()
      let breaks (id: SessionId) = heard.Add id; failwith "the subscriber broke"
      SessionRemoval.report breaks (registry [ 1uy; 2uy ]) Map.empty
      heard.Count |> Expect.equal "both removals were reported" 2

    testCase "WHY — a stop reports the session removed, so the daemon releases it whichever route stopped it" <| fun _ ->
      withManager <| fun mailbox removed ->
        let info = createSession mailbox
        drained mailbox
        removed |> Expect.isEmpty "creating a session removes nothing"
        match mailbox.PostAndReply((fun reply -> SessionCommand.StopSession(info.Id, reply)), patience) with
        | Ok () -> ()
        | Error err -> failtestf "stop failed: %s" (SageFsError.describe err)
        drained mailbox
        removed |> List.ofSeq |> Expect.equal "the stopped session, once" [ info.Id ]

    testCase "WHY — a worker that exits cleanly removes its session, and that is a removal too" <| fun _ ->
      withManager <| fun mailbox removed ->
        let info = createSession mailbox
        let pid = Process.GetCurrentProcess().Id
        mailbox.Post(SessionCommand.WorkerExited(info.Id, pid, 0))
        mailbox.PostAndReply((fun reply -> SessionCommand.GetSession(info.Id, reply)), patience)
        |> Expect.isNone "a clean exit takes the session out of the registry"
        removed |> List.ofSeq |> Expect.equal "the exited worker's session, once" [ info.Id ]

    testCase "WHY — a stop of a session the registry does not hold removes nothing" <| fun _ ->
      withManager <| fun mailbox removed ->
        let unknown = SessionId.newId ()
        match mailbox.PostAndReply((fun reply -> SessionCommand.StopSession(unknown, reply)), patience) with
        | Ok () -> failtest "a stop of an unknown session should be refused"
        | Error _ -> ()
        drained mailbox
        removed |> Expect.isEmpty "nothing was in the registry to remove"
  ]
