/// Workspace hygiene as the daemon, the CLI and the dashboard use it: where things live on this machine, what the
/// running daemon knows that a scan cannot (sessions, who made them, the SDKs it resolves), a cached snapshot so
/// a reply can mention the mess without scanning a disk, and the one tidy that runs a confirmed plan.
///
/// The decisions are `WorkspaceHygiene` (pure), the facts are `HygieneGather`, the hands are `HygieneEdge`.
module SageFs.HygieneService

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open SageFs
open SageFs.WorkspaceHygiene
open SageFs.WorkspaceHygieneRender
open SageFs.HygieneGather

// ─── Who made each session ──────────────────────────────────────────────

/// One session the daemon created for an MCP connection. The connection id is the identity; the agent name is
/// only what the agent called itself.
type OwnerRecord =
  { SessionId: string
    WorkingDirectory: string
    AgentName: string
    ConnectionId: string
    CreatedAt: DateTime }

module OwnerLedger =
  let fileName = "session-owners.json"
  let private profile = Json.indented Json.standard
  let private gate = obj ()

  let path (dataDir: string) : string = Path.Combine(dataDir, fileName)

  let read (dataDir: string) : OwnerRecord list =
    try
      match File.Exists(path dataDir) with
      | false -> []
      | true ->
        match Json.deserialize<OwnerRecord list> profile (File.ReadAllText(path dataDir)) with
        | Result.Ok records -> records
        | Result.Error _ -> []
    with _ -> []

  let private write (dataDir: string) (records: OwnerRecord list) : unit =
    try
      Directory.CreateDirectory dataDir |> ignore
      let target = path dataDir
      let tmp = target + ".tmp"
      File.WriteAllText(tmp, Json.serialize profile records)
      File.Move(tmp, target, true)
    with _ -> ()

  /// How many records the ledger keeps, newest first. A record has to outlive its session (a worktree is left
  /// behind after the session that made it stopped), so it is trimmed by count and not removed on stop. A busy
  /// month is a few hundred sessions; this is room for several.
  let maxRecords = 2000

  /// Remember who created a session. Best effort: a ledger that cannot be written only means the plan cannot say who.
  let recordKeeping (limit: int) (dataDir: string) (entry: OwnerRecord) : unit =
    lock gate (fun () ->
      let others = read dataDir |> List.filter (fun r -> r.SessionId <> entry.SessionId)
      let all = others @ [ entry ]
      write dataDir (all |> List.skip (max 0 (List.length all - limit))))

  let record (dataDir: string) (entry: OwnerRecord) : unit = recordKeeping maxRecords dataDir entry

  /// Record a session a connection just created, under the name that connection has been using (else `fallbackName`).
  let recordCreation
    (dataDir: string)
    (tracker: AgentActivityTracker.Tracker)
    (connectionId: string)
    (fallbackName: string)
    (sessionId: string)
    (workingDirectory: string)
    : unit =
    let name =
      AgentActivityTracker.getPresence tracker connectionId
      |> Option.map (fun p -> p.AgentName)
      |> Option.defaultValue fallbackName
    record
      dataDir
      { SessionId = sessionId
        WorkingDirectory = workingDirectory
        AgentName = name
        ConnectionId = connectionId
        CreatedAt = DateTime.UtcNow }

  /// The owners as the scan wants them: by working directory, with whether the connection is still around.
  let toLive (records: OwnerRecord list) (isConnected: string -> bool) : (string * Owner * AgentConnection) list =
    records
    |> List.map (fun r ->
      let connection = match isConnected r.ConnectionId with | true -> AgentConnection.StillConnected | false -> AgentConnection.NotConnected
      r.WorkingDirectory, Owner.CreatedByAgent(r.AgentName, r.ConnectionId), connection)

// ─── Where things are ───────────────────────────────────────────────────

/// The repository a working directory belongs to: the main checkout, also from inside one of its worktrees.
let mainRepoOf (dir: string) : string option =
  match Checkout.classify dir with
  | Checkout.Checkout.MainCheckout root -> Some root
  | Checkout.Checkout.Worktree(root, _) ->
    try
      let text = (File.ReadAllText(Path.Combine(root, ".git"))).Trim()
      let adminDir = match text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) with | true -> text.Substring(7).Trim() | false -> text
      let marker = "/.git/worktrees/"
      match adminDir.Replace('\\', '/').IndexOf(marker, StringComparison.Ordinal) with
      | -1 -> Some root
      | i -> Some(adminDir.Replace('\\', '/').Substring(0, i))
    with _ -> Some root
  | Checkout.Checkout.NotAGitCheckout -> None

/// The gate's state dir, as `scripts/local-gate.fsx` names it.
let gateDir () : string =
  match Environment.GetEnvironmentVariable "SAGEFS_GATE_HOME" with
  | null
  | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".local", "share", "sagefs-gate")
  | value -> value

/// What a daemon may call an orphan process: with its own data dir (`SAGEFS_DATA_DIR`: a test run, an agent's
/// private daemon) only what it started; the user's own daemon, the whole machine.
let processScope () : ProcessScope =
  match Environment.GetEnvironmentVariable "SAGEFS_DATA_DIR" with
  | null
  | "" -> ProcessScope.WholeMachine
  | _ -> ProcessScope.DescendantsOf Environment.ProcessId

let locationsFor (repo: string) : Locations =
  { Repo = repo
    GateDir = gateDir ()
    DataDir = DaemonState.SageFsDir
    HostCacheDir = IsolatedFsiSession.hostCacheRoot ()
    TempDir = Path.GetTempPath().TrimEnd('/', '\\')
    Processes = processScope () }

// ─── What the daemon knows ──────────────────────────────────────────────

let private sdkByDirectory = ConcurrentDictionary<string, string>()

/// The SDK version a session's working directory resolves to (global.json aware). Remembered per directory: it does
/// not change under a running daemon, and asking costs a `dotnet --version`.
let private sdkVersionFor (workingDirectory: string) : string option =
  match sdkByDirectory.TryGetValue workingDirectory with
  | true, v -> Some v
  | _ ->
    match FsiHostBuild.resolveSdkVersion (IsolatedFsiSession.dotnetPath ()) workingDirectory with
    | Result.Ok v ->
      sdkByDirectory.[workingDirectory] <- v
      Some v
    | Result.Error _ -> None

let liveFactsOf (sessions: (string * string) list) (owners: OwnerRecord list) (isConnected: string -> bool) : LiveFacts =
  { Sessions = sessions
    CurrentSdkVersions = sessions |> List.map snd |> List.distinct |> List.choose sdkVersionFor |> List.distinct
    Owners = OwnerLedger.toLive owners isConnected }

// ─── Snapshots ──────────────────────────────────────────────────────────

type Snapshot =
  { Repo: string
    TakenAt: DateTime
    Leftovers: Leftover list
    Plan: Plan
    Summary: Summary }

/// Scan the machine with the given scan and plan the tidy. The expensive part: sizes and git for every leftover. Every
/// git call is awaited, so the scan holds no thread while git works.
let takeFrom (scan: Scan) : Task<Snapshot> =
  task {
    let! leftovers = gather scan None
    let plan = Planner.plan leftovers
    return
      { Repo = scan.Loc.Repo
        TakenAt = DateTime.UtcNow
        Leftovers = leftovers
        Plan = plan
        Summary = summarize leftovers plan }
  }

/// Scan the machine for one repo and plan the tidy.
let take (loc: Locations) (live: LiveFacts) : Task<Snapshot> = takeFrom (realScan loc live)

[<RequireQualifiedAccess>]
type RefreshState =
  | NotRefreshing
  | Refreshing

[<RequireQualifiedAccess>]
type TidyState =
  | NotTidying
  | Tidying

/// What the last tidy did, in numbers a person reads.
type TidySummary =
  { Removed: int
    AlreadyGone: int
    Skipped: int
    Failed: int
    ReclaimedBytes: int64
    At: DateTime }

let summarizeReport (report: Report) : TidySummary =
  let count (matches: StepResult -> bool) = report.Executed |> List.filter (fun e -> matches e.Result) |> List.length
  { Removed = count (function | StepResult.Ran(Outcome.Done _) -> true | _ -> false)
    AlreadyGone = count (fun r -> r = StepResult.AlreadyGone)
    Skipped = count (function | StepResult.Skipped _ -> true | _ -> false)
    Failed = count (function | StepResult.Ran(Outcome.Failed _) -> true | _ -> false)
    ReclaimedBytes = report.ReclaimedBytes
    At = DateTime.UtcNow }

/// The last snapshot per repo, refreshed when something changes (daemon start, a session created or stopped), never
/// on a timer. A reply reads it; it never scans.
module Cache =
  let private snapshots = ConcurrentDictionary<string, Snapshot>()
  let private inFlight = ConcurrentDictionary<string, Task<Snapshot>>()
  let private tidied = ConcurrentDictionary<string, TidySummary>()

  let tryGet (repo: string) : Snapshot option =
    match snapshots.TryGetValue repo with
    | true, s -> Some s
    | _ -> None

  let put (snapshot: Snapshot) : unit = snapshots.[snapshot.Repo] <- snapshot

  /// Whether a scan of this repo is running right now.
  let refreshState (repo: string) : RefreshState =
    match inFlight.ContainsKey repo with
    | true -> RefreshState.Refreshing
    | false -> RefreshState.NotRefreshing

  let lastTidy (repo: string) : TidySummary option =
    match tidied.TryGetValue repo with
    | true, s -> Some s
    | _ -> None

  let setTidied (repo: string) (summary: TidySummary) : unit = tidied.[repo] <- summary
  let clearTidied (repo: string) : unit = tidied.TryRemove repo |> ignore

  let private failures = ConcurrentDictionary<string, string>()
  let private tidying = ConcurrentDictionary<string, DateTime>()

  /// Why the last scan of the repo failed, until a scan succeeds.
  let lastFailure (repo: string) : string option =
    match failures.TryGetValue repo with
    | true, why -> Some why
    | _ -> None

  /// Mark a tidy running (or finished). The dashboard says so at once instead of waiting for it.
  let beginTidy (repo: string) : unit = tidying.[repo] <- DateTime.UtcNow
  let endTidy (repo: string) : unit = tidying.TryRemove repo |> ignore

  let isTidying (repo: string) : TidyState =
    match tidying.ContainsKey repo with
    | true -> TidyState.Tidying
    | false -> TidyState.NotTidying

  /// Refresh in the background. A refresh already running for the repo is joined, not repeated.
  ///
  /// `live` is awaited, never waited on: a caller whose live facts come from a Task passes it here, and the scan starts
  /// once they arrive.
  let refreshAsync (loc: Locations) (live: unit -> Task<LiveFacts>) : Task<Snapshot> =
    // The entry is in the table before the scan starts, so a scan that finishes at once cannot remove it first.
    let mine = TaskCompletionSource<Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously)
    let current = inFlight.GetOrAdd(loc.Repo, mine.Task)
    match obj.ReferenceEquals(current, mine.Task) with
    | false -> current
    | true ->
      Task.Run(fun () ->
        task {
          try
            try
              let! facts = live ()
              let! snapshot = take loc facts
              put snapshot
              failures.TryRemove loc.Repo |> ignore
              mine.SetResult snapshot
            with ex ->
              failures.[loc.Repo] <- ex.Message
              mine.SetException ex
          finally
            inFlight.TryRemove(System.Collections.Generic.KeyValuePair(loc.Repo, mine.Task)) |> ignore
        } :> Task)
      |> ignore
      mine.Task

  /// `refreshAsync` for a caller that already has its live facts, or reads them without waiting.
  let refresh (loc: Locations) (live: unit -> LiveFacts) : Task<Snapshot> =
    refreshAsync loc (fun () -> Task.FromResult(live ()))

/// What the dashboard's hygiene panel shows, a closed set: no repository to look at, one not scanned yet, a scan
/// running, or a scan with what the last tidy did.
[<RequireQualifiedAccess>]
type TidiedBefore =
  | NothingTidiedYet
  | Tidied of TidySummary

[<RequireQualifiedAccess>]
type HygieneView =
  | NoRepository
  | NotScanned of repo: string
  | Scanning of repo: string
  | Tidying of repo: string
  | ScanFailed of repo: string * reason: string
  | Scanned of Snapshot * TidiedBefore

/// The view for the repo a session is in. Reads the cache; never scans.
let viewFor (workingDirectory: string) : HygieneView =
  match mainRepoOf workingDirectory with
  | None -> HygieneView.NoRepository
  | Some repo ->
    match Cache.isTidying repo, Cache.refreshState repo, Cache.tryGet repo, Cache.lastFailure repo with
    | TidyState.Tidying, _, _, _ -> HygieneView.Tidying repo
    | _, RefreshState.Refreshing, _, _ -> HygieneView.Scanning repo
    | _, _, None, Some reason -> HygieneView.ScanFailed(repo, reason)
    | _, _, None, None -> HygieneView.NotScanned repo
    | _, _, Some snapshot, _ ->
      match Cache.lastTidy repo with
      | Some summary -> HygieneView.Scanned(snapshot, TidiedBefore.Tidied summary)
      | None -> HygieneView.Scanned(snapshot, TidiedBefore.NothingTidiedYet)

/// What a person sees as the facts the scan could not read off the disk, from the sessions the daemon runs and who
/// made them (a tracker that knows which connections are still around, or none: then everyone is assumed to be).
let liveFactsWith (sessions: (string * string) list) (tracker: AgentActivityTracker.Tracker option) : LiveFacts =
  let isConnected (connectionId: string) =
    match tracker with
    | Some t -> AgentActivityTracker.getPresence t connectionId |> Option.isSome
    | None -> true
  liveFactsOf sessions (OwnerLedger.read DaemonState.SageFsDir) isConnected

// ─── Tidy ───────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type TidyOutcome =
  | Tidied of Report * Snapshot
  | NotConfirmed of ConfirmError * Plan

/// Run the Safe steps of the plan that exists now, if it is the plan the caller was shown. Each step looks at its
/// target again first. The cache is refreshed after.
let tidy (loc: Locations) (live: unit -> LiveFacts) (shown: PlanId) : Task<TidyOutcome> =
  task {
    let scan = realScan loc (live ())
    let! leftovers = gather scan None
    let plan = Planner.plan leftovers
    match Confirmation.safeOnly plan shown with
    | Result.Error error -> return TidyOutcome.NotConfirmed(error, plan)
    | Result.Ok confirmation ->
      Cache.beginTidy loc.Repo
      try
        let effects = HygieneEdge.effects (HygieneEdge.realContext loc live)
        let! report = Executor.runAsync effects (rootsOf loc) confirmation plan
        let! after = take loc (live ())
        Cache.put after
        Cache.setTidied loc.Repo (summarizeReport report)
        return TidyOutcome.Tidied(report, after)
      finally
        Cache.endTidy loc.Repo
  }

/// The host cache's own housekeeping: prune hosts nobody has used for `DataRetention.hostCacheMaxAge` that are not the
/// newest of an SDK the daemon resolves and that no process runs from. Same planner, same confirmation, same
/// second look as `tidy`; it only ever looks at host cache entries.
let pruneHostCacheWith (ctx: HygieneEdge.EdgeContext) (loc: Locations) : Task<Report> =
  // A host built before sessions marked their use has no `.last-used`, and its directory's own time is when it was
  // built, not when it was last run. Stamp those as used now, so the first prune on an old cache cannot throw away a
  // host somebody ran yesterday; they expire a retention from now unless a session uses them.
  task {
    try
      for dir in Directory.EnumerateDirectories loc.HostCacheDir do
        let marker = Path.Combine(dir, FsiHostBuild.HostLastUsedMarker)
        match File.Exists marker with
        | true -> ()
        | false -> File.WriteAllText(marker, DateTime.UtcNow.ToString "o")
    with _ -> ()
    let! leftovers = gatherKinds (ctx.MakeScan()) [ LeftoverKind.HostCacheEntry ]
    let plan = Planner.plan leftovers
    match Confirmation.safeOnly plan plan.Id with
    | Result.Error _ -> return { Executed = []; ReclaimedBytes = 0L }
    | Result.Ok confirmation -> return! Executor.runAsync (HygieneEdge.effects ctx) (rootsOf loc) confirmation plan
  }

let pruneHostCache (loc: Locations) (live: unit -> LiveFacts) : Task<Report> =
  pruneHostCacheWith (HygieneEdge.realContext loc live) loc

let private pruning : int ref = ref 0

/// Prune the host cache in the background, at most one at a time. Event-driven: the daemon calls it at start and when a
/// session stops, never on a timer. `report` hears what it did.
let pruneHostCacheInBackground (loc: Locations) (live: unit -> LiveFacts) (report: Report -> unit) (failed: exn -> unit) : unit =
  match System.Threading.Interlocked.CompareExchange(&pruning.contents, 1, 0) with
  | 0 ->
    Task.Run(fun () ->
      task {
        try
          try
            let! pruned = pruneHostCache loc live
            report pruned
          with ex -> failed ex
        finally
          System.Threading.Interlocked.Exchange(&pruning.contents, 0) |> ignore
      } :> Task)
    |> ignore
  | _ -> ()
