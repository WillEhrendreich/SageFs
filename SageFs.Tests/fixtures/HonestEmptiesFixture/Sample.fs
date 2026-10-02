/// Fixture for SageFs.Tests/HonestEmptiesOutcomeTests.fs: one test that passes and one that fails
/// on purpose.
module SageFs.Tests.Fixtures.HonestEmpties.Sample

open Expecto
open Expecto.Flip

let add a b = a + b

let tests = testList "honest empties fixture" [
  test "add computes the sum" {
    add 2 3 |> Expect.equal "2 + 3 = 5" 5
  }
  test "add is broken on purpose" {
    add 2 3 |> Expect.equal "this is the failure the gate looks for" 6
  }
]
