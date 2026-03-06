// ============================================================
//  🧘  About Pipelining — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutPipelining.fs
//
//  The |> operator is the heart of F# style.
//  x |> f   means   f x  — "send x through f"
//  Chain multiple operations for readable, composable code.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

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
    Expect.equal result1 __ "squares of evens in 0..5"
  }

  test "SquareEvenNumbers — nested parens" {
    Expect.equal result2 __ "same with parens"
  }

  test "SquareEvenNumbers — pipeline" {
    Expect.equal result3 __ "same with |>"
  }

  test "AllThreeAreEquivalent" {
    Expect.equal result1 result2 "separate == nested parens"
    Expect.equal result2 result3 "nested parens == pipeline"
  }

  test "HowThePipeOperatorIsDefined" {
    // Re-define |> locally to prove how simple it is:
    let (|>) x f = f x
    let result =
      [0..5]
      |> List.filter isEven
      |> List.map square
    Expect.equal result __ "same result even with redefined |>"
  }

  test "PipelineWithAnonymousFunctions" {
    let result =
      [1..10]
      |> List.filter (fun x -> x % 2 = 0)
      |> List.map (fun x -> x * 3)
      |> List.sum
    Expect.equal result __ "sum of (even * 3) for 1..10"
    // Hint: even numbers in 1..10 are 2,4,6,8,10 → ×3 → 6,12,18,24,30 → sum
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `result3` — see [0; 4; 16]
// 2. Add `|> List.sum` at the end — see 20
// 3. Build a pipeline that finds the 3 largest even squares in 1..20
// 4. Compare readability: nested parens vs |> for complex transforms
// ============================================================
