// ============================================================
//  🧘  About Tuples — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutTuples.fs
//
//  Tuples are F#'s way to group values together without naming them.
//  They're lightweight and frequently used for multiple return values.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Exploring tuples ──────────────────────────────────────────

let items = ("apple", "dog")    // a 2-tuple (pair)

// fst and snd extract the first and second elements:
fst items    // → "apple"
snd items    // → "dog"

// Pattern matching works for tuples of any size:
let (fruit, animal, car) = ("apple", "dog", "Mustang")
fruit    // → "apple"
animal   // → "dog"
car      // → "Mustang"

// Use _ to ignore values you don't need:
let (_, theAnimal, _) = ("apple", "dog", "Mustang")
theAnimal    // → "dog"

// Functions can "return" multiple values by returning a tuple:
let squareAndCube x = (x ** 2.0, x ** 3.0)

let (sq, cu) = squareAndCube 3.0
sq    // → 9.0
cu    // → 27.0

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about tuples" [

  test "CreatingTuples" {
    let t = ("apple", "dog")
    Expect.equal t ("apple", __) "second element should be dog"
  }

  test "AccessingTupleElements — fst" {
    let t = ("apple", "dog")
    Expect.equal (fst t) __ "fst should give the first element"
  }

  test "AccessingTupleElements — snd" {
    let t = ("apple", "dog")
    Expect.equal (snd t) __ "snd should give the second element"
  }

  test "AccessingWithPatternMatching — fruit" {
    let (f, _, _) = ("apple", "dog", "Mustang")
    Expect.equal f __ "first element is the fruit"
  }

  test "AccessingWithPatternMatching — animal" {
    let (_, a, _) = ("apple", "dog", "Mustang")
    Expect.equal a __ "second element is the animal"
  }

  test "AccessingWithPatternMatching — car" {
    let (_, _, c) = ("apple", "dog", "Mustang")
    Expect.equal c __ "third element is the car"
  }

  test "IgnoringValuesWithUnderscore" {
    let (_, animal, _) = ("apple", "dog", "Mustang")
    Expect.equal animal __ "only the animal matters here"
  }

  test "ReturningMultipleValuesFromAFunction — squared" {
    let (squared, _) = squareAndCube 3.0
    Expect.equal squared __ "3 squared is 9"
  }

  test "ReturningMultipleValuesFromAFunction — cubed" {
    let (_, cubed) = squareAndCube 3.0
    Expect.equal cubed __ "3 cubed is 27"
  }

  test "TheTruthBehindMultipleReturnValues" {
    // squareAndCube doesn't really return two values —
    // it returns ONE value that happens to be a tuple.
    let result = squareAndCube 3.0
    Expect.equal result __ "should be the tuple (9.0, 27.0)"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `items` — see ("apple", "dog")
// 2. Alt+Enter `fst items` — see "apple"
// 3. Create a 4-tuple and pattern match all four values
// 4. Write a function that swaps a 2-tuple: swap (a, b) = (b, a)
// ============================================================
