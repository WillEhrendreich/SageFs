namespace SageFs.Server

open System
open System.Threading.Tasks
open SageFs
open SageFs.Utils
open SageFs.WorkerProtocol
open SageFs.WorkflowTypes

/// What the watch last concluded about one session's workflow.
[<RequireQualifiedAccess>]
type WorkflowWatch =
  /// The session runs `workflow` and nothing is changing it.
  | Settled of workflow: SessionWorkflow
  /// The session records `target` but the worker that runs it is not serving yet: a switch is in flight.
  | Moving of from: SessionWorkflow * target: SessionWorkflow

/// Whether a session's worker is up and answering, read from its lifecycle. A DU, so the two ways of being "not
/// yet" (still coming, or gone) are one named case and not a flag that can mean either.
[<RequireQualifiedAccess>]
type WorkerReadiness =
  | Serving
  | NotServing

/// What one look at the sessions came to.
type WorkflowObservation =
  { /// The watch to carry into the next look.
    Watch: Map<string, WorkflowWatch>
    /// `workflow_switching` and `workflow_switched`, in the order they happened.
    Events: SseEvent list
    /// Sessions whose switch began in this look: each is waited on until its worker is ready, then looked at again.
    Awaiting: SessionId list }

/// The `workflow_switching` / `workflow_switched` events.
///
/// A switch is performed by `SessionOps.SwitchWorkflow`, which restarts the SAME session id spawn-first into the target
/// workflow: the session records the new workflow the moment the replacement worker is accepted, and that worker is
/// ready some seconds later. The two points are therefore seen in the session list, not in the route that asked: the
/// route (`POST /api/sessions/{sid}/workflow`), the dashboard and `run_app`'s automatic switch into hot reload all go
/// through the same command, so watching the list covers every one of them with one rule.
///
///   - `workflow_switching` when a session's recorded workflow changes from what it was running.
///   - `workflow_switched` when the worker that runs the new workflow is serving.
///
/// A switch whose replacement worker never gets there (it faults) emits `workflow_switching` and no `workflow_switched`:
/// the session's own health event says why.
module WorkflowSwitchWatch =

  let readinessOf (status: SessionLifecycleStatus) : WorkerReadiness =
    match status with
    | SessionLifecycleStatus.Ready _
    | SessionLifecycleStatus.Evaluating _
    | SessionLifecycleStatus.Building _ -> WorkerReadiness.Serving
    | SessionLifecycleStatus.Starting _
    | SessionLifecycleStatus.Faulted _
    | SessionLifecycleStatus.HostCrashed _
    | SessionLifecycleStatus.Restarting _
    | SessionLifecycleStatus.Stopped -> WorkerReadiness.NotServing

  /// What the watch knew of a session before this look.
  [<RequireQualifiedAccess>]
  type Known =
    | NeverSeen
    | Seen of WorkflowWatch

  /// Whether a look leaves a session's switch to be waited on.
  [<RequireQualifiedAccess>]
  type Wait =
    | UntilWorkerReady
    | NothingToWaitFor

  /// One session, one look: the watch to carry, what to say, and whether to wait for the worker.
  type Step =
    { Watch: WorkflowWatch
      Said: SseEvent list
      Wait: Wait }

  let switched (sid: string) (workflow: SessionWorkflow) : SseEvent =
    SseEvent.WorkflowSwitched(
      sid,
      SessionWorkflow.label workflow,
      ReplCapability.label (SessionWorkflow.replCapability workflow),
      SessionWorkflow.isHotReloadActive workflow)

  /// One session, one look. The new watch for it, and what to say about it.
  let rec stepSession
    (sid: string)
    (known: Known)
    (workflow: SessionWorkflow)
    (readiness: WorkerReadiness)
    : Step =
    match known, readiness with
    | Known.NeverSeen, _ ->
      // First sight is not a switch: a session that is created into a workflow was never "switched" into it.
      { Watch = WorkflowWatch.Settled workflow; Said = []; Wait = Wait.NothingToWaitFor }
    | Known.Seen (WorkflowWatch.Settled current), _ when current = workflow ->
      { Watch = WorkflowWatch.Settled current; Said = []; Wait = Wait.NothingToWaitFor }
    | Known.Seen (WorkflowWatch.Settled current), WorkerReadiness.Serving ->
      let starting = SseEvent.WorkflowSwitching(sid, SessionWorkflow.label current, SessionWorkflow.label workflow)
      { Watch = WorkflowWatch.Settled workflow; Said = [ starting; switched sid workflow ]; Wait = Wait.NothingToWaitFor }
    | Known.Seen (WorkflowWatch.Settled current), WorkerReadiness.NotServing ->
      let starting = SseEvent.WorkflowSwitching(sid, SessionWorkflow.label current, SessionWorkflow.label workflow)
      { Watch = WorkflowWatch.Moving(current, workflow); Said = [ starting ]; Wait = Wait.UntilWorkerReady }
    | Known.Seen (WorkflowWatch.Moving (_, target)), WorkerReadiness.Serving when target = workflow ->
      { Watch = WorkflowWatch.Settled workflow; Said = [ switched sid workflow ]; Wait = Wait.NothingToWaitFor }
    | Known.Seen (WorkflowWatch.Moving (from, target)), WorkerReadiness.NotServing when target = workflow ->
      { Watch = WorkflowWatch.Moving(from, target); Said = []; Wait = Wait.NothingToWaitFor }
    | Known.Seen (WorkflowWatch.Moving (_, target)), _ ->
      // Another switch while one was in flight: the first one's target is where this one starts from.
      stepSession sid (Known.Seen (WorkflowWatch.Settled target)) workflow readiness

  /// Look at every session. A session that is gone is forgotten.
  let observe (watch: Map<string, WorkflowWatch>) (sessions: SessionInfo list) : WorkflowObservation =
    let folded =
      sessions
      |> List.fold
        (fun (kept, events, awaiting) info ->
          let sid = SessionId.value info.Id
          let known =
            match Map.tryFind sid watch with
            | Some previous -> Known.Seen previous
            | None -> Known.NeverSeen
          let step = stepSession sid known info.Workflow (readinessOf info.Status)
          let awaiting' =
            match step.Wait with
            | Wait.UntilWorkerReady -> info.Id :: awaiting
            | Wait.NothingToWaitFor -> awaiting
          Map.add sid step.Watch kept, events @ step.Said, awaiting')
        (Map.empty, [], [])
    let kept, events, awaiting = folded
    { Watch = kept; Events = events; Awaiting = List.rev awaiting }

  /// Push the two events for every workflow switch the daemon performs, scoped to the session that switched. Looks at
  /// the sessions on every state change, and once more when a session that began a switch is ready (or has failed to
  /// be), because the state change that says "ready" can arrive before the list does.
  let wire
    (stateChanged: IEvent<SseEvent>)
    (ops: SessionManagementOps)
    (publish: SseFrame -> unit)
    : IDisposable =
    let gate = obj ()
    let watch : Map<string, WorkflowWatch> ref = ref Map.empty
    let look () : Task<SessionId list> =
      task {
        let! sessions = ops.GetAllSessions ()
        return
          lock gate (fun () ->
            let seen = observe watch.Value sessions
            watch.Value <- seen.Watch
            for evt in seen.Events do
              publish (SseEvent.frame evt)
            seen.Awaiting)
      }
    let rec lookAndSettle () : Task =
      task {
        let! begun = look ()
        for sid in begun do
          let settle : Task =
            task {
              let! _ = ops.AwaitReady sid Timeouts.warmupAbsoluteMax
              do! lookAndSettle ()
            }
          settle.ContinueWith(fun (t: Task) ->
            match t.IsFaulted with
            | true -> Log.error "[SSE] Workflow switch settle fault: %s" t.Exception.InnerException.Message
            | false -> ())
          |> ignore
      }
    stateChanged.Subscribe(fun _ ->
      let looked : Task = lookAndSettle ()
      looked.ContinueWith(fun (t: Task) ->
        match t.IsFaulted with
        | true -> Log.error "[SSE] Workflow switch watch fault: %s" t.Exception.InnerException.Message
        | false -> ())
      |> ignore)
