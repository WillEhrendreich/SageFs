// ============================================================
//  🧘  About Let — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutLet.fs
//
//  The let keyword is one of the most fundamental parts of F#.
//  You'll use it in almost every line of F# code you write.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

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
    Expect.equal bound __ "x should equal 50"
  }

  test "LetInfersTypesWherePoossible — int" {
    let n = 50
    Expect.equal (n.GetType()) typeof<int> "n should be an int"
  }

  test "LetInfersTypes — string" {
    let s = "a string"
    Expect.equal (s.GetType()) __ "s should be a string"
  }

  test "YouCanMakeTypesExplicit — int" {
    let (explicit: int) = 42
    Expect.equal (explicit.GetType()) __ "should be typeof<int>"
  }

  test "YouCanMakeTypesExplicit — string" {
    let (explicit: string) = "forty two"
    Expect.equal (explicit.GetType()) __ "should be typeof<string>"
  }

  test "FloatsAndIntsAreDifferentTypes" {
    // In F#, int and float are distinct — no implicit conversion.
    let intVal   = 20
    let floatVal = 20.0
    Expect.equal (intVal.GetType())   typeof<int>   "should be int"
    Expect.equal (floatVal.GetType()) __ "should be float"
    // NOTE: float in F# is the same as double in C#
  }

  test "ModifyingMutableValues" {
    let mutable n = 100
    n <- 200
    Expect.equal n __ "n should be 200 after reassignment"
  }

  test "ShadowingAllowsReusingNames" {
    let n = 50
    // Immutable — you cannot do: n <- 100
    // But you can shadow the name:
    let n = 100
    Expect.equal n __ "n should be the shadowed value 100"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter on `x` — see 50 inline
// 2. Add `let mutable m = 5` then `m <- 10` — see the mutation
// 3. Try removing `mutable` from counter — see the compiler error
// ============================================================
