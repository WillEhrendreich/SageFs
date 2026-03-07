module SageFs.Samples.Koans.AboutTuples

open Expecto
open Expecto.Flip

let squareAndCube x = (x ** 2.0, x ** 3.0)

let tests = testList "about tuples" [

  test "CreatingTuples" {
    let t = ("apple", "dog")
    t |> Expect.equal "second element should be dog" ("apple", "dog")
  }

  test "AccessingTupleElements — fst" {
    let t = ("apple", "dog")
    (fst t) |> Expect.equal "fst should give the first element" "apple"
  }

  test "AccessingTupleElements — snd" {
    let t = ("apple", "dog")
    (snd t) |> Expect.equal "snd should give the second element" "dog"
  }

  test "AccessingWithPatternMatching — fruit" {
    let (f, _, _) = ("apple", "dog", "Mustang")
    f |> Expect.equal "first element is the fruit" "apple"
  }

  test "AccessingWithPatternMatching — animal" {
    let (_, a, _) = ("apple", "dog", "Mustang")
    a |> Expect.equal "second element is the animal" "dog"
  }

  test "AccessingWithPatternMatching — car" {
    let (_, _, c) = ("apple", "dog", "Mustang")
    c |> Expect.equal "third element is the car" "Mustang"
  }

  test "IgnoringValuesWithUnderscore" {
    let (_, animal, _) = ("apple", "dog", "Mustang")
    animal |> Expect.equal "only the animal matters here" "dog"
  }

  test "ReturningMultipleValuesFromAFunction — squared" {
    let (squared, _) = squareAndCube 3.0
    squared |> Expect.equal "3 squared is 9" 9.0
  }

  test "ReturningMultipleValuesFromAFunction — cubed" {
    let (_, cubed) = squareAndCube 3.0
    cubed |> Expect.equal "3 cubed is 27" 27.0
  }

  test "TheTruthBehindMultipleReturnValues" {
    let result = squareAndCube 3.0
    result |> Expect.equal "should be the tuple (9.0, 27.0)" (9.0, 27.0)
  }

]
