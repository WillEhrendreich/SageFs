// ============================================================
//  🧘  About Let — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutLet.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  The let keyword is one of the most fundamental parts of F#.
//  You'll use it in almost every line of F# code you write.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Exploring let ────────────────────────────────────────────
// Alt+Enter any of these to see the result inline:

let x = 50              // → 50 (int)
let y = "a string"      // → "a string" (string)
let z = true            // → true (bool)

// F# infers types from values. You rarely need annotations.
x.GetType()             // → System.Int32
y.GetType()             // → System.String

// You can make types explicit:
let (explicitInt: int)    = 42
let (explicitStr: string) = "forty two"

// Mutable values require the mutable keyword:
let mutable counter = 100
counter <- 200          // ← is assignment (not =)
counter                 // → 200

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about let" [

  test "LetBindsANameToAValue" {
    let bound = 50
    bound |> Expect.equal "x should equal 50" __
  }

  test "LetInfersTypesWherePossible — int" {
    let n = 50
    (n.GetType()) |> Expect.equal "n should be an int" typeof<int>
  }

  test "LetInfersTypes — string" {
    let s = "a string"
    (s.GetType()) |> Expect.equal "s should be a string" __
  }

  test "YouCanMakeTypesExplicit — int" {
    let (explicit: int) = 42
    (explicit.GetType()) |> Expect.equal "should be typeof<int>" __
  }

  test "YouCanMakeTypesExplicit — string" {
    let (explicit: string) = "forty two"
    (explicit.GetType()) |> Expect.equal "should be typeof<string>" __
  }

  test "FloatsAndIntsAreDifferentTypes" {
    // In F#, int and float are distinct — no implicit conversion.
    let intVal   = 20
    let floatVal = 20.0
    (intVal.GetType()) |> Expect.equal "should be int" typeof<int>
    (floatVal.GetType()) |> Expect.equal "should be float" __
    // NOTE: float in F# is the same as double in C#
  }

  test "ModifyingMutableValues" {
    let mutable n = 100
    n <- 200
    n |> Expect.equal "n should be 200 after reassignment" __
  }

  test "ShadowingAllowsReusingNames" {
    let n = 50
    // Immutable — you cannot do: n <- 100
    // But you can shadow the name:
    let n = 100
    n |> Expect.equal "n should be the shadowed value 100" __
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter on `x` — see 50 inline
// 2. Add `let mutable m = 5` then `m <- 10` — see the mutation
// 3. Try removing `mutable` from counter — see the compiler error
// ============================================================
