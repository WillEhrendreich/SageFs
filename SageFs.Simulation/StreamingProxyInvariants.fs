namespace SageFs.Simulation

open SageFs.Simulation.StreamingProxySim

/// Named invariants over a drained `StreamingProxySim.Trace` /
/// `MultiRunTrace`. Same stable-id / `Holds`-vs-`Violated` shape as
/// `FileReloadRoutingInvariants` / `EvalActorInvariants`.
module StreamingProxyInvariants =

  [<RequireQualifiedAccess>]
  type Outcome =
    | Holds
    | Violated of message: string

  type Invariant =
    { Id: string
      Description: string
      Check: Trace -> Outcome }

  /// never-throws-at-the-caller: the run's final Outcome is never `Threw`,
  /// for ANY ordering of ops. This is the headline contract the bug
  /// violated — a cancelled run answers with a value, not an exception.
  let neverThrows : Invariant =
    { Id = "never-throws-at-the-caller"
      Description =
        "The proxy boundary's outcome is always one of Completed/TimedOut/Cancelled — never an exception escaping to the caller, regardless of how CallerCancels is ordered against BodyStarts."
      Check = fun t ->
        match t.Final.Ended with
        | Some (StreamingProxySim.Outcome.Threw reason) ->
          Outcome.Violated(
            sprintf "reducer=%s seed=%d ops=%A — run threw instead of answering with a value: %s" t.Reducer t.Scenario.Seed t.Scenario.Ops reason)
        | _ -> Outcome.Holds }

  /// cancelled-run-says-cancelled: if `CallerCancels` is the FIRST
  /// terminal-triggering op in the scenario (i.e. no StreamDone or
  /// ReadTimeoutFires precedes it), the final Outcome is EXACTLY Cancelled
  /// — never TimedOut, never Completed, and (per neverThrows) never Threw.
  /// A cancel that arrives AFTER a run has already reached a different
  /// terminal state is deliberately excluded here — it is noise on an
  /// already-decided run, covered by `lateCancelNeverOverridesADecidedRun`.
  let cancelledRunSaysCancelled : Invariant =
    { Id = "cancelled-run-says-cancelled"
      Description =
        "A run whose FIRST terminal event is CallerCancels always resolves to StreamOutcome.Cancelled — the cancellation contract holds whether the run has started, is mid-read, or never got a chance to start at all."
      Check = fun t ->
        let firstTerminal =
          t.Scenario.Ops
          |> List.tryFind (fun op ->
            match op with
            | RunOp.StreamDone | RunOp.ReadTimeoutFires | RunOp.CallerCancels -> true
            | _ -> false)
        match firstTerminal with
        | Some RunOp.CallerCancels ->
          match t.Final.Ended with
          | Some StreamingProxySim.Outcome.Cancelled -> Outcome.Holds
          | other ->
            Outcome.Violated(
              sprintf "reducer=%s seed=%d ops=%A — first terminal op was CallerCancels but final outcome was %A, not Cancelled" t.Reducer t.Scenario.Seed t.Scenario.Ops other)
        | _ -> Outcome.Holds }

  /// late-cancel-never-overrides-a-decided-run: once a run has genuinely
  /// reached Completed or TimedOut, a CallerCancels arriving afterwards
  /// (e.g. a stale supersede signal delivered late) must never retroactively
  /// flip the reported outcome — the caller already has its answer.
  let lateCancelNeverOverridesADecidedRun : Invariant =
    { Id = "late-cancel-never-overrides-a-decided-run"
      Description =
        "A CallerCancels that arrives strictly after the run already reached Completed or TimedOut does not change the final outcome."
      Check = fun t ->
        let rec firstTerminalIdx (ops: RunOp list) (idx: int) =
          match ops with
          | [] -> None
          | op :: rest ->
            match op with
            | RunOp.StreamDone -> Some(idx, StreamingProxySim.Outcome.Completed)
            | RunOp.ReadTimeoutFires -> Some(idx, StreamingProxySim.Outcome.TimedOut)
            | RunOp.CallerCancels -> Some(idx, StreamingProxySim.Outcome.Cancelled)
            | _ -> firstTerminalIdx rest (idx + 1)
        match firstTerminalIdx t.Scenario.Ops 0 with
        | Some(_, ((StreamingProxySim.Outcome.Completed | StreamingProxySim.Outcome.TimedOut) as expected)) ->
          match t.Final.Ended = Some expected with
          | true -> Outcome.Holds
          | false ->
            Outcome.Violated(
              sprintf "reducer=%s seed=%d ops=%A — the run had already decided %A, but a later op changed the final outcome to %A" t.Reducer t.Scenario.Seed t.Scenario.Ops expected t.Final.Ended)
        | _ -> Outcome.Holds }

  let all : Invariant list = [ neverThrows; cancelledRunSaysCancelled; lateCancelNeverOverridesADecidedRun ]

  /// The invariants that failed for a trace, if any (id, message).
  let violations (t: Trace) : (string * string) list =
    all
    |> List.choose (fun inv ->
      match inv.Check t with
      | Outcome.Holds -> None
      | Outcome.Violated msg -> Some(inv.Id, msg))

  // ── Multi-run: an outcome is never attributed to the wrong run ─────────

  [<RequireQualifiedAccess>]
  type MultiOutcome =
    | Holds
    | Violated of message: string

  /// no-cross-run-attribution: every run's outcome in the INTERLEAVED
  /// timeline equals what that SAME run alone (no interleaving with any
  /// other run's events) would have produced. This is a genuine external
  /// oracle — `foldIsolated` never reads the interleaved fold's state — so
  /// a shared-window/shared-token bug (one run's cancel or completion
  /// bleeding into another's) would show up as a real divergence here, not
  /// a tautology against the same fold being checked against itself.
  let noCrossRunAttribution (t: MultiRunTrace) : (int * MultiOutcome) list =
    t.Isolated
    |> Map.toList
    |> List.map (fun (runId, isolatedState) ->
      let interleavedState = t.Interleaved |> Map.tryFind runId
      match interleavedState = Some isolatedState with
      | true -> runId, MultiOutcome.Holds
      | false ->
        runId,
        MultiOutcome.Violated(
          sprintf
            "seed=%d run #%d — interleaved outcome %A does not match isolated outcome %A; another run's events leaked into this run's fold (ops=%A)"
            t.Scenario.Seed runId interleavedState isolatedState t.Scenario.InterleavedOps)
    )
