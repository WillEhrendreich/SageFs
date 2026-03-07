module SageFs.Samples.Koans.AboutOptionTypes

open Expecto
open Expecto.Flip

type Game = {
  Name: string
  Platform: string
  Score: int option
}

let chronoTrigger = { Name = "Chrono Trigger"; Platform = "SNES"; Score = Some 5 }
let halo = { Name = "Halo"; Platform = "Xbox"; Score = None }

let translate score =
  match score with
  | 5 -> "Great"  | 4 -> "Good"  | 3 -> "Decent"
  | 2 -> "Bad"    | 1 -> "Awful" | _ -> "Unknown"

let getScore game =
  match game.Score with
  | Some score -> translate score
  | None -> "Unknown"

let decideOn game =
  game.Score
  |> Option.map (fun score -> if score > 3 then "play it" else "don't play")

let tests = testList "about option types" [

  test "OptionTypesMightContainAValue — IsSome" {
    let v = Some 10
    v.IsSome |> Expect.equal "Some 10 is Some" true
  }

  test "OptionTypesMightContainAValue — IsNone" {
    let v = Some 10
    v.IsNone |> Expect.equal "Some 10 is not None" false
  }

  test "OptionTypesMightContainAValue — Value" {
    let v = Some 10
    v.Value |> Expect.equal "Value of Some 10" 10
  }

  test "NoneHasNoValue — IsSome" {
    let v: int option = None
    v.IsSome |> Expect.equal "None is not Some" false
  }

  test "NoneHasNoValue — IsNone" {
    let v: int option = None
    v.IsNone |> Expect.equal "None is None" true
  }

  test "NoneValueThrows" {
    let v: int option = None
    (fun () -> v.Value |> ignore) |> Expect.throws "accessing .Value on None throws"
  }

  test "UsingOptionWithPatternMatching — Chrono Trigger" {
    (getScore chronoTrigger) |> Expect.equal "Chrono Trigger got Great" "Great"
  }

  test "UsingOptionWithPatternMatching — Halo unscored" {
    (getScore halo) |> Expect.equal "Halo has no score" "Unknown"
  }

  test "ProjectingValuesFromOptions — Some result" {
    (decideOn chronoTrigger) |> Expect.equal "score > 3 → play it" (Some "play it")
  }

  test "ProjectingValuesFromOptions — None propagates" {
    (decideOn halo) |> Expect.equal "no score → None" None
  }

  test "DefaultValueHandlesNone" {
    let result = decideOn halo |> Option.defaultValue "no opinion"
    result |> Expect.equal "defaultValue provides fallback" "no opinion"
  }

]
