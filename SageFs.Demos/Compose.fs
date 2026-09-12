/// Pure composition planning (demo-gif-plan.md §4.6, §5): turns a returned
/// `StepLog` plus the resolved layout and style into the `ComposePlan`
/// `Ffmpeg.render` consumes.
module SageFs.Demos.Compose

open SageFs.Demos.Domain

/// The editor pane to magnify (§4.6's magnifier: "a 2× picture-in-picture of
/// the edited line"), if the layout places one. A `LayoutTemplate.DashboardOnly`
/// scenario (§9: sessions/agent demos) has no editor pane, so `Magnifier` is
/// `None` for it — never a made-up rect. Which *step* is a `Type` action (so
/// the magnifier should only show for that step, not the whole video) needs
/// `StepLog` to carry the originating `Action`, which it does not yet
/// (TODO(shape): Wave 3).
let private editorRect (layout: Map<ActorId, Rect>) : Rect option =
  layout
  |> Map.tryFind ActorId.VsCode
  |> Option.orElseWith (fun () -> Map.tryFind ActorId.Neovim layout)

/// Builds the compose plan for one scenario's recording (§4.6): one caption,
/// one segment and one pointer path per returned step, in the `StepLog`'s own
/// step order, plus the editor rect for the magnifier when the layout has one.
/// "Trim idle" (§4.6, §9) needs no separate step here — §4.5's capture policy
/// already excludes idle time from each segment, so there is nothing left to
/// trim by the time a `StepLog` reaches this planner.
let plan (log: StepLog) (layout: Map<ActorId, Rect>) (style: Style) : ComposePlan =
  { Segments = log.Steps |> List.map (fun step -> step.Segment)
    Layout = layout
    Style = style
    Captions = log.Steps |> List.map (fun step -> step.Caption)
    PointerPaths = log.Steps |> List.map (fun step -> step.PointerPath)
    Timings =
      log.Steps
      |> List.map (fun step -> { StartedMs = step.StartedMs; EndedMs = step.EndedMs; ObservedAtMs = step.ObservedAtMs })
    Magnifier = editorRect layout }
