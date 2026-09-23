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
          | Decision.Wait _
          | Decision.Refused _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// every-wait-carries-a-positive-retry-after: `Decision.Wait` must never
  /// tell a caller to retry in zero or negative time — that would just be a
  /// busy-loop wearing a Wait costume.
  let everyWaitHasPositiveRetryAfter : Invariant =
    { Id = "every-wait-has-positive-retry-after"
      Description = "Every Wait decision carries a strictly positive retryAfter."
      Check = fun states ->
        (List.last states).Decisions
        |> List.tryPick (fun r ->
          match r.Decision with
          | Decision.Wait(retryAfter, _) when retryAfter <= System.TimeSpan.Zero ->
            Some(sprintf "step %d: Wait carried a non-positive retryAfter %A" r.AtStep retryAfter)
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

  let all : Invariant list = [ grantNeverExceedsCap; everyWaitHasPositiveRetryAfter ]

  let violations (states: State list) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check states with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
