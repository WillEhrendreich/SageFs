/// Two passing tests and one pending test, for SageFs.Tests/ExpectoPendingOutcomeTests.fs.
/// Expecto's own runner reports this as 2 passed, 1 ignored. The pending test's body
/// would pass if it ran, so a SageFs run that reports 3 passed has executed a test that
/// Expecto skips.
module SageFs.Tests.Fixtures.ExpectoPending.Sample

open Expecto
open Expecto.Flip

let add a b = a + b

let tests = testList "pending fixture" [
  test "add computes the sum" {
    add 2 3 |> Expect.equal "2 + 3 = 5" 5
  }
  test "add zero is identity" {
    add 7 0 |> Expect.equal "7 + 0 = 7" 7
  }
  ptest "pending is not a pass" {
    add 1 1 |> Expect.equal "1 + 1 = 2" 2
  }
]
