/// What a person chose for one number in the live bindings pane: where it may go and how far one step moves it. A choice is made
/// per row, kept in `.SageFs/tweak-limits.json` beside the project (so it travels with the code and a team shares it), and obeyed
/// by the slider and by every door that writes a number. Pure: this file reads and writes no disk. The daemon does that, and passes
/// the text in.
///
/// A range here is what the author says is possible. It sits inside what the number's own type holds (`LiteralEdit.typeRangeOfText`),
/// never outside it, and neither is ever written past.
namespace SageFs.Features.Tweak

open System
open System.Collections.Generic
open System.Globalization
open SageFs
open SageFs.Features.Tweak.TweakAddress
open SageFs.Features.Tweak.LiteralEdit

/// One end of the numbers a row may take.
[<RequireQualifiedAccess>]
type Edge =
  /// No end was chosen on this side.
  | Unlimited
  /// The end, and the number is allowed ON it.
  | At of float

/// How far one step moves a number.
[<RequireQualifiedAccess>]
type StepChoice =
  /// From how the author spelled the number: `1.0` steps by 0.1, `0.12` by 0.01, a whole number by 1.
  | FromSpelling
  | Every of float

type NumberLimits =
  { Lowest: Edge
    Highest: Edge
    Step: StepChoice }

/// Why a choice was not accepted. A closed set, so a caller can say which field and why without parsing a message.
[<RequireQualifiedAccess>]
type LimitsRefusal =
  /// The text in a field is not a finite number. `field` is the person's word for it: lowest, highest or step.
  | NotANumber of field: string * given: string
  | LowestAboveHighest of lowest: float * highest: float
  | StepNotPositive of step: float

/// Why the file of choices could not be used. The row is named, so the person knows which entry to fix.
[<RequireQualifiedAccess>]
type LimitsFileFault =
  /// The text is not JSON of the shape the file has.
  | NotReadable of detail: string
  | RowRefused of row: string * refusal: LimitsRefusal

/// The three texts a person typed in the range editor. Empty means none chosen. A record, so the three cannot be swapped by mistake.
type LimitsTexts =
  { LowestText: string
    HighestText: string
    StepText: string }

/// A number a write would have put outside the range chosen for its row.
type RangeRefusal =
  { Row: string
    Value: float
    Lowest: Edge
    Highest: Edge }

module NumberLimits =
  /// No range and the step the spelling gives: how every number starts.
  let unlimited : NumberLimits = { Lowest = Edge.Unlimited; Highest = Edge.Unlimited; Step = StepChoice.FromSpelling }

  let isUnlimited (limits: NumberLimits) : bool = limits = unlimited

  let private number (value: float) : string = value.ToString("R", CultureInfo.InvariantCulture)

  let describe (refusal: LimitsRefusal) : string =
    match refusal with
    | LimitsRefusal.NotANumber(field, given) -> sprintf "The %s is '%s', which is not a number. Type digits, like 0 or 2.5, or leave it empty for none." field given
    | LimitsRefusal.LowestAboveHighest(lowest, highest) ->
      sprintf "The lowest (%s) is above the highest (%s), so no number fits. Lower the lowest or raise the highest." (number lowest) (number highest)
    | LimitsRefusal.StepNotPositive step -> sprintf "A step of %s moves nothing. Use a number above zero." (number step)

  /// A choice from numbers already read. The two ends keep their order and the step is above zero and finite.
  let ofNumbers (lowest: Edge) (highest: Edge) (step: StepChoice) : Result<NumberLimits, LimitsRefusal> =
    match lowest, highest, step with
    | Edge.At low, Edge.At high, _ when low > high -> Error(LimitsRefusal.LowestAboveHighest(low, high))
    | _, _, StepChoice.Every size when not (Double.IsFinite size) || size <= 0.0 -> Error(LimitsRefusal.StepNotPositive size)
    | Edge.At low, _, _ when not (Double.IsFinite low) -> Error(LimitsRefusal.NotANumber("lowest", string low))
    | _, Edge.At high, _ when not (Double.IsFinite high) -> Error(LimitsRefusal.NotANumber("highest", string high))
    | _ -> Ok { Lowest = lowest; Highest = highest; Step = step }

  /// One field's text: empty is "none chosen", anything else must be a finite number.
  let private read (field: string) (text: string) : Result<float option, LimitsRefusal> =
    match text.Trim() with
    | "" -> Ok None
    | trimmed ->
      match Double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture) with
      | true, value when Double.IsFinite value -> Ok(Some value)
      | _ -> Error(LimitsRefusal.NotANumber(field, trimmed))

  /// A choice from the three texts a person typed. An empty text is "no choice" for that field.
  let ofTexts (texts: LimitsTexts) : Result<NumberLimits, LimitsRefusal> =
    match read "lowest" texts.LowestText, read "highest" texts.HighestText, read "step" texts.StepText with
    | Error refusal, _, _
    | _, Error refusal, _
    | _, _, Error refusal -> Error refusal
    | Ok low, Ok high, Ok size ->
      let edge (value: float option) = match value with Some v -> Edge.At v | None -> Edge.Unlimited
      let choice = match size with Some s -> StepChoice.Every s | None -> StepChoice.FromSpelling
      ofNumbers (edge low) (edge high) choice

  /// Whether a number may be written under this choice. Both ends are inside.
  let allows (limits: NumberLimits) (value: float) : bool =
    let above = match limits.Lowest with Edge.At low -> value >= low | Edge.Unlimited -> true
    let below = match limits.Highest with Edge.At high -> value <= high | Edge.Unlimited -> true
    above && below

  /// An end as the text a field holds: nothing for no end.
  let edgeText (edge: Edge) : string =
    match edge with
    | Edge.At value -> number value
    | Edge.Unlimited -> ""

  /// A range said in words, for a refusal: `0 to 30`, `1 or more`, `30 or less`.
  let rangeText (lowest: Edge) (highest: Edge) : string =
    match lowest, highest with
    | Edge.At low, Edge.At high -> sprintf "%s to %s" (number low) (number high)
    | Edge.At low, Edge.Unlimited -> sprintf "%s or more" (number low)
    | Edge.Unlimited, Edge.At high -> sprintf "%s or less" (number high)
    | Edge.Unlimited, Edge.Unlimited -> "any number"

  let stepText (step: StepChoice) : string =
    match step with
    | StepChoice.Every size -> number size
    | StepChoice.FromSpelling -> ""

module RangeRefusal =
  /// The refusal as one sentence: the number, the row, the range.
  let describe (refusal: RangeRefusal) : string =
    sprintf "%s is outside the range chosen for %s (%s)." (refusal.Value.ToString("R", CultureInfo.InvariantCulture)) refusal.Row (NumberLimits.rangeText refusal.Lowest refusal.Highest)

module LimitsFileFault =
  let describe (fault: LimitsFileFault) : string =
    match fault with
    | LimitsFileFault.NotReadable detail -> sprintf "tweak-limits.json could not be read: %s" detail
    | LimitsFileFault.RowRefused(row, refusal) -> sprintf "tweak-limits.json: %s: %s" row (NumberLimits.describe refusal)

/// One entry as the file spells it. A field left out is "no choice". This is the shape of the file, read at the edge and turned into
/// `NumberLimits` at once.
[<CLIMutable>]
type LimitsEntry =
  { Min: Nullable<float>
    Max: Nullable<float>
    Step: Nullable<float> }

/// The row a choice is for is named the way the pane names it: the binding, then `/` and a record field for each step down.
type LimitsBook = Map<string, NumberLimits>

module LimitsBook =
  let empty : LimitsBook = Map.empty

  let private edgeOf (value: Nullable<float>) : Edge = if value.HasValue then Edge.At value.Value else Edge.Unlimited

  let private entryOf (limits: NumberLimits) : LimitsEntry =
    let number (edge: Edge) = match edge with Edge.At v -> Nullable v | Edge.Unlimited -> Nullable()
    { Min = number limits.Lowest
      Max = number limits.Highest
      Step = (match limits.Step with StepChoice.Every size -> Nullable size | StepChoice.FromSpelling -> Nullable()) }

  /// The file's text. Rows in name order, so the file changes only where a choice did and a diff reads.
  let text (book: LimitsBook) : string =
    let entries = SortedDictionary<string, LimitsEntry>(StringComparer.Ordinal)
    for KeyValue(row, limits) in book do
      entries.[row] <- entryOf limits
    Json.serialize (Json.indented (Json.omitNulls Json.camelCase)) entries

  /// Read the file's text. A file that is not JSON, or has an entry no number fits, is refused whole, with the row named: a file read
  /// as "no limits" because it was broken would let a number through that the person had ruled out.
  let parse (fileText: string) : Result<LimitsBook, LimitsFileFault> =
    match Json.deserialize<Dictionary<string, LimitsEntry>> (Json.omitNulls Json.camelCase) fileText with
    | Error error -> Error(LimitsFileFault.NotReadable(JsonError.describe error))
    | Ok entries ->
      entries
      |> Seq.fold
           (fun acc (KeyValue(row, entry)) ->
             match acc with
             | Error _ -> acc
             | Ok book ->
               let step = if entry.Step.HasValue then StepChoice.Every entry.Step.Value else StepChoice.FromSpelling
               match NumberLimits.ofNumbers (edgeOf entry.Min) (edgeOf entry.Max) step with
               | Ok limits -> Ok(Map.add row limits book)
               | Error refusal -> Error(LimitsFileFault.RowRefused(row, refusal)))
           (Ok Map.empty)

  /// The choice for a row: none when nobody made one.
  let forRow (book: LimitsBook) (row: string) : NumberLimits =
    match Map.tryFind row book with
    | Some limits -> limits
    | None -> NumberLimits.unlimited

  /// Keep a choice, or forget the row's when it is no choice at all.
  let set (row: string) (limits: NumberLimits) (book: LimitsBook) : LimitsBook =
    match NumberLimits.isUnlimited limits with
    | true -> Map.remove row book
    | false -> Map.add row limits book

  /// The row a tweak address belongs to, named the way the pane names it. A path through anything but record fields (a list item, a
  /// tuple part) is no row, so no choice applies to it.
  let rowOfAddress (address: TweakAddress) : string option =
    let fields = address.Path |> List.choose (function PathStep.RecordField name -> Some name | _ -> None)
    match fields.Length = address.Path.Length with
    | true -> Some(String.concat "/" (address.BindingName :: fields))
    | false -> None

  /// The number a text spells, when the text is exactly one number literal.
  let numberOf (written: string) : float option =
    match readLiteralText written with
    | Ok(LiteralValue.Integer value, _) -> Some(float value)
    | Ok(LiteralValue.Real value, _) -> Some value
    | _ -> None

  /// Whether writing `written` at `address` keeps the number inside the range chosen for its row. Text that is not a number, and an
  /// address that is not a row, are not the range's business.
  let checkWrite (book: LimitsBook) (address: TweakAddress) (written: string) : Result<unit, RangeRefusal> =
    match rowOfAddress address, numberOf written with
    | Some row, Some value ->
      let limits = forRow book row
      match NumberLimits.allows limits value with
      | true -> Ok()
      | false -> Error { Row = row; Value = value; Lowest = limits.Lowest; Highest = limits.Highest }
    | _ -> Ok()
