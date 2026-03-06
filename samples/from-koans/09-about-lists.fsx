// ============================================================
//  🧘  About Lists — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutLists.fs
//
//  Lists are F#'s primary immutable sequence type.
//  They're singly-linked and operations return NEW lists.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
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
    Expect.equal fruits.Head __ "Head is the first element"
  }

  test "CreatingLists — Tail" {
    Expect.equal fruits.Tail __ "Tail is everything after Head"
  }

  test "CreatingLists — Length" {
    Expect.equal fruits.Length __ "Length counts the elements"
  }

  test "BuildingNewListsWithCons — second" {
    Expect.equal second __ "prepend pear to first"
  }

  test "BuildingNewListsWithCons — first unchanged" {
    // Cons does NOT modify first — lists are immutable!
    Expect.equal first __ "first is unchanged"
  }

  test "ConcatenatingLists — base unchanged" {
    Expect.equal base' __ "@ does not mutate base'"
  }

  test "ConcatenatingLists — extended" {
    Expect.equal extended __ "@ appends peach"
  }

  test "CreatingListsWithRange — Head" {
    let list = [0..4]
    Expect.equal list.Head __ "first element of [0..4]"
  }

  test "CreatingListsWithRange — Tail" {
    let list = [0..4]
    Expect.equal list.Tail __ "tail of [0..4]"
  }

  test "CreatingListsWithComprehensions" {
    let list = [for i in 0..4 do yield i]
    Expect.equal list __ "comprehension 0..4"
  }

  test "ComprehensionsWithConditions" {
    let evens = [for i in 0..10 do if i % 2 = 0 then yield i]
    Expect.equal evens __ "even numbers 0..10"
  }

  test "TransformingListsWithMap — original unchanged" {
    let original = [0..5]
    let _ = List.map square original
    Expect.equal original __ "map does not mutate original"
  }

  test "TransformingListsWithMap — result" {
    let result = List.map square [0..5]
    Expect.equal result __ "squares of 0..5"
  }

  test "FilteringListsWithFilter — original unchanged" {
    let original = [0..5]
    let _ = List.filter isEven original
    Expect.equal original __ "filter does not mutate original"
  }

  test "FilteringListsWithFilter — result" {
    let result = List.filter isEven [0..5]
    Expect.equal result __ "even numbers in 0..5"
  }

  test "DividingListsWithPartition — odds" {
    let (odds, _) = List.partition isOdd [0..5]
    Expect.equal odds __ "odd numbers in 0..5"
  }

  test "DividingListsWithPartition — evens" {
    let (_, evens) = List.partition isOdd [0..5]
    Expect.equal evens __ "even numbers from partition"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `fruits.Head` → "apple"
// 2. Try `fruits.Head <- "mango"` — see the immutability error
// 3. Compare :: vs @ performance: :: is O(1), @ is O(n)
// 4. Explore: List.sum, List.fold, List.sortBy, List.groupBy
// ============================================================
