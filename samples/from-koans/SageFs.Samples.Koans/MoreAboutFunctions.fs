module SageFs.Samples.Koans.MoreAboutFunctions

open Expecto
open Expecto.Flip

let colors = ["maize"; "blue"]

let add x =
  (fun y -> x + y)

let addTen = add 10

let add2 x y = x + y

let addSeven = add2 7

let addTuple (x, y) = x + y

let tests = testList "more about functions" [

  test "DefiningLambdas" {
    let echo = colors |> List.map (fun x -> x + " " + x)
    echo |> Expect.equal "each color echoed" ["maize maize"; "blue blue"]
  }

  test "FunctionsThatReturnFunctions — simple call" {
    let result = add 2 4
    result |> Expect.equal "add 2 4" 6
  }

  test "FunctionsThatReturnFunctions — partial application" {
    let addTen' = add 10
    let result = addTen' 14
    result |> Expect.equal "add ten to 14" 24
  }

  test "AutomaticCurrying — unlucky number" {
    let unlucky = addSeven 6
    unlucky |> Expect.equal "7 + 6" 13
  }

  test "AutomaticCurrying — lucky number" {
    let lucky = addSeven 0
    lucky |> Expect.equal "7 + 0" 7
  }

  test "NonCurriedTupleForm" {
    let result = addTuple (5, 40)
    result |> Expect.equal "5 + 40 with tuple args" 45
  }

  test "PartialApplicationInPipelines" {
    let double = (*) 2
    let doubles = [1..5] |> List.map double
    doubles |> Expect.equal "double each of 1..5" [2; 4; 6; 8; 10]
  }

]
