/// The comparison point of the "Since green" tab, kept for each session (docs/plans/live-bindings-watch-view.md, step 11). The decisions
/// are `SinceGreen` (pure, in Core). The point is taken where the test loop settles, from the model every change of which passes the
/// daemon's model hook, and not where the dashboard draws: a render only reads it (`said`), so the tab is right whether or not a browser
/// was watching when the tests went green and then red. This is the daemon's record of a session, so it is not page state
/// (`PageState`), and a session that is gone takes its point with it (`forget`).
module SageFs.Server.LiveBindingsSinceGreen

open System
open System.Collections.Concurrent
open SageFs
open SageFs.Features
open SageFs.Features.LiveTesting
open SageFs.Features.Tweak

/// The daemon's comparison points, by session. One value owned by the daemon and written only by its model hook.
type Recorder = ConcurrentDictionary<string, SinceGreen.Green>

let create () : Recorder = Recorder()

/// The point a session has now, or none yet.
let pointOf (recorder: Recorder) (sessionId: string) : SinceGreen.Green =
  match recorder.TryGetValue sessionId with
  | true, green -> green
  | false, _ -> SinceGreen.Green.NoGreenYet

/// The loop settled run `generation` of a session into `activity` while its walk held `walk`. A green settle takes the point once for the
/// run; anything else leaves it.
let observe (recorder: Recorder) (sessionId: string) (generation: int64) (activity: LiveTestActivity.LiveTestActivity) (walk: LiveValueTree.LiveValueSnapshot) : unit =
  let event = SinceGreen.settleEvent generation activity
  recorder.AddOrUpdate(sessionId, (fun _ -> SinceGreen.settle SinceGreen.Green.NoGreenYet event walk), (fun _ green -> SinceGreen.settle green event walk))
  |> ignore

/// A session is gone: its point goes with it.
let forget (recorder: Recorder) (sessionId: string) : unit = recorder.TryRemove sessionId |> ignore

/// The walk of a session that has none yet: nothing bound, so whatever is bound later is new since the settle.
let private nothingBound (sessionId: string) : LiveValueTree.LiveValueSnapshot =
  { SessionId = sessionId
    Generation = 0L
    Bindings = []
    Truncated = false
    CapturedAt = DateTimeOffset.UnixEpoch }

/// Every change of the daemon's model, for every session whose live-testing cycle it holds. A session whose last run already has its point
/// is passed over at once (one read of the run's generation); otherwise the loop's activity for it is read, and a green settle takes the
/// point from the walk the daemon holds for the session.
let observeModel (recorder: Recorder) (walkOf: string -> LiveValueTree.LiveValueSnapshot option) (model: SageFsModel) : unit =
  let active =
    match ActiveSession.sessionId model.Sessions.ActiveSessionId with
    | Some sid -> [ WorkerProtocol.SessionId.value sid ]
    | None -> []
  let sessions = active @ (model.PerSessionLiveTesting |> Map.toList |> List.map fst) |> List.distinct
  for sessionId in sessions do
    let generation = int64 (RunGeneration.value (SageFsModel.cycleForSession sessionId model).TestState.LastGeneration)
    match pointOf recorder sessionId with
    // no run has completed in this session, so there is no settle to take a point at
    | _ when generation = 0L -> ()
    | SinceGreen.Green.GreenAt(taken, _) when taken = generation -> ()
    | SinceGreen.Green.GreenAt _
    | SinceGreen.Green.NoGreenYet ->
      let walk = walkOf sessionId |> Option.defaultValue (nothingBound sessionId)
      observe recorder sessionId generation (SageFsModel.liveTestActivityFor sessionId model) walk

/// What the tab shows for a point and the walk now. Only reads.
let said (green: SinceGreen.Green) (walk: LiveValueTree.LiveValueSnapshot) : LiveBindingsViewPanel.GreenSaid =
  match green with
  | SinceGreen.Green.NoGreenYet -> LiveBindingsViewPanel.GreenSaid.NoGreenSettleYet
  | SinceGreen.Green.GreenAt _ -> LiveBindingsViewPanel.GreenSaid.Moved(SinceGreen.moves green walk)
