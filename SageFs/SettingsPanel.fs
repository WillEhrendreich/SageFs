namespace SageFs.Server

open Falco.Markup
open Falco.Datastar
open StarFederation.Datastar.FSharp
open SageFs
open SageFs.Server.DashboardTypes

/// The dashboard Settings panel (unified-settings-design.md Phase B), rendered
/// server-authoritatively from the resolved config catalog — the Tao of
/// Datastar: the server owns the DOM, one render, no client second source.
/// Edit controls use the `Ds.*` builders (never hand-written data-* strings)
/// and POST back to handlers that re-render this panel and morph it in place.
///
/// This module is pure render + naming; the IO (resolving each descriptor
/// across the layer files) lives in the route handler, which hands this module
/// already-resolved rows.
module SettingsPanel =

  [<Literal>]
  let PanelDomId = "settings-panel"

  /// A descriptor paired with its resolution across the layers. A resolve
  /// error (a corrupt persisted value) is a first-class row state, shown
  /// rather than hidden.
  type SettingRow = {
    Descriptor: SettingDescriptor
    Resolved: Result<Provenance, ConfigError>
  }

  /// The outcome of the last edit, shown as a banner after a morph. A DU (not a
  /// bool/string option) so success and rejection each carry their own detail.
  type PanelNotice =
    | Quiet
    | Applied of name: string
    | Rejected of name: string * why: string

  /// A setting key sanitised into a valid Datastar signal name / URL segment
  /// (no dots or dashes). Deterministic and reversible-by-lookup: the handler
  /// finds the descriptor whose signalName matches the route segment.
  let signalName (key: string) : string =
    "set_" + key.Replace('.', '_').Replace('-', '_')

  /// The applicability chip — a DU-driven label+class+tooltip, so a new case
  /// would not silently fall through.
  let private applicabilityChip (a: SettingApplicability) : XmlNode =
    let label, cls, tip =
      match a with
      | Live -> "⚡ live", "settings-chip settings-chip-live", "Takes effect immediately."
      | RestartRequired -> "⟳ restart", "settings-chip settings-chip-restart", "Saved now; applies on the next restart."
      | Guarded -> "⚠ guarded", "settings-chip settings-chip-guarded", "Editing is guarded (e.g. the loopback bind-host RCE guard)."
    Elem.span [ Attr.class' cls; Attr.title tip ] [ Text.enc label ]

  /// A provenance badge naming the layer the effective value came from.
  let private sourceBadge (source: ConfigLayer) : XmlNode =
    let cls =
      match source with
      | LDefault -> "settings-badge settings-badge-default"
      | LGlobal -> "settings-badge settings-badge-global"
      | LRepo -> "settings-badge settings-badge-repo"
      | LSession -> "settings-badge settings-badge-session"
    Elem.span [ Attr.class' cls ] [ Text.enc (ConfigLayer.label source) ]

  /// The per-layer base-vs-override line: when more than one layer sets a
  /// value, show each layer's value so the override is explicit (requirement
  /// #2). `render` turns a typed value back into its display string.
  let private perLayerLine (render: SettingValue -> string) (p: Provenance) : XmlNode =
    match p.PerLayer with
    | [] | [ _ ] -> Elem.span [] []  // only the default — nothing to compare
    | layers ->
      let parts =
        layers
        |> List.map (fun (layer, v) -> sprintf "%s: %s" (ConfigLayer.label layer) (render v))
      Elem.span
        [ Attr.class' "settings-perlayer" ]
        [ Text.enc (String.concat "  →  " parts) ]

  /// The edit control for a row (Phase B2). `Guarded` settings (the bind-host
  /// RCE guard) are intentionally not casually editable from the panel; `Live`
  /// and `RestartRequired` get a `Ds.bind` input + `Ds.post` Save, plus a Reset
  /// when a layer above default set the value. All interactivity is via the
  /// `Ds.*` builders — no hand-written data-* strings.
  let private editControl (d: SettingDescriptor) (p: Provenance) : XmlNode =
    let sigName = signalName d.Key
    let current = d.Render p.Effective
    match d.Applicability with
    | Guarded ->
      Elem.span [ Attr.class' "settings-guarded-note" ] [ Text.raw "guarded — edit via config, not casually" ]
    | Live | RestartRequired ->
      // Reset is offered only when a layer above default set the value.
      let resetButton =
        match p.Source with
        | LDefault -> Elem.span [] []
        | LGlobal | LRepo | LSession ->
          Elem.button
            [ Attr.class' "settings-btn settings-btn-clear"
              Ds.onClick (Ds.post (sprintf "/dashboard/settings/clear/%s" sigName)) ]
            [ Text.raw "Reset" ]
      Elem.div [ Attr.class' "settings-edit" ] [
        Elem.input [
          Attr.type' "text"; Attr.class' "settings-input"
          Ds.signal (sigName, current)
          Ds.bind sigName ]
        Elem.button
          [ Attr.class' "settings-btn"
            Ds.indicator Signals.SettingsSaving
            Ds.attr' ("disabled", "$settingsSaving")
            Ds.onClick (Ds.post (sprintf "/dashboard/settings/edit/%s" sigName)) ]
          [ Text.raw "Save" ]
        resetButton
      ]

  /// One setting row: label + chip, description, resolved value + provenance,
  /// and (for a resolvable row) the edit control.
  let private renderRow (row: SettingRow) : XmlNode =
    let d = row.Descriptor
    let valueAndEdit =
      match row.Resolved with
      | Ok p ->
        [ Elem.div [ Attr.class' "settings-value" ] [
            Elem.span [ Attr.class' "settings-effective" ] [ Text.enc (d.Render p.Effective) ]
            sourceBadge p.Source
            perLayerLine d.Render p ]
          editControl d p ]
      | Error e ->
        [ Elem.div [ Attr.class' "settings-value settings-value-error" ] [
            Text.enc (sprintf "⚠ %s" (ConfigError.describe e)) ] ]
    let head =
      [ Elem.div [ Attr.class' "settings-row-head" ] [
          Elem.span [ Attr.class' "settings-name" ] [ Text.enc d.Name ]
          applicabilityChip d.Applicability ]
        Elem.div [ Attr.class' "settings-desc" ] [ Text.enc d.Description ] ]
    Elem.div
      [ Attr.class' "settings-row"; Attr.create "data-setting-key" d.Key ]
      (head @ valueAndEdit)

  /// The last-edit banner. `Quiet` renders nothing.
  let private noticeBanner (notice: PanelNotice) : XmlNode =
    match notice with
    | Quiet -> Elem.span [] []
    | Applied name ->
      Elem.div [ Attr.class' "settings-notice settings-notice-ok" ] [ Text.enc (sprintf "✓ %s saved" name) ]
    | Rejected(name, why) ->
      Elem.div [ Attr.class' "settings-notice settings-notice-err" ] [ Text.enc (sprintf "⚠ %s: %s" name why) ]

  /// The panel body: rows grouped by category, in the DU's display order.
  /// Empty categories are omitted. Carries the `SettingsSaving` indicator signal
  /// on the root so Save/Reset buttons get immediate in-flight feedback, and the
  /// last-edit notice banner. Re-rendered whole and morphed by `PanelDomId`
  /// after each edit (one authoritative render — the Tao of Datastar).
  let private renderGroup (rows: SettingRow list) (cat: SettingCategory) : XmlNode list =
    match rows |> List.filter (fun r -> r.Descriptor.Category = cat) with
    | [] -> []
    | catRows ->
      let title = Elem.h3 [ Attr.class' "settings-group-title" ] [ Text.enc (SettingCategory.label cat) ]
      [ Elem.div [ Attr.class' "settings-group" ] (title :: List.map renderRow catRows) ]

  let renderPanel (notice: PanelNotice) (rows: SettingRow list) : XmlNode =
    let groups = SettingCategory.displayOrder |> List.collect (renderGroup rows)
    Elem.div
      [ Attr.id PanelDomId; Attr.class' "panel settings-panel"; Ds.signal (Signals.SettingsSaving, false) ]
      [ Elem.h2 [] [ Text.raw "Settings" ]
        noticeBanner notice
        Elem.div [ Attr.class' "settings-groups" ] groups ]

  /// The full standalone page `GET /dashboard/settings` serves. Includes the
  /// self-hosted, pinned Datastar bundle so the Phase-B2 edit controls work,
  /// and the shared dashboard stylesheet.
  let renderPage (rows: SettingRow list) : XmlNode =
    Elem.html [] [
      Elem.head [] [
        Elem.title [] [ Text.raw "SageFs Settings" ]
        Elem.link [ Attr.rel "stylesheet"; Attr.href "/dashboard/dashboard.css" ]
        Elem.script [ Attr.type' "module"; Attr.src "/dashboard/datastar.js" ] []
      ]
      Elem.body [] [
        Elem.div [ Attr.class' "settings-page" ] [
          Elem.p [] [ Elem.a [ Attr.href "/dashboard" ] [ Text.raw "← Dashboard" ] ]
          renderPanel Quiet rows
        ]
      ]
    ]
