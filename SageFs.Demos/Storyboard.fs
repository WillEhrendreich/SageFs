/// Pure storyboard rendering (demo-gif-plan.md §4.8, §5): draws the layout
/// rects, actor placement, cursor path (over the *expected* target rects)
/// and captions for a scenario, with no launch and no sandbox — the review
/// step before a scenario costs a real recording.
module SageFs.Demos.Storyboard

open SageFs.Demos.Domain

/// Renders `scenario`'s storyboard strip at the given `screen` size and
/// `style`.
let svg (scenario: Scenario) (screen: Screen) (style: Style) : Svg =
  failwith "TODO: Storyboard — Wave 2"
