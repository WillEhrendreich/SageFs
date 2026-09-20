module SageFs.Tests.TestInfrastructure

open SageFs.ActorCreation
open SageFs.AppState
open SageFs.McpTools
open SageFs.WorkflowTypes
open System.Collections.Concurrent
open System.Threading

/// Harness-root Verify configuration — the ONE place that owns the snapshot
/// directory, the unique-prefix setting and the line-ending scrubber. Program.fs
/// calls `configure` before any test runs; snapshot tests call `verify` and never
/// configure Verify themselves (per-file `do try ... with _ -> ()` re-configuration
/// silently swallowed "settings must be set before the first Verify" failures).
module Snapshots =
  /// Committed snapshots live next to the test sources, in every configuration
  /// (Debug/Release, local/CI) — they are never copied or regenerated per build.
  let directory = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "snapshots")

  let private configured =
    lazy (
      VerifyTests.VerifierSettings.DisableRequireUniquePrefix()
      // Normalize CRLF to LF so comparisons are immune to git autocrlf /
      // editor line-ending differences (verified files are committed with LF).
      VerifyTests.VerifierSettings.AddScrubber(fun builder ->
        builder.Replace("\r\n", "\n") |> ignore)
      if not (System.IO.Directory.Exists directory) then
        System.IO.Directory.CreateDirectory directory |> ignore
      VerifyExpecto.Verifier.DerivePathInfo(fun _ _ typeName methodName ->
        VerifyTests.PathInfo(directory = directory, typeName = typeName, methodName = methodName)))

  /// Idempotent: safe to call from every entry point.
  let configure () : unit = configured.Force()

  /// Verify `value` as a `<typeName>.<name>.verified.<extension>` snapshot.
  /// `typeName` is explicit because VerifyExpecto derives it from the CALLER's
  /// source file — which would be this file, not the snapshot test's.
  let verify (typeName: string) (name: string) (extension: string) (value: string) =
    let settings = VerifyTests.VerifySettings()
    settings.UseTypeName typeName
    settings.DisableDiff()
    VerifyExpecto.Verifier.Verify(name, value, extension, settings).ToTask()

/// The repo-built SageFs daemon executable that real-process integration
/// suites spawn. Resolves the platform's apphost name (SageFs.exe on Windows,
/// the extensionless SageFs on Linux/macOS) across Debug and Release outputs,
/// newest build wins. It deliberately NEVER falls back to a bare "SageFs" on
/// PATH: that silently runs whatever (possibly stale) global tool is installed
/// instead of the code under test — and on Linux the global tool is named
/// `sagefs`, so the fallback simply failed to start.
module SageFsBinary =
  let private repoRoot =
    System.IO.Path.GetFullPath(System.IO.Path.Combine(__SOURCE_DIRECTORY__, ".."))

  let private fileName =
    match System.OperatingSystem.IsWindows() with
    | true -> "SageFs.exe"
    | false -> "SageFs"

  /// Every location a repo build can place the daemon executable.
  let candidates =
    [ "Debug"; "Release" ]
    |> List.map (fun cfg ->
      System.IO.Path.Combine(repoRoot, "SageFs", "bin", cfg, "net10.0", fileName))

  /// The newest built executable. When none is built, the first candidate is
  /// returned so Process.Start fails naming the exact expected path.
  let path () =
    candidates
    |> List.filter System.IO.File.Exists
    |> List.sortByDescending System.IO.File.GetLastWriteTimeUtc
    |> List.tryHead
    |> Option.defaultValue candidates.Head

/// Structural registry of [Integration] suites. Every integration suite is
/// registered here together with the runner that owns it, so:
///  - the default run excludes registered suites by the IDENTITY of their test
///    bodies (not by a name convention a suite can forget), and refuses to run
///    if any "[Integration]"-tagged test bypassed the registry;
///  - --integration-host runs EVERY suite registered as Host, so a new
///    self-contained suite joins CI by construction — no curated list.
/// Registration happens in each test file's module initialization, which is
/// LAZY: a file registers only when something touches it (measured: 0 suites
/// registered before assembly discovery, 73 after). `registered` therefore
/// forces discovery once before answering.
module Integration =
  type Runner =
    /// Self-contained: real processes, FSI sessions, daemons on reserved ports
    /// with isolated SAGEFS_DATA_DIRs — runs on a bare CI runner via
    /// --integration-host.
    | Host
    /// Needs infrastructure a dedicated entry point provisions (a browser, VS
    /// Code, a daemon that runner owns); the payload names that entry point.
    | Dedicated of entryPoint: string

  let private registry = System.Collections.Generic.List<Runner * Expecto.Test>()

  /// Register an existing test (list or case) as an integration suite.
  let register (runner: Runner) (test: Expecto.Test) : Expecto.Test =
    lock registry (fun () -> registry.Add((runner, test)))
    test

  let private tagged (name: string) = "[Integration] " + name

  /// A self-contained integration test list (runs under --integration-host).
  let hostList (name: string) (tests: Expecto.Test list) =
    Expecto.Tests.testList (tagged name) tests |> register Host

  /// A self-contained integration test case inside an otherwise-unit list.
  let hostCase (name: string) (body: unit -> unit) =
    Expecto.Tests.testCase (tagged name) body |> register Host

  /// Touch every [<Tests>] value in this assembly so every file's lazy module
  /// initialization — and with it every registration — has run.
  let private discovery =
    lazy (
      Expecto.Impl.testFromAssembly (System.Reflection.Assembly.GetExecutingAssembly())
      |> ignore)

  let registered () =
    discovery.Force()
    lock registry (fun () -> List.ofSeq registry)

  /// Every suite registered as Host, in registration (compile) order.
  let hostSuites () =
    registered ()
    |> List.choose (fun (runner, test) ->
      match runner with
      | Host -> Some test
      | Dedicated _ -> None)

  let rec private leaves (t: Expecto.Test) : Expecto.TestCode list =
    match t with
    | Expecto.TestCase (code, _) -> [ code ]
    | Expecto.TestList (tests, _) -> tests |> List.collect leaves
    | Expecto.TestLabel (_, inner, _) -> leaves inner
    | Expecto.Sequenced (_, inner) -> leaves inner

  /// Remove from `tree` every test whose BODY (its TestCode object) belongs to
  /// one of the `excluded` suites; containers the pruning empties are dropped.
  /// Matching is by reference on the test-body objects, never on names:
  /// Expecto's assembly discovery rebuilds the root node of each [<Tests>]
  /// value but keeps every leaf's TestCode object (measured: all 169
  /// registered leaves found by reference in the discovered tree, while no
  /// registered root node was). Pure — `excluded` is passed in — so it is
  /// testable in isolation.
  let exclude (excluded: Expecto.Test list) (tree: Expecto.Test) : Expecto.Test =
    let bodies = System.Collections.Generic.HashSet<obj>(HashIdentity.Reference)
    for suite in excluded do
      for code in leaves suite do
        bodies.Add(box code) |> ignore
    let rec prune (t: Expecto.Test) : Expecto.Test option =
      match t with
      | Expecto.TestCase (code, _) ->
        match bodies.Contains(box code) with
        | true -> None
        | false -> Some t
      | Expecto.TestList (tests, focus) ->
        match tests |> List.choose prune with
        | [] when not tests.IsEmpty -> None
        | kept -> Some (Expecto.TestList (kept, focus))
      | Expecto.TestLabel (name, inner, focus) ->
        prune inner |> Option.map (fun i -> Expecto.TestLabel (name, i, focus))
      | Expecto.Sequenced (how, inner) ->
        prune inner |> Option.map (fun i -> Expecto.Sequenced (how, i))
    prune tree
    |> Option.defaultValue (Expecto.TestList ([], Expecto.FocusState.Normal))

  /// The default-suite tree: everything except the registered integration suites.
  let excludeRegistered (tree: Expecto.Test) =
    exclude (registered () |> List.map snd) tree

  /// Full names in `tree` that still carry the "[Integration]" tag — tests that
  /// bypassed the registry. The default runner refuses to run while any exist.
  let unregisteredTagged (tree: Expecto.Test) =
    tree
    |> Expecto.Test.toTestCodeList
    |> List.map (fun flat -> String.concat "/" flat.name)
    |> List.filter (fun name -> name.Contains "[Integration]")

  /// Every distinct `Dedicated` entry point currently registered (in
  /// registration order).
  let dedicatedEntryPoints () =
    registered ()
    |> List.choose (fun (runner, _) -> match runner with Dedicated ep -> Some ep | Host -> None)
    |> List.distinct

  /// A `Dedicated` payload that names a real, single-token CLI flag (e.g.
  /// "--integration-hr") rather than a documented on-demand suite whose
  /// payload is a human-readable note (e.g. VscodeExtensionTests.fs's
  /// "--all (on demand: VS Code + a daemon already running on 37749)").
  /// Only bare-flag entry points are expected to have a Program.fs dispatch
  /// branch; an on-demand suite is intentionally reached only via --all,
  /// which runs the whole discovered assembly unfiltered.
  let isBareFlagEntryPoint (entryPoint: string) =
    entryPoint.StartsWith "--" && not (entryPoint.Contains " ")

  /// Bare-flag `Dedicated` entry points with no matching dispatch branch in
  /// `knownEntryPoints` — the same fail-closed discipline `unregisteredTagged`
  /// already applies in the other direction (a suite that bypassed the
  /// registry). Without this, a suite registered as
  /// `Integration.Dedicated "--some-flag"` but never given a Program.fs
  /// dispatch branch would run NOWHERE — not in CI, not locally — while
  /// looking wired from the test file's own header comment (the exact dark-
  /// gate class documented in outcome-gate-sweep.md §2.2/Fact 2).
  let unwiredDedicated (knownEntryPoints: string list) =
    dedicatedEntryPoints ()
    |> List.filter isBareFlagEntryPoint
    |> List.filter (fun ep -> not (List.contains ep knownEntryPoints))

  /// The tree a plain default run (`--summary`, no `--all`/`--integration`)
  /// actually executes: every [<Tests>] value in this assembly, minus the
  /// registered Integration suites and the [Benchmark]-tagged wall-clock perf
  /// tests excluded from the fast default run (roast-5 §12). Program.fs's
  /// default runner and TestCountBadge's README stamp both call this ONE
  /// function so the "how many tests actually ran" count and the "how many
  /// tests does the badge claim" count can never drift apart — previously the
  /// badge counted the whole assembly (including suites the default run never
  /// executes), which is why the badge, the auto-stamp and the real run
  /// reported three different numbers.
  let defaultSuite () =
    Expecto.Impl.testFromThisAssembly ()
    |> Option.defaultValue (Expecto.Tests.testList "empty" [])
    |> excludeRegistered
    |> Expecto.Test.filter
         Expecto.Tests.defaultConfig.joinWith.asString
         (fun z -> not ((Expecto.Tests.defaultConfig.joinWith.format z).Contains "[Benchmark]"))

let quietLogger =
  { new SageFs.Utils.ILogger with
      member _.LogDebug msg = ()
      member _.LogInfo msg = ()
      member _.LogError msg = ()
      member _.LogWarning msg = ()
  }

/// Serialize process-global environment-variable mutations across test lists.
/// Expecto runs test LISTS in parallel, so two lists mutating the same env
/// var (SAGEFS_DEVRELOAD kill-switch tests + HotReloadTool env-reading tests)
/// race: one test's SetEnvironmentVariable can be observed mid-flight by the
/// other, producing intermittent failures that pass in isolation.
let envLock = obj()

let withEnvVar (name: string) (value: string option) (f: unit -> 'T) : 'T =
  lock envLock (fun () ->
    let original = System.Environment.GetEnvironmentVariable(name)
    System.Environment.SetEnvironmentVariable(name, (match value with Some v -> v | None -> null))
    try f ()
    finally
      System.Environment.SetEnvironmentVariable(name, original))

/// Create a temporary file-based SQLite friction store for tests.
/// Each call creates a new database file in the temp directory.
/// The file is NOT automatically cleaned up — tests should delete it if needed,
/// or rely on OS temp cleanup. For most unit tests, leaving small temp files
/// is acceptable (they'll be cleaned eventually).
let tempFrictionStore () : SageFs.Features.FrictionSqlite.FrictionStore =
  let dbPath =
    System.IO.Path.Combine(
      System.IO.Path.GetTempPath(),
      sprintf "sagefs-test-friction-%s.db" (System.Guid.NewGuid().ToString("N")))
  let connStr = sprintf "Data Source=%s" dbPath
  let store = SageFs.Features.FrictionSqlite.Store.create connStr
  match store.Initialize() with
  | Ok () -> store
  | Error err -> failwithf "Failed to initialize test friction store: %s" err

/// Poll a condition with 10ms intervals until it returns true or timeout expires.
/// Returns the final condition value.
let waitFor (timeoutMs: int) (condition: unit -> bool) =
  let sw = System.Diagnostics.Stopwatch.StartNew()
  while not (condition ()) && sw.ElapsedMilliseconds < int64 timeoutMs do
    Thread.Sleep 10
  condition ()

/// Async version of waitFor for task-based tests.
let waitForAsync (timeoutMs: int) (condition: unit -> System.Threading.Tasks.Task<bool>) =
  task {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable result = false
    while not result && sw.ElapsedMilliseconds < int64 timeoutMs do
      let! ok = condition ()
      result <- ok
      if not result then
        do! System.Threading.Tasks.Task.Delay 50
    return result
  }

/// Await a condition with a hard ceiling, without sleep-polling.
/// Yields via Task.Delay so the thread pool is never hogged; returns true only
/// when the condition was satisfied before the ceiling elapsed.
let awaitCondition (timeoutMs: int) (condition: unit -> bool) =
  task {
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let mutable ok = false
    while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
      if condition () then ok <- true
      else do! System.Threading.Tasks.Task.Delay 10
    return ok
  }

/// Await a TaskCompletionSource with a hard ceiling. Completes the TCS with
/// false when the timeout elapses, so a timed-out wait fails the test with a
/// clear signal instead of hanging.
let awaitTcs (timeoutMs: int) (tcs: System.Threading.Tasks.TaskCompletionSource<bool>) =
  task {
    let! winner =
      System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(timeoutMs))
    let completed = obj.ReferenceEquals(winner, tcs.Task)
    if not completed then tcs.TrySetResult false |> ignore
    return completed
  }

/// Single shared actor result for all read-only tests across the entire test suite.
/// Created once on first access, reused everywhere.
let globalActorResult = lazy(
  let args = mkCommonActorArgs quietLogger false ignore SageFs.Args.ProjectLoadConfig.empty true
  createActor args |> Async.AwaitTask |> Async.RunSynchronously
)

/// Create a SessionProxy from a test actor result
let mkProxy (result: ActorResult) : SageFs.WorkerProtocol.SessionProxy =
  fun msg ->
    SageFs.Server.WorkerMain.handleMessage result.Actor result.GetSessionState result.GetEvalStats result.GetStatusMessage result.ProjectRoles (fun () -> SageFs.Features.LiveTesting.LiveTestHookResult.noOp) (fun _ -> ()) (fun () -> [||], []) (fun _ _ -> async { return Result.Error (SageFs.SageFsError.EvalFailed "EvalLiveTestFile not available on this test proxy") }) SageFs.Server.WorkerMain.noAppRuns msg

/// Create a test SessionManagementOps that routes to the global actor
let mkTestSessionOps (result: ActorResult) (sessionId: SageFs.WorkerProtocol.SessionId) : SageFs.SessionManagementOps =
  let proxy = mkProxy result
  { CreateSession = fun _ _ _ -> System.Threading.Tasks.Task.FromResult(Ok "test-session")
    ListSessions = fun () -> System.Threading.Tasks.Task.FromResult("No sessions")
    StopSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "stopped")
    PurgeSession = fun _ -> System.Threading.Tasks.Task.FromResult(Ok "purged")
    RestartSession = fun _ _ -> System.Threading.Tasks.Task.FromResult(Ok "restarted")
    GetProxy = fun _ -> System.Threading.Tasks.Task.FromResult(Some proxy)
    GetSessionInfo = fun _ ->
      System.Threading.Tasks.Task.FromResult(
        Some {
          Id = sessionId
          Name = None
          Projects = []
          WorkingDirectory = ""
          SolutionRoot = None
          Status = SageFs.WorkerProtocol.SessionLifecycleStatus.Ready { Pid = 1; Port = None }
          Workflow = SessionWorkflow.Interactive
          CreatedAt = System.DateTime.UtcNow
          LastActivity = System.DateTime.UtcNow
          ActiveProject = None
          ProjectRoles = []
          App = SageFs.AppRun.AppRunState.NotRunning
        })
    GetAllSessions = fun () -> System.Threading.Tasks.Task.FromResult([])
    UpdateSessionStatus = fun _ _ -> System.Threading.Tasks.Task.FromResult(())
    NotifyWorkerDied = fun _ -> ()
    ClaimRun = SageFs.SessionManagementOps.stub.ClaimRun
    ClaimStop = SageFs.SessionManagementOps.stub.ClaimStop
    AdvanceRun = SageFs.SessionManagementOps.stub.AdvanceRun
    EndAppRun = SageFs.SessionManagementOps.stub.EndAppRun
    AwaitReady = fun _ _ -> System.Threading.Tasks.Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
    SwitchWorkflow = fun _ _ -> System.Threading.Tasks.Task.FromResult(Result.Error (SageFs.SageFsError.HardResetFailed "Not available"))
    GetAdoptedCore = fun _ -> System.Threading.Tasks.Task.FromResult(None) }

/// Create a McpContext backed by the global shared actor
let sharedCtx () =
  let result = globalActorResult.Value
  let sessionId = SageFs.WorkerProtocol.SessionId.newId()
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["test"] <- SageFs.WorkerProtocol.SessionId.value sessionId
  { FrictionStore = Some (tempFrictionStore())
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = mkTestSessionOps result sessionId
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None; RecordEval = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveSnapshotSink = None
    CohortOwner = None } : McpContext

/// Create a McpContext with a custom session ID backed by the global shared actor
let sharedCtxWith (sessionId: SageFs.WorkerProtocol.SessionId) =
  let result = globalActorResult.Value
  let sessionMap = ConcurrentDictionary<string, string>()
  sessionMap.["test"] <- SageFs.WorkerProtocol.SessionId.value sessionId
  { FrictionStore = Some (tempFrictionStore())
    DiagnosticsChanged = result.DiagnosticsChanged
    StateChanged = None
    SessionOps = mkTestSessionOps result sessionId
    SessionMap = sessionMap
    McpPort = 0
    Dispatch = None
    GetElmModel = None
    GetElmRegions = None
    GetWarmupContext = None
    GetFeatureState = None; RecordEval = None
    ActivityTracker = SageFs.AgentActivityTracker.create()
    LiveSnapshotSink = None
    CohortOwner = None } : McpContext
