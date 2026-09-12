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
/// over 250ms". `drawbox` only draws axis-aligned rectangles — no vector
/// paths, no native circle/arrow primitive (confirmed against this
/// machine's `ffmpeg -filters`) — so a stack of rectangles can only ever
/// stair-step a diagonal edge or a ring; a real screen-recorder-quality
/// cursor and ripple are pre-rendered, anti-aliased PNGs (`assets/cursor.png`,
/// `assets/ripple.png` — generated once via ImageMagick at 4x scale then
/// downscaled, per §9/§8) composited with `overlay`, exactly how every real
/// screen recorder draws a synthetic pointer. `CursorHotspotX/Y` is the pixel
/// offset from the cursor image's top-left corner to its ACTUAL pointer tip
/// (baked into the asset's own geometry, not detected at runtime), so the
/// tip — not the image's corner — lands on the recorded pointer coordinate.
[<Literal>]
let private CursorHotspotX = 2

[<Literal>]
let private CursorHotspotY = 2

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
  | FilterGraph.Overlay (x, y, enable) ->
    // `eof_action=endall` (never the `overlay` filter's own default,
    // `repeat`): every overlay in this tool composites a FINITE video
    // against an INFINITE `-loop 1` PNG asset (cursor/ripple), and
    // `repeat` freezes the finite input's last frame and keeps pulling
    // frames from the still-infinite one FOREVER instead of ending —
    // confirmed directly against this machine's ffmpeg (a 2.6s clip
    // never finished encoding). `endall` ends the filter's output as
    // soon as the SHORTER of the two inputs ends, which is always the
    // finite video here (and is a no-op for the magnifier's two
    // same-length inputs).
    let core = sprintf "overlay=x=%s:y=%s:eof_action=endall" (extentString x) (extentString y)
    match enable with
    | Some expr -> core + sprintf ":enable='%s'" expr
    | None -> core
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
  | FilterGraph.ScaleTimed (width, height) ->
    sprintf "scale=w=%s:h=%s:eval=frame" (extentString width) (extentString height)
  | FilterGraph.Labeled (inputs, filter, outputs) ->
    let ins = inputs |> List.map bracket |> String.concat ""
    let outs = outputs |> List.map bracket |> String.concat ""
    sprintf "%s%s%s" ins (toCommandString filter) outs
  | FilterGraph.Complex nodes -> nodes |> List.map toCommandString |> String.concat ";"

// ---------------------------------------------------------------------------
// render — the §4.6 GIF pipeline, built as a real multi-input filter_complex.
// ---------------------------------------------------------------------------

/// One ffmpeg `if(lt(t,threshold),value,...)` ladder: `samples` are
/// (holdEndSec, valueDuringThatHold) pairs for every hold EXCEPT the last,
/// `finalValue` is the value once `t` passes every threshold. This is what
/// lets ONE `overlay` filter carry the cursor across every recorded sample —
/// the alternative (one chained `overlay` filter per hold, gated by its own
/// `enable` window) is DATA-equivalent but was measured directly against
/// this machine's ffmpeg to be drastically slower: ~20 chained `overlay`
/// filters over a 3s clip took minutes, because ffmpeg re-synchronizes the
/// looped image input at every single chained filter. A single filter whose
/// position EXPRESSION varies with `t` costs one filter's overhead no matter
/// how many holds it encodes.
let private ifLadder (samples: (float * int) list) (finalValue: int) : string =
  List.foldBack (fun (threshold, value) acc -> sprintf "if(lt(t,%s),%d,%s)" (formatFactor threshold) value acc) samples (string finalValue)

/// Builds the `x`/`y` position expressions for the WHOLE cursor path in one
/// step: `points` held in sequence, each for an equal share of `durationSec`
/// (§4.1's `PointerPath` carries no per-sample timestamp, so even spacing
/// across the step's own recorded `[Started,Ended]` window is the honest
/// reading of the data actually available — unchanged from the original
/// per-hold design, just now expressed as one pair of formulas instead of N
/// separately-gated filters).
let private cursorMotionExprs (points: Point list) (durationSec: float) : string * string =
  let n = points.Length
  let holdEndSec i = durationSec * float (i + 1) / float n

  let samples =
    points
    |> List.take (n - 1)
    |> List.mapi (fun i point -> holdEndSec i, point)

  let lastPoint = List.last points
  let xLadder = ifLadder (samples |> List.map (fun (t, p) -> t, p.X - CursorHotspotX)) (lastPoint.X - CursorHotspotX)
  let yLadder = ifLadder (samples |> List.map (fun (t, p) -> t, p.Y - CursorHotspotY)) (lastPoint.Y - CursorHotspotY)
  xLadder, yLadder

/// The single `Overlay` compositing the pre-rendered cursor image
/// (`cursorPad`, an ffmpeg `-i cursor.png -loop 1` input Runtime.fs appends)
/// onto `inPad` across the WHOLE step, positioned by `cursorMotionExprs`
/// (§4.6). One filter for the whole path, not one per sample — see
/// `ifLadder`'s doc for why that matters.
let private cursorMotion (cursorPad: Pad) (points: Point list) (durationSec: float) (inPad: Pad) (outPad: Pad) : FilterGraph =
  let xExpr, yExpr = cursorMotionExprs points durationSec

  FilterGraph.Labeled(
    [ inPad; cursorPad ],
    FilterGraph.Overlay(Extent.Expr xExpr, Extent.Expr yExpr, Some(sprintf "between(t,0,%s)" (formatFactor durationSec))),
    [ outPad ]
  )

/// The click ripple (§9: "Accent ring 8→28px over 250ms"): the pre-rendered
/// ring PNG (`ripplePad`), `ScaleTimed` to a size that grows over the
/// ripple's own window, then overlaid centered on `origin` using ffmpeg's
/// own `overlay_w`/`overlay_h` runtime variables — so the centering formula
/// never has to duplicate whatever `ScaleTimed` computed. The size
/// expression is `clip`ped to `[RippleFromSize,RippleToSize]` for ALL time,
/// not just the active window: `scale` (unlike `drawbox`/`overlay`) has no
/// `enable` gate, so it re-evaluates every frame of the whole clip, and an
/// unclamped `8 + rate*(t-start)` goes negative long before/after the
/// window and crashes `scale` with an invalid (non-positive) dimension —
/// confirmed directly against this machine's ffmpeg. The visible ON/OFF
/// gating is entirely the `overlay`'s own `enable`.
let private rippleNodes (ripplePad: Pad) (index: int) (startSec: float) (origin: Point) (inPad: Pad) (outPad: Pad) : FilterGraph list =
  let endSec = startSec + RippleDurationSec
  let growthPerSec = float (RippleToSize - RippleFromSize) / RippleDurationSec
  let rawSizeExpr = sprintf "%d+%s*(t-%s)" RippleFromSize (formatFactor growthPerSec) (formatFactor startSec)
  let sizeExpr = sprintf "clip(%s,%d,%d)" rawSizeExpr RippleFromSize RippleToSize
  let scaledPad = Pad.Named(sprintf "ring%d" index)

  [ FilterGraph.Labeled([ ripplePad ], FilterGraph.ScaleTimed(Extent.Expr sizeExpr, Extent.Expr sizeExpr), [ scaledPad ])
    FilterGraph.Labeled(
      [ inPad; scaledPad ],
      FilterGraph.Overlay(
        Extent.Expr(sprintf "%d-overlay_w/2" origin.X),
        Extent.Expr(sprintf "%d-overlay_h/2" origin.Y),
        Some(sprintf "between(t,%s,%s)" (formatFactor startSec) (formatFactor endSec))
      ),
      [ outPad ]
    ) ]

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

/// Threads the per-step content — the caption band, counter and progress
/// dots, then the cursor holds and the click ripple (§4.6, §9) — as a linear
/// sequence of nodes from `startPad` to `endPad`. Splices directly into the
/// enclosing node list: `toCommandString`'s `Complex` case `;`-joins
/// regardless of nesting depth, so a list of `Labeled` nodes here is exactly
/// as valid spliced into `render`'s top-level `Complex` as it is wrapped in
/// one of its own (`withMagnifier` relies on the same fact).
///
/// The caption/dots come FIRST, before the cursor/ripple overlays — not
/// last, despite neither §4.6's prose order nor visual layering requiring
/// it (the caption band and the cursor/ripple never occupy the same pixels
/// in this tool's layouts). This ordering is load-bearing for a real ffmpeg
/// defect, confirmed by bisection directly against this machine's ffmpeg: a
/// `drawtext`/`drawbox` chain fed from the OUTPUT of an `enable`+
/// `eof_action=endall`-gated `overlay` silently renders NOTHING from some
/// point in the timeline onward (the caption vanishes only after the first
/// enable-gated overlay's window has opened at least once — reproduced with
/// the cursor motion overlay alone, no ripple needed), while the identical
/// `drawtext`/`drawbox` chain feeding INTO that same overlay chain (this
/// ordering) is unaffected. Moving caption/dots ahead of the overlays keeps
/// the exact same pixels composited — only the ffmpeg node order changes.
let private contentNodes
  (style: Style)
  (cursorPad: Pad)
  (ripplePad: Pad)
  (index: int)
  (total: int)
  (timing: StepTiming)
  (caption: Caption)
  (pointerPath: Point list)
  (startPad: Pad)
  (endPad: Pad)
  : FilterGraph list =

  let freshLabel suffix = Pad.Named(sprintf "c%d_%s" index suffix)

  match pointerPath with
  | [] -> [ FilterGraph.Labeled([ startPad ], FilterGraph.Chain(captionBand style index total caption @ stepDots style index total), [ endPad ]) ]
  | points ->
    let captionedPad = freshLabel "captioned"
    let captionNode = FilterGraph.Labeled([ startPad ], FilterGraph.Chain(captionBand style index total caption @ stepDots style index total), [ captionedPad ])

    let durationSec = float (max 1 (timing.EndedMs - timing.StartedMs)) / 1000.0
    let motionPad = freshLabel "motion"
    let motionNode = cursorMotion cursorPad points durationSec captionedPad motionPad

    let rippleStartSec =
      max 0.0 (min (durationSec - RippleDurationSec) (float (timing.ObservedAtMs - timing.StartedMs) / 1000.0))

    let ripple = rippleNodes ripplePad index rippleStartSec (List.last points) motionPad endPad

    captionNode :: motionNode :: ripple

/// The 2× picture-in-picture magnifier over the editor pane (§4.6): a
/// self-referential PiP needs two copies of the SAME step input (`split`),
/// one cropped+scaled and overlaid onto the other — a single-input `Chain`
/// cannot express "overlay a video onto a cropped copy of itself" because
/// `overlay` is a two-input ffmpeg filter. Docked to the canvas's top-right
/// so it never overlaps the caption band at the bottom. Threads the step's
/// own already-composited content nodes as the base layer so the magnifier
/// sits on TOP of the cursor/ripple/caption, not underneath them.
let private withMagnifier (index: int) (rect: Rect) (bodyNodes: FilterGraph list) (bodyOut: Pad) (mainPad: Pad) (outPad: Pad) : FilterGraph =
  let scaledWidth, scaledHeight = rect.W * 2, rect.H * 2
  let dockX = CanvasWidth - scaledWidth
  // Indexed by step: two magnified steps in the same scenario would
  // otherwise both emit a label named "mag_src", and ffmpeg's filtergraph
  // labels are scenario-wide, not step-scoped — a bug that happened to be
  // invisible while every scenario this tool has actually recorded so far
  // had at most one magnified step.
  let splitA, splitB = Pad.Named(sprintf "mag%d_body" index), Pad.Named(sprintf "mag%d_src" index)
  let magOut = Pad.Named(sprintf "mag%d_pip" index)

  FilterGraph.Complex(
    [ FilterGraph.Labeled([ mainPad ], FilterGraph.Split 2, [ splitA; splitB ]) ]
    @ bodyNodes
    @ [ FilterGraph.Labeled([ splitB ], FilterGraph.Chain [ FilterGraph.Crop rect; FilterGraph.Scale(scaledWidth, scaledHeight) ], [ magOut ])
        FilterGraph.Labeled([ bodyOut; magOut ], FilterGraph.Overlay(Extent.Fixed dockX, Extent.Fixed 0, None), [ outPad ]) ]
  )

/// Builds the per-step node sequence (cursor+ripple, caption band, counter,
/// progress dots — §4.6, §9) for one step, wrapped in the magnifier's
/// self-split when the layout has an editor pane, wired to `mainPad` in and
/// `outPad` out.
let private stepNode
  (style: Style)
  (magnifier: Rect option)
  (cursorPad: Pad)
  (ripplePad: Pad)
  (index: int)
  (total: int)
  (timing: StepTiming)
  (caption: Caption)
  (pointerPath: Point list)
  (mainPad: Pad)
  (outPad: Pad)
  : FilterGraph =
  match magnifier with
  | Some rect ->
    let bodyStart = Pad.Named(sprintf "mag%d_body" index)
    let bodyOut = Pad.Named(sprintf "s%d_bodied" index)
    let bodyNodes = contentNodes style cursorPad ripplePad index total timing caption pointerPath bodyStart bodyOut
    withMagnifier index rect bodyNodes bodyOut mainPad outPad
  | None ->
    FilterGraph.Complex(contentNodes style cursorPad ripplePad index total timing caption pointerPath mainPad outPad)

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
/// filtergraph LABEL has exactly ONE consumer (a second `-map`/filter input
/// reading an already-consumed label is a runtime "does not exist ... or was
/// already used elsewhere" error — confirmed directly against this machine's
/// ffmpeg; a raw `-i` input pad has no such limit, which is what lets every
/// cursor hold and every step reuse the same cursor/ripple asset inputs), so
/// the post-decimate `[vd]` pad is split a further THREE ways — `v1`/`v2`
/// for the palette technique's own two branches, and `vmp4` purely so a
/// constant-frame-rate `.mp4` output can map a copy without stealing the
/// palette branches' only input. The final `[outv]` pad is the GIF-ready
/// stream; `[vmp4]` is what the `.mp4` output maps (§9). The cursor/ripple
/// PNG assets are the two `-i` inputs Runtime.fs appends right after every
/// step's own segment input, so their pad indices are always `total` and
/// `total + 1`.
let render (plan: ComposePlan) : FilterGraph =
  let total = plan.Segments.Length
  let cursorPad = Pad.Input total
  let ripplePad = Pad.Input(total + 1)

  let stepNodes =
    [ for index in 0 .. total - 1 ->
        let timing = plan.Timings.[index]
        let caption = plan.Captions.[index]
        let pointerPath = plan.PointerPaths.[index]
        stepNode plan.Style plan.Magnifier cursorPad ripplePad index total timing caption pointerPath (Pad.Input index) (Pad.Named(sprintf "s%d" index)) ]

  let stepPads = [ for index in 0 .. total - 1 -> Pad.Named(sprintf "s%d" index) ]

  let concatNode = FilterGraph.Labeled(stepPads, FilterGraph.Concat total, [ Pad.Named "base" ])
  let decimateNode = FilterGraph.Labeled([ Pad.Named "base" ], FilterGraph.Chain [ FilterGraph.MpDecimate; FilterGraph.SetPts 1.0 ], [ Pad.Named "vd" ])
  let splitNode = FilterGraph.Labeled([ Pad.Named "vd" ], FilterGraph.Split 3, [ Pad.Named "v1"; Pad.Named "v2"; Pad.Named "vmp4" ])
  let paletteGenNode = FilterGraph.Labeled([ Pad.Named "v2" ], FilterGraph.PaletteGen "diff", [ Pad.Named "pal" ])
  let paletteUseNode = FilterGraph.Labeled([ Pad.Named "v1"; Pad.Named "pal" ], FilterGraph.PaletteUse "sierra2_4a", [ Pad.Named "outv" ])

  FilterGraph.Complex(stepNodes @ [ concatNode; decimateNode; splitNode; paletteGenNode; paletteUseNode ])
