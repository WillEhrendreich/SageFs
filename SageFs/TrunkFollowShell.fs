/// The trunk's performer: what the daemon does when `TrunkFollow` asks it to move the trunk checkout and to tell a session about the
/// files a landing changed.
///
/// Everything it touches is passed in, so a test drives it with a world of its own (sessions that are ready or restarting, a worker
/// that answers or does not, a checkout that moves or refuses) and the daemon passes the real ones.
module SageFs.TrunkFollowShell

open System
open SageFs.Features
open SageFs.Features.TrunkFollow

/// What the performer needs from the daemon.
type TrunkShellDeps =
  { /// The trunk checkout's path, or None while the cohort has no integration configured.
    TrunkPath: unit -> string option
    /// Every session the daemon has right now.
    Sessions: unit -> WorkerProtocol.SessionInfo list
    /// Wait until the condition holds or the settle budget is spent, waking on the daemon's own change events rather than on a
    /// clock. Answers whether it held.
    AwaitSettled: (unit -> bool) -> Async<bool>
    /// Send a message to a session's worker.
    Ask: string -> WorkerProtocol.WorkerMessage -> Async<Result<WorkerProtocol.WorkerResponse, SageFsError>>
    /// The trunk checkout's HEAD.
    CurrentHead: string -> Async<Result<string, string>>
    /// What changed under a checkout between two commits, as absolute paths.
    Diff: string -> string -> string -> Async<Result<SavedFile list, string>>
    /// Move the checkout to a commit.
    MoveTo: string -> string -> Async<Result<unit, string>>
    /// Build the projects the trunk sessions that run an app have loaded, in the trunk checkout.
    Build: string -> Async<Result<unit, string>>
    /// A fresh reply id.
    NewReplyId: unit -> string }

let private anySettling (readings: TrunkSessions.Reading list) : bool =
  readings
  |> List.exists (function
    | TrunkSessions.Reading.Settling _ -> true
    | TrunkSessions.Reading.Settled _ -> false)

let private readSessions (deps: TrunkShellDeps) (trunkPath: string) : TrunkSessions.Reading list =
  deps.Sessions () |> TrunkSessions.sessionsIn trunkPath |> List.map TrunkSessions.read

/// The trunk sessions, once none is starting, building or restarting. One that has not settled when the budget is spent is
/// reported as unavailable with why, never read as a session with no app.
let private settledSessions (deps: TrunkShellDeps) (trunkPath: string) : Async<TrunkSession list> =
  async {
    let! _held = deps.AwaitSettled (fun () -> not (anySettling (readSessions deps trunkPath)))
    return
      readSessions deps trunkPath
      |> List.map (function
        | TrunkSessions.Reading.Settled session -> session
        | TrunkSessions.Reading.Settling (session, why) ->
          { Session = session; State = TrunkSessionState.Unavailable (sprintf "it had not settled when the landing arrived: %s" why) })
  }

/// A session about to have its files moved takes its saves from landings only, so its own watcher does not run the same save the
/// landing is about to hand it. A worker that cannot be told is not read as serving: its watcher might still run.
let private takeSavesFromLandings (deps: TrunkShellDeps) (session: TrunkSession) : Async<TrunkSession> =
  async {
    match session.State with
    | TrunkSessionState.NoRunningApp
    | TrunkSessionState.Unavailable _ -> return session
    | TrunkSessionState.Serving ->
      let! answer = deps.Ask session.Session (WorkerProtocol.WorkerMessage.SetSaveSource (SaveSource.LandedOnly, deps.NewReplyId ()))
      match answer with
      | Result.Ok (WorkerProtocol.WorkerResponse.SaveSourceSet (_, SaveSource.LandedOnly)) -> return session
      | Result.Ok other ->
        return { session with State = TrunkSessionState.Unavailable (sprintf "the worker did not take saves from landings only: %A" other) }
      | Result.Error err ->
        return { session with State = TrunkSessionState.Unavailable (sprintf "the worker could not be told to take saves from landings only: %s" (SageFsError.describe err)) }
  }

let private move (deps: TrunkShellDeps) (landing: LandedLanding) : Async<TrunkMove> =
  async {
    match deps.TrunkPath () with
    | None -> return TrunkMove.NotMoved "no trunk checkout is configured (call set_integration_ref)"
    | Some trunkPath ->
      let! settled = settledSessions deps trunkPath
      let! sessions = settled |> List.map (takeSavesFromLandings deps) |> Async.Sequential
      let! head = deps.CurrentHead trunkPath
      match head with
      | Result.Error reason -> return TrunkMove.NotMoved (sprintf "could not read the trunk checkout's head: %s" reason)
      | Result.Ok trunkHead ->
        let! diff = deps.Diff trunkPath trunkHead landing.Commit
        match diff with
        | Result.Error reason -> return TrunkMove.NotMoved (sprintf "could not tell what the landing changed: %s" reason)
        | Result.Ok files ->
          let! moved = deps.MoveTo trunkPath landing.Commit
          match moved with
          | Result.Error reason -> return TrunkMove.NotMoved (sprintf "could not move the trunk checkout to %s: %s" landing.Commit reason)
          | Result.Ok () ->
            // The patch the save pipeline applies lives in the running process only. What the next start of the app runs, and what
            // the daemon's own respawn runs when the pipeline asks for a restart, is the compiled output on disk, so a session
            // that runs an app gets a build of the checkout it now holds before it is told. A build that fails leaves the app as
            // it was, and says so, rather than telling it about files its next start could not run.
            let serving = sessions |> Array.exists (fun s -> s.State = TrunkSessionState.Serving)
            let! built =
              match serving && List.exists affectsBuild files with
              | true -> deps.Build trunkPath
              | false -> async { return Result.Ok () }
            match built with
            | Result.Ok () -> return TrunkMove.Moved (files, Array.toList sessions)
            | Result.Error reason ->
              let unbuilt (s: TrunkSession) =
                match s.State with
                | TrunkSessionState.Serving ->
                  { s with State = TrunkSessionState.Unavailable (sprintf "the trunk checkout holds the landing but it did not build, so the running app was not told: %s" reason) }
                | TrunkSessionState.NoRunningApp
                | TrunkSessionState.Unavailable _ -> s
              return TrunkMove.Moved (files, sessions |> Array.map unbuilt |> Array.toList)
  }

let private deliver (deps: TrunkShellDeps) (session: string) (files: SavedFile list) : Async<SessionOutcome> =
  async {
    let! answer = deps.Ask session (WorkerProtocol.WorkerMessage.ApplySaves (files, deps.NewReplyId ()))
    match answer with
    | Result.Ok (WorkerProtocol.WorkerResponse.SavesApplied (_, outcome)) -> return outcome
    | Result.Ok other -> return SessionOutcome.Unreachable (sprintf "the worker answered %A to the saves" other)
    | Result.Error err -> return SessionOutcome.Unreachable (SageFsError.describe err)
  }

/// The performer the trunk owner runs.
let performer (deps: TrunkShellDeps) : TrunkFollowOwner.TrunkPerformer =
  { Move = move deps
    Deliver = fun _ session files -> deliver deps session files }
