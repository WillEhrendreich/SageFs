module SageFs.Tests.TestExecutionReportTests

open System
open Expecto
open Expecto.Flip
open FsCheck
open SageFs.Features.LiveTesting

let private resultId (name: string) = TestId.create name TestFramework.Expecto

let private result (name: string) outcome : TestRunResult =
  { TestId = resultId name
    TestName = name
    Result = outcome
    Timestamp = DateTimeOffset.UnixEpoch
    Output = None }

let private names =
  [ "alpha"; "beta"; "gamma"; "delta" ]

[<Tests>]
let tests =
  testList "TestExecutionReport" [
    test "complete requested set is Complete" {
      let requested = names |> List.map resultId
      let results = names |> List.map (fun name -> result name (TestResult.Passed TimeSpan.Zero)) |> List.toArray
      let report = TestExecutionReport.create requested TestExecutionTermination.StreamCompleted results
      report.Integrity |> Expect.equal "complete" TestExecutionIntegrity.Complete
      TestExecutionReport.isComplete report |> Expect.isTrue "complete"
    }

    test "missing result is explicit" {
      let requested = names |> List.map resultId
      let results =
        names
        |> List.filter (fun name -> name <> "gamma")
        |> List.map (fun name -> result name (TestResult.Passed TimeSpan.Zero))
        |> List.toArray
      let report = TestExecutionReport.create requested TestExecutionTermination.StreamCompleted results
      match report.Integrity with
      | TestExecutionIntegrity.Invalid [ TestExecutionIssue.MissingResults [ missing ] ] ->
        missing |> Expect.equal "missing gamma" (resultId "gamma")
      | other -> failtestf "expected one missing result, got %A" other
    }

    test "duplicate result is explicit" {
      let requested = names |> List.map resultId
      let passed = names |> List.map (fun name -> result name (TestResult.Passed TimeSpan.Zero)) |> List.toArray
      let report =
        TestExecutionReport.create requested TestExecutionTermination.StreamCompleted (Array.append passed [| result "alpha" (TestResult.Skipped "again") |])
      match report.Integrity with
      | TestExecutionIntegrity.Invalid [ TestExecutionIssue.DuplicateResults [ duplicate ] ] ->
        duplicate |> Expect.equal "duplicate alpha" (resultId "alpha")
      | other -> failtestf "expected one duplicate result, got %A" other
    }

    test "unrequested result is explicit" {
      let requested = names |> List.map resultId
      let results =
        (names @ [ "extra" ])
        |> List.map (fun name -> result name (TestResult.Passed TimeSpan.Zero))
        |> List.toArray
      let report = TestExecutionReport.create requested TestExecutionTermination.StreamCompleted results
      match report.Integrity with
      | TestExecutionIntegrity.Invalid [ TestExecutionIssue.UnrequestedResults [ extra ] ] ->
        extra |> Expect.equal "extra" (resultId "extra")
      | other -> failtestf "expected one unrequested result, got %A" other
    }

    test "termination preserves transport and timeout reasons" {
      let requested: TestId list = []
      let stalled =
        TestExecutionReport.create requested (TestExecutionTermination.StreamStalled (TimeSpan.FromSeconds 3.0)) [||]
      stalled.Termination
      |> Expect.equal "stalled" (TestExecutionTermination.StreamStalled (TimeSpan.FromSeconds 3.0))
      TestExecutionTermination.noResultReason stalled.Termination
      |> Expect.equal "stalled maps to structured no-result" (NoResultReason.StreamStalled (TimeSpan.FromSeconds 3.0))
      TestExecutionTermination.TransportFailed "socket closed" |> TestExecutionTermination.noResultReason
      |> Expect.equal "transport reason" (NoResultReason.TransportFailed "socket closed")
    }

    testPropertyWithConfig FsCheckConfig.defaultConfig "permuting results preserves integrity" <|
      fun (NonNegativeInt seed) ->
        let requested = names |> List.map resultId
        let items = names |> List.map (fun name -> result name (TestResult.Passed TimeSpan.Zero)) |> List.toArray
        let shift = seed % (items.Length + 1)
        let rotated =
          Array.append (items |> Array.skip shift) (items |> Array.take shift)
        let left = TestExecutionReport.create requested TestExecutionTermination.StreamCompleted items
        let right = TestExecutionReport.create requested TestExecutionTermination.StreamCompleted rotated
        right.Integrity |> Expect.equal "permutation keeps classification" left.Integrity
  ]
