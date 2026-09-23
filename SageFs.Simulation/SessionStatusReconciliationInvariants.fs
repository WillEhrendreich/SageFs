namespace SageFs.Simulation

open SageFs
open SageFs.WorkerProtocol
open SageFs.Simulation.SessionStatusReconciliationSim

/// Named invariants over a drained `SessionStatusReconciliationSim.Trace`.
/// Same stable-id / `Holds`-vs-`Violated` shape as `WarmupInvariants` /
/// `StreamingProxyInvariants`.
module SessionStatusReconciliationInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  let private isTerminal = function
    | SessionLifecycleStatus.Faulted _ | SessionLifecycleStatus.Stopped -> true
    | _ -> false

  /// no-stale-poll-resurrection (THE fix this sim exists to pin): once any
  /// prefix reaches a terminal status (Faulted or Stopped), no LATER `Poll`
  /// event — a stale/lagging worker reply — ever changes it. `DaemonFault`,
  /// `DaemonStop` and `Restarted` are exempt: they are the daemon's OWN
  /// direct decisions (never routed through `ofWorkerReport`), not a worker
  /// reply racing one, and `Restarted` is REQUIRED to move a terminal
  /// session back to a fresh `Starting` — that is a feature, not a
  /// resurrection.
  ///
  /// Walks `t.History` against `t.Scenario.Events` directly — NOT a fresh
  /// re-fold through the real `ofWorkerReport` — so this invariant actually
  /// inspects whichever reducer (real or twin) produced the trace, and can
  /// therefore fire against the twin.
  let noStalePollResurrection : Invariant =
    { Id = "no-stale-poll-resurrection"
      Description =
        "Once the registry reaches Faulted or Stopped, no later Poll (a worker's own status reply) ever changes it away from that terminal state — only an explicit DaemonFault/DaemonStop/Restarted may."
      Check = fun t ->
        let pairs = List.zip (List.pairwise t.History) t.Scenario.Events
        pairs
        |> List.tryPick (fun ((before, after), event) ->
          match event, isTerminal before, after = before with
          | Event.Poll reported, true, false ->
            Some(
              Outcome.Violated(
                sprintf
                  "reducer=%s seed=%d — a stale Poll(%A) changed an already-terminal status %A to %A"
                  t.Reducer t.Scenario.Seed reported before after))
          | _ -> None)
        |> Option.defaultValue Outcome.Holds }

  /// health-agrees-with-registry: at EVERY point in the trace's history, the
  /// user-meaningful verdict `SessionHealth.classify` derives from the
  /// registry status agrees with the status itself on whether the session
  /// is done — a Faulted or Stopped status is ALWAYS `SessionHealth.Failed`,
  /// never anything a caller could read as still-alive. This is the
  /// second surface (`/health`'s own `health` field, `/api/sessions`,
  /// `get_fsi_status`'s health line) that must never disagree with the
  /// first (the raw status label) about whether the session is usable.
  let healthAgreesWithRegistry : Invariant =
    { Id = "health-agrees-with-registry"
      Description =
        "At every point in the trace, a Faulted or Stopped registry status classifies as SessionHealth.Failed — the health verdict and the raw status can never disagree about whether the session is done."
      Check = fun t ->
        t.History
        |> List.tryPick (fun status ->
          match isTerminal status with
          | false -> None
          | true ->
            match SessionHealth.classify status [] None with
            | SessionHealth.Failed _ -> None
            | other ->
              Some(
                Outcome.Violated(
                  sprintf
                    "reducer=%s seed=%d — terminal status %A classified as %A, not Failed"
                    t.Reducer t.Scenario.Seed status other)))
        |> Option.defaultValue Outcome.Holds }

  /// racing-polls-agree ("a status poll racing both"): from ANY terminal
  /// status, two DIFFERENT concurrent worker replies — whichever the
  /// registry happens to reconcile against first — settle on the exact
  /// same status. A caller that polled a fraction of a second earlier or
  /// later than another must never see a different answer.
  let racingPollsAgree : Invariant =
    { Id = "racing-polls-agree"
      Description =
        "From any terminal status, reconciling against two different concurrent worker replies yields the same result either way — no two simultaneous readers can be told different things."
      Check = fun t ->
        t.History
        |> List.filter isTerminal
        |> List.tryPick (fun terminalStatus ->
          let replies =
            [ SessionStatus.Starting; SessionStatus.Ready; SessionStatus.Evaluating
              SessionStatus.Building "restoring"; SessionStatus.Restarting; SessionStatus.Stopped
              SessionStatus.Faulted ]
          let results = replies |> List.map (SessionLifecycleStatus.ofWorkerReport terminalStatus)
          match results |> List.distinct with
          | [ _ ] -> None
          | many ->
            Some(
              Outcome.Violated(
                sprintf
                  "reducer=%s seed=%d — from terminal status %A, racing replies %A reconciled to %d different results: %A"
                  t.Reducer t.Scenario.Seed terminalStatus replies (List.length many) many)))
        |> Option.defaultValue Outcome.Holds }

  let all : Invariant list =
    [ noStalePollResurrection; healthAgreesWithRegistry; racingPollsAgree ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
