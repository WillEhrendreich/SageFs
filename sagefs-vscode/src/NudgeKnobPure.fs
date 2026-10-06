/// The editing half of a value knob: the span of a value the daemon's `nudge_value` inspect
/// reports, the gesture facts a VS Code selection event carries, and the arithmetic that turns
/// dragged columns into the literal to write — as pure data and pure decisions. No VS Code, no
/// HTTP, no timers: `tests/NudgeKnobContractTests.fsx` runs this under plain `dotnet fsi`.
///
/// The arithmetic mirrors the dashboard knob's (`SageFs.Core/Features/Tweak/BindingTweak.fs`
/// `Step` / `Stepping.literalAfter`): one step is one unit in the literal's last decimal place
/// (a whole unit when it is spelled without decimals), the result is rounded to the literal's
/// own decimals so the text is a number the author could have typed, and a step that would
/// leave the numbers a literal can spell is refused rather than wrapped. The door reads the
/// literal as the kind it already is and keeps the file's style, so what is sent here is plain
/// decimal and what comes back may be hex.
///
/// THE GESTURE, AND ITS HONEST LIMIT. VS Code's `TextEditorSelectionChangeEvent` carries only
/// the editor, the selections and a `kind` (its own `index.d.ts`: no `modifiers` field), and the
/// extension API has no editor mouse events at all — so an extension is never TOLD "Alt is
/// held". What it is told is what Alt+click CREATES: with `editor.multiCursorModifier = "alt"`
/// (VS Code's default) an Alt+click adds a cursor, so a mouse-kind selection change whose
/// selections exceed one is the only way that state arrives. That is the gesture this decodes:
/// a multi-cursor mouse drag over a knob scrubs it, column by column, and everything else
/// re-anchors instead of counting.
module SageFs.Vscode.NudgeKnobPure

/// What a knob's literal IS, as the dashboard's knob treats it: a number has a drag, anything
/// else has no steps.
[<RequireQualifiedAccess>]
type Scrub =
  | Whole of int64
  | Fraction of value: float * decimals: int
  | NotANumber

type Knob =
  { File: string
    /// As `nudge_value inspect` spells it: `Module.Path.binding/step/step`.
    Address: string
    /// The hash inspect gave — the `seen` every set is checked against.
    Hash: string
    /// The expression's current text in the file.
    Text: string
    /// Line (from 1) and columns (from 0) as inspect reports them, lines/columns inclusive of
    /// the ends the way a drag can sit one past the last digit.
    Line: int
    StartCol: int
    EndLine: int
    EndCol: int
    Scrub: Scrub }

/// How many steps one settle may carry — the dashboard knob's own `TweakSignals.MostSteps`, so
/// a long drag lands where the dashboard's would.
[<Literal>]
let maxPendingSteps = 100

/// The decimals a real literal spells (`1.0` is 1, `0.12` is 2, `5.` and `1e3` are 0, an
/// underscore is not a digit) — the same scan as `BindingTweak.Step.decimalsOf`.
let decimalsOf (text: string) : int =
  let isDigit c = c >= '0' && c <= '9'
  match text.IndexOf '.' with
  | -1 -> 0
  | i ->
    let mutable n = 0
    let mutable j = i + 1
    while j < text.Length && (isDigit text[j] || text[j] = '_') do
      if isDigit text[j] then n <- n + 1
      j <- j + 1
    n

/// The size of one step: one unit in the last decimal place, a whole unit when there are none —
/// `BindingTweak.Step.size`.
let stepSize (decimals: int) : float =
  if decimals <= 0 then 1.0 else System.Math.Pow(10.0, float -decimals)

let private isDigit (c: char) = c >= '0' && c <= '9'

/// Accumulate `digits` in `base_`, refusing to wrap: overflow is a refusal, never a silent
/// two's-complement turn.
let private digitsValue (base_: int64) (digits: string) : int64 option =
  let mutable acc = 0L
  let mutable ok = digits.Length > 0
  let mutable i = 0
  while ok && i < digits.Length do
    let d = digits[i]
    let v =
      if d >= '0' && d <= '9' then int64 (int d - int '0')
      elif d >= 'a' && d <= 'f' then int64 (int d - int 'a' + 10)
      elif d >= 'A' && d <= 'F' then int64 (int d - int 'A' + 10)
      else -1L
    if v < 0L || v >= base_ then ok <- false
    elif acc > (System.Int64.MaxValue - v) / base_ then ok <- false
    else acc <- acc * base_ + v
    i <- i + 1
  if ok then Some acc else None

/// A whole number as the door may spell it: underscores gone, an optional sign, and the
/// `0x` / `0o` / `0b` bases a reals' style-preservation can hand back.
let tryParseIntLike (text: string) : int64 option =
  let t = text.Replace("_", "").Trim()
  let signed, body =
    if t.StartsWith "-" then true, t.Substring(1)
    elif t.StartsWith "+" then false, t.Substring(1)
    else false, t
  let base_, digits =
    if body.StartsWith "0x" || body.StartsWith "0X" then 16L, body.Substring(2)
    elif body.StartsWith "0o" || body.StartsWith "0O" then 8L, body.Substring(2)
    elif body.StartsWith "0b" || body.StartsWith "0B" then 2L, body.Substring(2)
    else 10L, body
  digitsValue base_ digits
  |> Option.map (fun v -> if signed then -v else v)

/// A real as the door may spell it, parsed by hand so .NET and Fable agree — no culture enters
/// it. Accepts `-?digits`, an optional fraction and an optional exponent; anything else (a hex
/// text reaching a Real, an empty string) is not a number.
let tryParseInvariantFloat (text: string) : float option =
  let s = text.Replace("_", "").Trim()
  let n = s.Length
  if n = 0 then None else
  let mutable i = 0
  let mutable negative = false
  if s[i] = '+' || s[i] = '-' then
    negative <- s[i] = '-'
    i <- i + 1
  let mutable whole = 0.0
  let mutable seenDigit = false
  while i < n && isDigit s[i] do
    seenDigit <- true
    whole <- whole * 10.0 + float (int s[i] - int '0')
    i <- i + 1
  let mutable fraction = 0.0
  if i < n && s[i] = '.' then
    i <- i + 1
    let mutable scale = 0.1
    while i < n && isDigit s[i] do
      seenDigit <- true
      fraction <- fraction + float (int s[i] - int '0') * scale
      scale <- scale / 10.0
      i <- i + 1
  if not seenDigit then None else
  let mutable exponent = 0
  let mutable exponentNegative = false
  let mutable wellFormed = true
  if i < n && (s[i] = 'e' || s[i] = 'E') then
    i <- i + 1
    if i < n && (s[i] = '+' || s[i] = '-') then
      exponentNegative <- s[i] = '-'
      i <- i + 1
    let mutable seenExpDigit = false
    while i < n && isDigit s[i] do
      seenExpDigit <- true
      exponent <- exponent * 10 + (int s[i] - int '0')
      i <- i + 1
    if not seenExpDigit then wellFormed <- false
  if not wellFormed || i <> n then None else
  let value = if negative then -(whole + fraction) else whole + fraction
  if exponent = 0 then Some value
  elif exponentNegative then Some (value / System.Math.Pow(10.0, float exponent))
  else Some (value * System.Math.Pow(10.0, float exponent))

[<RequireQualifiedAccess>]
type StepRefusal =
  /// The step would leave the numbers a literal can spell — `BindingTweak.StepRefusal.OutOfRange`.
  | OutOfRange

/// The literal text to send to move a knob `steps` steps from where it stands (negative is
/// down), rounded to its own decimals — `BindingTweak.Stepping.literalAfter`'s arithmetic with
/// an honest refusal instead of a wrapped number.
let literalAfter (knob: Knob) (steps: int) : Result<string, StepRefusal> =
  match knob.Scrub with
  | Scrub.NotANumber -> Error StepRefusal.OutOfRange
  | Scrub.Fraction(value, decimals) ->
    let moved = System.Math.Round(value + float steps * stepSize decimals, decimals)
    match System.Double.IsNaN moved || System.Double.IsInfinity moved with
    | true -> Error StepRefusal.OutOfRange
    | false -> Ok(string moved)
  | Scrub.Whole value ->
    let by = int64 steps
    if by > 0L && value > System.Int64.MaxValue - by then Error StepRefusal.OutOfRange
    elif by < 0L && value < System.Int64.MinValue - by then Error StepRefusal.OutOfRange
    else Ok(string (value + by))

/// The shape of a knob whose text the door just wrote (it keeps the author's style, so
/// `0x1F` comes back hex): kind from what the knob WAS, value and decimals from the text that
/// is actually there now.
let scrubOfText (kind: Scrub) (text: string) : Scrub =
  match kind with
  | Scrub.NotANumber -> Scrub.NotANumber
  | Scrub.Whole _ ->
    match tryParseIntLike text with
    | Some v -> Scrub.Whole v
    | None -> Scrub.NotANumber
  | Scrub.Fraction _ ->
    match tryParseInvariantFloat text with
    | Some v -> Scrub.Fraction(v, decimalsOf text)
    | None -> Scrub.NotANumber

/// The knob as the door's `Written` reply leaves it: new text, new `seen` hash, and the span's
/// end moved by how much the text grew or shrank — a number literal is single-line, so its
/// start never moves.
let afterWrite (knob: Knob) (address: string) (afterText: string) (hashAfter: string) : Knob =
  { knob with
      Address = address
      Text = afterText
      Hash = hashAfter
      EndCol = knob.EndCol + (afterText.Length - knob.Text.Length)
      Scrub = scrubOfText knob.Scrub afterText }

/// Whether a 0-based (line, column) sits inside the knob's span. Both ends inclusive: the door
/// reports lines from 1 and columns from 0, and a drag can hold the caret one past the last
/// digit of the literal.
let hits (knob: Knob) (line: int) (col: int) : bool =
  let oneBased = line + 1
  if oneBased < knob.Line || oneBased > knob.EndLine then false
  elif knob.Line = knob.EndLine then col >= knob.StartCol && col <= knob.EndCol
  elif oneBased = knob.Line then col >= knob.StartCol
  elif oneBased = knob.EndLine then col <= knob.EndCol
  else true

type Gesture =
  { Knob: Knob
    LastLine: int
    LastCol: int
    /// Column steps not yet written: the dashboard knob's count, flushed one write per settle.
    Pending: int
    /// The last event was not a drag event (or a write just landed): the next qualifying event
    /// only re-anchors, because the document may have moved the caret without the pointer
    /// moving.
    Resync: bool }

[<RequireQualifiedAccess>]
type ScrubState =
  | Idle
  | Tracking of Gesture

/// What one `onDidChangeTextEditorSelection` event says, reduced to the facts the decision
/// needs. `Knobs` are what `nudge_value inspect` last reported for `File`.
type Facts =
  { File: string
    /// VS Code says the selection changed due to a click (`TextEditorSelectionChangeKind.Mouse`).
    IsMouse: bool
    /// The mouse gesture carries a multi-cursor. With `editor.multiCursorModifier = "alt"`
    /// (VS Code's default) that is the fingerprint of an Alt+click/Alt+drag — the event itself
    /// never names the modifiers.
    IsAltGesture: bool
    /// The event's caret positions, 0-based (line, character).
    Positions: (int * int) list
    Knobs: Knob list }

let private qualifies (facts: Facts) = facts.IsMouse && facts.IsAltGesture

let private nearest (positions: (int * int) list) (line: int) (col: int) : (int * int) option =
  positions
  |> List.sortBy (fun (l, c) -> abs (l - line) * 100000 + abs (c - col))
  |> List.tryHead

let private clampSteps (n: int) = max -maxPendingSteps (min maxPendingSteps n)

let isTracking (state: ScrubState) : bool =
  match state with
  | ScrubState.Idle -> false
  | ScrubState.Tracking _ -> true

/// The one decision: from where the scrub stands and what the selection event says, where it
/// stands after. An Alt-gesture over a number starts a scrub or counts the pointer's columns
/// into the pending steps; anything else — a plain click, a keypress, another editor —
/// re-anchors instead of counting, so a caret that jumped without the pointer moving can never
/// become a wild step. Pending steps are never dropped here: `flush` is what takes them.
let decide (state: ScrubState) (facts: Facts) : ScrubState =
  match qualifies facts, state with
  | false, ScrubState.Idle -> ScrubState.Idle
  | false, ScrubState.Tracking g -> ScrubState.Tracking { g with Resync = true }
  | true, ScrubState.Idle ->
    facts.Positions
    |> List.tryPick (fun (line, col) ->
      facts.Knobs
      |> List.filter (fun k -> k.File = facts.File && k.Scrub <> Scrub.NotANumber && hits k line col)
      |> List.tryHead
      |> Option.map (fun knob ->
        ScrubState.Tracking
          { Knob = knob; LastLine = line; LastCol = col; Pending = 0; Resync = false }))
    |> Option.defaultValue ScrubState.Idle
  | true, ScrubState.Tracking g when g.Knob.File <> facts.File ->
    ScrubState.Tracking { g with Resync = true }
  | true, ScrubState.Tracking g ->
    match nearest facts.Positions g.LastLine g.LastCol with
    | None -> ScrubState.Tracking { g with Resync = true }
    | Some (line, col) when g.Resync || line <> g.LastLine ->
      ScrubState.Tracking { g with LastLine = line; LastCol = col; Resync = false }
    | Some (line, col) ->
      ScrubState.Tracking
        { g with
            LastLine = line
            LastCol = col
            Pending = clampSteps (g.Pending + (col - g.LastCol)) }

/// The settle: what the quiet timer hands the write path. No event has arrived for the settle
/// window, so the gesture is over — the state goes idle and the pending column steps come back
/// as one (knob, steps) write. That is the dashboard knob's "one write per settle", with the
/// hash the knob last showed.
let flush (state: ScrubState) : ScrubState * (Knob * int) option =
  match state with
  | ScrubState.Idle -> ScrubState.Idle, None
  | ScrubState.Tracking g ->
    match g.Pending with
    | 0 -> ScrubState.Idle, None
    | steps -> ScrubState.Idle, Some(g.Knob, steps)
