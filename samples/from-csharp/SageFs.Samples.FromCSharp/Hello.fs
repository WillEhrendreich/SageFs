module SageFs.Samples.FromCSharp.Hello

open Expecto
open Expecto.Flip

// ============================================================
//  🔷 → 🦅  Coming from C#? You're going to love this.
//  F# is C#'s cooler sibling. Same .NET. Half the code. Twice the fun.
// ============================================================

// ── Records: no ceremony ──
// C#: public class Person { public string Name { get; init; } ... }
// F#:
type Person = { Name: string; Age: int }

let alice = { Name = "Alice"; Age = 30 }
let olderAlice = { alice with Age = 31 }

// ── Discriminated Unions: the enum you always wanted ──
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float
  | Triangle  of base': float * height: float

let area = function
  | Circle r          -> System.Math.PI * r * r
  | Rectangle (w, h)  -> w * h
  | Triangle (b, h)   -> 0.5 * b * h

// ── No null. Use Option<'T>. ──
let greet (name: string option) =
  match name with
  | Some n -> $"Hello, {n}!"
  | None   -> "Hello, stranger!"

// ── Result<'T, 'TError>: exceptions are for exceptional things ──
let divide a b =
  if b = 0 then Error "division by zero"
  else Ok (a / b)

// ── Pipelines ──
let sumOfEvenSquares =
  [1..10]
  |> List.filter (fun x -> x % 2 = 0)
  |> List.map    (fun x -> x * x)
  |> List.sum

// ── Type inference ──
let add a b = a + b

let tests = testList "from C#" [
  testList "records" [
    test "create a record" {
      alice.Name |> Expect.equal "name is Alice" "Alice"
      alice.Age  |> Expect.equal "age is 30" 30
    }
    test "non-destructive update" {
      olderAlice.Age  |> Expect.equal "age updated to 31" 31
      olderAlice.Name |> Expect.equal "name unchanged" "Alice"
    }
  ]

  testList "discriminated unions" [
    test "circle area" {
      area (Circle 3.0)
      |> Expect.floatClose "π * 3² ≈ 28.27" Accuracy.medium (System.Math.PI * 9.0)
    }
    test "rectangle area" {
      area (Rectangle (4.0, 5.0))
      |> Expect.floatClose "4 × 5 = 20" Accuracy.medium 20.0
    }
    test "triangle area" {
      area (Triangle (6.0, 4.0))
      |> Expect.floatClose "0.5 × 6 × 4 = 12" Accuracy.medium 12.0
    }
  ]

  testList "option" [
    test "greet with Some" {
      greet (Some "Bob")
      |> Expect.equal "greets Bob" "Hello, Bob!"
    }
    test "greet with None" {
      greet None
      |> Expect.equal "greets stranger" "Hello, stranger!"
    }
  ]

  testList "result" [
    test "divide success" {
      divide 10 2
      |> Expect.equal "10 / 2 = 5" (Ok 5)
    }
    test "divide by zero" {
      divide 10 0
      |> Expect.equal "division by zero" (Error "division by zero")
    }
  ]

  testList "pipelines" [
    test "sum of even squares 1..10" {
      sumOfEvenSquares
      |> Expect.equal "2²+4²+6²+8²+10² = 220" 220
    }
  ]

  testList "type inference" [
    test "add infers int" {
      add 3 4 |> Expect.equal "3 + 4 = 7" 7
    }
  ]
]
