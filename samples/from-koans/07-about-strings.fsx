// ============================================================
//  🧘  About Strings — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutStrings.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  Strings in F# use double quotes. Single chars use single quotes.
//  F# has string interpolation, formatting, and more.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Exploring strings ────────────────────────────────────────

// Simple string:
"hello"                                    // → "hello"

// Concatenation:
"hello " + "world"                         // → "hello world"

// Interpolation (F# 5+):
let name = "F#"
$"Hello, {name}!"                          // → "Hello, F#!"

// sprintf formatting (older style, still common):
sprintf "F# turns it to %d!" 11            // → "F# turns it to 11!"
sprintf "hello %s" "world"                 // → "hello world"
sprintf "Formatting other types: %A" (1,2,3) // → "Formatting other types: (1, 2, 3)"

// Character indexing (a char is denoted with single quotes 'c'):
let message = "hello world"
message.[0]    // → 'h'
message.[4]    // → 'o'

// Multiline strings using backslash continuation (no embedded newlines):
let singleLine =
  "super\
   cali\
   fragilistic"
// → "supercalifragilistic"  (leading spaces trimmed from each line)

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about strings" [

  test "StringValue" {
    let s = "hello"
    s |> Expect.equal "should be hello" __
  }

  test "StringConcatenation" {
    let s = "hello " + "world"
    s |> Expect.equal "concatenation with +" __
  }

  test "FormattingWithSprintf — int" {
    let s = sprintf "F# turns it to %d!" 11
    s |> Expect.equal "sprintf with %d" __
  }

  test "FormattingWithSprintf — string" {
    let s = sprintf "hello %s" "world"
    s |> Expect.equal "sprintf with %s" __
  }

  test "FormattingAnythingWithPercA" {
    let s = sprintf "Formatting other types is as easy as: %A" (1, 2, 3)
    s |> Expect.equal "sprintf with %A formats any type" __
  }

  test "StringInterpolation" {
    let lang = "F#"
    let s = $"Hello, {lang}!"
    s |> Expect.equal "interpolated string" __
  }

  test "ExtractFirstChar" {
    let s = "hello world"
    s.[0] |> Expect.equal "first char of 'hello world'" __
    // Note: single char literals use single quotes: 'h'
  }

  test "ExtractFifthChar" {
    let s = "hello world"
    s.[4] |> Expect.equal "fifth char (index 4) of 'hello world'" __
  }

  test "ApplyWhatYouLearned" {
    // Fill in the function so the assertions below pass:
    let getFunFacts x =
      __
      // Hint: sprintf "%d doubled is %d, and %d tripled is %d!" x (x*2) x (x*3)

    (getFunFacts 3) |> Expect.equal "fun facts about 3" "3 doubled is 6, and 3 tripled is 9!"
    (getFunFacts 6) |> Expect.equal "fun facts about 6" "6 doubled is 12, and 6 tripled is 18!"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `"hello " + "world"` — see "hello world"
// 2. Alt+Enter `$"Hello, {name}!"` — see "Hello, F#!"
// 3. Try `"hello".[0]` — see the char 'h'
// 4. Explore: String.length, String.toLower, String.toUpper
// ============================================================
