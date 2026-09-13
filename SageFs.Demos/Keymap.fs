/// Pure key → keysym → keycode resolution (demo-gif-plan.md §4.3, §5),
/// against a `KeyboardMapping` fetched live from the cell's X server —
/// resolution never hard-codes a layout. Chords expand to ordered
/// press/release sequences. Wave 2 tests: ASCII round-trip; chords/modifiers
/// resolve in order.
module SageFs.Demos.Keymap

open SageFs.Demos.Domain

// X11 keysym values (X11/keysymdef.h). Printable Latin-1 characters map to
// their own character code by X11 convention; the rest are the standard
// named keysyms for the `Key` cases that aren't `Char`.
[<Literal>]
let private KeysymReturn = 0xff0d

[<Literal>]
let private KeysymEscape = 0xff1b

[<Literal>]
let private KeysymTab = 0xff09

[<Literal>]
let private KeysymBackspace = 0xff08

[<Literal>]
let private KeysymLeft = 0xff51

[<Literal>]
let private KeysymUp = 0xff52

[<Literal>]
let private KeysymRight = 0xff53

[<Literal>]
let private KeysymDown = 0xff54

[<Literal>]
let private KeysymShiftL = 0xffe1

[<Literal>]
let private KeysymControlL = 0xffe3

[<Literal>]
let private KeysymAltL = 0xffe9

[<Literal>]
let private KeysymF1 = 0xffbe

/// `key`'s own single target keysym, by X11 convention: printable Latin-1
/// characters map to their own Unicode code point AS a keysym, uppercase
/// and lowercase letters included — `A` (0x41) and `a` (0x61) are two
/// DISTINCT keysyms that a real keyboard mapping puts on the SAME physical
/// key (level 0 = `a`, level 1 = `A`). This function never decides whether
/// Shift is needed — that is `mapping.ShiftedKeysyms`'s job in `resolve`,
/// because a live X server (not `Char.IsUpper`) is the only thing that
/// actually knows which symbols share a key with which.
let private baseKeysymFor (key: Key) : int =
  match key with
  | Key.Return -> KeysymReturn
  | Key.Escape -> KeysymEscape
  | Key.Tab -> KeysymTab
  | Key.Backspace -> KeysymBackspace
  | Key.Left -> KeysymLeft
  | Key.Right -> KeysymRight
  | Key.Up -> KeysymUp
  | Key.Down -> KeysymDown
  | Key.Ctrl -> KeysymControlL
  | Key.Shift -> KeysymShiftL
  | Key.Alt -> KeysymAltL
  | Key.F n -> KeysymF1 + (n - 1)
  | Key.Char c -> int c

/// Resolves one `Key` to the keycode(s) needed to actually produce it under
/// the given live server mapping: just its own keycode if reachable
/// unshifted, or Shift's keycode (itself resolved through `mapping`, never
/// hard-coded to a fixed value) followed by the base keycode when
/// `mapping.ShiftedKeysyms` says this keysym lives behind Shift — the one
/// mechanism that covers BOTH an uppercase letter (`A` shares `a`'s key) AND
/// a shifted punctuation symbol (`|` shares `\`'s key, `>` shares `.`'s
/// key), instead of the old `Char.IsUpper`-only special case that had no way
/// to see shifted punctuation at all (§9's root cause: `[1..10] |> List.sum`
/// typed with no shift for `|`/`>` produced whatever those keys' UNSHIFTED
/// symbols were). A keysym absent from `mapping` (the live server has no key
/// for it, or Shift's own keycode is unknown) is dropped rather than
/// silently producing the WRONG unshifted character — the caller receives
/// only what the current layout can actually, correctly produce.
let resolve (mapping: KeyboardMapping) (key: Key) : KeyCode list =
  let keysym = baseKeysymFor key
  match mapping.KeysymToKeycode |> Map.tryFind keysym with
  | None -> []
  | Some keycode ->
    if mapping.ShiftedKeysyms |> Set.contains keysym then
      match mapping.KeysymToKeycode |> Map.tryFind KeysymShiftL with
      | Some shiftKeycode -> [ KeyCode shiftKeycode; KeyCode keycode ]
      | None -> []
    else
      [ KeyCode keycode ]
