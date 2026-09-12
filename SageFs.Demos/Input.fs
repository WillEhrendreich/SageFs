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

/// TODO(shape): `Input.plan` has no live `KeyboardMapping` parameter either
/// (§5) — the cell-agent threads the mapping it fetched from its own X
/// display in Wave 2. Until then, key resolution here uses an identity
/// mapping (keysym == keycode) covering the keysyms `Keymap.keysymsFor` can
/// produce, so this layer is exercised and callable end to end; `Keymap`
/// itself never hard-codes this — only this placeholder default does.
let private identityMapping: KeyboardMapping =
  let named =
    [ 0xff0d; 0xff1b; 0xff09; 0xff08; 0xff51; 0xff52; 0xff53; 0xff54; 0xffe1; 0xffe3; 0xffe9 ]
    @ [ for n in 0..34 -> 0xffbe + n ]
  let ascii = [ 0..255 ]
  { KeysymToKeycode = (named @ ascii) |> List.distinct |> List.map (fun ks -> ks, ks) |> Map.ofList }

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

let private keyTapRequests (key: Key) : X11Request list =
  Keymap.resolve identityMapping key
  |> List.collect (fun kc -> [ X11Request.FakeKey(kc, Pressed.Down); X11Request.FakeKey(kc, Pressed.Up) ])

let private typingRequests (text: Text) (seed: Seed) : X11Request list =
  Cadence.keys text seed |> List.collect (fun (key, _delay) -> keyTapRequests key)

/// A chord/shortcut: every key pressed down in the given order (so earlier
/// keys — the modifiers — are already held by the time the last one goes
/// down), then released in reverse order (§4.3: "chords ... expand to
/// ordered press/release sequences").
let private chordRequests (keys: Key list) : X11Request list =
  let keycodes = keys |> List.collect (Keymap.resolve identityMapping)
  let downs = keycodes |> List.map (fun kc -> X11Request.FakeKey(kc, Pressed.Down))
  let ups = keycodes |> List.rev |> List.map (fun kc -> X11Request.FakeKey(kc, Pressed.Up))
  downs @ ups

/// The X11 requests that perform `action` at `target` (a click moves the
/// cursor there and presses/releases the button with a 60 ms hold; typing
/// clicks the position, then delivers `Cadence.keys`).
let plan (action: Action) (target: ScreenRect) : X11Request list =
  match action with
  | Action.Click _ -> clickRequests target
  | Action.Type(_, text, cadenceSeed) -> clickRequests target @ typingRequests text (seedOfCadenceSeed cadenceSeed)
  | Action.Chord keys -> chordRequests keys
  | Action.Typo(_, wrong, right) ->
    let wrongText = Text.value wrong
    let seed = seedOfCadenceSeed (CadenceSeed.ofId wrongText)
    let backspaces = [ for _ in 1 .. wrongText.Length -> keyTapRequests Key.Backspace ] |> List.collect id
    clickRequests target @ typingRequests wrong seed @ backspaces @ typingRequests right seed
  // Neither of these drives fake input: `Setup` is an API-level action
  // performed through a client's own command surface (§5: "not captured"),
  // and `Await` only waits for a `Signal` — nothing to inject.
  | Action.Setup _ -> []
  | Action.Await _ -> []
