// ============================================================
//  🧘  About Modules — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutModules.fs
//
//  Modules group types, values, and functions together.
//  They're like namespaces, but can also contain code.
//  `open Module` brings its members into scope.
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto

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
    Expect.equal MushroomKingdom.Mario.Name __ "Mario's name"
  }

  test "ModulesCanContainValues — Occupation" {
    Expect.equal MushroomKingdom.Mario.Occupation __ "Mario's job"
  }

  test "ModulesCanContainFunctions — powerUp" {
    Expect.equal superMario.Power __ "powered up Mario has a Mushroom"
  }

  test "OpenedModulesBringContentsInScope — Name" {
    Expect.equal Mario.Name __ "opened module: Mario's name"
  }

  test "OpenedModulesBringContentsInScope — Occupation" {
    Expect.equal Mario.Occupation __ "opened module: Mario's job"
  }

  test "OpenedModulesBringContentsInScope — Power" {
    Expect.equal Mario.Power __ "opened module: Mario starts with no power"
  }

  test "ModuleTypeIsAccessible" {
    // The Character type is defined inside MushroomKingdom
    Expect.equal (Mario.GetType()) typeof<MushroomKingdom.Character> "type from module"
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter `MushroomKingdom.Mario` — see the full record
// 2. Add a Luigi to MushroomKingdom module
// 3. Try `open MushroomKingdom` and then just write `Mario`
// 4. Notice List, Array, Seq, Map are all modules —
//    `List.map`, `Array.filter`, `Map.tryFind` all follow the same pattern
// ============================================================
