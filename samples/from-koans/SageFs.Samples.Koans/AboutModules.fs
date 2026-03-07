module SageFs.Samples.Koans.AboutModules

open Expecto
open Expecto.Flip

module MushroomKingdom =

  type Power = Mushroom | Star | FireFlower

  type Character = {
    Name: string
    Occupation: string
    Power: Power option
  }

  let Mario = { Name = "Mario"; Occupation = "Plumber"; Power = None }

  let powerUp character =
    { character with Power = Some Mushroom }

open MushroomKingdom

let superMario = MushroomKingdom.powerUp MushroomKingdom.Mario

let tests = testList "about modules" [

  test "ModulesCanContainValues — Name" {
    MushroomKingdom.Mario.Name |> Expect.equal "Mario's name" "Mario"
  }

  test "ModulesCanContainValues — Occupation" {
    MushroomKingdom.Mario.Occupation |> Expect.equal "Mario's job" "Plumber"
  }

  test "ModulesCanContainFunctions — powerUp" {
    superMario.Power |> Expect.equal "powered up Mario has a Mushroom" (Some Mushroom)
  }

  test "OpenedModulesBringContentsInScope — Name" {
    Mario.Name |> Expect.equal "opened module: Mario's name" "Mario"
  }

  test "OpenedModulesBringContentsInScope — Occupation" {
    Mario.Occupation |> Expect.equal "opened module: Mario's job" "Plumber"
  }

  test "OpenedModulesBringContentsInScope — Power" {
    Mario.Power |> Expect.equal "opened module: Mario starts with no power" None
  }

  test "ModuleTypeIsAccessible" {
    (Mario.GetType()) |> Expect.equal "type from module" typeof<MushroomKingdom.Character>
  }

]
