module SageFs.Tests.LandingCacheTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs.Features.LiveTesting

let private mkResult (testId: TestId) (result: TestResult) : TestRunResult =
  { TestId = testId
    TestName = "some.test"
    Result = result
    Timestamp = DateTimeOffset.UnixEpoch
    Output = None }

let private mkTestId (name: string) : TestId = TestId.create name (TestFramework.Unknown "x")

let private passedResult (testId: TestId) : TestRunResult =
  mkResult testId (TestResult.Passed(TimeSpan.FromSeconds 1.0))

let private failedResult (testId: TestId) : TestRunResult =
  mkResult testId (TestResult.Failed(TestFailure.AssertionFailed "boom", TimeSpan.FromSeconds 1.0))

let private sessionId = "session-a"

/// Fixed content-addressed hash per TestId, as the daemon edge would supply
/// via InputHashCoverage.ofCoverage — here just a deterministic function of
/// the test's own identity so tests are easy to reason about.
let private hashOf (testId: TestId) : string = "h-" + TestId.value testId

[<Tests>]
let partitionTests =
  testList "LandingCache.partition" [

    test "a test with no cache entry lands in MustRun" {
      let testA = mkTestId "A"

      let result = LandingCache.partition TestResultCache.empty hashOf sessionId [ testA ]

      result.Cached |> Expect.isEmpty "nothing cached yet"
      result.MustRun |> Expect.equal "the uncached test must run" [ testA ]
    }

    test "a test with a matching cache entry (same TestId+session+inputHash) lands in Cached" {
      let testA = mkTestId "A"
      let key = TestRunKey.create testA sessionId (hashOf testA)
      let cache = TestResultCache.empty |> TestResultCache.insert key (passedResult testA)

      let result = LandingCache.partition cache hashOf sessionId [ testA ]

      result.Cached |> Expect.equal "cache hit pairs the test with its cached result" [ testA, passedResult testA ]
      result.MustRun |> Expect.isEmpty "nothing left to run"
    }

    test "a stored entry is a MISS once inputHashOf returns a different hash (content changed)" {
      let testA = mkTestId "A"
      let staleKey = TestRunKey.create testA sessionId "h-old"
      let cache = TestResultCache.empty |> TestResultCache.insert staleKey (passedResult testA)

      // Simulate the file's content having changed since the cached run:
      // the same test, same session, but a different current InputHash.
      let currentHashOf (_: TestId) = "h-new"

      let result = LandingCache.partition cache currentHashOf sessionId [ testA ]

      result.Cached |> Expect.isEmpty "the old hash's entry does not answer for the new content"
      result.MustRun |> Expect.equal "content change busts the cache" [ testA ]
    }

    test "a stored entry under a different sessionId is a MISS (no cross-session stealing)" {
      let testA = mkTestId "A"
      let key = TestRunKey.create testA "other-session" (hashOf testA)
      let cache = TestResultCache.empty |> TestResultCache.insert key (passedResult testA)

      let result = LandingCache.partition cache hashOf sessionId [ testA ]

      result.Cached |> Expect.isEmpty "a different session's cached result must not be reused"
      result.MustRun |> Expect.equal "must run under this session" [ testA ]
    }

    test "order is preserved within each bucket, mixed cached/must-run" {
      let testA, testB, testC = mkTestId "A", mkTestId "B", mkTestId "C"
      let keyB = TestRunKey.create testB sessionId (hashOf testB)

      let cache = TestResultCache.empty |> TestResultCache.insert keyB (passedResult testB)

      let result = LandingCache.partition cache hashOf sessionId [ testA; testB; testC ]

      result.Cached |> Expect.equal "only B is cached" [ testB, passedResult testB ]
      result.MustRun |> Expect.equal "A then C, input order preserved" [ testA; testC ]
    }

    testProperty "every input test lands in exactly one bucket" <|
      fun (names: NonEmptyArray<NonEmptyString>) ->
        let tests = names.Get |> Array.toList |> List.map (fun (NonEmptyString s) -> mkTestId s) |> List.distinct
        let result = LandingCache.partition TestResultCache.empty hashOf sessionId tests

        let cachedIds = result.Cached |> List.map fst |> Set.ofList
        let mustRunIds = result.MustRun |> Set.ofList

        Set.isEmpty (Set.intersect cachedIds mustRunIds)
        && (cachedIds |> Set.union mustRunIds) = Set.ofList tests
  ]

[<Tests>]
let failingOfCachedTests =
  testList "LandingCache.failingOfCached" [

    test "a passing cached result is not failing" {
      let testA = mkTestId "A"
      LandingCache.failingOfCached [ testA, passedResult testA ] |> Expect.isEmpty "Passed is not failing"
    }

    test "a failed cached result is failing" {
      let testA = mkTestId "A"
      LandingCache.failingOfCached [ testA, failedResult testA ]
      |> Expect.equal "Failed counts as failing" [ testA ]
    }

    test "Skipped, NotRun, and NoResult are all fail-closed as failing" {
      let testA, testB, testC = mkTestId "A", mkTestId "B", mkTestId "C"

      let skipped = mkResult testA (TestResult.Skipped "not applicable")
      let notRun = mkResult testB TestResult.NotRun
      let noResult = mkResult testC (TestResult.NoResult NoResultReason.StreamEnded)

      LandingCache.failingOfCached [ testA, skipped; testB, notRun; testC, noResult ]
      |> Expect.equal "a cached \"we don't know\" must never read as green" [ testA; testB; testC ]
    }

    test "mixed cached results return exactly the non-passing subset" {
      let testA, testB, testC = mkTestId "A", mkTestId "B", mkTestId "C"

      LandingCache.failingOfCached
        [ testA, passedResult testA
          testB, failedResult testB
          testC, passedResult testC ]
      |> Expect.equal "only B is failing" [ testB ]
    }
  ]

[<Tests>]
let recordTests =
  testList "LandingCache.record" [

    test "a recorded result is subsequently found by partition (round-trip)" {
      let testA = mkTestId "A"
      let cache = LandingCache.record TestResultCache.empty hashOf sessionId [ testA, passedResult testA ]

      let result = LandingCache.partition cache hashOf sessionId [ testA ]

      result.Cached |> Expect.equal "the freshly recorded result is now a hit" [ testA, passedResult testA ]
      result.MustRun |> Expect.isEmpty "nothing left to run"
    }

    test "count grows by the number of distinct (TestId, session, inputHash) keys recorded" {
      let testA, testB = mkTestId "A", mkTestId "B"

      let cache =
        LandingCache.record
          TestResultCache.empty
          hashOf
          sessionId
          [ testA, passedResult testA; testB, failedResult testB ]

      cache |> TestResultCache.count |> Expect.equal "two distinct tests, two entries" 2
    }

    test "recording the same test twice at the same hash is one entry, latest wins" {
      let testA = mkTestId "A"

      let cacheAfterFirst = LandingCache.record TestResultCache.empty hashOf sessionId [ testA, passedResult testA ]
      let cache = LandingCache.record cacheAfterFirst hashOf sessionId [ testA, failedResult testA ]

      cache |> TestResultCache.count |> Expect.equal "one key, re-inserted" 1

      let result = LandingCache.partition cache hashOf sessionId [ testA ]
      result.Cached |> Expect.equal "the later record wins" [ testA, failedResult testA ]
    }

    testProperty "record then partition is deterministic" <|
      fun (name: NonEmptyString) ->
        let (NonEmptyString s) = name
        let testA = mkTestId s
        let cache = LandingCache.record TestResultCache.empty hashOf sessionId [ testA, passedResult testA ]
        let first = LandingCache.partition cache hashOf sessionId [ testA ]
        let second = LandingCache.partition cache hashOf sessionId [ testA ]
        first = second
  ]

/// inputHashOf that trusts every test (the daemon edge supplies this only for
/// tests with real coverage).
let private trustAll (testId: TestId) : string option = Some (hashOf testId)

[<Tests>]
let verifyTests =
  testList "LandingCache.verify" [

    testTask "a trustworthy MISS runs, is recorded, and a later verify skips it" {
      let testA = mkTestId "A"
      let mutable ranWith : TestId list = []
      let runMisses ts = async { ranWith <- ts; return Ok [] }
      let! cache', result = LandingCache.verify TestResultCache.empty trustAll sessionId [ testA ] runMisses
      ranWith |> Expect.equal "the uncached test was run" [ testA ]
      result |> Expect.equal "no failures" (Ok [])
      // second verify against the now-warmed cache must NOT re-run it
      let mutable ranAgain : TestId list = []
      let runAgain ts = async { ranAgain <- ts; return Ok [] }
      let! _, _ = LandingCache.verify cache' trustAll sessionId [ testA ] runAgain
      ranAgain |> Expect.isEmpty "a cache HIT is skipped on the second verify"
    }

    testTask "an untrusted test (inputHashOf None) ALWAYS runs and is NEVER cached" {
      let testA = mkTestId "A"
      let untrusted (_: TestId) : string option = None
      let mutable ranWith : TestId list = []
      let runMisses ts = async { ranWith <- ts; return Ok [] }
      let! cache', _ = LandingCache.verify TestResultCache.empty untrusted sessionId [ testA ] runMisses
      ranWith |> Expect.equal "an untrusted test is run" [ testA ]
      // it must NOT have been cached — even asking with a real hash finds nothing
      let part = LandingCache.partition cache' hashOf sessionId [ testA ]
      part.MustRun |> Expect.equal "untrusted test was not recorded, so it stays MustRun" [ testA ]
      part.Cached |> Expect.isEmpty "no cache entry was created for the untrusted test"
    }

    testTask "a trustworthy cache HIT is not handed to runMisses" {
      let testA = mkTestId "A"
      let key = TestRunKey.create testA sessionId (hashOf testA)
      let cache = TestResultCache.empty |> TestResultCache.insert key (passedResult testA)
      let mutable ranWith : TestId list = []
      let runMisses ts = async { ranWith <- ts; return Ok [] }
      let! _, result = LandingCache.verify cache trustAll sessionId [ testA ] runMisses
      ranWith |> Expect.isEmpty "a cached-passed test is never re-run"
      result |> Expect.equal "a cached PASS contributes no failure" (Ok [])
    }

    testTask "an untrusted run (runMisses Error) caches nothing and propagates the error" {
      let testA = mkTestId "A"
      let runMisses (_: TestId list) = async { return Error "session not trustworthy" }
      let! cache', result = LandingCache.verify TestResultCache.empty trustAll sessionId [ testA ] runMisses
      result |> Expect.equal "the error propagates unchanged" (Error "session not trustworthy")
      TestResultCache.count cache' |> Expect.equal "nothing was cached on an untrusted run" 0
    }

    testTask "combined failing = cached failures ++ freshly-run failures" {
      let cachedFail = mkTestId "cached-fail"
      let freshFail = mkTestId "fresh-fail"
      let freshPass = mkTestId "fresh-pass"
      let key = TestRunKey.create cachedFail sessionId (hashOf cachedFail)
      let cache = TestResultCache.empty |> TestResultCache.insert key (failedResult cachedFail)
      let runMisses (_: TestId list) = async { return Ok [ freshFail ] }
      let! _, result = LandingCache.verify cache trustAll sessionId [ cachedFail; freshFail; freshPass ] runMisses
      match result with
      | Ok failing ->
        failing |> List.contains cachedFail |> Expect.isTrue "a cached FAIL stays failing"
        failing |> List.contains freshFail |> Expect.isTrue "a freshly-run FAIL is failing"
        failing |> List.contains freshPass |> Expect.isFalse "a freshly-run PASS is not failing"
      | Error e -> failwithf "expected Ok, got Error %s" e
    }
  ]
