// ============================================================
//  🧘  More About Functions — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — MoreAboutFunctions.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  F# functions have superpowers: lambdas, currying, partial
//  application. These make |> pipelines so powerful.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Lambdas (anonymous functions) ────────────────────────────
// The `fun` keyword creates an anonymous function.

let colors = ["maize"; "blue"]

let echoed =
  colors
  |> List.map (fun x -> x + " " + x)
// → ["maize maize"; "blue blue"]

// ── Functions that return functions ───────────────────────────
// A function can return another function — this enables currying.

let add x =
  (fun y -> x + y)     // returns a function that adds x

add 2 4         // → 6   (call both at once)

let addTen = add 10   // partial application — capture x=10
addTen 14       // → 24  (call the residual function)

// ── Automatic currying ────────────────────────────────────────
// F# automatically curries multi-parameter functions.
// `add2 x y = x + y` is the same as `add2 x = (fun y -> x + y)`

let add2 x y = x + y

let addSeven = add2 7    // partial: fix first argument
addSeven 6   // → 13
addSeven 0   // → 7

// ── Non-curried form (tuple arguments) ───────────────────────
// Use this when you need C#/VB interop or explicit tupling.

let addTuple (x, y) = x + y    // takes one tuple argument

addTuple (5, 40)    // → 45
// addTuple 5 would NOT compile — must pass both at once

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "more about functions" [

  test "DefiningLambdas" {
    let echo = colors |> List.map (fun x -> x + " " + x)
    echo |> Expect.equal "each color echoed" __
  }

  test "FunctionsThatReturnFunctions — simple call" {
    let result = add 2 4
    result |> Expect.equal "add 2 4" __
  }

  test "FunctionsThatReturnFunctions — partial application" {
    let addTen' = add 10
    let result  = addTen' 14
    result |> Expect.equal "add ten to 14" __
  }

  test "AutomaticCurrying — unlucky number" {
    let unlucky = addSeven 6
    unlucky |> Expect.equal "7 + 6" __
  }

  test "AutomaticCurrying — lucky number" {
    let lucky = addSeven 0
    lucky |> Expect.equal "7 + 0" __
  }

  test "NonCurriedTupleForm" {
    let result = addTuple (5, 40)
    result |> Expect.equal "5 + 40 with tuple args" __
  }

  test "PartialApplicationInPipelines" {
    // List.map, List.filter etc. are automatically curried.
    // This lets you write: List.map square  (not: List.map (fun x -> square x))
    let double  = (*) 2          // partial application of (*)
    let doubles = [1..5] |> List.map double
    doubles |> Expect.equal "double each of 1..5" __
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `addSeven 6` — see 13
// 2. Try `add2 3` — get back a function (int -> int)
// 3. Write `multiply x y = x * y` then `triple = multiply 3`
// 4. Use `>>` (function composition): let squareThenDouble = square >> double
// ============================================================
