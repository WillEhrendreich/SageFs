namespace SageFs.Features.LiveTesting

/// A type-check or an eval of an edited buffer is a question put to the session's worker. The worker is not
/// always there to answer: a build confirmation restarts it (no proxy while the project builds, a proxy whose
/// worker is still warming afterwards), and a worker that is retired takes the question with it. This is the pure
/// decision about when to ask, what to do while nobody can answer, and which answers to believe. It folds events
/// into effects and owns no clock, no process and no state of the world: the daemon feeds it
/// (`SageFs.LiveCheckRelay`), and `SageFs.Simulation.LiveCheckPumpSim` drives the same fold under seeded chaos.
///
/// What it promises, and what the simulation checks:
///   * a request that finds no worker waits for one (event driven, never a poll) and is asked when one is Ready;
///   * a worker that is not Ready is never asked: a worker still warming answers with errors that are not there;
///   * an answer is believed only when the worker that gave it is still the Ready worker that was asked, so
///     nothing a retired or warming worker said is ever reported as a verdict;
///   * a request whose worker went away under it is asked again, not dropped;
///   * a question that could not be answered is reported as unanswered, never as an error in the user's code;
///   * a newer request for the same thing replaces an older one that has not been answered, and an answer to
///     the older one is never reported.

/// What the session manager says about the worker a request would use.
[<RequireQualifiedAccess>]
type WorkerView =
  /// Ready, with a transport, and this is the process.
  | Serving of workerPid: int
  /// Starting, restarting or building: a worker will come (or the session will fault). A proxy that exists for a
  /// worker still warming does not make it Serving.
  | Arriving
  /// Faulted, crashed, stopped or unknown: no worker is coming.
  | Gone of reason: string

/// How a request relates to the ones queued behind it.
[<RequireQualifiedAccess>]
type Supersession =
  /// A newer request for the same thing replaces an older one that was not answered.
  | NewestWins
  /// Every request is asked, in the order it was made.
  | EveryOneRuns

/// What the call to the worker came back with.
[<RequireQualifiedAccess>]
type WorkerReply<'reply> =
  | Replied of 'reply
  /// The call threw: the worker could not be reached.
  | Silent of why: string

/// Why a request ended without an answer from a worker.
[<RequireQualifiedAccess>]
type UnansweredWhy =
  /// No worker is coming: the session faulted, crashed or stopped.
  | WorkerGone of reason: string
  /// The worker the manager calls Ready did not answer.
  | WorkerSilent of why: string
  /// No worker was Ready within the wait.
  | NoWorkerInTime

/// How a request ended.
[<RequireQualifiedAccess>]
type PumpOutcome<'reply> =
  | Answered of 'reply
  | Unanswered of UnansweredWhy

[<RequireQualifiedAccess>]
type Flight<'req> =
  | Idle
  /// This request was put to this worker and has not come back.
  | InFlight of request: 'req * workerPid: int

[<RequireQualifiedAccess>]
type Awaiting =
  | NotAwaiting
  /// Waiting for the session to be Ready, as wait `waitId`. A wake-up for any other id is stale.
  | AwaitingWorker of waitId: int64

module UnansweredWhy =
  /// The reason, in words, for a log line or a message.
  let describe (why: UnansweredWhy) : string =
    match why with
    | UnansweredWhy.WorkerGone reason -> sprintf "no worker is coming (%s)" reason
    | UnansweredWhy.WorkerSilent reason -> sprintf "the worker did not answer (%s)" reason
    | UnansweredWhy.NoWorkerInTime -> "no worker was Ready in time"

type PumpState<'req> =
  { /// Requests not yet put to a worker, oldest first.
    Queue: 'req list
    Flight: Flight<'req>
    Awaiting: Awaiting
    NextWait: int64 }

[<RequireQualifiedAccess>]
type PumpEvent<'req, 'reply> =
  /// A request, and what the manager says about the worker right now.
  | Requested of 'req * WorkerView
  /// The call in flight came back, and what the manager says about the worker right now.
  | WorkerAnswered of WorkerReply<'reply> * WorkerView
  /// The wait for a Ready session ended (it answered), and what the manager says right now.
  | WorkerSeen of waitId: int64 * WorkerView
  /// The wait went on too long.
  | WaitDeadlineReached of waitId: int64

[<RequireQualifiedAccess>]
type PumpEffect<'req, 'reply> =
  /// Put this request to the worker with this pid.
  | Ask of 'req * workerPid: int
  /// Park until the session is Ready, then raise `WorkerSeen` with this id (or `WaitDeadlineReached`).
  | AwaitWorker of waitId: int64
  /// This is how the request ended: report it.
  | Deliver of 'req * PumpOutcome<'reply>
  /// A newer request replaced this one before it was answered, so nothing will be reported for it.
  | Superseded of 'req

module LiveCheckPump =
  let initial<'req> : PumpState<'req> =
    { Queue = []; Flight = Flight.Idle; Awaiting = Awaiting.NotAwaiting; NextWait = 1L }

  /// What the queue does next, given what the manager says about the worker right now: put the oldest request to a
  /// Ready worker, park until one is Ready, or end every request because none is coming.
  let private drive
    (state: PumpState<'req>)
    (view: WorkerView)
    (effects: PumpEffect<'req, 'reply> list)
    : PumpState<'req> * PumpEffect<'req, 'reply> list =
    match state.Flight, state.Queue with
    | Flight.InFlight _, _
    | Flight.Idle, [] -> state, effects
    | Flight.Idle, oldest :: rest ->
      match view with
      | WorkerView.Serving pid ->
        { state with Queue = rest; Flight = Flight.InFlight (oldest, pid) }, effects @ [ PumpEffect.Ask (oldest, pid) ]
      | WorkerView.Arriving ->
        match state.Awaiting with
        | Awaiting.AwaitingWorker _ -> state, effects
        | Awaiting.NotAwaiting ->
          { state with Awaiting = Awaiting.AwaitingWorker state.NextWait; NextWait = state.NextWait + 1L },
          effects @ [ PumpEffect.AwaitWorker state.NextWait ]
      | WorkerView.Gone reason ->
        { state with Queue = []; Awaiting = Awaiting.NotAwaiting },
        effects @ (state.Queue |> List.map (fun request -> PumpEffect.Deliver (request, PumpOutcome.Unanswered (UnansweredWhy.WorkerGone reason))))

  /// Fold one event. `supersedes newer older` says whether a request makes an older one pointless. Total: every
  /// state answers every event, and a wake-up or a deadline for a wait that is not the one parked is no event at all.
  let step
    (supersedes: 'req -> 'req -> bool)
    (state: PumpState<'req>)
    (event: PumpEvent<'req, 'reply>)
    : PumpState<'req> * PumpEffect<'req, 'reply> list =
    match event with
    // A newer request replaces the older ones queued behind it that it supersedes, then goes to the back.
    | PumpEvent.Requested (request, view) ->
      let replaced, kept = state.Queue |> List.partition (fun queued -> supersedes request queued)
      drive { state with Queue = kept @ [ request ] } view (replaced |> List.map PumpEffect.Superseded)
    | PumpEvent.WorkerAnswered (reply, view) ->
      match state.Flight with
      | Flight.Idle -> state, []
      | Flight.InFlight (asked, pid) ->
        let idle = { state with Flight = Flight.Idle }
        let superseded = state.Queue |> List.exists (fun queued -> supersedes queued asked)
        // Only the worker that was asked, still Ready, can vouch for what it said. A worker that was retired under
        // the call, or is not Ready now, said nothing that counts, whatever the call came back with.
        match view = WorkerView.Serving pid, superseded with
        | true, true -> drive idle view [ PumpEffect.Superseded asked ]
        | true, false ->
          match reply with
          | WorkerReply.Replied answer -> drive idle view [ PumpEffect.Deliver (asked, PumpOutcome.Answered answer) ]
          // The worker the manager calls Ready did not answer: asking it again would be a loop with no event to end it.
          | WorkerReply.Silent why -> drive idle view [ PumpEffect.Deliver (asked, PumpOutcome.Unanswered (UnansweredWhy.WorkerSilent why)) ]
        | false, true -> drive idle view [ PumpEffect.Superseded asked ]
        | false, false -> drive { idle with Queue = asked :: idle.Queue } view []
    | PumpEvent.WorkerSeen (waitId, view) ->
      match state.Awaiting with
      | Awaiting.AwaitingWorker parked when parked = waitId -> drive { state with Awaiting = Awaiting.NotAwaiting } view []
      | Awaiting.AwaitingWorker _
      | Awaiting.NotAwaiting -> state, []
    | PumpEvent.WaitDeadlineReached waitId ->
      match state.Awaiting with
      | Awaiting.AwaitingWorker parked when parked = waitId ->
        { state with Queue = []; Awaiting = Awaiting.NotAwaiting },
        state.Queue |> List.map (fun request -> PumpEffect.Deliver (request, PumpOutcome.Unanswered UnansweredWhy.NoWorkerInTime))
      | Awaiting.AwaitingWorker _
      | Awaiting.NotAwaiting -> state, []
