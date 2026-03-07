module SageFs.Samples.FromJava.Hello

open Expecto
open Expecto.Flip

// ============================================================
//  ☕ → 🦅  Coming from Java? Sit down, take a breath.
//  You don't need a framework. You don't need a factory.
// ============================================================

// ── Data classes ──
type Person = { Name: string; Age: int }

let alice = { Name = "Alice"; Age = 30 }
let older = { alice with Age = 31 }

// ── Sealed classes → Discriminated Unions ──
type Shape =
  | Circle    of radius: float
  | Rectangle of width: float * height: float
  | Triangle  of base': float * height: float

let area shape =
  match shape with
  | Circle r          -> System.Math.PI * r * r
  | Rectangle (w, h)  -> w * h
  | Triangle (b, h)   -> 0.5 * b * h

// ── Optional → Option<'T> ──
let safeDivide a b =
  if b = 0 then None
  else Some (a / b)

// ── Generic methods without noise ──
let maxOf a b = if a > b then a else b

// ── Streams → List pipelines ──
let evenSquares =
  [1..10]
  |> List.filter (fun x -> x % 2 = 0)
  |> List.map    (fun x -> x * x)

// ── Interfaces ──
type IAnimal =
  abstract member Speak: unit -> string

type Dog() =
  interface IAnimal with
    member _.Speak() = "Woof!"

// ── Result<'T, 'TError> ──
let readFileLines path =
  try Ok (System.IO.File.ReadAllLines(path))
  with ex -> Error ex.Message

let tests = testList "from Java" [
  testList "records" [
    test "create record" {
      alice.Name |> Expect.equal "name" "Alice"
      alice.Age  |> Expect.equal "age" 30
    }
    test "copy with update" {
      older.Age  |> Expect.equal "updated age" 31
      older.Name |> Expect.equal "name unchanged" "Alice"
    }
  ]

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

  testList "generics" [
    test "maxOf ints" {
      maxOf 3 7 |> Expect.equal "max of 3 and 7" 7
    }
    test "maxOf strings" {
      maxOf "apple" "banana" |> Expect.equal "max of strings" "banana"
    }
  ]

  testList "pipelines" [
    test "even squares from 1..10" {
      evenSquares |> Expect.equal "filter even, square" [4; 16; 36; 64; 100]
    }
  ]

  testList "interfaces" [
    test "Dog speaks" {
      let dog = Dog() :> IAnimal
      dog.Speak() |> Expect.equal "dog says woof" "Woof!"
    }
  ]
]
