module SageFs.Samples.Koans.AboutOrderOfEvaluation

open Expecto
open Expecto.Flip

let add x y = x + y
let double' x = x * 2

let tests = testList "about the order of evaluation" [

  test "SometimesYouNeedParenthesisToGroupThings" {
    let result = add (add 5 8) (add 1 1)
    result |> Expect.equal "nested adds: (5+8) + (1+1)" 15
  }

  test "BackwardPipeOperatorHelpsWithGrouping" {
    let result = double' <| add 5 8
    result |> Expect.equal "double the result of add 5 8" 26
  }

  test "ParensAndBackwardPipeAreEquivalent" {
    let withParens = double' (add 3 4)
    let withBwdPipe = double' <| add 3 4
    withParens |> Expect.equal "both should give same result" withBwdPipe
    withParens |> Expect.equal "double of (3+4)" 14
  }

]
