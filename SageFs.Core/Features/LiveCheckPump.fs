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

module LiveCheckPump =
  let initial<'req> : PumpState<'req> =
    { Queue = []; Flight = Flight.Idle; Awaiting = Awaiting.NotAwaiting; NextWait = 1L }

  /// Fold one event. `supersedes newer older` says whether a request makes an older queued one pointless.
  /// Total: every state answers every event.
  let step
    (supersedes: 'req -> 'req -> bool)
    (state: PumpState<'req>)
    (event: PumpEvent<'req, 'reply>)
    : PumpState<'req> * PumpEffect<'req, 'reply> list =
    match event with
    | PumpEvent.Requested (request, WorkerView.Serving pid) ->
      { state with Flight = Flight.InFlight (request, pid) }, [ PumpEffect.Ask (request, pid) ]
    | PumpEvent.Requested (_, _) -> state, []
    | PumpEvent.WorkerAnswered (WorkerReply.Replied reply, _) ->
      match state.Flight with
      | Flight.InFlight (request, _) -> { state with Flight = Flight.Idle }, [ PumpEffect.Deliver (request, PumpOutcome.Answered reply) ]
      | Flight.Idle -> state, []
    | PumpEvent.WorkerAnswered (WorkerReply.Silent why, _) ->
      match state.Flight with
      | Flight.InFlight (request, _) ->
        { state with Flight = Flight.Idle }, [ PumpEffect.Deliver (request, PumpOutcome.Unanswered (UnansweredWhy.WorkerSilent why)) ]
      | Flight.Idle -> state, []
    | PumpEvent.WorkerSeen _
    | PumpEvent.WaitDeadlineReached _ -> state, []
