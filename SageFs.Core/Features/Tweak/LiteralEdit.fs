/// Replace a literal leaf's value in source while keeping the author's
/// style: `1.0` stays `1.0` (never becomes `1.`), `0x1F` stays hex, `1_000`
/// keeps its underscore grouping, suffixes and units of measure survive, and
/// a float that round-trips through a double never drifts
/// (`0.12` never becomes `0.11999999`).
///
/// The trick that makes "setting the old value back reproduces the original
/// source byte for byte" true by construction rather than by luck: when the
/// requested new value numerically equals what was already there,
/// `setLiteral` hands back the untouched original text instead of
/// reformatting a value it already has. That is not a shortcut around the
/// property, it is the only honest thing to do: nothing changed, so nothing
/// should be rewritten.
module SageFs.Features.Tweak.LiteralEdit

open System
open System.Globalization
open System.Text.RegularExpressions
open Fantomas.FCS.Syntax
open SageFs.Features.Tweak.TweakAddress

[<RequireQualifiedAccess>]
type LiteralValue =
  | Bool of bool
  | Integer of int64
  | Real of float
  | Char of char
  | Text of string
  /// A DU-case-like bare identifier scrubbed as a literal, e.g. `Hard`.
  | Case of string

[<RequireQualifiedAccess>]
type NumberBase =
  | Dec
  | Hex
  | Oct
  | Bin

[<RequireQualifiedAccess>]
type LiteralStyle =
  | BoolStyle
  | IntStyle of numberBase: NumberBase * suffix: string * grouped: bool
  /// `trailingDot`: the source spelled it `5.` with nothing after the point.
  | FloatStyle of suffix: string * trailingDot: bool
  | CharStyle
  | StringStyle
  | CaseStyle
  | MeasureStyle of unitText: string * inner: LiteralStyle

type ResolvedLiteral =
  { Address: TweakAddress
    Range: Fantomas.FCS.Text.range
    OriginalText: string
    Value: LiteralValue
    Style: LiteralStyle }

[<RequireQualifiedAccess>]
type LiteralError =
  | Gone of ResolveError
  /// The address resolves, but to something that isn't a literal this
  /// module knows how to scrub (a call, a record, an unsupported constant
  /// kind such as a custom numeric suffix).
  | NotALiteral of text: string

[<RequireQualifiedAccess>]
type SetLiteralError =
  | Gone of ResolveError
  | NotALiteral of text: string
  /// The new value's kind doesn't match the literal's own style (e.g.
  /// setting a `Text` value onto an `IntStyle` address). A tweak never
  /// silently changes an expression's type.
  | KindMismatch of reason: string

// ── extracting VALUE from the parsed constant (correct for escapes,
//    unaffected by how the literal happens to be spelled) ──

let rec private valueOfConst (c: SynConst) : LiteralValue option =
  match c with
  | SynConst.Bool b -> Some(LiteralValue.Bool b)
  | SynConst.SByte v -> Some(LiteralValue.Integer(int64 v))
  | SynConst.Byte v -> Some(LiteralValue.Integer(int64 v))
  | SynConst.Int16 v -> Some(LiteralValue.Integer(int64 v))
  | SynConst.UInt16 v -> Some(LiteralValue.Integer(int64 v))
  | SynConst.Int32 v -> Some(LiteralValue.Integer(int64 v))
  | SynConst.UInt32 v -> Some(LiteralValue.Integer(int64 v))
  | SynConst.Int64 v -> Some(LiteralValue.Integer v)
  | SynConst.UInt64 v -> Some(LiteralValue.Integer(int64 v))
  | SynConst.IntPtr v -> Some(LiteralValue.Integer v)
  | SynConst.UIntPtr v -> Some(LiteralValue.Integer(int64 v))
  | SynConst.Single v -> Some(LiteralValue.Real(float v))
  | SynConst.Double v -> Some(LiteralValue.Real v)
  | SynConst.Decimal v -> Some(LiteralValue.Real(float v))
  | SynConst.Char c -> Some(LiteralValue.Char c)
  | SynConst.String(text = s) -> Some(LiteralValue.Text s)
  | SynConst.Measure(constant = inner) -> valueOfConst inner
  | _ -> None

// ── deriving STYLE from the raw text (the only source of truth for how the
//    author actually spelled it) ──

let private measureShape = Regex(@"^(?<body>.*)<(?<unit>[^<>]+)>$", RegexOptions.Compiled)
let private hexShape = Regex(@"^(?<sign>-?)0[xX](?<digits>[0-9a-fA-F_]+)(?<suffix>[a-zA-Z]*)$", RegexOptions.Compiled)
let private octShape = Regex(@"^(?<sign>-?)0[oO](?<digits>[0-7_]+)(?<suffix>[a-zA-Z]*)$", RegexOptions.Compiled)
let private binShape = Regex(@"^(?<sign>-?)0[bB](?<digits>[01_]+)(?<suffix>[a-zA-Z]*)$", RegexOptions.Compiled)
let private floatShape =
  Regex(@"^(?<sign>-?)(?<int>[0-9_]+)\.(?<frac>[0-9_]*)(?<suffix>[a-zA-Z]*)$", RegexOptions.Compiled)
let private intShape = Regex(@"^(?<sign>-?)(?<digits>[0-9_]+)(?<suffix>[a-zA-Z]*)$", RegexOptions.Compiled)

/// Style for everything except bool/char/string/case, which have exactly one
/// style each. Returns `None` for a numeric spelling this module doesn't
/// recognize (a custom-suffixed `SynConst.UserNum`, e.g. `1I`/`2N`).
let private numericStyleOf (text: string) : LiteralStyle option =
  let m = hexShape.Match text
  match m.Success with
  | true -> Some(LiteralStyle.IntStyle(NumberBase.Hex, m.Groups["suffix"].Value, m.Groups["digits"].Value.Contains "_"))
  | false ->
  let m = octShape.Match text
  match m.Success with
  | true -> Some(LiteralStyle.IntStyle(NumberBase.Oct, m.Groups["suffix"].Value, m.Groups["digits"].Value.Contains "_"))
  | false ->
  let m = binShape.Match text
  match m.Success with
  | true -> Some(LiteralStyle.IntStyle(NumberBase.Bin, m.Groups["suffix"].Value, m.Groups["digits"].Value.Contains "_"))
  | false ->
  let m = floatShape.Match text
  match m.Success with
  | true -> Some(LiteralStyle.FloatStyle(m.Groups["suffix"].Value, m.Groups["frac"].Value.Length = 0))
  | false ->
  let m = intShape.Match text
  match m.Success with
  | true -> Some(LiteralStyle.IntStyle(NumberBase.Dec, m.Groups["suffix"].Value, m.Groups["digits"].Value.Contains "_"))
  | false -> None

let rec private styleOf (text: string) : LiteralStyle option =
  let trimmed = text.Trim()
  match trimmed with
  | "true" | "false" -> Some LiteralStyle.BoolStyle
  | t when t.Length >= 2 && t.[0] = '\'' && t.[t.Length - 1] = '\'' -> Some LiteralStyle.CharStyle
  | t when t.Length >= 2 && t.[0] = '"' && t.[t.Length - 1] = '"' -> Some LiteralStyle.StringStyle
  | t when t.Length > 0 && Char.IsUpper t.[0] && t |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') ->
    Some LiteralStyle.CaseStyle
  | t ->
    let m = measureShape.Match t
    match m.Success with
    | true ->
      styleOf (m.Groups["body"].Value)
      |> Option.map (fun inner -> LiteralStyle.MeasureStyle(m.Groups["unit"].Value, inner))
    | false -> numericStyleOf t

/// Read the literal at `address`: its exact original text, the value it
/// carries, and the style to preserve when it's set again.
let readLiteral (source: string) (address: TweakAddress) : Result<ResolvedLiteral, LiteralError> =
  match resolve source address with
  | Error e -> Error(LiteralError.Gone e)
  | Ok resolved ->
    match parseExpr resolved.Text with
    | Error _ -> Error(LiteralError.NotALiteral resolved.Text)
    | Ok expr ->
      let valueAndStyle =
        match expr with
        | SynExpr.Const(constant = c) -> valueOfConst c |> Option.map (fun v -> v, styleOf resolved.Text)
        | SynExpr.Ident id when id.idText.Length > 0 && Char.IsUpper id.idText.[0] ->
          Some(LiteralValue.Case id.idText, Some LiteralStyle.CaseStyle)
        | _ -> None
      match valueAndStyle with
      | Some(value, Some style) ->
        Ok
          { Address = address
            Range = resolved.Range
            OriginalText = resolved.Text
            Value = value
            Style = style }
      | _ -> Error(LiteralError.NotALiteral resolved.Text)

// ── formatting a value back into text under a given style ──

let private escapeChar (c: char) =
  match c with
  | '\'' -> "\\'"
  | '\\' -> "\\\\"
  | '\n' -> "\\n"
  | '\r' -> "\\r"
  | '\t' -> "\\t"
  | c -> string c

let private escapeString (s: string) =
  s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")

/// Group a plain digit string every `size` digits from the right, `_`-joined
///, the convention this module applies to a freshly formatted value under a
/// style that was already grouped. It does not try to reproduce irregular
/// grouping a human might have typed by hand; the short-circuit in
/// `setLiteral` is what keeps re-setting the SAME value exact regardless.
let private groupDigits (size: int) (digits: string) : string =
  match digits.Length <= size with
  | true -> digits
  | false ->
    digits
    |> Seq.rev
    |> Seq.chunkBySize size
    |> Seq.map (Array.rev >> String)
    |> Seq.rev
    |> String.concat "_"

let private formatInteger (numberBase: NumberBase) (suffix: string) (grouped: bool) (v: int64) : string =
  // Hex/oct/bin literal TOKENS have no negative spelling in F# (a leading
  // `-` there would parse as unary negation, a different expression), this
  // module only ever produces one for Dec, which is the only base a
  // negative source value can legitimately have come from.
  let sign, magnitude = match v < 0L with true -> "-", uint64 (-v) | false -> "", uint64 v
  let digits =
    match numberBase with
    | NumberBase.Dec -> string magnitude
    | NumberBase.Hex -> magnitude.ToString("X", CultureInfo.InvariantCulture)
    | NumberBase.Oct -> Convert.ToString(int64 magnitude, 8)
    | NumberBase.Bin -> Convert.ToString(int64 magnitude, 2)
  let prefix =
    match numberBase with
    | NumberBase.Dec -> ""
    | NumberBase.Hex -> "0x"
    | NumberBase.Oct -> "0o"
    | NumberBase.Bin -> "0b"
  let groupSize = match numberBase with NumberBase.Hex -> 4 | _ -> 3
  let body = match grouped with true -> groupDigits groupSize digits | false -> digits
  sign + prefix + body + suffix

/// Exact round-trip float formatting: .NET's default `ToString` for
/// `double`/`single` has produced the shortest round-trippable string since
/// .NET Core 3.0, so `0.12` always comes back as `"0.12"`, never
/// `"0.11999999"`.
let private formatFloat (suffix: string) (trailingDot: bool) (v: float) : string =
  let core =
    match trailingDot && v = Math.Truncate v && not (Double.IsInfinity v) && not (Double.IsNaN v) with
    | true -> v.ToString("F0", CultureInfo.InvariantCulture) + "."
    | false ->
      let s = v.ToString(CultureInfo.InvariantCulture)
      match s.IndexOfAny [| '.'; 'e'; 'E' |] with
      | -1 -> s + ".0" // an F# float literal always carries a decimal point
      | _ -> s
  core + suffix

let rec formatLiteral (style: LiteralStyle) (value: LiteralValue) : Result<string, SetLiteralError> =
  match style, value with
  | LiteralStyle.MeasureStyle(unitText, inner), _ ->
    formatLiteral inner value |> Result.map (fun s -> sprintf "%s<%s>" s unitText)
  | LiteralStyle.BoolStyle, LiteralValue.Bool b -> Ok(match b with true -> "true" | false -> "false")
  | LiteralStyle.CaseStyle, LiteralValue.Case name -> Ok name
  | LiteralStyle.CharStyle, LiteralValue.Char c -> Ok(sprintf "'%s'" (escapeChar c))
  | LiteralStyle.StringStyle, LiteralValue.Text s -> Ok(sprintf "\"%s\"" (escapeString s))
  | LiteralStyle.IntStyle(numberBase, suffix, grouped), LiteralValue.Integer v ->
    Ok(formatInteger numberBase suffix grouped v)
  | LiteralStyle.FloatStyle(suffix, trailingDot), LiteralValue.Real v -> Ok(formatFloat suffix trailingDot v)
  | _ -> Error(SetLiteralError.KindMismatch "the new value's kind doesn't match this literal's own style")

let private sameValue (a: LiteralValue) (b: LiteralValue) : bool =
  match a, b with
  | LiteralValue.Bool x, LiteralValue.Bool y -> x = y
  | LiteralValue.Integer x, LiteralValue.Integer y -> x = y
  | LiteralValue.Real x, LiteralValue.Real y -> x = y || (Double.IsNaN x && Double.IsNaN y)
  | LiteralValue.Char x, LiteralValue.Char y -> x = y
  | LiteralValue.Text x, LiteralValue.Text y -> x = y
  | LiteralValue.Case x, LiteralValue.Case y -> x = y
  | _ -> false

/// Replace the literal at `address` with `newValue`, keeping the author's
/// style. Setting the exact value that was already there returns `source`
/// unchanged, byte for byte, see the module header.
let setLiteral (source: string) (address: TweakAddress) (newValue: LiteralValue) : Result<string, SetLiteralError> =
  match readLiteral source address with
  | Error(LiteralError.Gone e) -> Error(SetLiteralError.Gone e)
  | Error(LiteralError.NotALiteral t) -> Error(SetLiteralError.NotALiteral t)
  | Ok resolved when sameValue resolved.Value newValue -> Ok source
  | Ok resolved ->
    match formatLiteral resolved.Style newValue with
    | Error e -> Error e
    | Ok newText -> Ok(replaceRange source resolved.Range newText)
