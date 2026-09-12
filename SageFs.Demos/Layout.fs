/// Pure per-actor rect layout for a `LayoutTemplate` on a given `Screen`
/// (demo-gif-plan.md §5, §9). Wave 2 property tests: rects are multiples of
/// 8, tile without overlap, respect the 8 px gutter / 1 px separator rule.
module SageFs.Demos.Layout

open SageFs.Demos.Domain

/// The placed rect for every actor in a scenario using `template`, on a
/// screen of the given size.
let rects (template: LayoutTemplate) (screen: Screen) : Map<ActorId, Rect> =
  failwith "TODO: Layout — Wave 2"
