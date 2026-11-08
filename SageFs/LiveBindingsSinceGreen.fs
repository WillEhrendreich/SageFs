/// The comparison point of the "Since green" tab, kept for each session: the values at the last settle with no failures
/// (docs/plans/live-bindings-watch-view.md, step 11). The decisions are `SinceGreen` (pure, in Core). This remembers the point between
/// pushes and says what the tab shows. Every push observes the loop, so the point follows it without a timer.
module SageFs.Server.LiveBindingsSinceGreen

open SageFs
open SageFs.Features
open SageFs.Features.Tweak

/// How many sessions' comparison points are kept at once. A point is a convenience for a session on screen, so the oldest goes first.
let [<Literal>] SessionCapacity = 64

let private points = PageChoices<SinceGreen.Green>(SessionCapacity)

/// Take this push's reading of the loop and the walk: the point moves if the loop has just settled green, and what the tab shows is
/// what moved since the point.
let observe (sessionId: string) (activity: LiveTestActivity.LiveTestActivity) (walk: LiveValueTree.LiveValueSnapshot) : LiveBindingsViewPanel.GreenSaid =
  let current = SinceGreen.valuesOf walk
  match points.Update(sessionId, SinceGreen.Green.NoGreenYet, fun green -> SinceGreen.update green activity current) with
  | SinceGreen.Green.NoGreenYet -> LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet
  | SinceGreen.Green.GreenAt _ as green -> LiveBindingsViewPanel.GreenSaid.Moved(SinceGreen.moves green current)
