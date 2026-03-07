module SageFs.Samples.Koans.AboutLists

open Expecto
open Expecto.Flip

let fruits = ["apple"; "pear"; "grape"; "peach"]

let first = ["grape"; "peach"]
let second = "pear" :: first
let third = "apple" :: second

let base' = ["apple"; "pear"; "grape"]
let extended = base' @ ["peach"]

let square x = x * x
let isEven x = x % 2 = 0
let isOdd x = x % 2 <> 0

let tests = testList "about lists" [

  test "CreatingLists — Head" {
    fruits.Head |> Expect.equal "Head is the first element" "apple"
  }

  test "CreatingLists — Tail" {
    fruits.Tail |> Expect.equal "Tail is everything after Head" ["pear"; "grape"; "peach"]
  }

  test "CreatingLists — Length" {
    fruits.Length |> Expect.equal "Length counts the elements" 4
  }

  test "BuildingNewListsWithCons — second" {
    second |> Expect.equal "prepend pear to first" ["pear"; "grape"; "peach"]
  }

  test "BuildingNewListsWithCons — first unchanged" {
    first |> Expect.equal "first is unchanged" ["grape"; "peach"]
  }

  test "ConcatenatingLists — base unchanged" {
    base' |> Expect.equal "@ does not mutate base'" ["apple"; "pear"; "grape"]
  }

  test "ConcatenatingLists — extended" {
    extended |> Expect.equal "@ appends peach" ["apple"; "pear"; "grape"; "peach"]
  }

  test "CreatingListsWithRange — Head" {
    let list = [0..4]
    list.Head |> Expect.equal "first element of [0..4]" 0
  }

  test "CreatingListsWithRange — Tail" {
    let list = [0..4]
    list.Tail |> Expect.equal "tail of [0..4]" [1; 2; 3; 4]
  }

  test "CreatingListsWithComprehensions" {
    let list = [for i in 0..4 do yield i]
    list |> Expect.equal "comprehension 0..4" [0; 1; 2; 3; 4]
  }

  test "ComprehensionsWithConditions" {
    let evens = [for i in 0..10 do if i % 2 = 0 then yield i]
    evens |> Expect.equal "even numbers 0..10" [0; 2; 4; 6; 8; 10]
  }

  test "TransformingListsWithMap — original unchanged" {
    let original = [0..5]
    let _ = List.map square original
    original |> Expect.equal "map does not mutate original" [0; 1; 2; 3; 4; 5]
  }

  test "TransformingListsWithMap — result" {
    let result = List.map square [0..5]
    result |> Expect.equal "squares of 0..5" [0; 1; 4; 9; 16; 25]
  }

  test "FilteringListsWithFilter — original unchanged" {
    let original = [0..5]
    let _ = List.filter isEven original
    original |> Expect.equal "filter does not mutate original" [0; 1; 2; 3; 4; 5]
  }

  test "FilteringListsWithFilter — result" {
    let result = List.filter isEven [0..5]
    result |> Expect.equal "even numbers in 0..5" [0; 2; 4]
  }

  test "DividingListsWithPartition — odds" {
    let (odds, _) = List.partition isOdd [0..5]
    odds |> Expect.equal "odd numbers in 0..5" [1; 3; 5]
  }

  test "DividingListsWithPartition — evens" {
    let (_, evens) = List.partition isOdd [0..5]
    evens |> Expect.equal "even numbers from partition" [0; 2; 4]
  }

]
