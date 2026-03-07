module SageFs.Samples.Koans.AboutBranching

open Expecto
open Expecto.Flip

let isEven x =
  if x % 2 = 0 then "it's even!"
  else "it's odd!"

let isApple x =
  match x with
  | "apple" -> true
  | _ -> false

let getDinner x =
  match x with
  | (name, "veggies")
  | (name, "fish")
  | (name, "chicken") -> sprintf "%s doesn't want red meat" name
  | (name, food) -> sprintf "%s wants 'em some %s" name food

let tests = testList "about branching" [

  test "BasicBranching — even" {
    (isEven 2) |> Expect.equal "2 is even" "it's even!"
  }

  test "BasicBranching — odd" {
    (isEven 3) |> Expect.equal "3 is odd" "it's odd!"
  }

  test "IfStatementsReturnValues" {
    let result =
      if 2 = 3 then "something is REALLY wrong"
      else "no problem here"
    result |> Expect.equal "2 ≠ 3 so we get the else branch" "no problem here"
  }

  test "BranchingWithPatternMatch — apple" {
    (isApple "apple") |> Expect.equal "apple is an apple" true
  }

  test "BranchingWithPatternMatch — not apple" {
    (isApple "") |> Expect.equal "empty string is not an apple" false
  }

  test "TuplesWithIfStatementsGetClumsy" {
    let getDinnerClumsy x =
      let name, foodChoice = x
      if foodChoice = "veggies" || foodChoice = "fish" || foodChoice = "chicken" then
        sprintf "%s doesn't want red meat" name
      else
        sprintf "%s wants 'em some %s" name foodChoice

    (getDinnerClumsy ("Chris", "steak")) |> Expect.equal "Chris wants steak" "Chris wants 'em some steak"
    (getDinnerClumsy ("Dave", "veggies")) |> Expect.equal "Dave goes veggie" "Dave doesn't want red meat"
  }

  test "PatternMatchingIsNicer — fish" {
    (getDinner ("Bob", "fish")) |> Expect.equal "fish = no red meat" "Bob doesn't want red meat"
  }

  test "PatternMatchingIsNicer — Burger" {
    (getDinner ("Sally", "Burger")) |> Expect.equal "Sally gets a Burger" "Sally wants 'em some Burger"
  }

]
