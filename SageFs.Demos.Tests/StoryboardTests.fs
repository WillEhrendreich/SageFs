/// Proves `Storyboard.svg` (demo-gif-plan.md §4.8, §5) against the §6.1
/// worked scenario: it renders well-formed SVG carrying one caption per step
/// and one pane rect per actor in the resolved `Layout.rects` map, for every
/// `LayoutTemplate`, with no launch and no sandbox.
module SageFs.Demos.Tests.StoryboardTests

open System.Xml.Linq
open Expecto
open Expecto.Flip
open SageFs.Demos.Domain

let private screen = { Width = 1280; Height = 720 }

/// The flagship demo from demo-gif-plan.md §6.1, copied here (rather than
/// referencing `DomainTests`) so this test file has no dependency on another
/// test file's private binding.
let private hrDashboardVscodeWeb : Scenario =
  { Id = ScenarioId.derive Capability.HotReload Client.VsCode AppKind.Web
    Capability = Capability.HotReload
    Client = Client.VsCode
    App = AppKind.Web
    Sample = Sample.WebappDatastar
    Layout = LayoutTemplate.EditorLeft
    Cost = CostClass.web
    Masks = [ Region.clock ]
    Steps =
      [ { Caption = Caption.mk "Run the web app"
          Action = Action.Setup (ClientCommand.OpenFile SampleFile.homePage)
          Expect = Expectation.EditorSaved SampleFile.homePage
          Dwell = Dwell.short }
        { Caption = Caption.mk "1/4 · Press Run on the session card"
          Action = Action.Click (Target.DashboardElement DashboardId.runApp)
          Expect = Expectation.AppState AppRunStateCase.Running
          Dwell = Dwell.medium }
        { Caption = Caption.mk "2/4 · Change the heading"
          Action =
            Action.Type (
              Target.EditorPosition (SampleFile.homePage, 12, 20),
              Text.mk "SageFs is live",
              CadenceSeed.ofId "hr-dashboard-vscode-web"
            )
          Expect = Expectation.EditorSaved SampleFile.homePage
          Dwell = Dwell.short }
        { Caption = Caption.mk "3/4 · Save"
          Action = Action.Chord [ Key.Ctrl; Key.S ]
          Expect = Expectation.EditorSaved SampleFile.homePage
          Dwell = Dwell.short }
        { Caption = Caption.mk "4/4 · The running site repaints — no reload"
          Action = Action.Await Signal.appOutputChanged
          Expect = Expectation.AppOutputChanged Region.appHeading
          Dwell = Dwell.long } ] }

let private parse (Svg text) = XDocument.Parse text

let private paneRectElements (doc: XDocument) =
  let ns = doc.Root.Name.Namespace
  doc.Descendants(ns + "rect") |> Seq.filter (fun e -> e.Attribute(XName.Get "data-actor") <> null) |> Seq.toList

let private captionTextElements (doc: XDocument) =
  let ns = doc.Root.Name.Namespace
  doc.Descendants(ns + "text") |> Seq.toList

[<Tests>]
let tests =
  testList "Storyboard" [

    testCase "svg produces well-formed XML for the §6.1 worked scenario" <| fun _ ->
      let (Svg text) = SageFs.Demos.Storyboard.svg hrDashboardVscodeWeb screen Style.kanagawa
      let parsed = try Some(XDocument.Parse text) with _ -> None
      parsed.IsSome |> Expect.isTrue "the storyboard SVG should be well-formed XML"

    testCase "svg contains exactly one caption per step, in order" <| fun _ ->
      let doc = SageFs.Demos.Storyboard.svg hrDashboardVscodeWeb screen Style.kanagawa |> parse
      let captions = captionTextElements doc
      captions.Length
      |> Expect.equal "one <text> caption per step" hrDashboardVscodeWeb.Steps.Length
      let captionTexts = captions |> List.map (fun e -> e.Value)
      for step in hrDashboardVscodeWeb.Steps do
        let expected = Caption.value step.Caption
        captionTexts
        |> List.exists (fun t -> t.Contains(expected))
        |> Expect.isTrue (sprintf "caption '%s' should appear somewhere in the SVG" expected)

    testCase "svg contains one pane rect per actor in the resolved layout, for every LayoutTemplate" <| fun _ ->
      for template in [ LayoutTemplate.EditorLeft; LayoutTemplate.EditorFull; LayoutTemplate.DashboardOnly ] do
        let scenario = { hrDashboardVscodeWeb with Layout = template }
        let doc = SageFs.Demos.Storyboard.svg scenario screen Style.kanagawa |> parse
        let expectedCount = (SageFs.Demos.Layout.rects template screen).Count
        paneRectElements doc |> List.length
        |> Expect.equal (sprintf "%A should produce %d pane rects" template expectedCount) expectedCount

    testCase "svg pane rects match Layout.rects exactly for the worked scenario" <| fun _ ->
      let doc = SageFs.Demos.Storyboard.svg hrDashboardVscodeWeb screen Style.kanagawa |> parse
      let expected =
        SageFs.Demos.Layout.rects hrDashboardVscodeWeb.Layout screen
        |> Map.toList
        |> List.map snd
        |> List.distinct
        |> List.sortBy (fun r -> r.X, r.Y, r.W, r.H)
      let actual =
        paneRectElements doc
        |> List.map (fun e ->
          { X = int (e.Attribute(XName.Get "x").Value)
            Y = int (e.Attribute(XName.Get "y").Value)
            W = int (e.Attribute(XName.Get "width").Value)
            H = int (e.Attribute(XName.Get "height").Value) })
        |> List.distinct
        |> List.sortBy (fun r -> r.X, r.Y, r.W, r.H)
      actual |> Expect.equal "the SVG's distinct pane rects equal Layout.rects's distinct rects" expected
  ]
