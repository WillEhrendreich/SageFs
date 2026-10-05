/// The guided new-session dialog, as data.
///
/// A [+] on the Sessions list opens a dialog that finds the projects under a directory, says in one line what
/// each workflow means, warns before a second session lands in the same checkout, and shows a refusal from the
/// daemon in place with what to do next. This file is the whole model of that dialog and nothing else: no
/// markup, no HTTP, no daemon call. `NewSessionDiscovery` reads the disk, `NewSessionDialogView` draws a state,
/// `DashboardNewSession` wires it to the page. Each of those takes a value from here and gives one back.
///
/// The dialog is ONE closed set of states and one total `step`. A state that cannot be shown cannot be built:
/// `Warning` carries its first overlap apart from the rest so it can never be empty, `Creating` carries the
/// request that is in flight, `Refused` carries a named reason. What the person has typed or ticked is not
/// here: that is browser signals, sent with each click. Only what the server found out lives in a state.
module SageFs.Server.NewSessionDialog

open System
open System.IO
open SageFs
open SageFs.WorkflowTypes
open SageFs.Server.DashboardTypes

// ── What discovery found ─────────────────────────────────────────────────

/// The frameworks a project builds for, as far as its own project file says.
[<RequireQualifiedAccess>]
type Frameworks =
  | Declared of string list
  /// The project file names none: a Directory.Build.props or the SDK decides, and the daemon asks MSBuild at create time.
  | NamedByImports
  /// A solution has no framework of its own.
  | WholeSolution

module Frameworks =
  let describe = function
    | Frameworks.Declared tfms -> String.Join(", ", tfms)
    | Frameworks.NamedByImports -> "set by an imported props file"
    | Frameworks.WholeSolution -> "whole solution"

[<RequireQualifiedAccess>]
type CandidateKind =
  | Solution
  | Project

/// One thing the dialog offers to load. `Path` is relative to the directory searched, exactly as the daemon
/// resolves it.
type Candidate = { Path: string; Kind: CandidateKind; Frameworks: Frameworks }

/// What the dialog says about the workflow, before anything is chosen. A suggestion never selects: the
/// person's choice is the signal on the page, and the REPL is its default.
[<RequireQualifiedAccess>]
type WorkflowHint =
  | NoneSuggested
  | Suggested of WorkflowSuggestion

// ── Which sessions are already here ──────────────────────────────────────

/// The checkout a directory sits in, read from the filesystem (`SageFs.Checkout`, no `git` process). A git
/// worktree is its own boundary: its root and branch are not the main checkout's.
[<RequireQualifiedAccess>]
type Boundary =
  | Repository of root: string
  | Worktree of root: string * branch: string
  | Plain of directory: string

/// A session that is not stopped, as far as the overlap rule cares.
type LiveSession = { Id: string; WorkingDirectory: string; Boundary: Boundary }

[<RequireQualifiedAccess>]
type Relation =
  | SameDirectory
  | SameRepository of root: string

type Overlap = { Session: LiveSession; Relation: Relation }

/// What discovery found in the directory the person is looking at. Overlaps are not in here: they decide which
/// STATE the dialog is in (`Choosing` or `Warning`), so the type that holds the list cannot be empty.
type Found = { Directory: string; Candidates: Candidate list; Hint: WorkflowHint }

module Found =
  /// A directory that offers nothing: what a refusal shows when there was no discovery to keep.
  let nothing (directory: string) : Found =
    { Directory = directory; Candidates = []; Hint = WorkflowHint.NoneSuggested }

// ── What the person asked for ────────────────────────────────────────────

/// Load projects, or none at all.
[<RequireQualifiedAccess>]
type TargetKind =
  | LoadProjects
  | BareSession

module TargetKind =
  let all = [ TargetKind.LoadProjects; TargetKind.BareSession ]

  /// The value the page's radio carries and the signal holds.
  let key = function
    | TargetKind.LoadProjects -> "projects"
    | TargetKind.BareSession -> "bare"

  let tryOfKey (raw: string) : TargetKind option =
    all |> List.tryFind (fun kind -> String.Equals(key kind, raw, StringComparison.Ordinal))

/// What a session loads. `Load` names its first project apart from the rest, so "load projects" with nothing
/// to load cannot be built.
[<RequireQualifiedAccess>]
type Target =
  | Bare
  | Load of first: string * rest: string list

type Request = { Directory: string; Target: Target; Workflow: SessionWorkflow }

// ── Why a create did not happen ──────────────────────────────────────────

/// A refusal, by name. The daemon's own refusals keep the typed error, so the words come from the one table
/// every surface already reads (`SageFsError.describe` and `suggestedAction`) and the dialog never rewords them.
[<RequireQualifiedAccess>]
type Refusal =
  | NoDirectory
  | DirectoryMissing of path: string
  | NothingPicked
  | Daemon of SageFsError

/// How the daemon's refusals are grouped for a title. A new `SageFsError` case cannot compile without being
/// placed here.
[<RequireQualifiedAccess>]
type DaemonRefusal =
  | NotBuilt
  | AlreadyHere
  | CannotHost
  | NoSdk
  | Busy
  | UnsafePath
  | Other

module DaemonRefusal =
  let ofError (error: SageFsError) : DaemonRefusal =
    match error with
    | SageFsError.NeedsRebuild _ -> DaemonRefusal.NotBuilt
    | SageFsError.DuplicateSession _ -> DaemonRefusal.AlreadyHere
    | SageFsError.ProjectFrameworkNotHostable _ -> DaemonRefusal.CannotHost
    | SageFsError.WorkerSpawnFailed _ -> DaemonRefusal.NoSdk
    | SageFsError.SupervisorBusy _
    | SageFsError.MemoryPressureRefused _ -> DaemonRefusal.Busy
    | SageFsError.UnsafeSessionPath _ -> DaemonRefusal.UnsafePath
    | SageFsError.ToolNotAvailable _
    | SageFsError.SessionNotFound _
    | SageFsError.NoActiveSessions
    | SageFsError.AmbiguousSessions _
    | SageFsError.SessionCreationFailed _
    | SageFsError.SessionStopFailed _
    | SageFsError.SessionSwitchFailed _
    | SageFsError.SessionNotRoutable _
    | SageFsError.WorkerCommunicationFailed _
    | SageFsError.WorkerTimeout _
    | SageFsError.WorkerHttpError _
    | SageFsError.PipeClosed
    | SageFsError.EvalFailed _
    | SageFsError.ResetFailed _
    | SageFsError.HardResetFailed _
    | SageFsError.BuildFailed _
    | SageFsError.ScriptLoadFailed _
    | SageFsError.CheckFailed _
    | SageFsError.CompletionFailed _
    | SageFsError.CancelFailed _
    | SageFsError.EvalSupersededByReset
    | SageFsError.FsiHostCrashed _
    | SageFsError.WarmupOpenFailed _
    | SageFsError.WarmupContextFailed _
    | SageFsError.HotReloadFailed _
    | SageFsError.HotReloadStateError _
    | SageFsError.AppRunFailed _
    | SageFsError.AppStopFailed _
    | SageFsError.RestartLimitExceeded _
    | SageFsError.DaemonStartFailed _
    | SageFsError.DaemonNotRunning
    | SageFsError.PortInUse _
    | SageFsError.SseConnectionError _
    | SageFsError.JsonParseError _
    | SageFsError.CohortActionFailed _
    | SageFsError.Unexpected _ -> DaemonRefusal.Other

  let title = function
    | DaemonRefusal.NotBuilt -> "The project is not built yet"
    | DaemonRefusal.AlreadyHere -> "A session like this already exists"
    | DaemonRefusal.CannotHost -> "SageFs cannot host this project"
    | DaemonRefusal.NoSdk -> "The .NET SDK could not start a worker"
    | DaemonRefusal.Busy -> "The daemon is busy"
    | DaemonRefusal.UnsafePath -> "That path is not allowed"
    | DaemonRefusal.Other -> "The daemon refused to start the session"

module Refusal =
  let title = function
    | Refusal.NoDirectory -> "Choose a directory first"
    | Refusal.DirectoryMissing _ -> "That directory does not exist"
    | Refusal.NothingPicked -> "Nothing is ticked to load"
    | Refusal.Daemon error -> DaemonRefusal.title (DaemonRefusal.ofError error)

  let detail = function
    | Refusal.NoDirectory -> "A session lives in a working directory, and the box is empty."
    | Refusal.DirectoryMissing path -> sprintf "There is no directory at %s." path
    | Refusal.NothingPicked -> "Loading projects needs at least one ticked, and none is."
    // The daemon's sentence names an MCP tool the person at this page has no use for; the facts are the same.
    | Refusal.Daemon (SageFsError.DuplicateSession(existingId, directory)) ->
      sprintf "A session for this project already exists: session %s, working in %s." existingId directory
    | Refusal.Daemon error -> SageFsError.describe error

  let nextAction = function
    | Refusal.NoDirectory -> "Type or pick a directory, then Create."
    | Refusal.DirectoryMissing _ -> "Check the path for typos, or create the directory first."
    | Refusal.NothingPicked -> "Tick a project, or choose Bare to start without one."
    | Refusal.Daemon (SageFsError.DuplicateSession _) ->
      "Use the session that is already there (switch to it from the list), or load different projects."
    | Refusal.Daemon error -> SageFsError.suggestedAction error

module Request =
  /// Turn what one click sent into a request, or the refusal that says what is missing. A bare session loads
  /// nothing, so whatever is still ticked from an earlier directory is ignored.
  let parse
    (directory: string)
    (kind: TargetKind)
    (picked: string list)
    (workflowKey: string)
    : Result<Request, Refusal> =
    match String.IsNullOrWhiteSpace directory with
    | true -> Error Refusal.NoDirectory
    | false ->
      let target =
        match kind, picked with
        | TargetKind.BareSession, _ -> Ok Target.Bare
        | TargetKind.LoadProjects, [] -> Error Refusal.NothingPicked
        | TargetKind.LoadProjects, first :: rest -> Ok (Target.Load(first, rest))
      target
      |> Result.map (fun target ->
        { Directory = directory.Trim()
          Target = target
          Workflow = SessionWorkflow.tryOfString workflowKey |> Option.defaultValue SessionWorkflow.defaultWorkflow })

/// What is ticked, and which target is chosen, when discovery finishes: a sensible start the person can change.
module DefaultChoice =
  /// A solution is ticked on its own (it already covers its projects). A lone project is ticked. Several
  /// projects and no solution tick nothing: that is the person's choice, and Create waits for it. With
  /// nothing found the only thing that can be created is a bare session.
  let ofFound (found: Found) : TargetKind * string list =
    match found.Candidates with
    | [] -> TargetKind.BareSession, []
    | candidates ->
      match candidates |> List.tryFind (fun c -> c.Kind = CandidateKind.Solution), candidates with
      | Some solution, _ -> TargetKind.LoadProjects, [ solution.Path ]
      | None, [ only ] -> TargetKind.LoadProjects, [ only.Path ]
      | None, _ -> TargetKind.LoadProjects, []

// ── The workflows, in plain words ────────────────────────────────────────

module WorkflowChoice =
  /// The three the dashboard's switcher already offers, in its order.
  let all : SessionWorkflow list = WorkflowSwitch.options

  let key = WorkflowSwitch.requestValue

  /// The REPL: nothing runs on its own and nothing is restricted.
  let defaultChoice : SessionWorkflow = SessionWorkflow.defaultWorkflow

  /// One line, in the words someone would use to a colleague.
  let oneLine = function
    | SessionWorkflow.Interactive -> "Type F# and see the answer straight away, with your project's code already loaded."
    | SessionWorkflow.LiveTesting -> "Like the REPL, and the tests your edit affects run again as you type."
    | SessionWorkflow.HotReload _ -> "Run your app and watch each save take effect without restarting it. The REPL is limited to expressions."

// ── Overlap: is there already a session here? ────────────────────────────

module Boundary =
  /// The boundary a `Checkout` classification means. Pure; the classification itself is the filesystem read.
  let ofCheckout (directory: string) (checkout: Checkout.Checkout) : Boundary =
    match checkout with
    | Checkout.Checkout.MainCheckout root -> Boundary.Repository root
    | Checkout.Checkout.Worktree(root, branch) -> Boundary.Worktree(root, branch)
    | Checkout.Checkout.NotAGitCheckout -> Boundary.Plain directory

  /// Read the checkout `directory` sits in. Reads the filesystem and runs no process.
  let classify (directory: string) : Boundary =
    ofCheckout directory (Checkout.classify directory)

  /// The root a checkout-bound boundary is rooted at; a directory outside any checkout has none to share.
  let root = function
    | Boundary.Repository root -> Some root
    | Boundary.Worktree(root, _) -> Some root
    | Boundary.Plain _ -> None

module Overlap =
  let comparison =
    match OperatingSystem.IsWindows() with
    | true -> StringComparison.OrdinalIgnoreCase
    | false -> StringComparison.Ordinal

  /// One spelling of a directory: absolute, without a trailing separator. Text that is not a path at all (an
  /// empty box) is its own spelling, so comparing it never throws.
  let canonical (directory: string) : string =
    match String.IsNullOrWhiteSpace directory with
    | true -> directory
    | false ->
      try Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
      with :? ArgumentException | :? NotSupportedException | :? PathTooLongException -> directory

  let sameDirectory (a: string) (b: string) =
    String.Equals(canonical a, canonical b, comparison)

  /// Which of `sessions` already work where a new one is about to be made. The same directory is named first,
  /// then the same checkout. A git worktree is its own boundary: a session in a worktree nested under the
  /// repository is not the repository's, even though its path starts with it.
  let decide (directory: string) (boundary: Boundary) (sessions: LiveSession list) : Overlap list =
    let here =
      sessions
      |> List.filter (fun s -> sameDirectory s.WorkingDirectory directory)
      |> List.map (fun s -> { Session = s; Relation = Relation.SameDirectory })
    let sharedRoot =
      match Boundary.root boundary with
      | Some root ->
        sessions
        |> List.filter (fun s -> not (sameDirectory s.WorkingDirectory directory))
        |> List.choose (fun s ->
          match Boundary.root s.Boundary with
          | Some other when String.Equals(canonical other, canonical root, comparison) ->
            Some { Session = s; Relation = Relation.SameRepository root }
          | Some _
          | None -> None)
      | None -> []
    here @ sharedRoot

// ── The dialog ───────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type NewSessionDialog =
  | Closed
  | Discovering of directory: string
  | Choosing of Found
  /// Something already works here. The first overlap is held apart so a warning cannot be empty.
  | Warning of Found * first: Overlap * rest: Overlap list
  | Creating of Request * Found
  | Refused of Refusal * Found

[<RequireQualifiedAccess>]
type Event =
  | Open of directory: string
  /// Discovery finished for `Found.Directory`, and these live sessions overlap it.
  | Found of Found * Overlap list
  /// Discovery could not run for this directory.
  | Missing of directory: string * Refusal
  | Submit of Request
  | Created
  /// The daemon refused the create that was in flight.
  | Failed of Refusal
  /// The click was refused before it reached the daemon: nothing typed, nothing ticked, a path that escapes.
  | Rejected of directory: string * Refusal
  | Dismiss

module NewSessionDialog =
  /// The name the page carries on the dialog (`data-state`), so a journey can tell which state it is looking at.
  let stateKey = function
    | NewSessionDialog.Closed -> "closed"
    | NewSessionDialog.Discovering _ -> "discovering"
    | NewSessionDialog.Choosing _ -> "choosing"
    | NewSessionDialog.Warning _ -> "warning"
    | NewSessionDialog.Creating _ -> "creating"
    | NewSessionDialog.Refused _ -> "refused"

  /// What the dialog last found, when it has anything. A state that never discovered has nothing to keep.
  let foundOf (state: NewSessionDialog) (directory: string) : Found =
    match state with
    | NewSessionDialog.Choosing found
    | NewSessionDialog.Warning(found, _, _)
    | NewSessionDialog.Creating(_, found)
    | NewSessionDialog.Refused(_, found) -> found
    | NewSessionDialog.Closed
    | NewSessionDialog.Discovering _ -> Found.nothing directory

  /// The one transition. Total: an event that does not apply in a state leaves it as it was, so a late
  /// discovery, a double click or a Dismiss that arrives mid-create can never put the dialog somewhere wrong.
  let step (state: NewSessionDialog) (event: Event) : NewSessionDialog =
    match state, event with
    // A create in flight is changed only by how it ends.
    | NewSessionDialog.Creating _, Event.Created -> NewSessionDialog.Closed
    | NewSessionDialog.Creating(_, found), Event.Failed reason -> NewSessionDialog.Refused(reason, found)
    | NewSessionDialog.Creating _, (Event.Open _ | Event.Found _ | Event.Missing _ | Event.Submit _ | Event.Rejected _ | Event.Dismiss) -> state
    | _, Event.Open directory -> NewSessionDialog.Discovering directory
    | NewSessionDialog.Discovering waiting, Event.Found(found, overlaps) when Overlap.canonical waiting = Overlap.canonical found.Directory ->
      match overlaps with
      | [] -> NewSessionDialog.Choosing found
      | first :: rest -> NewSessionDialog.Warning(found, first, rest)
    // Nothing typed yet is not a mistake: the dialog opened on an empty box and waits for a directory.
    | NewSessionDialog.Discovering waiting, Event.Missing(directory, Refusal.NoDirectory) when Overlap.canonical waiting = Overlap.canonical directory ->
      NewSessionDialog.Choosing (Found.nothing directory)
    | NewSessionDialog.Discovering waiting, Event.Missing(directory, reason) when Overlap.canonical waiting = Overlap.canonical directory ->
      NewSessionDialog.Refused(reason, Found.nothing directory)
    | _, Event.Rejected(directory, reason) -> NewSessionDialog.Refused(reason, foundOf state directory)
    | _, Event.Submit request -> NewSessionDialog.Creating(request, foundOf state request.Directory)
    | _, Event.Dismiss -> NewSessionDialog.Closed
    | _, (Event.Found _ | Event.Missing _ | Event.Created | Event.Failed _) -> state

/// The page's names for the dialog: DOM ids, test ids, signals and routes. Written once here so the markup, the
/// handlers and the journeys can never spell one differently.
module NewSessionNames =
  // DOM ids
  [<Literal>]
  let RegionId = "sessions-region"
  [<Literal>]
  let DialogId = "new-session-dialog"
  [<Literal>]
  let TitleId = "new-session-title"
  [<Literal>]
  let StartingCardId = "new-session-starting"
  [<Literal>]
  let DirectoryInputId = "new-session-directory"
  [<Literal>]
  let OpenButtonId = "new-session-open"

  // data-testid values
  [<Literal>]
  let DialogTestId = "new-session-dialog"
  [<Literal>]
  let OpenTestId = "new-session-open"
  [<Literal>]
  let CloseTestId = "new-session-close"
  [<Literal>]
  let CancelTestId = "new-session-cancel"
  [<Literal>]
  let CreateTestId = "new-session"
  [<Literal>]
  let WarningTestId = "new-session-warning"
  [<Literal>]
  let SwitchTestId = "new-session-switch"
  [<Literal>]
  let RefusalTestId = "new-session-refusal"
  [<Literal>]
  let CandidateTestId = "new-session-candidate"
  [<Literal>]
  let WorkflowTestId = "new-session-workflow"
  [<Literal>]
  let StatusTestId = "new-session-status"
  [<Literal>]
  let StartingTestId = "session-card-starting"

  // browser signals (the open flag and the directory already exist as `Signals.NewSessionOpen`/`NewSessionDir`)
  [<Literal>]
  let TargetSignal = "newSessionTarget"
  [<Literal>]
  let ProjectsSignal = "newSessionProjects"
  [<Literal>]
  let WorkflowSignal = "newSessionWorkflow"

  // routes (opening the dialog is a discovery of the directory it opens on)
  [<Literal>]
  let DiscoverRoute = "/dashboard/new-session/discover"
  [<Literal>]
  let CreateRoute = "/dashboard/new-session/create"
  [<Literal>]
  let CloseRoute = "/dashboard/new-session/close"
