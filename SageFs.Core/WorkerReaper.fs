namespace SageFs

open System
open System.Diagnostics
open SageFs.Utils
open SageFs.WorkerProtocol

/// ROLE: a stopped session's workers leave on a side thread, and the manager keeps track of them until they are gone.
///   A stop used to run `StopWorker` inside the session manager's one mailbox loop. The worker never leaves inside
///   the 2 + 3 s it is given (Timeouts.fs measures 5.1 to 5.7 s on every machine), so every list, create, restart
///   and other stop waited behind it, and four agents stopping a session each queued to about 22 s (roast R-SERVER-3).
///   Now the loop drops the session from the registry, answers at once, and hands the workers to `reapOnDedicatedThreads`;
///   each worker's fate comes back to the loop as a message.
/// WHAT "STOPPED" MEANS: the registry no longer lists the session. WHAT "WORKER GONE" MEANS: the book below no longer
///   holds its pid. A caller that needs the second (purge deleting the manifest) parks on `await`, which the loop
///   answers when the last pid leaves, and never on the mailbox.
/// Assumes: a pid stays in the book until its process is seen gone, so the hygiene sweep and the daemon's own pid
///   list never lose track of a worker that is still running. A worker that outlives its stop AND the kill stays as a
///   survivor, named, instead of vanishing from the books.
module WorkerReaper =

  /// A worker the manager has told to leave: its pid (read before the stop disposes the handle), the handle itself for
  /// the force kill, and the stop to run.
  type Target =
    { Pid: int
      Process: Process
      Stop: Async<unit> }

  /// What became of one worker once its stop had run.
  [<RequireQualifiedAccess>]
  type Fate =
    | Left
    | Outlived of reason: string

  /// The workers of one stopped session that the manager is still watching, and the callers waiting to hear.
  type Reaping<'waiter> =
    { Exiting: int list
      Survivors: int list
      Waiters: 'waiter list }

  /// Every stopped session whose workers are not all gone yet. Not in the registry, which no longer lists them.
  type Book<'waiter> = Map<SessionId, Reaping<'waiter>>

  /// What a waiting caller is told once nothing is exiting any more.
  [<RequireQualifiedAccess>]
  type Settled =
    | AllGone
    | Survivors of pids: int list
    /// The manager failed while it was answering, so it cannot say. Never read as gone.
    | Unknown of reason: string

  [<RequireQualifiedAccess>]
  type Standing =
    | StillExiting of pids: int list
    | Done of Settled

  let empty<'waiter> : Book<'waiter> = Map.empty

  let nothing<'waiter> : Reaping<'waiter> = { Exiting = []; Survivors = []; Waiters = [] }

  /// The workers of `id` start leaving. A second stop of the same session adds to what is already exiting.
  let start (id: SessionId) (pids: int list) (book: Book<'waiter>) : Book<'waiter> =
    let current = Map.tryFind id book |> Option.defaultValue nothing
    Map.add id { current with Exiting = current.Exiting @ pids } book

  let standingOf (reaping: Reaping<'waiter>) : Standing =
    match reaping.Exiting, reaping.Survivors with
    | _ :: _, _ -> Standing.StillExiting reaping.Exiting
    | [], [] -> Standing.Done Settled.AllGone
    | [], survivors -> Standing.Done (Settled.Survivors survivors)

  /// Where the workers of `id` stand. A session never reaped has nothing left to wait for.
  let standing (id: SessionId) (book: Book<'waiter>) : Standing =
    match Map.tryFind id book with
    | Some reaping -> standingOf reaping
    | None -> Standing.Done Settled.AllGone

  /// A caller wants to know when `id`'s workers are gone. Answered now when they already are; parked otherwise.
  let await (id: SessionId) (waiter: 'waiter) (book: Book<'waiter>) : Book<'waiter> * Standing =
    match Map.tryFind id book with
    | Some reaping ->
      match standingOf reaping with
      | Standing.StillExiting _ as pending -> Map.add id { reaping with Waiters = waiter :: reaping.Waiters } book, pending
      | Standing.Done _ as settled -> book, settled
    | None -> book, Standing.Done Settled.AllGone

  let removeFirst (pid: int) (pids: int list) : int list =
    match List.tryFindIndex ((=) pid) pids with
    | Some index -> List.removeAt index pids
    | None -> pids

  /// One worker's fate arrives. Returns the book and the waiters to answer (none until the last pid has left). A fate
  /// for a pid the book does not hold changes nothing, so a duplicate report cannot settle a session early.
  let finish (id: SessionId) (pid: int) (fate: Fate) (book: Book<'waiter>) : Book<'waiter> * ('waiter * Settled) list =
    match Map.tryFind id book with
    | Some reaping when List.contains pid reaping.Exiting ->
      let survivors =
        match fate with
        | Fate.Left -> reaping.Survivors
        | Fate.Outlived _ -> pid :: reaping.Survivors
      let after = { reaping with Exiting = removeFirst pid reaping.Exiting; Survivors = survivors }
      match standingOf after with
      | Standing.StillExiting _ -> Map.add id after book, []
      | Standing.Done settled ->
        let answers = after.Waiters |> List.rev |> List.map (fun waiter -> waiter, settled)
        let rest = { after with Waiters = [] }
        match rest.Survivors with
        | [] -> Map.remove id book, answers
        | _ -> Map.add id rest book, answers
    | Some _ | None -> book, []

  /// Tell a caller its answer. A caller that stopped waiting (its bound passed) must not break the loop that answers it.
  let answer (reply: AsyncReplyChannel<Settled>) (settled: Settled) : unit =
    try reply.Reply settled with ex -> Log.warn "[WorkerReaper] a waiting caller could not be answered (continuing): %s" ex.Message

  let answerAll (answers: (AsyncReplyChannel<Settled> * Settled) list) : unit =
    for (reply, settled) in answers do answer reply settled

  /// A caller whose workers are already gone hears at once; one whose workers are still exiting waits in the book.
  let answerIfSettled (reply: AsyncReplyChannel<Settled>) (standing: Standing) : unit =
    match standing with
    | Standing.Done settled -> answer reply settled
    | Standing.StillExiting _ -> ()

  /// Every pid the manager is still watching: leaving, or survived the kill. For the hygiene sweep to see.
  let watchedPids (book: Book<'waiter>) : int list =
    book |> Map.toList |> List.collect (fun (_, reaping) -> reaping.Exiting @ reaping.Survivors)

  // -- The side thread --

  let pidOf (proc: Process) : int = try proc.Id with _ -> -1

  let targetOf (proc: Process) (stop: Async<unit>) : Target = { Pid = pidOf proc; Process = proc; Stop = stop }

  /// Is a process with this pid still running? Asked of the OS by pid, because the stop disposes the handle.
  let isRunning (pid: int) : bool =
    try
      use found = Process.GetProcessById pid
      not found.HasExited
    with _ -> false

  let fateOf (pid: int) : Fate =
    match isRunning pid with
    | true -> Fate.Outlived "the process was still running after its stop ended"
    | false -> Fate.Left

  /// A stop that threw leaves the worker's state unknown. The force-kill decision (issue #154) fails closed: kill unless
  /// the handle positively says the process exited.
  let forceKill (proc: Process) : unit =
    match WorkerStop.needsForceKill (fun () -> proc.HasExited) with
    | WorkerStop.ForceKill -> WorkerStop.killTree proc
    | WorkerStop.AlreadyExited -> ()

  /// One worker's whole retirement: its stop, the force kill when the stop itself threw, then what became of it.
  let reapOne (report: int -> Fate -> unit) (target: Target) : Async<unit> = async {
    let! stopped = Async.Catch target.Stop
    match stopped with
    | Choice1Of2 () -> ()
    | Choice2Of2 ex ->
      Log.warn "[WorkerReaper] stopping worker pid %d threw (%s); force-killing it" target.Pid ex.Message
      forceKill target.Process
    let fate = fateOf target.Pid
    match fate with
    | Fate.Left -> ()
    | Fate.Outlived reason -> Log.error "[WorkerReaper] worker pid %d is still running: %s. It stays in the reaping set." target.Pid reason
    report target.Pid fate
  }

  /// Hand each worker to its own dedicated thread (the pool can starve under memory pressure, which is when workers
  /// leak). The workers leave in parallel; `report` is called once per worker with what became of it.
  let reapOnDedicatedThreads (id: SessionId) (report: int -> Fate -> unit) (targets: Target list) : unit =
    for target in targets do
      WorkerReadyCommit.retireOnDedicatedThread (reapOne report) id target

  // -- What a caller is told --

  /// The bound on a stop's round trip to the mailbox has been spent. Since a stop no longer waits for its worker, this
  /// means the loop itself is not draining, and says so.
  let mailboxStuckMessage (bound: TimeSpan) : string =
    sprintf
      "The session supervisor did not respond within %.0fs. The daemon's session mailbox may be stuck — check the daemon log. (SAGEFS_STOP_SESSION_TIMEOUT_SECONDS to adjust)"
      bound.TotalSeconds

  /// The stop was accepted: the session is no longer registered, and its worker is leaving in the background.
  let stoppedMessage (sessionId: string) : string =
    sprintf "Session '%s' stopped. Its worker process is exiting in the background and is tracked until it is gone." sessionId

  /// What purge says once it has waited for the workers.
  let purgedMessage (sessionId: string) (settled: Settled) : string =
    match settled with
    | Settled.AllGone -> sprintf "Session '%s' purged — manifest entry removed, worker gone." sessionId
    | Settled.Survivors pids ->
      sprintf "Session '%s' purged — manifest entry removed, but worker pid%s %s still running after the kill and tracked." sessionId (match pids with [ _ ] -> "" | _ -> "s") (pids |> List.map string |> String.concat ", ")
    | Settled.Unknown reason -> sprintf "Session '%s' purged — manifest entry removed. Whether its worker is gone is unknown: %s" sessionId reason
