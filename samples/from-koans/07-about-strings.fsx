// ============================================================
//  🧘  About Strings — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutStrings.fs
//
//  Strings in F# use double quotes. Single chars use single quotes.
//  F# has string interpolation, formatting, and more.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

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
    Expect.equal s __ "should be hello"
  }

  test "StringConcatenation" {
    let s = "hello " + "world"
    Expect.equal s __ "concatenation with +"
  }

  test "FormattingWithSprintf — int" {
    let s = sprintf "F# turns it to %d!" 11
    Expect.equal s __ "sprintf with %d"
  }

  test "FormattingWithSprintf — string" {
    let s = sprintf "hello %s" "world"
    Expect.equal s __ "sprintf with %s"
  }

  test "FormattingAnythingWithPercA" {
    let s = sprintf "Formatting other types is as easy as: %A" (1, 2, 3)
    Expect.equal s __ "sprintf with %A formats any type"
  }

  test "StringInterpolation" {
    let lang = "F#"
    let s = $"Hello, {lang}!"
    Expect.equal s __ "interpolated string"
  }

  test "ExtractFirstChar" {
    let s = "hello world"
    Expect.equal s.[0] __ "first char of 'hello world'"
    // Note: single char literals use single quotes: 'h'
  }

  test "ExtractFifthChar" {
    let s = "hello world"
    Expect.equal s.[4] __ "fifth char (index 4) of 'hello world'"
  }

  test "ApplyWhatYouLearned" {
    // Fill in the function so the assertions below pass:
    let getFunFacts x =
      __
      // Hint: sprintf "%d doubled is %d, and %d tripled is %d!" x (x*2) x (x*3)

    Expect.equal (getFunFacts 3) "3 doubled is 6, and 3 tripled is 9!"  "fun facts about 3"
    Expect.equal (getFunFacts 6) "6 doubled is 12, and 6 tripled is 18!" "fun facts about 6"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `"hello " + "world"` — see "hello world"
// 2. Alt+Enter `$"Hello, {name}!"` — see "Hello, F#!"
// 3. Try `"hello".[0]` — see the char 'h'
// 4. Explore: String.length, String.toLower, String.toUpper
// ============================================================
