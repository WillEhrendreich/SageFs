namespace SageFs.Features.LiveTesting

/// The pure partition/record algebra for "verification is a cache lookup"
/// (sagefs-multiagent-vision.md §5.4), built on `TestRunKey`/`TestResultCache`
/// (`TestRunKey.fs`). This module has no IO and no knowledge of coverage
/// bitmaps or disk — `inputHashOf` is injected so a caller (the daemon edge)
/// can compute each test's `InputHash` however it likes, e.g. via
/// `InputHashCoverage.ofCoverage`. Keeping that dependency out of this module
/// is deliberate: the partition/record decision is the same regardless of how
/// the hash was derived.
module LandingCache =

  /// The result of splitting a cohort's tests against a `TestResultCache`:
  /// `Cached` tests already have a result under their current `TestRunKey`
  /// (same `TestId`, `sessionId`, and `inputHashOf` output) and never need to
  /// run again; `MustRun` tests have no matching entry — either never run
  /// before, or their content changed since the cached entry was recorded
  /// (a different `InputHash` is, structurally, a cache MISS). Both lists
  /// preserve the order `tests` was given in.
  type CachePartition = {
    Cached: (TestId * TestRunResult) list
    MustRun: TestId list
  }

  /// Split `tests` into what the cache already answers for `sessionId` and
  /// what genuinely has to run. Pure: `inputHashOf` is the only source of
  /// "what does this test's content look like right now" — this function
  /// only builds keys and looks them up.
  let partition
    (cache: TestResultCache)
    (inputHashOf: TestId -> string)
    (sessionId: string)
    (tests: TestId list)
    : CachePartition =
    let cached, mustRun =
      tests
      |> List.fold
        (fun (cachedAcc, mustRunAcc) testId ->
          let key = TestRunKey.create testId sessionId (inputHashOf testId)

          match TestResultCache.lookup key cache with
          | Some result -> ((testId, result) :: cachedAcc, mustRunAcc)
          | None -> (cachedAcc, testId :: mustRunAcc))
        ([], [])

    { Cached = List.rev cached
      MustRun = List.rev mustRun }

  /// Which of the `Cached` pairs did NOT come back a confirmed `Passed`.
  /// Fail-closed, mirroring `CohortLandingVerify.failingOf`'s convention for
  /// live results: `Failed`, `Skipped`, `NotRun`, and `NoResult` all count as
  /// failing here — a cached "we don't know" must never read as green.
  let failingOfCached (cached: (TestId * TestRunResult) list) : TestId list =
    cached
    |> List.choose (fun (testId, result) ->
      match result.Result with
      | TestResult.Passed _ -> None
      | _ -> Some testId)

  /// Fold freshly-run `results` into `cache`, one entry per `(TestId,
  /// sessionId, inputHashOf TestId)` key — the same key shape `partition`
  /// looks up, so a test recorded here is found by a later `partition` call
  /// as long as `inputHashOf` still returns the same hash for it.
  let record
    (cache: TestResultCache)
    (inputHashOf: TestId -> string)
    (sessionId: string)
    (results: (TestId * TestRunResult) list)
    : TestResultCache =
    results
    |> List.fold
      (fun acc (testId, result) ->
        let key = TestRunKey.create testId sessionId (inputHashOf testId)
        TestResultCache.insert key result acc)
      cache
