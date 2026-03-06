// ============================================================
//  🧘  About Branching — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutBranching.fs
//
//  In F#, if/else and match are EXPRESSIONS — they return values.
//  No separate if-statement vs if-expression distinction.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

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
    Expect.equal (isEven 2) __ "2 is even"
  }

  test "BasicBranching — odd" {
    Expect.equal (isEven 3) __ "3 is odd"
  }

  test "IfStatementsReturnValues" {
    let result =
      if 2 = 3 then "something is REALLY wrong"
      else "no problem here"
    Expect.equal result __ "2 ≠ 3 so we get the else branch"
  }

  test "BranchingWithPatternMatch — apple" {
    Expect.equal (isApple "apple") __ "apple is an apple"
  }

  test "BranchingWithPatternMatch — not apple" {
    Expect.equal (isApple "") __ "empty string is not an apple"
  }

  test "TuplesWithIfStatementsGetClumsy" {
    let getDinnerClumsy x =
      let name, foodChoice = x
      if foodChoice = "veggies" || foodChoice = "fish" || foodChoice = "chicken" then
        sprintf "%s doesn't want red meat" name
      else
        sprintf "%s wants 'em some %s" name foodChoice

    Expect.equal (getDinnerClumsy ("Chris", "steak"))    __ "Chris wants steak"
    Expect.equal (getDinnerClumsy ("Dave", "veggies"))   __ "Dave goes veggie"
  }

  test "PatternMatchingIsNicer — fish" {
    Expect.equal (getDinner ("Bob", "fish")) __ "fish = no red meat"
  }

  test "PatternMatchingIsNicer — Burger" {
    Expect.equal (getDinner ("Sally", "Burger")) __ "Sally gets a Burger"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `isEven 2` and `isEven 7` — see results inline
// 2. Add a `| "pear" -> true` case to isApple
// 3. Try omitting the `else` branch — F# will tell you why it's wrong
// 4. Write a classify function: "fizz" for %3, "buzz" for %5, "fizzbuzz"
// ============================================================
