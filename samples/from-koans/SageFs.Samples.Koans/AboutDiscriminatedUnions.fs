module SageFs.Samples.Koans.AboutDiscriminatedUnions

open Expecto
open Expecto.Flip

type Condiment = Mustard | Ketchup | Relish | Vinegar

let toColor condiment =
  match condiment with
  | Mustard -> "yellow"
  | Ketchup -> "red"
  | Relish -> "green"
  | Vinegar -> "brownish?"

type Favorite =
  | Bourbon of string
  | Number of int

let saySomething fav =
  match fav with
  | Number 7 -> "me too!"
  | Bourbon "Bookers" -> "me too!"
  | Bourbon b -> "I prefer Bookers to " + b
  | Number _ -> "I'm partial to 7"

type Shape =
  | Circle of radius: float
  | Rectangle of width: float * height: float
  | Triangle of base': float * height: float

let area shape =
  match shape with
  | Circle r -> System.Math.PI * r * r
  | Rectangle (w, h) -> w * h
  | Triangle (b, h) -> 0.5 * b * h

let tests = testList "about discriminated unions" [

  test "DUsCaptureASetOfOptions — Mustard" {
    (toColor Mustard) |> Expect.equal "Mustard is yellow" "yellow"
  }

  test "DUsCaptureASetOfOptions — Ketchup" {
    (toColor Ketchup) |> Expect.equal "Ketchup is red" "red"
  }

  test "DUsCaptureASetOfOptions — Relish" {
    (toColor Relish) |> Expect.equal "Relish is green" "green"
  }

  test "DUCasesCanHaveTypes — Bourbon" {
    let result = saySomething (Bourbon "Maker's Mark")
    result |> Expect.equal "prefers Bookers" "I prefer Bookers to Maker's Mark"
  }

  test "DUCasesCanHaveTypes — Number 7" {
    let result = saySomething (Number 7)
    result |> Expect.equal "me too for 7" "me too!"
  }

  test "DUCasesCanHaveTypes — Bookers" {
    let result = saySomething (Bourbon "Bookers")
    result |> Expect.equal "same favorite bourbon" "me too!"
  }

  test "DUCasesCanHaveTypes — other number" {
    let result = saySomething (Number 42)
    result |> Expect.equal "partial to 7" "I'm partial to 7"
  }

  test "ShapeDU — Circle area" {
    (area (Circle 3.0)) |> Expect.floatClose "π*r²" Accuracy.medium (System.Math.PI * 9.0)
  }

  test "ShapeDU — Rectangle area" {
    (area (Rectangle (4.0, 5.0))) |> Expect.equal "width * height" 20.0
  }

  test "ShapeDU — Triangle area" {
    (area (Triangle (6.0, 4.0))) |> Expect.equal "0.5 * base * height" 12.0
  }

]
