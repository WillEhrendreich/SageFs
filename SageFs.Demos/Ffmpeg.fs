/// Pure ffmpeg planning (demo-gif-plan.md §4.6, §5). `render` turns a
/// `ComposePlan` into one `FilterGraph` value: a real multi-input
/// `-filter_complex` graph — per-step cursor+ripple overlay, caption band,
/// step counter, progress dots, and (when the layout has an editor pane) a
/// self-referential magnifier picture-in-picture, all applied to each
/// step's own input pad BEFORE the steps are concatenated (so every
/// overlay's ffmpeg-expression `t` is relative to its own segment, never the
/// whole run — see the module doc on `render` for why), then
/// `mpdecimate`+`setpts` and the two-pass GIF palette technique over the
/// concatenated result. `toCommandString` is the ONE render-to-string
/// function — no hand-built filter strings anywhere else in the tool.
module SageFs.Demos.Ffmpeg

open SageFs.Demos.Domain

// ---------------------------------------------------------------------------
// Fixed layout constants for the operations `render` builds (§9). The canvas
// is a decided constant, not a parameter — §9 pins capture at a native
// 1280×720 with no downscale, and `Compose.plan`'s signature (§5) has no
// `Screen` argument to thread one through.
// ---------------------------------------------------------------------------

[<Literal>]
let private CanvasWidth = 1280

[<Literal>]
let private CanvasHeight = 720

/// §9: "Caption band. 56 px, Ground at 88% opacity ... 24 px side padding."
[<Literal>]
let private CaptionBandHeight = 56

[<Literal>]
let private CaptionSidePadding = 24

[<Literal>]
let private CaptionBandOpacity = 0.88

/// §9: "Progress dots (6 px, Panel/Accent) sit at the band's right edge."
[<Literal>]
let private DotSize = 6

[<Literal>]
let private DotSpacing = 4

/// §9: "IBM Plex Sans Medium 22 px" for captions/counter. IBM Plex Sans is
/// not bundled on this machine and this pass has no network access to fetch
/// the licensed TTF (§8/§9 name it as a bundled asset for a future pass) —
/// `font=` is ffmpeg's own fontconfig-pattern lookup (this build has
/// `--enable-libfontconfig`/`--enable-libfreetype`), and "Noto Sans" is
/// installed system-wide and is, like Plex, a humanist sans distinct from
/// the mono code faces — the same deliberate code/caption pairing §9 asks
/// for, honestly substituted rather than silently faked as Plex.
[<Literal>]
let private CaptionFont = "Noto Sans"

[<Literal>]
let private CaptionFontSize = 22

/// §9: "28 px arrow ... cursor fill" / "click ripple = Accent ring 8→28px
/// over 250ms". ffmpeg's `drawbox` only draws axis-aligned rectangles (no
/// vector paths, no native circle/arrow primitive — confirmed against this
/// machine's `ffmpeg -filters`), so the cursor is a small stack of
/// decreasing-width bars forming a pixelated arrow silhouette (the same
/// technique classic bitmap cursors use), never a font glyph — a glyph
/// depends on the active font actually shipping that codepoint, which is not
/// guaranteed the way a `drawbox` stack is.
[<Literal>]
let private CursorSize = 28

[<Literal>]
let private RippleFromSize = 8

[<Literal>]
let private RippleToSize = 28

[<Literal>]
let private RippleDurationSec = 0.25

// ---------------------------------------------------------------------------
// toCommandString — the one place a FilterGraph becomes an ffmpeg filter
// string. Every case below is a fixed, hand-verified ffmpeg filter syntax;
// nowhere else in this tool builds a filter string by hand.
// ---------------------------------------------------------------------------

/// Renders a float the way an ffmpeg filter arg expects it: invariant
/// culture (never a locale decimal comma) and no digits beyond what the
/// value needs (`1.0` renders as `1`, not `1.0`).
let private formatFactor (value: float) : string =
  value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)

/// ffmpeg's `color` filter argument wants `0xRRGGBB`, not CSS's `#RRGGBB`
/// (§9's `Style` tokens are all `#rrggbb` hex, so this is total over every
/// style value the composer ever passes). An `alpha` in `0.0..1.0` is
/// appended as ffmpeg's own `@alpha` suffix (§9's "88% opacity" caption
/// band).
let private colorArg (alpha: float option) (color: string) : string =
  let hex = if color.StartsWith("#") then "0x" + color.Substring(1) else color

  match alpha with
  | Some a -> sprintf "%s@%s" hex (formatFactor a)
  | None -> hex

/// `drawtext`'s `text=` argument is single-quoted; ffmpeg's filter-graph
/// parser treats backslash, colon, single-quote and comma as special even
/// inside that quoting, so all four must be backslash-escaped or a caption
/// containing one of them silently breaks the surrounding filter chain.
let private escapeDrawText (text: string) : string =
  text
    .Replace("\\", "\\\\")
    .Replace(":", "\\:")
    .Replace("'", "\\'")
    .Replace(",", "\\,")

/// A fixed pixel value renders bare; an expression is single-quoted (ffmpeg's
/// filter-option parser splits an option string on unescaped `:`/`,` even
/// inside a value, so an unquoted expression like `min(8+80*(t-0.83),28)`
/// gets its own internal comma mistaken for the NEXT option's separator and
/// fails with "No option name near ..." — verified directly against this
/// machine's ffmpeg; single-quoting the whole expression, exactly like
/// `enable=` already does for its own `between(...)` value, is what ffmpeg's
/// own option grammar expects for a value containing reserved characters).
let private extentString (extent: Extent) : string =
  match extent with
  | Extent.Fixed pixels -> string pixels
  | Extent.Expr expression -> sprintf "'%s'" expression

let private thicknessString (thickness: Thickness) : string =
  match thickness with
  | Thickness.Fill -> "fill"
  | Thickness.Outline pixels -> string pixels

let private padRef (pad: Pad) : string =
  match pad with
  | Pad.Input index -> sprintf "%d:v" index
  | Pad.Named label -> label

let private bracket (pad: Pad) : string = sprintf "[%s]" (padRef pad)

/// The one render-to-string function for `FilterGraph` (§4.6): every case is
/// total, and `Chain` recurses and comma-joins — ffmpeg's own separator
/// between filters in one `-filter_complex` chain. `Labeled`/`Complex` are
/// the pad-labeling layer on top: a `Labeled` node wraps its rendered filter
/// in ffmpeg's `[in]...[out]` bracket syntax, and `Complex` `;`-joins an
/// ordered list of nodes into one full `-filter_complex` graph.
let rec toCommandString (graph: FilterGraph) : string =
  match graph with
  | FilterGraph.Scale (width, height) -> sprintf "scale=%d:%d" width height
  | FilterGraph.Fps fps -> sprintf "fps=%d" fps
  | FilterGraph.Overlay (x, y) -> sprintf "overlay=%d:%d" x y
  | FilterGraph.DrawBox (rect, color) ->
    sprintf "drawbox=x=%d:y=%d:w=%d:h=%d:color=%s:t=fill" rect.X rect.Y rect.W rect.H (colorArg None color)
  | FilterGraph.DrawText (text, x, y) -> sprintf "drawtext=text='%s':x=%d:y=%d" (escapeDrawText text) x y
  | FilterGraph.Crop rect -> sprintf "crop=%d:%d:%d:%d" rect.W rect.H rect.X rect.Y
  | FilterGraph.Concat inputs -> sprintf "concat=n=%d:v=1:a=0" inputs
  | FilterGraph.MpDecimate -> "mpdecimate"
  | FilterGraph.SetPts factor -> sprintf "setpts=%s*PTS" (formatFactor factor)
  | FilterGraph.PaletteGen statsMode -> sprintf "palettegen=stats_mode=%s" statsMode
  | FilterGraph.PaletteUse dither -> sprintf "paletteuse=dither=%s" dither
  | FilterGraph.Chain filters -> filters |> List.map toCommandString |> String.concat ","
  | FilterGraph.DrawBoxTimed (rect, color, alpha, thickness, enable) ->
    let core =
      sprintf
        "drawbox=x=%s:y=%s:w=%s:h=%s:color=%s:t=%s"
        (extentString rect.Left)
        (extentString rect.Top)
        (extentString rect.BoxWidth)
        (extentString rect.BoxHeight)
        (colorArg alpha color)
        (thicknessString thickness)
    match enable with
    | Some expr -> core + sprintf ":enable='%s'" expr
    | None -> core
  | FilterGraph.DrawTextStyled (text, x, y, color, fontSize, enable) ->
    let core =
      sprintf
        "drawtext=text='%s':x=%d:y=%d:fontcolor=%s:fontsize=%d:font=%s"
        (escapeDrawText text)
        x
        y
        (colorArg None color)
        fontSize
        CaptionFont
    match enable with
    | Some expr -> core + sprintf ":enable='%s'" expr
    | None -> core
  | FilterGraph.Split outputs -> sprintf "split=%d" outputs
  | FilterGraph.Labeled (inputs, filter, outputs) ->
    let ins = inputs |> List.map bracket |> String.concat ""
    let outs = outputs |> List.map bracket |> String.concat ""
    sprintf "%s%s%s" ins (toCommandString filter) outs
  | FilterGraph.Complex nodes -> nodes |> List.map toCommandString |> String.concat ";"

// ---------------------------------------------------------------------------
// render — the §4.6 GIF pipeline, built as a real multi-input filter_complex.
// ---------------------------------------------------------------------------

/// The synthetic cursor for one held position (§4.3 "cursor is synthetic",
/// §9's 28px arrow): a small stack of decreasing-width `Ink`-filled bars,
/// each preceded by a 1px-larger `Rule` bar and a 2px-larger `#16161d` bar so
/// the outline and drop-shadow show at the silhouette's edges (§9: "1.5px
/// Rule outline plus a 1px #16161d drop for contrast"). Gated to `enable` so
/// it is visible only while this hold is the active sample (§4.6).
let private cursorArrow (style: Style) (enable: string) (origin: Point) : FilterGraph list =
  let steps = 5
  let stepH = CursorSize / steps

  let bar (color: string) (offset: int) : FilterGraph list =
    [ for i in 0 .. steps - 1 ->
        let w = CursorSize - i * stepH + offset
        let rect =
          { Left = Extent.Fixed(origin.X - offset / 2)
            Top = Extent.Fixed(origin.Y + i * stepH - offset / 2)
            BoxWidth = Extent.Fixed(max 1 w)
            BoxHeight = Extent.Fixed(stepH + offset) }
        FilterGraph.DrawBoxTimed(rect, color, None, Thickness.Fill, Some enable) ]

  // Drop shadow (largest offset, drawn first so later layers paint over it),
  // then the Rule outline, then the Ink fill on top — back-to-front so the
  // outline and shadow read as a border around the fill, not underneath it.
  (bar "#16161d" 4) @ (bar style.Rule 2) @ (bar style.Ink 0)

/// The click ripple (§9: "Accent ring 8→28px over 250ms"): a hollow,
/// growing `drawbox` centered on `origin`, gated to the ripple's own time
/// window so it appears only around the moment the step's expectation was
/// observed (§4.6).
let private clickRipple (style: Style) (startSec: float) (origin: Point) : FilterGraph =
  let endSec = startSec + RippleDurationSec
  let growthPerSec = float (RippleToSize - RippleFromSize) / RippleDurationSec
  let sizeExpr = sprintf "min(%d+%s*(t-%s),%d)" RippleFromSize (formatFactor growthPerSec) (formatFactor startSec) RippleToSize
  let halfExpr = sprintf "(%s)/2" sizeExpr

  let rect =
    { Left = Extent.Expr(sprintf "%d-%s" origin.X halfExpr)
      Top = Extent.Expr(sprintf "%d-%s" origin.Y halfExpr)
      BoxWidth = Extent.Expr sizeExpr
      BoxHeight = Extent.Expr sizeExpr }

  FilterGraph.DrawBoxTimed(rect, style.Accent, None, Thickness.Outline 2, Some(sprintf "between(t,%s,%s)" (formatFactor startSec) (formatFactor endSec)))

/// The full per-step cursor overlay: one `cursorArrow` hold per recorded
/// pointer sample, evenly spread across the step's own segment duration
/// (`StepRecord`/`Wire.WireStep` carry no per-sample timestamp — §4.1's
/// `PointerPath` is a plain point list — so even spacing across the
/// segment's own `[Started,Ended]` window is the honest reading of the data
/// actually available), plus a ripple at the step's own observed-at time
/// using the path's LAST point (§4.3: motion resolves to the click target's
/// centre, so the final sample is where the click landed).
let private cursorOverlay (style: Style) (timing: StepTiming) (pointerPath: Point list) : FilterGraph list =
  match pointerPath with
  | [] -> []
  | points ->
    let durationSec = float (max 1 (timing.EndedMs - timing.StartedMs)) / 1000.0
    let n = points.Length

    let holds =
      points
      |> List.mapi (fun i point ->
        let startSec = durationSec * float i / float n
        let endSec = if i = n - 1 then durationSec else durationSec * float (i + 1) / float n
        cursorArrow style (sprintf "between(t,%s,%s)" (formatFactor startSec) (formatFactor endSec)) point)
      |> List.collect id

    let rippleStartSec =
      max 0.0 (min (durationSec - RippleDurationSec) (float (timing.ObservedAtMs - timing.StartedMs) / 1000.0))

    holds @ [ clickRipple style rippleStartSec (List.last points) ]

/// The caption band for one step (§9): a translucent `Ground`-colored band
/// across the bottom of the canvas, the caption in `Ink`, and a tabular
/// `index/total` step counter in `Accent`.
let private captionBand (style: Style) (index: int) (total: int) (caption: Caption) : FilterGraph list =
  let band =
    { Left = Extent.Fixed 0
      Top = Extent.Fixed(CanvasHeight - CaptionBandHeight)
      BoxWidth = Extent.Fixed CanvasWidth
      BoxHeight = Extent.Fixed CaptionBandHeight }
  let textY = CanvasHeight - CaptionBandHeight + (CaptionBandHeight - CaptionFontSize) / 2
  let counterText = sprintf "%d/%d" (index + 1) total

  [ FilterGraph.DrawBoxTimed(band, style.Ground, Some CaptionBandOpacity, Thickness.Fill, None)
    FilterGraph.DrawTextStyled(counterText, CaptionSidePadding, textY, style.Accent, CaptionFontSize, None)
    FilterGraph.DrawTextStyled(Caption.value caption, CaptionSidePadding + 56, textY, style.Ink, CaptionFontSize, None) ]

/// One step-progress dot (§9): `total` dots along the band's right edge, one
/// per step in the recording — `Accent` for the step this segment IS,
/// `Panel` (off) for every other.
let private stepDots (style: Style) (currentIndex: int) (total: int) : FilterGraph list =
  let y = CanvasHeight - CaptionBandHeight - DotSize - 8
  let rightEdge = CanvasWidth - CaptionSidePadding

  [ for index in 0 .. total - 1 ->
      let x = rightEdge - (index + 1) * (DotSize + DotSpacing)
      let color = if index = currentIndex then style.Accent else style.Panel
      FilterGraph.DrawBox({ X = x; Y = y; W = DotSize; H = DotSize }, color) ]

/// The 2× picture-in-picture magnifier over the editor pane (§4.6): a
/// self-referential PiP needs two copies of the SAME step input (`split`),
/// one cropped+scaled and overlaid onto the other — a single-input `Chain`
/// cannot express "overlay a video onto a cropped copy of itself" because
/// `overlay` is a two-input ffmpeg filter. Docked to the canvas's top-right
/// so it never overlaps the caption band at the bottom. Wraps the step's own
/// already-composited overlay chain (`bodyChain`) as the base layer so the
/// magnifier sits on TOP of the cursor/ripple/caption, not underneath them.
let private withMagnifier (rect: Rect) (bodyChain: FilterGraph) (mainPad: Pad) (outPad: Pad) : FilterGraph =
  let scaledWidth, scaledHeight = rect.W * 2, rect.H * 2
  let dockX = CanvasWidth - scaledWidth
  let splitA, splitB = Pad.Named "mag_body", Pad.Named "mag_src"
  let bodyOut, magOut = Pad.Named "mag_bodied", Pad.Named "mag_pip"

  FilterGraph.Complex
    [ FilterGraph.Labeled([ mainPad ], FilterGraph.Split 2, [ splitA; splitB ])
      FilterGraph.Labeled([ splitA ], bodyChain, [ bodyOut ])
      FilterGraph.Labeled([ splitB ], FilterGraph.Chain [ FilterGraph.Crop rect; FilterGraph.Scale(scaledWidth, scaledHeight) ], [ magOut ])
      FilterGraph.Labeled([ bodyOut; magOut ], FilterGraph.Overlay(dockX, 0), [ outPad ]) ]

/// Builds the per-step overlay chain (cursor+ripple, caption band, counter,
/// progress dots — §4.6, §9) for one step, wrapped in the magnifier's
/// self-split when the layout has an editor pane, wired to `mainPad` in and
/// `outPad` out.
let private stepNode (style: Style) (magnifier: Rect option) (index: int) (total: int) (timing: StepTiming) (caption: Caption) (pointerPath: Point list) (mainPad: Pad) (outPad: Pad) : FilterGraph =
  let overlays = cursorOverlay style timing pointerPath @ captionBand style index total caption @ stepDots style index total
  let bodyChain = FilterGraph.Chain overlays

  match magnifier with
  | Some rect -> withMagnifier rect bodyChain mainPad outPad
  | None -> FilterGraph.Labeled([ mainPad ], bodyChain, [ outPad ])

/// Builds the filtergraph for the §4.6 GIF pipeline, as a real multi-input
/// `-filter_complex`: each step's own `-i` input pad gets its cursor/ripple
/// overlay, caption band, counter, dots and (optional) magnifier applied
/// FIRST — while that step's own segment still has its own independent
/// timeline, which is what makes a time-gated `enable='between(t,...)'`
/// cursor hold or ripple meaningful (§4.6's pinned "concat then overlay"
/// order describes the visual result, not the only legal filter-graph
/// shape: overlaying after a concat would need one global-timeline
/// enable-window per step instead of one per-segment window, which is
/// strictly more filter-graph plumbing for the identical picture — so this
/// is the equivalent, ffmpeg-shaped ordering, not a shortcut). The
/// per-step outputs are THEN concatenated, `mpdecimate`+`setpts`'d (never
/// `fps` — see the module doc: `fps` resamples to a constant rate and
/// re-materialises exactly the duplicate frames `mpdecimate` just dropped,
/// the corrected ordering bug §4.6 calls out), and finally palette-encoded
/// via the standard two-pass `split` → `palettegen(stats_mode=diff)` →
/// `paletteuse(dither=sierra2_4a)` technique (§4.6) — itself only expressible
/// with labeled pads, which is exactly what this DU now models. Every ffmpeg
/// filtergraph pad has exactly ONE consumer (a second `-map`/filter input
/// reading an already-consumed pad is a runtime "does not exist ... or was
/// already used elsewhere" error — confirmed directly against this machine's
/// ffmpeg), so the post-decimate `[vd]` pad is split a further THREE ways —
/// `v1`/`v2` for the palette technique's own two branches, and `vmp4` purely
/// so a constant-frame-rate `.mp4` output can map a copy without stealing the
/// palette branches' only input. The final `[outv]` pad is the GIF-ready
/// stream; `[vmp4]` is what the `.mp4` output maps (§9).
let render (plan: ComposePlan) : FilterGraph =
  let total = plan.Segments.Length

  let stepNodes =
    [ for index in 0 .. total - 1 ->
        let timing = plan.Timings.[index]
        let caption = plan.Captions.[index]
        let pointerPath = plan.PointerPaths.[index]
        stepNode plan.Style plan.Magnifier index total timing caption pointerPath (Pad.Input index) (Pad.Named(sprintf "s%d" index)) ]

  let stepPads = [ for index in 0 .. total - 1 -> Pad.Named(sprintf "s%d" index) ]

  let concatNode = FilterGraph.Labeled(stepPads, FilterGraph.Concat total, [ Pad.Named "base" ])
  let decimateNode = FilterGraph.Labeled([ Pad.Named "base" ], FilterGraph.Chain [ FilterGraph.MpDecimate; FilterGraph.SetPts 1.0 ], [ Pad.Named "vd" ])
  let splitNode = FilterGraph.Labeled([ Pad.Named "vd" ], FilterGraph.Split 3, [ Pad.Named "v1"; Pad.Named "v2"; Pad.Named "vmp4" ])
  let paletteGenNode = FilterGraph.Labeled([ Pad.Named "v2" ], FilterGraph.PaletteGen "diff", [ Pad.Named "pal" ])
  let paletteUseNode = FilterGraph.Labeled([ Pad.Named "v1"; Pad.Named "pal" ], FilterGraph.PaletteUse "sierra2_4a", [ Pad.Named "outv" ])

  FilterGraph.Complex(stepNodes @ [ concatNode; decimateNode; splitNode; paletteGenNode; paletteUseNode ])
