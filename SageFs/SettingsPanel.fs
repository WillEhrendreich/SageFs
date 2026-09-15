namespace SageFs.Server

open Falco.Markup
open Falco.Datastar
open StarFederation.Datastar.FSharp
open SageFs

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

  /// One setting row. Read-only for now (Phase B1); the edit control lands in
  /// Phase B2 as a `Ds.bind` input + `Ds.post` Save/Clear.
  let private renderRow (row: SettingRow) : XmlNode =
    let d = row.Descriptor
    let valueCell =
      match row.Resolved with
      | Ok p ->
        Elem.div [ Attr.class' "settings-value" ] [
          Elem.span [ Attr.class' "settings-effective" ] [ Text.enc (d.Render p.Effective) ]
          sourceBadge p.Source
          perLayerLine d.Render p
        ]
      | Error e ->
        Elem.div [ Attr.class' "settings-value settings-value-error" ] [
          Text.enc (sprintf "⚠ %s" (ConfigError.describe e))
        ]
    Elem.div
      [ Attr.class' "settings-row"; Attr.create "data-setting-key" d.Key ]
      [ Elem.div [ Attr.class' "settings-row-head" ] [
          Elem.span [ Attr.class' "settings-name" ] [ Text.enc d.Name ]
          applicabilityChip d.Applicability ]
        Elem.div [ Attr.class' "settings-desc" ] [ Text.enc d.Description ]
        valueCell ]

  /// The panel body: rows grouped by category, in the DU's display order.
  /// Empty categories are omitted.
  let renderPanel (rows: SettingRow list) : XmlNode =
    let byCategory (cat: SettingCategory) =
      rows |> List.filter (fun r -> r.Descriptor.Category = cat)
    let groups =
      SettingCategory.displayOrder
      |> List.choose (fun cat ->
        match byCategory cat with
        | [] -> None
        | catRows ->
          Some (
            Elem.div [ Attr.class' "settings-group" ] [
              Elem.h3 [ Attr.class' "settings-group-title" ] [ Text.enc (SettingCategory.label cat) ]
              yield! catRows |> List.map renderRow ]))
    Elem.div
      [ Attr.id PanelDomId; Attr.class' "panel settings-panel" ]
      [ Elem.h2 [] [ Text.raw "Settings" ]
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
          renderPanel rows
        ]
      ]
    ]
