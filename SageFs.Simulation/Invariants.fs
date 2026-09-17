namespace SageFs.Simulation

open System
open SageFs
open SageFs.Simulation.Scenario

/// Named invariants — each a stable string ID plus a human message — checked
/// over a folded `Trace`. Every invariant below is claimed to be a TRUE
/// property of the real supervision core: a failure is a genuine bug, not an
/// expected outcome, so failures are reported (with the replay seed), never
/// hidden.
module Invariants =

  /// Outcome of checking one invariant against one trace.
  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    /// The invariant was violated; the message pinpoints where and how.
    | Violated of message: string

  type Invariant =
    { /// Stable identifier, e.g. "give-up-terminal".
      Id: string
      /// Human-readable statement of the property.
      Description: string
      Check: Trace -> Outcome }

  let private isRestart (s: Step) =
    match s.Effect with
    | StepEffect.Restarted _ -> true
    | StepEffect.NoEffect | StepEffect.Stopped | StepEffect.GaveUp _ -> false

  let private isGiveUp (s: Step) =
    match s.Effect with
    | StepEffect.GaveUp _ -> true
    | StepEffect.NoEffect | StepEffect.Stopped | StepEffect.Restarted _ -> false

  /// The startup-crash circuit breaker's fixed delay: a flat 4x-base, capped at
  /// BackoffMax. This is the ONE value a restart delay is allowed to dip to
  /// within a window (see backoffMonotonicInWindow).
  let private circuitBreakerDelay (p: RestartPolicy.Policy) : TimeSpan =
    let fourX = p.BackoffBase.TotalMilliseconds * 4.0
    TimeSpan.FromMilliseconds(min fourX p.BackoffMax.TotalMilliseconds)

  /// give-up-terminal: after a GiveUp (Abandoned) outcome, no later Restart is
  /// ever issued. This is a property of the composition (run + the lifecycle
  /// functions): once the supervisor gives up, the session is Faulted and
  /// terminal. NOTE: `RestartPolicy.decide` ALONE is not terminal across a
  /// ResetWindow expiry — that is deliberate transient-failure recovery. The
  /// terminality is enforced by the lifecycle layer treating Faulted as
  /// terminal, which is exactly what this invariant pins.
  let giveUpTerminal : Invariant =
    { Id = "give-up-terminal"
      Description = "After a GiveUp outcome, no later Restart is ever issued."
      Check = fun t ->
        match t.Steps |> List.tryFindIndex isGiveUp with
        | None -> Outcome.Holds
        | Some i ->
          let giveUpStep = t.Steps.[i]
          match t.Steps |> List.tryFind (fun s -> s.Index > giveUpStep.Index && isRestart s) with
          | Some bad ->
            Outcome.Violated(
              sprintf "Restart at step %d follows a GiveUp at step %d" bad.Index giveUpStep.Index)
          | None -> Outcome.Holds }

  /// backoff-monotonic-in-window: within one crash window (restarts sharing the
  /// same RestartState.WindowStart), restart delays are non-decreasing — up to
  /// the BackoffMax cap — EXCEPT that a delay may dip to the circuit-breaker's
  /// fixed value. A window boundary (WindowStart changes when the ResetWindow
  /// expires and the count resets) legitimately drops the delay back down and
  /// is excluded by segmenting on WindowStart.
  ///
  /// ── DST FINDING (2026-09-17) ────────────────────────────────────────────
  /// The naive form — "ALL restart delays in a window are non-decreasing" — is
  /// FALSE for the real policy, and the simulation found it: under a policy
  /// whose StartupCrashMaxRestarts is high enough to allow a startup crash at a
  /// high restart count, the circuit breaker issues its FIXED 4x-base delay,
  /// which can be LOWER than the exponential delay already reached (e.g. delays
  /// 1s,2s,4s,8s then a rapid crash -> 4s, a drop from 8s within one window).
  /// This is intended circuit-breaker behavior, not a RestartPolicy bug: a
  /// startup crash (host failing to come up) is paired with a much lower give-up
  /// ceiling. It is INVISIBLE under RestartPolicy.defaultPolicy because its
  /// StartupCrashMaxRestarts (3) caps the count before exponential backoff ever
  /// exceeds 4x base.
  ///
  /// So the exact, observable property is: within a window the delay is
  /// non-decreasing EXCEPT for dips to exactly the circuit-breaker delay. We
  /// track the running max and allow a value below it only when it equals that
  /// constant — no inferred "regime" label needed. A worked example pinning the
  /// intended dip lives in the tests.
  let backoffMonotonicInWindow : Invariant =
    { Id = "backoff-monotonic-in-window"
      Description = "Within a crash window, restart delays are non-decreasing (capped), except for dips to the fixed circuit-breaker delay."
      Check = fun t ->
        let cb = circuitBreakerDelay t.Scenario.Policy
        let restartDelays =
          t.Steps
          |> List.choose (fun s ->
            match s.Effect with
            | StepEffect.Restarted delay -> Some(s.Index, s.RestartState.WindowStart, delay)
            | StepEffect.NoEffect | StepEffect.Stopped | StepEffect.GaveUp _ -> None)
        // Fold per (window, running-max). A delay below the running max is only
        // legitimate when it is exactly the circuit-breaker constant.
        let rec loop (state: (DateTime option * TimeSpan) option) items =
          match items with
          | [] -> Outcome.Holds
          | (idx, window, delay) :: rest ->
            match state with
            | Some (prevWindow, runningMax) when prevWindow = window && delay < runningMax && delay <> cb ->
              Outcome.Violated(
                sprintf "delay %O at step %d dropped below the window max %O and is not the circuit-breaker delay %O"
                  delay idx runningMax cb)
            | Some (prevWindow, runningMax) when prevWindow = window ->
              loop (Some (window, max runningMax delay)) rest
            | _ -> loop (Some (window, delay)) rest
        loop None restartDelays }

  /// crash-storm-terminates (LIVENESS): the supervisor never restarts forever.
  /// The number of restarts within any single window is bounded by the
  /// effective ceiling (max of MaxRestarts and StartupCrashMaxRestarts), so a
  /// stream of crashes inside the window always reaches GiveUp in bounded
  /// steps rather than looping Restart indefinitely.
  let crashStormTerminates : Invariant =
    { Id = "crash-storm-terminates"
      Description = "Restarts within any single window are bounded by the ceiling (no infinite restart loop)."
      Check = fun t ->
        let ceiling = max t.Scenario.Policy.MaxRestarts t.Scenario.Policy.StartupCrashMaxRestarts
        let overCeiling =
          t.Steps
          |> List.filter isRestart
          |> List.groupBy (fun s -> s.RestartState.WindowStart)
          |> List.map (fun (window, g) -> window, List.length g)
          |> List.tryFind (fun (_, n) -> n > ceiling)
        match overCeiling with
        | Some (window, n) ->
          Outcome.Violated(
            sprintf "%d restarts in window %A exceeds ceiling %d — restart loop is unbounded" n window ceiling)
        | None -> Outcome.Holds }

  /// All Phase 1 invariants.
  let all : Invariant list =
    [ giveUpTerminal; backoffMonotonicInWindow; crashStormTerminates ]

  /// Check every invariant against a trace, returning (id, outcome) pairs.
  let checkAll (t: Trace) : (string * Outcome) list =
    all |> List.map (fun inv -> inv.Id, inv.Check t)

  /// The invariants that failed for a trace, if any.
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))
