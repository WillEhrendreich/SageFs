module SageFs.Samples.FromPython.Hello

open Expecto
open Expecto.Flip

// ============================================================
//  🐍 → 🦅  Coming from Python? Welcome to F#!
// ============================================================

// ── Variables are immutable by default ──
let x = 42

// ── Functions are first-class ──
let double x = x * 2

let result = double 21

// ── Pipelines: |> is your new best friend ──
let answer =
  [1..10]
  |> List.map (fun x -> x * 2)
  |> List.filter (fun x -> x % 2 = 0)
  |> List.sum

// ── Pattern matching ──
let describe n =
  match n with
  | 0            -> "zero"
  | n when n < 0 -> "negative"
  | 1 | 2 | 3   -> "small"
  | _            -> "big"

// ── Discriminated Unions ──
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float
  | Triangle  of base': float * height: float

let area shape =
  match shape with
  | Circle r          -> System.Math.PI * r * r
  | Rectangle (w, h)  -> w * h
  | Triangle (b, h)   -> 0.5 * b * h

// ── Records ──
type Person = { Name: string; Age: int }

let alice = { Name = "Alice"; Age = 30 }
let olderAlice = { alice with Age = 31 }

// ── Option<'T>: None doesn't crash your program ──
let safeDivide a b =
  if b = 0 then None
  else Some (a / b)

// ── Collections ──
let numbers = [1; 2; 3; 4; 5]
let filteredSquares =
  numbers |> List.filter (fun x -> x > 2) |> List.map (fun x -> x * x)

let tests = testList "from Python" [
  testList "immutable bindings" [
    test "x is 42" {
      x |> Expect.equal "x bound to 42" 42
    }
  ]

  testList "functions" [
    test "double 21 = 42" {
      result |> Expect.equal "double 21" 42
    }
    test "double 0 = 0" {
      double 0 |> Expect.equal "double zero" 0
    }
  ]

  testList "pipelines" [
    test "map, filter, sum" {
      // [1..10] map (*2) = [2;4;6;8;10;12;14;16;18;20] — all even — sum = 110
      answer |> Expect.equal "sum of doubled evens" 110
    }
  ]

  testList "pattern matching" [
    test "zero" {
      describe 0 |> Expect.equal "zero case" "zero"
    }
    test "negative" {
      describe -5 |> Expect.equal "negative case" "negative"
    }
    test "small" {
      describe 2 |> Expect.equal "small case" "small"
    }
    test "big" {
      describe 100 |> Expect.equal "big case" "big"
    }
  ]

  testList "discriminated unions" [
    test "circle area" {
      area (Circle 5.0)
      |> Expect.floatClose "π × 5²" Accuracy.medium (System.Math.PI * 25.0)
    }
    test "rectangle area" {
      area (Rectangle (4.0, 6.0))
      |> Expect.floatClose "4 × 6 = 24" Accuracy.medium 24.0
    }
  ]

  testList "records" [
    test "create record" {
      alice.Name |> Expect.equal "name" "Alice"
    }
    test "non-destructive update" {
      olderAlice.Age |> Expect.equal "age updated" 31
    }
  ]

  testList "option" [
    test "safe divide success" {
      safeDivide 10 2 |> Expect.equal "10 / 2" (Some 5)
    }
    test "safe divide by zero" {
      safeDivide 10 0 |> Expect.equal "div by zero" None
    }
  ]

  testList "collections" [
    test "filter and map" {
      filteredSquares |> Expect.equal "[3²; 4²; 5²]" [9; 16; 25]
    }
  ]
]
