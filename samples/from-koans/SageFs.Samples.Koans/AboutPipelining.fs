module SageFs.Samples.Koans.AboutPipelining

open Expecto
open Expecto.Flip

let square x = x * x
let isEven x = x % 2 = 0

let numbers = [0..5]
let evens = List.filter isEven numbers
let result1 = List.map square evens

let result2 = List.map square (List.filter isEven [0..5])

let result3 =
  [0..5]
  |> List.filter isEven
  |> List.map square

let tests = testList "about pipelining" [

  test "SquareEvenNumbers — separate statements" {
    result1 |> Expect.equal "squares of evens in 0..5" [0; 4; 16]
  }

  test "SquareEvenNumbers — nested parens" {
    result2 |> Expect.equal "same with parens" [0; 4; 16]
  }

  test "SquareEvenNumbers — pipeline" {
    result3 |> Expect.equal "same with |>" [0; 4; 16]
  }

  test "AllThreeAreEquivalent" {
    result1 |> Expect.equal "separate == nested parens" result2
    result2 |> Expect.equal "nested parens == pipeline" result3
  }

  test "HowThePipeOperatorIsDefined" {
    let (|>) x f = f x
    let result =
      [0..5]
      |> List.filter isEven
      |> List.map square
    result |> Expect.equal "same result even with redefined |>" [0; 4; 16]
  }

  test "PipelineWithAnonymousFunctions" {
    let result =
      [1..10]
      |> List.filter (fun x -> x % 2 = 0)
      |> List.map (fun x -> x * 3)
      |> List.sum
    result |> Expect.equal "sum of (even * 3) for 1..10" 90
  }

]
