/// Pure ffmpeg planning (demo-gif-plan.md §4.6, §5). `render` turns a
/// `ComposePlan` into one `FilterGraph` value (concat → cursor/ripple
/// overlay → caption band → magnifier → mpdecimate+setpts → palettegen/use);
/// `toCommandString` is the ONE render-to-string function — no hand-built
/// filter strings anywhere else in the tool.
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

/// §9: "Progress dots (6 px, Panel/Accent) sit at the band's right edge."
[<Literal>]
let private DotSize = 6

[<Literal>]
let private DotSpacing = 4

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
/// style value the composer ever passes).
let private colorArg (color: string) : string =
  if color.StartsWith("#") then "0x" + color.Substring(1) else color

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

/// The one render-to-string function for `FilterGraph` (§4.6): every case is
/// total, and `Chain` recurses and comma-joins — ffmpeg's own separator
/// between filters in one `-filter_complex` chain.
let rec toCommandString (graph: FilterGraph) : string =
  match graph with
  | FilterGraph.Scale (width, height) -> sprintf "scale=%d:%d" width height
  | FilterGraph.Fps fps -> sprintf "fps=%d" fps
  | FilterGraph.Overlay (x, y) -> sprintf "overlay=%d:%d" x y
  | FilterGraph.DrawBox (rect, color) ->
    sprintf "drawbox=x=%d:y=%d:w=%d:h=%d:color=%s:t=fill" rect.X rect.Y rect.W rect.H (colorArg color)
  | FilterGraph.DrawText (text, x, y) -> sprintf "drawtext=text='%s':x=%d:y=%d" (escapeDrawText text) x y
  | FilterGraph.Crop rect -> sprintf "crop=%d:%d:%d:%d" rect.W rect.H rect.X rect.Y
  | FilterGraph.Concat inputs -> sprintf "concat=n=%d:v=1:a=0" inputs
  | FilterGraph.MpDecimate -> "mpdecimate"
  | FilterGraph.SetPts factor -> sprintf "setpts=%s*PTS" (formatFactor factor)
  | FilterGraph.PaletteGen statsMode -> sprintf "palettegen=stats_mode=%s" statsMode
  | FilterGraph.PaletteUse dither -> sprintf "paletteuse=dither=%s" dither
  | FilterGraph.Chain filters -> filters |> List.map toCommandString |> String.concat ","

// ---------------------------------------------------------------------------
// render — the §4.6 GIF pipeline, built in the pinned order.
// ---------------------------------------------------------------------------

/// The synthetic cursor + click-ripple overlay for one step's recorded
/// pointer path (§4.3 "cursor is synthetic", §4.6). `None` for a step with no
/// recorded motion (e.g. `Action.Await`, `Action.Chord`). Modeled as one
/// overlay at the path's resting point — a full per-frame motion trace needs
/// a time-varying ffmpeg expression this DU does not model yet
/// (TODO(shape): Wave 3).
let private cursorOverlay (pointerPath: Point list) : FilterGraph option =
  match pointerPath with
  | [] -> None
  | points ->
    let restingPoint = List.last points
    Some (FilterGraph.Overlay(restingPoint.X, restingPoint.Y))

/// The caption band for one step (§9): a `Ground`-colored band across the
/// bottom of the canvas with the caption drawn in `Ink`.
let private captionBand (style: Style) (caption: Caption) : FilterGraph =
  let band =
    { X = 0
      Y = CanvasHeight - CaptionBandHeight
      W = CanvasWidth
      H = CaptionBandHeight }
  let captionText = FilterGraph.DrawText(Caption.value caption, CaptionSidePadding, band.Y + (CaptionBandHeight / 2))
  FilterGraph.Chain [ FilterGraph.DrawBox(band, style.Ground); captionText ]

/// The 2× picture-in-picture magnifier over the editor pane (§4.6), docked to
/// the canvas's top-right so it never overlaps the caption band at the
/// bottom.
let private magnifierOverlay (rect: Rect) : FilterGraph =
  let scaledWidth, scaledHeight = rect.W * 2, rect.H * 2
  let dockX = CanvasWidth - scaledWidth
  FilterGraph.Chain [ FilterGraph.Crop rect; FilterGraph.Scale(scaledWidth, scaledHeight); FilterGraph.Overlay(dockX, 0) ]

/// One step-progress dot (§9): `total` dots along the band's right edge, one
/// per step in the recording.
let private stepDot (style: Style) (rightEdge: int) (y: int) (index: int) : FilterGraph =
  let x = rightEdge - (index + 1) * (DotSize + DotSpacing)
  FilterGraph.DrawBox({ X = x; Y = y; W = DotSize; H = DotSize }, style.Accent)

let private stepDots (style: Style) (totalSteps: int) : FilterGraph list =
  let y = CanvasHeight - CaptionBandHeight - DotSize - 8
  let rightEdge = CanvasWidth - CaptionSidePadding
  [ for index in 0 .. totalSteps - 1 -> stepDot style rightEdge y index ]

/// Builds the filtergraph for the §4.6 GIF pipeline, in the pinned order:
/// concat every segment -> cursor/ripple overlay per non-empty pointer path
/// -> caption band per step -> magnifier (only when the layout has an editor
/// pane) -> step-progress dots -> `mpdecimate` + `setpts` (never `fps` — see
/// the module doc: `fps` resamples to a constant rate and re-materialises
/// exactly the duplicate frames `mpdecimate` just dropped, which is the
/// corrected ordering bug §4.6 calls out) -> `palettegen` (`stats_mode=diff`)
/// -> `paletteuse` (`dither=sierra2_4a`). Part-splitting at 25s (§4.6) and the
/// constant-fps `.mp4` variant are output-selection concerns above this
/// single-filtergraph function, not additional filter nodes here.
let render (plan: ComposePlan) : FilterGraph =
  let concat = FilterGraph.Concat plan.Segments.Length
  let cursorOverlays = plan.PointerPaths |> List.choose cursorOverlay
  let captions = plan.Captions |> List.map (captionBand plan.Style)
  let magnifier = plan.Magnifier |> Option.map magnifierOverlay |> Option.toList
  let dots = stepDots plan.Style plan.Segments.Length
  FilterGraph.Chain (
    [ concat ]
    @ cursorOverlays
    @ captions
    @ magnifier
    @ dots
    @ [ FilterGraph.MpDecimate
        FilterGraph.SetPts 1.0
        FilterGraph.PaletteGen "diff"
        FilterGraph.PaletteUse "sierra2_4a" ]
  )
