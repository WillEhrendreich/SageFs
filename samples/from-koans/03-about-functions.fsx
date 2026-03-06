// ============================================================
//  🧘  About Functions — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutFunctions.fs
//
//  Now that you know let, you'll use it to define functions.
//  Functions are first-class values in F#.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Defining functions ────────────────────────────────────────
// In F#, functions are defined with let — no 'def', no 'fun', no return.
// The last expression in the body is the return value.

let add x y =
  x + y

// Alt+Enter these to explore:
add 2 2     // → 4
add 5 2     // → 7

// Nested functions — inner functions can access outer values (closure):
let caffeinate (text: string) =
  let suffix = "!!!"                    // captured from outer scope
  let exclaimed = text.Trim() + suffix
  let yelled = exclaimed.ToUpper()
  yelled

caffeinate "hello there"   // → "HELLO THERE!!!"

// Type annotations: sometimes you need to help the type inferencer:
let sayItLikeAnAuctioneer (text: string) =
  text.Replace(" ", "")

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about functions" [

  test "CreatingFunctionsWithLet — first call" {
    Expect.equal (add 2 2) __ "add 2 2 should be 4"
  }

  test "CreatingFunctionsWithLet — second call" {
    Expect.equal (add 5 2) __ "add 5 2 should be 7"
  }

  test "NestingFunctions" {
    let quadruple x =
      let double x = x * 2
      double (double x)
    Expect.equal (quadruple 4) __ "quadruple 4 should be 16"
  }

  test "AddingTypeAnnotations" {
    let result = sayItLikeAnAuctioneer "going once going twice sold to the lady in red"
    Expect.equal result __ "spaces should be removed"
  }

  test "VariablesInParentScopeCanBeAccessed" {
    let result = caffeinate "hello there"
    Expect.equal result __ "should be yelled with exclamation"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter on `add 2 2` — see 4 inline
// 2. Try removing the type annotation on sayItLikeAnAuctioneer
//    → F# can't infer .Replace without knowing it's a string
// 3. Write a function `multiply x y = x * y` and Alt+Enter it
// ============================================================
