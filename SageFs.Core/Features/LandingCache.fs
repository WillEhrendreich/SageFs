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

  /// Cache-aware verification (§5.4: "verification is a cache lookup"). The
  /// orchestration a landing verifier runs on the tests it must check:
  ///
  ///   * `inputHashOf` returns `Some hash` ONLY for a test whose input
  ///     signature is TRUSTWORTHY (it has real coverage). `None` means "no
  ///     trustworthy signature" — such a test is ALWAYS run and NEVER cached,
  ///     so a source change can never be masked by a stale skip. This is the
  ///     correctness guard: an empty/absent coverage bitmap hashes the same
  ///     regardless of the code, so trusting it would let a changed test be
  ///     wrongly skipped.
  ///   * Tests with a trustworthy hash that already have a cache entry are
  ///     skipped; the rest (misses + untrusted) are handed to `runMisses`.
  ///   * `runMisses` returns the FAILING subset of what it ran, or `Error` when
  ///     the run itself could not be trusted (e.g. an untrustworthy session).
  ///     On `Error`, NOTHING is cached and the error propagates — a landing must
  ///     never cache or pass on an unverified run.
  ///   * On success, only the trustworthy-hashed tests that actually ran are
  ///     recorded (never the untrusted ones), and the failing set returned is
  ///     the cached failures plus the freshly-run failures.
  ///
  /// Returns the updated cache and the combined verdict. Pure except for the
  /// injected `runMisses`; the synthesized cache entries carry a zero duration
  /// (the cache's only consumer is pass/non-pass via `failingOfCached`).
  let verify
    (cache: TestResultCache)
    (inputHashOf: TestId -> string option)
    (sessionId: string)
    (tests: TestId list)
    (runMisses: TestId list -> Async<Result<TestId list, string>>)
    : Async<TestResultCache * Result<TestId list, string>> =
    async {
      let hashed, untrusted = tests |> List.partition (fun t -> (inputHashOf t).IsSome)
      let hashOf t =
        match inputHashOf t with
        | Some h -> h
        | None -> failwith "verify: hashOf called on an untrusted test"  // unreachable: `hashed` only
      let part = partition cache hashOf sessionId hashed
      // Misses (trustworthy but not cached) AND every untrusted test both run.
      let toRun = part.MustRun @ untrusted
      let! runResult = runMisses toRun
      match runResult with
      | Error e -> return (cache, Error e)
      | Ok runFailing ->
        let failingSet = Set.ofList runFailing
        let now = System.DateTimeOffset.UtcNow
        let synth (testId: TestId) : TestRunResult =
          let result =
            match failingSet.Contains testId with
            | true -> TestResult.Failed(TestFailure.AssertionFailed "failed in a prior cached run", System.TimeSpan.Zero)
            | false -> TestResult.Passed System.TimeSpan.Zero
          { TestId = testId
            TestName = TestId.value testId
            Result = result
            Timestamp = now
            Output = None }
        // Record ONLY the trustworthy-hashed tests we actually ran — never the
        // untrusted ones (they have no stable key to cache under).
        let recordPairs = part.MustRun |> List.map (fun t -> (t, synth t))
        let cache' = record cache hashOf sessionId recordPairs
        let combinedFailing = failingOfCached part.Cached @ runFailing
        return (cache', Ok combinedFailing)
    }
