namespace SageFs.Simulation

open SageFs.Cohort
open SageFs.Simulation.CohortLandingSim

/// Named invariants over a drained `LandingTrace`. Same stable-id / `Holds`-vs-
/// `Violated` shape as Phase 1/2's invariant modules. The oracle here is
/// INDEPENDENT of the reducer under test: it inspects the final cohort state and
/// the scripted verdicts only — it never calls `Cohort.decide` — so it is a
/// genuine external check, not a tautology. Each HOLDS for the real reducer and
/// is VIOLATED for the jammed reducer whenever a failing landing sits ahead of
/// another in the queue.
module CohortLandingInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: LandingTrace -> Outcome }

  let private landings (t: LandingTrace) : (LandingId * LandingRequest<Member>) list =
    t.Final.Landings |> Map.toList

  let private verdictOf (t: LandingTrace) (id: LandingId) : Verdict option = Map.tryFind id t.Verdicts

  let private firstViolation (t: LandingTrace) (pick: LandingId * LandingRequest<Member> -> string option) : Outcome =
    match landings t |> List.tryPick pick with
    | Some msg -> Outcome.Violated msg
    | None -> Outcome.Holds

  /// passing-landing-always-lands: every landing whose scripted verdict is
  /// `Passes` reaches `Landed`. This is the roast's no-deadlock property (§5.4):
  /// a landing ahead of it that fails / conflicts / is inconclusive must be popped
  /// so the queue keeps advancing — it must NEVER freeze a healthy landing behind
  /// it. The jammed reducer leaves the failing landing at the head, so a following
  /// `Passes` landing never even starts rebasing — it is caught here, stuck
  /// `Queued`.
  let passingLandingAlwaysLands : Invariant =
    { Id = "passing-landing-always-lands"
      Description = "A landing whose tests pass always reaches Landed — a preceding non-passing landing never freezes the serial queue behind it."
      Check = fun t ->
        firstViolation t (fun (id, req) ->
          match verdictOf t id with
          | Some Verdict.Passes ->
            match req.State with
            | LandingState.Landed _ -> None
            | other ->
              Some(
                sprintf
                  "landing %A (verdict Passes) ended %A, not Landed — a preceding non-passing landing jammed the serial queue"
                  id other)
          | Some Verdict.FailsTests
          | Some Verdict.Conflict
          | Some Verdict.Inconclusive
          | None -> None) }

  /// queue-holds-only-in-flight: at the fixpoint the queue contains only landings
  /// still in flight (`Queued` / `Rebasing` / `Verifying`). A terminal landing
  /// (`Blocked` / `Landed` / `Withdrawn`) left in the queue is precisely the jam —
  /// a landing whose transition should have popped it never did. For the real
  /// reducer the queue drains empty; the jammed reducer leaves the `Blocked`
  /// failing landing sitting at the head.
  let queueHoldsOnlyInFlight : Invariant =
    { Id = "queue-holds-only-in-flight"
      Description = "The landing queue never retains a terminal (Blocked/Landed/Withdrawn) landing — a terminal landing at the head is the queue-jam."
      Check = fun t ->
        match
          t.Final.Queue
          |> List.tryPick (fun id ->
            match Map.tryFind id t.Final.Landings with
            | Some req ->
              match req.State with
              | LandingState.Queued
              | LandingState.Rebasing _
              | LandingState.Verifying _ -> None
              | terminal ->
                Some(sprintf "queue still holds %A in state %A — a terminal landing was never popped (queue-jam)" id terminal)
            | None -> Some(sprintf "queue references unknown landing %A" id))
        with
        | Some msg -> Outcome.Violated msg
        | None -> Outcome.Holds }

  /// every-landing-terminates: at the fixpoint every landing reached a terminal
  /// state (`Landed` / `Blocked` / `Withdrawn`) — none is stranded mid-pipeline
  /// (`Queued` / `Rebasing` / `Verifying`). The jammed reducer strands every
  /// landing behind the jam in `Queued`.
  let everyLandingTerminates : Invariant =
    { Id = "every-landing-terminates"
      Description = "Every requested landing reaches a terminal state at the fixpoint — none is stranded mid-pipeline behind a jam."
      Check = fun t ->
        firstViolation t (fun (id, req) ->
          match req.State with
          | LandingState.Landed _
          | LandingState.Blocked _
          | LandingState.Withdrawn -> None
          | inFlight ->
            Some(sprintf "landing %A never terminated (stuck %A at the fixpoint)" id inFlight)) }

  /// verdict-routes-to-its-blocker: teeth against a vacuous "everything lands" — a
  /// non-passing verdict must land its landing in the MATCHING blocker (fails ->
  /// FailingTests, conflict -> RebaseConflict, inconclusive -> Inconclusive), and
  /// a passing verdict must reach `Landed`. This proves the scripted chaos is
  /// actually exercised, not silently ignored.
  let verdictRoutesToItsBlocker : Invariant =
    { Id = "verdict-routes-to-its-blocker"
      Description = "Each scripted verdict routes its landing to the matching terminal: Passes->Landed, FailsTests->Blocked(FailingTests), Conflict->Blocked(RebaseConflict), Inconclusive->Blocked(Inconclusive)."
      Check = fun t ->
        firstViolation t (fun (id, req) ->
          match verdictOf t id, req.State with
          | Some Verdict.Passes, LandingState.Landed _ -> None
          | Some Verdict.FailsTests, LandingState.Blocked(LandingBlocker.FailingTests _, _) -> None
          | Some Verdict.Conflict, LandingState.Blocked(LandingBlocker.RebaseConflict _, _) -> None
          | Some Verdict.Inconclusive, LandingState.Blocked(LandingBlocker.Inconclusive _, _) -> None
          | Some v, other ->
            Some(sprintf "landing %A (verdict %A) ended %A — the verdict did not route to its expected terminal" id v other)
          | None, _ -> None) }

  let all : Invariant list =
    [ passingLandingAlwaysLands
      queueHoldsOnlyInFlight
      everyLandingTerminates
      verdictRoutesToItsBlocker ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: LandingTrace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
