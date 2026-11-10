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
    /// What the subsystem is using now, read from the consumer, so the row can say when it differs from what is saved.
    InUse: InUse
    /// Whether an environment variable or a flag wins over what was saved, and which.
    Override: SettingsCatalog.OverrideNote
    /// Sentences about this row that the layers cannot say: what a file beside the project set, and what was refused
    /// when the last session started. Empty for most rows.
    Notes: string list
  }

  /// The row with sentences under its value.
  let withNotes (notes: string list) (row: SettingRow) : SettingRow = { row with Notes = notes }

  /// A row for one descriptor: resolved across the files, the environment and the flags, with the in-use value read
  /// from its consumer. The IO is the layer file reads and the consumer read; the inputs are the caller's.
  let rowFor (inputs: ExternalInputs) (paths: ConfigPaths) (d: SettingDescriptor) : SettingRow =
    let resolved = SettingsCatalog.resolveWith inputs paths d
    match resolved with
    | Ok p ->
      { Descriptor = d
        Resolved = resolved
        InUse = SettingsCatalog.inUse d p
        Override = SettingsCatalog.overrideNote d p
        Notes = [] }
    | Error _ ->
      { Descriptor = d; Resolved = resolved; InUse = InUseSameAsSaved; Override = SettingsCatalog.NotOverridden; Notes = [] }

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

  /// The two edit-target scope values a client can hold in the scope signal.
  [<Literal>]
  let ScopeGlobal = "global"
  [<Literal>]
  let ScopeRepo = "repo"

  /// Which layer an edit targets, given the client's scope choice and the
  /// setting's own scope. A `Global`-only setting always edits the global
  /// layer regardless of the toggle; only a `RepoOverridable` setting can be
  /// written to the repo layer. (One place owns this mapping, used by both the
  /// render and the handler.)
  let layerForScope (scope: string) (d: SettingDescriptor) : ConfigLayer =
    match d.Scope with
    | RepoOverridable when scope = ScopeRepo -> LRepo
    | _ -> LGlobal

  /// The applicability chip — a DU-driven label+class+tooltip, so a new case
  /// would not silently fall through.
  let private applicabilityChip (a: SettingApplicability) : XmlNode =
    let label, cls, tip =
      match a with
      | Live -> (Glyphs.lead Glyph.Live + "live"), "settings-chip settings-chip-live", "Takes effect immediately."
      | RestartRequired -> "restart", "settings-chip settings-chip-restart", "Saved now; applies on the next restart."
      | NextSession -> "next session", "settings-chip settings-chip-next-session", "Saved now; applies to the next session started in this directory. A session that is already running keeps what it started with."
      | ReadOnly reason -> "read-only", "settings-chip settings-chip-readonly", ReadOnlyReason.describe reason
    Elem.span [ Attr.class' cls; Attr.title tip ] [ Text.enc label ]

  /// What saving a value does, in a sentence, so a row never leaves the person guessing when it applies.
  let private effectLine (a: SettingApplicability) : string =
    match a with
    | Live -> "Saving applies it now."
    | RestartRequired -> "Saving keeps it. It applies after the daemon restarts."
    | NextSession -> "Saving keeps it. It applies to the next session you start in this directory; a running session keeps what it started with."
    | ReadOnly reason -> ReadOnlyReason.describe reason

  /// A provenance badge naming the layer the effective value came from.
  let private sourceBadge (source: ConfigLayer) : XmlNode =
    let cls =
      match source with
      | LDefault -> "settings-badge settings-badge-default"
      | LGlobal -> "settings-badge settings-badge-global"
      | LRepo -> "settings-badge settings-badge-repo"
      | LSession -> "settings-badge settings-badge-session"
      | LEnvironment -> "settings-badge settings-badge-environment"
      | LCommandLine -> "settings-badge settings-badge-flag"
    Elem.span [ Attr.class' cls ] [ Text.enc (ConfigLayer.label source) ]

  /// The sentence under a value when an environment variable or a flag wins over what was saved.
  let private overrideLine (render: SettingValue -> string) (note: SettingsCatalog.OverrideNote) : XmlNode =
    match note with
    | SettingsCatalog.NotOverridden -> Elem.span [] []
    | SettingsCatalog.OverriddenBy(layer, name, saved) ->
      let how =
        match layer with
        | LCommandLine -> sprintf "while the daemon runs with %s" name
        | LEnvironment | LDefault | LGlobal | LRepo | LSession -> sprintf "while %s is set" name
      Elem.div
        [ Attr.class' "settings-override" ]
        [ Text.enc (sprintf "Saved %s. Ignored %s." (render saved) how) ]

  /// A sentence about a row from outside the layers, such as a refusal with its next step. It carries a warning mark and the
  /// words, so it does not rest on colour.
  let private noteLine (note: string) : XmlNode =
    Elem.div [ Attr.class' "settings-override settings-note" ] [ Text.enc (Glyphs.lead Glyph.Warn + note) ]

  /// The sentence that says what the subsystem is using when that differs from what the layers resolve to.
  let private inUseLine (d: SettingDescriptor) (p: Provenance) (inUse: InUse) : XmlNode =
    match inUse with
    | InUseSameAsSaved -> Elem.span [] []
    | InUseDiffers value ->
      let text =
        match p.Source, d.Applicability with
        | LDefault, _ -> sprintf "In use now: %s." (d.Render value)
        | _, Live -> sprintf "In use now: %s." (d.Render value)
        | _, (RestartRequired | ReadOnly _) -> sprintf "In use now: %s. The saved value applies after a restart." (d.Render value)
        | _, NextSession -> sprintf "In use now: %s. The saved value applies to the next session." (d.Render value)
      Elem.div [ Attr.class' "settings-inuse" ] [ Text.enc text ]

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
    // What a file holds, highest layer last. The input starts from it, so an environment variable that wins does not
    // overwrite the box with its own value; Reset is offered only when a file holds something to clear.
    let savedInFiles =
      p.PerLayer
      |> List.filter (fun (layer, _) ->
        match layer with
        | LGlobal | LRepo -> true
        | LDefault | LSession | LEnvironment | LCommandLine -> false)
      |> List.tryLast
    let current =
      match savedInFiles with
      | Some(_, saved) -> d.Render saved
      | None -> d.Render p.Effective
    match d.Applicability with
    | ReadOnly _ -> Elem.span [] []
    | Live | RestartRequired | NextSession ->
      let resetButton =
        match savedInFiles with
        | None -> Elem.span [] []
        | Some _ ->
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
          overrideLine d.Render row.Override
          inUseLine d p row.InUse
          editControl d p ]
      | Error e ->
        [ Elem.div [ Attr.class' "settings-value settings-value-error" ] [
            Text.enc (Glyphs.lead Glyph.Warn + ConfigError.describe e) ] ]
    let head =
      [ Elem.div [ Attr.class' "settings-row-head" ] [
          Elem.span [ Attr.class' "settings-name" ] [ Text.enc d.Name ]
          applicabilityChip d.Applicability ]
        Elem.div [ Attr.class' "settings-desc" ] [ Text.enc d.Description ]
        Elem.div [ Attr.class' "settings-effect" ] [ Text.enc (effectLine d.Applicability) ] ]
    Elem.div
      [ Attr.class' "settings-row"; Attr.create "data-setting-key" d.Key ]
      (head @ valueAndEdit @ (row.Notes |> List.map noteLine))

  /// The last-edit banner. `Quiet` renders nothing.
  let private noticeBanner (notice: PanelNotice) : XmlNode =
    match notice with
    | Quiet -> Elem.span [] []
    | Applied name ->
      Elem.div [ Attr.class' "settings-notice settings-notice-ok" ] [ Text.enc (sprintf "✓ %s saved" name) ]
    | Rejected(name, why) ->
      Elem.div [ Attr.class' "settings-notice settings-notice-err" ] [ Text.enc (sprintf "%s %s: %s" (Glyphs.text Glyph.Warn) name why) ]

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

  /// The edit-target scope toggle. Writes land in the chosen layer (repo only
  /// applies to RepoOverridable settings). Bound to the scope signal, sent with
  /// every Save/Reset @post.
  /// The repo target names the directory it writes. A page opened with no session, or for a session with no
  /// directory, has no repo layer to write, so it offers only the global target and says why.
  let private scopeSelector (repo: RepoLayerLocation) : XmlNode =
    let repoOption, repoNote =
      match repo with
      | RepoRootAt root ->
        [ Elem.option [ Attr.value ScopeRepo ] [ Text.enc (sprintf "This repo (%s)" root) ] ], Elem.span [] []
      | NoRepoCheckout ->
        [], Elem.span [ Attr.class' "settings-effect" ] [ Text.raw "Open Settings from a session to edit that repo's layer." ]
    Elem.div [ Attr.class' "settings-scope" ] [
      Elem.span [ Attr.class' "settings-scope-label" ] [ Text.raw "Edit target:" ]
      Elem.select [ Attr.class' "settings-scope-select"; Ds.bind Signals.SettingsScope ] (
        Elem.option [ Attr.value ScopeGlobal ] [ Text.raw "Global" ] :: repoOption)
      repoNote
    ]

  let renderPanel (notice: PanelNotice) (repo: RepoLayerLocation) (rows: SettingRow list) : XmlNode =
    let groups = SettingCategory.displayOrder |> List.collect (renderGroup rows)
    let repoDir =
      match repo with
      | RepoRootAt root -> root
      | NoRepoCheckout -> ""
    Elem.div
      [ Attr.id PanelDomId; Attr.class' "panel settings-panel"
        Ds.signal (Signals.SettingsSaving, false)
        Ds.signal (Signals.SettingsScope, ScopeGlobal)
        Ds.signal (Signals.SettingsRepoDir, repoDir) ]
      [ Elem.h2 [] [ Text.raw "Settings" ]
        scopeSelector repo
        noticeBanner notice
        Elem.div [ Attr.class' "settings-groups" ] groups ]

  /// The full standalone page `GET /dashboard/settings` serves. Includes the
  /// self-hosted, pinned Datastar bundle so the Phase-B2 edit controls work,
  /// and the shared dashboard stylesheet.
  let renderPage (repo: RepoLayerLocation) (rows: SettingRow list) : XmlNode =
    Elem.html [ Attr.lang "en" ] [
      Elem.head [] [
        yield! DashboardFragments.pageHeadMetas
        Elem.title [] [ Text.raw "SageFs Settings" ]
        Elem.link [ Attr.rel "stylesheet"; Attr.href "/dashboard/dashboard.css" ]
        Elem.script [ Attr.type' "module"; Attr.src "/dashboard/datastar.js" ] []
      ]
      Elem.body [] [
        Elem.div [ Attr.class' "settings-page" ] [
          Elem.p [] [ Elem.a [ Attr.href "/dashboard" ] [ Text.raw "← Dashboard" ] ]
          renderPanel Quiet repo rows
        ]
      ]
    ]
