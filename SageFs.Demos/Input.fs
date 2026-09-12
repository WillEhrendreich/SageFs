/// Pure translation of one `Action`, already resolved to a `ScreenRect`, into
/// the ordered `X11Request` list that delivers it (demo-gif-plan.md §4.3,
/// §5). The interpreter of this list is the libXtst P/Invoke edge in
/// `DemoRuntime.XTest` — this module only ever produces data.
module SageFs.Demos.Input

open SageFs.Demos.Domain

/// The X11 requests that perform `action` at `target` (a click moves the
/// cursor there and presses/releases the button with a 60 ms hold; typing
/// clicks the position, then delivers `Cadence.keys`).
let plan (action: Action) (target: ScreenRect) : X11Request list =
  failwith "TODO: Input — Wave 2"
