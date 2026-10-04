namespace SageFs.Simulation

open SageFs
open SageFs.ExpensiveWorkLease
open SageFs.Simulation.LeaseSim

/// Named invariants over a `LeaseSim` trace. Same shape as
/// `SupervisorInvariants`/`MemoryShedInvariants`: the oracle only inspects
/// each step's recorded `Decision` and the resulting pool state, and never
/// re-derives the decision itself.
module LeaseSimInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant = { Id: string; Description: string; Check: State list -> Outcome }

  /// grant-never-exceeds-cap: a `Granted` decision only ever fires when
  /// the PRE-grant active count was strictly under `maxConcurrentFor` the
  /// pressure in effect at that moment — i.e. a fresh admission NEVER
  /// pushes the pool over its current cap.
  ///
  /// This is deliberately NOT "active count never exceeds the cap at any
  /// point in the trace": already-granted leases are, BY DESIGN, never
  /// revoked just because pressure later rises (`ExpensiveWorkLease`'s own
  /// module doc comment — "existing work still runs to completion or
  /// expiry"). A grandfathered lease sitting through a pressure increase
  /// can legitimately leave `Active.Length` above the NEW, lower cap
  /// without a single new admission ever having caused it — an earlier
  /// version of this invariant asserted the stronger, wrong claim and
  /// flagged exactly that grandfathering as a false violation.
  let grantNeverExceedsCap : Invariant =
    { Id = "grant-never-exceeds-cap"
      Description = "A Granted decision's PRE-grant active count was strictly under that moment's cap."
      Check = fun states ->
        (List.last states).Decisions
        |> List.tryPick (fun r ->
          match r.Decision with
          | Decision.Granted _ ->
            let cap = maxConcurrentFor r.Pressure
            let preGrantCount = List.length r.ActiveAfter - 1
            match preGrantCount >= cap with
            | true ->
              Some(
                sprintf
                  "step %d: granted with %d already active (cap %d at %s pressure) — this admission alone pushed the pool over cap"
                  r.AtStep preGrantCount cap (MemoryPressure.describe r.Pressure)
              )
            | false -> None
          | Decision.AlreadyHeld _
          | Decision.Queued _
          | Decision.Refused _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// every-wait-carries-a-positive-retry-after: `Decision.Queued` must never
  /// tell a caller to retry in zero or negative time — that would just be a
  /// busy-loop wearing a Wait costume.
  let everyWaitHasPositiveRetryAfter : Invariant =
    { Id = "every-wait-has-positive-retry-after"
      Description = "Every Wait decision carries a strictly positive retryAfter."
      Check = fun states ->
        (List.last states).Decisions
        |> List.tryPick (fun r ->
          match r.Decision with
          | Decision.Queued waiting when waiting.RetryAfter <= System.TimeSpan.Zero ->
            Some(sprintf "step %d: Queued carried a non-positive retryAfter %A" r.AtStep waiting.RetryAfter)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// queue-drains-on-cooldown: after a scenario's cooldown tail (pressure
  /// back to Normal, every agent released, enough time passed to outlive
  /// every Kind's TTL, and enough retry ROUNDS for the queue's strict
  /// FIFO — one promotion per round in the worst case — to fully drain), a
  /// correctly-fair, non-deadlocking pool ends with an EMPTY queue: nobody
  /// left starved once conditions genuinely allow admission.
  ///
  /// Deliberately NOT part of `all` (the property checked against every
  /// random seed): `request`'s FIFO only ever promotes the queue's front
  /// entry per call, so draining an arbitrary randomly-generated queue
  /// depth needs a retry-round count matched to how many entries actually
  /// piled up — a fixed round budget large enough for the worst
  /// hand-crafted case can still be too few for some pathological random
  /// walk's queue depth, which would be a bug in the SIMULATION's own
  /// retry modeling, not in `request`. This invariant is used instead
  /// against FIXED, hand-crafted scenarios where the round budget is known
  /// to be sufficient: the named "four agents piling on" worked scenario
  /// (must HOLD) and the never-expires twin reproduction (must VIOLATE).
  let queueDrainsOnCooldown : Invariant =
    { Id = "queue-drains-on-cooldown"
      Description = "After the scenario's cooldown tail, the pool's queue is empty."
      Check = fun states ->
        let final = List.last states
        match final.Pool.Queue with
        | [] -> Outcome.Holds
        | pending -> Outcome.Violated(sprintf "queue still has %d pending request(s) after cooldown: %A" pending.Length pending) }

  /// The first message `check` finds over every recorded decision, or Holds.
  let private firstViolation (states: State list) (check: DecisionRecord -> string option) : Outcome =
    (List.last states).Decisions
    |> List.tryPick check
    |> function
       | Some msg -> Outcome.Violated msg
       | None -> Outcome.Holds

  let private liveAt (clock: System.DateTimeOffset) (leases: ActiveLease list) : ActiveLease list =
    leases |> List.filter (fun l -> l.ExpiresAt > clock)

  /// MUTUAL-EXCLUSION: at every step no holder holds more than
  /// `maxPerHolder` leases. Together with `grantNeverExceedsCap` (the pool
  /// as a whole never admits past its cap) a lease is exclusive to its
  /// holder, and a holder cannot hold-and-wait for a second.
  let mutualExclusion : Invariant =
    { Id = "mutual-exclusion"
      Description = "No holder ever holds more than maxPerHolder leases at once."
      Check = fun states ->
        states
        |> List.tryPick (fun s ->
          s.Pool.Active
          |> List.countBy (fun l -> l.Holder)
          |> List.tryFind (fun (_, n) -> n > maxPerHolder)
          |> Option.map (fun (holder, n) ->
            sprintf "step %d: %s holds %d leases at once (max %d)" s.Step (Holder.describe holder) n maxPerHolder))
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// EVERY-LEASE-EXPIRES: a lease lapses at its ttl whether or not anyone
  /// releases it. Three halves. A live lease always expires exactly its
  /// kind's ttl after it was granted, so nothing renews it and none is
  /// unbounded. A request never leaves a lapsed lease in the pool, so the
  /// next caller reclaims it. And a queued ask that has not been repeated
  /// within `Timeouts.leaseAskStaleAfter` is gone too, so a waiter that
  /// crashed cannot hold up everyone behind it.
  let everyLeaseExpires : Invariant =
    { Id = "every-lease-expires"
      Description = "A lease lapses at grant + ttl and is reclaimed on contact; an unrepeated ask lapses too."
      Check = fun states ->
        let moved =
          states
          |> List.tryPick (fun s ->
            s.Pool.Active
            |> List.tryFind (fun l -> l.ExpiresAt - l.GrantedAt <> Kind.defaultTtl l.Kind)
            |> Option.map (fun l ->
              sprintf "step %d: a %s lease granted %O expires %O, not at its ttl %O" s.Step (Kind.toToken l.Kind) l.GrantedAt l.ExpiresAt (Kind.defaultTtl l.Kind)))
        match moved with
        | Some msg -> Outcome.Violated msg
        | None ->
          firstViolation states (fun r ->
            match r.ActiveAfter |> List.tryFind (fun l -> l.ExpiresAt <= r.Clock) with
            | Some lapsed ->
              Some(sprintf "step %d: a %s lease of %s lapsed at %O and was still in the pool at %O" r.AtStep (Kind.toToken lapsed.Kind) (Holder.describe lapsed.Holder) lapsed.ExpiresAt r.Clock)
            | None ->
              r.QueueAfter
              |> List.tryFind (fun q -> q.LastAskedAt + Timeouts.leaseAskStaleAfter <= r.Clock)
              |> Option.map (fun q ->
                sprintf "step %d: the ask of %s was last repeated %O and was still queued at %O" r.AtStep (Holder.describe q.Holder) q.LastAskedAt r.Clock)) }

  /// REFUSAL-NAMES-THE-REAL-HOLDER: whatever a decision says about who holds
  /// what is true of the pool at that moment. A lease granted or handed back
  /// as the caller's is recorded against the caller. A refusal that names
  /// the caller's own lease names one the caller really holds. A queued
  /// caller is shown the leases that really occupy the pool (all of them
  /// when the pool is at its cap) and never itself.
  let refusalNamesTheRealHolder : Invariant =
    { Id = "refusal-names-the-real-holder"
      Description = "Every lease a decision names is in the pool, and the caller's own is the caller's."
      Check = fun states ->
        firstViolation states (fun r ->
          let inPool (lease: ActiveLease) =
            r.ActiveAfter |> List.exists (fun l -> l.Id = lease.Id && l.Holder = lease.Holder)
          let ownedByCaller (lease: ActiveLease) = inPool lease && lease.Holder = r.Holder
          match r.Decision with
          | Decision.Granted(id, _) ->
            match r.ActiveAfter |> List.tryFind (fun l -> l.Id = id) with
            | Some lease when lease.Holder = r.Holder -> None
            | Some lease -> Some(sprintf "step %d: %s was granted a lease recorded against %s" r.AtStep (Holder.describe r.Holder) (Holder.describe lease.Holder))
            | None -> Some(sprintf "step %d: a granted lease is not in the pool" r.AtStep)
          | Decision.AlreadyHeld lease ->
            match ownedByCaller lease with
            | true -> None
            | false -> Some(sprintf "step %d: %s was told it already holds a lease that belongs to %s" r.AtStep (Holder.describe r.Holder) (Holder.describe lease.Holder))
          | Decision.Refused(Refusal.HoldsOtherKind(lease, _)) ->
            match ownedByCaller lease with
            | true -> None
            | false -> Some(sprintf "step %d: %s was refused for a lease that belongs to %s" r.AtStep (Holder.describe r.Holder) (Holder.describe lease.Holder))
          | Decision.Refused(Refusal.OtherKindQueued(queued, _)) ->
            match r.QueueAfter |> List.exists (fun q -> q.Holder = r.Holder && q.Kind = queued) with
            | true -> None
            | false -> Some(sprintf "step %d: %s was refused for a queued ask it does not have" r.AtStep (Holder.describe r.Holder))
          | Decision.Queued waiting ->
            match waiting.Holding |> List.tryFind (fun l -> not (inPool l)) with
            | Some phantom -> Some(sprintf "step %d: a queued caller was told %s holds the pool, but that lease is not in it" r.AtStep (Holder.describe phantom.Holder))
            | None ->
              match waiting.Holding |> List.tryFind (fun l -> l.Holder = r.Holder) with
              | Some own -> Some(sprintf "step %d: a queued caller was shown its own lease %s as the blocker" r.AtStep (Holder.describe own.Holder))
              | None ->
                match waiting.Why with
                | WaitReason.AtCapacity when List.length waiting.Holding <> List.length r.ActiveAfter ->
                  Some(sprintf "step %d: the pool is at its cap with %d leases but the caller was shown %d" r.AtStep (List.length r.ActiveAfter) (List.length waiting.Holding))
                | WaitReason.AtCapacity
                | WaitReason.NotYourTurn -> None) }

  /// SAME-HOLDER-IDEMPOTENT: a holder that asks again for a kind it already
  /// holds gets that same lease back, with its expiry untouched and no
  /// second lease taken.
  let sameHolderIdempotent : Invariant =
    { Id = "same-holder-idempotent"
      Description = "Asking again for a lease you hold returns it unchanged and takes no second one."
      Check = fun states ->
        firstViolation states (fun r ->
          let heldBefore = liveAt r.Clock r.ActiveBefore |> List.filter (fun l -> l.Holder = r.Holder)
          match heldBefore |> List.tryFind (fun l -> l.Kind = r.Kind) with
          | None -> None
          | Some existing ->
            match r.Decision with
            | Decision.AlreadyHeld lease when lease.Id = existing.Id && lease.ExpiresAt = existing.ExpiresAt ->
              let heldAfter = r.ActiveAfter |> List.filter (fun l -> l.Holder = r.Holder) |> List.length
              match heldAfter = List.length heldBefore with
              | true -> None
              | false -> Some(sprintf "step %d: asking again changed %s's lease count from %d to %d" r.AtStep (Holder.describe r.Holder) (List.length heldBefore) heldAfter)
            | other -> Some(sprintf "step %d: %s already held a %s lease and asking again answered %A instead of handing it back" r.AtStep (Holder.describe r.Holder) (Kind.toToken r.Kind) other)) }

  /// The asks ahead of the caller's own at the moment it asked that the pool still held a slot for: still in line
  /// after anything stale aged out, and still coming back when they were told to (`waiterIsLive`). An ask the
  /// caller has not made before has every ask in the line ahead of it.
  let liveAheadOf (r: DecisionRecord) : QueuedRequest list =
    let mySeq =
      match r.QueueBefore |> List.tryFind (fun q -> q.Holder = r.Holder && q.Kind = r.Kind) with
      | Some mine -> mine.Seq
      | None -> System.Int64.MaxValue
    r.QueueBefore
    |> List.filter (fun q -> q.Seq < mySeq && q.LastAskedAt + Timeouts.leaseAskStaleAfter > r.Clock && waiterIsLive r.Clock q)

  /// GRANT-NEVER-JUMPS-A-LIVE-WAITER: a Granted decision only fires when the pool had a slot left for it after
  /// every live earlier ask had its own, that is `active + liveAhead < cap`. A later ask never takes the slot an
  /// earlier ask that is still coming back was waiting for.
  let grantNeverJumpsALiveWaiter : Invariant =
    { Id = "grant-never-jumps-a-live-waiter"
      Description = "A Granted decision leaves a slot for every live earlier ask: active + liveAhead < cap."
      Check = fun states ->
        firstViolation states (fun r ->
          match r.Decision with
          | Decision.Granted _ ->
            let cap = maxConcurrentFor r.Pressure
            let preGrantCount = List.length r.ActiveAfter - 1
            let liveAhead = liveAheadOf r
            match preGrantCount + List.length liveAhead >= cap with
            | true ->
              Some(
                sprintf
                  "step %d: %s was granted with %d active and %d live earlier ask(s) ahead (cap %d), taking a slot an earlier ask was waiting for"
                  r.AtStep (Holder.describe r.Holder) preGrantCount (List.length liveAhead) cap
              )
            | false -> None
          | Decision.AlreadyHeld _
          | Decision.Queued _
          | Decision.Refused _ -> None) }

  /// NO-ROOM-WITHHELD: a Queued decision only fires when there was no slot left after every live earlier ask had
  /// its own. Room beyond them is never withheld, and an earlier ask that is not coming back reserves nothing.
  let noRoomWithheld : Invariant =
    { Id = "no-room-withheld"
      Description = "A Queued decision only fires when active + liveAhead >= cap: no room is left unused behind a head that cannot go."
      Check = fun states ->
        firstViolation states (fun r ->
          match r.Decision with
          | Decision.Queued _ ->
            let cap = maxConcurrentFor r.Pressure
            let active = List.length r.ActiveAfter
            let liveAhead = liveAheadOf r
            match active + List.length liveAhead < cap with
            | true ->
              Some(
                sprintf
                  "step %d: %s was told to wait with %d active and %d live earlier ask(s) ahead (cap %d): there was room"
                  r.AtStep (Holder.describe r.Holder) active (List.length liveAhead) cap
              )
            | false -> None
          | Decision.Granted _
          | Decision.AlreadyHeld _
          | Decision.Refused _ -> None) }

  /// WITHDRAWN-ASK-LEAVES-THE-LINE: a caller that gives up its queued ask and says so leaves no entry of its own for
  /// that kind in the line. Otherwise the ask sits at the head, a ghost, until it ages out.
  let withdrawnAskLeavesTheLine : Invariant =
    { Id = "withdrawn-ask-leaves-the-line"
      Description = "After a Withdraw, the line holds no ask of that holder for that kind."
      Check = fun states ->
        (List.last states).Withdrawals
        |> List.tryPick (fun w ->
          w.LineAfter
          |> List.tryFind (fun q -> q.Holder = w.Withdrawer && q.Kind = w.WithdrawnKind)
          |> Option.map (fun q ->
            sprintf "step %d: %s withdrew its ask for %s and it is still in the line at seq %d" w.WithdrawnAtStep (Holder.describe w.Withdrawer) (Kind.toToken w.WithdrawnKind) q.Seq))
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  let all : Invariant list =
    [ withdrawnAskLeavesTheLine
      grantNeverExceedsCap
      grantNeverJumpsALiveWaiter
      noRoomWithheld
      everyWaitHasPositiveRetryAfter
      mutualExclusion
      everyLeaseExpires
      refusalNamesTheRealHolder
      sameHolderIdempotent ]

  let violations (states: State list) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check states with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
