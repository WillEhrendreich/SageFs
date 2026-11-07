/// How a row shows its value (docs/plans/live-bindings-watch-view.md, step 9): as it is, in hex, in binary, as a colour, or on a range.
/// The choice is closed (`FormatChoice`), one function turns a value into what the row draws (`format`), and the one that reads a number
/// back (`unformat`) is its inverse, so a value shown in hex reads back as the number it was. The plan calls this `RowView`; that name is
/// the row's view model in `BindingTweak`, so the choice is a format here.
module SageFs.Features.Tweak.RowFormatting

open System
open System.Globalization
open System.Numerics
open SageFs

/// What a person picks on a row. A slider has no bounds here: they are the row's own range, read when the row is drawn.
[<RequireQualifiedAccess>]
type FormatChoice =
  | AsIs
  | Hex
  | Binary
  | Colour
  | Slider

/// A choice with what it needs to be drawn.
[<RequireQualifiedAccess>]
type RowFormat =
  | AsIs
  | Hex
  | Binary
  | Colour
  | Slider of lowest: float * highest: float

/// Why a value cannot be shown, or a text cannot be read back, in a format. Each case carries what its words need.
[<RequireQualifiedAccess>]
type FormatRefusal =
  /// The format is for whole numbers and the value's type is not one.
  | NeedsWholeNumber of format: FormatChoice * typeName: string
  /// The type is a whole number and the text of the value is not.
  | NotAWholeNumber of format: FormatChoice * shown: string
  /// A colour is a whole number from 0 to 0xFFFFFF.
  | ColourOutOfRange of shown: string
  /// The value is outside the range the row gives.
  | OutsideRange of shown: string * lowest: float * highest: float
  /// A range is for numbers and the text is not one.
  | NotANumber of shown: string
  /// A text read back does not start with the format's prefix (`0x`, `0b`, `#`).
  | MissingPrefix of shown: string * prefix: string
  /// A text read back has characters that are not digits of the format.
  | NotDigits of shown: string * prefix: string
  /// A text read back is a number too wide for the type.
  | DoesNotFit of shown: string * bits: int

/// What the row draws in place of the value's own text.
[<RequireQualifiedAccess>]
type Formatted =
  /// Text, as it is or spelled in another base.
  | Text of text: string
  /// Text with a swatch of the colour it spells, as `#rrggbb`.
  | Swatch of text: string * rgb: string
  /// The value's text with how far along the range it is, from 0 to 1.
  | Meter of text: string * fraction: float
  /// This format does not apply to this value.
  | CannotShow of FormatRefusal

[<RequireQualifiedAccess>]
module FormatChoice =
  let all : FormatChoice list = [ FormatChoice.AsIs; FormatChoice.Hex; FormatChoice.Binary; FormatChoice.Colour; FormatChoice.Slider ]

  let token (choice: FormatChoice) : string =
    match choice with
    | FormatChoice.AsIs -> "as-is"
    | FormatChoice.Hex -> "hex"
    | FormatChoice.Binary -> "binary"
    | FormatChoice.Colour -> "colour"
    | FormatChoice.Slider -> "slider"

  let label (choice: FormatChoice) : string =
    match choice with
    | FormatChoice.AsIs -> "As is"
    | FormatChoice.Hex -> "Hex"
    | FormatChoice.Binary -> "Binary"
    | FormatChoice.Colour -> "Colour"
    | FormatChoice.Slider -> "Slider"

  let ofToken (text: string) : Result<FormatChoice, ConfigError> =
    match all |> List.tryFind (fun choice -> token choice = text) with
    | Some choice -> Ok choice
    | None -> Error(NotAMember(text, all |> List.map token))

  /// The choice a format stands for.
  let ofFormat (rowFormat: RowFormat) : FormatChoice =
    match rowFormat with
    | RowFormat.AsIs -> FormatChoice.AsIs
    | RowFormat.Hex -> FormatChoice.Hex
    | RowFormat.Binary -> FormatChoice.Binary
    | RowFormat.Colour -> FormatChoice.Colour
    | RowFormat.Slider _ -> FormatChoice.Slider

[<RequireQualifiedAccess>]
module FormatRefusal =
  /// The highest number a colour can be: white, `#FFFFFF`.
  let [<Literal>] ColourLimit = 0xFFFFFF

  /// The refusal in the words a row shows.
  let describe (refusal: FormatRefusal) : string =
    match refusal with
    | FormatRefusal.NeedsWholeNumber(format, typeName) -> sprintf "%s is for whole numbers, and this is a %s" (FormatChoice.label format) typeName
    | FormatRefusal.NotAWholeNumber(format, shown) -> sprintf "%s needs a whole number, and this is %s" (FormatChoice.label format) shown
    | FormatRefusal.ColourOutOfRange shown -> sprintf "a colour is a whole number from 0 to %d, and this is %s" ColourLimit shown
    | FormatRefusal.OutsideRange(shown, lowest, highest) -> sprintf "%s is outside the range %g to %g" shown lowest highest
    | FormatRefusal.NotANumber shown -> sprintf "a slider is for numbers, and this is %s" shown
    | FormatRefusal.MissingPrefix(shown, prefix) -> sprintf "%s should start with %s" shown prefix
    | FormatRefusal.NotDigits(shown, prefix) -> sprintf "%s is not a number written with %s" shown prefix
    | FormatRefusal.DoesNotFit(shown, bits) -> sprintf "%s does not fit in %d bits" shown bits

/// A whole-number type: how many bits it has and whether it is signed.
type IntegerKind = { Bits: int; Signed: bool }

/// The integer types a row can spell in another base, by their .NET names.
let integerKind (typeName: string) : IntegerKind option =
  match typeName with
  | "Byte" -> Some { Bits = 8; Signed = false }
  | "SByte" -> Some { Bits = 8; Signed = true }
  | "Int16" -> Some { Bits = 16; Signed = true }
  | "UInt16" -> Some { Bits = 16; Signed = false }
  | "Int32" -> Some { Bits = 32; Signed = true }
  | "UInt32" -> Some { Bits = 32; Signed = false }
  | "Int64" -> Some { Bits = 64; Signed = true }
  | "UInt64" -> Some { Bits = 64; Signed = false }
  | _ -> None

/// The types that have a fractional part, which a range can be drawn for.
let isFractional (typeName: string) : bool =
  match typeName with
  | "Single"
  | "Double"
  | "Decimal" -> true
  | _ -> false

/// What a value of this type can be shown as. A range is offered only when the row has bounds to draw it on.
let offered (typeName: string) (range: (float * float) option) : FormatChoice list =
  let slider =
    match range with
    | Some _ -> [ FormatChoice.Slider ]
    | None -> []
  match integerKind typeName, isFractional typeName with
  | Some _, _ -> [ FormatChoice.AsIs; FormatChoice.Hex; FormatChoice.Binary; FormatChoice.Colour ] @ slider
  | None, true -> FormatChoice.AsIs :: slider
  | None, false -> [ FormatChoice.AsIs ]

/// The format a choice is for this row now. A choice that no longer applies (the type changed, or the range was cleared) is the value as
/// it is, so a row never fails to draw because of a pick made earlier.
let resolve (choice: FormatChoice) (typeName: string) (range: (float * float) option) : RowFormat =
  match List.contains choice (offered typeName range), choice, range with
  | false, _, _ -> RowFormat.AsIs
  | true, FormatChoice.AsIs, _ -> RowFormat.AsIs
  | true, FormatChoice.Hex, _ -> RowFormat.Hex
  | true, FormatChoice.Binary, _ -> RowFormat.Binary
  | true, FormatChoice.Colour, _ -> RowFormat.Colour
  | true, FormatChoice.Slider, Some(lowest, highest) -> RowFormat.Slider(lowest, highest)
  | true, FormatChoice.Slider, None -> RowFormat.AsIs

let private parseInteger (shown: string) : BigInteger option =
  match BigInteger.TryParse(shown.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
  | true, n -> Some n
  | false, _ -> None

let private spanOf (kind: IntegerKind) : BigInteger = BigInteger.Pow(BigInteger 2, kind.Bits)

/// The bits of a value in its type's width: a negative number is its two's complement, so `-1` as an Int32 is 32 ones.
let private bitsOf (kind: IntegerKind) (n: BigInteger) : BigInteger =
  match n.Sign < 0 with
  | true -> n + spanOf kind
  | false -> n

let private fitsKind (kind: IntegerKind) (n: BigInteger) : bool =
  let span = spanOf kind
  match kind.Signed with
  | true -> n >= -(span / BigInteger 2) && n < span / BigInteger 2
  | false -> n >= BigInteger.Zero && n < span

let private digitsOf (radix: int) (n: BigInteger) : string =
  match radix with
  | 16 ->
    match n.ToString("X", CultureInfo.InvariantCulture).TrimStart('0') with
    | "" -> "0"
    | text -> text
  | _ ->
    let rec bits (rest: BigInteger) (acc: char list) : char list =
      match rest.IsZero with
      | true -> acc
      | false -> bits (rest / BigInteger 2) ((match (rest % BigInteger 2).IsZero with | true -> '0' | false -> '1') :: acc)
    match bits n [] with
    | [] -> "0"
    | chars -> String(Array.ofList chars)

/// What the row draws for a value. `preview` is the value's own text, as the walk gives it.
let format (rowFormat: RowFormat) (typeName: string) (preview: string) : Formatted =
  let wholeNumber (choice: FormatChoice) (spell: IntegerKind -> BigInteger -> Formatted) : Formatted =
    match integerKind typeName, parseInteger preview with
    | Some kind, Some n when fitsKind kind n -> spell kind n
    | Some _, _ -> Formatted.CannotShow(FormatRefusal.NotAWholeNumber(choice, preview))
    | None, _ -> Formatted.CannotShow(FormatRefusal.NeedsWholeNumber(choice, typeName))
  match rowFormat with
  | RowFormat.AsIs -> Formatted.Text preview
  | RowFormat.Hex -> wholeNumber FormatChoice.Hex (fun kind n -> Formatted.Text("0x" + digitsOf 16 (bitsOf kind n)))
  | RowFormat.Binary -> wholeNumber FormatChoice.Binary (fun kind n -> Formatted.Text("0b" + digitsOf 2 (bitsOf kind n)))
  | RowFormat.Colour ->
    wholeNumber FormatChoice.Colour (fun _ n ->
      match n >= BigInteger.Zero && n <= BigInteger FormatRefusal.ColourLimit with
      | true ->
        let rgb = (int n).ToString("X6", CultureInfo.InvariantCulture)
        Formatted.Swatch("#" + rgb, "#" + rgb.ToLowerInvariant())
      | false -> Formatted.CannotShow(FormatRefusal.ColourOutOfRange preview))
  | RowFormat.Slider(lowest, highest) ->
    match Double.TryParse(preview.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, value when value >= lowest && value <= highest && highest > lowest -> Formatted.Meter(preview, (value - lowest) / (highest - lowest))
    | true, _ -> Formatted.CannotShow(FormatRefusal.OutsideRange(preview, lowest, highest))
    | false, _ -> Formatted.CannotShow(FormatRefusal.NotANumber preview)

/// The value of a digit in a base up to 16, or nothing for a character that is not one.
let private digitValue (radix: int) (c: char) : int option =
  let value =
    match c with
    | _ when c >= '0' && c <= '9' -> Some(int c - int '0')
    | _ when c >= 'a' && c <= 'f' -> Some(int c - int 'a' + 10)
    | _ when c >= 'A' && c <= 'F' -> Some(int c - int 'A' + 10)
    | _ -> None
  value |> Option.filter (fun v -> v < radix)

/// The number a text spells in this format, as the walk would print it: the inverse of `format` for hex, binary and colour. Text that is
/// not in the format, or does not fit the type, is refused with the reason.
let unformat (rowFormat: RowFormat) (typeName: string) (shown: string) : Result<string, FormatRefusal> =
  let fromDigits (choice: FormatChoice) (prefix: string) (radix: int) : Result<string, FormatRefusal> =
    match integerKind typeName with
    | None -> Error(FormatRefusal.NeedsWholeNumber(choice, typeName))
    | Some kind ->
      let body = shown.Trim()
      match body.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) with
      | false -> Error(FormatRefusal.MissingPrefix(body, prefix))
      | true ->
        let digits = body.Substring prefix.Length
        let parsed =
          match digits.Length with
          | 0 -> None
          | _ ->
            digits
            |> Seq.fold (fun acc c -> acc |> Option.bind (fun v -> digitValue radix c |> Option.map (fun d -> v * BigInteger radix + BigInteger d))) (Some BigInteger.Zero)
        match parsed with
        | None -> Error(FormatRefusal.NotDigits(body, prefix))
        | Some value when value >= spanOf kind -> Error(FormatRefusal.DoesNotFit(body, kind.Bits))
        | Some value ->
          let signed =
            match kind.Signed && value >= spanOf kind / BigInteger 2 with
            | true -> value - spanOf kind
            | false -> value
          Ok(signed.ToString CultureInfo.InvariantCulture)
  match rowFormat with
  | RowFormat.AsIs -> Ok shown
  | RowFormat.Hex -> fromDigits FormatChoice.Hex "0x" 16
  | RowFormat.Binary -> fromDigits FormatChoice.Binary "0b" 2
  | RowFormat.Colour -> fromDigits FormatChoice.Colour "#" 16
  | RowFormat.Slider _ -> Ok shown
