/// Item 13a of sagefs-multiagent-vision.md: pure unit tests for
/// `CohortTestProjection` — no daemon, no FSI, no I/O. These pin the
/// Pass/Fail/Stale classification and the generation formula that
/// `DaemonMode`'s (deferred, see its own comment) `getSessionSnapshots`
/// closure would build `Cohort.SessionSnapshot`s from.
module SageFs.Tests.CohortTestProjectionTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features
open SageFs.Features.LiveTesting

let private mkEntry (tid: string) (status: TestRunStatus) : TestStatusEntry =
  { TestId = TestId.TestId tid
    DisplayName = tid
    FullName = tid
    Origin = TestOrigin.ReflectionOnly
    Framework = TestFramework.Expecto
    Category = TestCategory.Unit
    CurrentPolicy = RunPolicy.OnEveryChange
    Status = status
    PreviousStatus = TestRunStatus.Detected }

[<Tests>]
let cohortTestProjectionTests =
  testList "CohortTestProjection" [

    testList "toCohortTestId" [
      test "carries the same string identity across the LiveTesting/Cohort boundary" {
        let liveId = TestId.TestId "abc123"
        CohortTestProjection.toCohortTestId liveId
        |> Expect.equal "the underlying string round-trips unchanged" (Cohort.TestId "abc123")
      }
    ]

    testList "SessionTestProjection.ofStatusEntries" [
      test "Passed entries land in PassingTests" {
        let entries = [| mkEntry "t1" (TestRunStatus.Passed TimeSpan.Zero) |]
        let result = CohortTestProjection.SessionTestProjection.ofStatusEntries entries
        result.PassingTests |> Expect.equal "t1 passed" [ Cohort.TestId "t1" ]
        result.FailingTests |> Expect.equal "nothing failed" []
        result.StaleTests |> Expect.equal "nothing is stale" []
      }

      test "Failed entries land in FailingTests" {
        let entries = [| mkEntry "t1" (TestRunStatus.Failed(TestFailure.AssertionFailed "boom", TimeSpan.Zero)) |]
        let result = CohortTestProjection.SessionTestProjection.ofStatusEntries entries
        result.FailingTests |> Expect.equal "t1 failed" [ Cohort.TestId "t1" ]
        result.PassingTests |> Expect.equal "nothing passed" []
        result.StaleTests |> Expect.equal "nothing is stale" []
      }

      test "Stale entries land in StaleTests" {
        let entries = [| mkEntry "t1" TestRunStatus.Stale |]
        let result = CohortTestProjection.SessionTestProjection.ofStatusEntries entries
        result.StaleTests |> Expect.equal "t1 is stale" [ Cohort.TestId "t1" ]
        result.PassingTests |> Expect.equal "nothing passed" []
        result.FailingTests |> Expect.equal "nothing failed" []
      }

      test "Detected/Queued/Running/Skipped/PolicyDisabled contribute to none of the three bitplanes" {
        let entries =
          [| mkEntry "detected" TestRunStatus.Detected
             mkEntry "queued" TestRunStatus.Queued
             mkEntry "running" TestRunStatus.Running
             mkEntry "skipped" (TestRunStatus.Skipped "not applicable")
             mkEntry "disabled" TestRunStatus.PolicyDisabled |]
        let result = CohortTestProjection.SessionTestProjection.ofStatusEntries entries
        result
        |> Expect.equal
          "none of these settled states are pass, fail, or stale"
          CohortTestProjection.SessionTestProjection.empty
      }

      test "a mixed batch sorts each entry into exactly one bucket" {
        let entries =
          [| mkEntry "pass1" (TestRunStatus.Passed TimeSpan.Zero)
             mkEntry "pass2" (TestRunStatus.Passed TimeSpan.Zero)
             mkEntry "fail1" (TestRunStatus.Failed(TestFailure.AssertionFailed "x", TimeSpan.Zero))
             mkEntry "stale1" TestRunStatus.Stale
             mkEntry "running1" TestRunStatus.Running |]
        let result = CohortTestProjection.SessionTestProjection.ofStatusEntries entries
        result.PassingTests |> List.sortBy (fun (Cohort.TestId t) -> t)
        |> Expect.equal "both passing tests, sorted" [ Cohort.TestId "pass1"; Cohort.TestId "pass2" ]
        result.FailingTests |> Expect.equal "the one failing test" [ Cohort.TestId "fail1" ]
        result.StaleTests |> Expect.equal "the one stale test" [ Cohort.TestId "stale1" ]
      }

      test "an empty entry array projects to the empty projection" {
        CohortTestProjection.SessionTestProjection.ofStatusEntries [||]
        |> Expect.equal "empty in, empty out" CohortTestProjection.SessionTestProjection.empty
      }
    ]

    testList "generationOf" [
      test "is the run generation when discovery has never advanced" {
        let state = { LiveTestState.empty with LastGeneration = RunGeneration.next (RunGeneration.next RunGeneration.zero) }
        CohortTestProjection.generationOf state
        |> Expect.equal "two RunGeneration.next calls from zero is 2" 2L
      }

      test "is the discovery generation when it has advanced past the run generation" {
        let state = { LiveTestState.empty with DiscoveryGeneration = 5L }
        CohortTestProjection.generationOf state
        |> Expect.equal "discovery generation wins when the run generation is still zero" 5L
      }

      test "is monotonic: never regresses below either counter's current value" {
        let state =
          { LiveTestState.empty with
              LastGeneration = RunGeneration.next (RunGeneration.next (RunGeneration.next RunGeneration.zero))
              DiscoveryGeneration = 2L }
        CohortTestProjection.generationOf state
        |> Expect.equal "the run generation (3) outranks the smaller discovery generation (2)" 3L
      }
    ]

    testList "projectSession" [
      test "filters to the given session before classifying, via statusEntriesForSession" {
        let entryA = mkEntry "testA" (TestRunStatus.Passed TimeSpan.Zero)
        let entryB = mkEntry "testB" (TestRunStatus.Failed(TestFailure.AssertionFailed "x", TimeSpan.Zero))
        let state =
          { LiveTestState.empty with
              StatusIndex = TestStatusIndex.fromEntries [| entryA; entryB |]
              TestSessionMap = Map.ofList [ TestId.TestId "testA", "session-A"; TestId.TestId "testB", "session-B" ]
              DiscoveryGeneration = 4L }
        let projection, generation = CohortTestProjection.projectSession "session-A" state
        projection.PassingTests |> Expect.equal "session-A sees only its own passing test" [ Cohort.TestId "testA" ]
        projection.FailingTests |> Expect.equal "session-B's failing test does not leak into session-A" []
        generation |> Expect.equal "the generation is the session-independent state generation" 4L
      }
    ]
  ]
