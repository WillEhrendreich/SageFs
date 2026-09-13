/// Tests for `Keymap.resolve` (demo-gif-plan.md §4.3, §5): ASCII round-trip,
/// capitals holding Shift, and — the §9 fix — shifted PUNCTUATION holding
/// Shift too, against a fixture `KeyboardMapping` shaped like a real X11
/// layout (one physical key carries an unshifted AND a shifted symbol,
/// never one keycode per printable character).
module SageFs.Demos.Tests.KeymapTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain

let private keysymShiftL = 0xffe1
let private keysymControlL = 0xffe3
let private keysymReturn = 0xff0d
let private keysymF1 = 0xffbe

/// A fixture mapping shaped like a real X11 keyboard layout, not the old
/// "every printable char gets its own dedicated keycode" fiction (§9's
/// actual root cause on the live display — see `KeyboardMapping`'s doc):
/// every unshifted printable ASCII codepoint (including lowercase letters
/// and unshifted punctuation) gets its own keycode (`cp + 1`, so a test can
/// tell "this is the keycode for keysym K" without the mapping being a
/// no-op); every UPPERCASE letter shares its lowercase sibling's keycode and
/// is listed in `ShiftedKeysyms` (`A` lives on `a`'s key); and a handful of
/// shifted punctuation symbols (`!`, `>`, `|`) share an unshifted sibling's
/// keycode the same way (`!`/`1`, `>`/`.`, `|`/`\`) — exactly the "one key,
/// two levels" shape `XTest.keyboardMapping` fetches live.
let private fixtureMapping : KeyboardMapping =
  let unshiftedPairs =
    [ for cp in 32 .. 126 do
        if not (System.Char.IsUpper(char cp)) then
          yield cp, cp + 1 ]
  let upperPairs = [ for c in 'A' .. 'Z' -> int c, int (System.Char.ToLower c) + 1 ]
  let shiftedPunctuationPairs = [ int '!', int '1' + 1; int '>', int '.' + 1; int '|', int '\\' + 1 ]
  let namedPairs =
    [ keysymShiftL, keysymShiftL + 1
      keysymControlL, keysymControlL + 1
      keysymReturn, keysymReturn + 1
      keysymF1, keysymF1 + 1 ]
  { KeysymToKeycode = (unshiftedPairs @ upperPairs @ shiftedPunctuationPairs @ namedPairs) |> Map.ofList
    ShiftedKeysyms = (upperPairs @ shiftedPunctuationPairs) |> List.map fst |> Set.ofList }

let private keycodeToKeysym (KeyCode kc) : int = kc - 1

[<Tests>]
let tests =
  testList "Keymap" [

    testCase "a lowercase letter round-trips through a single keycode" <| fun _ ->
      let keycodes = SageFs.Demos.Keymap.resolve fixtureMapping (Key.Char 'a')
      keycodes |> Expect.equal "one keycode for a plain lowercase letter" [ KeyCode(int 'a' + 1) ]

    testCase "an ASCII round-trip holds for every printable, unshifted char 32..126" <| fun _ ->
      for cp in 32 .. 126 do
        if not (fixtureMapping.ShiftedKeysyms |> Set.contains cp) then
          let c = char cp
          let keycodes = SageFs.Demos.Keymap.resolve fixtureMapping (Key.Char c)
          match keycodes with
          | [ kc ] -> keycodeToKeysym kc |> Expect.equal (sprintf "keysym round-trips for '%c'" c) cp
          | other -> failwithf "expected exactly one keycode for '%c', got %A" c other

    testCase "a capital letter holds Shift (§4.3)" <| fun _ ->
      let keycodes = SageFs.Demos.Keymap.resolve fixtureMapping (Key.Char 'A')
      match keycodes with
      | [ shiftCode; letterCode ] ->
        keycodeToKeysym shiftCode |> Expect.equal "first keycode is Shift" keysymShiftL
        keycodeToKeysym letterCode |> Expect.equal "second keycode is the base (lowercase) letter's" (int 'a')
      | other -> failwithf "expected [shift; letter], got %A" other

    testCase "every uppercase letter resolves to exactly two keycodes" <| fun _ ->
      for c in 'A' .. 'Z' do
        SageFs.Demos.Keymap.resolve fixtureMapping (Key.Char c)
        |> List.length
        |> Expect.equal (sprintf "Shift + base letter for '%c'" c) 2

    testCase "a shifted punctuation symbol holds Shift, exactly like a capital letter (§9 root-cause fix: '|' and '>' need Shift too, not just letters)" <| fun _ ->
      let checkShifted (c: char) (unshiftedSibling: char) =
        match SageFs.Demos.Keymap.resolve fixtureMapping (Key.Char c) with
        | [ shiftCode; baseCode ] ->
          keycodeToKeysym shiftCode |> Expect.equal (sprintf "'%c' needs Shift held" c) keysymShiftL
          keycodeToKeysym baseCode |> Expect.equal (sprintf "'%c' shares its unshifted sibling's key" c) (int unshiftedSibling)
        | other -> failwithf "expected [shift; base] for '%c', got %A" c other

      checkShifted '!' '1'
      checkShifted '>' '.'
      checkShifted '|' '\\'

    testCase "named keys resolve against the mapping, not by guessing" <| fun _ ->
      SageFs.Demos.Keymap.resolve fixtureMapping Key.Ctrl
      |> Expect.equal "Ctrl resolves to Control_L's keycode" [ KeyCode(keysymControlL + 1) ]
      SageFs.Demos.Keymap.resolve fixtureMapping Key.Return
      |> Expect.equal "Return resolves to Return's keycode" [ KeyCode(keysymReturn + 1) ]
      SageFs.Demos.Keymap.resolve fixtureMapping (Key.F 1)
      |> Expect.equal "F1 resolves to F1's keycode" [ KeyCode(keysymF1 + 1) ]

    testCase "a keysym missing from the live mapping is dropped, not thrown" <| fun _ ->
      let emptyMapping: KeyboardMapping = { KeysymToKeycode = Map.empty; ShiftedKeysyms = Set.empty }
      SageFs.Demos.Keymap.resolve emptyMapping (Key.Char 'a')
      |> Expect.isEmpty "no keycode available under an empty mapping"

    testCase "a shifted keysym whose Shift key itself is unmapped is dropped whole, never sent unshifted (never silently produce the WRONG character)" <| fun _ ->
      let noShiftMapping: KeyboardMapping =
        { KeysymToKeycode = Map.ofList [ int 'a', int 'a' + 1; int 'A', int 'a' + 1 ]
          ShiftedKeysyms = Set.ofList [ int 'A' ] }
      SageFs.Demos.Keymap.resolve noShiftMapping (Key.Char 'A')
      |> Expect.isEmpty "dropped rather than sent as the unshifted 'a'"
  ]
