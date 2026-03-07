module SageFs.Samples.Koans.AboutRecordTypes

open Expecto
open Expecto.Flip

type Character = {
  Name: string
  Occupation: string
}

let mario = { Name = "Mario"; Occupation = "Plumber" }
let luigi = { mario with Name = "Luigi" }

let greenKoopa = { Name = "Koopa"; Occupation = "Soldier" }
let redKoopa = { Name = "Koopa"; Occupation = "Soldier" }
let bowser = { Name = "Bowser"; Occupation = "Kidnapper" }

let determineSide character =
  match character with
  | { Occupation = "Plumber" } -> "good guy"
  | _ -> "bad guy"

let tests = testList "about record types" [

  test "RecordsHaveProperties — Name" {
    mario.Name |> Expect.equal "mario's name" "Mario"
  }

  test "RecordsHaveProperties — Occupation" {
    mario.Occupation |> Expect.equal "mario's occupation" "Plumber"
  }

  test "CreatingFromExistingRecord — mario name unchanged" {
    mario.Name |> Expect.equal "with update does not change original" "Mario"
  }

  test "CreatingFromExistingRecord — luigi name" {
    luigi.Name |> Expect.equal "luigi gets a new name" "Luigi"
  }

  test "CreatingFromExistingRecord — luigi inherits occupation" {
    luigi.Occupation |> Expect.equal "luigi inherits mario's occupation" "Plumber"
  }

  test "ComparingRecords — same values are equal" {
    let koopaComparison =
      if greenKoopa = redKoopa then "all the koopas are pretty much the same"
      else "maybe one can fly"
    koopaComparison |> Expect.equal "koopas with same fields are equal" "all the koopas are pretty much the same"
  }

  test "ComparingRecords — different values are not equal" {
    let bowserComparison =
      if bowser = greenKoopa then "the king is a pawn"
      else "he is still kind of a koopa"
    bowserComparison |> Expect.equal "bowser ≠ koopa" "he is still kind of a koopa"
  }

  test "PatternMatchOnRecords — mario is good guy" {
    (determineSide mario) |> Expect.equal "plumbers are good guys" "good guy"
  }

  test "PatternMatchOnRecords — luigi is good guy" {
    (determineSide luigi) |> Expect.equal "luigi is also a plumber" "good guy"
  }

  test "PatternMatchOnRecords — bowser is bad guy" {
    (determineSide bowser) |> Expect.equal "kidnapper = bad guy" "bad guy"
  }

]
