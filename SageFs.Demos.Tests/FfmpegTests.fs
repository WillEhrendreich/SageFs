/// Proves the typed ffmpeg planner (demo-gif-plan.md §4.6, §5): `toCommandString`
/// is a total, exhaustive, stable `FilterGraph -> string` renderer — including
/// the multi-input `Labeled`/`Complex` pad-labeling layer that lets a real
/// `-filter_complex` graph be expressed as data — and `render` builds the
/// §4.6 GIF pipeline as one such graph: a per-step overlay node (cursor+
/// ripple, caption band, counter, dots, optional magnifier) wired to each
/// step's own `-i` input, concatenated, then `mpdecimate`+`setpts` (never
/// `fps` — the corrected dedup/fps-ordering bug the plan calls out) and the
/// two-pass GIF palette technique.
module SageFs.Demos.Tests.FfmpegTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain
open SageFs.Demos.Ffmpeg

/// Walks a `FilterGraph`, flattening every `Chain`/`Complex`/`Labeled` down
/// to its leaf filters, in traversal order — for assertions that only care
/// "does X appear, and in what relative order", not the pad wiring around it.
let rec private allLeaves (graph: FilterGraph) : FilterGraph list =
  match graph with
  | FilterGraph.Chain items -> items |> List.collect allLeaves
  | FilterGraph.Complex nodes -> nodes |> List.collect allLeaves
  | FilterGraph.Labeled(_, filter, _) -> allLeaves filter
  | leaf -> [ leaf ]

/// Every `-filter_complex` node of a rendered graph, descending through
/// nested `Complex`es (the magnifier's own split/crop/overlay sub-graph is
/// itself a `Complex` nested inside one step's slot — §4.6) so a search for
/// "the node reading Input i" or "the node whose filter is X" finds it
/// regardless of nesting depth. Does NOT descend into a `Labeled` node's own
/// `filter` — that's what `allLeaves` is for.
let rec private allNodes (graph: FilterGraph) : FilterGraph list =
  match graph with
  | FilterGraph.Complex nodes -> nodes |> List.collect allNodes
  | other -> [ other ]

let private tryFindLabeled (predicate: FilterGraph -> bool) (nodes: FilterGraph list) : (Pad list * FilterGraph * Pad list) option =
  nodes
  |> List.tryPick (function
    | FilterGraph.Labeled(ins, f, outs) when predicate f -> Some(ins, f, outs)
    | _ -> None)

let private isConcat = function FilterGraph.Concat _ -> true | _ -> false
let private isFps = function FilterGraph.Fps _ -> true | _ -> false
let private isMpDecimate = function FilterGraph.MpDecimate -> true | _ -> false
let private containsMpDecimate = function
  | FilterGraph.MpDecimate -> true
  | FilterGraph.Chain items -> items |> List.exists (function FilterGraph.MpDecimate -> true | _ -> false)
  | _ -> false
let private isSetPts = function FilterGraph.SetPts _ -> true | _ -> false
let private isPaletteGen = function FilterGraph.PaletteGen _ -> true | _ -> false
let private isPaletteUse = function FilterGraph.PaletteUse _ -> true | _ -> false
let private isSplit = function FilterGraph.Split _ -> true | _ -> false
let private isCrop = function FilterGraph.Crop _ -> true | _ -> false
let private isOverlay = function FilterGraph.Overlay _ -> true | _ -> false
let private isDrawTextStyled = function FilterGraph.DrawTextStyled _ -> true | _ -> false

let private isCursorBox = function
  | FilterGraph.DrawBoxTimed(_, _, None, Thickness.Fill, Some _) -> true
  | _ -> false

let private isRippleBox = function
  | FilterGraph.DrawBoxTimed(_, _, _, Thickness.Outline _, _) -> true
  | _ -> false

let private isCaptionBandBox = function
  | FilterGraph.DrawBoxTimed(_, _, Some _, Thickness.Fill, None) -> true
  | _ -> false

let private timing startedMs endedMs observedAtMs : StepTiming =
  { StartedMs = startedMs; EndedMs = endedMs; ObservedAtMs = observedAtMs }

let private samplePlan : ComposePlan =
  { Segments = [ "/out/step-00.mkv"; "/out/step-01.mkv" ]
    Layout = Map.ofList [ ActorId.VsCode, { X = 0; Y = 0; W = 704; H = 720 } ]
    Style = Style.kanagawa
    Captions = [ Caption.mk "1/2 · Press Run"; Caption.mk "2/2 · Save" ]
    PointerPaths = [ [ { X = 620; Y = 300 } ]; [] ]
    Timings = [ timing 0 2000 1500; timing 2000 3500 3200 ]
    Magnifier = Some { X = 0; Y = 0; W = 704; H = 720 } }

let private noMagnifierPlan =
  { samplePlan with
      Magnifier = None
      PointerPaths = [ []; [] ] }

/// Same shape as `samplePlan` (step 0 has a recorded pointer path, step 1
/// does not) but with NO magnifier, so each step's top-level node is a plain
/// `Labeled([Pad.Input i], ...)` rather than nested inside the magnifier's
/// own `Complex` sub-graph — isolates "does render place cursor/ripple/dots
/// correctly per step" from "does render wrap a step in the magnifier's
/// split/crop/overlay", which is tested separately.
let private stepContentPlan = { samplePlan with Magnifier = None }

[<Tests>]
let tests =
  testList "Ffmpeg" [

    testList "toCommandString — one golden string per FilterGraph case (exhaustive, §4.6)" [

      testCase "Scale" <| fun _ ->
        FilterGraph.Scale(1280, 720) |> toCommandString |> Expect.equal "scale filter" "scale=1280:720"

      testCase "Fps" <| fun _ ->
        FilterGraph.Fps 12 |> toCommandString |> Expect.equal "fps filter" "fps=12"

      testCase "Overlay" <| fun _ ->
        FilterGraph.Overlay(620, 300) |> toCommandString |> Expect.equal "overlay filter" "overlay=620:300"

      testCase "DrawBox with a Kanagawa hex color" <| fun _ ->
        FilterGraph.DrawBox({ X = 0; Y = 664; W = 1280; H = 56 }, "#1f1f28")
        |> toCommandString
        |> Expect.equal "drawbox filter, # rewritten to 0x for ffmpeg" "drawbox=x=0:y=664:w=1280:h=56:color=0x1f1f28:t=fill"

      testCase "DrawText escapes ffmpeg-special characters" <| fun _ ->
        FilterGraph.DrawText("3/4 : it's, done\\", 24, 692)
        |> toCommandString
        |> Expect.equal
          "colon, single quote, comma and backslash all escaped"
          "drawtext=text='3/4 \\: it\\'s\\, done\\\\':x=24:y=692"

      testCase "Crop" <| fun _ ->
        FilterGraph.Crop { X = 0; Y = 0; W = 704; H = 720 }
        |> toCommandString
        |> Expect.equal "crop filter is w:h:x:y" "crop=704:720:0:0"

      testCase "Concat" <| fun _ ->
        FilterGraph.Concat 5 |> toCommandString |> Expect.equal "concat filter, video-only" "concat=n=5:v=1:a=0"

      testCase "MpDecimate" <| fun _ ->
        FilterGraph.MpDecimate |> toCommandString |> Expect.equal "mpdecimate filter" "mpdecimate"

      testCase "SetPts with a whole-number factor" <| fun _ ->
        FilterGraph.SetPts 1.0 |> toCommandString |> Expect.equal "setpts filter, no stray decimal" "setpts=1*PTS"

      testCase "SetPts with a fractional factor" <| fun _ ->
        FilterGraph.SetPts 1.25 |> toCommandString |> Expect.equal "setpts filter, fractional factor" "setpts=1.25*PTS"

      testCase "PaletteGen with stats_mode=diff (§4.6)" <| fun _ ->
        FilterGraph.PaletteGen "diff"
        |> toCommandString
        |> Expect.equal "palettegen filter" "palettegen=stats_mode=diff"

      testCase "PaletteUse with dither=sierra2_4a (§4.6, pinned)" <| fun _ ->
        FilterGraph.PaletteUse "sierra2_4a"
        |> toCommandString
        |> Expect.equal "paletteuse filter" "paletteuse=dither=sierra2_4a"

      testCase "Chain renders its members comma-joined, in order" <| fun _ ->
        FilterGraph.Chain [ FilterGraph.Scale(1280, 720); FilterGraph.MpDecimate; FilterGraph.SetPts 1.0 ]
        |> toCommandString
        |> Expect.equal "comma-joined sub-filters" "scale=1280:720,mpdecimate,setpts=1*PTS"

      testCase "Chain nests recursively" <| fun _ ->
        let inner = FilterGraph.Chain [ FilterGraph.Crop { X = 0; Y = 0; W = 10; H = 10 }; FilterGraph.Scale(20, 20) ]
        FilterGraph.Chain [ inner; FilterGraph.Overlay(0, 0) ]
        |> toCommandString
        |> Expect.equal "nested Chains flatten to one comma-joined string" "crop=10:10:0:0,scale=20:20,overlay=0:0"

      testCase "DrawBoxTimed with fixed extents, no alpha, filled, ungated" <| fun _ ->
        let rect = { Left = Extent.Fixed 10; Top = Extent.Fixed 20; BoxWidth = Extent.Fixed 30; BoxHeight = Extent.Fixed 40 }
        FilterGraph.DrawBoxTimed(rect, "#dcd7ba", None, Thickness.Fill, None)
        |> toCommandString
        |> Expect.equal "fixed drawbox, no alpha, no enable gate" "drawbox=x=10:y=20:w=30:h=40:color=0xdcd7ba:t=fill"

      testCase "DrawBoxTimed with alpha renders an @alpha suffix on the color (§9 caption band opacity)" <| fun _ ->
        let rect = { Left = Extent.Fixed 0; Top = Extent.Fixed 664; BoxWidth = Extent.Fixed 1280; BoxHeight = Extent.Fixed 56 }
        FilterGraph.DrawBoxTimed(rect, "#1f1f28", Some 0.88, Thickness.Fill, None)
        |> toCommandString
        |> Expect.equal "alpha appended as @0.88" "drawbox=x=0:y=664:w=1280:h=56:color=0x1f1f28@0.88:t=fill"

      testCase "DrawBoxTimed with an Outline thickness and an enable gate (§9 click ripple)" <| fun _ ->
        let rect =
          { Left = Extent.Expr "620-((min(8+80*(t-1.5),28))/2)"
            Top = Extent.Fixed 300
            BoxWidth = Extent.Expr "min(8+80*(t-1.5),28)"
            BoxHeight = Extent.Fixed 28 }
        FilterGraph.DrawBoxTimed(rect, "#7e9cd8", None, Thickness.Outline 2, Some "between(t,1.5,1.75)")
        |> toCommandString
        |> Expect.equal
          "expression extents are single-quoted (ffmpeg's option parser would otherwise split on the internal comma), fixed extents stay bare, thickness and enable render as before"
          "drawbox=x='620-((min(8+80*(t-1.5),28))/2)':y=300:w='min(8+80*(t-1.5),28)':h=28:color=0x7e9cd8:t=2:enable='between(t,1.5,1.75)'"

      testCase "DrawTextStyled renders color, size and font, and escapes special characters" <| fun _ ->
        FilterGraph.DrawTextStyled("3/6 · Save the file", 24, 692, "#dcd7ba", 22, None)
        |> toCommandString
        |> Expect.equal
          "styled drawtext with fontcolor/fontsize/font"
          "drawtext=text='3/6 · Save the file':x=24:y=692:fontcolor=0xdcd7ba:fontsize=22:font=Noto Sans"

      testCase "DrawTextStyled with an enable gate appends it" <| fun _ ->
        FilterGraph.DrawTextStyled("hi", 0, 0, "#dcd7ba", 22, Some "between(t,0,1)")
        |> toCommandString
        |> Expect.equal "enable gate appended" "drawtext=text='hi':x=0:y=0:fontcolor=0xdcd7ba:fontsize=22:font=Noto Sans:enable='between(t,0,1)'"

      testCase "Split renders its output count" <| fun _ ->
        FilterGraph.Split 2 |> toCommandString |> Expect.equal "split filter" "split=2"

      testCase "Labeled brackets its input and output pads around the rendered filter" <| fun _ ->
        FilterGraph.Labeled([ Pad.Input 0; Pad.Input 1 ], FilterGraph.Concat 2, [ Pad.Named "base" ])
        |> toCommandString
        |> Expect.equal "input pads as i:v, output pad as a bare bracketed name" "[0:v][1:v]concat=n=2:v=1:a=0[base]"

      testCase "Labeled with a Chain filter renders the whole chain inside its pads" <| fun _ ->
        FilterGraph.Labeled([ Pad.Named "v1" ], FilterGraph.Chain [ FilterGraph.MpDecimate; FilterGraph.SetPts 1.0 ], [ Pad.Named "vd" ])
        |> toCommandString
        |> Expect.equal "chain renders comma-joined inside the pad brackets" "[v1]mpdecimate,setpts=1*PTS[vd]"

      testCase "Complex joins its nodes with ';' — ffmpeg's own filter_complex node separator" <| fun _ ->
        FilterGraph.Complex
          [ FilterGraph.Labeled([ Pad.Input 0 ], FilterGraph.Split 2, [ Pad.Named "a"; Pad.Named "b" ])
            FilterGraph.Labeled([ Pad.Named "a"; Pad.Named "b" ], FilterGraph.Overlay(0, 0), [ Pad.Named "out" ]) ]
        |> toCommandString
        |> Expect.equal "semicolon-joined filter_complex nodes" "[0:v]split=2[a][b];[a][b]overlay=0:0[out]"
    ]

    testCase "toCommandString is stable: the same FilterGraph renders to the same string every call" <| fun _ ->
      let graph = FilterGraph.Chain [ FilterGraph.MpDecimate; FilterGraph.SetPts 2.0; FilterGraph.PaletteUse "sierra2_4a" ]
      toCommandString graph |> Expect.equal "first render" (toCommandString graph)

    testList "render — a real multi-input filter_complex (§4.6)" [

      testCase "render's output is a Complex graph — a real -filter_complex, not a single linear Chain" <| fun _ ->
        match render samplePlan with
        | FilterGraph.Complex _ -> ()
        | other -> failtestf "expected FilterGraph.Complex, got %A" other

      testCase "render's leaves use mpdecimate + setpts, and NEVER fps (§4.6 pinned bug fix)" <| fun _ ->
        let leaves = render samplePlan |> allLeaves
        leaves |> List.exists isMpDecimate |> Expect.isTrue "mpdecimate is present"
        leaves |> List.exists isSetPts |> Expect.isTrue "setpts is present"
        leaves |> List.exists isFps |> Expect.isFalse "fps must never appear — it re-materialises the frames mpdecimate just dropped"

      testCase "render orders concat -> mpdecimate -> setpts -> palettegen -> paletteuse (§4.6)" <| fun _ ->
        let leaves = render samplePlan |> allLeaves
        let indexOf predicate = leaves |> List.findIndex predicate
        indexOf isConcat < indexOf isMpDecimate |> Expect.isTrue "concat before mpdecimate"
        indexOf isMpDecimate < indexOf isSetPts |> Expect.isTrue "mpdecimate before setpts"
        indexOf isSetPts < indexOf isPaletteGen |> Expect.isTrue "setpts before palettegen"
        indexOf isPaletteGen < indexOf isPaletteUse |> Expect.isTrue "palettegen before paletteuse"

      testCase "render's concat node consumes exactly one pad per step, in step order, from a Named per-step output" <| fun _ ->
        let nodes = render samplePlan |> allNodes
        match tryFindLabeled isConcat nodes with
        | None -> failtest "expected a Labeled Concat node"
        | Some(ins, filter, outs) ->
          match filter with
          | FilterGraph.Concat n -> n |> Expect.equal "concat covers every segment" samplePlan.Segments.Length
          | other -> failtestf "expected Concat, got %A" other
          ins.Length |> Expect.equal "one input pad per segment" samplePlan.Segments.Length
          outs |> Expect.equal "concat produces exactly one named output pad" [ Pad.Named "base" ]

      testCase "render's per-step nodes are wired to Input 0, Input 1, ... in step order" <| fun _ ->
        let nodes = render samplePlan |> allNodes
        let stepInputPads =
          nodes
          |> List.choose (function
            | FilterGraph.Labeled([ Pad.Input i ], _, _) -> Some i
            | _ -> None)
          |> List.distinct
          |> List.sort
        stepInputPads |> Expect.equal "one Labeled node reading straight from each -i input, in order" [ 0 .. samplePlan.Segments.Length - 1 ]

      testCase "render's palette stage is a real two-pass split/palettegen/paletteuse, ending at pad 'outv', with a 3rd tap for the .mp4 (§4.6, §9)" <| fun _ ->
        let nodes = render samplePlan |> allNodes
        // The palette-stage split is identified by its own input pad ('vd',
        // the post-decimate pad every step feeds into via concat), not just
        // "any Split" — a magnified plan's per-step nodes contain their OWN
        // (also arity-2) split for the picture-in-picture, so searching by
        // filter shape alone would under-specify which split this is about.
        match nodes |> List.tryPick (function FilterGraph.Labeled([ Pad.Named "vd" ], f, outs) -> Some(f, outs) | _ -> None) with
        | None -> failtest "expected a Labeled node reading pad 'vd' — the palette-stage split"
        | Some(filter, outs) ->
          // A single ffmpeg pad has exactly one consumer, so a THIRD branch
          // is required purely to give the .mp4 output its own tap without
          // stealing either palette branch's only input (verified directly
          // against this machine's ffmpeg: reusing a consumed pad in a
          // second -map fails with "does not exist ... or was already used
          // elsewhere").
          filter |> Expect.equal "splits into 3 branches: paletteuse's video, palettegen's video, and the .mp4 tap" (FilterGraph.Split 3)
          outs |> Expect.equal "v1 (paletteuse), v2 (palettegen), vmp4 (the .mp4 output's own tap)" [ Pad.Named "v1"; Pad.Named "v2"; Pad.Named "vmp4" ]
        match tryFindLabeled isPaletteUse nodes with
        | None -> failtest "expected a Labeled PaletteUse node"
        | Some(ins, _, outs) ->
          ins.Length |> Expect.equal "paletteuse takes the video AND the palette (two inputs)" 2
          outs |> Expect.equal "paletteuse produces the GIF-ready pad" [ Pad.Named "outv" ]

      testCase "render's decimate node produces pad 'vd' — what a constant-rate .mp4 output maps instead of the palette pad" <| fun _ ->
        let nodes = render samplePlan |> allNodes
        match tryFindLabeled containsMpDecimate nodes with
        | None -> failtest "expected a Labeled node whose Chain contains MpDecimate"
        | Some(_, _, outs) -> outs |> Expect.equal "decimate/retime output pad" [ Pad.Named "vd" ]

      testCase "render includes a caption-band box, a counter, and the caption text for every step" <| fun _ ->
        let leaves = render samplePlan |> allLeaves
        leaves |> List.filter isCaptionBandBox |> List.length |> Expect.equal "one translucent caption band per step" samplePlan.Segments.Length
        leaves |> List.filter isDrawTextStyled |> List.length
        |> Expect.equal "two styled texts per step: the counter and the caption" (samplePlan.Segments.Length * 2)

      testCase "render overlays the cursor + ripple only for the step with a non-empty pointer path" <| fun _ ->
        let nodes = render stepContentPlan |> allNodes
        match nodes |> List.tryPick (function FilterGraph.Labeled([ Pad.Input 0 ], f, _) -> Some f | _ -> None) with
        | None -> failtest "expected step 0's own node"
        | Some step0 ->
          let leaves = allLeaves step0
          leaves |> List.exists isCursorBox |> Expect.isTrue "step 0 (non-empty pointer path) draws a cursor"
          leaves |> List.exists isRippleBox |> Expect.isTrue "step 0 draws a click ripple"
        match nodes |> List.tryPick (function FilterGraph.Labeled([ Pad.Input 1 ], f, _) -> Some f | _ -> None) with
        | None -> failtest "expected step 1's own node"
        | Some step1 ->
          let leaves = allLeaves step1
          leaves |> List.exists isCursorBox |> Expect.isFalse "step 1 (empty pointer path) draws no cursor"
          leaves |> List.exists isRippleBox |> Expect.isFalse "step 1 draws no ripple"

      testCase "render's progress dots mark each step's OWN index Accent and every other step Panel" <| fun _ ->
        let nodes = render stepContentPlan |> allNodes
        let dotsOf padIndex =
          nodes
          |> List.tryPick (function FilterGraph.Labeled([ Pad.Input i ], f, _) when i = padIndex -> Some(allLeaves f) | _ -> None)
          |> Option.defaultValue []
          |> List.choose (function FilterGraph.DrawBox(_, color) -> Some color | _ -> None)
        let step0Dots = dotsOf 0
        step0Dots |> List.filter (fun c -> c = Style.kanagawa.Accent) |> List.length |> Expect.equal "exactly one Accent dot (this step)" 1
        step0Dots |> List.filter (fun c -> c = Style.kanagawa.Panel) |> List.length |> Expect.equal "one Panel dot per other step" (stepContentPlan.Segments.Length - 1)

      testCase "render includes the magnifier's split+crop+scale+overlay when the plan has one (§4.6)" <| fun _ ->
        let leaves = render samplePlan |> allLeaves
        leaves |> List.exists isSplit |> Expect.isTrue "magnifier requires a split (palette also uses one, so this alone is a lower bound)"
        leaves |> List.exists isCrop |> Expect.isTrue "magnifier crops the editor pane"
        leaves |> List.exists isOverlay |> Expect.isTrue "magnifier overlays the PiP onto the main frame"

      testCase "render omits the magnifier crop when the plan has none (DashboardOnly layouts, §9)" <| fun _ ->
        let leaves = render noMagnifierPlan |> allLeaves
        leaves |> List.exists isCrop |> Expect.isFalse "no editor pane to magnify"

      testCase "a single-step plan (hello-dashboard's shape) still renders a valid graph with one input pad" <| fun _ ->
        let oneStepPlan =
          { samplePlan with
              Segments = [ "/out/step-00.mkv" ]
              Captions = [ Caption.mk "1/1 · Press Quick Start" ]
              PointerPaths = [ [ { X = 620; Y = 300 }; { X = 630; Y = 298 } ] ]
              Timings = [ timing 0 2500 1800 ]
              Magnifier = None }
        let nodes = render oneStepPlan |> allNodes
        nodes |> List.exists (function FilterGraph.Labeled([ Pad.Input 0 ], _, _) -> true | _ -> false)
        |> Expect.isTrue "the one step reads straight from input 0"
        match tryFindLabeled isConcat nodes with
        | Some(ins, FilterGraph.Concat 1, _) -> ins.Length |> Expect.equal "concat of one input is still well-formed" 1
        | other -> failtestf "expected a 1-input Concat node, got %A" other
    ]
  ]
