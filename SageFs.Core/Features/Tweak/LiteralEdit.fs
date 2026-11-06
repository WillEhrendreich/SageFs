/// Replace a literal leaf's value in source while keeping the author's
/// style: `1.0` stays `1.0` (never becomes `1.`), `0x1F` stays hex, `1_024`
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
  /// The number is the right kind but the literal's own type cannot hold it (`300` for a byte, `NaN` for a float, a negative
  /// number in hex). Written, it would be text that does not compile.
  | OutOfRange of reason: string

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

/// The value and style a piece of source text spells, when it is exactly one
/// literal. Same reading as `readLiteral`, for a caller that already holds the
/// expression's text and does not want the file parsed again.
let readLiteralText (text: string) : Result<LiteralValue * LiteralStyle, LiteralError> =
  match parseExpr text with
  | Error _ -> Error(LiteralError.NotALiteral text)
  | Ok expr ->
    let valueAndStyle =
      match expr with
      | SynExpr.Const(constant = c) -> valueOfConst c |> Option.map (fun v -> v, styleOf text)
      | SynExpr.Ident id when id.idText.Length > 0 && Char.IsUpper id.idText.[0] ->
        Some(LiteralValue.Case id.idText, Some LiteralStyle.CaseStyle)
      | _ -> None
    match valueAndStyle with
    | Some(value, Some style) -> Ok(value, style)
    | _ -> Error(LiteralError.NotALiteral text)

/// Read the literal at `address`: its exact original text, the value it
/// carries, and the style to preserve when it's set again.
let readLiteral (source: string) (address: TweakAddress) : Result<ResolvedLiteral, LiteralError> =
  match resolve source address with
  | Error e -> Error(LiteralError.Gone e)
  | Ok resolved ->
    readLiteralText resolved.Text
    |> Result.map (fun (value, style) ->
      { Address = address
        Range = resolved.Range
        OriginalText = resolved.Text
        Value = value
        Style = style })

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

/// The type a spelling's suffix names, said as a person says it, with the whole numbers it holds. `L` is int64 and `l` is int32,
/// `UL` is a 64-bit unsigned and `ul` is uint32, the way F# reads them. A 64-bit unsigned stops at the top of an int64, because that is
/// the widest number `LiteralValue.Integer` carries.
let private integerFacts (suffix: string) : string * int64 * int64 =
  match suffix with
  | "y" -> "an sbyte", -128L, 127L
  | "uy" -> "a byte", 0L, 255L
  | "s" -> "an int16", -32768L, 32767L
  | "us" -> "a uint16", 0L, 65535L
  | ""
  | "l" -> "an int", int64 Int32.MinValue, int64 Int32.MaxValue
  | "u"
  | "ul" -> "a uint32", 0L, int64 UInt32.MaxValue
  | "UL"
  | "uL"
  | "un" -> "an unsigned 64-bit integer", 0L, Int64.MaxValue
  | _ -> "an int64", Int64.MinValue, Int64.MaxValue

/// Whether a whole number can be spelled as this literal. Hex, octal and binary tokens have no negative spelling (a leading minus
/// would be a different expression), so they start at zero.
let private checkInteger (numberBase: NumberBase) (suffix: string) (v: int64) : Result<unit, SetLiteralError> =
  let typeName, lowest, highest = integerFacts suffix
  let lowest =
    match numberBase with
    | NumberBase.Dec -> lowest
    | NumberBase.Hex
    | NumberBase.Oct
    | NumberBase.Bin -> max 0L lowest
  match v >= lowest && v <= highest with
  | true -> Ok()
  | false ->
    let spelled =
      match numberBase with
      | NumberBase.Dec -> ""
      | NumberBase.Hex -> " Hex has no negative spelling."
      | NumberBase.Oct -> " Octal has no negative spelling."
      | NumberBase.Bin -> " Binary has no negative spelling."
    Error(SetLiteralError.OutOfRange(sprintf "%d is outside what %s holds (%d to %d).%s" v typeName lowest highest spelled))

/// Whether a real number can be spelled as this literal: finite, and inside the suffix's own type.
let private checkReal (suffix: string) (v: float) : Result<unit, SetLiteralError> =
  let typeName, largest =
    match suffix with
    | "f"
    | "F" -> "a float32", float Single.MaxValue
    | "m"
    | "M" -> "a decimal", float Decimal.MaxValue
    | _ -> "a float", Double.MaxValue
  match Double.IsFinite v with
  | false -> Error(SetLiteralError.OutOfRange(sprintf "%s cannot be written as a literal: a literal is a finite number." (v.ToString(CultureInfo.InvariantCulture))))
  | true when abs v > largest ->
    Error(SetLiteralError.OutOfRange(sprintf "%s is outside what %s holds (-%s to %s)." (v.ToString(CultureInfo.InvariantCulture)) typeName (largest.ToString("G6", CultureInfo.InvariantCulture)) (largest.ToString("G6", CultureInfo.InvariantCulture))))
  | true -> Ok()

/// The numbers a type holds, smallest to largest. A record, so the two ends cannot be swapped by mistake.
type NumberRange = { Smallest: float; Largest: float }

/// The whole numbers a number literal's own spelling can hold, lowest and highest, as the slider needs them to know where its
/// ends are. The same facts `formatLiteral` enforces, read from the text a file spells (`100` is an int, `2uy` a byte, `1.0f` a
/// float32, `0x1F` starts at zero). A spelling this module does not recognise as a number has no type limit to report, so it gets
/// the widest a float holds.
let typeRangeOfText (text: string) : NumberRange =
  let widest = { Smallest = -Double.MaxValue; Largest = Double.MaxValue }
  let rec rangeOf (style: LiteralStyle option) : NumberRange =
    match style with
    | Some(LiteralStyle.MeasureStyle(_, inner)) -> rangeOf (Some inner)
    | Some(LiteralStyle.IntStyle(numberBase, suffix, _)) ->
      let _, lowest, highest = integerFacts suffix
      let lowest =
        match numberBase with
        | NumberBase.Dec -> lowest
        | NumberBase.Hex
        | NumberBase.Oct
        | NumberBase.Bin -> max 0L lowest
      { Smallest = float lowest; Largest = float highest }
    | Some(LiteralStyle.FloatStyle(suffix, _)) ->
      let largest =
        match suffix with
        | "f"
        | "F" -> float Single.MaxValue
        | "m"
        | "M" -> float Decimal.MaxValue
        | _ -> Double.MaxValue
      { Smallest = -largest; Largest = largest }
    | Some LiteralStyle.BoolStyle
    | Some LiteralStyle.CharStyle
    | Some LiteralStyle.StringStyle
    | Some LiteralStyle.CaseStyle
    | None -> widest
  rangeOf (styleOf text)

let rec formatLiteral (style: LiteralStyle) (value: LiteralValue) : Result<string, SetLiteralError> =
  match style, value with
  | LiteralStyle.MeasureStyle(unitText, inner), _ ->
    formatLiteral inner value |> Result.map (fun s -> sprintf "%s<%s>" s unitText)
  | LiteralStyle.BoolStyle, LiteralValue.Bool b -> Ok(match b with true -> "true" | false -> "false")
  | LiteralStyle.CaseStyle, LiteralValue.Case name -> Ok name
  | LiteralStyle.CharStyle, LiteralValue.Char c -> Ok(sprintf "'%s'" (escapeChar c))
  | LiteralStyle.StringStyle, LiteralValue.Text s -> Ok(sprintf "\"%s\"" (escapeString s))
  | LiteralStyle.IntStyle(numberBase, suffix, grouped), LiteralValue.Integer v ->
    match checkInteger numberBase suffix v with
    | Error refusal -> Error refusal
    | Ok() -> Ok(formatInteger numberBase suffix grouped v)
  | LiteralStyle.FloatStyle(suffix, trailingDot), LiteralValue.Real v ->
    match checkReal suffix v with
    | Error refusal -> Error refusal
    | Ok() -> Ok(formatFloat suffix trailingDot v)
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
