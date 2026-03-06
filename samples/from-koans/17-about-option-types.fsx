// ============================================================
//  🧘  About Option Types — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutOptionTypes.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  Option<'T> represents a value that may or may not exist.
//  Some 42 means "there IS a value: 42".
//  None means "there is NO value".
//  No nulls. No NullReferenceException. The compiler has your back.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Exploring Option types ────────────────────────────────────

type Game = {
  Name:     string
  Platform: string
  Score:    int option    // might have a score, might not
}

let chronoTrigger = { Name = "Chrono Trigger"; Platform = "SNES"; Score = Some 5 }
let halo          = { Name = "Halo";           Platform = "Xbox"; Score = None   }

// Checking Some/None:
chronoTrigger.Score.IsSome   // → true
chronoTrigger.Score.IsNone   // → false
chronoTrigger.Score.Value    // → 5  (unsafe — throws if None)
halo.Score.IsSome            // → false
halo.Score.IsNone            // → true
// halo.Score.Value           // ← throws System.InvalidOperationException!

// Pattern matching is the safe way to unwrap:
let translate score =
  match score with
  | 5 -> "Great"  | 4 -> "Good"  | 3 -> "Decent"
  | 2 -> "Bad"    | 1 -> "Awful" | _ -> "Unknown"

let getScore game =
  match game.Score with
  | Some score -> translate score
  | None       -> "Unknown"

getScore chronoTrigger   // → "Great"
getScore halo            // → "Unknown"

// Option.map — transform the value if present, else propagate None:
let decideOn game =
  game.Score
  |> Option.map (fun score -> if score > 3 then "play it" else "don't play")

decideOn chronoTrigger   // → Some "play it"
decideOn halo            // → None

// Option.defaultValue — provide a fallback:
decideOn chronoTrigger |> Option.defaultValue "no opinion"  // → "play it"
decideOn halo          |> Option.defaultValue "no opinion"  // → "no opinion"

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about option types" [

  test "OptionTypesMightContainAValue — IsSome" {
    let v = Some 10
    v.IsSome |> Expect.equal "Some 10 is Some" __
  }

  test "OptionTypesMightContainAValue — IsNone" {
    let v = Some 10
    v.IsNone |> Expect.equal "Some 10 is not None" __
  }

  test "OptionTypesMightContainAValue — Value" {
    let v = Some 10
    v.Value |> Expect.equal "Value of Some 10" __
  }

  test "NoneHasNoValue — IsSome" {
    let v: int option = None
    v.IsSome |> Expect.equal "None is not Some" __
  }

  test "NoneHasNoValue — IsNone" {
    let v: int option = None
    v.IsNone |> Expect.equal "None is None" __
  }

  test "NoneValueThrows" {
    let v: int option = None
    (fun () -> v.Value |> ignore) |> Expect.throws "accessing .Value on None throws"
  }

  test "UsingOptionWithPatternMatching — Chrono Trigger" {
    (getScore chronoTrigger) |> Expect.equal "Chrono Trigger got Great" __
  }

  test "UsingOptionWithPatternMatching — Halo unscored" {
    (getScore halo) |> Expect.equal "Halo has no score" __
  }

  test "ProjectingValuesFromOptions — Some result" {
    (decideOn chronoTrigger) |> Expect.equal "score > 3 → play it" __
  }

  test "ProjectingValuesFromOptions — None propagates" {
    (decideOn halo) |> Expect.equal "no score → None" __
  }

  test "DefaultValueHandlesNone" {
    let result = decideOn halo |> Option.defaultValue "no opinion"
    result |> Expect.equal "defaultValue provides fallback" __
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `chronoTrigger.Score` — see Some 5
// 2. Alt+Enter `halo.Score` — see None
// 3. Try `Option.bind`, `Option.orElse`, `Option.toList`
// 4. Write a safe division: `safeDivide a b → int option`
// 5. Chain options: `tryParseInt "42" |> Option.map (fun n -> n * 2)`
// ============================================================
