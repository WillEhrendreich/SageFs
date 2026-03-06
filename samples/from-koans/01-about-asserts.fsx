// ============================================================
//  🧘  About Asserts — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutAsserts.fs
//
//  The F# Koans taught you with NUnit's AssertEquality.
//  SageFs uses Expecto — the F# community's testing library.
//  Same idea. Live gutter markers instead of terminal output.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

// ── The fill-in-the-blank placeholder ────────────────────────
// __ can stand in for any type. Replace it with the real value.
let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── What Expecto looks like ───────────────────────────────────
//
// Old koan style:
//   AssertEquality (1 + 1) __
//
// Expecto style:
//   Expect.equal actual expected "description"
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
    Expect.equal actualValue expectedValue "values should be equal"
  }

  // Easy, right? Now fill in the next one.
  test "FillInValues" {
    Expect.equal (1 + 1) __ "1 + 1 should equal 2"
  }

  // ── String equality ───────────────────────────────────────
  test "StringEquality" {
    Expect.equal "hello" __ "strings can be equal too"
  }

  // ── Boolean equality ──────────────────────────────────────
  test "BooleanEquality" {
    Expect.equal true __ "true is true"
  }

]

// ── How to read Expecto output in SageFs ─────────────────────
//
// When a test FAILS: 🔴 gutter marker next to the test name
//                    hover to see "Expected: 2, Actual: failwith..."
// When a test PASSES: 🟢 gutter marker — you filled it in correctly!
//
// Expecto also has many other assertions:
//   Expect.isTrue      condition "message"
//   Expect.isFalse     condition "message"
//   Expect.isNone      optionValue "message"
//   Expect.isSome      optionValue "message"
//   Expect.throws      (fun () -> ...) "message"
//   Expect.stringContains str substr "message"
//   Expect.floatClose  Accuracy.medium actual expected "message"
// ============================================================
