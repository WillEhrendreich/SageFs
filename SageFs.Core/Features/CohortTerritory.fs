namespace SageFs.Features

open System.Text
open SageFs.Cohort
open SageFs.Features.Treemap

/// Phase 2 item 16 of sagefs-multiagent-vision.md (§6.5 "Territory map"),
/// scoped to what `Cohort.CohortFrame<'m>` actually carries: claims (`File`/
/// `Project` scope, holder index, `ClaimState`) — not a full repo file tree,
/// not per-file line counts. The vision's full territory map (§6.5) lays out
/// the *integration checkout's compile list* with coverage-health underlays;
/// that data does not exist on `CohortFrame` yet (`Cohort.fs`'s own scope
/// note: the territory map's fields are a later item's concern). This module
/// builds the piece the frame DOES support today — who owns which claimed
/// path — rather than inventing file-tree or LOC data that isn't there.
///
/// Each currently `Held` claim becomes one territory tile, weighted
/// uniformly (one claimed path = one unit of area — `CohortFrame` carries no
/// line/file count per claim to weight by, and the task brief's own fallback
/// for this case is "uniform per claimed path"). An `Orphaned` claim becomes
/// a neutral (unclaimed-colored, dashed-border) tile, so the map still shows
/// contested-but-abandoned ground. A `Released` claim is no longer anyone's
/// territory and is dropped — including it would let the tile count grow
/// without bound as claims cycle through a long-lived cohort (`Cohort.fs`
/// never removes a released claim from `CohortState.Claims`).
///
/// No IO, no Falco: `layout` reuses `Treemap.squarify` (`Features/Treemap.fs:29`)
/// and returns positioned rectangles; `toSvg` renders them as a raw SVG
/// string the dashboard embeds inline via `Text.raw`
/// (`CohortMatrixRender.toPngDataUri`'s pattern — no served route, no new
/// SSE channel, no extra HTTP round trip).
module CohortTerritory =

  /// One piece of territory: a claim's scope, its current holder (index into
  /// the frame's `MemberIds`, or -1 for `Orphaned`/neutral), and a stable
  /// display label.
  type TerritoryTile = {
    ClaimId: ClaimId
    Scope: ClaimScope
    Label: string
    HolderIndex: int
  }

  /// The repo-relative path either scope case names — `File`/`Project` are
  /// both already a path (`Cohort.ClaimScope`, `Cohort.fs:96-98`).
  let labelFor (scope: ClaimScope) : string =
    match scope with
    | ClaimScope.File path -> path
    | ClaimScope.Project path -> path

  /// Pure projection: `CohortFrame -> TerritoryTile list`. `Held` claims keep
  /// their holder index; `Orphaned` claims project with `HolderIndex = -1`
  /// (neutral — no live holder, but still real territory); `Released` claims
  /// are dropped (see module doc). Order follows the frame's own claim array
  /// order, which `Cohort.project` already makes deterministic for equal
  /// inputs (`Map.toArray`'s sorted-key order, `Cohort.fs:988`).
  let ofFrame (frame: CohortFrame<'m>) : TerritoryTile list =
    [ for i in 0 .. frame.ClaimIds.Length - 1 do
        match frame.ClaimState.[i] with
        | ClaimState.Released _ -> ()
        | ClaimState.Held _ ->
          yield
            { ClaimId = frame.ClaimIds.[i]
              Scope = frame.ClaimScope.[i]
              Label = labelFor frame.ClaimScope.[i]
              HolderIndex = frame.ClaimHolderIndex.[i] }
        | ClaimState.Orphaned _ ->
          yield
            { ClaimId = frame.ClaimIds.[i]
              Scope = frame.ClaimScope.[i]
              Label = labelFor frame.ClaimScope.[i]
              HolderIndex = -1 } ]

  /// Squarify tiles into `bounds`, reusing `Treemap.squarify` with a uniform
  /// weight per tile (see module doc). Deterministic for equal input lists —
  /// `squarify` has no shared mutable state that escapes it (`Treemap.fs:29-84`).
  let layout (bounds: Rect) (tiles: TerritoryTile list) : (TerritoryTile * Rect) list =
    tiles
    |> List.map (fun t -> t, 1.0)
    |> fun items -> squarify items bounds

  // ── stable per-member palette, SVG rendering ───────────────────────────

  /// Fixed, stable palette indexed by `HolderIndex` (`frame.MemberIds` order)
  /// so a member's tiles are always the same color across renders/ticks —
  /// never derived from a hash or from render order.
  let palette : string[] =
    [| "#4f8ef7"; "#f78e4f"; "#8ef74f"; "#f74f8e"; "#4ff7e2"; "#c04ff7"; "#f7d24f"; "#4f6ef7" |]

  /// Unclaimed/orphaned tiles — muted grey, never a member's color.
  let neutralColor = "#888888"

  let colorForHolder (holderIndex: int) : string =
    if holderIndex < 0 then neutralColor
    else palette.[holderIndex % palette.Length]

  let private escapeXml (s: string) : string =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;")

  /// Renders laid-out tiles as a raw SVG string. Deterministic: the same
  /// tiles and bounds always produce byte-identical markup, so it composes
  /// with `SnapshotRenderGuard` the same way `CohortMatrixRender.toPng` does.
  /// An empty tile list still renders a valid (empty) `<svg>` rather than
  /// throwing or returning `""` — the dashboard can always embed the result.
  /// Orphaned (neutral) tiles get a dashed stroke so "unclaimed" reads
  /// visually distinct from "held" even in the character-less picture.
  let toSvg (width: float) (height: float) (tiles: TerritoryTile list) : string =
    let rects = layout { X = 0.0; Y = 0.0; W = width; H = height } tiles
    let sb = StringBuilder()
    sb.Append(sprintf "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 %g %g\" width=\"100%%\" height=\"auto\">" width height)
    |> ignore
    for tile, r in rects do
      let color = colorForHolder tile.HolderIndex
      let stroke = if tile.HolderIndex < 0 then "#444444" else "#111111"
      let dash = if tile.HolderIndex < 0 then " stroke-dasharray=\"3,2\"" else ""
      sb.Append(
        sprintf
          "<rect x=\"%g\" y=\"%g\" width=\"%g\" height=\"%g\" fill=\"%s\" stroke=\"%s\"%s><title>%s</title></rect>"
          r.X r.Y r.W r.H color stroke dash (escapeXml tile.Label))
      |> ignore
      // Only label tiles big enough to hold readable text — a crowded map of
      // tiny slivers gets colored regions without illegible overlapping text.
      if r.W > 40.0 && r.H > 12.0 then
        let fileName =
          tile.Label.Split('/') |> Array.tryLast |> Option.defaultValue tile.Label
        sb.Append(
          sprintf "<text x=\"%g\" y=\"%g\" font-size=\"9\" fill=\"#ffffff\">%s</text>" (r.X + 2.0) (r.Y + 11.0) (escapeXml fileName))
        |> ignore
    sb.Append("</svg>") |> ignore
    sb.ToString()
