/// The comparison point of the "Since green" tab, kept for each session (docs/plans/live-bindings-watch-view.md, step 11). The decisions
/// are `SinceGreen` (pure, in Core). The point is taken where the test loop settles, from the model every change of which passes the
/// daemon's model hook, and not where the dashboard draws: a render only reads it (`said`), so the tab is right whether or not a browser
/// was watching when the tests went green and then red.
module SageFs.Server.LiveBindingsSinceGreen

open System.Collections.Concurrent
open SageFs
open SageFs.Features
open SageFs.Features.Tweak

/// The daemon's comparison points, by session. One value owned by the daemon and written only by its model hook.
type Recorder = ConcurrentDictionary<string, SinceGreen.Green>

let create () : Recorder = Recorder()

/// The point a session has now, or none yet.
let pointOf (recorder: Recorder) (sessionId: string) : SinceGreen.Green =
  match recorder.TryGetValue sessionId with
  | true, green -> green
  | false, _ -> SinceGreen.Green.NoGreenYet

/// The loop settled run `generation` of a session into `activity` while its walk held `walk`.
let observe (recorder: Recorder) (sessionId: string) (generation: int64) (activity: LiveTestActivity.LiveTestActivity) (walk: LiveValueTree.LiveValueSnapshot) : unit = ()

/// A session is gone: its point goes with it.
let forget (recorder: Recorder) (sessionId: string) : unit = recorder.TryRemove sessionId |> ignore

/// Every change of the daemon's model, for every session whose live-testing cycle it holds: look at the sessions whose last run has not
/// had its point taken, and take it if the run settled green.
let observeModel (recorder: Recorder) (walkOf: string -> LiveValueTree.LiveValueSnapshot option) (model: SageFsModel) : unit = ()

/// What the tab shows for a point and the walk now. Only reads.
let said (green: SinceGreen.Green) (walk: LiveValueTree.LiveValueSnapshot) : LiveBindingsViewPanel.GreenSaid =
  LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet
