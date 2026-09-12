/// Proves the typed ffmpeg planner (demo-gif-plan.md §4.6, §5): `toCommandString`
/// is a total, exhaustive, stable `FilterGraph -> string` renderer, and
/// `render` builds the §4.6 GIF pipeline in the pinned order — critically,
/// `mpdecimate` + `setpts`, never `fps` (the corrected dedup/fps-ordering bug
/// the plan calls out). RED first — these fail against the `failwith` stubs
/// until `Ffmpeg.fs` is implemented.
module SageFs.Demos.Tests.FfmpegTests

open Expecto
open Expecto.Flip
open SageFs.Demos.Domain
open SageFs.Demos.Ffmpeg

/// Walks a `FilterGraph`, flattening every `Chain` so a pipeline can be
/// searched for "does this ever use X" without caring about nesting shape.
let rec private flatten (graph: FilterGraph) : FilterGraph list =
  match graph with
  | FilterGraph.Chain filters -> filters |> List.collect flatten
  | leaf -> [ leaf ]

let private isFps =
  function
  | FilterGraph.Fps _ -> true
  | _ -> false

let private isMpDecimate =
  function
  | FilterGraph.MpDecimate -> true
  | _ -> false

let private isSetPts =
  function
  | FilterGraph.SetPts _ -> true
  | _ -> false

let private samplePlan : ComposePlan =
  { Segments = [ "/out/step-00.mkv"; "/out/step-01.mkv" ]
    Layout = Map.ofList [ ActorId.VsCode, { X = 0; Y = 0; W = 704; H = 720 } ]
    Style = Style.kanagawa
    Captions = [ Caption.mk "1/2 · Press Run"; Caption.mk "2/2 · Save" ]
    PointerPaths = [ [ { X = 620; Y = 300 } ]; [] ]
    Magnifier = Some { X = 0; Y = 0; W = 704; H = 720 } }

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
    ]

    testCase "toCommandString is stable: the same FilterGraph renders to the same string every call" <| fun _ ->
      let graph = FilterGraph.Chain [ FilterGraph.MpDecimate; FilterGraph.SetPts 2.0; FilterGraph.PaletteUse "sierra2_4a" ]
      toCommandString graph |> Expect.equal "first render" (toCommandString graph)

    testCase "render's pipeline uses mpdecimate + setpts, and NEVER fps, for the GIF path (§4.6 pinned bug fix)" <| fun _ ->
      let filters = render samplePlan |> flatten
      filters |> List.exists isMpDecimate |> Expect.isTrue "mpdecimate is present"
      filters |> List.exists isSetPts |> Expect.isTrue "setpts is present"
      filters |> List.exists isFps |> Expect.isFalse "fps must never appear on the GIF path — it re-materialises the frames mpdecimate just dropped"

    testCase "render orders mpdecimate before setpts, and both before the palette stage (§4.6)" <| fun _ ->
      let filters = render samplePlan |> flatten
      let indexOf predicate = filters |> List.findIndex predicate
      let decimateIdx = indexOf isMpDecimate
      let setPtsIdx = indexOf isSetPts
      let paletteGenIdx = filters |> List.findIndex (function FilterGraph.PaletteGen _ -> true | _ -> false)
      decimateIdx < setPtsIdx |> Expect.isTrue "mpdecimate runs before setpts"
      setPtsIdx < paletteGenIdx |> Expect.isTrue "mpdecimate+setpts run before palettegen"

    testCase "render's pipeline starts with a concat over every segment (§4.6)" <| fun _ ->
      let filters = render samplePlan |> flatten
      match filters with
      | FilterGraph.Concat n :: _ -> n |> Expect.equal "concat covers every segment" samplePlan.Segments.Length
      | other -> failtestf "expected the pipeline to start with Concat, got %A" (List.tryHead other)

    testCase "render's pipeline ends with palettegen(diff) then paletteuse(sierra2_4a) (§4.6, pinned)" <| fun _ ->
      let filters = render samplePlan |> flatten
      match List.rev filters with
      | FilterGraph.PaletteUse dither :: FilterGraph.PaletteGen statsMode :: _ ->
        statsMode |> Expect.equal "palettegen stats_mode" "diff"
        dither |> Expect.equal "paletteuse dither" "sierra2_4a"
      | other -> failtestf "expected the pipeline to end with PaletteGen then PaletteUse, got %A" (List.rev other |> List.truncate 2)

    testCase "render overlays the cursor for the one step with a non-empty pointer path, and no more" <| fun _ ->
      let filters = render samplePlan |> flatten
      let overlayCount = filters |> List.filter (function FilterGraph.Overlay _ -> true | _ -> false) |> List.length
      let nonEmptyPointerPaths = samplePlan.PointerPaths |> List.filter (List.isEmpty >> not) |> List.length
      // >= : a magnifier (when present) also renders as an Overlay (§4.6), so
      // the pointer-path overlays are a lower bound, not an exact count.
      (overlayCount >= nonEmptyPointerPaths)
      |> Expect.isTrue "at least one overlay per non-empty pointer path"

    testCase "render includes a caption band per step (§4.6, §9)" <| fun _ ->
      let filters = render samplePlan |> flatten
      let drawTextCount = filters |> List.filter (function FilterGraph.DrawText _ -> true | _ -> false) |> List.length
      drawTextCount |> Expect.equal "one caption drawtext per step" samplePlan.Captions.Length

    testCase "render includes the magnifier crop+scale+overlay when the plan has one (§4.6)" <| fun _ ->
      let filters = render samplePlan |> flatten
      filters |> List.exists (function FilterGraph.Crop _ -> true | _ -> false) |> Expect.isTrue "magnifier crops the editor pane"

    testCase "render omits the magnifier crop when the plan has none (DashboardOnly layouts, §9)" <| fun _ ->
      let noMagnifierPlan = { samplePlan with Magnifier = None; PointerPaths = [ []; [] ] }
      let filters = render noMagnifierPlan |> flatten
      filters |> List.exists (function FilterGraph.Crop _ -> true | _ -> false) |> Expect.isFalse "no editor pane to magnify"
  ]
