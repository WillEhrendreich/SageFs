namespace SageFs.Simulation

open SageFs
open SageFs.MemorySupervisor
open SageFs.Simulation.MemoryShedSim

/// Named invariants over a `MemoryShedSim` trace. Same shape as
/// `SupervisorInvariants`/`WorkerLifecycleInvariants`: the oracle only
/// inspects each step's pre/post state and the `Decision` that drove it, and
/// never re-derives the decision itself — a genuine external check, not a
/// tautology.
module MemoryShedInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  /// Every invariant checks the FULL trace (as `List.scan` produces it: one
  /// more state than events, `states.[0]` the pre-event initial state) so
  /// "gone the very next step" can be checked exactly, not just "gone by
  /// the end" — a later `CreateSessionRequest` reusing the same id (a
  /// legitimate new session) must never be mistaken for the old, un-reaped
  /// one.
  type Invariant =
    { Id: string
      Description: string
      Check: State list -> Outcome }

  let private violated fmt = Printf.kprintf (Outcome.Violated) fmt

  /// dead-memory-is-released: every session a decision listed under
  /// `ReapDeadSessions` must be GONE from the state immediately after that
  /// step (`states.[r.AtStep]`, since `step` increments `Step` before
  /// producing the state a `List.scan` index lines up with). This is Job 2's
  /// whole claim, expressed over the trace, and it is the invariant that
  /// must FAIL against `SupervisorBehavior.NeverReapTwin` (see
  /// `MemoryShedSimTests`'s twin proof).
  let deadMemoryIsReleased : Invariant =
    { Id = "dead-memory-is-released"
      Description = "A session named in ReapDeadSessions is gone from the state immediately after that step."
      Check = fun states ->
        let final = List.last states
        final.Decisions
        |> List.tryPick (fun r ->
          let reaped = r.Decision.Actions |> List.collect (function ShedAction.ReapDeadSessions ids -> ids | _ -> [])
          let after = states.[r.AtStep]
          reaped
          |> List.tryFind (fun id -> Map.containsKey id after.Sessions)
          |> Option.map (fun id -> sprintf "step %d reaped %s but it is still present immediately after that step" r.AtStep id))
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// reap-only-targets-dead: every id a decision reaps must have been
  /// `Dead` in that SAME decision's own pre-step snapshot — reaping never
  /// touches a live (Active/Idle) session.
  let reapOnlyTargetsDead : Invariant =
    { Id = "reap-only-targets-dead"
      Description = "ReapDeadSessions never names a session that wasn't Dead in that step's own pre-snapshot."
      Check = fun states ->
        (List.last states).Decisions
        |> List.tryPick (fun r ->
          let reaped = r.Decision.Actions |> List.collect (function ShedAction.ReapDeadSessions ids -> ids | _ -> [])
          reaped
          |> List.tryPick (fun id ->
            match r.PreSessions |> List.tryFind (fun snap -> snap.Id = id) with
            | Some snap when snap.Status <> SessionMemoryStatus.Dead ->
              Some(sprintf "step %d reaped %s but its pre-snapshot status was %A, not Dead" r.AtStep id snap.Status)
            | _ -> None))
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// never-sheds-active: `StopIdleSessions` never names a session the
  /// caller marked `IsUserActive` — the "never kill the session the user is
  /// looking at" guarantee, checked against the exact snapshot the decision
  /// was computed from.
  let neverShedsUserActive : Invariant =
    { Id = "never-sheds-user-active"
      Description = "StopIdleSessions never names a session flagged IsUserActive in that step's pre-snapshot."
      Check = fun states ->
        (List.last states).Decisions
        |> List.tryPick (fun r ->
          let stopped = r.Decision.Actions |> List.collect (function ShedAction.StopIdleSessions ids -> ids | _ -> [])
          stopped
          |> List.tryPick (fun id ->
            match r.PreSessions |> List.tryFind (fun snap -> snap.Id = id) with
            | Some snap when snap.IsUserActive -> Some(sprintf "step %d proposed stopping %s, which was IsUserActive" r.AtStep id)
            | _ -> None))
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// refusal-carries-reason: every `RefuseNewSessions` action's reason is a
  /// real, non-empty string — a refusal that can't say why is the same
  /// silence the whole effort exists to end.
  let refusalCarriesReason : Invariant =
    { Id = "refusal-carries-reason"
      Description = "Every RefuseNewSessions action carries a non-empty reason."
      Check = fun states ->
        (List.last states).Decisions
        |> List.tryPick (fun r ->
          r.Decision.Actions
          |> List.tryPick (function
            | ShedAction.RefuseNewSessions reason when System.String.IsNullOrWhiteSpace reason ->
              Some(sprintf "step %d refused a session with an empty reason" r.AtStep)
            | _ -> None))
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  /// steady-state-ticks-dont-flap: two decisions computed from the
  /// IDENTICAL `(LevelBefore, Machine, PreSessions)` — the full input to
  /// `MemorySupervisor.step`, `Thresholds` being constant across a
  /// scenario — must agree on the resulting `Level`. The hysteresis in
  /// `MemorySupervisor.nextLevel` guarantees a stable input never flips the
  /// level; this is that guarantee checked over the trace rather than just
  /// unit-tested against `nextLevel` directly. Comparing `PreSessions` and
  /// `Clock` alone is NOT enough — the sim's own `Machine` (bytes,
  /// other-usage) or `LevelBefore` (a strictly earlier decision's own
  /// output) can differ while the session-status view looks identical,
  /// which is exactly the false positive an earlier version of this
  /// invariant hit.
  let steadyStateTicksDontFlap : Invariant =
    { Id = "steady-state-ticks-dont-flap"
      Description = "Two decisions computed from an identical (LevelBefore, Machine, PreSessions) must agree on Level."
      Check = fun states ->
        (List.last states).Decisions
        |> List.pairwise
        |> List.tryPick (fun (a, b) ->
          match a.LevelBefore = b.LevelBefore, a.Machine = b.Machine, a.PreSessions = b.PreSessions with
          | true, true, true when a.Decision.Level <> b.Decision.Level ->
            Some(sprintf "steps %d and %d saw identical (LevelBefore, Machine, PreSessions) but levels differ: %A vs %A" a.AtStep b.AtStep a.Decision.Level b.Decision.Level)
          | _ -> None)
        |> function
           | Some msg -> Outcome.Violated msg
           | None -> Outcome.Holds }

  let all : Invariant list =
    [ deadMemoryIsReleased; reapOnlyTargetsDead; neverShedsUserActive; refusalCarriesReason; steadyStateTicksDontFlap ]

  let violations (states: State list) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check states with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
