// ============================================================
//  🧘  About Asserts — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutAsserts.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  The F# Koans taught you with NUnit's AssertEquality.
//  SageFs uses Expecto — the F# community's testing library.
//  Same idea. Live gutter markers instead of terminal output.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

// ── The fill-in-the-blank placeholder ────────────────────────
// __ can stand in for any type. Replace it with the real value.
let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── What Expecto looks like ───────────────────────────────────
//
// Old koan style (NUnit):
//   AssertEquality (1 + 1) __
//
// SageFs uses Expecto.Flip — the actual value pipes in last:
//   actual |> Expect.equal "description" expected
//
// Why Flip? It reads like English and works with F# pipelines:
//   myFunction input |> Expect.equal "should compute" expectedResult
//
// SageFs shows ✓/✗ in your gutter as you type.
// No dotnet run. No terminal scrolling. Just green markers.

// Alt+Enter these to see inline results:
let x = 1 + 1               // → 2
let greeting = "Hello, F#!" // → "Hello, F#!"

let tests = testList "about asserts" [

  // ── Fill in the __ below ──────────────────────────────────
  // What is 1 + 1?
  test "AssertExpectation" {
    let expectedValue = 1 + 1
    let actualValue   = __     // ← change this to 2
    actualValue |> Expect.equal "values should be equal" expectedValue
  }

  // Easy, right? Now fill in the next one.
  test "FillInValues" {
    (1 + 1) |> Expect.equal "1 + 1 should equal 2" __
  }

  // ── String equality ───────────────────────────────────────
  test "StringEquality" {
    "hello" |> Expect.equal "strings can be equal too" __
  }

  // ── Boolean equality ──────────────────────────────────────
  test "BooleanEquality" {
    true |> Expect.equal "true is true" __
  }

]

// ── How to read Expecto output in SageFs ─────────────────────
//
// When a test FAILS: 🔴 gutter marker next to the test name
//                    hover to see "Expected: 2, Actual: failwith..."
// When a test PASSES: 🟢 gutter marker — you filled it in correctly!
//
// Expecto.Flip cheat sheet (actual always pipes in last):
//   actual |> Expect.equal "msg" expected
//   actual |> Expect.isTrue "msg"
//   actual |> Expect.isFalse "msg"
//   actual |> Expect.isNone "msg"
//   actual |> Expect.isSome "msg"
//   (fun () -> ...) |> Expect.throws "msg"
//   actual |> Expect.stringContains "msg" substring
//   actual |> Expect.floatClose "msg" Accuracy.medium expected
// ============================================================
