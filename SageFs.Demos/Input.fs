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

/// The assumed cursor position before a scenario's very first hop, or after
/// a client (re)launch — nothing has moved the synthetic cursor yet, so the
/// top-left corner is as good an "unknown" as any. Every OTHER hop starts
/// from wherever the previous one actually ended (`plan`/`clickFrom`'s own
/// `start` parameter, threaded by the cell-agent) — §9's fix for a cursor
/// that visibly TELEPORTED back to this corner between a step's own
/// pre-click and primary hops instead of continuing on, which read as
/// inhuman on camera.
let restPosition: Point = { X = 0; Y = 0 }

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

/// Move from `start` to `target`'s centre, then press and release the left
/// button (§4.3: "Click target = Motion.path to rect centre then FakeButton
/// down/up (60 ms hold)"). The cell-agent chains hops continuously through
/// this — a pre-click, a primary click/type, and a submit click within one
/// step all start from wherever the PREVIOUS hop actually ended, `restPosition`
/// only for a step's very first hop — so the synthetic cursor never
/// teleports back to a corner mid-step (§9).
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

/// The X11 requests that perform `action` at `target`, with its own initial
/// motion starting from `start` — never a hard-coded rest position, so the
/// cell-agent can chain this hop continuously from wherever a preceding
/// pre-click hop actually ended (§9: a cursor that jumps back to a fixed
/// corner between two hops of the SAME step reads as obviously synthetic).
/// A click moves the cursor there and presses/releases the button with a 60
/// ms hold; typing clicks the position, then delivers `Cadence.keys` —
/// resolved against `mapping`, the LIVE keyboard mapping the cell-agent
/// fetched from its own X display (`XTest.keyboardMapping`) right after
/// opening it; every character this function types is only ever as correct
/// as `mapping` actually is (§9: a fictitious identity mapping here is what
/// corrupted typed text in a real recording — see `KeyboardMapping`'s own
/// doc).
let plan (mapping: KeyboardMapping) (start: Point) (action: Action) (target: ScreenRect) : X11Request list =
  match action with
  | Action.Click _ -> clickFrom start target
  | Action.Type(_, text, cadenceSeed) -> clickFrom start target @ typingRequests mapping text (seedOfCadenceSeed cadenceSeed)
  // The chained submit click is delivered separately by the cell-agent via
  // `clickFrom` (it targets a SECOND rect this function is never given —
  // `plan` resolves against exactly one `ScreenRect`, §5); this case covers
  // only the typing half, identically to `Type` above.
  | Action.TypeThenClick(_, text, cadenceSeed, _) -> clickFrom start target @ typingRequests mapping text (seedOfCadenceSeed cadenceSeed)
  // Same as `TypeThenClick`: this function plans for exactly ONE resolved
  // rect (`target`), so it covers only the click+type half; the cell-agent
  // delivers the pre-click and submit-click hops separately via
  // `clickFrom`, against their OWN resolved rects this function never sees.
  | Action.ClickThenTypeThenClick(_, _, text, cadenceSeed, _) -> clickFrom start target @ typingRequests mapping text (seedOfCadenceSeed cadenceSeed)
  | Action.Chord keys -> chordRequests mapping keys
  | Action.Typo(_, wrong, right) ->
    let wrongText = Text.value wrong
    let seed = seedOfCadenceSeed (CadenceSeed.ofId wrongText)
    let backspaces = [ for _ in 1 .. wrongText.Length -> keyTapRequests mapping Key.Backspace ] |> List.collect id
    clickFrom start target @ typingRequests mapping wrong seed @ backspaces @ typingRequests mapping right seed
  // Neither of these drives fake input: `Setup` is an API-level action
  // performed through a client's own command surface (§5: "not captured"),
  // and `Await` only waits for a `Signal` — nothing to inject.
  | Action.Setup _ -> []
  | Action.Await _ -> []
