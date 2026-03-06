// ============================================================
//  🧘  About Branching — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutBranching.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  In F#, if/else and match are EXPRESSIONS — they return values.
//  No separate if-statement vs if-expression distinction.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── if is an expression ───────────────────────────────────────

let isEven x =
  if x % 2 = 0 then "it's even!"
  else "it's odd!"

isEven 2    // → "it's even!"
isEven 3    // → "it's odd!"

// if returns a value — you can assign it:
let classification =
  if 2 = 3 then "something is REALLY wrong"
  else "no problem here"
// → "no problem here"

// ── match is pattern matching ─────────────────────────────────

let isApple x =
  match x with
  | "apple" -> true
  | _       -> false    // _ matches anything

isApple "apple"    // → true
isApple "pear"     // → false

// match with tuples:
let getDinner x =
  match x with
  | (name, "veggies")
  | (name, "fish")
  | (name, "chicken") -> sprintf "%s doesn't want red meat" name
  | (name, food)      -> sprintf "%s wants 'em some %s" name food

getDinner ("Bob", "fish")     // → "Bob doesn't want red meat"
getDinner ("Sally", "Burger") // → "Sally wants 'em some Burger"

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about branching" [

  test "BasicBranching — even" {
    (isEven 2) |> Expect.equal "2 is even" __
  }

  test "BasicBranching — odd" {
    (isEven 3) |> Expect.equal "3 is odd" __
  }

  test "IfStatementsReturnValues" {
    let result =
      if 2 = 3 then "something is REALLY wrong"
      else "no problem here"
    result |> Expect.equal "2 ≠ 3 so we get the else branch" __
  }

  test "BranchingWithPatternMatch — apple" {
    (isApple "apple") |> Expect.equal "apple is an apple" __
  }

  test "BranchingWithPatternMatch — not apple" {
    (isApple "") |> Expect.equal "empty string is not an apple" __
  }

  test "TuplesWithIfStatementsGetClumsy" {
    let getDinnerClumsy x =
      let name, foodChoice = x
      if foodChoice = "veggies" || foodChoice = "fish" || foodChoice = "chicken" then
        sprintf "%s doesn't want red meat" name
      else
        sprintf "%s wants 'em some %s" name foodChoice

    (getDinnerClumsy ("Chris", "steak")) |> Expect.equal "Chris wants steak" __
    (getDinnerClumsy ("Dave", "veggies")) |> Expect.equal "Dave goes veggie" __
  }

  test "PatternMatchingIsNicer — fish" {
    (getDinner ("Bob", "fish")) |> Expect.equal "fish = no red meat" __
  }

  test "PatternMatchingIsNicer — Burger" {
    (getDinner ("Sally", "Burger")) |> Expect.equal "Sally gets a Burger" __
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `isEven 2` and `isEven 7` — see results inline
// 2. Add a `| "pear" -> true` case to isApple
// 3. Try omitting the `else` branch — F# will tell you why it's wrong
// 4. Write a classify function: "fizz" for %3, "buzz" for %5, "fizzbuzz"
//
// 💡 SageFs convention: In production F#, prefer `match` over `if/else`.
//    Pattern matching is more expressive, exhaustive, and composes better.
//    Notice how getDinnerClumsy (if/else) is harder to read than getDinner
//    (match)? That's not a coincidence — match scales; if/else doesn't.
//    As you progress, you'll see pattern matching everywhere in F#.
// ============================================================
