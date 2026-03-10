module SageFs.Tests.MarkAllStaleTests

open System
open Expecto
open Expecto.Flip
open SageFs
open SageFs.Features.LiveTesting
open SageFs.Tests.LiveTestingTestHelpers

[<Tests>]
let markAllStaleTests = testList "MarkAllStale" [

  testList "Elm update" [

    test "MarkAllTestsStale sets all tests-with-results to Stale status" {
      let tc1 = mkTestCase "test1" TestFramework.Expecto TestCategory.Unit
      let tc2 = mkTestCase "test2" TestFramework.Expecto TestCategory.Unit
      let r1 = mkResult tc1.Id (LTTestResult.Passed (ts 10.0))
      let r2 = mkResult tc2.Id (LTTestResult.Passed (ts 20.0))
      // Start from a model with discovered tests and known results
      let model0 = SageFsModel.initial()
      let model1, _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s", [| tc1; tc2 |])))
          model0
      let model2, _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestResultsBatch [| r1; r2 |]))
          model1
      // Act
      let model3, effects =
        SageFsUpdate.update SageFsMsg.MarkAllTestsStale model2
      // Assert
      effects
      |> Expect.equal "no side effects" []
      let statuses =
        model3.LiveTesting.TestState.StatusEntries
        |> Array.map (fun e -> e.Status)
      statuses
      |> Array.forall (fun s -> s = TestRunStatus.Stale)
      |> Expect.isTrue "all tests with results should be Stale"
    }

    test "MarkAllTestsStale with no discovered tests is a no-op" {
      let model0 = SageFsModel.initial()
      let model1, effects =
        SageFsUpdate.update SageFsMsg.MarkAllTestsStale model0
      effects
      |> Expect.equal "no side effects" []
      model1.LiveTesting.TestState.StatusEntries.Length
      |> Expect.equal "still no status entries" 0
    }

    test "MarkAllTestsStale adds all test IDs to AffectedTests" {
      let tc1 = mkTestCase "a" TestFramework.Expecto TestCategory.Unit
      let tc2 = mkTestCase "b" TestFramework.Expecto TestCategory.Integration
      let model0 = SageFsModel.initial()
      let model1, _ =
        SageFsUpdate.update
          (SageFsMsg.Event (SageFsEvent.TestsDiscovered ("s", [| tc1; tc2 |])))
          model0
      let model2, _ =
        SageFsUpdate.update SageFsMsg.MarkAllTestsStale model1
      Set.contains tc1.Id model2.LiveTesting.TestState.AffectedTests
      |> Expect.isTrue "tc1 should be in AffectedTests"
      Set.contains tc2.Id model2.LiveTesting.TestState.AffectedTests
      |> Expect.isTrue "tc2 should be in AffectedTests"
    }

  ]

  testList "SSE payload" [

    test "TestResultsBatchPayload with all-stale entries has correct stale count in summary" {
      let tc = mkTestCase "t1" TestFramework.Expecto TestCategory.Unit
      let staleEntry : TestStatusEntry = {
        TestId = tc.Id
        DisplayName = tc.DisplayName
        FullName = tc.FullName
        Origin = tc.Origin
        Framework = tc.Framework
        Category = tc.Category
        CurrentPolicy = RunPolicy.OnEveryChange
        Status = TestRunStatus.Stale
        PreviousStatus = TestRunStatus.Passed (ts 5.0)
      }
      let payload =
        TestResultsBatchPayload.create
          RunGeneration.zero
          ResultFreshness.Fresh
          (BatchCompletion.Complete (1, 1))
          LiveTestingActivation.Active
          [| staleEntry |]
      payload.Summary.Stale
      |> Expect.equal "should have 1 stale" 1
      payload.Summary.Passed
      |> Expect.equal "should have 0 passed" 0
      payload.Entries.Length
      |> Expect.equal "should have 1 entry" 1
    }

    test "SageFsMsg DU has MarkAllTestsStale case" {
      let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<SageFsMsg>
        |> Array.map (fun uc -> uc.Name)
      cases
      |> Array.contains "MarkAllTestsStale"
      |> Expect.isTrue "SageFsMsg should have MarkAllTestsStale case"
    }

  ]

]
