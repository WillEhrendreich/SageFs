namespace SageFs.Simulation

open System
open SageFs
open SageFs.Simulation.Scenario
open SageFs.Simulation.WorkerLifecycleSim

/// Deterministic delta-debugging (ddmin-style shrinking) for DST failures.
///
/// Today a failing scenario prints its WHOLE event/command list (1-40+
/// entries, see `SimulationTests.assertHolds`); that is unreadable as a bug
/// report. `Shrink` takes a scenario/command list and a "still fails"
/// predicate and returns a MINIMAL sublist that still reproduces the
/// failure — the same technique used to shrink failing FsCheck properties,
/// applied here to the hand-rolled `Scenario`/`MgrScenario` chaos-as-data
/// families that FsCheck's own generic shrinker does not know how to shrink.
///
/// Design principles (same as the rest of the sim harness):
///   * Pure, deterministic, no IO, no `System.Random` — `ddmin` never calls
///     randomness; the SAME (predicate, input) pair always shrinks to the
///     SAME output.
///   * The predicate is supplied by the caller — `Shrink` has no opinion on
///     WHAT "still fails" means; `failsAnyInvariant` is the one canonical
///     predicate over `Scenario` this module ships, so it has no build
///     dependency on the (not-yet-landed) reference-model Oracle (Brief B1).
///     A hypothetical `failsOracle` predicate can be added as a separate
///     `let` later without touching `ddmin`/`shrinkScenario` at all.
module Shrink =

  /// Generic, pure delta-debugging minimizer (ddmin, Zeller-style).
  ///
  /// PRECONDITION: `stillFails xs` is true — shrinking a passing input is
  /// meaningless and `ddmin` does not special-case it (callers that may be
  /// handed a passing input, e.g. `shrinkScenario`/`shrinkMgr`, check first
  /// and return the input unchanged rather than calling `ddmin`).
  ///
  /// CONTRACT:
  ///   * Deterministic — same (stillFails, xs) always yields the same output
  ///     (no randomness, no time, no external state).
  ///   * Terminating — each iteration either shrinks the list or narrows the
  ///     granularity, and granularity is bounded by the list length.
  ///   * Sound — `stillFails (ddmin stillFails xs) = true` whenever the
  ///     precondition holds (every returned list still fails).
  ///   * Minimal (1-minimal / locally minimal) — no single further chunk can
  ///     be removed from the result without `stillFails` going false.
  ///   * Length-monotonic — `List.length (ddmin stillFails xs) <= List.length xs`.
  let ddmin<'a> (stillFails: 'a list -> bool) (xs: 'a list) : 'a list =
    // Zeller's ddmin: start by trying to remove ONE HALF at a time; every
    // time a complement (the list with one chunk removed) still fails, adopt
    // it and reset the chunk count down (coarser next pass); every time NO
    // chunk removal at the current granularity reproduces the failure, make
    // the chunks smaller (finer next pass) until chunks are single elements,
    // at which point no further progress is possible and the result is
    // 1-minimal.
    let rec loop (elements: 'a list) (chunkCount: int) : 'a list =
      let len = List.length elements
      if len < 2 then
        elements
      else
        let subsetLength = max 1 (len / chunkCount)
        // Try removing each chunk of `subsetLength` elements in turn (left to
        // right); the first removal whose complement still fails is adopted
        // immediately (greedy — deterministic because the scan order is
        // fixed).
        let rec scanFrom (start: int) : 'a list option =
          if start >= len then
            None
          else
            let stop = min len (start + subsetLength)
            let complement = (List.take start elements) @ (List.skip stop elements)
            if stillFails complement then Some complement else scanFrom stop
        match scanFrom 0 with
        | Some complement ->
          // A chunk was removable: keep shrinking, but do not let the chunk
          // count collapse below 2 (2 chunks = trying to remove one half).
          loop complement (max (chunkCount - 1) 2)
        | None ->
          if chunkCount >= len then
            // Already down to single-element chunks and none is removable:
            // this is a local (1-minimal) fixed point.
            elements
          else
            // No chunk of this size was removable: refine to smaller chunks.
            loop elements (min (chunkCount * 2) len)
    loop xs 2

  /// The canonical predicate over Phase-1 `Scenario`s: "the real supervision
  /// core, folded through `Runner.run`, violates at least one named
  /// invariant." Independent of any reference-model oracle — see the module
  /// doc for why `Shrink` has no hard dependency on Brief B1's `Oracle`.
  let failsAnyInvariant (scn: Scenario) : bool =
    Invariants.violations (Runner.run scn) <> []

  /// Shrink one `ClockAdvance` span at a time toward `TimeSpan.Zero`, keeping
  /// only the change when the predicate (evaluated over the FULL candidate
  /// event list) still fails. A single deterministic left-to-right pass —
  /// not a binary search of the span's magnitude — because for these DST
  /// scenarios only two values of a span matter to the policy: "zero" (no
  /// time passes) and "whatever the original was"; the policy's own window
  /// thresholds are already exercised by which events are adjacent, not by
  /// the exact non-zero magnitude.
  let private shrinkSpansTowardZero (stillFails: SimEvent list -> bool) (events: SimEvent list) : SimEvent list =
    let arr = List.toArray events
    for i in 0 .. arr.Length - 1 do
      match arr.[i] with
      | SimEvent.ClockAdvance span when span <> TimeSpan.Zero ->
        let original = arr.[i]
        arr.[i] <- SimEvent.ClockAdvance TimeSpan.Zero
        if not (stillFails (List.ofArray arr)) then
          arr.[i] <- original // zeroing this span loses the failure — keep it
      | SimEvent.ClockAdvance _
      | SimEvent.WorkerCrashed
      | SimEvent.WorkerExitedGracefully -> ()
    List.ofArray arr

  /// Shrink `Policy` toward `RestartPolicy.defaultPolicy`, one field at a
  /// time, keeping only fields whose default value still reproduces the
  /// failure (evaluated over the full candidate scenario). Field order
  /// follows the record's own declaration order.
  let private shrinkPolicyToward
    (stillFails: RestartPolicy.Policy -> bool)
    (policy: RestartPolicy.Policy)
    : RestartPolicy.Policy =
    let dflt = RestartPolicy.defaultPolicy
    let tryField (current: RestartPolicy.Policy) (candidate: RestartPolicy.Policy) =
      if candidate = current then current
      elif stillFails candidate then candidate
      else current
    policy
    |> fun p -> tryField p { p with MaxRestarts = dflt.MaxRestarts }
    |> fun p -> tryField p { p with BackoffBase = dflt.BackoffBase }
    |> fun p -> tryField p { p with BackoffMax = dflt.BackoffMax }
    |> fun p -> tryField p { p with ResetWindow = dflt.ResetWindow }
    |> fun p -> tryField p { p with StartupCrashWindow = dflt.StartupCrashWindow }
    |> fun p -> tryField p { p with StartupCrashMaxRestarts = dflt.StartupCrashMaxRestarts }

  /// Shrink a failing Phase-1 `Scenario` to a minimal reproducer:
  ///   1. `ddmin` the `Events` list (the dominant source of size).
  ///   2. Shrink surviving `ClockAdvance` spans toward zero.
  ///   3. Shrink `Policy` toward `RestartPolicy.defaultPolicy`, field by field.
  /// Each step only keeps a change that still satisfies `stillFails`. Returns
  /// the ORIGINAL scenario, unchanged, if it does not fail to begin with —
  /// shrinking a passing scenario is a no-op, never an error.
  let shrinkScenario (stillFails: Scenario -> bool) (scn: Scenario) : Scenario =
    if not (stillFails scn) then
      scn
    else
      let minEvents = ddmin (fun evs -> stillFails { scn with Events = evs }) scn.Events
      let spanShrunkEvents =
        shrinkSpansTowardZero (fun evs -> stillFails { scn with Events = evs }) minEvents
      let minPolicy =
        shrinkPolicyToward (fun p -> stillFails { scn with Events = spanShrunkEvents; Policy = p }) scn.Policy
      { scn with Events = spanShrunkEvents; Policy = minPolicy }

  /// Shrink a failing Phase-2 `MgrScenario` to a minimal reproducer: `ddmin`
  /// the `Commands` list. Returns the original scenario unchanged if it does
  /// not fail to begin with.
  let shrinkMgr (stillFails: MgrScenario -> bool) (scn: MgrScenario) : MgrScenario =
    if not (stillFails scn) then
      scn
    else
      let minCommands = ddmin (fun cmds -> stillFails { scn with Commands = cmds }) scn.Commands
      { scn with Commands = minCommands }
