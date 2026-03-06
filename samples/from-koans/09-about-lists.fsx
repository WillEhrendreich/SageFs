// ============================================================
//  🧘  About Lists — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutLists.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  Lists are F#'s primary immutable sequence type.
//  They're singly-linked and operations return NEW lists.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip
open System.Collections.Generic

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Exploring lists ───────────────────────────────────────────

let fruits = ["apple"; "pear"; "grape"; "peach"]

fruits.Head      // → "apple"     (first element)
fruits.Tail      // → ["pear"; "grape"; "peach"]  (all but first)
fruits.Length    // → 4

// Building lists with :: (cons) — prepend an element:
let first  = ["grape"; "peach"]
let second = "pear" :: first      // → ["pear"; "grape"; "peach"]
let third  = "apple" :: second    // → ["apple"; "pear"; "grape"; "peach"]

// Concatenating with @ — appends two lists:
let base'   = ["apple"; "pear"; "grape"]
let extended = base' @ ["peach"]   // → ["apple"; "pear"; "grape"; "peach"]

// Range syntax:
[0..4]         // → [0; 1; 2; 3; 4]
[0..2..10]     // → [0; 2; 4; 6; 8; 10]  (step 2)

// Comprehensions:
[for i in 0..4 do yield i]                           // → [0; 1; 2; 3; 4]
[for i in 0..10 do if i % 2 = 0 then yield i]       // → [0; 2; 4; 6; 8; 10]

// List.map — transform every element:
let square x = x * x
List.map square [0..5]      // → [0; 1; 4; 9; 16; 25]

// List.filter — keep elements matching a predicate:
let isEven x = x % 2 = 0
List.filter isEven [0..5]   // → [0; 2; 4]

// List.partition — split into two lists:
let isOdd x = x % 2 <> 0
List.partition isOdd [0..5]  // → ([1; 3; 5], [0; 2; 4])

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about lists" [

  test "CreatingLists — Head" {
    fruits.Head |> Expect.equal "Head is the first element" __
  }

  test "CreatingLists — Tail" {
    fruits.Tail |> Expect.equal "Tail is everything after Head" __
  }

  test "CreatingLists — Length" {
    fruits.Length |> Expect.equal "Length counts the elements" __
  }

  test "BuildingNewListsWithCons — second" {
    second |> Expect.equal "prepend pear to first" __
  }

  test "BuildingNewListsWithCons — first unchanged" {
    // Cons does NOT modify first — lists are immutable!
    first |> Expect.equal "first is unchanged" __
  }

  test "ConcatenatingLists — base unchanged" {
    base' |> Expect.equal "@ does not mutate base'" __
  }

  test "ConcatenatingLists — extended" {
    extended |> Expect.equal "@ appends peach" __
  }

  test "CreatingListsWithRange — Head" {
    let list = [0..4]
    list.Head |> Expect.equal "first element of [0..4]" __
  }

  test "CreatingListsWithRange — Tail" {
    let list = [0..4]
    list.Tail |> Expect.equal "tail of [0..4]" __
  }

  test "CreatingListsWithComprehensions" {
    let list = [for i in 0..4 do yield i]
    list |> Expect.equal "comprehension 0..4" __
  }

  test "ComprehensionsWithConditions" {
    let evens = [for i in 0..10 do if i % 2 = 0 then yield i]
    evens |> Expect.equal "even numbers 0..10" __
  }

  test "TransformingListsWithMap — original unchanged" {
    let original = [0..5]
    let _ = List.map square original
    original |> Expect.equal "map does not mutate original" __
  }

  test "TransformingListsWithMap — result" {
    let result = List.map square [0..5]
    result |> Expect.equal "squares of 0..5" __
  }

  test "FilteringListsWithFilter — original unchanged" {
    let original = [0..5]
    let _ = List.filter isEven original
    original |> Expect.equal "filter does not mutate original" __
  }

  test "FilteringListsWithFilter — result" {
    let result = List.filter isEven [0..5]
    result |> Expect.equal "even numbers in 0..5" __
  }

  test "DividingListsWithPartition — odds" {
    let (odds, _) = List.partition isOdd [0..5]
    odds |> Expect.equal "odd numbers in 0..5" __
  }

  test "DividingListsWithPartition — evens" {
    let (_, evens) = List.partition isOdd [0..5]
    evens |> Expect.equal "even numbers from partition" __
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `fruits.Head` → "apple"
// 2. Try `fruits.Head <- "mango"` — see the immutability error
// 3. Compare :: vs @ performance: :: is O(1), @ is O(n)
// 4. Explore: List.sum, List.fold, List.sortBy, List.groupBy
// ============================================================
