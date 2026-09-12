/// Pure ffmpeg planning (demo-gif-plan.md §4.6, §5). `render` turns a
/// `ComposePlan` into one `FilterGraph` value (concat → cursor/ripple
/// overlay → caption band → magnifier → mpdecimate+setpts → palettegen/use);
/// `toCommandString` is the ONE render-to-string function — no hand-built
/// filter strings anywhere else in the tool.
module SageFs.Demos.Ffmpeg

open SageFs.Demos.Domain

/// Builds the filtergraph for `plan` (§4.6's pipeline, in order).
let render (plan: ComposePlan) : FilterGraph =
  failwith "TODO: Ffmpeg — Wave 2"

/// The one place a `FilterGraph` becomes the string ffmpeg's `-filter_complex`
/// actually receives.
let toCommandString (graph: FilterGraph) : string =
  failwith "TODO: Ffmpeg — Wave 2"
