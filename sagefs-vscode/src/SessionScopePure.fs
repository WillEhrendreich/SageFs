/// Pure decisions about which session a VS Code window talks to.
///
/// WHY — measured with the tour harness on a daemon that already had other agents' sessions:
///   * "SageFs: Create Session" made a session, but the window kept naming another one, because the
///     extension bound itself to the head of the daemon's whole session list; Enable Live Testing then
///     acted on that other session until the user picked their own by id;
///   * a window with no session of its own silently took a stranger's session as its own.
/// The binding is now decided here, from the sessions the daemon lists, the folders this window has
/// open, and the one session the user (or Create Session) picked. A session is never chosen for a
/// window only because it comes first.
///
/// No Fable dependency; tested under `dotnet fsi` (tests/SessionScopeContractTests.fsx).
module SageFs.Vscode.SessionScopePure

/// The two facts about a session this module needs.
type SessionRef = {
  Id: string
  WorkingDirectory: string
}

/// Whether a session's working directory is inside one of this window's workspace folders.
[<RequireQualifiedAccess>]
type WorkspaceRelation =
  | InThisWorkspace
  | ElsewhereOnThisMachine

let relationOf (workspaceRoots: string list) (workingDirectory: string) : WorkspaceRelation =
  match System.String.IsNullOrWhiteSpace workingDirectory with
  | true -> WorkspaceRelation.ElsewhereOnThisMachine
  | false ->
    match workspaceRoots |> List.exists (fun root -> BufferBridge.isWithinDirectory root workingDirectory) with
    | true -> WorkspaceRelation.InThisWorkspace
    | false -> WorkspaceRelation.ElsewhereOnThisMachine

/// The session the user, or Create Session, picked for this window.
[<RequireQualifiedAccess>]
type Selection =
  | Selected of sessionId: string
  | NotSelected

/// The session this window talks to.
[<RequireQualifiedAccess>]
type Binding =
  | Bound of SessionRef
  /// Nothing the user picked, and none of the daemon's sessions belongs to this workspace. A stranger's
  /// session is never taken instead.
  | NoSessionForThisWorkspace

/// A session the user picked stays bound while it exists, wherever it lives. Otherwise the first
/// listed session that belongs to this workspace. Otherwise none.
let bind (workspaceRoots: string list) (selection: Selection) (sessions: SessionRef list) : Binding =
  let picked =
    match selection with
    | Selection.Selected id -> sessions |> List.tryFind (fun s -> s.Id = id)
    | Selection.NotSelected -> None
  match picked with
  | Some s -> Binding.Bound s
  | None ->
    match sessions |> List.tryFind (fun s -> relationOf workspaceRoots s.WorkingDirectory = WorkspaceRelation.InThisWorkspace) with
    | Some s -> Binding.Bound s
    | None -> Binding.NoSessionForThisWorkspace

/// What a session's own lifecycle status means for a window showing it.
[<RequireQualifiedAccess>]
type SessionPhase =
  /// It can evaluate.
  | Usable
  /// Still coming up, or being restarted.
  | Warming
  /// It fell over or was stopped.
  | Down
  /// A status this client does not know. Named, never judged.
  | Unrecognised of status: string

let phaseOfStatus (status: string) : SessionPhase =
  match status with
  | "Ready" | "Evaluating" | "Building" -> SessionPhase.Usable
  | "Starting" | "Restarting" | "Warming Up" -> SessionPhase.Warming
  | "Faulted" | "Stopped" | "error" -> SessionPhase.Down
  | other -> SessionPhase.Unrecognised other

/// The session a Create Session call just made.
[<RequireQualifiedAccess>]
type CreatedSession =
  | CreatedAs of SessionRef
  /// Nothing new, or more than one candidate: none is guessed.
  | NotIdentified

/// `before` is the ids the daemon listed before the call, `reply` is the daemon's answer to it (the new
/// id, when it says so), `after` is the list once it returned. A reply that names a session already in
/// `before` is not the new one. Otherwise the one new session in the working directory the call asked
/// for; another agent's session that appeared at the same moment is in a different directory.
let identifyCreated (before: string list) (reply: string) (workingDirectory: string) (after: SessionRef list) : CreatedSession =
  let isNew (s: SessionRef) = not (List.contains s.Id before)
  let newOnes = after |> List.filter isNew
  match newOnes |> List.tryFind (fun s -> s.Id = reply.Trim()) with
  | Some named -> CreatedSession.CreatedAs named
  | None ->
    match newOnes |> List.filter (fun s -> relationOf [ workingDirectory ] s.WorkingDirectory = WorkspaceRelation.InThisWorkspace) with
    | [ only ] -> CreatedSession.CreatedAs only
    | _ -> CreatedSession.NotIdentified
