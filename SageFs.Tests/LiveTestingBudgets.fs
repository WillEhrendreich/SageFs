namespace SageFs.Tests

/// ── The live-testing suite's timings, named once ──────────────────────
///
/// WHY this file exists: these numbers were inline `TimeSpan.FromSeconds
/// 60.0` literals repeated fifteen times across two integration suites, so a
/// change to any of them was fifteen edits, and the two suites could drift
/// apart while disagreeing about how long the same queue takes. The repo
/// already names the sibling constant (`daemonStartupHealthTimeout`); this is
/// the same idea applied to the waits that were left anonymous.
///
/// The two budgets are different facts and are kept apart deliberately:
///   - `baselineRun`   — how long a QUEUED live-testing run gets to produce
///                       results. Paid only while a run is outstanding.
///   - `sessionReady`  — how long a daemon gets to publish a ready session.
///                       Paid once per daemon start.
///
/// Neither is a "reasonable timeout" chosen by feel: each is a property of the
/// thing being waited on, and the retry schedules elsewhere are derived from
/// budgets rather than hardcoded alongside them.
///
/// A `let`, not `[<Literal>]`: a TimeSpan is not a valid literal.
module LiveTestingBudgets =

  /// How long a queued live-testing run gets to reach a settled state.
  let baselineRun : System.TimeSpan = System.TimeSpan.FromSeconds 60.0

  /// How long a daemon gets to publish a ready session.
  let sessionReady : System.TimeSpan = System.TimeSpan.FromSeconds 60.0

  /// How often a wait re-reads the status it is waiting on. Derived rather
  /// than repeated, so a polling loop and its budget cannot disagree.
  let pollInterval : System.TimeSpan = System.TimeSpan.FromMilliseconds 250.0
