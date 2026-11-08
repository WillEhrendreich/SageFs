/// The picker on a row of the live bindings pane: which rows carry one, what a press asks for, how each format is drawn, and the route that
/// keeps the page's choice. The formats themselves are `RowFormatTests`; this is the markup and the request around them.
module SageFs.Tests.LiveBindingsRowFormatTests

open System
open System.Text.Json
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Features
open SageFs.Features.LiveBindingsPane
open SageFs.Features.LiveValueTree
open SageFs.Features.Tweak.RowFormatting
open SageFs.Server
open SageFs.Server.DockPanes
open SageFs.Server.LiveBindingsDock
open SageFs.Server.LiveBindingsRowFormat
open SageFs.Server.LiveBindingsRowFormatRoute

let private decoded (node: XmlNode) = Net.WebUtility.HtmlDecode(renderNode node)

let private decodedAll (nodes: XmlNode list) = nodes |> List.map decoded |> String.concat ""

let private leaf (label: string) (typeName: string) (preview: string) : LiveValueNode =
  { Label = label
    TypeName = typeName
    Preview = preview
    Kind = NodeKind.Leaf
    Children = []
    BestEffort = false
    Assign = Assignment.NotKnown
    Depth = 0 }

let private paneOf (leaves: LiveValueNode list) : PaneView =
  { Snapshot =
      { SessionId = "abcd1234"
        Generation = 1L
        Bindings = leaves |> List.map (fun n -> { Name = n.Label; TypeSignature = n.TypeName; Root = n })
        Truncated = false
        CapturedAt = DateTimeOffset.UnixEpoch }
    Notes = { Mode = ValueWalk.standard; Click = NoClickYet } }

let private count (needle: string) (html: string) = html.Split([| needle |], StringSplitOptions.None).Length - 1

[<Tests>]
let tests =
  testList "the picker on a row" [

    testCase "a press moves to the next format the value offers, wraps to the first, and a format the value no longer offers moves to the first" <| fun _ ->
      let wholeNumber = [ FormatChoice.AsIs; FormatChoice.Hex; FormatChoice.Binary; FormatChoice.Colour ]
      nextChoice wholeNumber FormatChoice.AsIs |> Expect.equal "as is to hex" FormatChoice.Hex
      nextChoice wholeNumber FormatChoice.Colour |> Expect.equal "the last wraps to the first" FormatChoice.AsIs
      nextChoice wholeNumber FormatChoice.Slider |> Expect.equal "a format the value does not offer starts again" FormatChoice.AsIs

    testProperty "WHY — pressing as many times as there are formats comes back to where it started" <| fun (start: byte) ->
      let offeredChoices = [ FormatChoice.AsIs; FormatChoice.Hex; FormatChoice.Binary; FormatChoice.Colour ]
      let from = offeredChoices.[int start % offeredChoices.Length]
      [ 1 .. offeredChoices.Length ] |> List.fold (fun current _ -> nextChoice offeredChoices current) from = from

    testCase "only a value with something else to show gets a picker: a whole number does, a Double and a string do not, a Double with a range does" <| fun _ ->
      let formats = Map.empty
      shownFor formats "r" "Int32" None |> Expect.isSome "a whole number"
      shownFor formats "r" "Double" None |> Expect.isNone "a Double has only itself"
      shownFor formats "r" "String" None |> Expect.isNone "a string has only itself"
      shownFor formats "r" "Double" (Some(0.0, 1.0)) |> Expect.isSome "a Double with a range can be a bar"

    testCase "a row is as it is until the page picked, and a pick the value no longer offers draws as it is" <| fun _ ->
      let picked = Map.ofList [ "r", FormatChoice.Hex ]
      (shownFor Map.empty "r" "Int32" None).Value.Current |> Expect.equal "nothing picked" RowFormat.AsIs
      (shownFor picked "r" "Int32" None).Value.Current |> Expect.equal "hex picked" RowFormat.Hex
      (shownFor picked "other" "Int32" None).Value.Current |> Expect.equal "another row is untouched" RowFormat.AsIs
      (shownFor (Map.ofList [ "r", FormatChoice.Slider ]) "r" "Int32" None).Value.Current |> Expect.equal "a slider with no range is as it is" RowFormat.AsIs

    testCase "each format is drawn from its own piece of markup: text, a swatch with its colour, a bar with its fraction, a note with the reason" <| fun _ ->
      let html (rowFormat: RowFormat) (typeName: string) (preview: string) = valueNodes "live-preview" rowFormat typeName preview |> decodedAll
      html RowFormat.Hex "Int32" "255" |> Expect.stringContains "hex text" "= 0xFF"
      let colour = html RowFormat.Colour "Int32" "16711680"
      colour |> Expect.stringContains "colour text" "= #FF0000"
      colour |> Expect.stringContains "the swatch" "data-testid=\"live-swatch\""
      colour |> Expect.stringContains "its colour" "background-color: #ff0000"
      let bar = html (RowFormat.Slider(0.0, 100.0)) "Double" "25"
      bar |> Expect.stringContains "the bar names its place on the range" "aria-valuenow=\"25\""
      bar |> Expect.stringContains "the fill's width" "width: 25%"
      let refused = html RowFormat.Colour "Int32" "20000000"
      refused |> Expect.stringContains "the value as it is" "= 20000000"
      refused |> Expect.stringContains "the reason, in words" "16777215"
      count "live-swatch" refused |> Expect.equal "and no swatch" 0

    testCase "WHY — the button's word is what a press does (the next format), and the state is its own words: 'Hex' alone read as the action" <| fun _ ->
      let button = picker (rowId "flags" []) [ FormatChoice.AsIs; FormatChoice.Hex ] FormatChoice.AsIs "flags" |> decoded
      button |> Expect.stringContains "the word is the action" ">Show as Hex<"
      button |> Expect.stringContains "the accessible name" "Show flags as Hex instead of As is"
      button |> Expect.stringContains "the next format in the request" "f=hex"
      button |> Expect.stringContains "the row in the request" "r=flags"
      let inHex = picker (rowId "flags" []) [ FormatChoice.AsIs; FormatChoice.Hex; FormatChoice.Binary ] FormatChoice.Hex "flags" |> decoded
      inHex |> Expect.stringContains "in hex the press is for binary" ">Show as Binary<"
      inHex |> Expect.stringContains "and the tooltip says both" "Shown as Hex. Press to show it as Binary."
      (stateNote FormatChoice.AsIs |> decodedAll).Contains "live-fmt-state" |> Expect.isFalse "a row shown as it is names no format"
      let stateHtml = stateNote FormatChoice.Hex |> decodedAll
      stateHtml |> Expect.stringContains "the state is words" ">shown as Hex<"
      stateHtml |> Expect.stringContains "and is found by its test id" "data-testid=\"live-fmt-state\""

    testCase "WHY — a changed value's highlight is an underline that fades, never a background fill the text has to sit on: the fill made dim text unreadable" <| fun _ ->
      let script = flashScript () |> decoded
      script |> Expect.stringContains "an inset underline" "boxShadow"
      script.Contains "{ backgroundColor: accent }" |> Expect.isFalse "no full-strength background start"

    testCase "WHY — a row in the dock carries a picker when its value can be spelled another way, and carries none otherwise" <| fun _ ->
      let html = renderDock SessionInView "abcd1234" (WalkedBindings(paneOf [ leaf "flags" "Int32" "255"; leaf "gravity" "Double" "9.8"; leaf "name" "String" "\"Ada\"" ])) |> decoded
      count "data-testid=\"live-fmt-button\"" html |> Expect.equal "one picker, on the whole number" 1

    testCase "the request names the page, the row and a format; a token that is not a format is refused with the set" <| fun _ ->
      let body (clientId: string) = JsonDocument.Parse(sprintf "{\"clientId\":\"%s\"}" clientId).RootElement
      askedOf "flags" "hex" (body "page-1") |> Expect.equal "a request" (FormatAsked.Asked("page-1", "flags", FormatChoice.Hex))
      match askedOf "flags" "octal" (body "page-1") with
      | FormatAsked.NotAsked why -> why |> Expect.stringContains "names the word and the set" "octal"
      | other -> failtestf "octal was accepted: %A" other
      askedOf "flags" "hex" (body "") |> function | FormatAsked.NotAsked _ -> () | other -> failtestf "a request with no page: %A" other
      askedOf "" "hex" (body "page-1") |> function | FormatAsked.NotAsked _ -> () | other -> failtestf "a request with no row: %A" other

    testCase "a page's choice is kept for that page and for no other, and a page that never chose has every row as it is" <| fun _ ->
      let page = sprintf "page-%s" (Guid.NewGuid().ToString("N"))
      let other = sprintf "page-%s" (Guid.NewGuid().ToString("N"))
      let pages = SageFs.Server.PageState.PageStore(8)
      pages.SetFormat(page, rowId "flags" [], FormatChoice.Binary)
      pages.SetFormat(page, rowId "tint" [], FormatChoice.Colour)
      (pages.Look page).Formats |> Map.find (rowId "flags" []) |> Expect.equal "the first row kept" FormatChoice.Binary
      (pages.Look page).Formats |> Map.find (rowId "tint" []) |> Expect.equal "the second row kept, the first not lost" FormatChoice.Colour
      (pages.Look other).Formats |> Expect.equal "another page has chosen nothing" Map.empty
      (pages.Look "").Formats |> Expect.equal "no page id, no choices" Map.empty
  ]
