// ============================================================
//  🧘  About Looping — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutLooping.fs
//
//  F# supports imperative for/while loops, but idiomatic F#
//  prefers List/Seq/Array module functions. Learn both.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Imperative loops ─────────────────────────────────────────

// for..in — iterate over a sequence:
let mutable sumForIn = 0
for value in [0..10] do
  sumForIn <- sumForIn + value
sumForIn    // → 55

// for..to — numeric range:
let mutable sumForTo = 0
for i = 1 to 5 do
  sumForTo <- sumForTo + i
sumForTo    // → 15

// while — loop until condition is false:
let mutable n = 1
while n < 10 do
  n <- n + n
n           // → 16  (1 → 2 → 4 → 8 → 16, stops when n >= 10)

// ── The functional alternative ────────────────────────────────
// These loops above use mutable state. The functional way:
[0..10] |> List.sum                      // → 55  (no mutation!)
[1..5]  |> List.sum                      // → 15

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about looping" [

  test "LoopingOverAList — for..in" {
    let values = [0..10]
    let mutable sum = 0
    for value in values do
      sum <- sum + value
    Expect.equal sum __ "sum of 0..10"
  }

  test "LoopingWithExpressions — for..to" {
    let mutable sum = 0
    for i = 1 to 5 do
      sum <- sum + i
    Expect.equal sum __ "sum of 1..5"
  }

  test "LoopingWithWhile" {
    let mutable s = 1
    while s < 10 do
      s <- s + s
    Expect.equal s __ "doubling: 1→2→4→8→16, first ≥10"
  }

  test "FunctionalAlternative — List.sum equals for..in result" {
    let imperativeSum =
      let mutable acc = 0
      for i in [0..10] do acc <- acc + i
      acc
    let functionalSum = [0..10] |> List.sum
    Expect.equal imperativeSum functionalSum "both give the same answer"
    Expect.equal functionalSum __ "sum of 0..10"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `sumForIn` — see 55
// 2. Rewrite the while loop using List.fold or List.reduce
// 3. Try `[1..100] |> List.sum` — answer in <1ms, no loop needed
// 4. Use `for i in [0..2..10] do` — loop with step size
//
// NOTE: Use loops when you need side effects (printing, IO).
//       Use List/Seq/Array functions for transformations.
// ============================================================
