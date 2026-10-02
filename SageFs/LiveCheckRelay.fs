namespace SageFs

open System
open System.Collections.Concurrent
open SageFs.WorkerProtocol
open SageFs.Features.LiveTesting

// ── The daemon side of LiveCheckPump ─────────────────────────────────────────────────────────────────
//
// A type-check or an eval of an edited buffer is a question for the session's worker, and the worker is
// not always there to answer: a build confirmation replaces it. `LiveCheckPump` is the pure decision about
// when to ask, what to do while nobody can answer and which answers to believe. This is the part that touches the
// world: it reads what the session manager says, makes the call, parks on the manager's own AwaitReady (the
// manager answers the instant the session is Ready, so there is no timer polling for it) and reports how each
// request ended. One relay per (session, kind of question, file), so a newer text for one file replaces an older
// one for that file and never one for another.

module LiveCheckRelay =

  /// What the session manager says about the worker a request would use, from the session's status and whether a
  /// proxy for it is registered. A worker that has a proxy but is still Starting is not Serving: a worker that is
  /// still warming answers a type-check with no diagnostics and no symbols, which reads as a clean check.
  let viewOf (session: SessionInfo option) (hasProxy: bool) : WorkerView =
    match session with
    | None -> WorkerView.Gone "the session is not registered"
    | Some info ->
      match info.Status with
      | SessionLifecycleStatus.Ready worker
      | SessionLifecycleStatus.Evaluating worker ->
        match hasProxy with
        | true -> WorkerView.Serving worker.Pid
        // Ready is only ever reported after the transport is installed, so this is a fault, and a request that waited
        // for the manager to say Ready again would be answered at once, forever.
        | false -> WorkerView.Gone "the session is Ready but has no worker transport"
      | SessionLifecycleStatus.Starting _
      | SessionLifecycleStatus.Restarting _
      | SessionLifecycleStatus.Building _ -> WorkerView.Arriving
      | SessionLifecycleStatus.Faulted reason -> WorkerView.Gone (FaultReason.describe reason)
      | SessionLifecycleStatus.HostCrashed (_, crash) -> WorkerView.Gone (HostCrash.describe crash)
      | SessionLifecycleStatus.Stopped -> WorkerView.Gone "the session stopped"

  /// How a wait for the session to be Ready ended.
  [<RequireQualifiedAccess>]
  type WaitEnded =
    /// The manager answered: the session is Ready, or can no longer become Ready. What the view says now tells which.
    | SessionAnswered
    | DeadlineReached

  /// Everything a relay needs from the world.
  type Ports<'req, 'reply> =
    { /// What the manager says about the worker right now.
      ReadView: unit -> Async<WorkerView>
      /// Put the request to the worker with this pid. Never throws: a call that failed is `Silent`.
      Ask: 'req -> int -> Async<WorkerReply<'reply>>
      /// Park until the session is Ready or can no longer be, or the deadline passes.
      AwaitReady: Async<WaitEnded>
      /// How a request ended.
      Deliver: 'req -> PumpOutcome<'reply> -> unit
      /// A newer request replaced this one before it was answered, so nothing will ever be reported for it.
      Superseded: 'req -> unit
      /// A line for the log naming what a relay did, with the key it is for.
      Say: string -> unit }

  /// One pump and the lock that serialises its steps.
  type Relay<'req, 'reply>(supersedes: 'req -> 'req -> bool, ports: Ports<'req, 'reply>) =
    let gate = obj ()
    let mutable state : PumpState<'req> = LiveCheckPump.initial

    /// Fold one event, then do what the fold asked, under the same lock, so the order the fold decided is the order
    /// requests are reported in. Everything an effect does is short and never waits: a call or a wait is started and
    /// left to raise its own event, and reporting only queues a message for the Elm loop.
    member private this.Raise(event: PumpEvent<'req, 'reply>) : unit =
      lock gate (fun () ->
        let next, effects = LiveCheckPump.step supersedes state event
        state <- next
        for effect in effects do
          this.Interpret effect)

    /// Run a continuation that raises events. One that throws must not leave a request in flight with nobody to end
    /// it, so it ends as an answer from a worker that is gone, which the pump turns into an unanswered request.
    member private this.Continue(what: string, failure: exn -> PumpEvent<'req, 'reply>, work: Async<PumpEvent<'req, 'reply>>) : unit =
      Async.Start(
        async {
          try
            let! event = work
            this.Raise event
          with ex ->
            ports.Say (sprintf "%s failed: %s" what ex.Message)
            try this.Raise(failure ex)
            with inner -> ports.Say (sprintf "%s could not be ended either: %s" what inner.Message)
        })

    member private this.Interpret(effect: PumpEffect<'req, 'reply>) : unit =
      match effect with
      | PumpEffect.Ask (request, pid) ->
        this.Continue(
          sprintf "the call to worker %d" pid,
          (fun ex -> PumpEvent.WorkerAnswered (WorkerReply.Silent ex.Message, WorkerView.Gone ex.Message)),
          async {
            let! reply = ports.Ask request pid
            let! view = ports.ReadView()
            return PumpEvent.WorkerAnswered (reply, view)
          })
      | PumpEffect.AwaitWorker waitId ->
        ports.Say (sprintf "no worker is Ready, so the request waits for one (wait %d)" waitId)
        this.Continue(
          sprintf "wait %d" waitId,
          (fun _ -> PumpEvent.WaitDeadlineReached waitId),
          async {
            match! ports.AwaitReady with
            | WaitEnded.SessionAnswered ->
              let! view = ports.ReadView()
              return PumpEvent.WorkerSeen (waitId, view)
            | WaitEnded.DeadlineReached -> return PumpEvent.WaitDeadlineReached waitId
          })
      | PumpEffect.Deliver (request, outcome) ->
        try ports.Deliver request outcome
        with ex -> ports.Say (sprintf "reporting how a request ended failed: %s" ex.Message)
      | PumpEffect.Superseded request ->
        try ports.Superseded request
        with ex -> ports.Say (sprintf "closing a request that was replaced failed: %s" ex.Message)

    /// A request, and what the manager says about the worker right now.
    member this.Submit(request: 'req) : Async<unit> =
      async {
        let! view = ports.ReadView()
        this.Raise(PumpEvent.Requested (request, view))
      }

  /// The relays of every session, by what they relay.
  type Registry<'key, 'req, 'reply when 'key: equality>() =
    let relays = ConcurrentDictionary<'key, Relay<'req, 'reply>>()

    /// The relay for this key, made with `make` the first time the key is seen.
    member _.For(key: 'key, make: unit -> Relay<'req, 'reply>) : Relay<'req, 'reply> =
      relays.GetOrAdd(key, fun _ -> make ())

  // ── What the daemon asks a worker ───────────────────────────────────────────────────────────────────

  /// A message for the worker and what to do with how it ended.
  type WorkerCall =
    { Message: WorkerMessage
      Deliver: PumpOutcome<WorkerResponse> -> unit
      /// A newer call for the same thing replaced this one before it was answered.
      Superseded: unit -> unit }

  /// What a call is for. A type-check of the newest text replaces an older one, because only the newest text has a
  /// verdict worth reporting. An eval runs the tests its check selected, and every one of those is owed.
  [<RequireQualifiedAccess>]
  type CallKind =
    | Check
    | Eval

  module CallKind =
    let supersession (kind: CallKind) : Supersession =
      match kind with
      | CallKind.Check -> Supersession.NewestWins
      | CallKind.Eval -> Supersession.EveryOneRuns

    let describe (kind: CallKind) : string =
      match kind with
      | CallKind.Check -> "type-check"
      | CallKind.Eval -> "eval"

    /// Whether a newer call of this kind makes an older one pointless.
    let supersedes (kind: CallKind) : WorkerCall -> WorkerCall -> bool =
      match supersession kind with
      | Supersession.NewestWins -> fun _ _ -> true
      | Supersession.EveryOneRuns -> fun _ _ -> false

  /// What a relay is for: one session, one kind of call, one file.
  type CallKey =
    { Session: string
      Kind: CallKind
      File: string }
