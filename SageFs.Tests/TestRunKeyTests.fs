module SageFs.Tests.TestRunKeyTests

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

let private passed = mkResult (TestId.create "T" (TestFramework.Unknown "x")) (TestResult.Passed(TimeSpan.FromSeconds 1.0))

[<Tests>]
let inputHashTests =
  testList "InputHash" [

    testProperty "identical content sequences hash identically" <| fun (contents: NonEmptyArray<string>) ->
      let xs = contents.Get |> Array.toList
      InputHash.compute xs = InputHash.compute xs

    testProperty "ofContent agrees with compute on a single element" <| fun (s: string) ->
      InputHash.ofContent s = InputHash.compute [ s ]

    testProperty "different content is (almost certainly) a different hash" <|
      fun (NonEmptyString a) (NonEmptyString b) ->
        a <> b ==> lazy (InputHash.compute [ a ] <> InputHash.compute [ b ])

    testProperty "splitting content differently changes the hash (length-prefixed, not naive concat)" <|
      fun (NonEmptyString a) (NonEmptyString b) ->
        // "ab","c" must not collide with "a","bc" just because the raw bytes
        // concatenate the same way.
        InputHash.compute [ a; b ] <> InputHash.compute [ a + b ]

    test "hash is 16 hex chars, matching the TestId convention" {
      let hash = InputHash.compute [ "x"; "y" ]
      hash.Length |> Expect.equal "16 hex chars = 64 bits" 16
      hash |> Seq.forall Uri.IsHexDigit |> Expect.isTrue "all hex digits"
    }
  ]

[<Tests>]
let testRunKeyTests =
  testList "TestRunKey" [

    testProperty "identical inputs produce an identical key" <|
      fun (NonEmptyString name) (NonEmptyString sessionId) (NonEmptyString inputHash) ->
        let testId = TestId.create name (TestFramework.Unknown "x")
        TestRunKey.create testId sessionId inputHash = TestRunKey.create testId sessionId inputHash

    testProperty "a different SessionId is a different key (no cross-checkout stealing)" <|
      fun (NonEmptyString name) (NonEmptyString sessionA) (NonEmptyString sessionB) (NonEmptyString inputHash) ->
        sessionA <> sessionB ==> lazy (
          let testId = TestId.create name (TestFramework.Unknown "x")
          TestRunKey.create testId sessionA inputHash <> TestRunKey.create testId sessionB inputHash)

    testProperty "a different InputHash is a different key" <|
      fun (NonEmptyString name) (NonEmptyString sessionId) (NonEmptyString hashA) (NonEmptyString hashB) ->
        hashA <> hashB ==> lazy (
          let testId = TestId.create name (TestFramework.Unknown "x")
          TestRunKey.create testId sessionId hashA <> TestRunKey.create testId sessionId hashB)
  ]

[<Tests>]
let cacheTests =
  testList "TestResultCache" [

    test "empty cache misses on any key" {
      let key = TestRunKey.create (TestId.create "T" (TestFramework.Unknown "x")) "session-a" "hash-1"
      TestResultCache.empty |> TestResultCache.lookup key |> Expect.isNone "no results yet"
    }

    testProperty "insert then lookup with the same key is a hit (round-trip)" <|
      fun (NonEmptyString name) (NonEmptyString sessionId) (NonEmptyString inputHash) ->
        let key = TestRunKey.create (TestId.create name (TestFramework.Unknown "x")) sessionId inputHash
        TestResultCache.empty
        |> TestResultCache.insert key passed
        |> TestResultCache.lookup key
        = Some passed

    test "a changed InputHash misses even though the TestId and SessionId are unchanged" {
      let testId = TestId.create "T" (TestFramework.Unknown "x")
      let originalKey = TestRunKey.create testId "session-a" "hash-1"
      let changedKey = TestRunKey.create testId "session-a" "hash-2"

      TestResultCache.empty
      |> TestResultCache.insert originalKey passed
      |> TestResultCache.lookup changedKey
      |> Expect.isNone "different inputs must not hit the old result"
    }

    test "two sessions on identical inputs can share one result without re-running" {
      // §5.3: "the same test on the same bytes is one fact, not two runs."
      // A member whose InputHash for a test matches another session's already
      // finds the same TestRunResult — no special "shared" API, the caller
      // just inserts the value it already looked up under the new key.
      let testId = TestId.create "T" (TestFramework.Unknown "x")
      let integrationKey = TestRunKey.create testId "integration-session" "shared-hash"
      let memberKey = TestRunKey.create testId "member-session" "shared-hash"

      let cache = TestResultCache.empty |> TestResultCache.insert integrationKey passed

      let shared =
        match TestResultCache.lookup integrationKey cache with
        | Some result -> TestResultCache.insert memberKey result cache
        | None -> cache

      shared |> TestResultCache.lookup memberKey |> Expect.equal "member inherits the result" (Some passed)
      shared |> TestResultCache.lookup integrationKey |> Expect.equal "integration result untouched" (Some passed)
    }

    test "insert is idempotent by key: the latest insert for a key wins" {
      let key = TestRunKey.create (TestId.create "T" (TestFramework.Unknown "x")) "session-a" "hash-1"
      let failed = mkResult key.TestId (TestResult.Failed(TestFailure.AssertionFailed "boom", TimeSpan.Zero))

      TestResultCache.empty
      |> TestResultCache.insert key passed
      |> TestResultCache.insert key failed
      |> TestResultCache.lookup key
      |> Expect.equal "second insert replaces the first" (Some failed)
    }

    test "count reflects distinct keys, not distinct inserts" {
      let key = TestRunKey.create (TestId.create "T" (TestFramework.Unknown "x")) "session-a" "hash-1"

      TestResultCache.empty
      |> TestResultCache.insert key passed
      |> TestResultCache.insert key passed
      |> TestResultCache.count
      |> Expect.equal "same key inserted twice is still one entry" 1
    }
  ]
