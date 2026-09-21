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

/// Readiness for a daemon a test spawned itself. "Something answered on the
/// port" is not readiness: ports are reserved then released before the daemon
/// binds, so another suite's daemon can be the one answering, and a daemon
/// binds its MCP port and its dashboard (MCP+1) separately, so one can answer
/// before the other listens. Both mistakes have produced flakes here. The rule
/// is: the dashboard's /api/daemon-info reports the pid we spawned.
module DaemonIdentity =
  /// Does a /api/daemon-info body name this pid?
  let reportsPid (body: string) (pid: int) =
    try
      use doc = System.Text.Json.JsonDocument.Parse body
      doc.RootElement.GetProperty("pid").GetInt32() = pid
    with _ -> false

/// The one port allocator every real-daemon-spawning test harness goes
/// through. A daemon binds two ports — the MCP port it is given, and the
/// dashboard at that port + 1 — so "free" means both are free, proven by
/// binding real loopback listeners and releasing them immediately (the
/// resulting reserve-then-release window is why every caller that actually
/// spawns a daemon re-verifies the answering daemon's own pid afterwards —
/// see `DaemonIdentity` above).
///
/// `ci-pipeline.fsx` runs several test tiers CONCURRENTLY, each in its own
/// checkout clone and its own /tmp, but every tier shares ONE network
/// namespace — mount namespaces do not isolate ports. Before this module
/// existed, every harness that needed a daemon port picked its own
/// "preferred" literal plus a small random offset (`38700 + Random().Next
/// 100`, `39100 + Random.Shared.Next 300`, ...), spread out only enough to
/// avoid the OTHER literals in the SAME process — never another tier's
/// process. Two tiers whose literal ranges overlapped could both
/// reserve-then-release the identical pair in the same instant; the daemon
/// that lost that race kept running and logged "ready" anyway (fixed
/// alongside this module in SageFs/DaemonMode.fs — a failed required bind is
/// now a fatal, non-zero-exit startup failure), and the test that spawned it
/// spent its whole timeout waiting on a port nobody would ever answer on.
///
/// `SAGEFS_TEST_PORT_RANGE=<lo>-<hi>` (set by ci-pipeline.fsx, one disjoint
/// slice of the pool per concurrently-running tier — see build/TierPlan.fs's
/// `portRangeOf`) makes that cross-tier collision structurally impossible:
/// every harness that spawns a daemon scans ONLY inside its own tier's
/// slice. Outside the pipeline (a developer running one tier by hand, the
/// env var absent) scanning falls back to the OS's own ephemeral-port
/// assignment — the pre-existing behaviour.
module TestPorts =
  open System
  open System.Net
  open System.Net.Sockets

  let private rangeEnvVar = "SAGEFS_TEST_PORT_RANGE"

  /// Attempts scanning a caller-assigned range before giving up — wide enough
  /// that a handful of daemons reserving ports at the same moment inside one
  /// tier essentially never exhausts it by chance.
  let private maxRangeAttempts = 200

  /// Attempts asking the OS for an ephemeral port before giving up — mirrors
  /// the pre-existing `HttpApiIntegrationTests.reserveLoopbackPort` fallback:
  /// the port itself is always free (the OS just handed it out), so only its
  /// +1 dashboard neighbour can be taken, which is uncommon.
  let private maxEphemeralAttempts = 20

  let private parseRange (raw: string) =
    match raw.Split('-') with
    | [| lo; hi |] ->
      match Int32.TryParse lo, Int32.TryParse hi with
      | (true, lo), (true, hi) when hi > lo -> Some(lo, hi)
      | _ -> None
    | _ -> None

  /// This tier's assigned slice, or None outside the pipeline.
  let private assignedRange () =
    match Environment.GetEnvironmentVariable rangeEnvVar with
    | null | "" -> None
    | raw -> parseRange raw

  /// Both the mcp port and its dashboard neighbour (port + 1) bind — held
  /// only long enough to prove they are free right now, then released so the
  /// daemon can bind them itself.
  let private tryReservePair (port: int) =
    try
      use mcp = new TcpListener(IPAddress.Loopback, port)
      mcp.Start()
      let bound = (mcp.LocalEndpoint :?> IPEndPoint).Port
      use dashboard = new TcpListener(IPAddress.Loopback, bound + 1)
      dashboard.Start()
      Some bound
    with :? SocketException -> None

  /// One (mcpPort, dashboardPort) pair, free right now.
  let reservePair () : int * int =
    let mcpPort =
      match assignedRange () with
      | Some(lo, hi) ->
        // `hi` is exclusive: a candidate at hi - 1 would push the dashboard
        // neighbour (candidate + 1) to hi — one port past this tier's slice,
        // and, at a slice boundary, into the next tier's.
        let span = max 1 (hi - lo - 1)
        Seq.init maxRangeAttempts (fun _ -> lo + Random.Shared.Next span)
        |> Seq.choose tryReservePair
        |> Seq.tryHead
        |> Option.defaultWith (fun () ->
          failwithf "TestPorts.reservePair: no free port pair in the assigned range %d-%d (%s)" lo hi rangeEnvVar)
      | None ->
        Seq.init maxEphemeralAttempts (fun _ -> tryReservePair 0)
        |> Seq.tryPick id
        |> Option.defaultWith (fun () ->
          failwith "TestPorts.reservePair: unable to reserve a free ephemeral loopback port pair")
    mcpPort, mcpPort + 1

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

  /// An integration test case whose subject is a capability that does NOT work
  /// yet, so it cannot gate the main pipeline — but which must stay runnable,
  /// named, and un-weakened, so the day the capability lands it goes green on
  /// its own. `entryPoint` is the CLI flag that runs it; Program.fs fails
  /// closed if no dispatch branch exists for it, which is what keeps this from
  /// degrading into the silent no-op gate this repo has been bitten by before.
  let dedicatedCase (entryPoint: string) (name: string) (body: unit -> unit) =
    Expecto.Tests.testCase (tagged name) body |> register (Dedicated entryPoint)

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

  /// The `Dedicated` entry points Program.fs has a dispatch branch for. ONE
  /// list, read by Program.fs's fail-closed `unwiredDedicated` check and by the
  /// "every tier is invoked by CI" structural test — so "dispatched" and
  /// "invoked" are checked against the same set rather than two hand copies.
  let dispatchedEntryPoints =
    [ "--integration-browser"; "--integration-hr"; "--integration-lt"; "--integration-disconnect" ]

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

/// One trust signal for every test tier (default, host, each dedicated entry
/// point, mutation).
///
/// An exit code alone has repeatedly hidden the thing that mattered:
///  - a filter that matches nothing exits 0 having run nothing (AGENTS.md);
///  - a runner can execute a different list than the one its suites registered;
///  - a runner can die in setup before any test ran, which the pipeline then
///    reads as "that stage failed" while every later stage never ran at all.
/// So every tier runs through `run`, which captures Expecto's own summary,
/// compares what RAN against what was REGISTERED, prints one `TRUST` line and
/// appends one JSON row to the ledger named by SAGEFS_TRUST_LEDGER. The CI
/// pipeline's final stage renders that ledger as one table and fails on any
/// row that is not Trusted — or on any tier that wrote no row.
module TrustSignal =
  type Tally =
    { Passed: int; Failed: int; Errored: int; Ignored: int }
    member t.Ran = t.Passed + t.Failed + t.Errored + t.Ignored

  /// Whether the run was shaped to be an acceptance signal. A filter, a `--run`
  /// selection or a stress loop narrows or repeats the tree, so its count can
  /// never be compared against the registered set.
  type Scope =
    | Acceptance
    | Narrowed

  type Verdict =
    /// Unfiltered, everything registered ran, nothing failed.
    | Trusted
    /// Passed, but filtered — an inner-loop result, never an acceptance check.
    | NarrowedRun
    | TestsFailed of failed: int * errored: int
    /// Zero tests executed. Expecto exits 0 for this; we do not.
    | NothingRan
    /// Unfiltered, yet the executed count differs from the registered count:
    /// the runner ran some other tree than the one its suites registered
    /// (or a focused test silently narrowed it).
    | CountMismatch of registered: int * ran: int
    /// Mutation tier only: the score meets its threshold, yet mutants
    /// survived. Not a failure (Program.fs explains why the bar, not every
    /// survivor, gates) — but reported as its own verdict so a green table
    /// can never hide surviving mutants.
    | SurvivorsUnderBar of survived: int

  module Verdict =
    let name (v: Verdict) =
      match v with
      | Trusted -> "Trusted"
      | NarrowedRun -> "NarrowedRun"
      | TestsFailed _ -> "TestsFailed"
      | NothingRan -> "NothingRan"
      | CountMismatch _ -> "CountMismatch"
      | SurvivorsUnderBar _ -> "SurvivorsUnderBar"

    let describe (v: Verdict) =
      match v with
      | Trusted -> "every registered test ran and passed"
      | NarrowedRun -> "passed, but filtered: not an acceptance signal"
      | TestsFailed (f, e) -> sprintf "%d failed, %d errored" f e
      | NothingRan -> "zero tests executed"
      | CountMismatch (r, n) -> sprintf "registered %d but %d ran" r n
      | SurvivorsUnderBar n -> sprintf "score meets the threshold, but %d mutant(s) survived" n

    /// Exit code for the verdict. `NothingRan` and `CountMismatch` get their
    /// own code (3) so they are never mistaken for Expecto's 1/2.
    let exitCode (expectoExit: int) (v: Verdict) =
      match v with
      | Trusted | NarrowedRun | SurvivorsUnderBar _ -> expectoExit
      | TestsFailed _ -> match expectoExit with 0 -> 1 | code -> code
      | NothingRan | CountMismatch _ -> 3

  let private narrowingFlags =
    set [ "--filter"; "--filter-test-list"; "--filter-test-case"; "--run"; "--stress" ]

  let scopeOf (argv: string array) =
    match argv |> Array.exists narrowingFlags.Contains with
    | true -> Narrowed
    | false -> Acceptance

  /// Pure: the verdict for a run. A failure outranks everything; an empty run
  /// is never green, filtered or not; only an acceptance-shaped run can be
  /// Trusted, and only when it ran exactly what was registered.
  let judge (scope: Scope) (registered: int) (tally: Tally) : Verdict =
    match tally with
    | t when t.Failed > 0 || t.Errored > 0 -> TestsFailed (t.Failed, t.Errored)
    | t when t.Ran = 0 -> NothingRan
    | t ->
      match scope with
      | Narrowed -> NarrowedRun
      | Acceptance when t.Ran <> registered -> CountMismatch (registered, t.Ran)
      | Acceptance -> Trusted

  /// One ledger row. Plain record so the JSON shape is the field list.
  type Row =
    { Tier: string
      Registered: int
      Ran: int
      Passed: int
      Failed: int
      Errored: int
      Ignored: int
      Verdict: string
      Detail: string
      ExitCode: int }

  let LedgerEnvironmentVariable = "SAGEFS_TRUST_LEDGER"

  /// Print the row and append it to the ledger (when one is configured).
  let record (row: Row) =
    printfn "TRUST tier=%s registered=%d ran=%d passed=%d failed=%d errored=%d ignored=%d verdict=%s (%s)"
      row.Tier row.Registered row.Ran row.Passed row.Failed row.Errored row.Ignored row.Verdict row.Detail
    match System.Environment.GetEnvironmentVariable LedgerEnvironmentVariable with
    | null | "" -> ()
    | path ->
      let line = System.Text.Json.JsonSerializer.Serialize row
      lock LedgerEnvironmentVariable (fun () -> System.IO.File.AppendAllText(path, line + "\n"))

  let rowOf (tier: string) (registered: int) (tally: Tally) (verdict: Verdict) (exitCode: int) =
    { Tier = tier
      Registered = registered
      Ran = tally.Ran
      Passed = tally.Passed
      Failed = tally.Failed
      Errored = tally.Errored
      Ignored = tally.Ignored
      Verdict = Verdict.name verdict
      Detail = Verdict.describe verdict
      ExitCode = exitCode }

  let registeredCount (tests: Expecto.Test) = tests |> Expecto.Test.toTestCodeList |> List.length

  /// Run `tests` as `tier` and return the exit code the verdict demands.
  /// `--list-tests` executes nothing by design, so it bypasses judgement.
  let runObserved
    (report: Row -> unit)
    (observe: Expecto.Impl.TestRunSummary -> unit)
    (tier: string)
    (argv: string array)
    (tests: Expecto.Test)
    : int =
    match argv |> Array.contains "--list-tests" with
    | true -> Expecto.Tests.runTestsWithCLIArgs [] argv tests
    | false ->
      let summary = ref None
      let handler =
        Expecto.Tests.CLIArguments.Append_Summary_Handler(
          Expecto.Tests.SummaryHandler(fun s ->
            summary.Value <- Some s
            observe s))
      let expectoExit = Expecto.Tests.runTestsWithCLIArgs [ handler ] argv tests
      let tally =
        match summary.Value with
        | Some s ->
          { Passed = s.passed.Length; Failed = s.failed.Length; Errored = s.errored.Length; Ignored = s.ignored.Length }
        | None -> { Passed = 0; Failed = 0; Errored = 0; Ignored = 0 }
      let registered = registeredCount tests
      let verdict = judge (scopeOf argv) registered tally
      let exitCode = Verdict.exitCode expectoExit verdict
      report (rowOf tier registered tally verdict exitCode)
      // Where this tier's time went, in every log, so the next speed-up is
      // chosen from data: the slowest single tests, and the heaviest top-level
      // lists (the unit a shard or a split would move).
      match summary.Value with
      | Some s ->
        let timed = s.passed @ s.failed @ s.errored
        printfn "SLOWEST tests in %s:" tier
        timed
        |> List.sortByDescending (fun (_, t) -> t.duration)
        |> List.truncate 15
        |> List.iter (fun (flat, t) ->
          printfn "  %7.1fs  %s" t.duration.TotalSeconds (String.concat " / " flat.name))
        printfn "HEAVIEST lists in %s:" tier
        timed
        |> List.groupBy (fun (flat, _) -> match flat.name with root :: _ -> root | [] -> "")
        |> List.map (fun (root, xs) -> root, xs |> List.sumBy (fun (_, t) -> t.duration.TotalSeconds), xs.Length)
        |> List.sortByDescending (fun (_, seconds, _) -> seconds)
        |> List.truncate 10
        |> List.iter (fun (root, seconds, n) -> printfn "  %7.1fs  %4d tests  %s" seconds n root)
      | None -> ()
      exitCode

  /// The argument strings of every `testTier "<args>"` / `testTierAfter [..]
  /// "<args>"` step in ci-pipeline.fsx text. ONE parser, shared by
  /// TrustSignalTests and the Definition-of-Done probe, so "does CI invoke this
  /// runner" cannot mean two different things in two places.
  let pipelineTierArgs (pipelineText: string) : string list =
    System.Text.RegularExpressions.Regex.Matches(
      pipelineText, "testTier(?:After\\s*\\[[^\\]]*\\])?\\s*\\$?\"([^\"]+)\"")
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> List.ofSeq

  /// Tier name of an argument string: its first token, with the bare
  /// `--summary` default run named "default" (matches the ledger's Tier field).
  let tierOfArgs (args: string) =
    match args.Split(' ')[0] with
    | "--summary" -> "default"
    | flag -> flag

  let runReporting (report: Row -> unit) (tier: string) (argv: string array) (tests: Expecto.Test) : int =
    runObserved report ignore tier argv tests

  /// `runReporting` with the real sink: print the TRUST line and append to the ledger.
  let run (tier: string) (argv: string array) (tests: Expecto.Test) : int = runReporting record tier argv tests

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
