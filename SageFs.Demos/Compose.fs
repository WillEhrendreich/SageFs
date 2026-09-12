/// Pure composition planning (demo-gif-plan.md §4.6, §5): turns a returned
/// `StepLog` plus the resolved layout and style into the `ComposePlan`
/// `Ffmpeg.render` consumes.
module SageFs.Demos.Compose

open SageFs.Demos.Domain

/// Builds the compose plan for one scenario's recording.
let plan (log: StepLog) (layout: Map<ActorId, Rect>) (style: Style) : ComposePlan =
  failwith "TODO: Compose — Wave 2"
