namespace SageFs.Features

open System.IO
open SageFs
open SageFs.WorkerProtocol
open SageFs.Features.TrunkFollow

/// Which sessions are the trunk's, and what each is doing as far as a landing is concerned.
///
/// A TRUNK SESSION is a session whose working directory is the trunk checkout. Nothing is registered: the working directory is
/// the whole definition, so a session started in the trunk checkout after landings have begun is a trunk session the moment it
/// exists, and one stopped is not.
module TrunkSessions =

  /// A session's state when a landing arrives. A session that is starting, building or restarting, or whose app is coming up
  /// or being restarted, is not read as "no running app": it is waited on, then read again.
  [<RequireQualifiedAccess>]
  type Reading =
    | Settled of TrunkSession
    | Settling of session: string * why: string

  let private normalize (path: string) : string =
    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

  /// The sessions that work in the trunk checkout.
  let sessionsIn (trunkPath: string) (sessions: SessionInfo list) : SessionInfo list =
    let trunk = normalize trunkPath
    sessions |> List.filter (fun info -> System.String.Equals(normalize info.WorkingDirectory, trunk, System.StringComparison.Ordinal))

  let private ofApp (session: string) (app: AppRun.AppRunState) : Reading =
    match app with
    | AppRun.AppRunState.Running _ -> Reading.Settled { Session = session; State = TrunkSessionState.Serving }
    | AppRun.AppRunState.Starting _ -> Reading.Settling (session, "its app is starting")
    | AppRun.AppRunState.RestartRequired _ -> Reading.Settling (session, "its app is being restarted")
    | AppRun.AppRunState.NotRunning
    | AppRun.AppRunState.Exited _
    | AppRun.AppRunState.Crashed _
    | AppRun.AppRunState.CouldNotStart _
    | AppRun.AppRunState.BuildFailed _
    // What the app is doing is not known, so it is not claimed to be serving.
    | AppRun.AppRunState.LostTrack _ -> Reading.Settled { Session = session; State = TrunkSessionState.NoRunningApp }

  /// Read one session.
  let read (info: SessionInfo) : Reading =
    let session = SessionId.value info.Id
    let unavailable (reason: string) = Reading.Settled { Session = session; State = TrunkSessionState.Unavailable reason }
    match info.Status with
    | SessionLifecycleStatus.Ready _
    | SessionLifecycleStatus.Evaluating _ -> ofApp session info.App
    | SessionLifecycleStatus.Starting _ -> Reading.Settling (session, "the session is starting")
    | SessionLifecycleStatus.Building (reason, _) -> Reading.Settling (session, sprintf "the session is building: %s" reason)
    | SessionLifecycleStatus.Restarting _ -> Reading.Settling (session, "the session is restarting")
    | SessionLifecycleStatus.Faulted reason -> unavailable (sprintf "the session faulted: %s" (FaultReason.describe reason))
    | SessionLifecycleStatus.HostCrashed _ -> unavailable "the session's FSI host crashed; reset it to bring it back"
    | SessionLifecycleStatus.Stopped -> unavailable "the session is stopped"
