module SageFs.Samples.FromJavaScript.Hello

open Expecto
open Expecto.Flip

// ============================================================
//  🟨 → 🦅  Coming from JavaScript / TypeScript? You'll feel right at home.
// ============================================================

// ── Variables: let is immutable by default ──
let x = 42

// ── Lambdas and function definitions ──
let double = fun x -> x * 2
let double' x = x * 2

// ── Types ──
type Person = { Name: string; Age: int }

let alice = { Name = "Alice"; Age = 30 }
let older = { alice with Age = 31 }

// ── Discriminated Unions ──
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float

let area shape =
  match shape with
  | Circle r          -> System.Math.PI * r * r
  | Rectangle (w, h)  -> w * h

// ── Option<'T>: no more undefined ──
let greet (name: string option) =
  match name with
  | Some n -> $"Hey, {n}!"
  | None   -> "Hey, you!"

// ── Pipelines ──
let evenSquares =
  [1..10]
  |> List.filter (fun x -> x % 2 = 0)
  |> List.map    (fun x -> x * x)

// ── Modules ──
module MathUtils =
  let square x = x * x
  let cube x = x * x * x

// ── Pattern matching ──
let classify x =
  match x with
  | 0                 -> "zero"
  | n when n < 0      -> "negative"
  | n when n % 2 = 0  -> "positive even"
  | _                 -> "positive odd"

// ── Type inference ──
let add a b = a + b
let concat (a: string) b = a + b

let tests = testList "from JavaScript" [
  testList "immutable bindings" [
    test "x is 42" {
      x |> Expect.equal "x bound to 42" 42
    }
  ]

  testList "functions" [
    test "lambda double" {
      double 21 |> Expect.equal "double 21" 42
    }
    test "named double'" {
      double' 21 |> Expect.equal "double' 21" 42
    }
  ]

  testList "records" [
    test "create record" {
      alice.Name |> Expect.equal "name" "Alice"
    }
    test "spread-style copy" {
      older.Age |> Expect.equal "updated age" 31
    }
  ]

  testList "discriminated unions" [
    test "circle area" {
      area (Circle 3.0)
      |> Expect.floatClose "π × 3²" Accuracy.medium (System.Math.PI * 9.0)
    }
    test "rectangle area" {
      area (Rectangle (4.0, 5.0))
      |> Expect.floatClose "4 × 5 = 20" Accuracy.medium 20.0
    }
  ]

  testList "option" [
    test "greet with Some" {
      greet (Some "Alice") |> Expect.equal "greets Alice" "Hey, Alice!"
    }
    test "greet with None" {
      greet None |> Expect.equal "greets anonymous" "Hey, you!"
    }
  ]

  testList "pipelines" [
    test "even squares from 1..10" {
      evenSquares |> Expect.equal "filter even, square" [4; 16; 36; 64; 100]
    }
  ]

  testList "modules" [
    test "square" {
      MathUtils.square 5 |> Expect.equal "5² = 25" 25
    }
    test "cube" {
      MathUtils.cube 3 |> Expect.equal "3³ = 27" 27
    }
  ]

  testList "pattern matching" [
    test "zero" {
      classify 0 |> Expect.equal "zero" "zero"
    }
    test "negative" {
      classify -3 |> Expect.equal "negative" "negative"
    }
    test "positive even" {
      classify 4 |> Expect.equal "positive even" "positive even"
    }
    test "positive odd" {
      classify 5 |> Expect.equal "positive odd" "positive odd"
    }
  ]

  testList "type inference" [
    test "add ints" {
      add 3 4 |> Expect.equal "3 + 4" 7
    }
    test "concat strings" {
      concat "hello " "world" |> Expect.equal "concat" "hello world"
    }
  ]
]
