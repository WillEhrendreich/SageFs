namespace SageFs

/// Pure single-owner decision core for the live-test file watcher (roast-9
/// #10). This module contains no IO: it only decides, given the current
/// state and an incoming message, what the new state is and which effects
/// the impure shell (LiveTestWatcherManager in SageFs/DaemonMode.fs) must
/// carry out. One MailboxProcessor owns the state and runs the effects, so
/// every message is processed FIFO by a single owner — no lock, no
/// ConcurrentDictionary, no epoch/generation counter.
///
/// Why the old epoch/stale-event guard (LiveTestWatcherStaleGuard) is gone:
/// with a single-owner mailbox, a FileSaved message posted by a watcher that
/// is later torn down always sits BEHIND the RemoveDirectory/StopWatch
/// message that tore it down (FIFO on one owner). By the time DebounceElapsed
/// drains that path, `sessionsForPath` is evaluated against the CURRENT
/// DirSessions, which no longer has a claim for it — so the stale reload is
/// dropped by construction, not by a race-prone epoch comparison.
///
/// Top-level module (not nested) so FSI / REPL consumers can resolve
/// `LiveTestWatcherCore.apply` etc. by name.
module LiveTestWatcherCore =

  open WorkerProtocol

  /// Everything that can change watcher state arrives as one of these.
  type Msg =
    /// A session claims a directory should be watched.
    | AddDirectory of dir: string * sessionId: SessionId
    /// A session drops its claim on a directory.
    | RemoveDirectory of dir: string * sessionId: SessionId
    /// Replace all session claims with exactly this (sessionId, dir) list —
    /// used on a daemon tick to reconcile watched dirs to the live session set.
    | SyncToSessions of sessions: (SessionId * string) list
    /// A watched file changed (posted by a FileSystemWatcher callback, already
    /// filtered by the shell for watched-extension / not-nested-checkout).
    | FileSaved of path: string
    /// The debounce window elapsed — drain whatever paths are pending.
    | DebounceElapsed

  /// What the shell must actually do as a result of applying a message.
  /// StartWatch/StopWatch are directory-level (create/dispose a
  /// FileSystemWatcher); ArmDebounce (re)starts the debounce timer;
  /// DrainPending hands the shell the coalesced path set to resolve and read.
  type Effect =
    | StartWatch of dir: string
    | StopWatch of dir: string
    | ArmDebounce
    | DrainPending of paths: string list

  /// DirSessions: dir -> the session IDs that claim it (empty/absent = no
  /// claim). Pending: paths saved since the last debounce drain. Watched: the
  /// dirs the core currently believes are (or should be) watched — this is
  /// recomputed by `reconcile` and always equals `neededDirs fallback DirSessions`
  /// after any message is applied.
  type State =
    { DirSessions: Map<string, SessionId list>
      Pending: Set<string>
      Watched: Set<string> }

  let empty: State =
    { DirSessions = Map.empty
      Pending = Set.empty
      Watched = Set.empty }

  /// The set of directories that must be watched: the fallback dir (if any)
  /// plus every dir with at least one real session claim.
  let neededDirs (fallbackDir: string option) (dirSessions: Map<string, SessionId list>) : Set<string> =
    let claimed =
      dirSessions
      |> Map.toSeq
      |> Seq.filter (fun (_, claims) -> not (List.isEmpty claims))
      |> Seq.map fst
      |> Set.ofSeq
    match fallbackDir with
    | Some f -> Set.add f claimed
    | None -> claimed

  /// Recompute Watched from DirSessions and emit Start/Stop effects for the
  /// diff against the previous Watched set. Called after every message that
  /// can change which dirs are needed.
  let reconcile (fallbackDir: string option) (state: State) : State * Effect list =
    let needed = neededDirs fallbackDir state.DirSessions
    let effects =
      [ for d in Set.difference needed state.Watched -> StartWatch d ]
      @ [ for d in Set.difference state.Watched needed -> StopWatch d ]
    { state with Watched = needed }, effects

  /// The pure decision: (fallbackDir, state, msg) -> (newState, effects).
  /// No IO happens here — the shell runs the returned effects.
  let apply (fallbackDir: string option) (state: State) (msg: Msg) : State * Effect list =
    match msg with
    | AddDirectory(dir, sessionId) ->
      let existing = Map.tryFind dir state.DirSessions |> Option.defaultValue []
      let claims = if List.contains sessionId existing then existing else sessionId :: existing
      reconcile fallbackDir { state with DirSessions = Map.add dir claims state.DirSessions }
    | RemoveDirectory(dir, sessionId) ->
      let existing = Map.tryFind dir state.DirSessions |> Option.defaultValue []
      let dirSessions =
        match existing |> List.filter (fun s -> s <> sessionId) with
        | [] -> Map.remove dir state.DirSessions
        | remaining -> Map.add dir remaining state.DirSessions
      reconcile fallbackDir { state with DirSessions = dirSessions }
    | SyncToSessions sessions ->
      let desired = sessions |> List.map snd |> Set.ofList
      let added =
        sessions
        |> List.fold
          (fun m (sessionId, dir) ->
            let existing = Map.tryFind dir m |> Option.defaultValue []
            let claims = if List.contains sessionId existing then existing else sessionId :: existing
            Map.add dir claims m)
          state.DirSessions
      let kept = added |> Map.filter (fun dir _ -> desired.Contains dir)
      reconcile fallbackDir { state with DirSessions = kept }
    | FileSaved path -> { state with Pending = Set.add path state.Pending }, [ ArmDebounce ]
    | DebounceElapsed -> { state with Pending = Set.empty }, [ DrainPending(Set.toList state.Pending) ]

  /// Session ID(s) owning the dir that contains `path` (the deepest containing
  /// dir wins; the fallback dir claims no session and is not in DirSessions,
  /// so a path under only the fallback dir resolves to []).
  /// "Contains" is `PathContainment.isWithin`, the one definition: whole path
  /// segments after the platform resolves `.`, `..` and a trailing separator,
  /// compared with the platform's case rule. `/a/repo2/x.fs` is not inside
  /// `/a/repo`; a text prefix test said it was.
  ///
  /// Every save in a watched tree asks this. The path is split once and each directory once, and the walk keeps the
  /// deepest match as it goes: it used to split the path again for every directory, split each match again to sort by
  /// depth, and build a sequence. Two spellings of one directory are the same depth, and the first in the map's order
  /// keeps the path (the sort was stable).
  let sessionsForPath (dirSessions: Map<string, SessionId list>) (path: string) : SessionId list =
    let pathCase = PathContainment.PathCase.ofCurrentOs ()
    let pathSegments = PathContainment.fullSegments path
    let struct (_, owners) =
      dirSessions
      |> Map.fold
        (fun (struct (deepest, _) as best) dir sessions ->
          let rootSegments = PathContainment.fullSegments dir
          let depth = List.length rootSegments
          match depth > deepest && PathContainment.segmentsWithin pathCase rootSegments pathSegments with
          | true -> struct (depth, sessions)
          | false -> best)
        (struct (-1, []))
    owners

  /// Whether `path` is under a directory that is CURRENTLY watched (a claimed
  /// session dir OR the session-less fallback dir). The shell uses this, not
  /// `sessionsForPath`, to decide whether to fire FileContentChanged on a drain:
  /// a file under the fallback dir fires FileContentChanged with no session
  /// (the fallback contract), while a save queued before its dir was removed
  /// resolves to a dir no longer in `Watched` and is dropped (the stale case).
  let isUnderWatchedDir (watched: Set<string>) (path: string) : bool =
    let pathCase = PathContainment.PathCase.ofCurrentOs ()
    watched |> Set.exists (fun dir -> PathContainment.isWithin pathCase dir path)

/// What the live-test watcher does with a file that is not F# source: a project file, props, targets, a lock file, the
/// SDK pin, or a data file in the project's folder. The watcher used to see `.fs` and `.fsx` only, so a changed package
/// version or fixture never reached live testing and the status stayed green over a build that no longer matched it.
///
/// The rule is the landing gate's (`ProjectFileChange.select`, see docs/decisions.md): an edit that only changes a
/// project's compile list runs the tests that cover the files at and after the first difference, and any other change to a
/// build input runs the whole suite. Either way the session is rebuilt first, since only a build reads these files.
module LiveTestBuildInput =

  open SageFs.Features
  open SageFs.Features.LiveTesting

  [<RequireQualifiedAccess>]
  type BuildInputKind =
    /// A `.fsproj`, `.csproj` or `.vbproj`: its text is diffed against the text the watcher saw last.
    | ProjectFile
    /// Anything else a build reads: props, targets, a lock file, the SDK pin, a resource or data file. Any change to its
    /// content can reach any test.
    | OtherInput

  /// What a changed file under a watched directory is to live testing.
  [<RequireQualifiedAccess>]
  type WatchedFile =
    /// `.fs` or `.fsx`: the source save path, unchanged.
    | Source
    | BuildInput of BuildInputKind
    /// Nothing to run: an editor's scratch file, a test run's own output, or a file no build reads.
    | Ignored of SageFs.FileWatcher.WatchDrop

  /// File names an editor or a test run writes next to real files while saving or failing: vim's `name~`, `.name.swp` and
  /// its `4913` write probe, Emacs' `.#name`, `~name` and `name.tmp`, and Verify's `name.received.ext`. A run that wrote
  /// a received file and was then woken by it would never stop.
  let isScratchName (fileName: string) : bool =
    let lower = fileName.ToLowerInvariant()
    lower.StartsWith "~"
    || lower.StartsWith ".#"
    || lower.EndsWith "~"
    || lower.EndsWith ".tmp"
    || lower.EndsWith ".swp"
    || lower.EndsWith ".swo"
    || lower.EndsWith ".swx"
    || lower = "4913"
    || lower.Contains ".received."

  let classify (path: string) : WatchedFile =
    let normalized = path.Replace('\\', '/')
    let fileName = System.IO.Path.GetFileName normalized
    let lower = fileName.ToLowerInvariant()
    // The daemon writes its own state under <session>/.SageFs (warmup replay cache, manifests, friction DB).
    // Reading that as a build input makes every state write rebuild the session that wrote it, which writes
    // state again — a loop that leaves every row Stale/BuildDisagrees and never settles.
    match normalized.Split('/') |> Array.exists (fun part -> part = ".SageFs") with
    | true -> WatchedFile.Ignored SageFs.FileWatcher.WatchDrop.UnwatchedExtension
    | false ->
      match isScratchName fileName with
      | true -> WatchedFile.Ignored SageFs.FileWatcher.WatchDrop.TemporaryFile
      | false ->
        match lower.EndsWith ".fs" || lower.EndsWith ".fsx" with
        | true -> WatchedFile.Source
        | false ->
          match CohortLandingPlan.kindByName path with
          | CohortLandingPlan.FileByName.ProjectFile -> WatchedFile.BuildInput BuildInputKind.ProjectFile
          | CohortLandingPlan.FileByName.BuildInputByName
          | CohortLandingPlan.FileByName.CompiledSource -> WatchedFile.BuildInput BuildInputKind.OtherInput
          | CohortLandingPlan.FileByName.Inert -> WatchedFile.Ignored SageFs.FileWatcher.WatchDrop.UnwatchedExtension
          | CohortLandingPlan.FileByName.Content ->
            // A dotfile (.editorconfig, .DS_Store, .gitattributes) is tooling state, not something a test reads.
            match fileName.StartsWith "." with
            | true -> WatchedFile.Ignored SageFs.FileWatcher.WatchDrop.UnwatchedExtension
            | false -> WatchedFile.BuildInput BuildInputKind.OtherInput

  /// Why the live-test watcher drops an event, or `None` when it queues it. Source keeps the `.fs`/`.fsx` filter it
  /// always had; a build input passes the same bin/obj and pattern checks with its own extension; a scratch file or a
  /// document is dropped by name. `config.Extensions` is ignored: the kind of file decides it.
  let dropReason (config: SageFs.FileWatcher.WatchConfig) (path: string) : SageFs.FileWatcher.WatchDrop option =
    let watchedAs (extensions: string list) = SageFs.FileWatcher.dropReason { config with Extensions = extensions } path
    match classify path with
    | WatchedFile.Ignored reason -> Some reason
    | WatchedFile.Source -> watchedAs [ ".fs"; ".fsx" ]
    | WatchedFile.BuildInput _ -> watchedAs [ System.IO.Path.GetExtension(path).ToLowerInvariant() ]

  /// What a change to a build input asks of live testing.
  [<RequireQualifiedAccess>]
  type Action =
    /// The file states what it stated before (a save with no change, whitespace or a comment in a project file).
    | Unchanged
    /// Rebuild, then run this selection.
    | Run of ProjectFileChange.Selection

  /// The decision for one changed build input. `relativePath` is the file's path under the session's directory, so the
  /// compile list resolves the way the landing gate resolves it; `readText` reads a source file by that same kind of path.
  /// For a project file `before` and `after` are its texts (`None` for a side that did not exist). For any other input
  /// they are fingerprints of its content.
  let decide
    (readText: string -> string option)
    (relativePath: string)
    (kind: BuildInputKind)
    (before: string option)
    (after: string option)
    : Action =
    match kind with
    | BuildInputKind.ProjectFile ->
      match ProjectFileChange.diff relativePath before after with
      | [] -> Action.Unchanged
      | changes -> Action.Run (ProjectFileChange.select readText changes)
    | BuildInputKind.OtherInput ->
      match before = after with
      | true -> Action.Unchanged
      | false -> Action.Run (ProjectFileChange.Selection.WholeSuite [ ProjectFileChange.Change.OtherBuildInput relativePath ])

  /// Each test's covered files from the session's coverage, the way the landing gate reads them: `None` for a test whose
  /// coverage cannot be trusted (no instrumentation, or a bitmap of another size), so the selection keeps it.
  let coveredFilesOf
    (blind: string list)
    (maps: InstrumentationMap array)
    (bitmaps: Map<TestId, CoverageBitmap>)
    : TestId -> string list option =
    let merged = InstrumentationMap.merge maps
    fun testId ->
      match merged.Slots.Length with
      | 0 -> None
      | _ ->
        match Map.tryFind testId bitmaps with
        | Some bitmap when bitmap.Count = merged.TotalProbes && bitmap.Count > 0 ->
          Some (InputHashCoverage.selectionFiles blind merged bitmap)
        | _ -> None

  /// The tests a selection runs. A narrow that selects nothing runs the whole suite, the landing gate's floor: a build
  /// input changed, and a run of nothing would leave every result standing over a build it no longer describes.
  let testsFor
    (selection: ProjectFileChange.Selection)
    (coveredFilesOf: TestId -> string list option)
    (tests: TestCase array)
    : TestCase array =
    match selection with
    | ProjectFileChange.Selection.WholeSuite _ -> tests
    | ProjectFileChange.Selection.NarrowToFiles files ->
      let chosen =
        AffectedTests.affected files coveredFilesOf (tests |> Array.map (fun test -> test.Id) |> Array.toList) |> Set.ofList
      match Set.isEmpty chosen with
      | true -> tests
      | false -> tests |> Array.filter (fun test -> chosen.Contains test.Id)

  /// A build input's content as the watcher compares it: a project file's text, any other input's SHA-256. `None` when
  /// the file is gone or cannot be read.
  let read (kind: BuildInputKind) (path: string) : string option =
    try
      match System.IO.File.Exists path, kind with
      | false, _ -> None
      | true, BuildInputKind.ProjectFile -> Some (System.IO.File.ReadAllText path)
      | true, BuildInputKind.OtherInput ->
        use stream = System.IO.File.OpenRead path
        Some (System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData stream))
    with
    | :? System.IO.IOException
    | :? System.UnauthorizedAccessException -> None

  /// What each build input under the watched directories said when the watcher last read it, and the decision for the
  /// next change against it. The impure edge of `decide`: it reads files. Owned by the watcher's mailbox loop, the
  /// only caller, so it takes no lock.
  type Store() =
    let lastSeen = System.Collections.Generic.Dictionary<string, string>()

    /// Read every project file under `dir` (pruned the way the watcher prunes), so the first edit to one is diffed
    /// against the text it had and a compile-list edit narrows from the start. Other inputs are not read ahead: their
    /// first change runs the whole suite, which is what any change to them does.
    member _.Seed(dir: string) =
      for watched in SageFs.FileWatcher.watchableDirs dir SageFs.FileWatcher.hasCheckoutMarker do
        try
          for file in System.IO.Directory.EnumerateFiles watched do
            match classify file with
            | WatchedFile.BuildInput BuildInputKind.ProjectFile ->
              read BuildInputKind.ProjectFile file |> Option.iter (fun text -> lastSeen[file] <- text)
            | WatchedFile.BuildInput BuildInputKind.OtherInput
            | WatchedFile.Source
            | WatchedFile.Ignored _ -> ()
        with
        | :? System.IO.IOException
        | :? System.UnauthorizedAccessException -> ()

    /// Decide a change to `path`, a build input under one of `watched`, and remember what it says now. The compile list
    /// resolves against the deepest watched directory holding the file, the way the landing gate resolves it against
    /// the repository root.
    member _.Decide(watched: Set<string>, path: string, kind: BuildInputKind) : Action =
      let pathCase = PathContainment.PathCase.ofCurrentOs ()
      let root =
        watched
        |> Seq.filter (fun dir -> PathContainment.isWithin pathCase dir path)
        |> Seq.fold (fun (best: string) dir -> match dir.Length > best.Length with | true -> dir | false -> best) ""
      let relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/')
      let readText (file: string) =
        try Some (System.IO.File.ReadAllText(System.IO.Path.Combine(root, file)))
        with
        | :? System.IO.IOException
        | :? System.UnauthorizedAccessException -> None
      let before = match lastSeen.TryGetValue path with | true, seen -> Some seen | false, _ -> None
      let after = read kind path
      match after with
      | Some now -> lastSeen[path] <- now
      | None -> lastSeen.Remove path |> ignore
      decide readText relative kind before after
