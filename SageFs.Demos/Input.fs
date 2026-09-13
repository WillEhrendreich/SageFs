/// Pure translation of one `Action`, already resolved to a `ScreenRect`, into
/// the ordered `X11Request` list that delivers it (demo-gif-plan.md §4.3,
/// §5). The interpreter of this list is the libXtst P/Invoke edge in
/// `DemoRuntime.XTest` — this module only ever produces data.
module SageFs.Demos.Input

open SageFs.Demos.Domain

/// §9: "click ripple ... over 250 ms"; the plan's `FakeButton` hold itself
/// (§4.3: "60 ms hold") has no timing field to carry in `X11Request` (only
/// `PointerFrame` carries `TMs`) — the interpreter is responsible for
/// pacing the down/up pair by that amount when it delivers this list.
[<Literal>]
let private ClickHoldMs = 60

/// TODO(shape): `Input.plan`'s signature (§5) carries no "where is the
/// cursor right now" — threading the previous step's end position through a
/// scenario is a cell-agent/session concern for Wave 2. Until then, every
/// planned motion starts from this fixed assumed rest position, so the
/// layer is exercised and callable end to end.
let private restPosition: Point = { X = 0; Y = 0 }

/// A small, non-cryptographic, process-stable combine — deliberately not
/// `HashCode.Combine`/`string.GetHashCode`, both of which are randomized per
/// process in modern .NET and would break "identical every run" (§1, §9).
let private combineHash (acc: int) (value: int) : int = (acc * 397) ^^^ value

let private seedFromRect (rect: ScreenRect) : Seed =
  [ rect.X; rect.Y; rect.W; rect.H ] |> List.fold combineHash 17 |> uint32 |> uint64 |> Seed

/// FNV-1a over UTF-8 bytes — stable across processes, unlike the default
/// string hash (§1, §9: seeds must be identical every run).
let private fnv1a (text: string) : uint64 =
  let offsetBasis = 14695981039346656037UL
  let prime = 1099511628211UL
  (offsetBasis, System.Text.Encoding.UTF8.GetBytes text)
  ||> Array.fold (fun hash b -> (hash ^^^ uint64 b) * prime)

let private seedOfCadenceSeed (cadenceSeed: CadenceSeed) : Seed = CadenceSeed.value cadenceSeed |> fnv1a |> Seed

let private rectCentre (rect: ScreenRect) : Point =
  { X = rect.X + rect.W / 2
    Y = rect.Y + rect.H / 2 }

let private motionRequests (start: Point) (finish: Point) (seed: Seed) : X11Request list =
  Motion.path start finish seed |> List.map (fun frame -> X11Request.FakeMotion(frame.At.X, frame.At.Y))

/// Move to `target`'s centre, then press and release the left button
/// (§4.3: "Click target = Motion.path to rect centre then FakeButton
/// down/up (60 ms hold)").
let private clickRequests (target: ScreenRect) : X11Request list =
  let centre = rectCentre target
  let seed = seedFromRect target
  motionRequests restPosition centre seed
  @ [ X11Request.FakeButton(Button.Left, Pressed.Down); X11Request.FakeButton(Button.Left, Pressed.Up) ]

/// Like `clickRequests`, but starting the motion from an explicit prior
/// cursor position instead of the fixed rest position — used by the
/// cell-agent to chain a second click after a `TypeThenClick` step's typing
/// (e.g. click the `[EVAL]` button right after typing the expression) so
/// the cursor moves on continuously instead of resetting to the rest point.
let clickFrom (start: Point) (target: ScreenRect) : X11Request list =
  let centre = rectCentre target
  let seed = seedFromRect target
  motionRequests start centre seed
  @ [ X11Request.FakeButton(Button.Left, Pressed.Down); X11Request.FakeButton(Button.Left, Pressed.Up) ]

/// Presses every keycode down in the given order, then releases them in
/// reverse — a modifier (e.g. Shift, resolved ahead of its base key by
/// `Keymap.resolve`) stays HELD across every key that follows it, exactly
/// what producing a shifted character or a chord requires. Shared by single
/// characters and multi-key chords alike: a single keysym needing Shift
/// (`Keymap.resolve`'s `[shift; base]`) IS a two-key chord, and the old
/// per-keycode "down immediately followed by up" shape here (fixed by this
/// change) released Shift before the base key was even pressed — one of
/// §9's two eval-text-corruption bugs (the other was the fictitious
/// `identityMapping` this function's `mapping` parameter replaces).
let private pressHoldingRequests (keycodes: KeyCode list) : X11Request list =
  let downs = keycodes |> List.map (fun kc -> X11Request.FakeKey(kc, Pressed.Down))
  let ups = keycodes |> List.rev |> List.map (fun kc -> X11Request.FakeKey(kc, Pressed.Up))
  downs @ ups

let private keyTapRequests (mapping: KeyboardMapping) (key: Key) : X11Request list =
  Keymap.resolve mapping key |> pressHoldingRequests

let private typingRequests (mapping: KeyboardMapping) (text: Text) (seed: Seed) : X11Request list =
  Cadence.keys text seed |> List.collect (fun (key, _delay) -> keyTapRequests mapping key)

/// A chord/shortcut: every key pressed down in the given order (so earlier
/// keys — the modifiers — are already held by the time the last one goes
/// down), then released in reverse order (§4.3: "chords ... expand to
/// ordered press/release sequences").
let private chordRequests (mapping: KeyboardMapping) (keys: Key list) : X11Request list =
  keys |> List.collect (Keymap.resolve mapping) |> pressHoldingRequests

/// The X11 requests that perform `action` at `target` (a click moves the
/// cursor there and presses/releases the button with a 60 ms hold; typing
/// clicks the position, then delivers `Cadence.keys`) — resolved against
/// `mapping`, the LIVE keyboard mapping the cell-agent fetched from its own
/// X display (`XTest.keyboardMapping`) right after opening it; every
/// character this function types is only ever as correct as `mapping`
/// actually is (§9: a fictitious identity mapping here is what corrupted
/// typed text in a real recording — see `KeyboardMapping`'s own doc).
let plan (mapping: KeyboardMapping) (action: Action) (target: ScreenRect) : X11Request list =
  match action with
  | Action.Click _ -> clickRequests target
  | Action.Type(_, text, cadenceSeed) -> clickRequests target @ typingRequests mapping text (seedOfCadenceSeed cadenceSeed)
  // The chained submit click is delivered separately by the cell-agent via
  // `clickFrom` (it targets a SECOND rect this function is never given —
  // `plan` resolves against exactly one `ScreenRect`, §5); this case covers
  // only the typing half, identically to `Type` above.
  | Action.TypeThenClick(_, text, cadenceSeed, _) -> clickRequests target @ typingRequests mapping text (seedOfCadenceSeed cadenceSeed)
  // Same as `TypeThenClick`: this function plans for exactly ONE resolved
  // rect (`target`), so it covers only the click+type half; the cell-agent
  // delivers the pre-click and submit-click hops separately via
  // `clickFrom`, against their OWN resolved rects this function never sees.
  | Action.ClickThenTypeThenClick(_, _, text, cadenceSeed, _) -> clickRequests target @ typingRequests mapping text (seedOfCadenceSeed cadenceSeed)
  | Action.Chord keys -> chordRequests mapping keys
  | Action.Typo(_, wrong, right) ->
    let wrongText = Text.value wrong
    let seed = seedOfCadenceSeed (CadenceSeed.ofId wrongText)
    let backspaces = [ for _ in 1 .. wrongText.Length -> keyTapRequests mapping Key.Backspace ] |> List.collect id
    clickRequests target @ typingRequests mapping wrong seed @ backspaces @ typingRequests mapping right seed
  // Neither of these drives fake input: `Setup` is an API-level action
  // performed through a client's own command surface (§5: "not captured"),
  // and `Await` only waits for a `Signal` — nothing to inject.
  | Action.Setup _ -> []
  | Action.Await _ -> []
