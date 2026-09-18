namespace SageFs.Simulation

open SageFs.Simulation.FileReloadRoutingSim

/// Named invariants over a drained `FileReloadRoutingSim.Trace`. Same
/// stable-id / `Holds`-vs-`Violated` shape as `EvalActorInvariants` /
/// `CohortLandingInvariants`. The oracle here is INDEPENDENT of the router
/// under test: every check compares a `RoutingDecision`'s `SessionIdxs`
/// (what the router decided) against its `ExpectedOwners` (ground truth
/// from `FileReloadRoutingSim`'s own Claim/Unclaim bookkeeping, which never
/// reads `LiveTestWatcherCore.State`) — it never calls `sessionsForPath` or
/// `isUnderWatchedDir` itself, so it is a genuine external check, not a
/// tautology.
module FileReloadRoutingInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// each-owning-session-gets-exactly-one-FileReloaded-per-save: for every
  /// drained path, the router's `SessionIdxs` — as a SET — equals
  /// `ExpectedOwners` exactly, AND contains no repeated session index. This
  /// catches both under-notification (an owner skipped) and
  /// over-notification (a non-owner notified, or an owner notified more
  /// than once). The double-fire twin trips the duplicate half; the
  /// broadcast-to-all twin trips the set-equality half.
  let eachOwningSessionExactlyOnce : Invariant =
    { Id = "each-owning-session-gets-exactly-one-FileReloaded-per-save"
      Description =
        "For a save under a claimed dir, every owning session receives exactly one FileReloaded — no owner is skipped, no non-owner is notified, and no owner is notified twice."
      Check = fun t ->
        t.Final.Drains
        |> List.tryPick (fun d ->
          let distinctCount = d.SessionIdxs |> List.distinct |> List.length
          match distinctCount <> d.SessionIdxs.Length with
          | true ->
            Some(
              sprintf
                "path %s: session(s) notified more than once — SessionIdxs=%A (seed=%d, ops=%A)"
                d.Path d.SessionIdxs t.Scenario.Seed t.Scenario.Ops)
          | false ->
            match Set.ofList d.SessionIdxs = d.ExpectedOwners with
            | true -> None
            | false ->
              Some(
                sprintf
                  "path %s: expected owners %A, router decided %A (seed=%d, ops=%A)"
                  d.Path (Set.toList d.ExpectedOwners) d.SessionIdxs t.Scenario.Seed t.Scenario.Ops))
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// unclaimed-sessions-get-none: no drained path's `SessionIdxs` ever
  /// contains a session index outside `ExpectedOwners` — a session that
  /// does not claim the saved path's directory is NEVER notified, no
  /// matter what any OTHER directory's claims look like. This is exactly
  /// what the broadcast-to-all twin violates (it notifies every session
  /// that claims ANY dir, not just the saved dir's claimants).
  let unclaimedSessionsGetNone : Invariant =
    { Id = "unclaimed-sessions-get-none"
      Description =
        "A session that does not claim the saved path's directory is never sent FileReloaded for that save."
      Check = fun t ->
        t.Final.Drains
        |> List.tryPick (fun d ->
          d.SessionIdxs
          |> List.tryFind (fun idx -> not (Set.contains idx d.ExpectedOwners))
          |> Option.map (fun idx ->
            sprintf
              "path %s: session #%d was notified but does not claim dir #%d (expected owners=%A, seed=%d, ops=%A)"
              d.Path idx d.DirIdx (Set.toList d.ExpectedOwners) t.Scenario.Seed t.Scenario.Ops))
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  let all : Invariant list = [ eachOwningSessionExactlyOnce; unclaimedSessionsGetNone ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
