namespace SageFs

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open SageFs.Utils
open SageFs.WorkerProtocol

/// A throwaway worker the daemon built for one confirmation, and where it answers.
type ThrowawayWorker =
  { WorkerProcessId: int
    BaseUrl: string }

/// Whether the session's worker opens the project's namespaces when it warms up.
[<RequireQualifiedAccess>]
type NamespaceOpening =
  | AutoOpened
  | LeftClosed

/// What a throwaway worker's start needs to know about a session.
type ThrowawaySpec =
  { Targets: SessionProjectTarget list
    WorkingDir: string
    Opening: NamespaceOpening }

/// A throwaway worker that has been started and may not answer yet. The process is behind the two functions, so the
/// owner (and a test that puts a fake world under it) never touches one.
type StartedThrowaway =
  { ProcessId: int
    /// Wait until the worker answers and is Ready: its base URL, or why it will not.
    AwaitReady: Async<Result<string, string>>
    /// Stop the worker and what it holds. The argument is the worker's URL, empty when it never came up, so only the
    /// process is left to stop.
    Retire: string -> Async<unit> }

/// Everything the owner of the throwaway workers does to the world, as functions, so a test can put a fake world under
/// the real owner.
type ThrowawayPorts =
  { /// How to start a worker for this session. None when the session is gone.
    SpecOf: SessionId -> Async<ThrowawaySpec option>
    /// The build the confirmation is about. The session's own worker is not involved.
    Build: ThrowawaySpec -> Async<Result<string, SageFsError>>
    /// Start the worker. Returns at once, before the worker reports a port.
    Spawn: SessionId -> ThrowawaySpec -> Result<StartedThrowaway, SageFsError> }

/// The one owner of this daemon's throwaway workers. `ThrowawayBook` (pure, DST-backed) decides; this holds the
/// processes and performs the effects.
type ConfirmWorkerOwner =
  { /// A throwaway worker for this session's confirmation of this generation: built, started and Ready, or why not.
    /// Answers every ask: a generation that ends, is replaced, or whose session goes before the worker is up is told so.
    Want: SessionId -> int64 -> Async<Result<ThrowawayWorker, string>>
    /// This generation is over. Its worker is retired, or the caller still waiting for it is told.
    Ended: SessionId -> int64 -> unit
    /// The session is gone, and its throwaway worker goes with it.
    SessionGone: SessionId -> unit
    /// The pids of the throwaway workers that are up.
    Held: unit -> Async<int list> }

module ConfirmWorkers =

  type Msg =
    | Want of SessionId * generation: int64 * AsyncReplyChannel<Result<ThrowawayWorker, string>>
    | Book of ThrowawayBook.Event
    /// The worker for this generation is Ready, and this is how to stop it.
    | Up of SessionId * generation: int64 * StartedThrowaway * baseUrl: string
    | Held of AsyncReplyChannel<int list>

  type State =
    { Book: ThrowawayBook.Book
      Waiting: Map<SessionId * int64, AsyncReplyChannel<Result<ThrowawayWorker, string>>>
      Running: Map<int, StartedThrowaway * string> }

  /// One start, off the owner's loop: build, spawn, wait for Ready. Whatever happens it posts exactly one message, and a
  /// worker that was spawned and then failed to come up is stopped here, because the book never hears of it.
  let startJob (ports: ThrowawayPorts) (post: Msg -> unit) (session: SessionId) (generation: int64) : Async<unit> =
    let fail (reason: string) = post (Msg.Book (ThrowawayBook.Event.StartFailed (session, generation, reason)))
    let work =
      async {
        match! ports.SpecOf session with
        | None -> fail "the session is gone"
        | Some spec ->
          match! ports.Build spec with
          | Error err -> fail (SageFsError.describe err)
          | Ok _ ->
            match ports.Spawn session spec with
            | Error err -> fail (SageFsError.describe err)
            | Ok started ->
              match! started.AwaitReady |> Async.Catch with
              | Choice1Of2 (Ok baseUrl) -> post (Msg.Up (session, generation, started, baseUrl))
              | Choice1Of2 (Error reason) ->
                do! started.Retire ""
                fail reason
              | Choice2Of2 ex ->
                do! started.Retire ""
                fail ex.Message
      }
    // Whatever throws before the worker is handed over is a failed start, said once.
    async {
      match! work |> Async.Catch with
      | Choice1Of2 () -> ()
      | Choice2Of2 ex -> fail ex.Message
    }

  /// Carry out what the book decided: start a worker, retire one, answer the caller waiting for one.
  let perform (ports: ThrowawayPorts) (post: Msg -> unit) (state: State) (effects: ThrowawayBook.Effect list) : State =
    effects
    |> List.fold
      (fun current effect ->
        match effect with
        | ThrowawayBook.Effect.Start (session, generation) ->
          Async.Start (startJob ports post session generation)
          current
        | ThrowawayBook.Effect.Retire handle ->
          match Map.tryFind handle current.Running with
          | Some (started, baseUrl) ->
            Log.info "[confirm] retiring throwaway worker %d" handle
            Async.Start (started.Retire baseUrl)
            { current with Running = Map.remove handle current.Running }
          | None -> current
        | ThrowawayBook.Effect.Serve (session, generation, handle) ->
          match Map.tryFind (session, generation) current.Waiting, Map.tryFind handle current.Running with
          | Some reply, Some (_, baseUrl) ->
            reply.Reply (Ok { WorkerProcessId = handle; BaseUrl = baseUrl })
            { current with Waiting = Map.remove (session, generation) current.Waiting }
          | Some reply, None ->
            reply.Reply (Error "the throwaway worker was not recorded")
            { current with Waiting = Map.remove (session, generation) current.Waiting }
          | None, _ -> current
        | ThrowawayBook.Effect.Fail (session, generation, reason) ->
          match Map.tryFind (session, generation) current.Waiting with
          | Some reply ->
            reply.Reply (Error reason)
            { current with Waiting = Map.remove (session, generation) current.Waiting }
          | None -> current)
      state

  let emptyState : State = { Book = ThrowawayBook.empty; Waiting = Map.empty; Running = Map.empty }

  /// Fold one message: the book decides, the effects are performed.
  let handle (ports: ThrowawayPorts) (post: Msg -> unit) (state: State) (message: Msg) : State =
    let fold (current: State) (event: ThrowawayBook.Event) =
      let book, effects = ThrowawayBook.step current.Book event
      perform ports post { current with Book = book } effects
    match message with
    | Want (session, generation, reply) ->
      // The same generation asked twice leaves the first caller unanswered, so it is told.
      let waiting =
        match Map.tryFind (session, generation) state.Waiting with
        | Some earlier ->
          earlier.Reply (Error "the same confirmation asked again")
          Map.remove (session, generation) state.Waiting
        | None -> state.Waiting
      fold { state with Waiting = Map.add (session, generation) reply waiting } (ThrowawayBook.Event.Wanted (session, generation))
    | Book event -> fold state event
    | Up (session, generation, started, baseUrl) ->
      fold { state with Running = Map.add started.ProcessId (started, baseUrl) state.Running } (ThrowawayBook.Event.Started (session, generation, started.ProcessId))
    | Held reply ->
      reply.Reply (state.Running |> Map.toList |> List.map fst)
      state

  /// Start the owner. One mailbox: every decision about a throwaway worker is made on it, in order.
  let start (ports: ThrowawayPorts) : ConfirmWorkerOwner =
    let mailbox =
      MailboxProcessor<Msg>.Start (fun inbox ->
        let rec loop (state: State) =
          async {
            let! message = inbox.Receive ()
            return! loop (handle ports inbox.Post state message)
          }
        loop emptyState)
    { Want = fun session generation -> mailbox.PostAndAsyncReply (fun reply -> Msg.Want (session, generation, reply))
      Ended = fun session generation -> mailbox.Post (Msg.Book (ThrowawayBook.Event.Ended (session, generation)))
      SessionGone = fun session -> mailbox.Post (Msg.Book (ThrowawayBook.Event.SessionGone session))
      Held = fun () -> mailbox.PostAndAsyncReply Msg.Held }

  /// A worker that has not come up, or is being retired: stop the process tree, or ask the worker to shut down first.
  let retire (proc: Process) (baseUrl: string) : Async<unit> =
    match baseUrl with
    | "" ->
      async {
        WorkerStop.killTree proc
        proc.Dispose ()
      }
    | url -> WorkerStop.stopProcess proc (HttpWorkerClient.httpProxy url)

  /// What the worker process holds in memory now, in megabytes, for the log. 0 when it cannot be read.
  let workingSetMb (proc: Process) : int64 =
    try proc.WorkingSet64 / 1048576L
    with :? InvalidOperationException -> 0L

  /// Wait for a started worker to report its port and then to be Ready, bounded by the confirmation's own deadline.
  let awaitReady (ledger: Ledger) (session: SessionId) (proc: Process) : Async<Result<string, string>> =
    async {
      let port = TaskCompletionSource<Result<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously)
      WorkerStartup.await ledger proc CancellationToken.None (StartTimeoutDecision.firstStartBudget ledger)
        { OnProgress = ignore
          OnReady = (fun _ url -> port.TrySetResult (Ok url) |> ignore)
          OnSpawnFailed = (fun _ reason -> port.TrySetResult (Error reason) |> ignore)
          OnStartTimedOut = (fun _ _ tail -> port.TrySetResult (Error (sprintf "the throwaway worker went silent while starting: %s" tail)) |> ignore)
          OnAppOutput = fun _ _ -> () }
      match! Async.AwaitTask port.Task with
      | Error reason -> return Error reason
      | Ok url ->
        let ready = TaskCompletionSource<Result<unit, string>>(TaskCreationOptions.RunContinuationsAsynchronously)
        use watching = new CancellationTokenSource ()
        Async.Start (
          WorkerPostReady.watchUntilReady
            (SessionId.value session)
            (HttpWorkerClient.httpProxy url)
            watching.Token
            { OnReady = (fun _ -> ready.TrySetResult (Ok ()) |> ignore)
              OnFaulted = (fun reason -> ready.TrySetResult (Error reason) |> ignore)
              OnTimedOut = (fun reason -> ready.TrySetResult (Error reason) |> ignore) },
          watching.Token)
        let! outcome = RebuildReadyWait.await (Async.AwaitTask ready.Task) Timeouts.liveTestConfirmationDeadline CancellationToken.None
        watching.Cancel ()
        match outcome with
        | RebuildReadyWait.Outcome.Ready ->
          Log.info "[confirm] throwaway worker pid %d for session %s is Ready (working set %d MB)" proc.Id (SessionId.value session) (workingSetMb proc)
          return Ok url
        | RebuildReadyWait.Outcome.Failed reason -> return Error reason
        | RebuildReadyWait.Outcome.DeadlineReached -> return Error "the throwaway worker was not Ready before the confirmation's deadline"
        | RebuildReadyWait.Outcome.Cancelled -> return Error "the wait for the throwaway worker was cancelled"
    }

  /// The real world: the session's own spawn spec, its own build, a worker started the way the session manager starts one.
  /// The throwaway runs the Interactive workflow: it only runs tests, so it must not watch files or start an app.
  let productionPorts (ledger: Ledger) (getSession: SessionId -> Async<SessionManager.ManagedSession option>) : ThrowawayPorts =
    { SpecOf =
        fun session ->
          async {
            let! found = getSession session
            return
              found
              |> Option.map (fun managed ->
                { Targets = managed.Targets
                  WorkingDir = managed.WorkingDir
                  Opening = (match managed.AutoOpenNamespaces with | true -> NamespaceOpening.AutoOpened | false -> NamespaceOpening.LeftClosed) })
          }
      Build = fun spec -> SessionBuild.answeringAlways (fun () -> SessionBuild.runBuildAsync (SessionProjectTarget.paths spec.Targets) spec.WorkingDir)
      Spawn =
        fun session spec ->
          WorkerSpawn.startWorkerProcess session spec.Targets spec.WorkingDir (spec.Opening = NamespaceOpening.AutoOpened) WorkflowTypes.SessionWorkflow.Interactive (fun _ _ -> ())
          |> Result.map (fun spawned ->
            { ProcessId = spawned.Process.Id
              AwaitReady = awaitReady ledger session spawned.Process
              Retire = retire spawned.Process }) }

  /// What the daemon has seen go into each worker's session, by session: hand evals and evaluated buffers. A swap
  /// drops all of it.
  type HandWorkStore() =
    let ledger = ConcurrentDictionary<SessionId, SessionHandWork.ForWorker>()

    /// Count what a message put into the worker with this pid. A reset empties the session, so its entry goes.
    member _.Note (session: SessionId) (workerPid: int) (put: WorkPut) : unit =
      match put with
      | WorkPut.HandEval ->
        ledger.AddOrUpdate (session, (fun _ -> SessionHandWork.noteHandEvalFor workerPid None), (fun _ existing -> SessionHandWork.noteHandEvalFor workerPid (Some existing)))
        |> ignore
      | WorkPut.Buffer file ->
        ledger.AddOrUpdate (session, (fun _ -> SessionHandWork.noteBufferFor workerPid file None), (fun _ existing -> SessionHandWork.noteBufferFor workerPid file (Some existing)))
        |> ignore
      | WorkPut.DebugHold ->
        let at = System.DateTimeOffset.UtcNow
        ledger.AddOrUpdate (session, (fun _ -> SessionHandWork.noteDebugHoldFor workerPid at None), (fun _ existing -> SessionHandWork.noteDebugHoldFor workerPid at (Some existing)))
        |> ignore
      | WorkPut.DebugReleased ->
        ledger.AddOrUpdate (session, (fun _ -> SessionHandWork.noteDebugReleasedFor workerPid None), (fun _ existing -> SessionHandWork.noteDebugReleasedFor workerPid (Some existing)))
        |> ignore
      | WorkPut.Cleared -> ledger.TryRemove session |> ignore
      | WorkPut.Nothing -> ()

    /// What the worker with this pid holds. Nothing for a worker the store has not seen. A debug hold nobody has mentioned for
    /// the hold's own length (`Timeouts.debugHold`) is not counted: the host dropped it.
    member _.HeldBy (session: SessionId) (workerPid: int) : SessionHandWork.Held =
      match ledger.TryGetValue session with
      | true, known when known.WorkerPid = workerPid -> SessionHandWork.unlessLapsed System.DateTimeOffset.UtcNow Timeouts.debugHold known.Held
      | true, _
      | false, _ -> SessionHandWork.nothing

    member _.Forget (session: SessionId) : unit = ledger.TryRemove session |> ignore

  /// What every route into a worker has put into it, for the whole daemon. A session id is unique and a swap changes the
  /// pid the count belongs to, so one store serves every session.
  let handWork : HandWorkStore = HandWorkStore()

  /// The owners this process has started, so a session leaving the registry (stopped by any route) reaches them without
  /// the callback that reports it knowing which runtime built which owner.
  let owners : ConcurrentBag<ConfirmWorkerOwner> = ConcurrentBag<ConfirmWorkerOwner>()

  /// Record an owner so `sessionRemoved` reaches it. Returns it, so a daemon registers where it starts one.
  let register (owner: ConfirmWorkerOwner) : ConfirmWorkerOwner =
    owners.Add owner
    owner

  /// The registry no longer lists this session: whatever it held in a throwaway worker goes, and so does the count of
  /// what its worker held.
  let sessionRemoved (session: SessionId) : unit =
    handWork.Forget session
    for owner in owners do
      owner.SessionGone session

  /// Wrap a session's proxy so what its messages put into the worker is counted against the worker the snapshot says
  /// serves. A session with no worker is left as it is: there is nothing to count against.
  let notingWorkOf (snapshot: SessionManager.QuerySnapshot) (session: SessionId) (proxy: SessionProxy) : SessionProxy =
    match SessionManager.QuerySnapshot.tryGetSession session snapshot |> Option.bind (fun info -> SessionLifecycleStatus.workerPid info.Status) with
    | Some pid -> SessionProxy.notingWork (fun put -> handWork.Note session pid put) proxy
    | None -> proxy
