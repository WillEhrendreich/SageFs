namespace SageFs.Tests

/// ── The suites that wait on a live worker ────────────────────────────
///
/// WHY a shared group name: Expecto runs test LISTS in parallel, and the
/// `--integration-host` tier starts 58 daemons across 62 ports. Two suites
/// WAIT for a worker to reach a ready state rather than just asserting on
/// something already in memory, and under that contention the worker's test
/// proxy does not become available inside their wait. Both then time out with
/// every test `Stale` — a run that really was dispatched, reported as no
/// result.
///
/// The fix is scheduling, not a longer timeout: raising the wait budget from
/// 60s to 180s was measured and made it worse (69s/129s -> 188s/368s), because
/// the run is never going to arrive. Both tests pass alone in ~10s.
///
/// The name lives here so the two files cannot disagree about which suites
/// they are — a repeated string literal across files is exactly the thing that
/// silently stops sequencing, leaving a green-looking but unordered tier.
module LiveTestingWorkerSuites =

  /// The shared sequencing group. Both worker-waiting suites join it, so they
  /// run in order relative to each other and never overlap.
  [<Literal>]
  let groupName = "live-testing-workers"
