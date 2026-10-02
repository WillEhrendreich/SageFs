namespace SageFs

open SageFs.WorkerProtocol

/// What a caller who is waiting for a session is told, and when.
///
/// A session is worth waiting on in two situations, and they are different facts:
///
///   * it is on its way to Ready (Starting, Restarting, Building);
///   * it IS Ready, and a rebuild is running anyway. A build-first rebuild keeps the old worker
///     serving while `dotnet build` runs, so the lifecycle says Ready the whole time. A caller who
///     asked for a rebuild and then asks "is it ready?" is asking about the NEW build. Answering
///     "Ready" from the old worker is true and useless: the caller reads it as "my change is live".
///
/// This is the one place that decides it. The session manager asks it to settle the callers it
/// parked, and the status tool asks it whether to park a caller at all, so the two cannot disagree.
/// It is pure so a simulation can fold the real decision through every order of events.
[<RequireQualifiedAccess>]
module ReadyWait =

  /// What a parked caller is told now.
  [<RequireQualifiedAccess>]
  type Verdict =
    /// Not yet. The session is on its way to Ready, or a rebuild is still running.
    | KeepParked
    /// Ready, and no rebuild is running.
    | Ready
    /// Waiting will not make it Ready. The error says why.
    | Failed of SageFsError

  /// What the session's own state says.
  ///
  /// A rebuild in progress is waited on whatever the lifecycle says, except for a session that was
  /// stopped: a Faulted or crashed session with a build running has a way back to Ready, and the build
  /// ending decides it (`ofRebuildEnd`).
  let ofSession (status: SessionLifecycleStatus) (rebuild: LastRebuild) : Verdict =
    match status, rebuild with
    | SessionLifecycleStatus.Stopped, _ -> Verdict.Failed (SageFsError.WorkerSpawnFailed "the session stopped before it became Ready")
    | _, LastRebuild.Latest (RebuildOutcome.InProgress _) -> Verdict.KeepParked
    | (SessionLifecycleStatus.Ready _ | SessionLifecycleStatus.Evaluating _), _ -> Verdict.Ready
    | (SessionLifecycleStatus.Starting _ | SessionLifecycleStatus.Restarting _ | SessionLifecycleStatus.Building _), _ -> Verdict.KeepParked
    | SessionLifecycleStatus.Faulted reason, _ -> Verdict.Failed (SageFsError.WorkerSpawnFailed (FaultReason.describe reason))
    | SessionLifecycleStatus.HostCrashed (_, crash), _ -> Verdict.Failed (SageFsError.FsiHostCrashed crash)

  /// What a caller that was parked THROUGH a rebuild is told when that rebuild ends.
  ///
  /// A failed rebuild leaves the old worker serving, so the session reads Ready again and
  /// `ofSession` alone would call the wait a success. The caller asked about the new build, and
  /// there is none, so the wait fails with the build's own error. A rebuild that succeeded has
  /// started the replacement worker, and the session's status decides from there.
  let ofRebuildEnd (outcome: RebuildOutcome) : Verdict =
    match outcome with
    | RebuildOutcome.InProgress _
    | RebuildOutcome.Succeeded _ -> Verdict.KeepParked
    | RebuildOutcome.FailedStillServing (error, _)
    | RebuildOutcome.FailedNotServing (error, _) -> Verdict.Failed error

  /// Whether a status request is worth parking.
  [<RequireQualifiedAccess>]
  type Plan =
    | Park
    | DoNotPark

  /// A caller is parked exactly when the session would keep it parked.
  let plan (status: SessionLifecycleStatus) (rebuild: LastRebuild) : Plan =
    match ofSession status rebuild with
    | Verdict.KeepParked -> Plan.Park
    | Verdict.Ready
    | Verdict.Failed _ -> Plan.DoNotPark

  /// Answers every parked caller the session's state settles, and returns the callers still parked.
  /// `find` reads a session's status and rebuild record; a session that is gone settles its callers
  /// with `SessionNotFound`. Generic in the caller so the manager can hand in its reply channels.
  let settle
    (answer: 'waiter -> Result<unit, SageFsError> -> unit)
    (find: SessionId -> (SessionLifecycleStatus * LastRebuild) voption)
    (parked: Map<SessionId, 'waiter list>)
    : Map<SessionId, 'waiter list> =
    parked
    |> Map.filter (fun id waiters ->
      let verdict =
        match find id with
        | ValueNone -> Verdict.Failed (SageFsError.SessionNotFound (SessionId.value id))
        | ValueSome (status, rebuild) -> ofSession status rebuild
      match verdict with
      | Verdict.KeepParked -> true
      | Verdict.Ready ->
        for waiter in waiters do answer waiter (Result.Ok ())
        false
      | Verdict.Failed error ->
        for waiter in waiters do answer waiter (Result.Error error)
        false)

  /// Wakes every caller in `answers`, in order. A caller that cannot be woken is reported and never stops the rest.
  let deliver
    (answers: ('waiter * Result<unit, SageFsError>) seq)
    (wake: 'waiter -> Result<unit, SageFsError> -> unit)
    (onFailure: exn -> unit)
    : unit =
    for waiter, result in answers do
      try wake waiter result
      with ex -> onFailure ex

  /// Answers the callers parked on one session through a rebuild that has just ended, when it ended
  /// badly, with the build's own error. Returns the callers still parked.
  let settleAfterRebuild
    (answer: 'waiter -> Result<unit, SageFsError> -> unit)
    (id: SessionId)
    (outcome: RebuildOutcome)
    (parked: Map<SessionId, 'waiter list>)
    : Map<SessionId, 'waiter list> =
    match ofRebuildEnd outcome, Map.tryFind id parked with
    | Verdict.Failed error, Some waiters ->
      for waiter in waiters do answer waiter (Result.Error error)
      Map.remove id parked
    | Verdict.Failed _, None
    | Verdict.KeepParked, _
    | Verdict.Ready, _ -> parked
