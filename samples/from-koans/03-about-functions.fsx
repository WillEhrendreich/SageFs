// ============================================================
//  🧘  About Functions — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutFunctions.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  Now that you know let, you'll use it to define functions.
//  Functions are first-class values in F#.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

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
    (add 2 2) |> Expect.equal "add 2 2 should be 4" __
  }

  test "CreatingFunctionsWithLet — second call" {
    (add 5 2) |> Expect.equal "add 5 2 should be 7" __
  }

  test "NestingFunctions" {
    let quadruple x =
      let double x = x * 2
      double (double x)
    (quadruple 4) |> Expect.equal "quadruple 4 should be 16" __
  }

  test "AddingTypeAnnotations" {
    let result = sayItLikeAnAuctioneer "going once going twice sold to the lady in red"
    result |> Expect.equal "spaces should be removed" __
  }

  test "VariablesInParentScopeCanBeAccessed" {
    let result = caffeinate "hello there"
    result |> Expect.equal "should be yelled with exclamation" __
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter on `add 2 2` — see 4 inline
// 2. Try removing the type annotation on sayItLikeAnAuctioneer
//    → F# can't infer .Replace without knowing it's a string
// 3. Write a function `multiply x y = x * y` and Alt+Enter it
// ============================================================
