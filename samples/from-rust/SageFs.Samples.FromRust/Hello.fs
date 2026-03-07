module SageFs.Samples.FromRust.Hello

open Expecto
open Expecto.Flip

// ============================================================
//  🦀 → 🦅  Coming from Rust? You're going to feel the same love.
//  Make illegal states unrepresentable — same philosophy.
// ============================================================

// ── Enums with data → Discriminated Unions ──
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float
  | Triangle  of base': float * height: float

let area shape =
  match shape with
  | Circle r          -> System.Math.PI * r * r
  | Rectangle (w, h)  -> w * h
  | Triangle (b, h)   -> 0.5 * b * h

// ── Option<T>: None / Some ──
let safeDivide a b =
  if b = 0 then None
  else Some (a / b)

// ── Result<T, E>: Ok / Error ──
let parsePositive (s: string) =
  match System.Int32.TryParse(s) with
  | true, n when n > 0 -> Ok n
  | true, _            -> Error "must be positive"
  | _                  -> Error "not a number"

// ── Structs / Records ──
type Point = { X: float; Y: float }

let origin = { X = 0.0; Y = 0.0 }
let moved  = { origin with X = 3.0 }

// ── Closures ──
let double = fun x -> x * 2
let triple x = x * 3

// ── Higher-order functions ──
let applyTwice f x = f (f x)

// ── Iterators / pipelines ──
let evenSquares =
  [1..10]
  |> List.filter (fun x -> x % 2 = 0)
  |> List.map    (fun x -> x * x)

// ── Structural equality (like derive(PartialEq)) ──
type Color = Red | Green | Blue

let tests = testList "from Rust" [
  testList "discriminated unions" [
    test "circle area" {
      area (Circle 5.0)
      |> Expect.floatClose "π × 5²" Accuracy.medium (System.Math.PI * 25.0)
    }
    test "rectangle area" {
      area (Rectangle (3.0, 4.0))
      |> Expect.floatClose "3 × 4 = 12" Accuracy.medium 12.0
    }
    test "triangle area" {
      area (Triangle (6.0, 4.0))
      |> Expect.floatClose "0.5 × 6 × 4 = 12" Accuracy.medium 12.0
    }
  ]

  testList "option" [
    test "safe divide success" {
      safeDivide 10 3 |> Expect.equal "10 / 3" (Some 3)
    }
    test "safe divide by zero" {
      safeDivide 10 0 |> Expect.equal "div by zero" None
    }
  ]

  testList "result" [
    test "parse positive" {
      parsePositive "42" |> Expect.equal "parse 42" (Ok 42)
    }
    test "parse negative rejected" {
      parsePositive "-5" |> Expect.equal "negative" (Error "must be positive")
    }
    test "parse non-number" {
      parsePositive "abc" |> Expect.equal "not a number" (Error "not a number")
    }
    test "parse zero rejected" {
      parsePositive "0" |> Expect.equal "zero" (Error "must be positive")
    }
  ]

  testList "records" [
    test "origin point" {
      origin.X |> Expect.floatClose "x = 0" Accuracy.medium 0.0
      origin.Y |> Expect.floatClose "y = 0" Accuracy.medium 0.0
    }
    test "non-destructive update" {
      moved.X |> Expect.floatClose "x moved to 3" Accuracy.medium 3.0
      moved.Y |> Expect.floatClose "y unchanged" Accuracy.medium 0.0
    }
  ]

  testList "closures and higher-order" [
    test "double" {
      double 21 |> Expect.equal "double 21" 42
    }
    test "triple" {
      triple 7 |> Expect.equal "triple 7" 21
    }
    test "applyTwice double" {
      applyTwice double 3 |> Expect.equal "double(double 3)" 12
    }
    test "applyTwice triple" {
      applyTwice triple 2 |> Expect.equal "triple(triple 2)" 18
    }
  ]

  testList "pipelines" [
    test "even squares from 1..10" {
      evenSquares |> Expect.equal "filter even, square" [4; 16; 36; 64; 100]
    }
  ]

  testList "structural equality" [
    test "same color equal" {
      (Red = Red) |> Expect.isTrue "Red = Red"
    }
    test "different colors not equal" {
      (Red = Blue) |> Expect.isFalse "Red ≠ Blue"
    }
  ]
]
