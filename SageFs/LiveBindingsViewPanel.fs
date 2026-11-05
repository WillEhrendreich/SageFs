/// The view panel of the Live Bindings pane (docs/plans/live-bindings-watch-view.md, phase 1, step 3): a button in the pane bar that
/// opens a small panel where a person picks how the bindings are shown, and where that choice is saved.
///
/// It draws the resolved view (`SettingsCatalog.viewFor`) and posts one request per click, to one route per session
/// (`/dashboard/bindings-view/{id}`). The click stages its verb, axis and value in signals and posts them, the same way a row's knob
/// does. The server writes the layer file, and the one `#main` morph carries the new view back, so the panel and the dock cannot
/// disagree about what is chosen. Nothing here decides a write: `ViewEdit.writes` does.
///
/// A control is offered only for an axis the dock draws (`ViewAxis.drawn`), so no control changes nothing.
module SageFs.Server.LiveBindingsViewPanel

open System
open Falco.Markup
open Falco.Datastar
open SageFs
open SageFs.Server.DashboardFragments

/// Client-side state of the panel, declared once in the page shell with the dock's.
module ViewSignals =
  /// Whether the panel is open.
  let [<Literal>] Open = "bvOpen"
  /// The verb a click stages (`ViewAction.token`), with the axis and value it names, and where the choice is saved.
  let [<Literal>] Action = "bvAction"
  let [<Literal>] Axis = "bvAxis"
  let [<Literal>] Value = "bvValue"
  let [<Literal>] Scope = "bvScope"

  let initial : XmlAttribute list =
    [ Ds.signal (Open, false)
      Ds.signal (Action, "")
      Ds.signal (Axis, "")
      Ds.signal (Value, "")
      Ds.signal (Scope, ViewScope.token ViewScope.ThisApp) ]

module ViewIds =
  let [<Literal>] Button = "live-view-btn"
  let [<Literal>] Panel = "live-view-panel"
  let [<Literal>] ScopeSelect = "live-view-scope"

/// The route a click posts to.
let endpoint (sessionId: string) : string = sprintf "/dashboard/bindings-view/%s" (Uri.EscapeDataString sessionId)

/// The pane bar's button and the panel it opens, drawn together because they share one signal.
type ViewControls = { Button: XmlNode; Panel: XmlNode }

/// Where an axis's value came from, in the words a person reads.
let sourceWords (layer: ConfigLayer) : string =
  match layer with
  | LDefault -> "recommended"
  | LGlobal -> "saved for all projects"
  | LRepo -> "saved for this project"
  | LSession -> "this session only"

let private slidersIcon : XmlNode =
  Text.raw "<svg viewBox=\"0 0 16 16\" width=\"12\" height=\"12\" aria-hidden=\"true\"><path d=\"M2 4h12M2 8h12M2 12h12\" stroke=\"currentColor\" stroke-width=\"1.2\" fill=\"none\"/><circle cx=\"5\" cy=\"4\" r=\"1.6\" fill=\"currentColor\"/><circle cx=\"11\" cy=\"8\" r=\"1.6\" fill=\"currentColor\"/><circle cx=\"6\" cy=\"12\" r=\"1.6\" fill=\"currentColor\"/></svg>"

/// What a click stages before it posts.
let private click (sessionId: string) (action: ViewAction) (axis: string) (value: string) : string =
  attrEnc (
    sprintf "$%s = '%s'; $%s = '%s'; $%s = '%s'; " ViewSignals.Action (ViewAction.token action) ViewSignals.Axis axis ViewSignals.Value value
    + Ds.post (endpoint sessionId)
  )

let private choice (sessionId: string) (pressed: bool) (label: string) (action: ViewAction) (axis: string) (value: string) : XmlNode =
  Elem.button
    [ Attr.class' "session-btn session-btn-labeled live-view-opt"
      Attr.type' "button"
      Attr.create "aria-pressed" (match pressed with | true -> "true" | false -> "false")
      Ds.onClick (click sessionId action axis value) ]
    [ // The chosen option carries a mark as well as a fill, so the choice never rests on colour alone. The mark is decoration:
      // `aria-pressed` already tells a screen reader which one is chosen.
      match pressed with
      | true -> Elem.span [ Attr.class' "live-view-check"; Attr.create "aria-hidden" "true" ] [ Text.raw "✓ " ]
      | false -> ()
      textEnc label ]

/// The presets worth offering while some of the axes are not drawn: the workflow's own, and the plain watch window. The others set
/// axes nothing draws yet, so a button for them would change nothing the person can see.
let private offeredPresets (workflow: ViewWorkflow) : ViewPreset list =
  [ ViewPreset.recommendedFor workflow; ViewPreset.WatchWindow ] |> List.distinct

let private presetRow (sessionId: string) (workflow: ViewWorkflow) (view: View) : XmlNode =
  let recommended = ViewPreset.recommendedFor workflow
  let detected = View.detect workflow view.Layout
  let isPressed (preset: ViewPreset) =
    match detected with
    | DetectedPreset.IsRecommended -> preset = recommended
    | DetectedPreset.IsPreset shown -> preset = shown
    | DetectedPreset.IsCustom -> false
  Elem.div [ Attr.class' "live-view-row"; Attr.create "role" "group"; Attr.create "aria-label" "Preset" ] [
    Elem.span [ Attr.class' "live-view-label" ] [ Text.raw "Preset" ]
    yield!
      offeredPresets workflow
      |> List.map (fun preset ->
        let value =
          match preset = recommended with
          | true -> ViewEdit.RecommendedToken
          | false -> ViewEdit.presetToken preset
        let label =
          match preset = recommended with
          | true -> sprintf "%s (recommended)" (ViewPreset.label preset)
          | false -> ViewPreset.label preset
        choice sessionId (isPressed preset) label ViewAction.ApplyPreset "" value)
    match detected with
    | DetectedPreset.IsCustom -> Elem.span [ Attr.class' "live-view-source" ] [ Text.raw "custom" ]
    | DetectedPreset.IsRecommended
    | DetectedPreset.IsPreset _ -> ()
  ]

let private axisRow (sessionId: string) (resolved: ResolvedView) (axis: ViewAxis) : XmlNode =
  let current = View.valueOf resolved.View axis
  let source = Map.tryFind axis resolved.Sources |> Option.defaultValue LDefault
  Elem.div [ Attr.class' "live-view-row"; Attr.create "role" "group"; Attr.create "aria-label" (attrEnc (ViewAxis.label axis)) ] [
    Elem.span [ Attr.class' "live-view-label" ] [ textEnc (ViewAxis.label axis) ]
    yield!
      AxisValue.allFor axis
      |> List.map (fun value ->
        choice sessionId (value = current) (AxisValue.label value) ViewAction.SetAxis (ViewAxis.token axis) (AxisValue.token value))
    // Only a choice somebody saved says where it came from. A default says nothing here: the preset row above already names the
    // recommended view, and the word repeated beside every pair read as noise.
    match source with
    | LDefault -> ()
    | LGlobal
    | LRepo
    | LSession ->
      Elem.span [ Attr.class' "live-view-source" ] [ textEnc (sourceWords source) ]
      Elem.button
        [ Attr.class' "session-btn session-btn-labeled live-view-reset"
          Attr.type' "button"
          Attr.create "aria-label" (attrEnc (sprintf "Put %s back to the recommended view" (ViewAxis.label axis)))
          Ds.onClick (click sessionId ViewAction.ResetAxis (ViewAxis.token axis) "") ]
        [ Text.raw "Reset" ]
  ]

let private scopeRow (sessionId: string) : XmlNode =
  Elem.div [ Attr.class' "live-view-row" ] [
    Elem.label [ Attr.class' "live-view-label"; Attr.create "for" ViewIds.ScopeSelect ] [ Text.raw "Save choices for" ]
    Elem.select
      [ Attr.id ViewIds.ScopeSelect
        Attr.class' "live-view-scope"
        Ds.bind ViewSignals.Scope ]
      [ for scope in ViewScope.all -> Elem.option [ Attr.value (ViewScope.token scope) ] [ textEnc (ViewScope.label scope) ] ]
    Elem.button
      [ Attr.class' "session-btn session-btn-labeled live-view-reset-all"
        Attr.type' "button"
        Ds.onClick (click sessionId ViewAction.ResetEverything "" "") ]
      [ Text.raw "Reset to recommended" ]
  ]

/// The button and the panel, for one session's workflow and its resolved view.
let render (sessionId: string) (workflow: ViewWorkflow) (resolved: ResolvedView) : ViewControls =
  let button =
    Elem.button
      [ Attr.id ViewIds.Button
        Attr.class' "session-btn live-pane-view"
        Attr.type' "button"
        Attr.create "aria-label" "Change how the live bindings are shown"
        Attr.create "aria-expanded" "false"
        Ds.attr' ("aria-expanded", sprintf "$%s ? 'true' : 'false'" ViewSignals.Open)
        Ds.class' ("session-btn-on", sprintf "$%s" ViewSignals.Open)
        Ds.preserveAttr "class aria-expanded"
        Ds.onClick (sprintf "$%s = !$%s" ViewSignals.Open ViewSignals.Open) ]
      [ slidersIcon ]
  let panel =
    Elem.div
      [ Attr.id ViewIds.Panel
        Attr.class' "live-view-panel live-hidden"
        Attr.create "role" "group"
        Attr.create "aria-label" (attrEnc (sprintf "How the live bindings are shown in %s" (ViewWorkflow.label workflow)))
        Ds.class' ("live-hidden", sprintf "!$%s" ViewSignals.Open)
        Ds.preserveAttr "class" ]
      [ Elem.div [ Attr.class' "live-view-head" ] [ textEnc (sprintf "View for %s" (ViewWorkflow.label workflow)) ]
        presetRow sessionId workflow resolved.View
        yield! ViewAxis.drawn |> List.map (axisRow sessionId resolved)
        scopeRow sessionId
        Elem.div [ Attr.class' "live-view-note" ] [
          Text.raw "A choice is saved as you click it. \"This project\" writes .SageFs/settings.json in the project folder. \"All projects\" writes your own settings.json."
        ] ]
  { Button = button; Panel = panel }
