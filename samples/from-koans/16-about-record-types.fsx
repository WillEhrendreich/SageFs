// ============================================================
//  🧘  About Record Types — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutRecordTypes.fs
//
//  Records are named, immutable product types.
//  They give you structural equality, ToString, and GetHashCode
//  for free — no boilerplate classes needed.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Defining record types ─────────────────────────────────────
type Character = {
  Name:       string
  Occupation: string
}

// ── Creating record values ────────────────────────────────────
let mario = { Name = "Mario"; Occupation = "Plumber" }

mario.Name         // → "Mario"
mario.Occupation   // → "Plumber"

// Non-destructive update with `with` — original is UNCHANGED:
let luigi = { mario with Name = "Luigi" }

luigi.Name         // → "Luigi"
luigi.Occupation   // → "Plumber"   (inherited from mario)
mario.Name         // → "Mario"     (still "Mario", not changed)

// ── Structural equality ───────────────────────────────────────
// Records compare by VALUE, not by reference (no need to override Equals):
let greenKoopa = { Name = "Koopa"; Occupation = "Soldier" }
let redKoopa   = { Name = "Koopa"; Occupation = "Soldier" }
let bowser     = { Name = "Bowser"; Occupation = "Kidnapper" }

greenKoopa = redKoopa   // → true  (same values)
greenKoopa = bowser     // → false (different values)

// ── Pattern matching on records ───────────────────────────────
let determineSide character =
  match character with
  | { Occupation = "Plumber" } -> "good guy"
  | _                          -> "bad guy"

determineSide mario   // → "good guy"
determineSide bowser  // → "bad guy"

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about record types" [

  test "RecordsHaveProperties — Name" {
    Expect.equal mario.Name __ "mario's name"
  }

  test "RecordsHaveProperties — Occupation" {
    Expect.equal mario.Occupation __ "mario's occupation"
  }

  test "CreatingFromExistingRecord — mario name unchanged" {
    Expect.equal mario.Name __ "with update does not change original"
  }

  test "CreatingFromExistingRecord — luigi name" {
    Expect.equal luigi.Name __ "luigi gets a new name"
  }

  test "CreatingFromExistingRecord — luigi inherits occupation" {
    Expect.equal luigi.Occupation __ "luigi inherits mario's occupation"
  }

  test "ComparingRecords — same values are equal" {
    let koopaComparison =
      if greenKoopa = redKoopa then "all the koopas are pretty much the same"
      else "maybe one can fly"
    Expect.equal koopaComparison __ "koopas with same fields are equal"
  }

  test "ComparingRecords — different values are not equal" {
    let bowserComparison =
      if bowser = greenKoopa then "the king is a pawn"
      else "he is still kind of a koopa"
    Expect.equal bowserComparison __ "bowser ≠ koopa"
  }

  test "PatternMatchOnRecords — mario is good guy" {
    Expect.equal (determineSide mario) __ "plumbers are good guys"
  }

  test "PatternMatchOnRecords — luigi is good guy" {
    Expect.equal (determineSide luigi) __ "luigi is also a plumber"
  }

  test "PatternMatchOnRecords — bowser is bad guy" {
    Expect.equal (determineSide bowser) __ "kidnapper = bad guy"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `mario` — see the record printed
// 2. Add a `Level: int` field to Character — watch all record
//    creations highlight as needing the new field
// 3. Create a list of characters and filter by occupation
// 4. Try `mario = { Name = "Mario"; Occupation = "Plumber" }` — true!
// ============================================================
