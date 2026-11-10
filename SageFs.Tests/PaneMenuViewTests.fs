/// The page drawn from a pane layout: the movable panels in the regions the layout names and in its order, a closed panel nowhere
/// on the page and on the menu with a way back, and every button of the Panels menu posting an edit the daemon can read, with a
/// name a screen reader can say. The default layout draws every panel where the page always drew it.
module SageFs.Tests.PaneMenuViewTests

open System
open System.Text.RegularExpressions
open Expecto
open Expecto.Flip
open Falco.Markup
open SageFs
open SageFs.Server
open SageFs.Server.DashboardPanelContext
open SageFs.Server.DashboardTypes
open SageFs.Server.DashboardFragments
open SageFs.Server.PaneMenuView
open SageFs.Tests.DashboardPanelVisibilityTests
open SageFs.Tests.PaneLayoutTests

/// A panel drawn as a marker, so the tests read placement and order and not the real panels' markup.
let markerId (pane: PanelId) : string = "marker-" + PanelId.token pane

let marker (pane: PanelId) : XmlNode =
  Elem.div [ Attr.id (markerId pane); Attr.class' "panel" ] [ Text.enc (PanelId.label pane) ]

let markedSnapshot : DashboardSnapshot =
  { snapshotWith off [] with
      HotReloadPanel = marker PanelId.HotReload
      LiveTestingPanel = marker PanelId.LiveTesting
      SessionContextPanel = marker PanelId.SessionContext
      FrictionPanel = marker PanelId.Friction
      CohortPanel = marker PanelId.AgentTeam
      HygienePanel = marker PanelId.Hygiene }

let viewAfter (workflow: LayoutWorkflow) (edits: BookEdit list) : PaneLayoutView =
  let read : PaneLayoutRead = { Book = LayoutBook.read (layerAfter edits); Reading = FileReading.Readable }
  PaneLayoutView.ofRead workflow read replFacts

let htmlOf (view: PaneLayoutView) : string = renderMainContentIn view markedSnapshot |> renderNode

let indexOfId (html: string) (id: string) : int = html.IndexOf(sprintf "id=\"%s\"" id, StringComparison.Ordinal)

let edit (workflow: LayoutWorkflow) (inner: LayoutEdit) : BookEdit = BookEdit.InWorkflow(workflow, inner)

let repl = LayoutWorkflow.Repl

let postedEdits (html: string) : string list =
  [ for m in Regex.Matches(html, @"@post\(&#39;/dashboard/layout\?e=([^&]*)&#39;\)|@post\('/dashboard/layout\?e=([^']*)'\)") ->
      Uri.UnescapeDataString(if m.Groups.[1].Success then m.Groups.[1].Value else m.Groups.[2].Value) ]

[<Tests>]
let paneMenuViewTests =
  testList "Pane layout on the page and in the Panels menu" [

    testCase "the default layout draws every panel in the sidebar in the order the page always drew them, and no dock region" <| fun _ ->
      let html = htmlOf PaneLayoutView.today
      let sidebar = html.IndexOf("class=\"sidebar-inner\"", StringComparison.Ordinal)
      Expect.isGreaterThan "the sidebar is on the page" (sidebar, 0)
      let positions = PanelId.all |> List.map (fun pane -> indexOfId html (markerId pane))
      positions |> List.forall (fun at -> at > sidebar) |> Expect.isTrue "every panel is inside the sidebar"
      positions |> Expect.equal "in the default order" (List.sort positions)
      indexOfId html "pane-dock" |> Expect.equal "no dock region is drawn when nothing is in it" -1

    testCase "a panel moved to the dock is drawn under the output, in the dock region, and not in the sidebar" <| fun _ ->
      let view = viewAfter repl [ edit repl (LayoutEdit.MoveToRegion(PanelId.AgentTeam, PaneRegion.Dock)) ]
      let html = htmlOf view
      let dock = indexOfId html "pane-dock"
      let sidebar = html.IndexOf("class=\"sidebar-inner\"", StringComparison.Ordinal)
      Expect.isGreaterThan "the dock region is drawn" (dock, 0)
      let cohort = indexOfId html (markerId PanelId.AgentTeam)
      Expect.isGreaterThan "the panel is inside the dock region" (cohort, dock)
      Expect.isLessThan "and the dock region comes before the sidebar" (cohort, sidebar)
      Regex.Matches(html, sprintf "id=\"%s\"" (markerId PanelId.AgentTeam)).Count |> Expect.equal "the panel is on the page once" 1

    testCase "the panels of a region are drawn in the layout's order, and a move up changes it" <| fun _ ->
      let view = viewAfter repl [ edit repl (LayoutEdit.MoveUp PanelId.Hygiene) ]
      let html = htmlOf view
      Expect.isLessThan "hygiene is now above cohort"
        (indexOfId html (markerId PanelId.Hygiene), indexOfId html (markerId PanelId.AgentTeam))

    testCase "a closed panel is nowhere on the page and is on the menu, closed, with a Reopen button" <| fun _ ->
      let view = viewAfter repl [ edit repl (LayoutEdit.Close PanelId.Friction) ]
      let html = htmlOf view
      indexOfId html (markerId PanelId.Friction) |> Expect.equal "not on the page" -1
      html |> Expect.stringContains "its row is listed as closed" "data-pane=\"friction\" data-place=\"closed\""
      html |> Expect.stringContains "with a Reopen button" "id=\"pane-friction-reopen\""

    testCase "a closed panel's row offers only Reopen, and an open one offers move, up, down and close" <| fun _ ->
      let html = htmlOf (viewAfter repl [ edit repl (LayoutEdit.Close PanelId.Friction) ])
      for action in [ "move"; "up"; "down"; "close" ] do
        html |> Expect.stringContains (sprintf "an open panel has %s" action) (sprintf "id=\"pane-cohort-%s\"" action)
        html.Contains(sprintf "id=\"pane-friction-%s\"" action) |> Expect.isFalse (sprintf "a closed panel has no %s" action)

    testCase "the first panel's Up and the last panel's Down are disabled, and the others are not" <| fun _ ->
      let html = htmlOf PaneLayoutView.today
      let buttonTag (id: string) = Regex.Match(html, sprintf "<button[^>]*id=\"%s\"[^>]*>" id).Value
      buttonTag "pane-hot-reload-up" |> Expect.stringContains "first up is disabled" "disabled"
      buttonTag "pane-hygiene-down" |> Expect.stringContains "last down is disabled" "disabled"
      buttonTag "pane-cohort-up" |> fun tag -> tag.Contains "disabled" |> Expect.isFalse "a middle up is enabled"
      buttonTag "pane-cohort-down" |> fun tag -> tag.Contains "disabled" |> Expect.isFalse "a middle down is enabled"

    testCase "Reset is disabled while the layout is the default and enabled once it is not" <| fun _ ->
      let tagIn (html: string) (id: string) = Regex.Match(html, sprintf "<button[^>]*id=\"%s\"[^>]*>" id).Value
      let isDefault = htmlOf (viewAfter repl [])
      tagIn isDefault "pane-reset" |> Expect.stringContains "reset this is disabled" "disabled"
      tagIn isDefault "pane-reset-all" |> Expect.stringContains "reset all is disabled" "disabled"
      let changed = htmlOf (viewAfter repl [ edit repl (LayoutEdit.Close PanelId.AgentTeam) ])
      tagIn changed "pane-reset" |> fun tag -> tag.Contains "disabled" |> Expect.isFalse "reset this is enabled"
      tagIn changed "pane-reset-all" |> fun tag -> tag.Contains "disabled" |> Expect.isFalse "reset all is enabled"
      // Another workflow's change enables reset all only: this workflow is still the default.
      let elsewhere = htmlOf (viewAfter repl [ edit LayoutWorkflow.HotReload (LayoutEdit.Close PanelId.AgentTeam) ])
      tagIn elsewhere "pane-reset" |> Expect.stringContains "this workflow is still the default" "disabled"
      tagIn elsewhere "pane-reset-all" |> fun tag -> tag.Contains "disabled" |> Expect.isFalse "but another workflow is not"

    testCase "every button posts an edit the daemon can read, aimed at the workflow the menu shows" <| fun _ ->
      let view = viewAfter LayoutWorkflow.LiveTesting [ edit LayoutWorkflow.LiveTesting (LayoutEdit.Close PanelId.AgentTeam) ]
      let posted = postedEdits (htmlOf view)
      Expect.isGreaterThan "the menu posts something" (List.length posted, 10)
      for text in posted do
        match BookEdit.parse text with
        | Result.Error refusal -> failtestf "'%s' is not an edit: %A" text refusal
        | Result.Ok(BookEdit.ResetAll) -> ()
        | Result.Ok(BookEdit.InWorkflow(workflow, _)) ->
          workflow |> Expect.equal (sprintf "'%s' is aimed at the workflow in view" text) LayoutWorkflow.LiveTesting

    testCase "every button has an accessible name of its own, naming the panel and what it does" <| fun _ ->
      let html = htmlOf (viewAfter repl [ edit repl (LayoutEdit.Close PanelId.Friction) ])
      let names =
        [ for m in Regex.Matches(html, "<button[^>]*class=\"[^\"]*pane-menu-btn[^\"]*\"[^>]*aria-label=\"([^\"]*)\"") -> m.Groups.[1].Value ]
      Expect.isGreaterThan "the menu has buttons" (List.length names, 10)
      names |> List.forall (fun name -> name.Trim() <> "") |> Expect.isTrue "no empty name"
      names |> List.distinct |> List.length |> Expect.equal "no two buttons share a name" (List.length names)
      names |> Expect.contains "a move names the panel and the place" "Move Agent team to the dock"

    testCase "the menu names the workflow whose layout it edits" <| fun _ ->
      htmlOf (viewAfter LayoutWorkflow.HotReload []) |> Expect.stringContains "title" "Panels for Hot reload sessions"

    testCase "a panel that has only a reason line to show says so on the menu, and one with nothing to report says that" <| fun _ ->
      let html = htmlOf (viewAfter repl [])
      html |> Expect.stringContains "hot reload shows its line" "shows the line &quot;Hot reload: off&quot; for now"
      html |> Expect.stringContains "friction has nothing to show" "nothing to show: friction reports are off until a tab asks for them"

    testCase "a saved layout that could not be used says why, on the menu, and the default is drawn" <| fun _ ->
      let read : PaneLayoutRead =
        { Book = LayoutBook.read (Map.ofList [ LayoutWorkflow.key repl, "garbage" ]); Reading = FileReading.Readable }
      let view = PaneLayoutView.ofRead repl read replFacts
      view.Layout |> Expect.equal "the default" (PaneLayout.defaultFor repl)
      htmlOf view |> Expect.stringContains "the reason is on the menu" "The saved layout could not be used, so the default is shown"
      let unreadable : PaneLayoutRead = { Book = LayoutBook.empty; Reading = FileReading.Unreadable "settings.json is not a settings layer" }
      htmlOf (PaneLayoutView.ofRead repl unreadable replFacts) |> Expect.stringContains "the file's reason" "settings.json is not a settings layer"

    testCase "a healthy layout draws no health note" <| fun _ ->
      (htmlOf (viewAfter repl [])).Contains "pane-menu-health" |> Expect.isFalse "no note"

    testCase "the menu is a details element that a morph leaves open, with the word beside the glyph" <| fun _ ->
      let html = htmlOf PaneLayoutView.today
      html |> Expect.stringContains "a details" "<details id=\"pane-menu\""
      html |> Expect.stringContains "kept open by the morph" "data-preserve-attr=\"open\""
      html |> Expect.stringContains "the word" "Panels</span>"
  ]
