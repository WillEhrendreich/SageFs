/// The Live Bindings view as settings (docs/plans/live-bindings-watch-view.md, phase 1, step 1). Pure: nothing here reads a file or a session.
/// Each fact below was first proved in a SageFs REPL session on SageFs.Core, then written down here.
module SageFs.Tests.ViewSettingsTests

open Expecto
open Expecto.Flip
open FsCheck
open SageFs

let private everyAxisValue : (ViewWorkflow * ViewAxis * AxisValue) list =
  [ for workflow in ViewWorkflow.all do
      for axis in ViewAxis.all do
        for value in AxisValue.allFor axis do
          yield workflow, axis, value ]

[<Tests>]
let catalogTests =
  testList "ViewSettings catalog" [

    testCase "WHY — every workflow has every axis as its own setting, with its own key, because a key that two settings share would make one overwrite the other" <| fun _ ->
      ViewSettings.descriptors |> List.length |> Expect.equal "three workflows by eight axes" 24
      ViewSettings.descriptors |> List.map (fun d -> d.Key) |> List.distinct |> List.length |> Expect.equal "all keys distinct" 24
      ViewSettings.descriptors
      |> List.forall (fun d -> d.Key.StartsWith "bindings.view.")
      |> Expect.isTrue "all under one prefix, so the panel can group them"

    testCase "a key names the workflow and the axis, and the workflow and axis come from closed sets with one spelling each" <| fun _ ->
      ViewSettings.key ViewWorkflow.HotReload ViewAxis.Inspector |> Expect.equal "hot reload inspector" "bindings.view.hotreload.inspector"
      ViewSettings.key ViewWorkflow.Repl ViewAxis.ValueMode |> Expect.equal "repl values" "bindings.view.repl.values"

    testCase "WHY — every legal value of every axis survives parse then render unchanged, because a value that changed on its way through a file would silently change the view" <| fun _ ->
      for workflow, axis, value in everyAxisValue do
        let descriptor = ViewSettings.descriptor workflow axis
        let raw = AxisValue.token value
        match descriptor.Parse raw with
        | Ok setting ->
          descriptor.Render setting |> Expect.equal (sprintf "%s %s renders as it was written" (ViewWorkflow.token workflow) raw) raw
          ViewSettings.axisValueOf axis setting |> Expect.equal "and reads back as the same axis value" (Some value)
        | Error why -> failtestf "%s refused its own value '%s': %A" descriptor.Key raw why

    testCase "a value outside an axis's set is refused with the set it should have been" <| fun _ ->
      let descriptor = ViewSettings.descriptor ViewWorkflow.Repl ViewAxis.GroupBy
      descriptor.Parse "sideways" |> Expect.equal "named, with the legal ones" (Error(NotAMember("sideways", [ "none"; "origin" ])))
      (ViewSettings.descriptor ViewWorkflow.Repl ViewAxis.TypeColumn).Parse "maybe" |> Expect.isError "a toggle is on or off"

    testCase "WHY — a setting that was never written resolves to the recommended view's value, because the default is what an untouched axis follows" <| fun _ ->
      for workflow in ViewWorkflow.all do
        for axis in ViewAxis.all do
          let descriptor = ViewSettings.descriptor workflow axis
          ViewSettings.axisValueOf axis descriptor.Default
          |> Expect.equal (sprintf "%s default" descriptor.Key) (Some(View.valueOf (View.recommended workflow) axis))

    testCase "every view setting is a repo-overridable, live setting in its own category" <| fun _ ->
      for descriptor in ViewSettings.descriptors do
        descriptor.Scope |> Expect.equal (descriptor.Key + " scope") RepoOverridable
        descriptor.Applicability |> Expect.equal (descriptor.Key + " applicability") Live
        descriptor.Category |> Expect.equal (descriptor.Key + " category") LiveBindingsView
      SettingCategory.displayOrder |> List.contains LiveBindingsView |> Expect.isTrue "the panel lists the category"

    testCase "the session workflow maps onto the closed set a key is built from" <| fun _ ->
      ViewWorkflow.ofSessionWorkflow WorkflowTypes.SessionWorkflow.Interactive |> Expect.equal "interactive is the REPL" ViewWorkflow.Repl
      ViewWorkflow.ofSessionWorkflow WorkflowTypes.SessionWorkflow.LiveTesting |> Expect.equal "live testing" ViewWorkflow.LiveTesting
      ViewWorkflow.ofSessionWorkflow (WorkflowTypes.SessionWorkflow.fromHotReloadBool true) |> Expect.equal "hot reload, whatever its refresh config" ViewWorkflow.HotReload
  ]

/// A layer stack for one workflow, drawn from a seed: for each axis, some subset of the layers each holding a legal value.
let private stackOf (seed: int) : (ViewAxis * (ConfigLayer * AxisValue) list) list =
  let rng = System.Random seed
  ViewAxis.all
  |> List.map (fun axis ->
    let legal = AxisValue.allFor axis
    axis, [ for layer in [ LGlobal; LRepo; LSession ] do
              if rng.Next 2 = 0 then yield layer, legal.[rng.Next legal.Length] ])

let private topOf (set: (ConfigLayer * AxisValue) list) : (ConfigLayer * AxisValue) option =
  match set with
  | [] -> None
  | _ -> Some(set |> List.maxBy (fun (layer, _) -> ConfigLayer.rank layer))

let private resolveStack (workflow: ViewWorkflow) (stack: (ViewAxis * (ConfigLayer * AxisValue) list) list) : ResolvedView =
  let table = Map.ofList stack
  ViewSettings.resolveView workflow (fun axis ->
    let descriptor = ViewSettings.descriptor workflow axis
    SettingsResolver.resolve descriptor.Default [ for layer, value in table.[axis] -> layer, ViewSettings.settingValueOf value ])

[<Tests>]
let resolutionTests =
  testList "ViewSettings resolution" [

    testProperty "WHY — the highest layer that sets an axis wins and says so, and an axis nobody set follows the recommendation" <| fun (seed: int) (workflowIndex: byte) ->
      let workflow = ViewWorkflow.all.[int workflowIndex % ViewWorkflow.all.Length]
      let stack = stackOf seed
      let resolved = resolveStack workflow stack
      let expectedView =
        stack |> List.fold (fun view (_, set) -> match topOf set with Some(_, value) -> View.withValue value view | None -> view) (View.recommended workflow)
      let expectedSources =
        stack |> List.map (fun (axis, set) -> axis, (match topOf set with Some(layer, _) -> layer | None -> LDefault)) |> Map.ofList
      resolved.View = expectedView && resolved.Sources = expectedSources

    testCase "WHY — a stored value that is not one of its axis's keeps the recommended value and says the default supplied it, because a hand-edited file must not break the pane" <| fun _ ->
      let workflow = ViewWorkflow.HotReload
      let resolved =
        ViewSettings.resolveView workflow (fun axis ->
          let descriptor = ViewSettings.descriptor workflow axis
          match axis with
          | ViewAxis.TypeColumn -> SettingsResolver.resolve descriptor.Default [ LRepo, VPort(match Port.create 4000 with Ok p -> p | Error _ -> failwith "a valid port") ]
          | _ -> SettingsResolver.resolve descriptor.Default [])
      resolved.View |> Expect.equal "the recommended view" (View.recommended workflow)
      resolved.Sources.[ViewAxis.TypeColumn] |> Expect.equal "supplied by the default, not the repo" LDefault

    testCase "WHY — a write equal to what the layer below would give clears the override instead of storing it, so only overrides are stored" <| fun _ ->
      for axis in ViewAxis.all do
        for below in AxisValue.allFor axis do
          for chosen in AxisValue.allFor axis do
            ViewSettings.planWrite below chosen
            |> Expect.equal (sprintf "%A over %A" chosen below) (if below = chosen then Write.ClearOverride else Write.Store chosen)

    testCase "TWIN — a planner that always stores would keep a value equal to the recommendation, which is the bug the rule prevents" <| fun _ ->
      let alwaysStores (_: AxisValue) (chosen: AxisValue) = Write.Store chosen
      let below = AxisValue.TypeColumn On
      alwaysStores below below |> Expect.equal "the twin stores a no-op" (Write.Store below)
      (ViewSettings.planWrite below below = alwaysStores below below) |> Expect.isFalse "and the real planner does not"
  ]

[<Tests>]
let presetTests =
  testList "ViewSettings presets" [

    testCase "WHY — the five presets are five different layouts, because two presets that were the same would make the menu a lie" <| fun _ ->
      ViewPreset.all |> List.map ViewPreset.layout |> List.distinct |> List.length |> Expect.equal "all distinct" ViewPreset.all.Length

    testCase "WHY — Watch window is recommended for REPL and Live testing and Tuner for Hot reload, because tweaking is where the extra columns and the inspector pay" <| fun _ ->
      ViewPreset.recommendedFor ViewWorkflow.Repl |> Expect.equal "repl" ViewPreset.WatchWindow
      ViewPreset.recommendedFor ViewWorkflow.LiveTesting |> Expect.equal "live testing" ViewPreset.WatchWindow
      ViewPreset.recommendedFor ViewWorkflow.HotReload |> Expect.equal "hot reload" ViewPreset.Tuner

    testCase "a layout is detected as the recommended one, a named preset, or custom" <| fun _ ->
      for workflow in ViewWorkflow.all do
        for preset in ViewPreset.all do
          let expected =
            match preset = ViewPreset.recommendedFor workflow with
            | true -> DetectedPreset.IsRecommended
            | false -> DetectedPreset.IsPreset preset
          View.detect workflow (ViewPreset.layout preset) |> Expect.equal (sprintf "%A in %A" preset workflow) expected
      View.detect ViewWorkflow.Repl { ViewPreset.flat with State = On } |> Expect.equal "a flat layout with State on is nobody's preset" DetectedPreset.IsCustom

    testCase "WHY — picking a preset sets the six layout axes and leaves value printing and row height alone, because those are personal" <| fun _ ->
      let mine = { View.recommended ViewWorkflow.Repl with Values = ValueMode.Full; Row = RowHeight.Roomy }
      let picked = View.withPreset ViewPreset.Tuner mine
      picked.Layout |> Expect.equal "the preset's layout" (ViewPreset.layout ViewPreset.Tuner)
      picked.Values |> Expect.equal "values untouched" ValueMode.Full
      picked.Row |> Expect.equal "row height untouched" RowHeight.Roomy

    testCase "a view's axis values rebuild the same view, so none is lost on the way" <| fun _ ->
      for workflow in ViewWorkflow.all do
        let view = View.recommended workflow
        View.axisValues view |> List.fold (fun v value -> View.withValue value v) (View.recommended ViewWorkflow.Repl)
        |> Expect.equal "round trip" view
  ]

/// The properties a column-fitting rule must have, as the names the failures are counted under.
let private failedProperties (fit: ValueMode -> Layout -> int -> Fit) : (string * int) list =
  let layouts =
    [ for t in [ On; Off ] do
        for s in [ On; Off ] do
          for src in [ On; Off ] do
            yield { ViewPreset.flat with Type = t; State = s; Source = src } ]
  [ for layout in layouts do
      for mode in [ ValueMode.Clipped; ValueMode.Full ] do
        let requested = ViewColumns.requested layout
        for width in 0 .. 900 do
          let fitted = fit mode layout width
          let partition = (fitted.Hidden @ fitted.Shown) = requested
          let hidesInOrder = (requested |> List.truncate fitted.Hidden.Length) = fitted.Hidden
          let shownFits = (List.isEmpty fitted.Shown && fitted.Hidden = requested) || ViewColumns.needed mode fitted.Shown <= width
          let minimal = List.isEmpty fitted.Hidden || ViewColumns.needed mode (List.last fitted.Hidden :: fitted.Shown) > width
          let widerHidesNoMore = (fit mode layout (width + 1)).Hidden.Length <= fitted.Hidden.Length
          let fullHidesAtLeastAsMuch = (fit ValueMode.Full layout width).Hidden.Length >= (fit ValueMode.Clipped layout width).Hidden.Length
          for name, holds in
            [ "partition", partition
              "hides in order", hidesInOrder
              "what is shown fits", shownFits
              "minimal hiding", minimal
              "wider hides no more", widerHidesNoMore
              "full hides at least as much", fullHidesAtLeastAsMuch ] do
            if not holds then yield name ]
  |> List.countBy id

[<Tests>]
let columnTests =
  testList "ViewColumns fit" [

    testCase "WHY — columns give way before the value does: over every layout, both value modes and widths 0 to 900, the hidden columns are the first requested, what is shown fits, nothing more is hidden than must be, and a wider pane never hides more" <| fun _ ->
      failedProperties ViewColumns.fit |> Expect.isEmpty "no property fails anywhere"

    testCase "TWIN — a rule that hides State before Type fails the order property, so the order is tested and not assumed" <| fun _ ->
      let hidesStateFirst (mode: ValueMode) (layout: Layout) (width: int) : Fit =
        let rec go hidden remaining =
          match remaining with
          | [] -> { Shown = []; Hidden = List.rev hidden }
          | _ when ViewColumns.needed mode remaining <= width -> { Shown = remaining; Hidden = List.rev hidden }
          | first :: rest -> go (first :: hidden) rest
        go [] (ViewColumns.requested layout |> List.rev)
      failedProperties hidesStateFirst |> List.map fst |> Expect.contains "caught by the order property" "hides in order"

    testCase "TWIN — a rule that never hides fails the fits property, so too narrow a pane is not allowed to keep every column" <| fun _ ->
      let neverHides (_: ValueMode) (layout: Layout) (_: int) : Fit = { Shown = ViewColumns.requested layout; Hidden = [] }
      failedProperties neverHides |> List.map fst |> Expect.contains "caught by the fits property" "what is shown fits"

    testCase "the widths the mockup measured: five columns need 588 px, one less hides Type, and Full needs a wider value" <| fun _ ->
      let five = { ViewPreset.flat with State = On; Source = On }
      ViewColumns.fit ValueMode.Clipped five 588 |> Expect.equal "fits at 588" { Shown = [ OptionalColumn.Type; OptionalColumn.Source; OptionalColumn.State ]; Hidden = [] }
      (ViewColumns.fit ValueMode.Clipped five 587).Hidden |> Expect.equal "Type goes first at 587" [ OptionalColumn.Type ]
      (ViewColumns.fit ValueMode.Full five 620).Hidden |> Expect.equal "Full at 620 hides Type, then Source" [ OptionalColumn.Type; OptionalColumn.Source ]

    testCase "a pane too narrow even for Name and Value hides every optional column and never the value" <| fun _ ->
      let fitted = ViewColumns.fit ValueMode.Clipped { ViewPreset.flat with State = On; Source = On } 10
      fitted.Shown |> Expect.isEmpty "nothing optional shown"
      fitted.Hidden |> Expect.equal "all hidden, in order" [ OptionalColumn.Type; OptionalColumn.Source; OptionalColumn.State ]
  ]
