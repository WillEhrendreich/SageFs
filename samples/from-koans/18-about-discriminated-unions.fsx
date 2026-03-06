// ============================================================
//  🧘  About Discriminated Unions — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutDiscriminatedUnions.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  Discriminated unions (DUs) are F#'s superpower.
//  They model "one of these fixed choices" — with or without data.
//  Think: enums that can carry values. Algebraic data types.
//  The compiler guarantees you handle every case.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Simple DU — like an enum ──────────────────────────────────
type Condiment = Mustard | Ketchup | Relish | Vinegar

let toColor condiment =
  match condiment with
  | Mustard -> "yellow"
  | Ketchup -> "red"
  | Relish  -> "green"
  | Vinegar -> "brownish?"

toColor Mustard   // → "yellow"
toColor Ketchup   // → "red"

// ── DU cases can carry data ───────────────────────────────────
type Favorite =
  | Bourbon of string
  | Number  of int

let saySomething fav =
  match fav with
  | Number 7          -> "me too!"
  | Bourbon "Bookers" -> "me too!"
  | Bourbon b         -> "I prefer Bookers to " + b
  | Number _          -> "I'm partial to 7"

saySomething (Bourbon "Maker's Mark")  // → "I prefer Bookers to Maker's Mark"
saySomething (Number 7)                 // → "me too!"

// ── Real-world DU example ────────────────────────────────────
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float
  | Triangle  of base': float * height: float

let area shape =
  match shape with
  | Circle r           -> System.Math.PI * r * r
  | Rectangle (w, h)   -> w * h
  | Triangle (b, h)    -> 0.5 * b * h

area (Circle 3.0)           // → 28.274...
area (Rectangle (4.0, 5.0)) // → 20.0

// TRY IT: Add a Square case and see what happens to `area`.
//         The compiler will warn you about incomplete match!

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about discriminated unions" [

  test "DUsCaptureASetOfOptions — Mustard" {
    (toColor Mustard) |> Expect.equal "Mustard is yellow" __
  }

  test "DUsCaptureASetOfOptions — Ketchup" {
    (toColor Ketchup) |> Expect.equal "Ketchup is red" __
  }

  test "DUsCaptureASetOfOptions — Relish" {
    (toColor Relish) |> Expect.equal "Relish is green" __
  }

  test "DUCasesCanHaveTypes — Bourbon" {
    let result = saySomething (Bourbon "Maker's Mark")
    result |> Expect.equal "prefers Bookers" __
  }

  test "DUCasesCanHaveTypes — Number 7" {
    let result = saySomething (Number 7)
    result |> Expect.equal "me too for 7" __
  }

  test "DUCasesCanHaveTypes — Bookers" {
    let result = saySomething (Bourbon "Bookers")
    result |> Expect.equal "same favorite bourbon" __
  }

  test "DUCasesCanHaveTypes — other number" {
    let result = saySomething (Number 42)
    result |> Expect.equal "partial to 7" __
  }

  test "ShapeDU — Circle area" {
    (area (Circle 3.0)) |> Expect.floatClose "π*r²" Accuracy.medium (System.Math.PI * 9.0)
  }

  test "ShapeDU — Rectangle area" {
    (area (Rectangle (4.0, 5.0))) |> Expect.equal "width * height" __
  }

  test "ShapeDU — Triangle area" {
    (area (Triangle (6.0, 4.0))) |> Expect.equal "0.5 * base * height" __
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `toColor Mustard` — see "yellow"
// 2. Try removing a case from `area` — see the compiler warning
// 3. Add a Square case: `Square of side: float`
//    → update `area` → compiler guides you to completeness
// 4. Model a traffic light: type TrafficLight = Red | Amber | Green
//    Write a function nextLight : TrafficLight -> TrafficLight
// ============================================================
