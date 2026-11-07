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

/// What a page chose for how the pane looks, beside what the workflow gives: the tab it has open and how each of its rows is shown.
type PageLook = { Tab: WatchTab; Formats: Map<string, FormatChoice> }

module PageLook =
  /// A page that has chosen nothing: the All tab and every row as it is.
  let onAll : PageLook = { Tab = WatchTab.All; Formats = Map.empty }

/// How many pages' choices are kept at once. A choice is a convenience for the page that made it, so the oldest page's goes first.
let [<Literal>] PageCapacity = 256

/// The format each page picked for each row, by row. A page that reloads has a new client id and starts with every row as it is.
let private chosen = PageChoices<Map<string, FormatChoice>>(PageCapacity)

/// The name a row goes by in a request: its binding and the labels down to it, apart by a character no label holds.
let rowId (binding: string) (path: string list) : string = binding :: path |> String.concat "\u001f"

let formatsOf (clientId: string) : Map<string, FormatChoice> =
  match clientId with
  | null
  | "" -> Map.empty
  | id -> chosen.Find(id, Map.empty)

let choose (clientId: string) (row: string) (choice: FormatChoice) : unit =
  match clientId with
  | null
  | "" -> ()
  | id -> chosen.Update(id, Map.empty, Map.add row choice) |> ignore

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

/// The button on a row: its word is the format the row is in, and a press moves to the next one.
let picker (row: string) (offeredChoices: FormatChoice list) (current: FormatChoice) (label: string) : XmlNode =
  let next = nextChoice offeredChoices current
  Elem.button
    [ Attr.class' "session-btn session-btn-labeled live-fmt-btn"
      Attr.type' "button"
      testid "live-fmt-button"
      Attr.create "aria-label" (attrEnc (sprintf "Show %s as %s instead of %s" label (FormatChoice.label next) (FormatChoice.label current)))
      Attr.title (attrEnc (sprintf "Shown as %s. Press for %s." (FormatChoice.label current) (FormatChoice.label next)))
      Ds.onClick (Ds.post (sprintf "%s?r=%s&f=%s" Endpoint (Uri.EscapeDataString row) (FormatChoice.token next))) ]
    [ Elem.span [ Attr.class' "session-btn-word" ] [ textEnc (FormatChoice.label current) ] ]
