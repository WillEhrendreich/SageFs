/// The test file of the source-state outcome gate (SageFs.Tests/SourceStateOutcomeTests.fs). The gate edits it on disk
/// without rebuilding, which is the case a `run_tests` receipt must not call a plain pass.
module SageFs.Tests.Fixtures.SourceState.DomainTests

open Expecto
open Expecto.Flip

let tests = testList "source state fixture" [
  test "add computes the sum" {
    Domain.add 2 3 |> Expect.equal "2 + 3 = 5" 5
  }
  test "add zero is identity" {
    Domain.add 7 0 |> Expect.equal "7 + 0 = 7" 7
  }
  test "twice doubles" {
    Extra.twice 4 |> Expect.equal "4 * 2 = 8" 8
  }
]
