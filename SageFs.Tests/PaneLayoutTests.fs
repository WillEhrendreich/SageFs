/// Where the dashboard's movable panels sit, as pure data: every edit keeps every pane placed exactly once, a reset is the default,
/// the saved text and the layout are the same thing, and an edit to one workflow never touches another's key. The properties run
/// over generated edit sequences folded through the REAL `LayoutBook.writesFor` and the real settings layer function
/// (`SettingsStore.applyChange`), so what is proven is what the file would hold.
module SageFs.Tests.PaneLayoutTests

open System
open System.IO
open Expecto
open Expecto.Flip
open FsCheck
open FsCheck.FSharp
open SageFs
open SageFs.Tests.SharedGenerators

// ── Generators: every value valid by construction ────────────────────────────────

let genPane : Gen<PanelId> = Gen.elements PanelId.all
let genRegion : Gen<PaneRegion> = Gen.elements PaneRegion.all
let genWorkflow : Gen<LayoutWorkflow> = Gen.elements LayoutWorkflow.all

let genEdit : Gen<LayoutEdit> =
  Gen.oneof
    [ Gen.map2 (fun pane region -> LayoutEdit.MoveToRegion(pane, region)) genPane genRegion
      Gen.map LayoutEdit.MoveUp genPane
      Gen.map LayoutEdit.MoveDown genPane
      Gen.map LayoutEdit.Close genPane
      Gen.map LayoutEdit.Reopen genPane
      Gen.constant LayoutEdit.Reset ]

let genBookEdit : Gen<BookEdit> =
  Gen.frequency
    [ 9, Gen.map2 (fun workflow edit -> BookEdit.InWorkflow(workflow, edit)) genWorkflow genEdit
      1, Gen.constant BookEdit.ResetAll ]

let genEdits : Gen<BookEdit list> = Gen.listOf genBookEdit

/// The settings layer an edit sequence leaves, starting from an empty file.
let layerAfter (edits: BookEdit list) : Map<string, string> =
  edits
  |> List.fold
    (fun layer edit -> LayoutBook.writesFor layer edit |> List.fold (fun l write -> PaneLayoutStore.applyWrite write l) layer)
    Map.empty

let layoutAfter (workflow: LayoutWorkflow) (edits: BookEdit list) : PaneLayout =
  LayoutBook.layoutOf workflow (LayoutBook.read (layerAfter edits))

let allLayouts (edits: BookEdit list) : (LayoutWorkflow * PaneLayout) list =
  let book = LayoutBook.read (layerAfter edits)
  LayoutWorkflow.all |> List.map (fun workflow -> workflow, LayoutBook.layoutOf workflow book)

let withTempDir (body: string -> unit) =
  let dir = Path.Combine(Path.GetTempPath(), "pane-layout-" + Guid.NewGuid().ToString("N"))
  Directory.CreateDirectory dir |> ignore
  try body dir
  finally Directory.Delete(dir, true)

let moveToDock (pane: PanelId) (workflow: LayoutWorkflow) (layout: PaneLayout) =
  PaneLayout.apply workflow (LayoutEdit.MoveToRegion(pane, PaneRegion.Dock)) layout

[<Tests>]
let paneLayoutTests =
  testList "Pane layout" [

    testList "the default" [
      testCase "moving a pane to the dock puts it in the last dock slot and leaves every other pane placed exactly once" <| fun _ ->
        let workflow = LayoutWorkflow.Repl
        let layout =
          PaneLayout.defaultFor workflow
          |> moveToDock PanelId.AgentTeam workflow
          |> moveToDock PanelId.Friction workflow
        layout.Dock |> Expect.equal "cohort first, friction last" [ PanelId.AgentTeam; PanelId.Friction ]
        layout.Sidebar
        |> Expect.equal "the rest keep their order" [ PanelId.HotReload; PanelId.LiveTesting; PanelId.SessionContext; PanelId.Hygiene ]
        PaneLayout.isValid layout |> Expect.isTrue "every pane is placed once"

      testCase "the default is every pane in the sidebar, in the order the sidebar always drew them, for every workflow" <| fun _ ->
        for workflow in LayoutWorkflow.all do
          let layout = PaneLayout.defaultFor workflow
          layout.Sidebar
          |> Expect.equal (sprintf "%A sidebar" workflow)
            [ PanelId.HotReload; PanelId.LiveTesting; PanelId.SessionContext; PanelId.Friction; PanelId.AgentTeam; PanelId.Hygiene ]
          (layout.Dock, layout.Closed) |> Expect.equal (sprintf "%A dock and closed are empty" workflow) ([], [])

      testCase "a layout with nothing saved is the default, and saves nothing" <| fun _ ->
        for workflow in LayoutWorkflow.all do
          LayoutBook.layoutOf workflow LayoutBook.empty |> Expect.equal "default" (PaneLayout.defaultFor workflow)
        LayoutBook.writesFor Map.empty (BookEdit.InWorkflow(LayoutWorkflow.Repl, LayoutEdit.Reset))
        |> Expect.isEmpty "resetting what is already the default writes nothing"
    ]

    testList "every edit keeps the layout whole" [
      testPropertyWithConfig propConfig "after any edit sequence every pane is placed exactly once, in every workflow"
        (Prop.forAll (Arb.fromGen genEdits) (fun edits -> allLayouts edits |> List.forall (snd >> PaneLayout.isValid)))

      testPropertyWithConfig propConfig "what an edit sequence writes is always text that reads back clean, with no problem and no note"
        (Prop.forAll (Arb.fromGen genEdits) (fun edits ->
          let book = LayoutBook.read (layerAfter edits)
          Map.isEmpty book.Problems && Map.isEmpty book.Notes))

      testPropertyWithConfig propConfig "reset puts that workflow back to its default and writes no key for it"
        (Prop.forAll (Arb.fromGen (Gen.zip genEdits genWorkflow)) (fun (edits, workflow) ->
          let after = edits @ [ BookEdit.InWorkflow(workflow, LayoutEdit.Reset) ]
          layoutAfter workflow after = PaneLayout.defaultFor workflow
          && not (Map.containsKey (LayoutWorkflow.key workflow) (layerAfter after))))

      testPropertyWithConfig propConfig "reset all puts every workflow back to its default and leaves no layout key"
        (Prop.forAll (Arb.fromGen genEdits) (fun edits ->
          let layer = layerAfter (edits @ [ BookEdit.ResetAll ])
          allLayouts (edits @ [ BookEdit.ResetAll ]) |> List.forall (fun (workflow, layout) -> layout = PaneLayout.defaultFor workflow)
          && layer |> Map.toList |> List.forall (fun (key, _) -> not (key.StartsWith "dashboard.layout."))))

      testPropertyWithConfig propConfig "closing a pane and reopening it puts it in its default region, last"
        (Prop.forAll (Arb.fromGen (Gen.zip3 genEdits genWorkflow genPane)) (fun (edits, workflow, pane) ->
          let layout = layoutAfter workflow edits
          let reopened =
            layout
            |> PaneLayout.apply workflow (LayoutEdit.Close pane)
            |> PaneLayout.apply workflow (LayoutEdit.Reopen pane)
          let region = PaneLayout.defaultRegion workflow pane
          PaneLayout.place pane reopened = PanePlace.InRegion(region, List.length (PaneLayout.panes region reopened) - 1)
          && PaneLayout.isValid reopened))

      testPropertyWithConfig propConfig "every pane is reachable: it is placed, or closed with a reopen that places it"
        (Prop.forAll (Arb.fromGen (Gen.zip3 genEdits genWorkflow genPane)) (fun (edits, workflow, pane) ->
          let layout = layoutAfter workflow edits
          match PaneLayout.place pane layout with
          | PanePlace.InRegion _ -> true
          | PanePlace.ClosedByUser ->
            match PaneLayout.place pane (PaneLayout.apply workflow (LayoutEdit.Reopen pane) layout) with
            | PanePlace.InRegion _ -> true
            | PanePlace.ClosedByUser -> false))

      testPropertyWithConfig propConfig "moving to a region the pane is already in changes nothing, and moving twice is moving once"
        (Prop.forAll (Arb.fromGen (Gen.zip3 genEdits genPane genRegion)) (fun (edits, pane, region) ->
          let workflow = LayoutWorkflow.Repl
          let once = PaneLayout.apply workflow (LayoutEdit.MoveToRegion(pane, region)) (layoutAfter workflow edits)
          PaneLayout.apply workflow (LayoutEdit.MoveToRegion(pane, region)) once = once))

      testPropertyWithConfig propConfig "a move up swaps the pane with the one above it and moves nothing else"
        (Prop.forAll (Arb.fromGen (Gen.zip genEdits genPane)) (fun (edits, pane) ->
          let workflow = LayoutWorkflow.Repl
          let layout = layoutAfter workflow edits
          match PaneLayout.place pane layout with
          | PanePlace.ClosedByUser -> PaneLayout.apply workflow (LayoutEdit.MoveUp pane) layout = layout
          | PanePlace.InRegion(region, slot) ->
            let before = PaneLayout.panes region layout
            let after = PaneLayout.panes region (PaneLayout.apply workflow (LayoutEdit.MoveUp pane) layout)
            match slot with
            | 0 -> after = before
            | _ ->
              let expected = before |> List.toArray
              let swapped = Array.copy expected
              swapped.[slot - 1] <- expected.[slot]
              swapped.[slot] <- expected.[slot - 1]
              after = List.ofArray swapped))

      testPropertyWithConfig propConfig "the buttons that are drawn disabled are exactly the moves that would change nothing"
        (Prop.forAll (Arb.fromGen (Gen.zip genEdits genPane)) (fun (edits, pane) ->
          let workflow = LayoutWorkflow.Repl
          let layout = layoutAfter workflow edits
          PaneLayout.canMoveUp pane layout = (PaneLayout.apply workflow (LayoutEdit.MoveUp pane) layout <> layout)
          && PaneLayout.canMoveDown pane layout = (PaneLayout.apply workflow (LayoutEdit.MoveDown pane) layout <> layout)))
    ]

    testList "workflows keep their own layout" [
      testPropertyWithConfig propConfig "an edit in one workflow changes no other workflow's saved key"
        (Prop.forAll (Arb.fromGen (Gen.zip3 genEdits genWorkflow genEdit)) (fun (edits, workflow, edit) ->
          let before = layerAfter edits
          let after = layerAfter (edits @ [ BookEdit.InWorkflow(workflow, edit) ])
          LayoutWorkflow.all
          |> List.filter (fun other -> other <> workflow)
          |> List.forall (fun other ->
            let key = LayoutWorkflow.key other
            Map.tryFind key before = Map.tryFind key after)))

      testPropertyWithConfig propConfig "an edit leaves the layout the pure edit says, and every key that is not a layout key alone"
        (Prop.forAll (Arb.fromGen (Gen.zip3 genEdits genWorkflow genEdit)) (fun (edits, workflow, edit) ->
          let layer = layerAfter edits |> Map.add "daemon.mcpPort" "5000"
          let expected = PaneLayout.apply workflow edit (LayoutBook.layoutOf workflow (LayoutBook.read layer))
          let after =
            LayoutBook.writesFor layer (BookEdit.InWorkflow(workflow, edit))
            |> List.fold (fun l write -> PaneLayoutStore.applyWrite write l) layer
          LayoutBook.layoutOf workflow (LayoutBook.read after) = expected
          && Map.tryFind "daemon.mcpPort" after = Some "5000"))

      testPropertyWithConfig propConfig "an edit that changes nothing writes nothing"
        (Prop.forAll (Arb.fromGen (Gen.zip3 genEdits genWorkflow genEdit)) (fun (edits, workflow, edit) ->
          let layer = layerAfter edits
          let layout = LayoutBook.layoutOf workflow (LayoutBook.read layer)
          match PaneLayout.apply workflow edit layout = layout with
          | true -> List.isEmpty (LayoutBook.writesFor layer (BookEdit.InWorkflow(workflow, edit)))
          | false -> true))
    ]

    testList "the saved text and the request tokens" [
      testPropertyWithConfig propConfig "a layout written as text reads back as the same layout, with nothing set aside"
        (Prop.forAll (Arb.fromGen (Gen.zip genEdits genWorkflow)) (fun (edits, workflow) ->
          let layout = layoutAfter workflow edits
          PaneLayout.parse workflow (PaneLayout.render layout) = Result.Ok(layout, [])))

      testPropertyWithConfig propConfig "a request token reads back as the edit that made it"
        (Prop.forAll (Arb.fromGen genBookEdit) (fun edit -> BookEdit.parse (BookEdit.token edit) = Result.Ok edit))

      testCase "a pane the saved text does not place takes its default place, and the menu is told" <| fun _ ->
        match PaneLayout.parse LayoutWorkflow.Repl "sidebar=cohort;dock=hygiene;closed=" with
        | Result.Error refusal -> failtestf "should read: %A" refusal
        | Result.Ok(layout, notes) ->
          layout.Sidebar
          |> Expect.equal "cohort stays first, the unplaced panes follow in the default order"
            [ PanelId.AgentTeam; PanelId.HotReload; PanelId.LiveTesting; PanelId.SessionContext; PanelId.Friction ]
          layout.Dock |> Expect.equal "hygiene stays in the dock" [ PanelId.Hygiene ]
          notes
          |> Expect.contains "one of them is named" (LayoutNote.PaneMissingTookDefault PanelId.HotReload)

      testCase "a name that is not a pane is dropped and a pane listed twice counts once, and both are said" <| fun _ ->
        match PaneLayout.parse LayoutWorkflow.Repl "sidebar=cohort,bogus,cohort;dock=;closed=" with
        | Result.Error refusal -> failtestf "should read: %A" refusal
        | Result.Ok(layout, notes) ->
          PaneLayout.isValid layout |> Expect.isTrue "whole"
          notes |> Expect.contains "the unknown name" (LayoutNote.UnknownPaneDropped "bogus")
          notes |> Expect.contains "the repeat" (LayoutNote.PaneListedTwice PanelId.AgentTeam)

      testCase "text that is not region=list is refused with the segment, so the menu can say why the default is shown" <| fun _ ->
        PaneLayout.parse LayoutWorkflow.Repl "nonsense"
        |> Expect.equal "malformed" (Result.Error(LayoutRefusal.MalformedSegment "nonsense"))
        PaneLayout.parse LayoutWorkflow.Repl "floor=cohort"
        |> Expect.equal "unknown region" (Result.Error(LayoutRefusal.MalformedSegment "floor=cohort"))

      testCase "a request that names no edit, a pane, a region or a workflow is refused with the word it could not read" <| fun _ ->
        BookEdit.parse "repl/move:bogus:dock" |> Expect.equal "pane" (Result.Error(LayoutRefusal.UnknownPane "bogus"))
        BookEdit.parse "repl/move:cohort:floor" |> Expect.equal "region" (Result.Error(LayoutRefusal.UnknownRegion "floor"))
        BookEdit.parse "tablet/reset" |> Expect.equal "workflow" (Result.Error(LayoutRefusal.UnknownWorkflow "tablet"))
        BookEdit.parse "repl/shuffle" |> Expect.equal "edit" (Result.Error(LayoutRefusal.UnknownEdit "shuffle"))
        BookEdit.parse "all/close:cohort" |> Expect.equal "all takes only reset" (Result.Error(LayoutRefusal.UnknownEdit "all/close:cohort"))

      testCase "a layout key that is saved as text that cannot be read is a problem for that workflow, and the default is shown" <| fun _ ->
        let layer = Map.ofList [ LayoutWorkflow.key LayoutWorkflow.Repl, "garbage" ]
        let book = LayoutBook.read layer
        LayoutBook.layoutOf LayoutWorkflow.Repl book |> Expect.equal "default" (PaneLayout.defaultFor LayoutWorkflow.Repl)
        LayoutBook.problemOf LayoutWorkflow.Repl book
        |> Expect.equal "the reason is kept" (Some(LayoutRefusal.MalformedSegment "garbage"))
        // An edit repairs it: the file is saved over, whatever was there.
        LayoutBook.writesFor layer (BookEdit.InWorkflow(LayoutWorkflow.Repl, LayoutEdit.Close PanelId.AgentTeam))
        |> List.length
        |> Expect.equal "one save" 1
        LayoutBook.writesFor layer (BookEdit.InWorkflow(LayoutWorkflow.Repl, LayoutEdit.Reset))
        |> Expect.equal "a reset clears the bad key" [ LayoutWrite.Clear(LayoutWorkflow.key LayoutWorkflow.Repl) ]
    ]

    testList "the decisions have teeth: a mutant of each is caught" [
      let workflow = LayoutWorkflow.Repl
      let moved = PaneLayout.defaultFor workflow |> moveToDock PanelId.AgentTeam workflow |> moveToDock PanelId.Friction workflow
      let mutant name description apply : MutationTestingFramework.Mutant<(LayoutEdit * PaneLayout) -> PaneLayout> =
        { Name = name; Description = description; Apply = apply }
      let real (edit, layout) = PaneLayout.apply workflow edit layout

      MutationTestingFramework.detectsOutputMutant
        (mutant "close loses the pane" "a closed pane is dropped instead of listed" (fun realFn input ->
          let result = realFn input
          { result with Closed = [] }))
        (LayoutEdit.Close PanelId.Hygiene, PaneLayout.defaultFor workflow)
        real
        (fun result -> PaneLayout.isValid result && result.Closed = [ PanelId.Hygiene ])

      MutationTestingFramework.detectsOutputMutant
        (mutant "move copies" "a moved pane stays in the region it left as well" (fun realFn ((edit, layout) as input) ->
          let result = realFn input
          match edit with
          | LayoutEdit.MoveToRegion(pane, _) -> { result with Sidebar = result.Sidebar @ (if List.contains pane layout.Sidebar then [ pane ] else []) }
          | _ -> result))
        (LayoutEdit.MoveToRegion(PanelId.AgentTeam, PaneRegion.Dock), PaneLayout.defaultFor workflow)
        real
        (fun result -> PaneLayout.isValid result && result.Dock = [ PanelId.AgentTeam ])

      MutationTestingFramework.detectsOutputMutant
        (mutant "reopen to the wrong region" "a reopened pane goes to the dock" (fun realFn input ->
          let result = realFn input
          match List.contains PanelId.Hygiene result.Sidebar with
          | true -> { result with Sidebar = result.Sidebar |> List.filter ((<>) PanelId.Hygiene); Dock = result.Dock @ [ PanelId.Hygiene ] }
          | false -> result))
        (LayoutEdit.Reopen PanelId.Hygiene, PaneLayout.apply workflow (LayoutEdit.Close PanelId.Hygiene) (PaneLayout.defaultFor workflow))
        real
        (fun result -> PaneLayout.place PanelId.Hygiene result = PanePlace.InRegion(PaneRegion.Sidebar, 5))

      MutationTestingFramework.detectsOutputMutant
        (mutant "reset keeps the edits" "a reset returns the layout it was given" (fun _ (_, layout) -> layout))
        (LayoutEdit.Reset, moved)
        real
        (fun result -> result = PaneLayout.defaultFor workflow)

      MutationTestingFramework.detectsOutputMutant
        (mutant "up is down" "a move up moves the pane down" (fun realFn (edit, layout) ->
          match edit with
          | LayoutEdit.MoveUp pane -> realFn (LayoutEdit.MoveDown pane, layout)
          | _ -> realFn (edit, layout)))
        (LayoutEdit.MoveUp PanelId.AgentTeam, PaneLayout.defaultFor workflow)
        real
        (fun result -> PaneLayout.place PanelId.AgentTeam result = PanePlace.InRegion(PaneRegion.Sidebar, 3))

      MutationTestingFramework.detectsOutputMutant
        (mutant "move to the front" "a pane moved to a region goes first instead of last" (fun realFn ((edit, _) as input) ->
          let result = realFn input
          match edit with
          | LayoutEdit.MoveToRegion(pane, PaneRegion.Dock) -> { result with Dock = pane :: (result.Dock |> List.filter ((<>) pane)) }
          | _ -> result))
        (LayoutEdit.MoveToRegion(PanelId.Hygiene, PaneRegion.Dock), PaneLayout.defaultFor workflow |> moveToDock PanelId.AgentTeam workflow)
        real
        (fun result -> result.Dock = [ PanelId.AgentTeam; PanelId.Hygiene ])
    ]

    testList "the store: the global settings layer file, read and written the way every saved setting is" [
      testCase "an edit is saved, and a fresh store reads it back" <| fun _ ->
        withTempDir (fun dir ->
          let edit = BookEdit.InWorkflow(LayoutWorkflow.Repl, LayoutEdit.MoveToRegion(PanelId.AgentTeam, PaneRegion.Dock))
          PaneLayoutStore.Store(dir).Edit edit |> Expect.isOk "saved"
          let read = PaneLayoutStore.Store(dir).Read()
          read.Reading |> Expect.equal "the file reads" FileReading.Readable
          (LayoutBook.layoutOf LayoutWorkflow.Repl read.Book).Dock |> Expect.equal "cohort is in the dock" [ PanelId.AgentTeam ]
          LayoutBook.layoutOf LayoutWorkflow.HotReload read.Book
          |> Expect.equal "another workflow is still the default" (PaneLayout.defaultFor LayoutWorkflow.HotReload))

      testCase "a store sees its own edit at once and leaves no half-written file behind" <| fun _ ->
        withTempDir (fun dir ->
          let store = PaneLayoutStore.Store dir
          store.Read().Book |> Expect.equal "nothing saved" LayoutBook.empty
          store.Edit(BookEdit.InWorkflow(LayoutWorkflow.Repl, LayoutEdit.Close PanelId.Friction)) |> Expect.isOk "saved"
          (LayoutBook.layoutOf LayoutWorkflow.Repl (store.Read().Book)).Closed |> Expect.equal "closed at once" [ PanelId.Friction ]
          Directory.GetFiles(dir, "*.tmp") |> Expect.isEmpty "tmp-and-move leaves no tmp file")

      testCase "the other keys in the settings file are kept, and a reset leaves them" <| fun _ ->
        withTempDir (fun dir ->
          SettingsStore.setKey (SettingsStore.globalPath dir) "daemon.mcpPort" "5000" |> Expect.isOk "another setting"
          let store = PaneLayoutStore.Store dir
          store.Edit(BookEdit.InWorkflow(LayoutWorkflow.NoSession, LayoutEdit.Close PanelId.Hygiene)) |> Expect.isOk "saved"
          store.Edit BookEdit.ResetAll |> Expect.isOk "reset"
          SettingsStore.readLayer (SettingsStore.globalPath dir)
          |> Expect.equal "only the other setting is left" (Map.ofList [ "daemon.mcpPort", "5000" ]))

      testCase "a settings file that cannot be read shows the default with the reason, and an edit replaces it and keeps the old bytes" <| fun _ ->
        withTempDir (fun dir ->
          let path = SettingsStore.globalPath dir
          File.WriteAllText(path, "{ not json")
          let store = PaneLayoutStore.Store dir
          let read = store.Read()
          (match read.Reading with
           | FileReading.Unreadable _ -> ()
           | FileReading.Readable -> failtest "the reason is carried")
          LayoutBook.layoutOf LayoutWorkflow.Repl read.Book |> Expect.equal "default" (PaneLayout.defaultFor LayoutWorkflow.Repl)
          store.Edit(BookEdit.InWorkflow(LayoutWorkflow.Repl, LayoutEdit.Close PanelId.AgentTeam)) |> Expect.isOk "saved over it"
          File.ReadAllText(SettingsStore.corruptCopyPath path) |> Expect.equal "the old bytes are kept beside it" "{ not json"
          let after = store.Read()
          after.Reading |> Expect.equal "the file reads again" FileReading.Readable
          (LayoutBook.layoutOf LayoutWorkflow.Repl after.Book).Closed |> Expect.equal "the edit landed" [ PanelId.AgentTeam ])

      testCase "a hand-edited layout with a name that is not a pane is used, with a note, and is not rewritten by reading" <| fun _ ->
        withTempDir (fun dir ->
          let path = SettingsStore.globalPath dir
          let text = "sidebar=cohort,bogus;dock=;closed="
          SettingsStore.setKey path (LayoutWorkflow.key LayoutWorkflow.Repl) text |> Expect.isOk "hand edit"
          let read = PaneLayoutStore.Store(dir).Read()
          LayoutBook.notesOf LayoutWorkflow.Repl read.Book
          |> Expect.contains "the unknown name is noted" (LayoutNote.UnknownPaneDropped "bogus")
          SettingsStore.readLayer path
          |> Map.find (LayoutWorkflow.key LayoutWorkflow.Repl)
          |> Expect.equal "the file is as it was" text)

      testCase "the session workflow picks the layout workflow" <| fun _ ->
        PaneLayoutStore.ofSessionWorkflow WorkflowTypes.SessionWorkflow.Interactive |> Expect.equal "repl" LayoutWorkflow.Repl
        PaneLayoutStore.ofSessionWorkflow WorkflowTypes.SessionWorkflow.LiveTesting |> Expect.equal "live testing" LayoutWorkflow.LiveTesting
        PaneLayoutStore.ofSessionWorkflow (WorkflowTypes.SessionWorkflow.HotReload WorkflowTypes.BrowserRefreshConfig.defaults)
        |> Expect.equal "hot reload" LayoutWorkflow.HotReload
    ]
  ]
