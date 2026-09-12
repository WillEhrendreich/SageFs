/// Tests for `Keymap.resolve` (demo-gif-plan.md §4.3, §5): ASCII round-trip
/// and capitals holding Shift, against a fixture `KeyboardMapping`.
module SageFs.Demos.Tests.KeymapTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain

let private keysymShiftL = 0xffe1
let private keysymControlL = 0xffe3
let private keysymReturn = 0xff0d
let private keysymF1 = 0xffbe

/// A fixture mapping: every printable ASCII codepoint (and the handful of
/// named keysyms the `Key` DU covers) maps identity-style to a keycode one
/// higher, so a test can tell "this is a keycode for keysym K" without the
/// mapping being a no-op.
let private fixtureMapping : KeyboardMapping =
  let asciiPairs = [ for cp in 32 .. 126 -> cp, cp + 1 ]
  let namedPairs =
    [ keysymShiftL, keysymShiftL + 1
      keysymControlL, keysymControlL + 1
      keysymReturn, keysymReturn + 1
      keysymF1, keysymF1 + 1 ]
  { KeysymToKeycode = (asciiPairs @ namedPairs) |> Map.ofList }

let private keycodeToKeysym (KeyCode kc) : int = kc - 1

[<Tests>]
let tests =
  testList "Keymap" [

    testCase "a lowercase letter round-trips through a single keycode" <| fun _ ->
      let keycodes = SageFs.Demos.Keymap.resolve fixtureMapping (Key.Char 'a')
      keycodes |> Expect.equal "one keycode for a plain lowercase letter" [ KeyCode(int 'a' + 1) ]

    testCase "an ASCII round-trip holds for every printable char 32..126" <| fun _ ->
      for cp in 32 .. 126 do
        let c = char cp
        if not (System.Char.IsUpper c) then
          let keycodes = SageFs.Demos.Keymap.resolve fixtureMapping (Key.Char c)
          match keycodes with
          | [ kc ] -> keycodeToKeysym kc |> Expect.equal (sprintf "keysym round-trips for '%c'" c) cp
          | other -> failwithf "expected exactly one keycode for '%c', got %A" c other

    testCase "a capital letter holds Shift (§4.3)" <| fun _ ->
      let keycodes = SageFs.Demos.Keymap.resolve fixtureMapping (Key.Char 'A')
      match keycodes with
      | [ shiftCode; letterCode ] ->
        keycodeToKeysym shiftCode |> Expect.equal "first keycode is Shift" keysymShiftL
        keycodeToKeysym letterCode |> Expect.equal "second keycode is the base (lowercase) letter" (int 'a')
      | other -> failwithf "expected [shift; letter], got %A" other

    testCase "every uppercase letter resolves to exactly two keycodes" <| fun _ ->
      for c in 'A' .. 'Z' do
        SageFs.Demos.Keymap.resolve fixtureMapping (Key.Char c)
        |> List.length
        |> Expect.equal (sprintf "Shift + base letter for '%c'" c) 2

    testCase "named keys resolve against the mapping, not by guessing" <| fun _ ->
      SageFs.Demos.Keymap.resolve fixtureMapping Key.Ctrl
      |> Expect.equal "Ctrl resolves to Control_L's keycode" [ KeyCode(keysymControlL + 1) ]
      SageFs.Demos.Keymap.resolve fixtureMapping Key.Return
      |> Expect.equal "Return resolves to Return's keycode" [ KeyCode(keysymReturn + 1) ]
      SageFs.Demos.Keymap.resolve fixtureMapping (Key.F 1)
      |> Expect.equal "F1 resolves to F1's keycode" [ KeyCode(keysymF1 + 1) ]

    testCase "a keysym missing from the live mapping is dropped, not thrown" <| fun _ ->
      let emptyMapping: KeyboardMapping = { KeysymToKeycode = Map.empty }
      SageFs.Demos.Keymap.resolve emptyMapping (Key.Char 'a')
      |> Expect.isEmpty "no keycode available under an empty mapping"
  ]
