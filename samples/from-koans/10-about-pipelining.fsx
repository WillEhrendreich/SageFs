// ============================================================
//  🧘  About Pipelining — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutPipelining.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  The |> operator is the heart of F# style.
//  x |> f   means   f x  — "send x through f"
//  Chain multiple operations for readable, composable code.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Exploring pipelines ───────────────────────────────────────

let square x = x * x
let isEven x = x % 2 = 0

// Three ways to square even numbers from 0..5:

// 1. Separate statements (verbose, requires naming each step):
let numbers = [0..5]
let evens   = List.filter isEven numbers
let result1 = List.map square evens
// → [0; 4; 16]

// 2. Nested parens (hard to read — works inside out):
let result2 = List.map square (List.filter isEven [0..5])
// → [0; 4; 16]

// 3. Pipeline operator (reads like English — left to right):
let result3 =
  [0..5]
  |> List.filter isEven
  |> List.map square
// → [0; 4; 16]

// How |> is defined:
// let (|>) x f = f x
// That's literally it. Simplest useful operator in F#.

// Real-world pipeline — Alt+Enter to see each step:
[1..20]
|> List.filter (fun x -> x % 3 = 0)    // keep multiples of 3
|> List.map (fun x -> x * x)            // square them
|> List.sum                             // sum them up
// → 1^2 + ... well, try it!

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about pipelining" [

  test "SquareEvenNumbers — separate statements" {
    result1 |> Expect.equal "squares of evens in 0..5" __
  }

  test "SquareEvenNumbers — nested parens" {
    result2 |> Expect.equal "same with parens" __
  }

  test "SquareEvenNumbers — pipeline" {
    result3 |> Expect.equal "same with |>" __
  }

  test "AllThreeAreEquivalent" {
    result1 |> Expect.equal "separate == nested parens" result2
    result2 |> Expect.equal "nested parens == pipeline" result3
  }

  test "HowThePipeOperatorIsDefined" {
    // Re-define |> locally to prove how simple it is:
    let (|>) x f = f x
    let result =
      [0..5]
      |> List.filter isEven
      |> List.map square
    result |> Expect.equal "same result even with redefined |>" __
  }

  test "PipelineWithAnonymousFunctions" {
    let result =
      [1..10]
      |> List.filter (fun x -> x % 2 = 0)
      |> List.map (fun x -> x * 3)
      |> List.sum
    result |> Expect.equal "sum of (even * 3) for 1..10" __
    // Hint: even numbers in 1..10 are 2,4,6,8,10 → ×3 → 6,12,18,24,30 → sum
  }

]

// ── Things to try (SageFs makes this magic) ──────────────────
// 1. Alt+Enter `result3` — see [0; 4; 16] instantly inline
// 2. Add `|> List.sum` at the end — see 20 appear in your editor
// 3. Build a pipeline that finds the 3 largest even squares in 1..20
// 4. Compare readability: nested parens vs |> for complex transforms
//
// 🔥 Try this: highlight the real-world pipeline above (lines 49-53)
//    and press Alt+Enter. SageFs evaluates the whole pipeline and shows
//    the result inline. No REPL window, no terminal — just results.
// ============================================================
