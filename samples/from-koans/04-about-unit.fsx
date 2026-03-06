// ============================================================
//  🧘  About Unit — SageFs Edition
//
//  Original: ChrisMarinos/FSharpKoans — AboutUnit.fs
//  Adapted from FSharpKoans by Chris Marinos (MIT). See LICENSE-FSharpKoans.
//
//  The unit type is F#'s equivalent of void, but unit is a real
//  type with exactly one value: ()
//
//  Fill in each __ to turn 🔴 tests 🟢. Save to see results.
// ============================================================

#r "nuget: Expecto"
open Expecto
open Expecto.Flip

let inline __<'T> : 'T = failwith "Seek wisdom by filling in the __"

// ── Exploring unit ────────────────────────────────────────────
// Functions that produce side effects and return "nothing"
// actually return unit ().

let sendData (data: string) =
  // imagine sending data to a server...
  ()    // () is the unit value — it IS the return value

// Alt+Enter these:
let result = sendData "data"   // → ()   (unit value)
result.GetType()               // → Microsoft.FSharp.Core.Unit

// Parameterless functions take unit as their argument:
let sayHello () =
  "hello"

sayHello ()     // → "hello"

// ── Tests ─────────────────────────────────────────────────────

let tests = testList "about unit" [

  test "UnitIsUsedWhenThereIsNoReturnValue" {
    let r = sendData "data"
    r |> Expect.equal "sendData returns unit" __
    // Hint: what is the only unit value?
  }

  test "ParameterlessFunctionsTakeUnit" {
    let r = sayHello ()
    r |> Expect.equal "sayHello should return 'hello'" __
  }

  test "UnitIsAType" {
    let r = sendData "data"
    (r.GetType()) |> Expect.equal "should be typeof<unit>" typeof<unit>
  }

]

// ── Things to try ─────────────────────────────────────────────
// 1. Alt+Enter on `result` — see ()
// 2. In F#, `printfn "hello"` returns unit — try it
// 3. A function that only has side effects should return unit
// ============================================================
