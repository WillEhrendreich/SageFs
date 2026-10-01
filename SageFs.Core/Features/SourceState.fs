namespace SageFs

open System

// ── SourceState: is the build this session runs behind the files on disk? ─────────
//
// Two different facts sit next to each other on every status surface, and a reader must not confuse them:
//
//   * `ReplFreshness` (SessionReload.fs): the REPL is BEHIND THE APP. A save was patched into the running app by a
//     metadata delta, and the FSI host the REPL and the tests run in kept the build from before it.
//   * `SourceState` (this file): the DISK IS AHEAD OF THE BUILD. A file the build was made from was written after that
//     build, so the host (and the tests in it) run code that is not what the files say.
//
// They can be true together, apart, or not at all. Neither implies the other.
//
// The decision is a pure function of what an edge read (when the worker loaded its build, when the build's output was
// written, when each source file was written) and of the session's rebuild record. Every IO failure arrives as a CASE
// (`StampRead.Unreadable`, `LoadedAt.NotReported`, `ProjectEvidence.NotInspectable`), so "could not tell" is its own
// answer and can never be read as "in sync".

/// A file's last-write time as the edge read it. A read that failed is a case, never a missing value.
[<RequireQualifiedAccess>]
type StampRead =
  | Written of at: DateTime
  | Unreadable of reason: string

/// One file and what the edge read of it.
type StampedFile = { Path: string; Stamp: StampRead }

/// What the edge found out about one project the session loaded.
[<RequireQualifiedAccess>]
type ProjectEvidence =
  /// The project's build output, and every file that builds into it (the project file included).
  | Inspected of project: string * output: StampedFile * sources: StampedFile list
  /// What builds into it could not be listed (project file unreadable or unparsable, a wildcard item, no loaded
  /// assembly that the project built).
  | NotInspectable of project: string * reason: string

/// When the worker loaded the build it runs.
[<RequireQualifiedAccess>]
type LoadedAt =
  | Reported of at: DateTime
  | NotReported of reason: string

/// Why a file counts as changed.
[<RequireQualifiedAccess>]
type StaleBecause =
  /// The file was written after the build this session runs was made.
  | EditedAfterBuild of editedAt: DateTime * builtAt: DateTime
  /// A newer build of the project exists on disk than the one this session loaded.
  | RebuiltAfterLoad of builtAt: DateTime * loadedAt: DateTime

type StaleFile = { Path: string; Because: StaleBecause }

/// Why nothing can be said about the build. Each case names what is missing.
[<RequireQualifiedAccess>]
type UnknownReason =
  /// Nobody looked: the receipt was read without a source check.
  | NotAssessed
  /// The session loaded no project, so there is no build to be behind.
  | NoProjectLoaded
  /// The worker did not say when it loaded its build.
  | LoadTimeNotReported of reason: string
  /// A file's write time could not be read.
  | Unreadable of path: string * reason: string
  /// What builds into a project could not be listed.
  | NotInspectable of project: string * reason: string

[<RequireQualifiedAccess>]
type SourceState =
  /// Nothing the build was made from has been written since the build the session runs. `builtAt` is the oldest
  /// build among the session's projects; `filesChecked` is how many files were compared.
  | InSync of builtAt: DateTime * filesChecked: int
  /// These files changed on disk after the build the session runs.
  | Stale of changed: StaleFile list
  /// A rebuild is in progress. The old worker keeps serving the build from before it.
  | Rebuilding of since: DateTime
  /// Could not tell, and why. Never to be read as in sync.
  | Unknown of reason: UnknownReason

[<RequireQualifiedAccess>]
module SourceState =

  /// How many changed files a sentence names before it says how many more there are.
  let private namedInSentence = 5

  let private clock (at: DateTime) = at.ToString("yyyy-MM-dd HH:mm:ss'Z'")

  let private stamp (file: StampedFile) : Result<DateTime, string> =
    match file.Stamp with
    | StampRead.Written at -> Result.Ok at
    | StampRead.Unreadable reason -> Result.Error reason

  /// What one project says: the files that changed after the build it ran, what it could not read, and how many files it
  /// compared.
  type private Reading =
    { Changed: StaleFile list
      Unknown: UnknownReason list
      Compared: int
      BuiltAt: DateTime voption }

  let private readProject (loadedAt: DateTime) (evidence: ProjectEvidence) : Reading =
    match evidence with
    | ProjectEvidence.NotInspectable (project, reason) ->
      { Changed = []; Unknown = [ UnknownReason.NotInspectable (project, reason) ]; Compared = 0; BuiltAt = ValueNone }
    | ProjectEvidence.Inspected (_, output, sources) ->
      match stamp output with
      | Result.Error reason ->
        { Changed = []; Unknown = [ UnknownReason.Unreadable (output.Path, reason) ]; Compared = 0; BuiltAt = ValueNone }
      | Result.Ok builtAt ->
        let outputChange =
          match builtAt > loadedAt with
          | true -> [ { Path = output.Path; Because = StaleBecause.RebuiltAfterLoad (builtAt, loadedAt) } ]
          | false -> []
        let readSources =
          sources
          |> List.map (fun source ->
            match stamp source with
            | Result.Ok editedAt when editedAt > builtAt ->
              Result.Ok (ValueSome { Path = source.Path; Because = StaleBecause.EditedAfterBuild (editedAt, builtAt) })
            | Result.Ok _ -> Result.Ok ValueNone
            | Result.Error reason -> Result.Error (UnknownReason.Unreadable (source.Path, reason)))
        { Changed = outputChange @ (readSources |> List.choose (function Result.Ok (ValueSome change) -> Some change | _ -> None))
          Unknown = readSources |> List.choose (function Result.Error reason -> Some reason | _ -> None)
          Compared = sources.Length
          BuiltAt = ValueSome builtAt }

  /// The decision. The rebuild record answers first (a rebuild in progress answers by itself), then there must be a project and
  /// a load time, then what changed is named, then what could not be read, and only when every part of the evidence checked
  /// out is the answer InSync. A file that changed outranks one that could not be read: it is the fact someone can act on, and
  /// the rebuild that fixes it reads the rest again.
  let decide (rebuild: LastRebuild) (loaded: LoadedAt) (projects: ProjectEvidence list) : SourceState =
    match rebuild with
    | LastRebuild.Latest (RebuildOutcome.InProgress since) -> SourceState.Rebuilding since
    | LastRebuild.NeverRebuilt
    | LastRebuild.Latest (RebuildOutcome.Succeeded _)
    | LastRebuild.Latest (RebuildOutcome.FailedStillServing _)
    | LastRebuild.Latest (RebuildOutcome.FailedNotServing _) ->
      match projects, loaded with
      | [], _ -> SourceState.Unknown UnknownReason.NoProjectLoaded
      | _, LoadedAt.NotReported reason -> SourceState.Unknown (UnknownReason.LoadTimeNotReported reason)
      | _, LoadedAt.Reported loadedAt ->
        let readings = projects |> List.map (readProject loadedAt)
        match readings |> List.collect (fun r -> r.Changed), readings |> List.collect (fun r -> r.Unknown) with
        | _ :: _ as changed, _ -> SourceState.Stale changed
        | [], firstUnknown :: _ -> SourceState.Unknown firstUnknown
        | [], [] ->
          let oldestBuild = readings |> List.choose (fun r -> match r.BuiltAt with ValueSome at -> Some at | ValueNone -> None) |> List.min
          SourceState.InSync (oldestBuild, readings |> List.sumBy (fun r -> r.Compared))

  /// The one wire spelling of each state.
  let token (state: SourceState) : string =
    match state with
    | SourceState.InSync _ -> "InSync"
    | SourceState.Stale _ -> "Stale"
    | SourceState.Rebuilding _ -> "Rebuilding"
    | SourceState.Unknown _ -> "Unknown"

  /// Stale outranks a rebuild, a rebuild outranks not knowing, and not knowing outranks in sync.
  let private rank (state: SourceState) : int =
    match state with
    | SourceState.InSync _ -> 0
    | SourceState.Unknown _ -> 1
    | SourceState.Rebuilding _ -> 2
    | SourceState.Stale _ -> 3

  /// The worse of two readings of the same session, for a run that spans both (dispatched at one, finished at the other).
  /// Two stale readings keep every file either named, each once.
  let worse (earlier: SourceState) (later: SourceState) : SourceState =
    match earlier, later with
    | SourceState.Stale first, SourceState.Stale second ->
      SourceState.Stale (first @ second |> List.distinctBy (fun file -> file.Path))
    | _ when rank earlier > rank later -> earlier
    | SourceState.Unknown _, SourceState.Unknown _ -> earlier
    | _ -> later

  let private reasonText (reason: UnknownReason) : string =
    match reason with
    | UnknownReason.NotAssessed -> "this receipt was read without a source check"
    | UnknownReason.NoProjectLoaded -> "the session loaded no project, so there is no build to be behind"
    | UnknownReason.LoadTimeNotReported detail -> sprintf "the worker did not report when it loaded its build (%s)" detail
    | UnknownReason.Unreadable (path, detail) -> sprintf "%s could not be read (%s)" path detail
    | UnknownReason.NotInspectable (project, detail) -> sprintf "what builds into %s could not be listed (%s)" project detail

  let private reasonKind (reason: UnknownReason) : string =
    match reason with
    | UnknownReason.NotAssessed -> "NotAssessed"
    | UnknownReason.NoProjectLoaded -> "NoProjectLoaded"
    | UnknownReason.LoadTimeNotReported _ -> "LoadTimeNotReported"
    | UnknownReason.Unreadable _ -> "Unreadable"
    | UnknownReason.NotInspectable _ -> "NotInspectable"

  let private becauseKind (because: StaleBecause) : string =
    match because with
    | StaleBecause.EditedAfterBuild _ -> "EditedAfterBuild"
    | StaleBecause.RebuiltAfterLoad _ -> "RebuiltAfterLoad"

  let private becauseText (file: StaleFile) : string =
    match file.Because with
    | StaleBecause.EditedAfterBuild (editedAt, builtAt) ->
      sprintf "%s (edited %s, the build is from %s)" file.Path (clock editedAt) (clock builtAt)
    | StaleBecause.RebuiltAfterLoad (builtAt, loadedAt) ->
      sprintf "%s (a build from %s exists, this session loaded at %s)" file.Path (clock builtAt) (clock loadedAt)

  /// What the state says, in a sentence a reader can act on without knowing the history.
  let describe (state: SourceState) : string =
    match state with
    | SourceState.InSync (builtAt, checkedFiles) ->
      sprintf "Nothing the build was made from has changed since the build (%s); %d file(s) compared." (clock builtAt) checkedFiles
    | SourceState.Stale changed ->
      let named =
        match changed.Length <= namedInSentence with
        | true -> changed |> List.map becauseText |> String.concat "; "
        | false ->
          sprintf "%s; and %d more" (changed |> List.truncate namedInSentence |> List.map becauseText |> String.concat "; ") (changed.Length - namedInSentence)
      sprintf
        "STALE SOURCE: %d file(s) changed on disk after the build this session runs, so the REPL and the tests run code that is not what the files say: %s. hard_reset_fsi_session with rebuild=true builds the files and loads them."
        changed.Length named
    | SourceState.Rebuilding since ->
      sprintf "A rebuild is in progress (started %s). The session keeps serving the build from before it, so a run now says nothing about the edits the rebuild is picking up." (clock since)
    | SourceState.Unknown reason ->
      sprintf "Whether the build is current could not be told: %s." (reasonText reason)

  /// The warning a tool result carries, or nothing when the build is current.
  let banner (state: SourceState) : string =
    match state with
    | SourceState.InSync _ -> ""
    | SourceState.Stale _
    | SourceState.Rebuilding _
    | SourceState.Unknown _ -> sprintf "WARNING: %s" (describe state)

  /// The line a session list carries under each session: always one, so a reader never has to read an absence as "fine".
  let listLine (state: SourceState) : string =
    match state with
    | SourceState.InSync (_, checkedFiles) -> sprintf "Source: in sync with the build (%d file(s) compared)" checkedFiles
    | SourceState.Unknown UnknownReason.NoProjectLoaded -> "Source: no project is loaded, so there is no build to be behind"
    | SourceState.Stale _
    | SourceState.Rebuilding _
    | SourceState.Unknown _ -> banner state

  /// A tool's result, with the warning after it when the build is not known to be current. The result stays first and whole.
  let annotate (state: SourceState) (text: string) : string =
    match banner state with
    | "" -> text
    | warning -> sprintf "%s\n\n%s" text warning

  /// How the state appears in every status shape: always a field, never an absence a client has to read as "fine".
  let toWire (state: SourceState) : obj =
    match state with
    | SourceState.InSync (builtAt, checkedFiles) ->
      box {| state = token state; message = describe state; builtAt = builtAt; filesChecked = checkedFiles |}
    | SourceState.Stale changed ->
      box
        {| state = token state
           message = describe state
           changedFiles =
             changed
             |> List.map (fun file ->
               let writtenAt, comparedTo =
                 match file.Because with
                 | StaleBecause.EditedAfterBuild (editedAt, builtAt) -> editedAt, builtAt
                 | StaleBecause.RebuiltAfterLoad (builtAt, loadedAt) -> builtAt, loadedAt
               {| path = file.Path
                  because = becauseKind file.Because
                  writtenAt = writtenAt
                  comparedTo = comparedTo
                  detail = becauseText file |})
             |> List.toArray |}
    | SourceState.Rebuilding since -> box {| state = token state; message = describe state; since = since |}
    | SourceState.Unknown reason ->
      box {| state = token state; message = describe state; reason = {| kind = reasonKind reason; detail = reasonText reason |} |}
