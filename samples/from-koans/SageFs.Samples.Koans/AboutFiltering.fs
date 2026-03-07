module SageFs.Samples.Koans.AboutFiltering

open Expecto
open Expecto.Flip

let someIfEven x =
  if x % 2 = 0 then Some x
  else None

let names = [ "Alice"; "Bob"; "Eve" ]
let numbers = [1; 2; 3]
let optNames = [ None; Some "Alice"; None ]
let optNames2 = [ None; Some "Alice"; None; Some "Bob" ]

let tests = testList "about filtering" [

  test "FilteringAList — starting with A" {
    let result = names |> List.filter (fun n -> n.StartsWith("A"))
    result |> Expect.equal "names starting with A" [ "Alice" ]
  }

  test "FilteringAList — starting with B" {
    let startsWithB (s: string) = s.StartsWith("B")
    let result = names |> List.filter startsWithB
    result |> Expect.equal "names starting with B" [ "Bob" ]
  }

  test "FindingJustOneItem" {
    let result = names |> List.find (fun n -> n = "Bob")
    result |> Expect.equal "find returns the element itself" "Bob"
  }

  test "FindingJustOneOrZeroItem — Eve exists" {
    let eve = names |> List.tryFind (fun n -> n = "Eve")
    eve.IsSome |> Expect.equal "Eve is in the list" true
  }

  test "FindingJustOneOrZeroItem — Zelda absent" {
    let zelda = names |> List.tryFind (fun n -> n = "Zelda")
    zelda.IsSome |> Expect.equal "Zelda is not in the list" false
  }

  test "ChoosingItemsFromAList — even numbers" {
    let result = numbers |> List.choose someIfEven
    result |> Expect.equal "only even numbers survive choose" [ 2 ]
  }

  test "ChoosingItemsFromAList — option list with id" {
    let result = optNames |> List.choose id
    result |> Expect.equal "choose id keeps the Somes" [ "Alice" ]
  }

  test "PickingFirstEvenFromRange" {
    let result = [5..10] |> List.pick someIfEven
    result |> Expect.equal "first even number ≥ 5" 6
  }

  test "PickingFirstSomeFromOptionList" {
    let result = optNames2 |> List.pick id
    result |> Expect.equal "first Some in the list" "Alice"
  }

]
