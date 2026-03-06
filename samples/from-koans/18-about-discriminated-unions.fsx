// ============================================================
//  🧘  About Discriminated Unions — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutDiscriminatedUnions.fs
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
    Expect.equal (toColor Mustard) __ "Mustard is yellow"
  }

  test "DUsCaptureASetOfOptions — Ketchup" {
    Expect.equal (toColor Ketchup) __ "Ketchup is red"
  }

  test "DUsCaptureASetOfOptions — Relish" {
    Expect.equal (toColor Relish) __ "Relish is green"
  }

  test "DUCasesCanHaveTypes — Bourbon" {
    let result = saySomething (Bourbon "Maker's Mark")
    Expect.equal result __ "prefers Bookers"
  }

  test "DUCasesCanHaveTypes — Number 7" {
    let result = saySomething (Number 7)
    Expect.equal result __ "me too for 7"
  }

  test "DUCasesCanHaveTypes — Bookers" {
    let result = saySomething (Bourbon "Bookers")
    Expect.equal result __ "same favorite bourbon"
  }

  test "DUCasesCanHaveTypes — other number" {
    let result = saySomething (Number 42)
    Expect.equal result __ "partial to 7"
  }

  test "ShapeDU — Circle area" {
    Expect.floatClose Accuracy.medium (area (Circle 3.0)) (System.Math.PI * 9.0) "π*r²"
  }

  test "ShapeDU — Rectangle area" {
    Expect.equal (area (Rectangle (4.0, 5.0))) __ "width * height"
  }

  test "ShapeDU — Triangle area" {
    Expect.equal (area (Triangle (6.0, 4.0))) __ "0.5 * base * height"
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
