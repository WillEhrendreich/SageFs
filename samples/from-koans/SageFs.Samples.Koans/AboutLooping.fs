module SageFs.Samples.Koans.AboutLooping

open Expecto
open Expecto.Flip

let tests = testList "about looping" [

  test "LoopingOverAList — for..in" {
    let values = [0..10]
    let mutable sum = 0
    for value in values do
      sum <- sum + value
    sum |> Expect.equal "sum of 0..10" 55
  }

  test "LoopingWithExpressions — for..to" {
    let mutable sum = 0
    for i = 1 to 5 do
      sum <- sum + i
    sum |> Expect.equal "sum of 1..5" 15
  }

  test "LoopingWithWhile" {
    let mutable s = 1
    while s < 10 do
      s <- s + s
    s |> Expect.equal "doubling: 1→2→4→8→16, first ≥10" 16
  }

  test "FunctionalAlternative — List.sum equals for..in result" {
    let imperativeSum =
      let mutable acc = 0
      for i in [0..10] do acc <- acc + i
      acc
    let functionalSum = [0..10] |> List.sum
    imperativeSum |> Expect.equal "both give the same answer" functionalSum
    functionalSum |> Expect.equal "sum of 0..10" 55
  }

]
