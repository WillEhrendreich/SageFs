namespace SageFs

open System
open System.IO
open SageFs.WorkerProtocol

/// ROLE: when a hard reset may start the replacement worker.
///   The default is spawn-first: the replacement warms up while the old worker keeps serving, and the old worker
///   retires once the replacement reports Ready. A session whose startup profile (`.SageFs/init.fsx`) runs at load
///   cannot take that order. The profile may start an app on a fixed port, and the old worker still holds the port, so
///   the new one loses the bind. A profile that waits for the port never lets the new worker report Ready, so the
///   old one is never retired and the daemon reverts the swap after 30 s of silence (#179).
///   For those sessions the old worker leaves first, and the replacement starts once the reaper says it is gone.
/// Assumes: the profile is found the way the worker finds it (`StartupProfile.searchDirectories`). The worker knows its
///   primary project, which the manager cannot know for a solution target without resolving it, so a solution is
///   looked for in its own folder. A profile kept only in a project folder of a solution is not seen here, and that
///   session keeps spawn-first.
module RespawnAfterReap =

  [<RequireQualifiedAccess>]
  type Sequencing =
    /// The replacement warms up while the old worker serves, and the old worker retires when the replacement is Ready.
    | SpawnFirst
    /// The old worker leaves first. Carries the startup profile that decided it, for the log.
    | ReapThenSpawn of startupScript: string

  /// The folders a target's startup profile may be in: each project or solution's own folder, then the working
  /// directory. A target with no folder (a bare path, `Bare`) adds none.
  let searchFolders (targets: SessionProjectTarget list) (workingDir: string) : string list =
    let own =
      SessionProjectTarget.paths targets
      |> List.choose (fun path -> Path.GetDirectoryName path |> Option.ofObj |> Option.filter (String.IsNullOrEmpty >> not))
    own @ [ workingDir ] |> List.distinct

  /// Looks at the disk: two file-exists probes per folder. The edge of this module.
  let tryFindStartupScript (targets: SessionProjectTarget list) (workingDir: string) : StartupProfile.InitScript option =
    StartupProfile.tryFindInitScript (searchFolders targets workingDir)

  let sequencingOf (script: StartupProfile.InitScript option) : Sequencing =
    match script with
    | Some found -> Sequencing.ReapThenSpawn found.Path
    | None -> Sequencing.SpawnFirst

  let sequencingFor (targets: SessionProjectTarget list) (workingDir: string) : Sequencing =
    sequencingOf (tryFindStartupScript targets workingDir)

  /// Replacements waiting for their old worker to leave, by session. Generic so the decision is testable without a
  /// session record.
  type Book<'session> = Map<SessionId, 'session>

  [<RequireQualifiedAccess>]
  type Verdict<'session> =
    /// No replacement is waiting for this session.
    | NothingParked
    /// An old worker is still exiting.
    | StillWaiting of pids: int list
    /// Every old worker is gone: start the replacement.
    | SpawnNow of 'session
    /// An old worker outlived its stop and its kill. Start the replacement anyway, since waiting would leave the
    /// session Restarting forever, and say which worker may still hold the port.
    | SpawnDespiteSurvivors of 'session * pids: int list

  let empty<'session> : Book<'session> = Map.empty

  let park (id: SessionId) (session: 'session) (book: Book<'session>) : Book<'session> = Map.add id session book

  let isParked (id: SessionId) (book: Book<'session>) : bool = Map.containsKey id book

  let drop (id: SessionId) (book: Book<'session>) : Book<'session> = Map.remove id book

  /// Where the old workers of `id` stand decides whether its parked replacement starts. A started replacement leaves
  /// the book, so a second report for the same session cannot start another.
  let verdict (id: SessionId) (standing: WorkerReaper.Standing) (book: Book<'session>) : Verdict<'session> * Book<'session> =
    match Map.tryFind id book with
    | None -> Verdict.NothingParked, book
    | Some session ->
      match standing with
      | WorkerReaper.Standing.StillExiting pids -> Verdict.StillWaiting pids, book
      | WorkerReaper.Standing.Done WorkerReaper.Settled.AllGone -> Verdict.SpawnNow session, drop id book
      | WorkerReaper.Standing.Done (WorkerReaper.Settled.Survivors pids) -> Verdict.SpawnDespiteSurvivors (session, pids), drop id book
      | WorkerReaper.Standing.Done (WorkerReaper.Settled.Unknown _) -> Verdict.SpawnDespiteSurvivors (session, []), drop id book
