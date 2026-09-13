namespace SageFs.Features

open SageFs
open SageFs.Features.LiveTesting

/// Item 13a of sagefs-multiagent-vision.md: pure conversion from one session's
/// live-testing view into the Passing/Failing/Stale `Cohort.TestId` lists and
/// the monotonic generation a `Cohort.SessionSnapshot` needs. No IO, no
/// session→member resolution (that is the shell's concern — see DaemonMode.fs
/// and CohortOwner.fs's `getSessionSnapshots` seam).
///
/// `LiveTesting.TestId` (this feature's stable per-test identity,
/// LiveTestingTypes.fs:90-100) and `Cohort.TestId` (the cohort core's local,
/// deliberately minimal test identity, Cohort.fs:79) are distinct types by
/// design (Cohort.fs:74-78) — this module is the one place that converts
/// between them.
module CohortTestProjection =

  /// The only conversion site between `LiveTesting.TestId` and `Cohort.TestId`.
  let toCohortTestId (testId: TestId) : Cohort.TestId =
    Cohort.TestId(TestId.value testId)

  /// One session's Pass/Fail/Stale `Cohort.TestId` lists.
  type SessionTestProjection = {
    PassingTests: Cohort.TestId list
    FailingTests: Cohort.TestId list
    StaleTests: Cohort.TestId list
  }

  module SessionTestProjection =
    let empty = { PassingTests = []; FailingTests = []; StaleTests = [] }

    /// Classifies each entry's `TestRunStatus` the same way
    /// `TestSummary.fromStatuses` counts them (LiveTestingTypes.fs:1805-1814):
    /// `Passed`/`Failed`/`Stale` only. `Detected`/`Queued`/`Running`/`Skipped`/
    /// `PolicyDisabled` contribute to none of the three bitplanes — they are
    /// not yet a settled pass/fail/stale outcome.
    ///
    /// `entries` is expected to already be filtered to one session, i.e. the
    /// output of `LiveTestState.statusEntriesForSession sessionId state`
    /// (LiveTestingTypes.fs:1625-1634) — this function does no session
    /// filtering of its own.
    let ofStatusEntries (entries: TestStatusEntry array) : SessionTestProjection =
      let mutable passing = []
      let mutable failing = []
      let mutable stale = []
      for e in entries do
        match e.Status with
        | TestRunStatus.Passed _ -> passing <- toCohortTestId e.TestId :: passing
        | TestRunStatus.Failed _ -> failing <- toCohortTestId e.TestId :: failing
        | TestRunStatus.Stale -> stale <- toCohortTestId e.TestId :: stale
        | TestRunStatus.Detected
        | TestRunStatus.Queued
        | TestRunStatus.Running
        | TestRunStatus.Skipped _
        | TestRunStatus.PolicyDisabled -> ()
      { PassingTests = passing; FailingTests = failing; StaleTests = stale }

  /// A session's generation for `Cohort.SessionSnapshot.Generation`
  /// (Cohort.fs:856, threaded into `CohortFrame.SessionGens` by `project` so a
  /// frame consumer can tell a session's test view moved since the last read).
  /// `LastGeneration` (bumped on every completed test run, LiveTestingTypes.fs:
  /// 1472) and `DiscoveryGeneration` (bumped on every meaningful
  /// `TestsDiscovered` merge, LiveTestingTypes.fs:1491-1495) advance
  /// independently — a re-run with no new discovery bumps only the first, a
  /// re-discovery with no new run bumps only the second — so `max` of the two
  /// is the only combination that is monotonic across both kinds of session
  /// activity. Documented here because callers must not swap this for either
  /// counter alone.
  let generationOf (state: LiveTestState) : int64 =
    max (int64 (RunGeneration.value state.LastGeneration)) state.DiscoveryGeneration

  /// Everything one session contributes to a `Cohort.SessionSnapshot` except
  /// `SessionId` and `Member` — attribution and identity are the caller's
  /// concern (DaemonMode's `getSessionSnapshots` closure), this function only
  /// ever reads the (already session-filtered) test view.
  let projectSession (sessionId: string) (state: LiveTestState) : SessionTestProjection * int64 =
    let entries = LiveTestState.statusEntriesForSession sessionId state
    SessionTestProjection.ofStatusEntries entries, generationOf state
