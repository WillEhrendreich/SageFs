namespace SageFs

open System

/// The Live Bindings view as settings (docs/plans/live-bindings-watch-view.md, phase 1, step 1).
///
/// A view is a handful of independent axes: which optional columns show, whether rows group by origin, whether a pinned strip and an
/// inspector show, how much of a value prints, and how tall a row is. The four layouts the roast of the pane produced are presets of
/// this one component. Each (workflow, axis) pair is a setting in the unified catalog (`Settings.fs`), so the layers, the provenance
/// and the file store that already exist do the persisting: a value is stored only where someone chose it, and an axis nobody
/// touched follows the recommended view for its workflow.
///
/// Everything here is pure. Reading and writing the layer files is `SettingsCatalog`'s job; this module says what the settings are,
/// what the recommended view is, what a write should do, and which columns fit.

/// The workflow a view belongs to. `WorkflowTypes.SessionWorkflow` carries data (the hot reload case holds a browser refresh
/// config), so it cannot be a settings key. This is the closed set a key can be built from.
[<RequireQualifiedAccess>]
type ViewWorkflow =
  | Repl
  | LiveTesting
  | HotReload

/// The settings a view is made of, in the order the view panel lists them.
[<RequireQualifiedAccess>]
type ViewAxis =
  | TypeColumn
  | StateColumn
  | SourceColumn
  | GroupBy
  | PinnedStrip
  | Inspector
  | ValueMode
  | RowHeight

[<RequireQualifiedAccess>]
type GroupMode =
  | NoGrouping
  | ByOrigin

/// How much of a value a row prints. `Full` wraps instead of clipping.
[<RequireQualifiedAccess>]
type ValueMode =
  | Clipped
  | Full

[<RequireQualifiedAccess>]
type RowHeight =
  | Compact
  | Roomy

/// The six axes a preset sets. Value printing and row height are personal and stay out of it.
type Layout =
  { Type: Toggle
    State: Toggle
    Source: Toggle
    Group: GroupMode
    Strip: Toggle
    Inspector: Toggle }

type View =
  { Layout: Layout
    Values: ValueMode
    Row: RowHeight }

/// One axis with its value, so an override is a list of these and an axis cannot be given a value of another axis's type.
[<RequireQualifiedAccess>]
type AxisValue =
  | TypeColumn of Toggle
  | StateColumn of Toggle
  | SourceColumn of Toggle
  | GroupBy of GroupMode
  | PinnedStrip of Toggle
  | Inspector of Toggle
  | ValueMode of ValueMode
  | RowHeight of RowHeight

[<RequireQualifiedAccess>]
type ViewPreset =
  | WatchWindow
  | TreeAndInspector
  | PinnedStrip
  | GroupedByOrigin
  | Tuner

/// Whether a layout is the recommended one for its workflow, a named preset, or neither.
[<RequireQualifiedAccess>]
type DetectedPreset =
  | IsRecommended
  | IsPreset of ViewPreset
  | IsCustom

/// What a write to a layer should do. A value equal to what the layer below would give is not stored, so only overrides are stored.
[<RequireQualifiedAccess>]
type Write =
  | Store of AxisValue
  | ClearOverride

/// The columns that give way when the pane is too narrow, in the order they are hidden.
[<RequireQualifiedAccess>]
type OptionalColumn =
  | Type
  | Source
  | State

/// What fits. `Shown` and `Hidden` are in hiding order. Name and Value are not optional and are always shown.
type Fit =
  { Shown: OptionalColumn list
    Hidden: OptionalColumn list }

/// A view with where each axis's value came from.
type ResolvedView =
  { View: View
    Sources: Map<ViewAxis, ConfigLayer> }

[<RequireQualifiedAccess>]
module ViewWorkflow =
  let all : ViewWorkflow list = [ ViewWorkflow.Repl; ViewWorkflow.LiveTesting; ViewWorkflow.HotReload ]

  /// The word in a settings key.
  let token (workflow: ViewWorkflow) : string =
    match workflow with
    | ViewWorkflow.Repl -> "repl"
    | ViewWorkflow.LiveTesting -> "livetesting"
    | ViewWorkflow.HotReload -> "hotreload"

  /// The words a person reads, the same as `WorkflowTypes.SessionWorkflow.label`.
  let label (workflow: ViewWorkflow) : string =
    match workflow with
    | ViewWorkflow.Repl -> "REPL"
    | ViewWorkflow.LiveTesting -> "Live testing"
    | ViewWorkflow.HotReload -> "Hot reload"

  let ofSessionWorkflow (workflow: WorkflowTypes.SessionWorkflow) : ViewWorkflow =
    match workflow with
    | WorkflowTypes.SessionWorkflow.Interactive -> ViewWorkflow.Repl
    | WorkflowTypes.SessionWorkflow.LiveTesting -> ViewWorkflow.LiveTesting
    | WorkflowTypes.SessionWorkflow.HotReload _ -> ViewWorkflow.HotReload

[<RequireQualifiedAccess>]
module ViewAxis =
  let all : ViewAxis list =
    [ ViewAxis.TypeColumn
      ViewAxis.StateColumn
      ViewAxis.SourceColumn
      ViewAxis.GroupBy
      ViewAxis.PinnedStrip
      ViewAxis.Inspector
      ViewAxis.ValueMode
      ViewAxis.RowHeight ]

  /// The axes the dock draws today, in the order the view panel lists them. The other settings exist and persist, and nothing
  /// draws them yet; the panel offers a control only for an axis here, so it has no control that changes nothing. A test holds this
  /// list to the dock: each drawn axis, set to each of its values, must change what the dock draws.
  let drawn : ViewAxis list = [ ViewAxis.TypeColumn; ViewAxis.ValueMode; ViewAxis.RowHeight ]

  /// The word in a settings key.
  let token (axis: ViewAxis) : string =
    match axis with
    | ViewAxis.TypeColumn -> "type"
    | ViewAxis.StateColumn -> "state"
    | ViewAxis.SourceColumn -> "source"
    | ViewAxis.GroupBy -> "group"
    | ViewAxis.PinnedStrip -> "strip"
    | ViewAxis.Inspector -> "inspector"
    | ViewAxis.ValueMode -> "values"
    | ViewAxis.RowHeight -> "row"

  let label (axis: ViewAxis) : string =
    match axis with
    | ViewAxis.TypeColumn -> "Type column"
    | ViewAxis.StateColumn -> "State column"
    | ViewAxis.SourceColumn -> "Source column"
    | ViewAxis.GroupBy -> "Group by"
    | ViewAxis.PinnedStrip -> "Pinned strip"
    | ViewAxis.Inspector -> "Inspector"
    | ViewAxis.ValueMode -> "Values"
    | ViewAxis.RowHeight -> "Row height"

  let description (axis: ViewAxis) : string =
    match axis with
    | ViewAxis.TypeColumn -> "Show each value's type in its own column."
    | ViewAxis.StateColumn -> "Show how each value relates to its source: source, tweaked, formula, evolved or live only."
    | ViewAxis.SourceColumn -> "Show the file and line each value comes from."
    | ViewAxis.GroupBy -> "Group rows by where the value lives: in a file, REPL only, or not evaluated."
    | ViewAxis.PinnedStrip -> "Keep pinned values in a strip above the tree so they stay in view while it scrolls."
    | ViewAxis.Inspector -> "Show the selected row's type, origin, full value and controls in a panel beside the tree."
    | ViewAxis.ValueMode -> "Clip each value to one line, or print it in full and wrap it."
    | ViewAxis.RowHeight -> "How tall a row is."

[<RequireQualifiedAccess>]
module AxisValue =
  let axisOf (value: AxisValue) : ViewAxis =
    match value with
    | AxisValue.TypeColumn _ -> ViewAxis.TypeColumn
    | AxisValue.StateColumn _ -> ViewAxis.StateColumn
    | AxisValue.SourceColumn _ -> ViewAxis.SourceColumn
    | AxisValue.GroupBy _ -> ViewAxis.GroupBy
    | AxisValue.PinnedStrip _ -> ViewAxis.PinnedStrip
    | AxisValue.Inspector _ -> ViewAxis.Inspector
    | AxisValue.ValueMode _ -> ViewAxis.ValueMode
    | AxisValue.RowHeight _ -> ViewAxis.RowHeight

  let toggleToken (t: Toggle) : string =
    match t with
    | On -> "on"
    | Off -> "off"

  /// The text a value is stored as in a layer file.
  let token (value: AxisValue) : string =
    match value with
    | AxisValue.TypeColumn t
    | AxisValue.StateColumn t
    | AxisValue.SourceColumn t
    | AxisValue.PinnedStrip t
    | AxisValue.Inspector t -> toggleToken t
    | AxisValue.GroupBy GroupMode.NoGrouping -> "none"
    | AxisValue.GroupBy GroupMode.ByOrigin -> "origin"
    | AxisValue.ValueMode ValueMode.Clipped -> "clipped"
    | AxisValue.ValueMode ValueMode.Full -> "full"
    | AxisValue.RowHeight RowHeight.Compact -> "compact"
    | AxisValue.RowHeight RowHeight.Roomy -> "roomy"

  /// The words a person reads on the button for a value.
  let label (value: AxisValue) : string =
    match value with
    | AxisValue.TypeColumn t
    | AxisValue.StateColumn t
    | AxisValue.SourceColumn t
    | AxisValue.PinnedStrip t
    | AxisValue.Inspector t ->
      match t with
      | On -> "On"
      | Off -> "Off"
    | AxisValue.GroupBy GroupMode.NoGrouping -> "None"
    | AxisValue.GroupBy GroupMode.ByOrigin -> "By origin"
    | AxisValue.ValueMode ValueMode.Clipped -> "Clipped"
    | AxisValue.ValueMode ValueMode.Full -> "Full"
    | AxisValue.RowHeight RowHeight.Compact -> "Compact"
    | AxisValue.RowHeight RowHeight.Roomy -> "Roomy"

  /// Every legal value of an axis, which is also the closed set its setting accepts.
  let allFor (axis: ViewAxis) : AxisValue list =
    match axis with
    | ViewAxis.TypeColumn -> [ AxisValue.TypeColumn On; AxisValue.TypeColumn Off ]
    | ViewAxis.StateColumn -> [ AxisValue.StateColumn On; AxisValue.StateColumn Off ]
    | ViewAxis.SourceColumn -> [ AxisValue.SourceColumn On; AxisValue.SourceColumn Off ]
    | ViewAxis.GroupBy -> [ AxisValue.GroupBy GroupMode.NoGrouping; AxisValue.GroupBy GroupMode.ByOrigin ]
    | ViewAxis.PinnedStrip -> [ AxisValue.PinnedStrip On; AxisValue.PinnedStrip Off ]
    | ViewAxis.Inspector -> [ AxisValue.Inspector On; AxisValue.Inspector Off ]
    | ViewAxis.ValueMode -> [ AxisValue.ValueMode ValueMode.Clipped; AxisValue.ValueMode ValueMode.Full ]
    | ViewAxis.RowHeight -> [ AxisValue.RowHeight RowHeight.Compact; AxisValue.RowHeight RowHeight.Roomy ]

  /// The only place a raw string becomes an axis value. A value outside the axis's set says what the set is.
  let parse (axis: ViewAxis) (raw: string) : Result<AxisValue, ConfigError> =
    let candidates = allFor axis
    match candidates |> List.tryFind (fun v -> token v = raw) with
    | Some value -> Ok value
    | None -> Error(NotAMember(raw, candidates |> List.map token))

[<RequireQualifiedAccess>]
module ViewPreset =
  let all : ViewPreset list =
    [ ViewPreset.WatchWindow
      ViewPreset.TreeAndInspector
      ViewPreset.PinnedStrip
      ViewPreset.GroupedByOrigin
      ViewPreset.Tuner ]

  let label (preset: ViewPreset) : string =
    match preset with
    | ViewPreset.WatchWindow -> "Watch window"
    | ViewPreset.TreeAndInspector -> "Tree and inspector"
    | ViewPreset.PinnedStrip -> "Pinned strip"
    | ViewPreset.GroupedByOrigin -> "Grouped by origin"
    | ViewPreset.Tuner -> "Tuner"

  /// Name, Value and Type on one line, nothing else.
  let flat : Layout =
    { Type = On
      State = Off
      Source = Off
      Group = GroupMode.NoGrouping
      Strip = Off
      Inspector = Off }

  let layout (preset: ViewPreset) : Layout =
    match preset with
    | ViewPreset.WatchWindow -> flat
    | ViewPreset.TreeAndInspector -> { flat with Type = Off; Inspector = On }
    | ViewPreset.PinnedStrip -> { flat with Type = Off; Strip = On }
    | ViewPreset.GroupedByOrigin -> { flat with Group = GroupMode.ByOrigin }
    | ViewPreset.Tuner ->
      { Type = On
        State = On
        Source = On
        Group = GroupMode.NoGrouping
        Strip = On
        Inspector = On }

  /// Watch window for REPL and Live testing, where nothing is tweaked. Tuner for Hot reload, where tweaking needs State and Source,
  /// the strip and the inspector together.
  let recommendedFor (workflow: ViewWorkflow) : ViewPreset =
    match workflow with
    | ViewWorkflow.Repl
    | ViewWorkflow.LiveTesting -> ViewPreset.WatchWindow
    | ViewWorkflow.HotReload -> ViewPreset.Tuner

[<RequireQualifiedAccess>]
module View =
  /// The built-in view for a workflow: its recommended layout, clipped values, compact rows.
  let recommended (workflow: ViewWorkflow) : View =
    { Layout = ViewPreset.layout (ViewPreset.recommendedFor workflow)
      Values = ValueMode.Clipped
      Row = RowHeight.Compact }

  /// A view's value on one axis.
  let valueOf (view: View) (axis: ViewAxis) : AxisValue =
    match axis with
    | ViewAxis.TypeColumn -> AxisValue.TypeColumn view.Layout.Type
    | ViewAxis.StateColumn -> AxisValue.StateColumn view.Layout.State
    | ViewAxis.SourceColumn -> AxisValue.SourceColumn view.Layout.Source
    | ViewAxis.GroupBy -> AxisValue.GroupBy view.Layout.Group
    | ViewAxis.PinnedStrip -> AxisValue.PinnedStrip view.Layout.Strip
    | ViewAxis.Inspector -> AxisValue.Inspector view.Layout.Inspector
    | ViewAxis.ValueMode -> AxisValue.ValueMode view.Values
    | ViewAxis.RowHeight -> AxisValue.RowHeight view.Row

  /// The view with one axis set. Total, because an `AxisValue` already carries the right type for its axis.
  let withValue (value: AxisValue) (view: View) : View =
    match value with
    | AxisValue.TypeColumn t -> { view with Layout = { view.Layout with Type = t } }
    | AxisValue.StateColumn t -> { view with Layout = { view.Layout with State = t } }
    | AxisValue.SourceColumn t -> { view with Layout = { view.Layout with Source = t } }
    | AxisValue.GroupBy g -> { view with Layout = { view.Layout with Group = g } }
    | AxisValue.PinnedStrip t -> { view with Layout = { view.Layout with Strip = t } }
    | AxisValue.Inspector t -> { view with Layout = { view.Layout with Inspector = t } }
    | AxisValue.ValueMode m -> { view with Values = m }
    | AxisValue.RowHeight h -> { view with Row = h }

  /// Every axis of a view, in the order the view panel lists them.
  let axisValues (view: View) : AxisValue list = ViewAxis.all |> List.map (valueOf view)

  /// A preset sets the six layout axes and leaves value printing and row height as they are.
  let withPreset (preset: ViewPreset) (view: View) : View = { view with Layout = ViewPreset.layout preset }

  let detect (workflow: ViewWorkflow) (layout: Layout) : DetectedPreset =
    match layout = ViewPreset.layout (ViewPreset.recommendedFor workflow) with
    | true -> DetectedPreset.IsRecommended
    | false ->
      match ViewPreset.all |> List.tryFind (fun preset -> ViewPreset.layout preset = layout) with
      | Some preset -> DetectedPreset.IsPreset preset
      | None -> DetectedPreset.IsCustom

[<RequireQualifiedAccess>]
module ViewSettings =
  /// The settings key for one axis of one workflow's view.
  let key (workflow: ViewWorkflow) (axis: ViewAxis) : string =
    sprintf "bindings.view.%s.%s" (ViewWorkflow.token workflow) (ViewAxis.token axis)

  /// The setting value an axis value is held as: a toggle for the on/off axes, a member of the axis's closed set for the rest.
  let settingValueOf (value: AxisValue) : SettingValue =
    match value with
    | AxisValue.TypeColumn t
    | AxisValue.StateColumn t
    | AxisValue.SourceColumn t
    | AxisValue.PinnedStrip t
    | AxisValue.Inspector t -> VToggle t
    | AxisValue.GroupBy _
    | AxisValue.ValueMode _
    | AxisValue.RowHeight _ ->
      let allowed = AxisValue.allFor (AxisValue.axisOf value) |> List.map AxisValue.token
      match EnumValue.create allowed (AxisValue.token value) with
      | Ok chosen -> VEnum chosen
      // Unreachable: `allowed` is built from the same values as `chosen`. A test round-trips every value, so this cannot go unnoticed.
      | Error _ -> VToggle Off

  /// The axis value a stored setting holds, when it holds one of that axis.
  let axisValueOf (axis: ViewAxis) (value: SettingValue) : AxisValue option =
    match value with
    | VToggle t ->
      AxisValue.allFor axis
      |> List.tryFind (fun candidate -> AxisValue.token candidate = AxisValue.toggleToken t)
    | VEnum chosen -> AxisValue.parse axis (EnumValue.value chosen) |> Result.toOption
    | VPort _
    | VTimeout _
    | VBindHost _
    | VDir _ -> None

  /// The descriptor of one axis of one workflow's view. The default is the recommended view's value, so an untouched axis
  /// follows the recommendation and nothing is stored.
  let descriptor (workflow: ViewWorkflow) (axis: ViewAxis) : SettingDescriptor =
    { Key = key workflow axis
      Name = sprintf "%s: %s" (ViewWorkflow.label workflow) (ViewAxis.label axis)
      Description = ViewAxis.description axis
      Category = LiveBindingsView
      Scope = RepoOverridable
      Applicability = Live
      Default = settingValueOf (View.valueOf (View.recommended workflow) axis)
      Parse = fun raw -> AxisValue.parse axis raw |> Result.map settingValueOf
      Render =
        fun value ->
          match axisValueOf axis value with
          | Some v -> AxisValue.token v
          | None -> ""
      Apply = ignore }

  /// All 24 settings: three workflows by eight axes, each key distinct.
  let descriptors : SettingDescriptor list =
    [ for workflow in ViewWorkflow.all do
        for axis in ViewAxis.all -> descriptor workflow axis ]

  /// The view a workflow resolves to, given each axis's resolved setting. An axis whose stored value is not one of that axis's
  /// (a layer file edited by hand) keeps the recommended value and says its source is the default.
  let resolveView (workflow: ViewWorkflow) (provenanceOf: ViewAxis -> Provenance) : ResolvedView =
    let recommended = View.recommended workflow
    let step (view, sources) axis =
      let provenance = provenanceOf axis
      match axisValueOf axis provenance.Effective with
      | Some value -> View.withValue value view, Map.add axis provenance.Source sources
      | None -> view, Map.add axis LDefault sources
    let view, sources = ViewAxis.all |> List.fold step (recommended, Map.empty)
    { View = view; Sources = sources }

  /// What a write of `chosen` should do, given what the layer below would give for the axis. Equal means nothing to store.
  let planWrite (below: AxisValue) (chosen: AxisValue) : Write =
    match below = chosen with
    | true -> Write.ClearOverride
    | false -> Write.Store chosen

/// What a person does in the view panel, before it is turned into writes to a layer.
[<RequireQualifiedAccess>]
type ViewEdit =
  /// Choose a value for one axis.
  | Set of AxisValue
  /// Drop this layer's choice for one axis, so it follows the layer below again.
  | Reset of ViewAxis
  /// Take a preset's six layout axes. Value printing and row height are left as they are.
  | Preset of ViewPreset
  /// Drop every choice this layer holds for the workflow.
  | ResetAll

/// One axis of a layer, and what to do to it.
type AxisWrite = { Axis: ViewAxis; Write: Write }

/// The verbs the view panel posts. Closed, so the panel and the endpoint cannot disagree about a spelling.
[<RequireQualifiedAccess>]
type ViewAction =
  | SetAxis
  | ResetAxis
  | ApplyPreset
  | ResetEverything

/// Where a view edit is saved: this app's own settings file, or the person's global one.
[<RequireQualifiedAccess>]
type ViewScope =
  | ThisApp
  | Everywhere

[<RequireQualifiedAccess>]
module ViewAction =
  let all : ViewAction list = [ ViewAction.SetAxis; ViewAction.ResetAxis; ViewAction.ApplyPreset; ViewAction.ResetEverything ]

  let token (action: ViewAction) : string =
    match action with
    | ViewAction.SetAxis -> "set"
    | ViewAction.ResetAxis -> "reset"
    | ViewAction.ApplyPreset -> "preset"
    | ViewAction.ResetEverything -> "resetall"

  let parse (raw: string) : Result<ViewAction, ConfigError> =
    match all |> List.tryFind (fun action -> token action = raw) with
    | Some action -> Ok action
    | None -> Error(NotAMember(raw, all |> List.map token))

[<RequireQualifiedAccess>]
module ViewScope =
  let all : ViewScope list = [ ViewScope.ThisApp; ViewScope.Everywhere ]

  let token (scope: ViewScope) : string =
    match scope with
    | ViewScope.ThisApp -> "app"
    | ViewScope.Everywhere -> "everywhere"

  let label (scope: ViewScope) : string =
    match scope with
    | ViewScope.ThisApp -> "This app"
    | ViewScope.Everywhere -> "Everywhere"

  let parse (raw: string) : Result<ViewScope, ConfigError> =
    match all |> List.tryFind (fun scope -> token scope = raw) with
    | Some scope -> Ok scope
    | None -> Error(NotAMember(raw, all |> List.map token))

/// What a view panel click posts, as the three words it sends. Named fields, so the verb, the axis and the value cannot be passed
/// in each other's place.
type PostedViewEdit =
  { Verb: string
    Setting: string
    Choice: string }

[<RequireQualifiedAccess>]
module ViewEdit =
  /// The word a preset button posts. `recommended` is not one of the five presets: it means the workflow's own.
  [<Literal>]
  let RecommendedToken = "recommended"

  let presetToken (preset: ViewPreset) : string =
    match preset with
    | ViewPreset.WatchWindow -> "watchwindow"
    | ViewPreset.TreeAndInspector -> "treeandinspector"
    | ViewPreset.PinnedStrip -> "pinnedstrip"
    | ViewPreset.GroupedByOrigin -> "groupedbyorigin"
    | ViewPreset.Tuner -> "tuner"

  /// The edit a posted request means, for one workflow: the axis and value tokens are read only for the actions that use them.
  let parse (workflow: ViewWorkflow) (posted: PostedViewEdit) : Result<ViewEdit, ConfigError> =
    let axisOf (raw: string) : Result<ViewAxis, ConfigError> =
      match ViewAxis.all |> List.tryFind (fun candidate -> ViewAxis.token candidate = raw) with
      | Some found -> Ok found
      | None -> Error(NotAMember(raw, ViewAxis.all |> List.map ViewAxis.token))
    let presetOf (raw: string) : Result<ViewPreset, ConfigError> =
      match raw = RecommendedToken with
      | true -> Ok(ViewPreset.recommendedFor workflow)
      | false ->
        match ViewPreset.all |> List.tryFind (fun candidate -> presetToken candidate = raw) with
        | Some found -> Ok found
        | None -> Error(NotAMember(raw, RecommendedToken :: (ViewPreset.all |> List.map presetToken)))
    ViewAction.parse posted.Verb
    |> Result.bind (fun parsed ->
      match parsed with
      | ViewAction.SetAxis -> axisOf posted.Setting |> Result.bind (fun found -> AxisValue.parse found posted.Choice) |> Result.map ViewEdit.Set
      | ViewAction.ResetAxis -> axisOf posted.Setting |> Result.map ViewEdit.Reset
      | ViewAction.ApplyPreset -> presetOf posted.Choice |> Result.map ViewEdit.Preset
      | ViewAction.ResetEverything -> Ok ViewEdit.ResetAll)

  /// The axes a preset sets.
  let private layoutAxes : ViewAxis list =
    [ ViewAxis.TypeColumn
      ViewAxis.StateColumn
      ViewAxis.SourceColumn
      ViewAxis.GroupBy
      ViewAxis.PinnedStrip
      ViewAxis.Inspector ]

  /// The writes an edit comes to for one layer, given the view the layer below would give. Only an override is stored: a value equal
  /// to what the layer below gives is cleared, so a layer file never holds a choice that changes nothing.
  let writes (below: View) (edit: ViewEdit) : AxisWrite list =
    let store (chosen: AxisValue) : AxisWrite =
      let axis = AxisValue.axisOf chosen
      { Axis = axis; Write = ViewSettings.planWrite (View.valueOf below axis) chosen }
    match edit with
    | ViewEdit.Set value -> [ store value ]
    | ViewEdit.Reset axis -> [ { Axis = axis; Write = Write.ClearOverride } ]
    | ViewEdit.Preset preset ->
      let chosen = View.withPreset preset below
      layoutAxes |> List.map (fun axis -> store (View.valueOf chosen axis))
    | ViewEdit.ResetAll -> ViewAxis.all |> List.map (fun axis -> { Axis = axis; Write = Write.ClearOverride })

[<RequireQualifiedAccess>]
module ViewColumns =
  /// The least width a column needs. A value needs more in `Full`, so columns give way sooner when it is on.
  [<Literal>]
  let MinName = 130

  [<Literal>]
  let MinValueClipped = 140

  [<Literal>]
  let MinValueFull = 320

  [<Literal>]
  let MinType = 70

  [<Literal>]
  let MinState = 96

  [<Literal>]
  let MinSource = 120

  /// The space between two columns.
  [<Literal>]
  let Gap = 8

  let minValue (values: ValueMode) : int =
    match values with
    | ValueMode.Clipped -> MinValueClipped
    | ValueMode.Full -> MinValueFull

  let minOf (column: OptionalColumn) : int =
    match column with
    | OptionalColumn.Type -> MinType
    | OptionalColumn.Source -> MinSource
    | OptionalColumn.State -> MinState

  /// The optional columns a layout asks for, in the order they are hidden.
  let requested (layout: Layout) : OptionalColumn list =
    [ match layout.Type with
      | On -> yield OptionalColumn.Type
      | Off -> ()
      match layout.Source with
      | On -> yield OptionalColumn.Source
      | Off -> ()
      match layout.State with
      | On -> yield OptionalColumn.State
      | Off -> () ]

  /// The width a set of optional columns needs beside Name and Value.
  let needed (values: ValueMode) (columns: OptionalColumn list) : int =
    MinName + minValue values + (columns |> List.sumBy minOf) + Gap * (columns.Length + 1)

  /// Columns give way before the value does. Type goes first, then Source, then State, until what remains fits `width`. Name and
  /// Value are never hidden, so when even they do not fit, every optional column is hidden and the rest is the pane's to scroll.
  let fit (values: ValueMode) (layout: Layout) (width: int) : Fit =
    let rec go (hidden: OptionalColumn list) (remaining: OptionalColumn list) : Fit =
      match remaining with
      | [] -> { Shown = []; Hidden = hidden }
      | _ when needed values remaining <= width -> { Shown = remaining; Hidden = hidden }
      | first :: rest -> go (hidden @ [ first ]) rest
    go [] (requested layout)
