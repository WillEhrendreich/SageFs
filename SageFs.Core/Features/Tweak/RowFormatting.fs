/// How a row shows its value (docs/plans/live-bindings-watch-view.md, step 9): as it is, in hex, in binary, as a colour, or on a range.
/// The choice is closed (`FormatChoice`), one function turns a value into what the row draws (`format`), and the one that reads a number
/// back (`unformat`) is its inverse, so a value shown in hex reads back as the number it was. The plan calls this `RowView`; that name is
/// the row's view model in `BindingTweak`, so the choice is a format here.
module SageFs.Features.Tweak.RowFormatting

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
  let all : FormatChoice list = [ FormatChoice.AsIs ]

  let token (choice: FormatChoice) : string = ""

  let label (choice: FormatChoice) : string = ""

  let ofToken (text: string) : Result<FormatChoice, ConfigError> = Ok FormatChoice.AsIs

  /// The choice a format stands for.
  let ofFormat (rowFormat: RowFormat) : FormatChoice = FormatChoice.AsIs

[<RequireQualifiedAccess>]
module FormatRefusal =
  /// The refusal in the words a row shows.
  let describe (refusal: FormatRefusal) : string = ""

/// What a value of this type can be shown as. A range is offered only when the row has bounds to draw it on.
let offered (typeName: string) (range: (float * float) option) : FormatChoice list = [ FormatChoice.AsIs ]

/// The format a choice is for this row now. A choice that no longer applies is the value as it is.
let resolve (choice: FormatChoice) (typeName: string) (range: (float * float) option) : RowFormat = RowFormat.AsIs

/// What the row draws for a value. `preview` is the value's own text, as the walk gives it.
let format (rowFormat: RowFormat) (typeName: string) (preview: string) : Formatted = Formatted.Text preview

/// The number a text spells in this format, as the walk would print it.
let unformat (rowFormat: RowFormat) (typeName: string) (shown: string) : Result<string, FormatRefusal> = Ok shown
