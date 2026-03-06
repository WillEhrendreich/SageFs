// ============================================================
//  🧘  About Modules — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutModules.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  Modules group types, values, and functions together.
//  They're like namespaces, but can also contain code.
//  `open Module` brings its members into scope.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Defining a module ─────────────────────────────────────────
module MushroomKingdom =

  type Power = Mushroom | Star | FireFlower

  type Character = {
    Name:       string
    Occupation: string
    Power:      Power option
  }

  let Mario = { Name = "Mario"; Occupation = "Plumber"; Power = None }

  let powerUp character =
    { character with Power = Some Mushroom }

// ── Accessing module members ──────────────────────────────────
MushroomKingdom.Mario.Name         // → "Mario"
MushroomKingdom.Mario.Occupation   // → "Plumber"
MushroomKingdom.Mario.Power        // → None

// Power up Mario:
let superMario = MushroomKingdom.powerUp MushroomKingdom.Mario
superMario.Power   // → Some Mushroom

// ── Opening a module ──────────────────────────────────────────
open MushroomKingdom    // bring everything into scope

Mario.Name           // → "Mario"  (no MushroomKingdom. prefix needed)
Mario.Occupation     // → "Plumber"
Mario.Power          // → None

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about modules" [

  test "ModulesCanContainValues — Name" {
    MushroomKingdom.Mario.Name |> Expect.equal "Mario's name" __
  }

  test "ModulesCanContainValues — Occupation" {
    MushroomKingdom.Mario.Occupation |> Expect.equal "Mario's job" __
  }

  test "ModulesCanContainFunctions — powerUp" {
    superMario.Power |> Expect.equal "powered up Mario has a Mushroom" __
  }

  test "OpenedModulesBringContentsInScope — Name" {
    Mario.Name |> Expect.equal "opened module: Mario's name" __
  }

  test "OpenedModulesBringContentsInScope — Occupation" {
    Mario.Occupation |> Expect.equal "opened module: Mario's job" __
  }

  test "OpenedModulesBringContentsInScope — Power" {
    Mario.Power |> Expect.equal "opened module: Mario starts with no power" __
  }

  test "ModuleTypeIsAccessible" {
    // The Character type is defined inside MushroomKingdom
    (Mario.GetType()) |> Expect.equal "type from module" typeof<MushroomKingdom.Character>
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `MushroomKingdom.Mario` — see the full record
// 2. Add a Luigi to MushroomKingdom module
// 3. Try `open MushroomKingdom` and then just write `Mario`
// 4. Notice List, Array, Seq, Map are all modules —
//    `List.map`, `Array.filter`, `Map.tryFind` all follow the same pattern
// ============================================================
