/// Minimal Expecto fixture for SageFs.Tests/ConfirmKeepsReplStateTests.fs: a confirming build runs in a throwaway
/// worker, so what the session's REPL holds survives it.
module SageFs.Tests.Fixtures.ConfirmKeepsRepl.Sample

open Expecto
open Expecto.Flip

let add a b = a + b

let subtract a b = a - b

let tests = testList "confirm keeps repl fixture" [
  testList "arithmetic" [
    test "add computes the sum" {
      add 2 3 |> Expect.equal "2 + 3 = 5" 5
    }
    test "subtract computes the difference" {
      subtract 10 4 |> Expect.equal "10 - 4 = 6" 6
    }
  ]
  testList "identity" [
    test "add zero is identity" {
      add 7 0 |> Expect.equal "7 + 0 = 7" 7
    }
  ]
]
