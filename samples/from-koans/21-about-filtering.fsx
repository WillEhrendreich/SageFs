// ============================================================
//  🧘  About Filtering — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutFiltering.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  Lists can be filtered in several ways:
//    filter  — keep elements matching a predicate
//    find    — first match or exception
//    tryFind — first match or None
//    choose  — transform + filter in one step (None gets dropped)
//    pick    — like choose but returns first result or exception
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Helper used throughout ────────────────────────────────────
// Returns Some x if x is even, None if odd:
let someIfEven x =
  if x % 2 = 0 then Some x
  else None

// ── Exploring list filtering functions ───────────────────────

let names = [ "Alice"; "Bob"; "Eve" ]

// filter — keep matching elements:
names |> List.filter (fun n -> n.StartsWith("A"))   // → ["Alice"]
names |> List.filter (fun n -> n.StartsWith("B"))   // → ["Bob"]

// find — first match (throws if no match!):
names |> List.find (fun n -> n = "Bob")             // → "Bob"

// tryFind — first match or None (safe):
names |> List.tryFind (fun n -> n = "Eve")          // → Some "Eve"
names |> List.tryFind (fun n -> n = "Zelda")        // → None

// choose — apply f to each element, keep the Somes:
let numbers = [1; 2; 3]
numbers |> List.choose someIfEven                   // → [2]

// choose with `id` on option lists — keep the Somes:
let optNames = [ None; Some "Alice"; None ]
optNames |> List.choose id                          // → ["Alice"]

// pick — like choose but returns the FIRST result (throws if none):
[5..10] |> List.pick someIfEven                     // → 6  (first even ≥ 5)

let optNames2 = [ None; Some "Alice"; None; Some "Bob" ]
optNames2 |> List.pick id                           // → "Alice"  (first Some)

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about filtering" [

  test "FilteringAList — starting with A" {
    let result = names |> List.filter (fun n -> n.StartsWith("A"))
    result |> Expect.equal "names starting with A" [ __ ]
  }

  test "FilteringAList — starting with B" {
    let startsWithB (s: string) = s.StartsWith("B")
    let result = names |> List.filter startsWithB
    result |> Expect.equal "names starting with B" [ __ ]
  }

  test "FindingJustOneItem" {
    let result = names |> List.find (fun n -> n = "Bob")
    result |> Expect.equal "find returns the element itself" __
  }

  test "FindingJustOneOrZeroItem — Eve exists" {
    let eve = names |> List.tryFind (fun n -> n = "Eve")
    eve.IsSome |> Expect.equal "Eve is in the list" __
  }

  test "FindingJustOneOrZeroItem — Zelda absent" {
    let zelda = names |> List.tryFind (fun n -> n = "Zelda")
    zelda.IsSome |> Expect.equal "Zelda is not in the list" __
  }

  test "ChoosingItemsFromAList — even numbers" {
    let result = numbers |> List.choose someIfEven
    result |> Expect.equal "only even numbers survive choose" [ __ ]
  }

  test "ChoosingItemsFromAList — option list with id" {
    let result = optNames |> List.choose id
    result |> Expect.equal "choose id keeps the Somes" [ __ ]
  }

  test "PickingFirstEvenFromRange" {
    let result = [5..10] |> List.pick someIfEven
    result |> Expect.equal "first even number ≥ 5" __
  }

  test "PickingFirstSomeFromOptionList" {
    let result = optNames2 |> List.pick id
    result |> Expect.equal "first Some in the list" __
  }

]

// ── Cheat sheet: when to use which ───────────────────────────
// filter   → keep ALL matching elements       → returns list
// find     → first match (exception if none)  → returns element
// tryFind  → first match (None if none)       → returns option
// choose   → transform + filter, keep Somes   → returns list
// pick     → first Some (exception if none)   → returns element
// tryPick  → first Some (None if none)        → returns option
//
// Things to try:
// 1. Alt+Enter `names |> List.filter (fun n -> n.StartsWith("A"))`
// 2. Try `List.find` with a name that doesn't exist — see the exception
// 3. Write a `tryParseInt` using `System.Int32.TryParse` and `choose` it
//    over a list of strings to extract all parseable integers
// ============================================================
