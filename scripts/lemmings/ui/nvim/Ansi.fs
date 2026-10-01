// ANSI terminal text to HTML, for the screenshots a design review reads.
//
// `tmux capture-pane -e -p` returns the pane as text with SGR escape sequences. This turns it
// into cells (a character and a style), then into HTML with one <div> per row. Two rules
// make it safe to feed anything:
//   - it is total: any string in, a grid out, never an exception;
//   - the SGR cases it understands are a closed union (Sgr). Everything else, including every
//     other CSI sequence, OSC string and stray control character, is dropped, never printed.
// The visible text is never changed: the text of the HTML equals the text of the input with
// the escape sequences removed (a property test pins that).
module LemDrive.Ansi

open System
open System.Text
open System.Text.RegularExpressions

type Color =
  | DefaultColor
  | Palette of int
  | Rgb of int * int * int

type Attr =
  | Bold
  | Dim
  | Italic
  | Underline
  | Reverse
  | Strike

type Style =
  { Fg: Color
    Bg: Color
    Attrs: Set<Attr> }

let plain : Style = { Fg = DefaultColor; Bg = DefaultColor; Attrs = Set.empty }

/// What one SGR parameter run means. The only things a style can ever be told to do.
type Sgr =
  | ResetAll
  | SetAttr of Attr
  | ClearAttr of Attr
  | SetFg of Color
  | SetBg of Color
  | Ignored of int

module Sgr =
  let private paletteMax = 255
  let private channelMax = 255
  let private standardColors = 8

  let private clamp (lo: int) (hi: int) (v: int) = max lo (min hi v)

  /// Reads the colour that follows a 38 or 48: `5;n` (palette) or `2;r;g;b` (true colour).
  /// Returns the colour and the rest of the parameters.
  let private extended (rest: int list) : (Color * int list) option =
    match rest with
    | 5 :: n :: tail -> Some(Palette(clamp 0 paletteMax n), tail)
    | 2 :: r :: g :: b :: tail -> Some(Rgb(clamp 0 channelMax r, clamp 0 channelMax g, clamp 0 channelMax b), tail)
    | _ -> None

  let rec ofParams (ps: int list) : Sgr list =
    match ps with
    | [] -> []
    | 0 :: tail -> ResetAll :: ofParams tail
    | 1 :: tail -> SetAttr Bold :: ofParams tail
    | 2 :: tail -> SetAttr Dim :: ofParams tail
    | 3 :: tail -> SetAttr Italic :: ofParams tail
    | 4 :: tail -> SetAttr Underline :: ofParams tail
    | 7 :: tail -> SetAttr Reverse :: ofParams tail
    | 9 :: tail -> SetAttr Strike :: ofParams tail
    | 22 :: tail -> ClearAttr Bold :: ClearAttr Dim :: ofParams tail
    | 23 :: tail -> ClearAttr Italic :: ofParams tail
    | 24 :: tail -> ClearAttr Underline :: ofParams tail
    | 27 :: tail -> ClearAttr Reverse :: ofParams tail
    | 29 :: tail -> ClearAttr Strike :: ofParams tail
    | 39 :: tail -> SetFg DefaultColor :: ofParams tail
    | 49 :: tail -> SetBg DefaultColor :: ofParams tail
    | n :: tail when n >= 30 && n < 30 + standardColors -> SetFg(Palette(n - 30)) :: ofParams tail
    | n :: tail when n >= 40 && n < 40 + standardColors -> SetBg(Palette(n - 40)) :: ofParams tail
    | n :: tail when n >= 90 && n < 90 + standardColors -> SetFg(Palette(n - 90 + standardColors)) :: ofParams tail
    | n :: tail when n >= 100 && n < 100 + standardColors -> SetBg(Palette(n - 100 + standardColors)) :: ofParams tail
    | 38 :: tail ->
      match extended tail with
      | Some(color, rest) -> SetFg color :: ofParams rest
      | None -> Ignored 38 :: []
    | 48 :: tail ->
      match extended tail with
      | Some(color, rest) -> SetBg color :: ofParams rest
      | None -> Ignored 48 :: []
    | n :: tail -> Ignored n :: ofParams tail

  let apply (style: Style) (code: Sgr) : Style =
    match code with
    | ResetAll -> plain
    | SetAttr a -> { style with Attrs = Set.add a style.Attrs }
    | ClearAttr a -> { style with Attrs = Set.remove a style.Attrs }
    | SetFg c -> { style with Fg = c }
    | SetBg c -> { style with Bg = c }
    | Ignored _ -> style

type Cell = { Text: string; Style: Style }

// ---------------------------------------------------------------------------
// Tokenising
// ---------------------------------------------------------------------------

let private esc = '\u001b'
let private bel = '\u0007'

type private Token =
  | Printable of string
  | StyleChange of Sgr list
  | NewLine

/// Parameter bytes of a CSI sequence are digits, ';' and ':'. A missing number is 0.
let private parseParams (text: string) : int list =
  if text.Length = 0 then [ 0 ]
  else
    text.Split([| ';'; ':' |])
    |> Array.map (fun p ->
      match Int32.TryParse p with
      | true, n -> n
      | _ -> 0)
    |> List.ofArray

let private isCsiFinal (c: char) = c >= '@' && c <= '~'

let private tokenize (input: string) : Token list =
  let tokens = ResizeArray<Token>()
  let mutable i = 0
  while i < input.Length do
    let c = input.[i]
    if c = esc then
      if i + 1 >= input.Length then
        i <- i + 1
      else
        match input.[i + 1] with
        | '[' ->
          // CSI: parameter and intermediate bytes, then one final byte. An unterminated
          // sequence swallows the rest of the input, which is the safe side to err on.
          let mutable j = i + 2
          while j < input.Length && not (isCsiFinal input.[j]) do
            j <- j + 1
          if j < input.Length then
            if input.[j] = 'm' then tokens.Add(StyleChange(Sgr.ofParams (parseParams (input.Substring(i + 2, j - i - 2)))))
            i <- j + 1
          else
            i <- input.Length
        | ']' ->
          // OSC: ends at BEL or ESC \
          let mutable j = i + 2
          let mutable finished = false
          while j < input.Length && not finished do
            if input.[j] = bel then
              finished <- true
              j <- j + 1
            elif input.[j] = esc && j + 1 < input.Length && input.[j + 1] = '\\' then
              finished <- true
              j <- j + 2
            else
              j <- j + 1
          i <- j
        | _ -> i <- i + 2
    elif c = '\n' then
      tokens.Add NewLine
      i <- i + 1
    elif c = '\t' then
      tokens.Add(Printable " ")
      i <- i + 1
    elif Char.IsControl c then
      i <- i + 1
    elif Char.IsHighSurrogate c && i + 1 < input.Length && Char.IsLowSurrogate input.[i + 1] then
      tokens.Add(Printable(input.Substring(i, 2)))
      i <- i + 2
    elif Char.IsSurrogate c then
      tokens.Add(Printable "�")
      i <- i + 1
    else
      tokens.Add(Printable(string c))
      i <- i + 1
  List.ofSeq tokens

/// The grid: one list of cells per row. An input with N newlines has N + 1 rows.
let parse (input: string) : Cell list list =
  let rows = ResizeArray<Cell list>()
  let row = ResizeArray<Cell>()
  let mutable style = plain
  for token in tokenize input do
    match token with
    | Printable text -> row.Add { Text = text; Style = style }
    | StyleChange codes -> style <- List.fold Sgr.apply style codes
    | NewLine ->
      rows.Add(List.ofSeq row)
      row.Clear()
  rows.Add(List.ofSeq row)
  List.ofSeq rows

let plainText (grid: Cell list list) : string =
  grid
  |> List.map (fun row -> row |> List.map (fun c -> c.Text) |> String.concat "")
  |> String.concat "\n"

// ---------------------------------------------------------------------------
// Colours
// ---------------------------------------------------------------------------

module Theme =
  let DefaultFg = "#d4d4d4"
  let DefaultBg = "#1e1e1e"

  /// xterm's 16 base colours, tuned for a dark background.
  let private baseColors =
    [| "#000000"; "#cd3131"; "#0dbc79"; "#e5e510"; "#2472c8"; "#bc3fbc"; "#11a8cd"; "#e5e5e5"
       "#666666"; "#f14c4c"; "#23d18b"; "#f5f543"; "#3b8eea"; "#d670d6"; "#29b8db"; "#ffffff" |]

  let private cubeStart = 16
  let private greyStart = 232
  let private cubeSteps = 6
  let private cubeLevel (n: int) = if n = 0 then 0 else 55 + n * 40
  let private greyLevel (n: int) = 8 + n * 10

  let paletteHex (n: int) : string =
    if n < cubeStart then baseColors.[max 0 n]
    elif n < greyStart then
      let k = n - cubeStart
      sprintf "#%02x%02x%02x" (cubeLevel (k / (cubeSteps * cubeSteps))) (cubeLevel ((k / cubeSteps) % cubeSteps)) (cubeLevel (k % cubeSteps))
    else
      let g = greyLevel (min 23 (n - greyStart))
      sprintf "#%02x%02x%02x" g g g

  let hex (color: Color) (fallback: string) : string =
    match color with
    | DefaultColor -> fallback
    | Palette n -> paletteHex n
    | Rgb(r, g, b) -> sprintf "#%02x%02x%02x" r g b

// ---------------------------------------------------------------------------
// HTML
// ---------------------------------------------------------------------------

let private escapeHtml (text: string) : string =
  let sb = StringBuilder(text.Length)
  for c in text do
    match c with
    | '&' -> sb.Append "&amp;" |> ignore
    | '<' -> sb.Append "&lt;" |> ignore
    | '>' -> sb.Append "&gt;" |> ignore
    | '"' -> sb.Append "&quot;" |> ignore
    | '\'' -> sb.Append "&#39;" |> ignore
    | _ -> sb.Append c |> ignore
  sb.ToString()

/// Cells that take two columns on a terminal: East Asian wide forms and emoji. Drawing each in a
/// two-column box keeps the rest of the row on the grid.
let private wideRanges =
  [ 0x1100, 0x115F
    0x2E80, 0xA4CF
    0xAC00, 0xD7A3
    0xF900, 0xFAFF
    0xFE30, 0xFE6F
    0xFF00, 0xFF60
    0xFFE0, 0xFFE6
    0x1F300, 0x1FAFF
    0x20000, 0x3FFFD ]

let isWide (text: string) : bool =
  if text.Length = 0 then false
  else
    let cp = Char.ConvertToUtf32(text, 0)
    wideRanges |> List.exists (fun (lo, hi) -> cp >= lo && cp <= hi)

let private cssOf (style: Style) : string =
  let reversed = style.Attrs.Contains Reverse
  let fg = Theme.hex style.Fg Theme.DefaultFg
  let bg = Theme.hex style.Bg Theme.DefaultBg
  let shownFg, shownBg = if reversed then bg, fg else fg, bg
  let parts =
    [ yield sprintf "color:%s" shownFg
      if shownBg <> Theme.DefaultBg then yield sprintf "background:%s" shownBg
      if style.Attrs.Contains Bold then yield "font-weight:bold"
      if style.Attrs.Contains Dim then yield "opacity:.6"
      if style.Attrs.Contains Italic then yield "font-style:italic"
      match style.Attrs.Contains Underline, style.Attrs.Contains Strike with
      | true, true -> yield "text-decoration:underline line-through"
      | true, false -> yield "text-decoration:underline"
      | false, true -> yield "text-decoration:line-through"
      | false, false -> () ]
  String.concat ";" parts

let private runHtml (cells: Cell list) : string =
  let style = (List.head cells).Style
  let body =
    cells
    |> List.map (fun c -> if isWide c.Text then sprintf "<i class=\"w\">%s</i>" (escapeHtml c.Text) else escapeHtml c.Text)
    |> String.concat ""
  sprintf "<span style=\"%s\">%s</span>" (cssOf style) body

let private rowHtml (cells: Cell list) : string =
  let runs = cells |> List.fold (fun (acc: Cell list list) cell ->
    match acc with
    | (last :: _ as current) :: rest when last.Style = cell.Style -> (cell :: current) :: rest
    | _ -> [ cell ] :: acc) []
  let ordered = runs |> List.rev |> List.map List.rev
  sprintf "<div class=\"row\">%s</div>" (ordered |> List.map runHtml |> String.concat "")

/// The grid as rows of HTML. No document wrapper: the caller supplies the page and its font.
let toHtml (grid: Cell list list) : string =
  grid |> List.map rowHtml |> String.concat "\n"

/// Text -> HTML in one step.
let render (ansi: string) : string = toHtml (parse ansi)

/// The text of rendered HTML: tags removed, entities decoded. For tests and for checking a render.
let textOfHtml (html: string) : string =
  Regex.Replace(html, "<[^>]*>", "")
  |> fun s -> s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&amp;", "&")
