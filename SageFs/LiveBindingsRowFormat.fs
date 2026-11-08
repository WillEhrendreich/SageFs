/// The picker on a row of the live bindings pane (docs/plans/live-bindings-watch-view.md, step 9): a button that shows a number as it is, in
/// hex, in binary, as a colour or on its range, each press moving to the next format the value can be shown in. The formats and the
/// function that draws them are `RowFormatting` (pure, in Core); this remembers the choice for a page and draws the value and its button.
module SageFs.Server.LiveBindingsRowFormat

open System
open Falco.Markup
open Falco.Datastar
open SageFs
open SageFs.Features.Tweak.RowFormatting
open SageFs.Server.DashboardFragments

let [<Literal>] Endpoint = "/dashboard/row-format"

/// The name a row goes by in a request: its binding and the labels down to it, apart by a character no label holds.
let rowId (binding: string) (path: string list) : string = binding :: path |> String.concat "\u001f"

/// The format a press moves a row to: the one after the current in the order the value offers them, and the first again after the last.
/// A current format the value no longer offers moves to the first.
let nextChoice (offeredChoices: FormatChoice list) (current: FormatChoice) : FormatChoice =
  match offeredChoices |> List.tryFindIndex (fun c -> c = current) with
  | Some index -> offeredChoices.[(index + 1) % offeredChoices.Length]
  | None -> List.head offeredChoices

/// The range a row's limits give, when both ends are chosen.
let rangeOf (limits: Features.Tweak.NumberLimits) : (float * float) option =
  match limits.Lowest, limits.Highest with
  | Features.Tweak.Edge.At lowest, Features.Tweak.Edge.At highest when highest > lowest -> Some(lowest, highest)
  | Features.Tweak.Edge.At _, _
  | Features.Tweak.Edge.Unlimited, _ -> None

/// How a row is shown now, and what a press would show next.
type RowShown =
  { Offered: FormatChoice list
    Current: RowFormat }

/// What a leaf of this type can be shown as, and what it is shown as given the choice the page made for it. A type with nothing else to
/// show has no picker.
let shownFor (formats: Map<string, FormatChoice>) (row: string) (typeName: string) (range: (float * float) option) : RowShown option =
  match offered typeName range with
  | []
  | [ _ ] -> None
  | choices ->
    let picked = Map.tryFind row formats |> Option.defaultValue FormatChoice.AsIs
    Some { Offered = choices; Current = resolve picked typeName range }

/// The value cell for a leaf in its format: the text, and for a colour its swatch and for a range its bar. A format that cannot show the
/// value shows the value as it is, with the reason beside it, in words.
let valueNodes (cssClass: string) (rowFormat: RowFormat) (typeName: string) (preview: string) : XmlNode list =
  let cell (text: string) =
    Elem.span [ Attr.class' cssClass; Attr.create "title" (attrEnc preview) ] [ textEnc (sprintf "= %s" text) ]
  match format rowFormat typeName preview with
  | Formatted.Text text -> [ cell text ]
  | Formatted.Swatch(text, rgb) ->
    [ cell text
      Elem.span
        [ Attr.class' "live-swatch"
          testid "live-swatch"
          Attr.create "aria-hidden" "true"
          Attr.create "style" (sprintf "background-color: %s" (attrEnc rgb)) ]
        [] ]
  | Formatted.Meter(text, fraction) ->
    let percent = (fraction * 100.0).ToString("0.##", Globalization.CultureInfo.InvariantCulture)
    [ cell text
      Elem.span
        [ Attr.class' "live-meter"
          testid "live-meter"
          Attr.create "role" "meter"
          Attr.create "aria-valuemin" "0"
          Attr.create "aria-valuemax" "100"
          Attr.create "aria-valuenow" percent
          Attr.create "aria-label" (attrEnc (sprintf "%s on its range" text)) ]
        [ Elem.span [ Attr.class' "live-meter-fill"; Attr.create "style" (sprintf "width: %s%%" percent) ] [] ] ]
  | Formatted.CannotShow refusal ->
    [ cell preview
      Elem.span [ Attr.class' "live-fmt-note"; testid "live-fmt-note" ] [ textEnc (FormatRefusal.describe refusal) ] ]

/// The words that say how a row is shown now, beside the button that says what a press does. A row shown as it is says nothing.
let stateNote (current: FormatChoice) : XmlNode list =
  match current with
  | FormatChoice.AsIs -> []
  | FormatChoice.Hex
  | FormatChoice.Binary
  | FormatChoice.Colour
  | FormatChoice.Slider ->
    [ Elem.span [ Attr.class' "live-fmt-state"; testid "live-fmt-state" ] [ textEnc (sprintf "shown as %s" (FormatChoice.label current)) ] ]

/// The button on a row: its word is what a press does, which is to show the value as the next format. The state is `stateNote`'s words.
let picker (row: string) (offeredChoices: FormatChoice list) (current: FormatChoice) (label: string) : XmlNode =
  let next = nextChoice offeredChoices current
  // the words that follow "as": the format's name, or "it is" for the value as it is, so a press never reads "Show as As is"
  let nextWords =
    match next with
    | FormatChoice.AsIs -> "it is"
    | FormatChoice.Hex
    | FormatChoice.Binary
    | FormatChoice.Colour
    | FormatChoice.Slider -> FormatChoice.label next
  Elem.button
    [ Attr.class' "session-btn session-btn-labeled live-fmt-btn"
      Attr.type' "button"
      testid "live-fmt-button"
      Attr.create "aria-label" (attrEnc (sprintf "Show %s as %s instead of %s" label nextWords (FormatChoice.label current)))
      Attr.title (attrEnc (sprintf "Shown as %s. Press to show it as %s." (FormatChoice.label current) nextWords))
      Ds.onClick (Ds.post (sprintf "%s?r=%s&f=%s" Endpoint (Uri.EscapeDataString row) (FormatChoice.token next))) ]
    [ Elem.span [ Attr.class' "session-btn-word" ] [ textEnc (sprintf "Show as %s" nextWords) ] ]
