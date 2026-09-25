namespace SageFs

// ── How long to wait for a worker's test proxy to appear ──────────────
//
// WHY this is its own pure module: a live-testing run needs a live worker
// (`GetStreamingTestProxy` resolves `WorkerBaseUrls` from the session
// snapshot). If that lookup has not settled, the run is dispatched with no
// proxy and every test becomes `NotRun` — the suite then times out waiting
// for results that were never going to arrive, and the failure message reads
// "0 passed, 0 failed" rather than "the worker never registered".
//
// The history here is the point. An earlier version waited 10 seconds. It was
// shortened to four fixed attempts — 50/100/200/400ms, 750ms total — to speed
// up the common case where the worker is already up. That is fine when one
// daemon is running and actively wrong when the integration tier starts 250:
// the shortening was measured against a warm worker and kept as a fixed
// constant, so under contention every run failed at 750ms.
//
// A fixed attempt count cannot express this, because the thing being waited on
// has no fixed latency — it depends on how loaded the machine is. The budget
// is therefore a DEADLINE, and the delays are derived from it. The safe failure
// direction is preserved: a long wait only costs time, while a short one
// strands a real run and reports a false result.

module WorkerProxyWait =

  /// The ceiling on how long we will wait for a worker to register before
  /// giving up and dispatching `NotRun`. Generous on purpose — the wait is
  /// bounded and only paid when the worker is genuinely absent, while the
  /// cost of cutting it short is a silently unrun suite.
  [<Literal>]
  let BudgetMs = 15000

  /// The first delay. Small, so a worker that is already up is picked up
  /// immediately and the common case stays fast.
  [<Literal>]
  let FirstDelayMs = 50

  /// Delays to try, growing, bounded by `budgetMs`.
  ///
  /// Two properties matter, and the second is easy to get wrong. The total
  /// never exceeds the budget, and the sequence is STRICTLY INCREASING. The
  /// obvious implementations both fail the second: clamping the last delay
  /// with `min next remaining` yields `…6400; 2250` — a backwards step — which
  /// a property test caught here.
  ///
  /// A short remainder is therefore DROPPED rather than emitted. Waiting a
  /// fraction of the previous step buys nothing over waiting the step itself,
  /// and the budget is a ceiling, not a quota: at 15000ms this yields
  /// 50…6400 (12750ms), which is under budget by design. A remainder is only
  /// emitted when it is longer than the last full delay.
  ///
  /// A non-positive budget or a non-positive first delay yields no retries, so
  /// a caller can never silently get an empty schedule and mistake it for
  /// "looked once".
  let delays (budgetMs: int) (firstDelayMs: int) : int list =
    if budgetMs <= 0 || firstDelayMs <= 0 then
      []
    else
      let rec grow acc next remaining =
        if next <= remaining then
          grow (next :: acc) (next * 2) (remaining - next)
        else
          match acc with
          | [] -> List.rev [ remaining ]
          | last :: _ when remaining > last -> List.rev (remaining :: acc)
          | _ -> List.rev acc

      grow [] firstDelayMs budgetMs

  /// The default schedule: the budget, doubling from the first delay.
  let delaysByDefault = delays BudgetMs FirstDelayMs

  /// Whether a wait is over. Named so a caller's intent reads, and so the
  /// deadline is the thing being reasoned about rather than a loop counter.
  let isExpired (elapsedMs: int) (budgetMs: int) = elapsedMs >= budgetMs
