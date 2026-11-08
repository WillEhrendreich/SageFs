namespace SageFs.Features

open System
open System.Threading.Tasks

/// What the cohort landing gate does while the integration session is not ready yet, and what it says when it gives up.
///
/// Finding C-restart (cohort run, 2026-11-07): a landing was Blocked Inconclusive with "Session '5bb94271' timed out during
/// restart after 120.0s. Try again or use hard_reset_fsi_session." The gate parked on the session manager's ready signal with a
/// fixed 120 s deadline (`Timeouts.cohortIntegrationSettle`), and a restart of the integration session loads the whole test
/// project, which takes minutes when eighteen builds share the machine. The restart was working the whole time and the gate cut
/// it off at a number picked in advance.
///
/// The rule here: a wait ends when the session is ready, when it is dead, or when it has stopped changing. Never because a clock
/// ran out while the work was moving. The gate parks in slices of one stall window. At the end of a slice it looks at the phase
/// the session is in, and the SAME phase at two looks in a row is a stall. A different phase is progress, so the wait goes on for
/// another slice, however many slices that takes. The judgement is `judge`, pure, over the phases seen; `await` is the loop around
/// it with its two ports injected, so the simulation in SageFs.Simulation folds the real loop through virtual time.
///
/// One phase has its bound somewhere else on purpose: a rebuild in flight (`BuildInFlight`). `dotnet build` prints nothing the
/// session manager can see for minutes, so "no change" says nothing about it, and the build has its own bound
/// (`Timeouts.buildCompletion`, applied by the build runner) whose failure the wait hears as an answer. Judging it by a window
/// here would cut a healthy build off, the thing this module exists to stop.
///
/// Finding C-pending: a landing requested while the integration session was still being built was Blocked at once with "not
/// started yet, retry set_integration_ref", which a member cannot run and the conductor had already run. `awaitChange` is the
/// event-driven wait the gate uses to hold such a landing until the session starts or fails.
module IntegrationSettle =

  // -- What the session is doing --

  /// The last thing the worker said about what it is doing: nothing yet, or a line of progress text.
  [<RequireQualifiedAccess>]
  type WorkerSaid =
    | Nothing
    | Said of text: string

  module WorkerSaid =
    /// What the session manager keeps: the last progress payload, if the worker sent one. Blank text says nothing.
    let ofText (text: string option) : WorkerSaid =
      match text with
      | Some line when not (String.IsNullOrWhiteSpace line) -> WorkerSaid.Said line
      | Some _
      | None -> WorkerSaid.Nothing

  /// The phase the integration session is in, as far as a waiter can tell. Equality is the point: two looks that read the same
  /// phase, progress text included, saw no progress between them.
  [<RequireQualifiedAccess>]
  type Phase =
    /// A restart replaced or is replacing the worker. `progress` is the last thing the worker said about it.
    | Restarting of progress: WorkerSaid
    /// The worker is warming up: loading projects, opening namespaces.
    | Starting of progress: WorkerSaid
    /// The worker itself reported a build step.
    | Building of reason: string * progress: WorkerSaid
    /// A rebuild of the session's project is running (the old worker may still be serving). Its bound lives in the build runner.
    | BuildInFlight
    | Ready
    /// The session faulted. The reason is the worker's or the supervisor's own.
    | Faulted of reason: string
    /// The worker is alive and its FSI host died. A reset brings it back; waiting does not.
    | HostCrashed
    | Stopped
    /// The session manager has no such session.
    | Missing

  let withProgress (label: string) (progress: WorkerSaid) : string =
    match progress with
    | WorkerSaid.Said text -> sprintf "%s (%s)" label text
    | WorkerSaid.Nothing -> label

  /// The phase in words, for the conductor.
  let describePhase (phase: Phase) : string =
    match phase with
    | Phase.Restarting progress -> withProgress "restarting" progress
    | Phase.Starting progress -> withProgress "starting up" progress
    | Phase.Building (reason, progress) -> withProgress (sprintf "building (%s)" reason) progress
    | Phase.BuildInFlight -> "rebuilding its project"
    | Phase.Ready -> "ready"
    | Phase.Faulted reason -> sprintf "faulted (%s)" reason
    | Phase.HostCrashed -> "crashed (its FSI host died)"
    | Phase.Stopped -> "stopped"
    | Phase.Missing -> "missing (the daemon has no such session)"

  // -- The judgement --

  [<RequireQualifiedAccess>]
  type Verdict =
    /// Ready: the caller goes on to classify the session's trust.
    | Settled
    /// Moving, or not yet looked at twice: wait another slice.
    | KeepWaiting
    /// The same phase at two looks a window apart: nothing moved. Carries the phase it stuck in.
    | Stuck of phase: Phase
    /// Waiting will not make it ready: it is dead. Carries what it is.
    | Dead of phase: Phase

  /// What to make of the phase seen now, given the phase seen at the previous look (none on the first look).
  /// Exhaustive on purpose: a new phase must say which of the four it is.
  let judge (previous: Phase option) (seen: Phase) : Verdict =
    match seen with
    | Phase.Ready -> Verdict.Settled
    | Phase.Faulted _
    | Phase.HostCrashed
    | Phase.Stopped
    | Phase.Missing -> Verdict.Dead seen
    | Phase.BuildInFlight -> Verdict.KeepWaiting
    | Phase.Restarting _
    | Phase.Starting _
    | Phase.Building _ ->
      match previous with
      | Some earlier when earlier = seen -> Verdict.Stuck seen
      | Some _
      | None -> Verdict.KeepWaiting

  // -- The wait --

  /// How one slice of the wait ended.
  [<RequireQualifiedAccess>]
  type Wake =
    /// The session manager said Ready.
    | Ready
    /// The slice ran out. Look at the phase.
    | SliceElapsed
    /// The session manager answered with a refusal: waiting is over and this is why (a failed rebuild, a fault).
    | Refused of reason: string

  /// What the wait is given: how to park for one slice, and how to read the phase.
  type Ports = {
    AwaitReady: TimeSpan -> Async<Wake>
    Observe: unit -> Async<Phase>
  }

  [<RequireQualifiedAccess>]
  type Outcome =
    /// Ready. The caller classifies the session's trust as it always did.
    | Settled
    | StuckIn of phase: Phase * window: TimeSpan
    | DiedAs of phase: Phase
    /// The session manager's own refusal, verbatim.
    | Refused of reason: string

  /// Park on the session's ready signal in slices of `window`, and judge the phase at the end of each. There is no deadline: the
  /// loop ends only through `judge` or an answer, and an answer or a Ready arrives the instant it happens, because the slice is
  /// a park, not a poll.
  let await (window: TimeSpan) (ports: Ports) : Async<Outcome> =
    let rec loop (previous: Phase option) : Async<Outcome> =
      async {
        match! ports.AwaitReady window with
        | Wake.Ready -> return Outcome.Settled
        | Wake.Refused reason -> return Outcome.Refused reason
        | Wake.SliceElapsed ->
          let! seen = ports.Observe ()
          match judge previous seen with
          | Verdict.Settled -> return Outcome.Settled
          | Verdict.Dead phase -> return Outcome.DiedAs phase
          | Verdict.Stuck phase -> return Outcome.StuckIn (phase, window)
          | Verdict.KeepWaiting -> return! loop (Some seen)
      }
    loop None

  // -- Waiting for the session to exist --

  /// Park until `read ()` returns something `isReady` accepts, woken by `changed`. The subscription is made BEFORE the read, so
  /// a change between the read and the park cannot be missed: it completes the very wait the read is about to start.
  /// `Async.Sleep` and polling are not used; a value that never changes never wakes this, and that is the contract.
  let awaitChange (changed: IEvent<unit>) (read: unit -> 'a) (isReady: 'a -> bool) : Async<'a> =
    let rec loop () : Async<'a> =
      async {
        let woke = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        use _subscription = changed.Subscribe(fun () -> woke.TrySetResult () |> ignore)
        let value = read ()
        match isReady value with
        | true -> return value
        | false ->
          do! Async.AwaitTask woke.Task
          return! loop ()
      }
    loop ()

  // -- What the gate says --

  /// What a refusal tells each party to do. Two texts because the two parties have different tools: a member cannot run the
  /// conductor's tools, and a refusal that tells a member to "retry set_integration_ref" tells it to run something it may not.
  type Refusal = {
    /// What went wrong, in one sentence both parties read.
    What: string
    /// What the requester of the landing does next. Never names a conductor-only tool.
    ForMember: string
    /// What the conductor looks at and runs.
    ForConductor: string
  }

  /// The one text a Blocked landing carries.
  let render (refusal: Refusal) : string =
    sprintf "%s Member: %s Conductor: %s" refusal.What refusal.ForMember refusal.ForConductor

  let memberWithdrawAndRequest (sessionId: string) : string =
    sprintf
      "withdraw this landing (withdraw_landing) and request it again after get_cohort_status shows \"Integration session: %s (started)\" and the conductor has the session ready. A request made then waits for the session's own start-up, so it is not cut off while the session is still loading."
      sessionId

  /// The session stayed in one phase for a whole window.
  let stuckRefusal (sessionId: string) (phase: Phase) (window: TimeSpan) : Refusal =
    { What =
        sprintf
          "The landing was not verified: integration session '%s' made no progress (still %s) across %.0f s, so the gate stopped waiting for it."
          sessionId (describePhase phase) window.TotalSeconds
      ForMember = memberWithdrawAndRequest sessionId
      ForConductor =
        sprintf
          "session '%s' has been %s for at least %.0f s with nothing new from its worker. A loaded machine stretches a phase, so look at the load first (a worker still compiling shows CPU in its process), then at the worker's log in the daemon log. If it is hung, hard_reset_fsi_session on '%s' starts a fresh worker; landings that arrive meanwhile wait for it."
          sessionId (describePhase phase) window.TotalSeconds sessionId }

  /// The session is dead, or the session manager refused the wait with a reason.
  let deadRefusal (sessionId: string) (why: string) : Refusal =
    { What = sprintf "The landing was not verified: integration session '%s' is not coming back by waiting: %s." sessionId why
      ForMember =
        "withdraw this landing (withdraw_landing). Nothing a member runs revives the integration session, so ask the conductor, and request the landing again after get_cohort_status shows the session started."
      ForConductor =
        sprintf
          "session '%s' is %s. hard_reset_fsi_session on it restarts the worker; if its worktree or build is the cause, set_integration_ref with working_directory set to the repository replaces the worktree and starts a fresh session."
          sessionId why }

  /// The integration was re-pointed while a landing was being verified, so the session it was verified against no longer exists.
  let repointedRefusal : Refusal =
    { What = "The landing was not verified: the conductor is setting the integration up again, so the session it was being verified against is going away."
      ForMember = "withdraw this landing (withdraw_landing) and request it again after get_cohort_status shows \"Integration session: ... (started)\"."
      ForConductor = "set_integration_ref is still building the new integration session; landings requested now wait for it and start when it does." }

  /// What the outcome of a wait says, as the text a Blocked landing carries. `Settled` has no text: the caller carries on.
  let describeOutcome (sessionId: string) (outcome: Outcome) : string option =
    match outcome with
    | Outcome.Settled -> None
    | Outcome.StuckIn (phase, window) -> Some (render (stuckRefusal sessionId phase window))
    | Outcome.DiedAs phase -> Some (render (deadRefusal sessionId (describePhase phase)))
    | Outcome.Refused reason -> Some (render (deadRefusal sessionId reason))
