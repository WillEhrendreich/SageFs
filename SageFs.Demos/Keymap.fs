/// Pure key → keysym → keycode resolution (demo-gif-plan.md §4.3, §5),
/// against a `KeyboardMapping` fetched live from the cell's X server —
/// resolution never hard-codes a layout. Chords expand to ordered
/// press/release sequences. Wave 2 tests: ASCII round-trip; chords/modifiers
/// resolve in order.
module SageFs.Demos.Keymap

open SageFs.Demos.Domain

/// Resolves one `Key` to the keycode(s) needed to produce it under the given
/// live server mapping (a chord key may need more than one keycode, e.g. a
/// shifted character).
let resolve (mapping: KeyboardMapping) (key: Key) : KeyCode list =
  failwith "TODO: Keymap — Wave 2"
