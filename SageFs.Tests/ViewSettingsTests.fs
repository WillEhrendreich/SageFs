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
      let expected = ViewWorkflow.all.Length * ViewAxis.all.Length
      ViewSettings.descriptors |> List.length |> Expect.equal "every workflow by every axis" expected
      ViewSettings.descriptors |> List.map (fun d -> d.Key) |> List.distinct |> List.length |> Expect.equal "all keys distinct" expected
      expected |> Expect.equal "three workflows by nine axes: the eight of the plan and the changes strip" 27
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

/// What the stylesheet does with a width, read back from the rules `ViewColumns.containerRules` generates: the columns the rules
/// that apply at that width hide. The browser applies a container query to every rule at once, so this is the same reading.
let private hiddenByRules (rules: ContainerRule list) (values: ValueMode) (layout: Layout) (width: int) : Set<OptionalColumn> =
  rules
  |> List.filter (fun rule -> rule.Values = values && rule.Requested = ViewColumns.requested layout && width <= rule.Below)
  |> List.map (fun rule -> rule.Hides)
  |> Set.ofList

let private everyLayout : Layout list =
  [ for t in [ On; Off ] do
      for s in [ On; Off ] do
        for src in [ On; Off ] do
          yield { ViewPreset.flat with Type = t; State = s; Source = src } ]

[<Tests>]
let containerRuleTests =
  testList "ViewColumns container rules" [

    testCase "WHY — at every width from 0 to 900, for every layout and both value modes, the generated rules hide exactly the columns the pure fit hides, so the stylesheet cannot disagree with the function" <| fun _ ->
      let disagreements =
        [ for layout in everyLayout do
            for mode in [ ValueMode.Clipped; ValueMode.Full ] do
              for width in 0 .. 900 do
                let fromFit = (ViewColumns.fit mode layout width).Hidden |> Set.ofList
                let fromRules = hiddenByRules ViewColumns.containerRules mode layout width
                if fromFit <> fromRules then yield mode, layout, width ]
      disagreements |> Expect.isEmpty "no width where the rules and the fit disagree"

    testCase "TWIN — rules that hide State at the width Type should go are caught, so the thresholds are tested and not assumed" <| fun _ ->
      let swapped =
        ViewColumns.containerRules
        |> List.map (fun rule ->
          match rule.Hides with
          | OptionalColumn.Type -> { rule with Hides = OptionalColumn.State }
          | _ -> rule)
      let caught =
        [ for layout in everyLayout do
            for width in 0 .. 900 do
              if (ViewColumns.fit ValueMode.Clipped layout width).Hidden |> Set.ofList <> hiddenByRules swapped ValueMode.Clipped layout width then yield width ]
      caught |> Expect.isNonEmpty "a wrong column is noticed"

    testCase "a layout that asks for no optional column has no rule, and says so in its token" <| fun _ ->
      ViewColumns.containerRules |> List.filter (fun rule -> rule.Requested.IsEmpty) |> Expect.isEmpty "nothing to hide"
      ViewColumns.requestedToken { ViewPreset.flat with Type = Off } |> Expect.equal "the token for no columns" ""

    testCase "the token lists the requested columns in hiding order, as words a stylesheet can match one at a time" <| fun _ ->
      ViewColumns.requestedToken { ViewPreset.flat with Type = On; State = On; Source = On } |> Expect.equal "all three" "type source state"
      ViewColumns.requestedToken { ViewPreset.flat with Type = Off; State = On } |> Expect.equal "state alone" "state"
  ]

/// Reading a workflow's view from the layer files (`SettingsCatalog.viewFor`), the way the dock does for the app it draws.
[<Tests>]
let viewForTests =
  let withApp (body: ConfigPaths -> unit) =
    let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "sagefs-viewfor-%s" (System.Guid.NewGuid().ToString "N"))
    let globalDir = System.IO.Path.Combine(root, "global")
    System.IO.Directory.CreateDirectory globalDir |> ignore
    try body { GlobalDir = globalDir; Repo = RepoRootAt root }
    finally (try System.IO.Directory.Delete(root, true) with _ -> ())
  let store (paths: ConfigPaths) (layer: ConfigLayer) (workflow: ViewWorkflow) (value: AxisValue) =
    let d = ViewSettings.descriptor workflow (AxisValue.axisOf value)
    match SettingsCatalog.edit paths layer (AxisValue.token value) d with
    | Result.Ok _ -> ()
    | Result.Error e -> failtestf "the edit should persist: %A" e
  testList "SettingsCatalog.viewFor" [
    testCase "WHY — an app nobody touched shows the workflow's recommended view, and says every axis is a default" <| fun _ ->
      withApp (fun paths ->
        for workflow in ViewWorkflow.all do
          let resolved = SettingsCatalog.viewFor paths workflow
          resolved.View |> Expect.equal (sprintf "%A follows its recommendation" workflow) (View.recommended workflow)
          resolved.Sources |> Map.forall (fun _ layer -> layer = LDefault) |> Expect.isTrue "no axis claims a file")

    testCase "WHY — a choice saved for this app changes that workflow only, and says it came from the repo layer" <| fun _ ->
      withApp (fun paths ->
        store paths LRepo ViewWorkflow.Repl (AxisValue.TypeColumn Off)
        store paths LRepo ViewWorkflow.Repl (AxisValue.ValueMode ValueMode.Full)
        let repl = SettingsCatalog.viewFor paths ViewWorkflow.Repl
        repl.View.Layout.Type |> Expect.equal "the Type column is off" Off
        repl.View.Values |> Expect.equal "values print in full" ValueMode.Full
        repl.Sources |> Map.find ViewAxis.TypeColumn |> Expect.equal "from the repo layer" LRepo
        (SettingsCatalog.viewFor paths ViewWorkflow.HotReload).View
        |> Expect.equal "hot reload is untouched" (View.recommended ViewWorkflow.HotReload))

    testCase "WHY — the repo layer beats the global one for the same axis" <| fun _ ->
      withApp (fun paths ->
        store paths LGlobal ViewWorkflow.LiveTesting (AxisValue.RowHeight RowHeight.Roomy)
        store paths LRepo ViewWorkflow.LiveTesting (AxisValue.RowHeight RowHeight.Compact)
        (SettingsCatalog.viewFor paths ViewWorkflow.LiveTesting).View.Row |> Expect.equal "this app's choice wins" RowHeight.Compact)

    testCase "WHY — a layer file edited by hand to nonsense leaves the page working on the recommended view" <| fun _ ->
      withApp (fun paths ->
        let key = ViewSettings.key ViewWorkflow.Repl ViewAxis.RowHeight
        System.IO.File.WriteAllText(SettingsStore.globalPath paths.GlobalDir, sprintf "{ \"%s\": \"gigantic\" }" key)
        (SettingsCatalog.viewFor paths ViewWorkflow.Repl).View |> Expect.equal "the recommendation" (View.recommended ViewWorkflow.Repl))
  ]

/// What a person does in the view panel, turned into writes for one layer (docs/plans/live-bindings-watch-view.md, phase 1, step 3).
/// A layer is modelled as the choices it holds, and the view is what those choices make over the layer below.
[<Tests>]
let viewEditTests =
  let apply (layer: Map<ViewAxis, AxisValue>) (writes: AxisWrite list) : Map<ViewAxis, AxisValue> =
    writes
    |> List.fold
      (fun held write ->
        match write.Write with
        | Write.Store value -> Map.add write.Axis value held
        | Write.ClearOverride -> Map.remove write.Axis held)
      layer
  let resolve (below: View) (layer: Map<ViewAxis, AxisValue>) : View =
    layer |> Map.fold (fun view _ value -> View.withValue value view) below
  let pick (rng: System.Random) (axis: ViewAxis) : AxisValue =
    let all = AxisValue.allFor axis
    all.[rng.Next all.Length]
  /// A below-view and a layer that already holds some choices, both built from a seed.
  let situation (seed: int) : View * Map<ViewAxis, AxisValue> =
    let rng = System.Random seed
    let workflow = ViewWorkflow.all.[rng.Next ViewWorkflow.all.Length]
    let below = ViewAxis.all |> List.fold (fun view axis -> View.withValue (pick rng axis) view) (View.recommended workflow)
    let layer = ViewAxis.all |> List.filter (fun _ -> rng.Next 2 = 0) |> List.map (fun axis -> axis, pick rng axis) |> Map.ofList
    below, layer
  let editFor (seed: int) : ViewEdit =
    let rng = System.Random(seed * 31 + 7)
    match rng.Next 4 with
    | 0 -> ViewEdit.Set(pick rng ViewAxis.all.[rng.Next ViewAxis.all.Length])
    | 1 -> ViewEdit.Reset ViewAxis.all.[rng.Next ViewAxis.all.Length]
    | 2 -> ViewEdit.Preset ViewPreset.all.[rng.Next ViewPreset.all.Length]
    | _ -> ViewEdit.ResetAll
  testList "ViewEdit" [

    testProperty "WHY — choosing a value sets that axis and leaves every other axis as it was" <| fun (seed: int) ->
      let below, layer = situation seed
      let rng = System.Random seed
      let value = pick rng ViewAxis.all.[rng.Next ViewAxis.all.Length]
      let before = resolve below layer
      let after = resolve below (apply layer (ViewEdit.writes below (ViewEdit.Set value)))
      let axis = AxisValue.axisOf value
      View.valueOf after axis = value
      && (ViewAxis.all |> List.filter ((<>) axis) |> List.forall (fun other -> View.valueOf after other = View.valueOf before other))

    testProperty "WHY — a preset sets the six layout axes and leaves value printing and row height alone" <| fun (seed: int) ->
      let below, layer = situation seed
      let preset = ViewPreset.all.[System.Random(seed).Next ViewPreset.all.Length]
      let before = resolve below layer
      let after = resolve below (apply layer (ViewEdit.writes below (ViewEdit.Preset preset)))
      after.Layout = ViewPreset.layout preset && after.Values = before.Values && after.Row = before.Row

    testProperty "WHY — resetting an axis makes it follow the layer below again, and nothing else moves" <| fun (seed: int) ->
      let below, layer = situation seed
      let axis = ViewAxis.all.[System.Random(seed).Next ViewAxis.all.Length]
      let before = resolve below layer
      let after = resolve below (apply layer (ViewEdit.writes below (ViewEdit.Reset axis)))
      View.valueOf after axis = View.valueOf below axis
      && (ViewAxis.all |> List.filter ((<>) axis) |> List.forall (fun other -> View.valueOf after other = View.valueOf before other))

    testProperty "WHY — reset all leaves the layer holding nothing, so the view is the layer below's" <| fun (seed: int) ->
      let below, layer = situation seed
      let after = apply layer (ViewEdit.writes below ViewEdit.ResetAll)
      Map.isEmpty after && resolve below after = below

    testProperty "WHY — no edit stores a value the layer below already gives, so a layer file holds only real overrides" <| fun (seed: int) ->
      let below, _ = situation seed
      ViewEdit.writes below (editFor seed)
      |> List.forall (fun write ->
        match write.Write with
        | Write.Store value -> value <> View.valueOf below write.Axis
        | Write.ClearOverride -> true)

    testCase "WHY — a twin that stores every chosen value, even one equal to the layer below, is caught by the no-redundant-override rule" <| fun _ ->
      // The twin: a writes function without planWrite. On the recommended view, choosing what is already shown stores a choice.
      let below = View.recommended ViewWorkflow.Repl
      let twin (value: AxisValue) = [ { Axis = AxisValue.axisOf value; Write = Write.Store value } ]
      let same = View.valueOf below ViewAxis.RowHeight
      twin same |> List.forall (fun w -> match w.Write with Write.Store v -> v <> View.valueOf below w.Axis | Write.ClearOverride -> true)
      |> Expect.isFalse "the twin stores a redundant override"
      ViewEdit.writes below (ViewEdit.Set same) |> Expect.equal "the real one clears it instead" [ { Axis = ViewAxis.RowHeight; Write = Write.ClearOverride } ]
  ]

/// The request the view panel posts, parsed. Every token the panel can send is one the parser takes, and nothing else is.
[<Tests>]
let viewEditParseTests =
  testList "ViewEdit.parse" [

    testProperty "WHY — every value of every axis, posted the way the panel posts it, parses back to the edit it names" <| fun (workflowSeed: int) ->
      let workflow = ViewWorkflow.all.[abs (workflowSeed % ViewWorkflow.all.Length)]
      ViewAxis.all
      |> List.collect AxisValue.allFor
      |> List.forall (fun value ->
        let axis = AxisValue.axisOf value
        ViewEdit.parse workflow { Verb = ViewAction.token ViewAction.SetAxis; Setting = ViewAxis.token axis; Choice = AxisValue.token value } = Ok(ViewEdit.Set value))

    testCase "WHY — each preset's token parses to it, and 'recommended' means the workflow's own preset" <| fun _ ->
      for preset in ViewPreset.all do
        ViewEdit.parse ViewWorkflow.Repl { Verb = ViewAction.token ViewAction.ApplyPreset; Setting = ""; Choice = ViewEdit.presetToken preset }
        |> Expect.equal "the preset" (Ok(ViewEdit.Preset preset))
      for workflow in ViewWorkflow.all do
        ViewEdit.parse workflow { Verb = ViewAction.token ViewAction.ApplyPreset; Setting = ""; Choice = ViewEdit.RecommendedToken }
        |> Expect.equal "the workflow's own" (Ok(ViewEdit.Preset(ViewPreset.recommendedFor workflow)))

    testCase "WHY — reset names its axis and reset-all needs none" <| fun _ ->
      ViewEdit.parse ViewWorkflow.Repl { Verb = ViewAction.token ViewAction.ResetAxis; Setting = ViewAxis.token ViewAxis.RowHeight; Choice = "" }
      |> Expect.equal "reset one" (Ok(ViewEdit.Reset ViewAxis.RowHeight))
      ViewEdit.parse ViewWorkflow.Repl { Verb = ViewAction.token ViewAction.ResetEverything; Setting = ""; Choice = "" }
      |> Expect.equal "reset all" (Ok ViewEdit.ResetAll)

    testCase "WHY — a verb, an axis, a value or a preset that is not one of the closed sets is refused with the set it should have been" <| fun _ ->
      let refused (result: Result<ViewEdit, ConfigError>) =
        match result with
        | Error(NotAMember(_, allowed)) -> not allowed.IsEmpty
        | Error _ | Ok _ -> false
      ViewEdit.parse ViewWorkflow.Repl { Verb = "explode"; Setting = ""; Choice = "" } |> refused |> Expect.isTrue "unknown verb"
      ViewEdit.parse ViewWorkflow.Repl { Verb = "set"; Setting = "colour"; Choice = "red" } |> refused |> Expect.isTrue "unknown axis"
      ViewEdit.parse ViewWorkflow.Repl { Verb = "set"; Setting = "row"; Choice = "gigantic" } |> refused |> Expect.isTrue "unknown value"
      ViewEdit.parse ViewWorkflow.Repl { Verb = "preset"; Setting = ""; Choice = "sparkly" } |> refused |> Expect.isTrue "unknown preset"

    testCase "WHY — the scope tokens round-trip and an unknown one is refused" <| fun _ ->
      for scope in ViewScope.all do
        ViewScope.parse (ViewScope.token scope) |> Expect.equal "round trip" (Ok scope)
      ViewScope.parse "galaxy" |> Expect.isError "unknown scope"
  ]

/// A view edit carried out against the layer files (`SettingsCatalog.applyViewEdit`), the way the view panel's route does.
[<Tests>]
let applyViewEditTests =
  let withApp (body: ConfigPaths -> unit) =
    let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), sprintf "sagefs-viewedit-%s" (System.Guid.NewGuid().ToString "N"))
    let globalDir = System.IO.Path.Combine(root, "global")
    System.IO.Directory.CreateDirectory globalDir |> ignore
    try body { GlobalDir = globalDir; Repo = RepoRootAt root }
    finally (try System.IO.Directory.Delete(root, true) with _ -> ())
  let applied (paths: ConfigPaths) (workflow: ViewWorkflow) (scope: ViewScope) (edit: ViewEdit) =
    match SettingsCatalog.applyViewEdit paths workflow scope edit with
    | Result.Ok() -> ()
    | Result.Error e -> failtestf "the edit should apply: %s" (ConfigError.describe e)
  let repoFile (paths: ConfigPaths) =
    match paths.Repo with
    | RepoRootAt root -> SettingsStore.readLayer (SettingsStore.repoPath root)
    | NoRepoCheckout -> Map.empty
  testList "SettingsCatalog.applyViewEdit" [

    testCase "WHY — a choice saved for this app lands in the app's file and shows up in the view" <| fun _ ->
      withApp (fun paths ->
        applied paths ViewWorkflow.Repl ViewScope.ThisApp (ViewEdit.Set(AxisValue.RowHeight RowHeight.Roomy))
        (SettingsCatalog.viewFor paths ViewWorkflow.Repl).View.Row |> Expect.equal "roomy" RowHeight.Roomy
        Map.containsKey (ViewSettings.key ViewWorkflow.Repl ViewAxis.RowHeight) (repoFile paths) |> Expect.isTrue "stored in the repo layer")

    testCase "WHY — a choice saved everywhere lands in the global file and applies to an app with no choice of its own" <| fun _ ->
      withApp (fun paths ->
        applied paths ViewWorkflow.Repl ViewScope.Everywhere (ViewEdit.Set(AxisValue.TypeColumn Off))
        (SettingsCatalog.viewFor paths ViewWorkflow.Repl).View.Layout.Type |> Expect.equal "off" Off
        repoFile paths |> Map.isEmpty |> Expect.isTrue "nothing was written for the app")

    testCase "WHY — choosing what the layer below already shows stores nothing, so only real overrides are in a file" <| fun _ ->
      withApp (fun paths ->
        applied paths ViewWorkflow.Repl ViewScope.ThisApp (ViewEdit.Set(AxisValue.RowHeight RowHeight.Compact))
        repoFile paths |> Map.isEmpty |> Expect.isTrue "compact is already what the recommendation gives")

    testCase "WHY — an app's choice is measured against what everywhere gives, so putting an app back to the global choice clears it" <| fun _ ->
      withApp (fun paths ->
        applied paths ViewWorkflow.Repl ViewScope.Everywhere (ViewEdit.Set(AxisValue.RowHeight RowHeight.Roomy))
        applied paths ViewWorkflow.Repl ViewScope.ThisApp (ViewEdit.Set(AxisValue.RowHeight RowHeight.Compact))
        (SettingsCatalog.viewFor paths ViewWorkflow.Repl).View.Row |> Expect.equal "the app's own choice wins" RowHeight.Compact
        applied paths ViewWorkflow.Repl ViewScope.ThisApp (ViewEdit.Set(AxisValue.RowHeight RowHeight.Roomy))
        repoFile paths |> Map.isEmpty |> Expect.isTrue "roomy is what everywhere gives, so the app stores nothing")

    testCase "WHY — resetting an axis clears the layer that holds it, whichever scope the panel shows" <| fun _ ->
      withApp (fun paths ->
        applied paths ViewWorkflow.Repl ViewScope.Everywhere (ViewEdit.Set(AxisValue.RowHeight RowHeight.Roomy))
        applied paths ViewWorkflow.Repl ViewScope.ThisApp (ViewEdit.Reset ViewAxis.RowHeight)
        (SettingsCatalog.viewFor paths ViewWorkflow.Repl).View.Row |> Expect.equal "back to the recommendation" RowHeight.Compact)

    testCase "WHY — reset all drops this scope's choices for the workflow only" <| fun _ ->
      withApp (fun paths ->
        applied paths ViewWorkflow.Repl ViewScope.ThisApp (ViewEdit.Set(AxisValue.RowHeight RowHeight.Roomy))
        applied paths ViewWorkflow.HotReload ViewScope.ThisApp (ViewEdit.Set(AxisValue.RowHeight RowHeight.Roomy))
        applied paths ViewWorkflow.Repl ViewScope.ThisApp ViewEdit.ResetAll
        (SettingsCatalog.viewFor paths ViewWorkflow.Repl).View.Row |> Expect.equal "repl is back" RowHeight.Compact
        (SettingsCatalog.viewFor paths ViewWorkflow.HotReload).View.Row |> Expect.equal "hot reload kept its choice" RowHeight.Roomy)

    testCase "WHY — a preset saved for this app sets the layout and leaves the value mode alone" <| fun _ ->
      withApp (fun paths ->
        applied paths ViewWorkflow.Repl ViewScope.ThisApp (ViewEdit.Set(AxisValue.ValueMode ValueMode.Full))
        applied paths ViewWorkflow.Repl ViewScope.ThisApp (ViewEdit.Preset ViewPreset.Tuner)
        let view = (SettingsCatalog.viewFor paths ViewWorkflow.Repl).View
        view.Layout |> Expect.equal "the tuner layout" (ViewPreset.layout ViewPreset.Tuner)
        view.Values |> Expect.equal "values untouched" ValueMode.Full)

    testCase "WHY — saving for an app with no checkout is refused, not written somewhere else" <| fun _ ->
      withApp (fun paths ->
        let noRepo = { paths with Repo = NoRepoCheckout }
        SettingsCatalog.applyViewEdit noRepo ViewWorkflow.Repl ViewScope.ThisApp (ViewEdit.Set(AxisValue.RowHeight RowHeight.Roomy))
        |> Expect.isError "there is no app layer to write")
  ]
