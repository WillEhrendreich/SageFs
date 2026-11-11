namespace SageFs

/// Session working-directory / project-path containment validation.
///
/// Its own small module — not more mass bolted onto `McpAdapter.fs`'s
/// formatting grab-bag — so BOTH the `create_session` MCP tool (`Mcp.fs`)
/// and the `/api/sessions/create` HTTP route (`McpServer.fs`) can call ONE
/// shared implementation: one rule, one place it lives (sagefs-roast.md
/// Finding #1/#13). It compiles before both call sites (McpAdapter.fs,
/// Mcp.fs, McpTools.fs, McpServer.fs — see SageFs.fsproj compile order),
/// which is the reason it is a standalone module rather than living next to
/// either caller.
module SessionPathValidation =

  /// Canonicalize a path the same way `/load-script`'s own `resolveRealPath`
  /// does (`Path.GetFullPath` + `ResolveLinkTarget(returnFinalTarget=true)`),
  /// so a symlink cannot be used to make a contained-looking path resolve
  /// somewhere else at eval time.
  let private resolveRealSessionPath (p: string) : string =
    let full = System.IO.Path.GetFullPath p
    // Only resolve a symlink target when something actually exists at the path.
    // A project path frequently does NOT exist as a file at Combine(workingDir,
    // name) — the daemon resolves/locates projects itself — and calling
    // ResolveLinkTarget on a non-existent path throws DirectoryNotFoundException,
    // which the create handler does not catch (→ a spurious 500 instead of a
    // clean containment decision). The canonical `full` path is the right value
    // to containment-check against when there is no link to follow, so a missing
    // path still canonicalizes and an escaping one is still rejected as 400.
    let fsiOpt : System.IO.FileSystemInfo option =
      if System.IO.Directory.Exists full then Some (System.IO.DirectoryInfo(full) :> System.IO.FileSystemInfo)
      elif System.IO.File.Exists full then Some (System.IO.FileInfo(full) :> System.IO.FileSystemInfo)
      else None
    match fsiOpt with
    | None -> full
    | Some fsi ->
      match fsi.ResolveLinkTarget(returnFinalTarget = true) with
      | null -> full
      | resolved -> resolved.FullName

  /// A path that is its own root (`/`, `C:\`), after links are followed.
  let isFilesystemRoot (canonical: string) : bool =
    let trimmed (p: string) = p.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
    match System.IO.Path.GetPathRoot canonical with
    | null | "" -> false
    | root -> System.String.Equals(trimmed root, trimmed canonical, System.StringComparison.OrdinalIgnoreCase)

  /// What a session's reply should warn about its working directory, when anything.
  [<RequireQualifiedAccess>]
  type SessionRootNotice =
    | NoNotice
    /// The person's home directory itself. The watchers refuse it (`FileWatcher.classifyWatchRoot`: a recursive watch
    /// there covered every file the person has, and a symlink cycle under it once grew the daemon to 55 GB), so the
    /// session gets no file events, and the reply has to say so.
    | HomeDirectory of path: string

  module SessionRootNotice =
    let describe (notice: SessionRootNotice) : string option =
      match notice with
      | SessionRootNotice.NoNotice -> None
      | SessionRootNotice.HomeDirectory path ->
        Some (sprintf "Warning: this session works in your home directory (%s). SageFs will not watch a folder that holds every file you have, so hot reload and live testing get no file events in this session. Start the session in the project's own directory instead." path)

    /// `reply` with the notice's text after it, when the notice has one. Every reply to a session create goes through
    /// this, so the warning reaches whoever asked.
    let appendTo (notice: SessionRootNotice) (reply: string) : string =
      match describe notice with
      | None -> reply
      | Some text -> reply + "\n\n" + text

  let private isUncPath (p: string) =
    not (System.String.IsNullOrWhiteSpace p)
    && (p.StartsWith(@"\\") || p.StartsWith("//"))

  /// The directory the person's home is, as a type of its own: `noticeFor` takes this and a working directory, and two
  /// bare strings there could be swapped without the compiler noticing.
  type HomeDir = HomeDir of string

  /// Pure apart from following links: whether `workingDir` is `home` itself. `home` is a parameter so a test names it.
  let noticeFor (HomeDir home: HomeDir) (workingDir: string) : SessionRootNotice =
    match System.String.IsNullOrWhiteSpace home || System.String.IsNullOrWhiteSpace workingDir with
    | true -> SessionRootNotice.NoNotice
    | false ->
      let trimmed (p: string) = p.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
      match System.String.Equals(trimmed (resolveRealSessionPath home), trimmed (resolveRealSessionPath workingDir), System.StringComparison.Ordinal) with
      | true -> SessionRootNotice.HomeDirectory workingDir
      | false -> SessionRootNotice.NoNotice

  /// `noticeFor` against this user's home directory.
  let noticeForUser (workingDir: string) : SessionRootNotice =
    noticeFor (HomeDir (System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile)) workingDir

  /// Validate a session-create request's `workingDirectory`/`projects` with
  /// the SAME canonicalization + containment discipline
  /// `DashboardTypes.resolveSessionProjects` already applies on the
  /// dashboard's own `/dashboard/session/create` path (and `/load-script`'s
  /// `resolveRealPath`/`isContained` apply to a loaded file): the working
  /// directory must exist, must not be a UNC path, and every project path
  /// must canonicalize to somewhere inside it.
  ///
  /// Before this helper existed, `/api/sessions/create` fed
  /// `workingDirectory` and `projects` straight into
  /// `SessionOps.CreateSession` with no validation at all, and the MCP tool
  /// `create_session` STILL did (sagefs-roast.md Finding #1/#13) — any local
  /// process, or (absent the Origin/Host checks fixed separately) any web
  /// page reaching this port, could root a session at an arbitrary path.
  /// Unlike `resolveSessionProjects` (which silently filters escaping
  /// projects out of the list), this REJECTS the whole request on the first
  /// unsafe path — a request that named an unsafe path should get a clear
  /// refusal, never a session quietly created somewhere else. Pure — no IO
  /// beyond path/filesystem probes — so it is unit-tested directly.
  let validateSessionCreateRequest (workingDir: string) (projects: string list) : Result<unit, SageFsError> =
    match System.String.IsNullOrWhiteSpace workingDir with
    | true -> Error (SageFsError.UnsafeSessionPath(workingDir, "workingDirectory is required"))
    | false ->
    match isUncPath workingDir with
    | true -> Error (SageFsError.UnsafeSessionPath(workingDir, "UNC paths are not allowed"))
    | false ->
    match System.IO.Directory.Exists workingDir with
    | false -> Error (SageFsError.UnsafeSessionPath(workingDir, "directory does not exist"))
    | true ->
    let canonicalDir = resolveRealSessionPath workingDir
    match isFilesystemRoot canonicalDir with
    | true ->
      Error (SageFsError.UnsafeSessionPath(workingDir, "a filesystem root holds every file on the machine, so the session would watch all of them and contain nothing; start it in the project's own directory, the one that holds its .fsproj or .sln"))
    | false ->
    let isContained (p: string) =
      not (isUncPath p)
      && (let full =
            match System.IO.Path.IsPathRooted p with
            | true -> p
            | false -> System.IO.Path.Combine(workingDir, p)
          let canonical = resolveRealSessionPath full
          canonical.StartsWith(
            canonicalDir + string System.IO.Path.DirectorySeparatorChar,
            System.StringComparison.OrdinalIgnoreCase)
          || canonical.Equals(canonicalDir, System.StringComparison.OrdinalIgnoreCase))
    match projects |> List.tryFind (isContained >> not) with
    | Some escaping -> Error (SageFsError.UnsafeSessionPath(escaping, "project path escapes the session working directory"))
    | None ->
      match SessionProjectTarget.tryCreateMany projects with
      | Error reason -> Error (SageFsError.SessionCreationFailed reason)
      | Ok targets -> SessionProjectTarget.validate targets |> Result.mapError SageFsError.SessionCreationFailed
