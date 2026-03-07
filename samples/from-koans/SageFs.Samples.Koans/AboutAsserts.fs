module SageFs.Samples.Koans.AboutAsserts

open Expecto
open Expecto.Flip

let tests = testList "about asserts" [

  test "AssertExpectation" {
    let expectedValue = 1 + 1
    let actualValue = 2
    actualValue |> Expect.equal "values should be equal" expectedValue
  }

  test "FillInValues" {
    (1 + 1) |> Expect.equal "1 + 1 should equal 2" 2
  }

  test "StringEquality" {
    "hello" |> Expect.equal "strings can be equal too" "hello"
  }

  test "BooleanEquality" {
    true |> Expect.equal "true is true" true
  }

]
