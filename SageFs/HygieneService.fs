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

/// The gate's state dir, as `scripts/local-gate` names it.
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

/// Scan the machine for one repo and plan the tidy. The expensive part: sizes and git for every leftover.
let take (loc: Locations) (live: LiveFacts) : Snapshot =
  let leftovers = gather (realScan loc live) None
  let plan = Planner.plan leftovers
  { Repo = loc.Repo
    TakenAt = DateTime.UtcNow
    Leftovers = leftovers
    Plan = plan
    Summary = summarize leftovers plan }

/// The last snapshot per repo, refreshed when something changes (daemon start, a session created or stopped), never
/// on a timer. A reply reads it; it never scans.
module Cache =
  let private snapshots = ConcurrentDictionary<string, Snapshot>()
  let private inFlight = ConcurrentDictionary<string, Task<Snapshot>>()

  let tryGet (repo: string) : Snapshot option =
    match snapshots.TryGetValue repo with
    | true, s -> Some s
    | _ -> None

  let put (snapshot: Snapshot) : unit = snapshots.[snapshot.Repo] <- snapshot

  /// Refresh in the background. A refresh already running for the repo is joined, not repeated.
  let refresh (loc: Locations) (live: unit -> LiveFacts) : Task<Snapshot> =
    inFlight.GetOrAdd(
      loc.Repo,
      fun repo ->
        Task.Run(fun () ->
          try
            let snapshot = take loc (live ())
            put snapshot
            snapshot
          finally
            inFlight.TryRemove repo |> ignore))

/// The one-line nudge for a reply, from the cached snapshot of the repo the working directory belongs to. Silent
/// while there is no snapshot yet (it is being taken) or the workspace is tidy.
let nudgeFor (workingDirectory: string) : string option =
  match mainRepoOf workingDirectory with
  | None -> None
  | Some repo ->
    match Cache.tryGet repo with
    | None -> None
    | Some snapshot -> nudge snapshot.Summary

// ─── Tidy ───────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
type TidyOutcome =
  | Tidied of Report * Snapshot
  | NotConfirmed of ConfirmError * Plan

/// Run the Safe steps of the plan that exists now, if it is the plan the caller was shown. Each step looks at its
/// target again first. The cache is refreshed after.
let tidy (loc: Locations) (live: unit -> LiveFacts) (shown: PlanId) : TidyOutcome =
  let scan = realScan loc (live ())
  let leftovers = gather scan None
  let plan = Planner.plan leftovers
  match Confirmation.safeOnly plan shown with
  | Result.Error error -> TidyOutcome.NotConfirmed(error, plan)
  | Result.Ok confirmation ->
    let effects = HygieneEdge.effects (HygieneEdge.realContext loc live)
    let report = Executor.run effects (rootsOf loc) confirmation plan
    let after = take loc (live ())
    Cache.put after
    TidyOutcome.Tidied(report, after)

/// The host cache's own housekeeping: prune hosts nobody has used for `DataRetention.hostCacheMaxAge` that are not the
/// newest of an SDK the daemon resolves and that no process runs from. Same planner, same confirmation, same
/// second look as `tidy`; it only ever looks at host cache entries.
let pruneHostCache (loc: Locations) (live: unit -> LiveFacts) : Report =
  let scan = realScan loc (live ())
  let leftovers = gatherKinds scan [ LeftoverKind.HostCacheEntry ]
  let plan = Planner.plan leftovers
  match Confirmation.safeOnly plan plan.Id with
  | Result.Error _ -> { Executed = []; ReclaimedBytes = 0L }
  | Result.Ok confirmation ->
    let effects = HygieneEdge.effects (HygieneEdge.realContext loc live)
    Executor.run effects (rootsOf loc) confirmation plan
