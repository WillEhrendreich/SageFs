namespace SageFs

open SageFs.WorkerProtocol
open SageFs.Features.LiveTesting

/// Which session a discovered test, and its file position, belongs to. The
/// daemon learned this wrongly three ways (a new session's `list_tests` carried
/// another session's FilePaths): it read the Primary cycle whoever asked, it
/// source-mapped against the daemon-global warmup context of whichever session
/// the viewer last looked at, and a stopped session's cycle was never retired.
/// Everything here is pure and takes the session's own data as arguments.
module SessionTestAttribution =

  /// The directories a session may claim source files in: its working directory
  /// and the directory of each project it loaded. Empty when the daemon has no
  /// snapshot of the session (yet), which makes every mapping for it fail
  /// closed instead of borrowing another session's files.
  let rootsFor (sessions: SessionSnapshot list) (sessionId: string) : string list =
    sessions
    |> List.tryFind (fun s -> SessionId.value s.Id = sessionId)
    |> Option.map (fun s ->
      let projectDirs =
        s.Projects
        |> List.filter (fun p -> not (System.String.IsNullOrWhiteSpace p))
        |> List.choose (fun p ->
          System.IO.Path.GetDirectoryName(System.IO.Path.Combine(s.WorkingDirectory, p))
          |> Option.ofObj)
      s.WorkingDirectory :: projectDirs
      |> List.filter (fun d -> not (System.String.IsNullOrWhiteSpace d))
      |> List.distinct)
    |> Option.defaultValue []

  /// Source-map one session's freshly discovered tests. Names alone cannot say
  /// whose file a test lives in, so a session claims only files under its own
  /// roots: for positions the worker reported, for tree-sitter locations, and
  /// for the warmup files a test is mapped to by module name.
  let mapDiscovered
    (sessions: SessionSnapshot list)
    (sessionId: string)
    (warmup: SessionContext option)
    (sourceLocations: SourceTestLocation array)
    (discovered: TestCase array)
    : TestCase array =
    let roots = rootsFor sessions sessionId
    let own = SessionSourceScope.confineToRoots roots discovered
    match Array.isEmpty sourceLocations with
    | true ->
      let files =
        warmup
        |> Option.map (fun c -> c.FileStatuses |> List.map (fun f -> f.Path))
        |> Option.defaultValue []
        |> List.filter (SessionSourceScope.isWithinRoots roots)
        |> Array.ofList
      SourceMapping.mapFromProjectFiles files own
    | false -> SourceMapping.mergeSourceLocations sourceLocations own
    |> SessionSourceScope.confineToRoots roots

  /// The warmup files the Primary cycle's own session may claim. The warmup
  /// context is the daemon-global one and need not belong to that session.
  let warmupFilesFor (sessions: SessionSnapshot list) (primary: LiveTestState) (warmup: SessionContext) : string array =
    let roots = LiveTestState.ownerSessionId primary |> Option.map (rootsFor sessions) |> Option.defaultValue []
    warmup.FileStatuses
    |> List.map (fun f -> f.Path)
    |> List.filter (SessionSourceScope.isWithinRoots roots)
    |> Array.ofList

  /// Tree-sitter locations a session may claim. An empty id is the file-change
  /// path, which names no session and is not narrowed.
  let locationsFor (sessions: SessionSnapshot list) (sessionId: string) (detected: SourceTestLocation array) =
    match sessionId with
    | "" -> detected
    | _ ->
      let roots = rootsFor sessions sessionId
      detected |> Array.filter (fun l -> SessionSourceScope.isWithinRoots roots l.FilePath)

  /// A session that is gone or stopped owns no live-testing state. Its
  /// background slot is dropped; when it owned the Primary cycle, Primary
  /// becomes the session now being viewed (promoted from its background slot)
  /// or empty. `None` when there is nothing to retire.
  let retire
    (sessionId: string)
    (nowViewing: string option)
    (primary: LiveTestCycleState)
    (background: Map<string, LiveTestCycleState>)
    : (LiveTestCycleState * Map<string, LiveTestCycleState>) option =
    let remaining = Map.remove sessionId background
    match LiveTestState.ownerSessionId primary.TestState = Some sessionId with
    | false when not (Map.containsKey sessionId background) -> None
    | false -> Some (primary, remaining)
    | true ->
      // Live testing being switched on is the user's setting, not the dead
      // session's data: a fresh Primary keeps it.
      let fresh = { LiveTestCycleState.empty with TestState = { LiveTestState.empty with Activation = primary.TestState.Activation } }
      let promoted =
        nowViewing
        |> Option.bind (fun id -> Map.tryFind id remaining)
        |> Option.defaultValue fresh
      Some (promoted, (nowViewing |> Option.map (fun id -> Map.remove id remaining) |> Option.defaultValue remaining))

  /// Sessions whose live-testing state must go after a list refresh: the stop
  /// paths (MCP, dashboard, daemon) dispatch only a refresh, never
  /// SessionStopped, so this is where a vanished or stopped session is noticed.
  let retiring (previous: SessionSnapshot list) (current: SessionSnapshot list) : string list =
    let present = current |> List.map (fun s -> s.Id) |> Set.ofList
    let vanished = previous |> List.filter (fun s -> not (Set.contains s.Id present))
    let stopped = current |> List.filter (fun s -> s.Status = SessionDisplayStatus.Stopped)
    (vanished @ stopped) |> List.map (fun s -> SessionId.value s.Id)

  /// The tests `list_tests` may report for the calling session: that session's
  /// own cycle (never whichever cycle is Primary), with any position outside
  /// the session's roots reported as "no location". `perClient` is the
  /// caller's bound session, or "" to follow the active one.
  let listable
    (cycleFor: string -> LiveTestCycleState)
    (registry: SessionRegistryView)
    (perClient: string)
    : TestCase array =
    let sessionId =
      match perClient, ActiveSession.sessionId registry.ActiveSessionId with
      | "", Some active -> SessionId.value active
      | id, _ -> id
    (cycleFor sessionId).TestState.DiscoveredTests
    |> SessionSourceScope.confineToRoots (rootsFor registry.Sessions sessionId)
