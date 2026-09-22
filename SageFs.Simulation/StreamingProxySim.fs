namespace SageFs.Simulation

/// Deterministic Simulation Testing (DST) for the streaming-test-proxy
/// cancellation contract fixed in `SageFs.Core/HttpWorkerClient.fs`
/// (`streamingTestProxy` / `streamingTestProxyWithCoverage`).
///
/// THE BUG: `StreamingProxyTests.fs`'s "cancelling the run cancels the
/// in-flight read" errored once in CI with a raw `TaskCanceledException`,
/// after passing eleven gates the same day. Root cause, confirmed
/// empirically in the SageFs REPL before any code changed: the old proxy
/// read the AMBIENT `Async.CancellationToken` and was started via
/// `Async.StartAsTask(proxy, cancellationToken = run.Token)` — the SAME
/// token object handed to BOTH places. `Async.StartAsTask` wires its OWN
/// forced-cancellation callback against that token the instant it is
/// called, independent of whatever the proxy's own body is doing. If the
/// caller cancels before the .NET thread pool has actually begun running
/// the proxy's streaming-read body (or before that body's own
/// cancellation-observing read has registered), the starter's forced
/// cancellation wins the race and the CALLER sees a thrown exception
/// instead of the graceful `StreamOutcome.Cancelled` the proxy's own
/// `streamRun` `try ... with :? OperationCanceledException -> return
/// StreamOutcome.Cancelled` was always ready to produce. Reproduced 600/600
/// times cancelling synchronously before/just after `Async.StartAsTask`;
/// 0/400 times once the token was threaded as a plain function parameter
/// with nothing else registered against it a second way (the fix this sim
/// pins).
///
/// WHY A MODEL, NOT A LITERAL FOLD: unlike most sims in this project
/// (`FileReloadRoutingSim`, `EvalActorSim`, ...), the subject here is a
/// genuine OS-level scheduling race over real `Task`/`CancellationToken`
/// machinery, not a pure decision function `SageFs.Core` already exposes —
/// there is no pure core to fold directly. What DST replaces is the
/// scheduling nondeterminism itself: which of "the streaming body starts
/// running" and "the caller cancels" happens first is turned into an
/// explicit, seeded, replayable choice (`RunOp` ordering) instead of a wall
/// clock. The REAL reducer (`stepReal`) encodes the CONTRACT the fix
/// establishes — a cancelled run always resolves to a value, never throws,
/// regardless of ordering — and the TWIN (`stepTwinDoubleTokenRace`)
/// reintroduces the exact historical mechanism so the invariants are proven
/// to have teeth against the real bug, not a strawman.
module StreamingProxySim =

  /// One scripted event in a single run's timeline.
  ///   - `BodyStarts`: the thread pool actually begins executing the
  ///     streaming read loop (registers its OWN cancellation observation).
  ///   - `SchedulerTick`: a no-op standing in for unrelated thread-pool work
  ///     (contention) — lets a scenario push `BodyStarts` arbitrarily late
  ///     relative to `CallerCancels` without changing the outcome logic.
  ///   - `LineArrives`: a keep-alive/data line arrives from the worker
  ///     (re-arms the inactivity window; does not end the run).
  ///   - `StreamDone`: `event: done` / EOF — the worker finished cleanly.
  ///   - `ReadTimeoutFires`: the inactivity window expired.
  ///   - `CallerCancels`: the run's owner cancels (a newer run superseded
  ///     it, the daemon is stopping).
  [<RequireQualifiedAccess>]
  type RunOp =
    | BodyStarts
    | SchedulerTick
    | LineArrives
    | StreamDone
    | ReadTimeoutFires
    | CallerCancels

  /// A fully-specified, replayable single-run scenario.
  type Scenario = { Seed: int; Ops: RunOp list }

  /// What the proxy boundary reports to ITS caller. `Threw` is not a
  /// legitimate member of the real contract's range — the fixed proxy never
  /// produces it, no matter the ordering; only the twin can.
  [<RequireQualifiedAccess>]
  type Outcome =
    | Completed
    | TimedOut
    | Cancelled
    | Threw of reason: string

  /// Folding state for one run.
  type RunState = { Started: bool; Ended: Outcome option }

  let private initial : RunState = { Started = false; Ended = None }

  /// THE FIX's contract: the caller's cancellation token is threaded as an
  /// explicit VALUE into the run's own graceful, Task-based handling, and
  /// nothing else independently races that token against the run's
  /// completion — so a cancel is ALWAYS a value, a `StreamOutcome`, in every
  /// position relative to `BodyStarts`. Once a run has reached a terminal
  /// state, later ops are noise (mirrors a superseded run whose late events
  /// must not retroactively change an already-reported outcome).
  let private stepReal (s: RunState) (op: RunOp) : RunState =
    match s.Ended with
    | Some _ -> s
    | None ->
      match op with
      | RunOp.BodyStarts -> { s with Started = true }
      | RunOp.SchedulerTick
      | RunOp.LineArrives -> s
      | RunOp.StreamDone -> { s with Ended = Some Outcome.Completed }
      | RunOp.ReadTimeoutFires -> { s with Ended = Some Outcome.TimedOut }
      | RunOp.CallerCancels -> { s with Ended = Some Outcome.Cancelled }

  /// TWIN — reintroduces the historical bug: the caller's token was BOTH
  /// read internally (the old `underCallerToken`'s `Async.CancellationToken`)
  /// AND handed a second time to `Async.StartAsTask`'s own
  /// `cancellationToken` parameter, whose forced-cancellation callback is
  /// registered the instant the async is started — before the body has
  /// necessarily begun running. If `CallerCancels` arrives before
  /// `BodyStarts` has had a chance to register the run's OWN cancellation
  /// handling, the starter's registration wins the race and the caller
  /// observes a raw `TaskCanceledException` instead of a `Cancelled` value.
  let private stepTwinDoubleTokenRace (s: RunState) (op: RunOp) : RunState =
    match s.Ended with
    | Some _ -> s
    | None ->
      match op with
      | RunOp.CallerCancels when not s.Started ->
        { s with
            Ended =
              Some(
                Outcome.Threw
                  "TaskCanceledException — Async.StartAsTask's forced-cancellation callback (registered against the SAME token at start time) won the race before the run's own OperationCanceledException handling had registered") }
      | _ -> stepReal s op

  type Trace = { Scenario: Scenario; Final: RunState; Reducer: string }

  let private runWith (name: string) (step: RunState -> RunOp -> RunState) (scenario: Scenario) : Trace =
    { Scenario = scenario; Final = List.fold step initial scenario.Ops; Reducer = name }

  /// Run through the FIXED contract (explicit token, no competing starter).
  let run (scenario: Scenario) : Trace =
    runWith "real (explicit-token, no competing starter registration)" stepReal scenario

  /// Run through the twin that reintroduces the double-token race.
  let runDoubleTokenRace (scenario: Scenario) : Trace =
    runWith "twin-double-token-race (Async.CancellationToken + Async.StartAsTask cancellationToken=ct)" stepTwinDoubleTokenRace scenario

  // ── Multi-run attribution ────────────────────────────────────────────
  //
  // "An outcome is never attributed to the wrong run." Several
  // `streamingTestProxy` calls can be in flight concurrently (a superseding
  // run created while the previous one is still draining); each carries its
  // own token and its own `streamRun`/`InactivityWindow`, so one run's
  // events must never influence another's fold. This is modelled as an
  // interleaved timeline of (RunId, RunOp) pairs folded through independent
  // per-run state, and proven against the SAME ops replayed for that run in
  // ISOLATION — a real cross-check, not a tautology: if the fold ever
  // shared state across runs (e.g. one InactivityWindow reused, or a stale
  // run's Cancelled bleeding into a fresh run's Outcome), interleaved and
  // isolated results would diverge.

  type MultiRunScenario = { Seed: int; InterleavedOps: (int * RunOp) list }

  /// Fold an interleaved multi-run timeline through independent per-run
  /// states, keyed by RunId — never a shared window, never a shared token.
  let foldInterleaved (ops: (int * RunOp) list) : Map<int, RunState> =
    ops
    |> List.fold
      (fun (states: Map<int, RunState>) (runId, op) ->
        let current = states |> Map.tryFind runId |> Option.defaultValue initial
        Map.add runId (stepReal current op) states)
      Map.empty

  /// Fold one run's own ops in isolation (no other run's events present at
  /// all) — the independent ground truth an interleaved run's outcome must
  /// match.
  let foldIsolated (ops: RunOp list) : RunState =
    List.fold stepReal initial ops

  /// This run's own ops, in order, extracted from an interleaved timeline —
  /// the isolation oracle's input.
  let private opsForRun (runId: int) (ops: (int * RunOp) list) : RunOp list =
    ops |> List.choose (fun (r, op) -> if r = runId then Some op else None)

  type MultiRunTrace =
    { Scenario: MultiRunScenario
      Interleaved: Map<int, RunState>
      /// RunId -> what that run alone (no interleaving) would have ended as.
      Isolated: Map<int, RunState> }

  let runMulti (scenario: MultiRunScenario) : MultiRunTrace =
    let interleaved = foldInterleaved scenario.InterleavedOps
    let runIds = scenario.InterleavedOps |> List.map fst |> List.distinct
    let isolated =
      runIds
      |> List.map (fun runId -> runId, foldIsolated (opsForRun runId scenario.InterleavedOps))
      |> Map.ofList
    { Scenario = scenario; Interleaved = interleaved; Isolated = isolated }
