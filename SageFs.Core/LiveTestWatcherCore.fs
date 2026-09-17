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

  /// Session ID(s) owning the dir that contains `path` (longest matching dir
  /// prefix wins; the fallback dir claims no session and is not in
  /// DirSessions, so a path under only the fallback dir resolves to []).
  /// Path/dir comparison is normalized (full path, ordinal-ignore-case) to
  /// match the daemon-CWD / session-working-dir comparisons this replaces
  /// (see the pre-mailbox `dirForPath`/`sessionsForPath` in DaemonMode.fs).
  let sessionsForPath (dirSessions: Map<string, SessionId list>) (path: string) : SessionId list =
    let normalizedPath = System.IO.Path.GetFullPath(path)
    dirSessions
    |> Map.toSeq
    |> Seq.filter (fun (dir, _) ->
      normalizedPath.StartsWith(System.IO.Path.GetFullPath(dir), System.StringComparison.OrdinalIgnoreCase))
    |> Seq.sortByDescending (fun (dir, _) -> dir.Length)
    |> Seq.tryHead
    |> Option.map snd
    |> Option.defaultValue []

  /// Whether `path` is under a directory that is CURRENTLY watched (a claimed
  /// session dir OR the session-less fallback dir). The shell uses this, not
  /// `sessionsForPath`, to decide whether to fire FileContentChanged on a drain:
  /// a file under the fallback dir fires FileContentChanged with no session
  /// (the fallback contract), while a save queued before its dir was removed
  /// resolves to a dir no longer in `Watched` and is dropped (the stale case).
  let isUnderWatchedDir (watched: Set<string>) (path: string) : bool =
    let normalizedPath = System.IO.Path.GetFullPath(path)
    watched
    |> Set.exists (fun dir ->
      normalizedPath.StartsWith(System.IO.Path.GetFullPath(dir), System.StringComparison.OrdinalIgnoreCase))
