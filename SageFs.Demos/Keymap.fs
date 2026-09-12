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

/// The ordered keysym(s) needed to produce `key` — more than one only when a
/// modifier must be held alongside a base keysym (an uppercase letter holds
/// Shift; §4.3 "capitals hold Shift").
let private keysymsFor (key: Key) : int list =
  match key with
  | Key.Return -> [ KeysymReturn ]
  | Key.Escape -> [ KeysymEscape ]
  | Key.Tab -> [ KeysymTab ]
  | Key.Backspace -> [ KeysymBackspace ]
  | Key.Left -> [ KeysymLeft ]
  | Key.Right -> [ KeysymRight ]
  | Key.Up -> [ KeysymUp ]
  | Key.Down -> [ KeysymDown ]
  | Key.Ctrl -> [ KeysymControlL ]
  | Key.Shift -> [ KeysymShiftL ]
  | Key.Alt -> [ KeysymAltL ]
  | Key.F n -> [ KeysymF1 + (n - 1) ]
  | Key.Char c when System.Char.IsUpper c -> [ KeysymShiftL; int (System.Char.ToLower c) ]
  | Key.Char c -> [ int c ]

/// Resolves one `Key` to the keycode(s) needed to produce it under the given
/// live server mapping (a chord key may need more than one keycode, e.g. a
/// shifted character). A keysym absent from `mapping` (the live server has
/// no key for it) is dropped rather than raised — the caller receives
/// whatever the current layout can actually produce.
let resolve (mapping: KeyboardMapping) (key: Key) : KeyCode list =
  keysymsFor key
  |> List.choose (fun keysym -> mapping.KeysymToKeycode |> Map.tryFind keysym |> Option.map KeyCode)
