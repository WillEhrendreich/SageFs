#!/usr/bin/env -S dotnet fsi
// ci-pipeline.fsx
//
// THIS SCRIPT IS THE CI PIPELINE. GitHub Actions (.github/workflows/main.yml)
// supplies only the runner, the two checkouts, and the SDK/Node/xvfb install; it
// encodes no build/test/pack logic of its own. The exact same stages run
// locally and in CI:
//
//   dotnet fsi ci-pipeline.fsx             # build, format, unit + integration
//   dotnet fsi ci-pipeline.fsx -- ci       # + the mutation-score gate
//   dotnet fsi ci-pipeline.fsx -- ci release # + VSIX + nupkg + release manifest
//
// The design goal (the whole reason this replaces a page of YAML): build the
// solution ONCE in Release, then run every downstream check --no-build off that
// single output, and PROMOTE the packed artifacts rather than a separate job
// rebuilding them. It runs entirely on ONE Linux runner — the real-daemon
// integration-host suites and the VS Code command-proof were verified green on
// Linux (Args.resolveHostLaunch launches the FSI host via the dotnet muxer on
// non-Windows; the proof self-provisions a Linux VS Code under xvfb), so the
// former windows leg is gone. No more re-packing the forked MCP SDK five times,
// no more building the solution once per job, no separate build/integration/
// extensions/benchmarks/release jobs each from a clean checkout.
//
// Stage selection:
//   * unconditional — restore, build, format, samples, VS Code extension
//     compile + test-electron host + client contract tests (npm run
//     test:golden + every sagefs-vscode/tests/*.fsx), then "test tiers": the
//     default suite and the integration-host suites.
//   * `ci` adds to "test tiers" — the mutation-score gate and every real-browser
//                            journey (dashboard, hot-reload, live-testing,
//                            disconnect-indicator) — CI-gated so the fast
//                            local loop never fetches a browser.
//   * "test tiers" runs every tier regardless of the others; concurrently, each
//     in a private copy-on-write clone, where the machine supports it (see the
//     tier scheduler section and build/TierPlan.fs).
//   * always, after the tiers — the "trust report": one table of every tier's
//     registered/ran/verdict; the one place a red test tier fails the run.
//   * whenCmdArg "release" — pack the shippable bundle + write release-manifest.
//
// Cross-platform packing is safe: every per-RID tree-sitter native is committed
// under runtimes/ and the fsproj includes them by Condition="Exists(...)", so
// the single Linux pack produces a complete cross-platform nupkg. (The Windows
// user gets the FSI host via the dotnet muxer rather than a native
// SageFs.Host.exe — the Unix-proven launch path; if a native Windows apphost is
// ever wanted in the package, cross-publish it with `dotnet publish -r win-x64`,
// which works from Linux.)

#r "nuget: Fun.Build, 1.2.0"

open System
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.Json
open System.Xml.Linq
open Fun.Build
open Fun.Build.Github

let rootDir = __SOURCE_DIRECTORY__
let mcpSdkDir = Path.Combine(rootDir, "mcp-sdk")
let mcpNupkgDir = Path.Combine(rootDir, "mcp-sdk-nupkg")
let harmonyDir = Path.Combine(rootDir, "harmony-fork")
let harmonyNupkgDir = Path.Combine(rootDir, "harmony-nupkg")
let harmonyRepoUrl = "https://github.com/WillEhrendreich/LibHarmony.git"
// Pinned fork commit + the pack's SageFsBuild number; keep in sync with SageFs.Harmony's version in Directory.Packages.props.
let harmonyCommit = "ffb6e9cabd1b83d4a51ef02fcaa914bf1269e51d"
let harmonyBuild = "1"
let harmonyNupkg = Path.Combine(harmonyNupkgDir, $"SageFs.Harmony.2.4.2-sagefs.{harmonyBuild}.nupkg")
let mcpSdkRepoUrl = "https://github.com/WillEhrendreich/ModelContextProtocolSdk.git"
let mcpSdkProjectDir name = Path.Combine(mcpSdkDir, "src", name)
let mcpSdkCoreDir = mcpSdkProjectDir "ModelContextProtocol.Core"
let mcpSdkClientDir = mcpSdkProjectDir "ModelContextProtocol"
let mcpSdkAspNetCoreDir = mcpSdkProjectDir "ModelContextProtocol.AspNetCore"
let releaseDir = Path.Combine(rootDir, "release")
let vscodeDir = Path.Combine(rootDir, "sagefs-vscode")

// ---- release helpers (faithful F# translations of the old pwsh steps) --------

/// The single source-of-truth version, from Directory.Build.props.
let propsVersion () =
  let doc = XDocument.Load(Path.Combine(rootDir, "Directory.Build.props"))
  doc.Descendants()
  |> Seq.tryFind (fun e -> e.Name.LocalName = "Version")
  |> Option.map (fun e -> e.Value.Trim())
  |> Option.defaultWith (fun () -> failwith "Directory.Build.props: no <Version> element found")

/// The VS Code extension version, from its package.json.
let pkgJsonVersion () =
  use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(vscodeDir, "package.json")))
  doc.RootElement.GetProperty("version").GetString()

let sha256Hex (path: string) =
  use s = File.OpenRead path
  use sha = SHA256.Create()
  (sha.ComputeHash s |> Array.map (fun b -> b.ToString("x2")) |> String.concat "")

/// Fail if the packaged VSIX version would drift from the NuGet/tool version —
/// publish locates the VSIX by the Directory.Build.props version, so a mismatch
/// only surfaces after a full build (observed 2026-08: props vs package.json).
let verifyVersionAlignment () =
  let p, k = propsVersion (), pkgJsonVersion ()
  if p <> k then
    failwithf
      "Version drift: Directory.Build.props is %s but sagefs-vscode/package.json is %s. Run dotnet fsi scripts/bump-version.fsx, or set package.json to %s."
      p k p
  printfn "Versions aligned at %s" p

/// issue #131: a tool packaged for the wrong TFM is uninstallable on net10 SDKs
/// ("DotnetToolSettings.xml was not found"). SageFs now multi-targets
/// net10.0;net11.0 (Directory.Build.props' SageFsTargetFrameworks) precisely so
/// this can never recur in either direction: `dotnet pack` on a multi-targeted
/// PackAsTool project emits one tools/<tfm>/any payload per TFM, and
/// `dotnet tool install` picks the payload matching the CALLER's own SDK — a
/// .NET 10 SDK user gets the net10.0 build, a .NET 11 SDK user gets the
/// net11.0 build. Fail HERE, before anything ships, if either payload is
/// missing or an unexpected extra TFM shows up.
let requiredToolTfms = [ "net10.0"; "net11.0" ]
let verifyToolInstallable () =
  // The package for THIS build's version, never whichever nupkg sorts first.
  let expected = Path.Combine(releaseDir, $"SageFs.{pkgJsonVersion ()}.nupkg")
  match File.Exists expected with
  | false -> failwithf "No %s in release/. Did pack fail?" (Path.GetFileName expected)
  | true ->
    let nupkg = expected
    use zip = ZipFile.OpenRead nupkg
    let tfms =
      zip.Entries
      |> Seq.map (fun e -> e.FullName)
      |> Seq.filter (fun n -> n.StartsWith "tools/" && n.EndsWith "/any/DotnetToolSettings.xml")
      |> Seq.map (fun n -> (n.Split('/')).[1])
      |> Seq.distinct
      |> Seq.toList
    // nuget.org refuses anything over 250 MB with a 413, and that only shows up
    // at the publish step, after the VS Code marketplaces already took the
    // release. Catch it here. The margin leaves room for ordinary growth.
    let nugetLimitBytes = 250L * 1024L * 1024L
    let size = FileInfo(nupkg).Length
    match size < nugetLimitBytes * 9L / 10L with
    | false ->
      failwithf "%s is %d MB. nuget.org rejects packages over 250 MB, and this gate wants 10%% headroom." (Path.GetFileName nupkg) (size / 1048576L)
    | true -> ()
    // SageFs loads exactly one tree-sitter grammar, its own F# one. Any other
    // grammar in the package is dead weight that got copied in from
    // TreeSitter.DotNet (Directory.Build.targets strips them).
    let strayGrammars =
      zip.Entries
      |> Seq.map (fun e -> Path.GetFileName e.FullName)
      |> Seq.filter (fun f ->
        (f.StartsWith "tree-sitter-" || f.StartsWith "libtree-sitter-")
        && not (f.Contains "tree-sitter-fsharp"))
      |> Seq.distinct
      |> Seq.toList
    match strayGrammars with
    | [] -> ()
    | stray -> failwithf "%s bundles tree-sitter grammars SageFs never loads: %s" (Path.GetFileName nupkg) (String.concat ", " stray)
    let missing = requiredToolTfms |> List.filter (fun t -> not (List.contains t tfms))
    let unexpected = tfms |> List.filter (fun t -> not (List.contains t requiredToolTfms))
    match missing, unexpected with
    | [], [] ->
      printfn "OK %s installs on both %s SDKs." (Path.GetFileName nupkg) (String.concat " and " requiredToolTfms)
    | missing, [] ->
      failwithf "DotnetToolSettings.xml missing for %s in %s — those SDK users' installs will fail (issue #131)." (String.concat ", " missing) (Path.GetFileName nupkg)
    | [], unexpected ->
      failwithf "Tool package also targets unexpected TFM(s) %s in %s — the repo must pack exactly %s." (String.concat ", " unexpected) (Path.GetFileName nupkg) (String.concat ", " requiredToolTfms)
    | missing, unexpected ->
      failwithf "Tool package TFM mismatch in %s: missing %s, unexpected %s." (Path.GetFileName nupkg) (String.concat ", " missing) (String.concat ", " unexpected)

/// Write release/release-manifest.json in the exact shape publish.yml consumes
/// (version + sourceSha + per-file sha256). The GitHub context comes in as env.
let writeReleaseManifest () =
  let envOr k = match Environment.GetEnvironmentVariable k with null -> "" | v -> v
  let files =
    Directory.GetFiles releaseDir
    |> Array.filter (fun f -> let e = Path.GetExtension f in e = ".nupkg" || e = ".vsix")
    |> Array.sortBy Path.GetFileName
  if files.Length < 2 then
    failwithf "Expected release files were not staged (found %d; need the nupkg and the vsix)." files.Length
  let manifest =
    {| version = propsVersion ()
       sourceSha = envOr "SOURCE_SHA"
       sourceRunId = envOr "SOURCE_RUN_ID"
       sourceRunAttempt = envOr "SOURCE_RUN_ATTEMPT"
       files = files |> Array.map (fun f -> {| name = Path.GetFileName f; sha256 = sha256Hex f |}) |}
  let json = JsonSerializer.Serialize(manifest, JsonSerializerOptions(WriteIndented = true))
  File.WriteAllText(Path.Combine(releaseDir, "release-manifest.json"), json)
  printfn "Wrote release-manifest.json for %s (%d files)" manifest.version files.Length

// ---- the trust ledger and the tier scheduler -----------------------------------
//
// Every test tier is declared with `testTier` and run by `runTiers`. Things that
// used to lose information or time:
//  * the pipeline stopped at the FIRST red test stage, so every later tier went
//    unrun and unreported — `integration host` stayed red for a day while two
//    later tiers were broken the whole time and nobody could see it;
//  * a stage's exit code was the only signal, and Expecto exits 0 for a run
//    that executed nothing;
//  * tiers ran one after another, so the gate took the SUM of every tier.
// Now every tier runs regardless of the others, each writes one registered/ran/
// verdict row to the ledger (SageFs.Tests TestInfrastructure.TrustSignal), and
// the "trust report" stage joins those rows with each tier's exit into ONE table
// and fails the pipeline on any tier that is not Trusted — including a tier whose
// process died before it could report. Where the filesystem can clone
// copy-on-write, tiers run CONCURRENTLY, each in a private clone of the built
// checkout mounted at the checkout's own path (build/TierPlan.fs explains why
// the mount is required). A tier starts when the machine has room for it (cpu pressure
// under a bound, memory for its recorded peak plus a reserve, a cap on processes: TierPlan.advance,
// proven by SageFs.Simulation.TierSched), the one that holds up the end of the run first
// (TierSchedule.orderByCriticalPath). `TrustSignalTests` fails
// the fast suite if a registered tier is not declared here, or if a test run
// bypasses `testTier`.

#load "build/TierPlan.fs"
#load "build/TierCost.fs"
#load "build/FailureReport.fs"
#load "build/TierSchedule.fs"
#load "build/BuildStamps.fs"
#load "build/PassRecord.fs"
open SageFs.Build

// Every downstream check runs against the ONE Release build of the primary
// framework (see "build" stage). The default suite also runs on the other
// frameworks the tool ships for (see `testTierOn`); the "build other frameworks"
// stage builds the test assembly for each of those too, before any tier starts.
let testBinDir = TierPlan.testBinDirOf TierPlan.Framework.primary
let testDll = TierPlan.dllOf TierPlan.Framework.primary

let trustLedger = Path.Combine(rootDir, "test-results", "trust-ledger.jsonl")
Directory.CreateDirectory(Path.GetDirectoryName trustLedger) |> ignore
if File.Exists trustLedger then File.Delete trustLedger

/// (tier, args, did the tier's process exit 0) for every tier this pipeline ran.
let invokedTiers = Collections.Generic.List<string * string * bool>()

let tierNameOf = TierPlan.nameOfArgs

/// Declare a test tier: one `dotnet SageFs.Tests.dll <args>` invocation.
let testTier (args: string) = TierPlan.tier args

/// Declare a tier that runs on another framework than the primary one: the same
/// argv against that framework's own build, under its own ledger row
/// (`default-net10`), so a failure that exists on one runtime only is its own
/// red row. `TrustSignalTests` requires the default suite on every framework in
/// Directory.Build.props' SageFsTargetFrameworks.
let testTierOn (framework: TierPlan.Framework) (args: string) = TierPlan.tierOn framework args

/// Per-tier scratch, OUTSIDE the checkout: inside a tier's mount namespace the
/// checkout path shows that tier's clone, so anything the parent must read back
/// (ledger rows, logs) has to live elsewhere.
let tierWork = Path.Combine(Path.GetDirectoryName rootDir, Path.GetFileName rootDir + ".tiers")

/// Recorded per-tier durations, for longest-first ordering. The local gate
/// points this at its persistent state; elsewhere it lives with the results.
let durationsFile =
  match Environment.GetEnvironmentVariable "SAGEFS_TIER_HISTORY" with
  | null | "" -> Path.Combine(rootDir, "test-results", "tier-durations.json")
  | path -> path

/// Recorded per-SUITE seconds for the sharded host tier (TierPlan.assign).
let suiteDurationsFile =
  match Environment.GetEnvironmentVariable "SAGEFS_SUITE_HISTORY" with
  | null | "" -> Path.Combine(tierWork, "suite-durations.json")
  | path -> path

/// One FSI host cache for every tier, prebuilt once before any tier starts.
let sharedHostCache = Path.Combine(tierWork, "fsihost-cache")

let readJsonMap (path: string) : Map<string, float> =
  try JsonSerializer.Deserialize<Map<string, float>>(File.ReadAllText path)
  with _ -> Map.empty

let readDurations () = readJsonMap durationsFile

/// Run argv to completion with output drained to `log` (files cannot deadlock
/// a child the way an undrained pipe can). Returns the exit code.
/// Exit code for a tier the pipeline had to kill (same number `timeout(1)` uses).
let killedExitCode = 124

/// Run argv, streaming both pipes to `log`. The whole process tree is killed
/// on timeout or cancellation. It used to just stop waiting, and a hung tier
/// kept running for an hour after the stage was cancelled.
/// What a caller can hang on a running process: a look at every output line (the streaming failure detector),
/// and a way to learn how to kill the whole thing from outside (the fail-fast cancel).
type Watch =
  { /// Called with every raw output line, after it is in the log. Never under the log's lock.
    OnLine: string -> unit
    /// Called once the process is running, with the function that kills it and everything it started.
    OnStarted: (unit -> unit) -> unit }

let noWatch = { OnLine = ignore; OnStarted = ignore }

let execToLogWatching (watch: Watch) (timeout: TimeSpan) (workingDir: string) (env: (string * string) list) (log: string) (argv: string list) =
  async {
    let! ct = Async.CancellationToken
    let psi = Diagnostics.ProcessStartInfo(List.head argv)
    List.tail argv |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- workingDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    for (k, v) in env do psi.Environment[k] <- v
    use writer = new StreamWriter(log, false)
    let gate = obj ()
    let write (line: string) =
      if not (isNull line) then
        lock gate (fun () -> writer.WriteLine line)
        watch.OnLine line
    use p = new Diagnostics.Process(StartInfo = psi)
    p.OutputDataReceived.Add(fun e -> write e.Data)
    p.ErrorDataReceived.Add(fun e -> write e.Data)
    p.Start() |> ignore
    p.BeginOutputReadLine()
    p.BeginErrorReadLine()
    let killTree () = try p.Kill(entireProcessTree = true) with _ -> ()
    watch.OnStarted killTree
    use _ = ct.Register(fun () -> killTree ())
    let exited = p.WaitForExitAsync()
    let! finished = Threading.Tasks.Task.WhenAny(exited, Threading.Tasks.Task.Delay timeout) |> Async.AwaitTask
    match obj.ReferenceEquals(finished, exited) with
    | true ->
      p.WaitForExit() // flush the async readers
      return p.ExitCode
    | false ->
      killTree ()
      p.WaitForExit()
      write (sprintf "KILLED: no exit after %.0fs. Timed out, not failed; see the log above for where it stopped." timeout.TotalSeconds)
      return killedExitCode
  }

let execToLog = execToLogWatching noWatch

let private exitOf (argv: string list) =
  try
    let psi = Diagnostics.ProcessStartInfo(List.head argv)
    List.tail argv |> List.iter psi.ArgumentList.Add
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Diagnostics.Process.Start psi
    p.WaitForExit()
    p.ExitCode
  with _ -> -1

/// CopyOnWrite only when BOTH a reflink clone and a rootless mount namespace
/// actually work here — probed, never assumed. Anything else runs serially.
let detectIsolation () =
  try
    Directory.CreateDirectory tierWork |> ignore
    let probe = Path.Combine(tierWork, ".reflink-probe")
    File.WriteAllText(probe, "probe")
    let reflink = exitOf [ "cp"; "--reflink=always"; probe; probe + ".clone" ] = 0
    let userns = exitOf [ "unshare"; "--user"; "--map-root-user"; "--mount"; "--"; "true" ] = 0
    for f in [ probe; probe + ".clone" ] do (try File.Delete f with _ -> ())
    // A checkout under /tmp would be hidden by the tier's private /tmp mount.
    let checkoutOutsideTmp = not (rootDir.StartsWith "/tmp/")
    match reflink && userns && checkoutOutsideTmp with
    | true -> TierPlan.CopyOnWrite
    | false -> TierPlan.Shared
  with _ -> TierPlan.Shared

let private procId (field: string) =
  File.ReadAllLines "/proc/self/status"
  |> Array.find (fun l -> l.StartsWith(field + ":"))
  |> fun l -> int (l.Split([| '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries).[1])

/// Whether a transient systemd user scope can be made here, probed and never assumed. With one, every tier
/// runs inside its own scope and reports what its whole process tree cost (CPU, peak memory); without one
/// the tier runs as before and reports no cost.
let detectAccounting () =
  exitOf [ "systemd-run"; "--user"; "--scope"; "--quiet"; "true" ] = 0

/// What each tier cost this run, by tier name. Persisted beside the durations for the next run.
let tierCosts = Collections.Concurrent.ConcurrentDictionary<string, TierCost.Cost>()

let accountingAvailable = lazy (detectAccounting ())

let costsFile = Path.ChangeExtension(durationsFile, ".costs.json")

// ---- what the scheduler knows and what it reads off the machine -------------------------------------
//
// History is the three files the pipeline keeps beside its results (wall seconds per tier, cost per tier, seconds per
// host suite). A file that is missing or unreadable is an empty map, so a first run, a new checkout or a damaged file
// schedules in the declared order and assumes every unit's memory: slower to plan, never wrong.

let readCosts () : Map<string, TierCost.Cost> =
  try JsonSerializer.Deserialize<Map<string, TierCost.Cost>>(File.ReadAllText costsFile)
  with _ -> Map.empty

let readHistory () : TierSchedule.History =
  { Wall = readDurations (); Costs = readCosts () }

let readProc (path: string) : Result<string, string> =
  try Result.Ok (File.ReadAllText path)
  with e -> Result.Error (sprintf "%s: %s" path e.Message)

/// The machine as /proc reports it. A file that cannot be read is a reading that says so, never an idle machine.
let machinePort : TierPlan.MachinePort =
  { Read =
      fun () ->
        { Pressure =
            (match readProc "/proc/pressure/cpu" with
             | Result.Ok text -> TierPlan.parseCpuPressure text
             | Result.Error reason -> TierPlan.NotMeasured reason)
          Memory =
            (match readProc "/proc/meminfo" with
             | Result.Ok text -> TierPlan.parseMemAvailable text
             | Result.Error reason -> TierPlan.Unreadable reason) } }

/// How many cases every host suite registers when none is left out, from a `--list-tests` of the whole host tier. The
/// shards' own counts must add up to this: a suite no shard was handed would otherwise just be a test that never ran.
type HostCaseCount =
  | NotCounted of reason: string
  | Counted of cases: int

let hostCaseCount : HostCaseCount ref = ref (NotCounted "the host tier was not listed")

/// Start offset, wall and cpu of every tier `runTiers` ran, for the timing table.
let tierTimings = Collections.Concurrent.ConcurrentDictionary<string, TierSchedule.TierTiming>()

/// The runtime settings every test tier's processes get (the tier process, and every daemon, worker and build it spawns),
/// never the pipeline's own build.
///
/// Measured 2026-10-04 (one host case, `[net11.0] rule 1: a public let mutable ...`, each variant in its own cgroup, six
/// rounds with the order rotated, CPU seconds user+system, medians; the machine was never quiet, loads 11 to 22):
///   default 21.8 | TieredPGO=0 16.0 | + gcConcurrent=0 18.2 | + QuickJitForLoops/OSR knobs 16.2 | + TieredCompilation=0 20.2
/// TieredPGO=0 beat the default in 5 of 6 paired rounds (the two runs at pressure under 2: 15.5 against 14.4, so 7% when
/// quiet, 26% at the medians when not). Turning off concurrent GC cost CPU in 6 of 6 pairs against TieredPGO=0 alone, and the
/// tiering knobs did nothing beyond it, so only TieredPGO=0 is adopted. These are runtime settings, not code: a process that
/// lives ten seconds never earns back the instrumentation tier-0 code carries to feed profile-guided optimization.
let tierRuntimeEnvironment : (string * string) list = [ "DOTNET_TieredPGO", "0" ]

// ---- fail fast -------------------------------------------------------------------
//
// A red case used to be found in a tier log minutes after it happened, because every tier ran to the end whatever
// the others did. Now the first failure any tier prints is reported the moment Expecto writes it (the pure reader
// is build/FailureReport.fs), and unless the run says --keep-going, every other tier is cancelled.
//
// Cancelling kills by CGROUP, not by walking process trees: each tier runs in its own systemd scope, and killing
// the scope kills every process in it, including a daemon that outlived the test process that started it. The
// scope is exactly what this run started, so nothing else can be hit. Without a scope (no systemd user session)
// it falls back to killing the process tree and says so.

let keepGoing =
  fsi.CommandLineArgs |> Array.contains "--keep-going"
  || (match Environment.GetEnvironmentVariable "SAGEFS_KEEP_GOING" with
      | "1" | "true" -> true
      | _ -> false)

/// The first failure of the run, with the lines that were printed for it.
type FirstFailure = { Tier: string; Case: string; Report: string list }

let firstFailure : FirstFailure option ref = ref None
let failureGate = obj ()
let cancelRequested = ref false
/// Tiers that failed after the first one, named but not reported in full.
let alsoFailed = Collections.Generic.List<string * string>()
/// Kill-everything functions of the tiers running right now.
let runningKillers = Collections.Concurrent.ConcurrentDictionary<string, unit -> unit>()
/// Tiers stopped (or never started) because of the first failure, with the reason.
let cancelledByFailFast = Collections.Concurrent.ConcurrentDictionary<string, string>()
/// The systemd scope each tier ran in, for the cleanup audit.
let tierUnits = Collections.Concurrent.ConcurrentDictionary<string, string>()

/// Kills every process in the tier's scope, orphans included.
let killScope (unitName: string) =
  exitOf [ "systemctl"; "--user"; "kill"; "--kill-whom=all"; "--signal=SIGKILL"; unitName + ".scope" ] |> ignore

/// Stop every running tier and let nothing new start.
let cancelAll (because: string) =
  lock failureGate (fun () -> cancelRequested.Value <- true)
  for kv in runningKillers do
    cancelledByFailFast[kv.Key] <- because
    try kv.Value () with _ -> ()

/// A tier ended its first failing case. Fail fast prints the report now and stops everything; keep going prints
/// it and carries on. Later failures are named in one line each.
let reportFailure (tier: TierPlan.Tier) (failure: FailureReport.Failure) =
  let reproduce = FailureReport.reproduceCommand (TierPlan.dllOf tier.Framework) tier.Args failure.Name
  let lines = FailureReport.render tier.Name rootDir reproduce FailureReport.NotRecorded failure
  let isFirst =
    lock failureGate (fun () ->
      match firstFailure.Value with
      | None ->
        firstFailure.Value <- Some { Tier = tier.Name; Case = failure.Name; Report = lines }
        true
      | Some _ ->
        alsoFailed.Add((tier.Name, failure.Name))
        false)
  match isFirst with
  | true ->
    lock invokedTiers (fun () -> for l in lines do printfn "%s" l)
    match keepGoing with
    | true -> printfn "%sthe run keeps going (--keep-going): every other tier still runs" FailureReport.linePrefix
    | false ->
      printfn "%scancelling every other tier (pass --keep-going to see all failures)" FailureReport.linePrefix
      // Off the reader thread that found it: killing a tier ends its own reader.
      Threading.Tasks.Task.Run(fun () -> cancelAll (sprintf "%s failed in %s" failure.Name tier.Name)) |> ignore
  | false -> printfn "%salso failed, tier %s: %s" FailureReport.linePrefix tier.Name failure.Name

/// Run one tier (in its own clone when isolated) and record its outcome.
/// `slotIndex` is which of the `slots` concurrently-running workers is
/// running it (see `runTiers`) — NOT the tier's own index, since one slot
/// runs many tiers over the run, one after another, off the shared queue.
/// The port range is keyed to the slot for exactly that reason: whichever
/// tier a slot is running at a given moment is the only one using that
/// slot's range at that moment.
let runTier (isolation: TierPlan.Isolation) (slots: int) (slotIndex: int) (t: TierPlan.Tier) =
  async {
    let safe = TierPlan.fileNameOf t.Name
    let clone = Path.Combine(tierWork, safe)
    let log = Path.Combine(tierWork, safe + ".log")
    let ledger = Path.Combine(tierWork, safe + ".jsonl")
    let dataDir = Path.Combine(tierWork, safe + ".data")
    let tmpDir = Path.Combine(tierWork, safe + ".tmp")
    for d in [ clone; dataDir; tmpDir ] do
      if Directory.Exists d then Directory.Delete(d, true)
    if File.Exists ledger then File.Delete ledger
    Directory.CreateDirectory dataDir |> ignore
    Directory.CreateDirectory tmpDir |> ignore
    let portLo, portHi = TierPlan.portRangeOf slots slotIndex
    let env =
      [ "SAGEFS_TRUST_LEDGER", ledger
        "SAGEFS_DATA_DIR", dataDir
        "TMPDIR", tmpDir
        "SAGEFS_HOST_CACHE_DIR", sharedHostCache
        // A build node that outlives its tier could serve the next tier's build
        // from the wrong filesystem view; the private /tmp already hides it, and
        // this stops tiers leaving nodes behind at all.
        "MSBUILDDISABLENODEREUSE", "1"
        "SAGEFS_SUITE_DURATIONS", suiteDurationsFile
        "SAGEFS_SUITE_TIMINGS_OUT", Path.Combine(tierWork, safe + ".suites.json")
        // Every case's seconds, slowest first, beside the tier's log: the log itself prints only the slowest 15.
        "SAGEFS_CASE_TIMINGS_OUT", Path.Combine(tierWork, safe + ".cases.tsv")
        // Every harness that spawns a real daemon reserves its ports through
        // TestPorts.reservePair(), which scans ONLY inside this range — see
        // build/TierPlan.fs `portRangeOf` for why disjoint-per-slot ranges
        // make a cross-tier port collision structurally impossible.
        "SAGEFS_TEST_PORT_RANGE", $"{portLo}-{portHi}" ]
      @ tierRuntimeEnvironment
    let command = $"dotnet {TierPlan.dllOf t.Framework} {t.Args}"
    let tierTimeout = TierPlan.timeoutOf (readDurations ()) t
    let costFile = Path.Combine(tierWork, safe + ".cost")
    if File.Exists costFile then File.Delete costFile
    let accounted = accountingAvailable.Force()
    let env = (TierCost.statFileEnvironmentVariable, costFile) :: env
    let unitName = sprintf "sagefs-tier-%s-%d" safe Environment.ProcessId
    let inScope (argv: string list) =
      match accounted with
      | true ->
        tierUnits[t.Name] <- unitName
        TierCost.accountedArgv unitName argv
      | false -> argv
    // The streaming failure reader: one per tier, fed every output line. A block is reported when the next log
    // line ends it, or when the log has been quiet for `quietBeforeFlush` (the last block of a tier that has
    // gone quiet would otherwise wait for a line that never comes).
    let reading = TierPlan.caseFailureFailsTier t
    let detector = ref FailureReport.Idle
    let detectorGate = obj ()
    let lastLine = Diagnostics.Stopwatch.StartNew()
    let handle (found: FailureReport.Failure option) =
      match found with
      | Some failure when reading -> reportFailure t failure
      | _ -> ()
    let onLine (raw: string) =
      match reading with
      | false -> ()
      | true ->
        let found =
          lock detectorGate (fun () ->
            lastLine.Restart()
            let next, found = FailureReport.step detector.Value (FailureReport.stripAnsi raw)
            detector.Value <- next
            found)
        handle found
    let quietBeforeFlush = TimeSpan.FromSeconds 2.0
    let flushWhenQuiet =
      new Threading.Timer(
        (fun _ ->
          let found =
            lock detectorGate (fun () ->
              match lastLine.Elapsed >= quietBeforeFlush with
              | true ->
                let held = FailureReport.flush detector.Value
                detector.Value <- FailureReport.Idle
                held
              | false -> None)
          handle found),
        null, quietBeforeFlush, quietBeforeFlush)
    let kill () =
      match accounted with
      | true -> killScope unitName
      | false -> ()
    let watch =
      { OnLine = onLine
        OnStarted =
          fun killTree ->
            let killer () =
              kill ()
              killTree ()
            runningKillers[t.Name] <- killer
            // A tier that starts after the cancel went out must not run.
            if cancelRequested.Value then
              cancelledByFailFast.TryAdd(t.Name, "started after the first failure") |> ignore
              killer () }
    let sw = Diagnostics.Stopwatch.StartNew()
    let! code =
      match isolation with
      | TierPlan.Shared -> execToLogWatching watch tierTimeout rootDir env log (inScope [ "sh"; "-c"; command ])
      | TierPlan.CopyOnWrite ->
        async {
          match exitOf [ "cp"; "-a"; "--reflink=always"; rootDir; clone ] with
          | 0 ->
            let argv = TierPlan.isolatedArgv (procId "Uid") (procId "Gid") rootDir clone tmpDir command
            return! execToLogWatching watch tierTimeout rootDir env log (inScope argv)
          | failed ->
            File.WriteAllText(log, sprintf "could not clone the checkout for this tier (cp exit %d)" failed)
            return failed
        }
    flushWhenQuiet.Dispose()
    runningKillers.TryRemove t.Name |> ignore
    handle (lock detectorGate (fun () -> FailureReport.flush detector.Value))
    let seconds = sw.Elapsed.TotalSeconds
    let cancelled = cancelledByFailFast.ContainsKey t.Name
    lock invokedTiers (fun () -> invokedTiers.Add((t.Name, t.Args, (code = 0))))
    let tail =
      match code with
      | 0 -> ""
      | _ when cancelled -> ""
      | _ ->
        File.ReadAllLines log
        |> Array.map (fun l -> Text.RegularExpressions.Regex.Replace(l, "\x1b\\[[0-9;?]*[A-Za-z]", ""))
        |> fun lines -> lines[max 0 (lines.Length - 60) ..]
        |> String.concat "\n"
    let cost =
      match File.Exists costFile with
      | false -> None
      | true ->
        match TierCost.parse (File.ReadAllText costFile) with
        | Result.Ok cost ->
          tierCosts[t.Name] <- cost
          Some cost
        | Result.Error _ -> None
    let costText =
      match cost with
      | Some c -> sprintf "  cpu=%.0fs (user %.0f, sys %.0f) peak=%.1fGB" (TierCost.totalSeconds c) c.UserSeconds c.SystemSeconds (float c.PeakBytes / 1073741824.0)
      | None -> ""
    let cancelledText = match cancelled with | true -> "  CANCELLED by fail-fast" | false -> ""
    lock invokedTiers (fun () ->
      printfn "── tier %-28s exit=%d in %.0fs%s%s  (log: %s)" t.Name code seconds costText cancelledText log
      if tail <> "" then printfn "%s\n── end of %s" tail t.Name)
    // A passing tier's clone is only scratch; a failing one is kept to inspect. A tier cancelled because ANOTHER
    // tier failed has nothing to inspect, so its clone goes too.
    let isTheFailingTier = firstFailure.Value |> Option.exists (fun f -> f.Tier = t.Name)
    if (code = 0 || (cancelled && not isTheFailingTier)) && Directory.Exists clone then
      try Directory.Delete(clone, true) with _ -> ()
    return t.Name, seconds
  }

/// After a cancel: every scope of a cancelled tier must be gone, which means no process of that tier is left,
/// orphaned daemons and workers included. A scope that is still there after the grace is killed again, and one that
/// still survives is named loudly (a leaked daemon would hold the tier's ports for the next run).
let auditCleanup () =
  async {
    let graceSeconds = 10.0
    let pollEvery = 200
    for kv in cancelledByFailFast do
      match tierUnits.TryGetValue kv.Key with
      | false, _ -> ()
      | true, unitName ->
        let active () = exitOf [ "systemctl"; "--user"; "--quiet"; "is-active"; unitName + ".scope" ] = 0
        let deadline = DateTime.UtcNow.AddSeconds graceSeconds
        while active () && DateTime.UtcNow < deadline do
          do! Async.Sleep pollEvery
        match active () with
        | false -> printfn "cleanup: %-28s scope gone, nothing of this tier is left running" kv.Key
        | true ->
          killScope unitName
          do! Async.Sleep (pollEvery * 5)
          match active () with
          | false -> printfn "cleanup: %-28s scope needed a second kill, now gone" kv.Key
          | true -> printfn "cleanup: LEFTOVER %s still has processes in %s.scope; `systemctl --user kill -s KILL %s.scope`" kv.Key unitName unitName
  }

/// Run every tier, `slots` at a time, longest-expected first, then merge the
/// per-tier ledger rows into the one ledger the trust report reads.
let runTiers (tiers: TierPlan.Tier list) =
  async {
    let isolation = detectIsolation ()
    // Build the FSI host ONCE into the shared cache, before any tier starts, so
    // no shard pays a cold host build inside its own time.
    Directory.CreateDirectory sharedHostCache |> ignore
    let! prebuilt =
      execToLog (TimeSpan.FromMinutes 30.0) rootDir [ "SAGEFS_HOST_CACHE_DIR", sharedHostCache ] (Path.Combine(tierWork, "prebuild-host.log"))
        [ "dotnet"; testDll; "--prebuild-host" ]
    printfn "FSI host prebuild: exit %d" prebuilt
    let requested =
      match Int32.TryParse(Environment.GetEnvironmentVariable "SAGEFS_TIER_PARALLEL") with
      | true, n -> Some n
      | _ -> None
    let policy = TierPlan.policyFor isolation Environment.ProcessorCount requested
    let slots = TierPlan.slotsOf policy
    let history = readHistory ()
    let ordered = TierSchedule.orderByCriticalPath history tiers
    let estimate (t: TierPlan.Tier) = history.Wall.TryFind t.Name |> Option.defaultValue 0.0
    let serial = ordered |> List.sumBy estimate
    let longest = ordered |> List.fold (fun m t -> max m (estimate t)) 0.0
    printfn "Tiers: %d, admitted by %s (%A), order: %s" tiers.Length
      (match policy with
       | TierPlan.Fixed n -> sprintf "a fixed count of %d" n
       | TierPlan.ByPressure l -> sprintf "cpu pressure under %.0f%% and memory above %d GiB reserve, at most %d at once" l.CpuPressureBound (l.MemoryReserveBytes / TierPlan.Admission.bytesPerGiB) l.MaxTierProcesses)
      isolation
      (ordered |> List.map (fun t -> t.Name) |> String.concat ", ")
    if serial > 0.0 then
      printfn "Expected: the longest unit is %.0fs (the floor of this stage), all units in series would be %.0fs, from recorded durations" longest serial
    // How many cases every host suite registers, so the trust report can check the shards were handed all of them.
    // Listing executes nothing (Expecto's --list-tests), costs one process start, and a failure is a red row, not a skip.
    // It runs beside the first units (a task, awaited when the tiers are done), so it is never on the critical path.
    let listing =
      match tiers |> List.exists (fun t -> t.Args.StartsWith "--integration-host") with
      | false -> Threading.Tasks.Task.CompletedTask
      | true ->
        let listLog = Path.Combine(tierWork, "host-list.log")
        Async.StartAsTask(
          async {
            let! listed =
              execToLog (TimeSpan.FromMinutes 5.0) rootDir [ "SAGEFS_DATA_DIR", Path.Combine(tierWork, "host-list.data") ] listLog
                [ "dotnet"; testDll; "--integration-host"; "--list-tests" ]
            hostCaseCount.Value <-
              match listed with
              | 0 ->
                let cases = File.ReadAllLines listLog |> Array.filter (fun l -> Text.RegularExpressions.Regex.IsMatch(l, "^[NPF] ")) |> Array.length
                Counted cases
              | code -> NotCounted (sprintf "`--integration-host --list-tests` exited %d (log: %s)" code listLog)
            printfn "host tier: %A" hostCaseCount.Value
          })
        :> Threading.Tasks.Task
    // The scheduler: a unit starts when the machine has room (TierPlan.advance), the head of the line first. A finishing unit
    // wakes the loop at once; otherwise it looks again every `reevaluateEverySeconds`. Everything it decides is
    // TierPlan.advanceWith, the function SageFs.Simulation.TierSched runs against a fake machine.
    let clock = Diagnostics.Stopwatch.StartNew()
    let gate = obj ()
    let schedule = ref (TierPlan.startSchedule 0.0 (ordered |> List.map (TierSchedule.candidateOf history)))
    let byLabel = ordered |> List.map (fun t -> t.Name, t) |> Map.ofList
    let freeSlots = Collections.Generic.Stack<int>([ slots - 1 .. -1 .. 0 ])
    let somethingFinished = new Threading.SemaphoreSlim(0)
    let results = Collections.Concurrent.ConcurrentBag<string * float>()
    let tasks = ResizeArray<Threading.Tasks.Task>()
    let lastHeld = ref ""
    let waitingLeft () = lock gate (fun () -> not schedule.Value.Waiting.IsEmpty)
    while not cancelRequested.Value && waitingLeft () do
      let step =
        lock gate (fun () ->
          let now = clock.Elapsed.TotalSeconds
          match TierPlan.advance policy (machinePort.Read ()) now schedule.Value with
          | TierPlan.Started (candidate, basis, next) ->
            schedule.Value <- next
            TierPlan.Started (candidate, basis, next), freeSlots.Pop(), now
          | held -> held, -1, now)
      match step with
      | TierPlan.Started (candidate, basis, _), slot, startedAt ->
        let t = byLabel[candidate.Label]
        lastHeld.Value <- ""
        printfn "── start %-28s at +%.0fs, slot %d, %d running (%s)" t.Name startedAt slot
          (lock gate (fun () -> schedule.Value.Running.Length))
          (match basis with TierPlan.Fits -> "the machine has room" | TierPlan.StarvationGuard -> "starvation guard: nothing of ours runs and it waited out the patience")
        let tierTask =
          Async.StartAsTask(
            async {
              let! (name, seconds) = runTier isolation slots slot t
              let cpu =
                match tierCosts.TryGetValue name with
                | true, c -> TierSchedule.CpuSeconds (TierCost.totalSeconds c)
                | false, _ -> TierSchedule.CpuNotMeasured
              tierTimings[name] <- { Tier = name; StartOffsetSeconds = startedAt; WallSeconds = seconds; Cpu = cpu }
              results.Add((name, seconds))
              lock gate (fun () ->
                freeSlots.Push slot
                schedule.Value <- TierPlan.finishUnit name schedule.Value)
              somethingFinished.Release() |> ignore
            })
        lock gate (fun () -> tasks.Add tierTask)
      | TierPlan.Held reason, _, _ ->
        let kind = (match reason with TierPlan.AtProcessCap _ -> "cap" | TierPlan.Settling _ -> "settling" | TierPlan.CpuBusy _ -> "cpu" | TierPlan.MemoryShort _ -> "memory")
        // Said once per kind, not once per look: a wait of minutes is one line, not a hundred. The settle between two
        // starts is routine and not worth a line.
        if lastHeld.Value <> kind && kind <> "settling" then
          lastHeld.Value <- kind
          printfn "── hold: next unit waits, %s" (TierPlan.WaitReason.describe reason)
        do! somethingFinished.WaitAsync(TimeSpan.FromSeconds TierPlan.Admission.reevaluateEverySeconds) |> Async.AwaitTask |> Async.Ignore
      | TierPlan.Drained, _, _ -> ()
    do! (lock gate (fun () -> tasks.ToArray())) |> Threading.Tasks.Task.WhenAll |> Async.AwaitTask
    do! listing |> Async.AwaitTask
    let measured = [ List.ofSeq results ]
    // Tiers that never started because the run was cancelled are red in the trust report, never absent from it.
    let because =
      firstFailure.Value
      |> Option.map (fun f -> sprintf "%s failed in %s" f.Case f.Tier)
      |> Option.defaultValue "the run was cancelled"
    for neverStarted in lock gate (fun () -> schedule.Value.Waiting) do
      let t = byLabel[neverStarted.Label]
      cancelledByFailFast[t.Name] <- because
      lock invokedTiers (fun () -> invokedTiers.Add((t.Name, t.Args, false)))
      printfn "── tier %-28s never started (cancelled by fail-fast)" t.Name
    do! auditCleanup ()
    // Merge ledgers (per-tier files: separate processes never share a writer).
    for t in tiers do
      let ledger = Path.Combine(tierWork, TierPlan.fileNameOf t.Name + ".jsonl")
      if File.Exists ledger then File.AppendAllText(trustLedger, File.ReadAllText ledger)
    // What this run learned feeds the next. A cancelled tier's time and cost are how long it got to run, not what it takes,
    // so none of it is recorded.
    let finishedTiers = tiers |> List.filter (fun t -> not (cancelledByFailFast.ContainsKey t.Name))
    // Per-suite seconds: every finished tier's case file (SAGEFS_CASE_TIMINGS_OUT) summed by suite, falling back to the
    // suite file the process itself writes, then blended into the recorded weights so one run under load cannot
    // move a suite to another shard and back (TierSchedule.blend).
    let suitesOf (t: TierPlan.Tier) =
      let cases = Path.Combine(tierWork, TierPlan.fileNameOf t.Name + ".cases.tsv")
      match File.Exists cases with
      | true -> TierSchedule.suiteSecondsOfCases TierSchedule.hostRootName (TierSchedule.parseCaseTimings (File.ReadAllText cases))
      | false -> readJsonMap (Path.Combine(tierWork, TierPlan.fileNameOf t.Name + ".suites.json"))
    let latestSuites =
      finishedTiers
      |> List.map suitesOf
      |> List.fold (fun (acc: Map<string, float>) m -> m |> Map.fold (fun (a: Map<string, float>) k v -> a.Add(k, v)) acc) Map.empty
    let suiteTimings = TierSchedule.blend (readJsonMap suiteDurationsFile) latestSuites
    try File.WriteAllText(suiteDurationsFile, JsonSerializer.Serialize suiteTimings) with _ -> ()
    try
      Directory.CreateDirectory(Path.GetDirectoryName costsFile) |> ignore
      let costs =
        finishedTiers
        |> List.fold
          (fun (acc: Map<string, TierCost.Cost>) t ->
            match tierCosts.TryGetValue t.Name with
            | true, cost -> acc.Add(t.Name, cost)
            | false, _ -> acc)
          history.Costs
      File.WriteAllText(costsFile, JsonSerializer.Serialize costs)
    with _ -> ()
    let updated =
      measured
      |> Seq.concat
      |> Seq.filter (fun (n, _) -> not (cancelledByFailFast.ContainsKey n))
      |> Seq.fold (fun (m: Map<string, float>) (n, s) -> m.Add(n, s)) history.Wall
    try
      Directory.CreateDirectory(Path.GetDirectoryName durationsFile) |> ignore
      File.WriteAllText(durationsFile, JsonSerializer.Serialize updated)
    with _ -> ()
  }

/// How to run the ratchet lane by hand, printed with the stage and again on red.
let ratchetReproduction =
  $"dotnet build SageFs.Tests -c Release && dotnet {testDll} --ratchets"

/// The failures in an Expecto log, by name: each `[E]` line, then the message line under it. The
/// tail a failed tier prints is its last 60 lines, and a lane's own noise can push the failing
/// name out of them, so the ratchets stage names the failures itself.
let failuresInLog (log: string) : string list =
  match File.Exists log with
  | false -> []
  | true ->
    let lines =
      File.ReadAllLines log
      |> Array.map (fun l -> Text.RegularExpressions.Regex.Replace(l, "\x1b\\[[0-9;?]*[A-Za-z]", ""))
    [ for i in 0 .. lines.Length - 1 do
        match lines[i].StartsWith "[E] " with
        | false -> ()
        | true ->
          yield lines[i]
          if i + 1 < lines.Length then yield "    " + lines[i + 1] ]

/// The ratchet lane: every registered ratchet (budgets, literal counts, generated
/// pages, CI wiring) and nothing else, in this process's own tree, right after the
/// build. It is pure file reads, so it takes seconds, and it runs through the same
/// `testTier` step as every other tier, so its trust row (registered/ran/verdict)
/// lands in the trust report. Not isolated in a clone: nothing in it spawns a process
/// or binds a port, and a clone of the checkout would cost more than the lane.
let runRatchetLane () =
  async {
    let lane = testTier "--ratchets"
    printfn "ratchets: to reproduce, run: %s" ratchetReproduction
    let! (_, seconds) = runTier TierPlan.Shared 1 0 lane
    let ledger = Path.Combine(tierWork, TierPlan.fileNameOf lane.Name + ".jsonl")
    if File.Exists ledger then File.AppendAllText(trustLedger, File.ReadAllText ledger)
    let green = lock invokedTiers (fun () -> invokedTiers |> Seq.exists (fun (name, _, ok) -> name = lane.Name && ok))
    printfn "ratchets: %s in %.0fs" (match green with true -> "green" | false -> "RED") seconds
    match green with
    | true -> return Ok()
    | false ->
      printfn "ratchets: the failing ratchets:"
      for line in failuresInLog (Path.Combine(tierWork, TierPlan.fileNameOf lane.Name + ".log")) do
        printfn "%s" line
      return
        Error(
          sprintf
            "ratchets: a ratchet is red, so nothing slower ran. The failing ratchets are listed above. Reproduce: %s. A budget that only went DOWN is fixed by appending --tighten to that command."
            ratchetReproduction)
  }

// ---- reusing a green tier of the same commit ---------------------------------------
//
// A gate that was red for one flaky tier used to rerun all of them to learn what the others had already said.
// A tier that goes green on a clean tree leaves a pass record (build/PassRecord.fs: the key, the rules, the
// format); a later run of the SAME commit with byte-identical binaries takes it instead and the trust table says
// `Trusted (reused from <time>)`. `--fresh` (or SAGEFS_FRESH=1) runs everything. Records live in
// SAGEFS_TIER_PASSES, which the local gate sets; with no store (CI, a plain run) nothing is reused and nothing
// is written.

/// stdout of `argv` in `workingDir`, trimmed. An `Error` when it cannot run or exits non-zero, so "git printed
/// nothing" and "git failed" are never confused.
let captureOf (workingDir: string) (argv: string list) : Result<string, string> =
  try
    let psi = Diagnostics.ProcessStartInfo(List.head argv)
    List.tail argv |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- workingDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Diagnostics.Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    p.StandardError.ReadToEndAsync() |> ignore
    p.WaitForExit()
    match p.ExitCode with
    | 0 -> Result.Ok(out.Result.Trim())
    | code -> Result.Error(sprintf "%s exited %d" (String.concat " " argv) code)
  with e -> Result.Error e.Message

let passFreshness =
  let asked =
    fsi.CommandLineArgs |> Array.contains "--fresh"
    || (match Environment.GetEnvironmentVariable "SAGEFS_FRESH" with
        | "1" | "true" -> true
        | _ -> false)
  match asked with
  | true -> Freshness.Fresh
  | false -> Freshness.Reuse

let passStore =
  match Environment.GetEnvironmentVariable "SAGEFS_TIER_PASSES" with
  | null | "" -> Store.Disabled
  | directory -> Store.At directory

/// The tiers that took a record this run, by tier name.
let reusedTiers = Collections.Concurrent.ConcurrentDictionary<string, PassRecord>()

/// The inputs each tier was judged on, by tier name: what a green run records.
let passInputsByTier = Collections.Concurrent.ConcurrentDictionary<string, PassInputs>()

/// The suite-duration table this run was asked to partition by, when it is the one a first run of this commit
/// recorded rather than today's. A later run of a commit has to partition like the first or a shard's record covers
/// other suites; `Live` carries the table as it was, so it can be put back with this run's timings merged in.
type PartitionSource =
  | Live
  | Frozen of live: Map<string, float>

let partitionSource = ref Live

let isoNow () = DateTimeOffset.Now.ToString "yyyy-MM-dd'T'HH:mm:sszzz"

/// The tiers a pass record may never stand in for: the mutation score is judged against its own bar.
let eligibilityOf (tier: TierPlan.Tier) : Eligibility =
  match tier.Args.StartsWith("--mutation-score", StringComparison.Ordinal) with
  | true -> Eligibility.Ineligible "the mutation score is judged against its own bar"
  | false -> Eligibility.Eligible

/// Clean means NOTHING differs from the commit, untracked files included. A git that cannot answer is dirty.
let treeStateNow () : TreeState =
  match captureOf rootDir [ "git"; "status"; "--porcelain" ] with
  | Result.Ok "" -> TreeState.Clean
  | _ -> TreeState.Dirty

let closureDirectoriesOf (framework: TierPlan.Framework) : string list =
  [ TierPlan.testBinDirOf framework; sprintf "SageFs/bin/Release/%s" (TierPlan.Framework.tfm framework) ]

/// Hashes of what a framework's tiers load, once per run (the product closure is hundreds of MB).
let frameworkHashes =
  Collections.Concurrent.ConcurrentDictionary<TierPlan.Framework, Result<string * string, string>>()

let hashesOf (framework: TierPlan.Framework) : Result<string * string, string> =
  frameworkHashes.GetOrAdd(
    framework,
    fun f ->
      try
        let testAssembly = PassRecord.hashFile (Path.Combine(rootDir, TierPlan.dllOf f))
        Result.Ok(testAssembly, PassRecord.closureHash rootDir (closureDirectoriesOf f))
      with e -> Result.Error(sprintf "cannot hash the %s build: %s" (TierPlan.Framework.tfm f) e.Message))

let sdkVersionNow = lazy (captureOf rootDir [ "dotnet"; "--version" ])

let passInputsOf (sha: string) (tier: TierPlan.Tier) : Result<PassInputs, string> =
  match hashesOf tier.Framework, sdkVersionNow.Force() with
  | Result.Ok(testAssembly, closure), Result.Ok sdk ->
    let partition =
      match TierPlan.shardOfArgs (tier.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries)) with
      | Some _ ->
        PassRecord.sha256OfText (match File.Exists suiteDurationsFile with | true -> File.ReadAllText suiteDurationsFile | false -> "")
      | None -> ""
    Result.Ok
      { Sha = sha; Tier = tier.Name; Args = tier.Args; Framework = TierPlan.Framework.tfm tier.Framework; Sdk = sdk
        TestAssembly = testAssembly; Closure = closure; Partition = partition }
  | Result.Error e, _ -> Result.Error e
  | _, Result.Error e -> Result.Error e

/// The first run of a commit writes down the suite-duration table it partitions by; every later run of that commit
/// partitions by the same table, so a shard's record covers the same suites when it is asked about again.
let freezePartition (directory: string) (sha: string) : unit =
  let snapshot = Path.Combine(directory, sha, "partition.json")
  match File.Exists snapshot with
  | true ->
    partitionSource.Value <- Frozen(readJsonMap suiteDurationsFile)
    Directory.CreateDirectory(Path.GetDirectoryName suiteDurationsFile) |> ignore
    File.Copy(snapshot, suiteDurationsFile, true)
  | false ->
    Directory.CreateDirectory(Path.GetDirectoryName snapshot) |> ignore
    match File.Exists suiteDurationsFile with
    | true -> File.Copy(suiteDurationsFile, snapshot, true)
    | false -> File.WriteAllText(snapshot, "{}")

/// After the tiers: today's table gets this run's timings on top of what it held before the freeze.
let restorePartition () : unit =
  match partitionSource.Value with
  | Live -> ()
  | Frozen live ->
    let merged = readJsonMap suiteDurationsFile |> Map.fold (fun (m: Map<string, float>) k v -> m.Add(k, v)) live
    try File.WriteAllText(suiteDurationsFile, JsonSerializer.Serialize merged) with _ -> ()
    partitionSource.Value <- Live

/// Takes the records it may, says so for every tier, and returns the tiers that still have to run.
let takeRecordedPasses (tiers: TierPlan.Tier list) : TierPlan.Tier list =
  match passStore with
  | Store.Disabled -> tiers
  | Store.At directory ->
    match captureOf rootDir [ "git"; "rev-parse"; "HEAD" ], treeStateNow () with
    | Result.Error e, _ ->
      printfn "pass records: not used, the commit is unknown (%s)" e
      tiers
    | Result.Ok sha, tree ->
      PassRecord.prune directory
      match tree with
      | TreeState.Clean -> freezePartition directory sha
      | TreeState.Dirty -> ()
      let toRun = ResizeArray<TierPlan.Tier>()
      for tier in tiers do
        let stored = PassRecord.read (PassRecord.recordPath directory sha (TierPlan.fileNameOf tier.Name))
        let decision =
          match passInputsOf sha tier with
          | Result.Ok inputs ->
            passInputsByTier[tier.Name] <- inputs
            PassRecord.decide passFreshness passStore tree (eligibilityOf tier) inputs stored
          | Result.Error e -> Decision.RunTier(RunBecause.UnreadableRecord e)
        match decision with
        | Decision.ReuseRecord record ->
          reusedTiers[tier.Name] <- record
          File.AppendAllText(trustLedger, record.LedgerRow + "\n")
          lock invokedTiers (fun () -> invokedTiers.Add((tier.Name, tier.Args, true)))
          printfn "── tier %-28s reused: green in %.0fs on %s, the same commit and the same bytes (record %s)" tier.Name record.Seconds record.RecordedAt (record.Key.Substring(0, 12))
        | Decision.RunTier because ->
          printfn "pass records: %-28s runs (%s)" tier.Name (PassRecord.describeRun because)
          toRun.Add tier
      List.ofSeq toRun

type TrustLine =
  { Tier: string
    Registered: string
    Ran: string
    Passed: string
    Failed: string
    Errored: string
    Ignored: string
    Verdict: string
    Detail: string
    Red: bool }

/// Join what the pipeline invoked with what each test process reported.
let tierTrustLines () =
  let rows =
    match File.Exists trustLedger with
    | false -> []
    | true ->
      File.ReadAllLines trustLedger
      |> Array.filter (fun l -> l.Trim() <> "")
      |> Array.map (fun l -> JsonDocument.Parse(l).RootElement.Clone())
      |> Array.toList
  let str (e: JsonElement) (name: string) =
    match e.GetProperty(name).ValueKind with
    | JsonValueKind.Number -> string (e.GetProperty(name).GetInt32())
    | _ -> e.GetProperty(name).GetString()
  [ for (tier, _args, stepOk) in List.ofSeq invokedTiers ->
      match rows |> List.filter (fun r -> str r "Tier" = tier) |> List.tryLast with
      | None ->
        let cancelled = cancelledByFailFast.TryGetValue tier
        { Tier = tier; Registered = "?"; Ran = "?"; Passed = "?"; Failed = "?"; Errored = "?"; Ignored = "?"
          Verdict =
            match cancelled with
            | true, _ -> "Cancelled"
            | false, _ -> "NoReport"
          Detail =
            match cancelled, stepOk with
            | (true, because), _ -> sprintf "stopped by fail-fast, so it did not finish: %s" because
            | (false, _), true -> "the step passed but the process reported nothing"
            | (false, _), false -> "the process failed before reporting (runner setup or crash)"
          Red = true }
      | Some r ->
        let verdict = str r "Verdict"
        let greenVerdict = verdict = "Trusted" || verdict = "SurvivorsUnderBar"
        { Tier = tier
          Registered = str r "Registered"
          Ran = str r "Ran"
          Passed = str r "Passed"
          Failed = str r "Failed"
          Errored = str r "Errored"
          Ignored = str r "Ignored"
          Verdict =
            match greenVerdict, stepOk, reusedTiers.TryGetValue tier with
            | true, false, _ -> "ExitMismatch"
            // A tier that took a record says so, and when: never a plain Trusted.
            | true, true, (true, record) -> PassRecord.reusedVerdict record
            | _ -> verdict
          Detail =
            match greenVerdict, stepOk with
            | true, false -> "reported green, but the process exited non-zero"
            | _ -> str r "Detail"
          Red = not (greenVerdict && stepOk) } ]

/// The one row that says the host shards between them ran every host case: the shards' registered counts must add up to
/// what a listing of the whole host tier registers, so a suite no shard was handed is a red row and not a test that
/// quietly never ran. Not asked when a shard did not report (that tier is already red, and its count says nothing), and
/// not asked when the run did not include the host shards.
let hostCoverageLines (lines: TrustLine list) : TrustLine list =
  let shards = lines |> List.filter (fun l -> l.Tier.StartsWith "--integration-host[")
  let row (red: bool) (verdict: string) (registered: string) (detail: string) =
    { Tier = "--integration-host (coverage)"
      Registered = registered; Ran = "-"; Passed = "-"; Failed = "-"; Errored = "-"; Ignored = "-"
      Verdict = verdict; Detail = detail; Red = red }
  match shards with
  | [] -> []
  | _ when shards |> List.exists (fun l -> l.Registered = "?") -> []
  | _ ->
    match hostCaseCount.Value with
    | NotCounted reason -> [ row true "CoverageUnknown" "?" (sprintf "the host tier could not be listed, so nobody can say every host case was handed to a shard: %s" reason) ]
    | Counted expected ->
      let registered = shards |> List.map (fun l -> int l.Registered)
      match TierSchedule.checkHostCoverage expected registered with
      | TierSchedule.Covered ->
        [ row false "Covered" (string expected) (sprintf "%d shards registered %d host cases, every one of the %d the host suites register" shards.Length (List.sum registered) expected) ]
      | TierSchedule.CoverageGap (wanted, got) ->
        [ row true "CoverageGap" (string got) (sprintf "the host suites register %d cases and the %d shards registered %d between them: a suite was dropped or taken twice" wanted shards.Length got) ]

let trustLines () =
  let lines = tierTrustLines ()
  lines @ hostCoverageLines lines

let renderTrustTable (lines: TrustLine list) =
  let header =
    [ "| Tier | Registered | Ran | Passed | Failed | Errored | Ignored | Verdict | Detail |"
      "|---|---:|---:|---:|---:|---:|---:|---|---|" ]
  let body =
    lines
    |> List.map (fun l ->
      let mark = match l.Red with true -> "❌" | false -> "✅"
      $"| {l.Tier} | {l.Registered} | {l.Ran} | {l.Passed} | {l.Failed} | {l.Errored} | {l.Ignored} | {mark} {l.Verdict} | {l.Detail} |")
  String.concat "\n" ("## Test trust report" :: "" :: header @ body)

/// The row a tier's process wrote to the trust ledger, verbatim: what a record carries so a reused tier's row is the original.
let ledgerRowOf (tier: string) : string =
  match File.Exists trustLedger with
  | false -> ""
  | true ->
    File.ReadAllLines trustLedger
    |> Array.filter (fun l ->
      l.Trim() <> ""
      && (try JsonDocument.Parse(l).RootElement.GetProperty("Tier").GetString() = tier with _ -> false))
    |> Array.tryLast
    |> Option.defaultValue ""

/// After the tiers: every tier that RAN and came out Trusted, on a clean tree, leaves a record for its inputs.
/// A tier that took a record this run is not recorded again; one that was red, cancelled or errored is never recorded.
let recordGreenPasses (ran: TierPlan.Tier list) : unit =
  match passStore, treeStateNow (), captureOf rootDir [ "git"; "rev-parse"; "HEAD" ] with
  | Store.At directory, TreeState.Clean, Result.Ok sha ->
    let lines = trustLines ()
    let seconds = readDurations ()
    for tier in ran do
      match passInputsByTier.TryGetValue tier.Name, lines |> List.tryFind (fun l -> l.Tier = tier.Name) with
      | (true, inputs), Some line when inputs.Sha = sha && not line.Red && line.Verdict = "Trusted" && not (reusedTiers.ContainsKey tier.Name) ->
        match ledgerRowOf tier.Name with
        | "" -> ()
        | row ->
          let record =
            PassRecord.make inputs (isoNow ()) (seconds.TryFind tier.Name |> Option.defaultValue 0.0) line.Verdict
              (line.Registered, line.Ran, line.Passed, line.Failed, line.Errored, line.Ignored) line.Detail row
          try PassRecord.write (PassRecord.recordPath directory sha (TierPlan.fileNameOf tier.Name)) record
          with e -> printfn "pass records: could not record %s (%s)" tier.Name e.Message
      | _ -> ()
  | Store.At _, TreeState.Dirty, _ -> printfn "pass records: nothing recorded, the working tree is not clean"
  | _ -> ()

// ---- work that runs beside the stages ------------------------------------------
//
// Some stages do not depend on the one before them. The net10 test assembly is built from its own private obj tree
// (TierPlan.testBuildCommands), so it can be compiled while the net11 build is still going; the VS Code stages
// touch nothing .NET built here. They used to wait their turn, about 130 seconds of a 260 second run-up to the
// tiers. Now each is STARTED as early as its inputs exist and JOINED by the stage that used to run it, so the
// stages keep their names and order, a failure still stops the pipeline at the same stage, and the wait that is
// left is only what the work really costs beyond what it overlapped.
//
// A background job writes to its own log (printed, tail first, when it fails) and is killed when the pipeline
// ends, however it ends, so a red stage never leaves a build running behind it.

/// One command of a background job.
type BackgroundStep = { Argv: string list; WorkingDir: string; Env: (string * string) list }

/// A job's steps run one after another. Each has its own completion, so the stage that used to run step N can join
/// exactly that far and be blamed for exactly that step.
type Background =
  { Name: string
    Steps: Threading.Tasks.TaskCompletionSource<Result<unit, string>> array }

let backgroundKillers = Collections.Concurrent.ConcurrentDictionary<string, unit -> unit>()
let backgroundClock = Collections.Concurrent.ConcurrentDictionary<string, Diagnostics.Stopwatch>()

let killBackground () =
  for kv in backgroundKillers do
    try kv.Value () with _ -> ()

AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> killBackground ())

/// The last lines of a log with colour removed, for a failure message.
let tailOfLog (log: string) (count: int) : string =
  match File.Exists log with
  | false -> "(no log was written)"
  | true ->
    File.ReadAllLines log
    |> Array.map FailureReport.stripAnsi
    |> fun lines -> lines[max 0 (lines.Length - count) ..]
    |> String.concat "\n"

let backgroundTailLines = 40
let backgroundTimeout = TimeSpan.FromMinutes 30.0

/// Start `steps` in order (stopping at the first one that fails) beside whatever the pipeline is doing.
let startBackground (name: string) (steps: BackgroundStep list) : Background =
  Directory.CreateDirectory tierWork |> ignore
  backgroundClock[name] <- Diagnostics.Stopwatch.StartNew()
  let completions =
    steps
    |> List.map (fun _ -> Threading.Tasks.TaskCompletionSource<Result<unit, string>>(Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously))
    |> Array.ofList
  let watch = { noWatch with OnStarted = fun killTree -> backgroundKillers[name] <- killTree }
  let rec go (index: int) (remaining: BackgroundStep list) : Async<unit> =
    async {
      match remaining with
      | [] -> ()
      | step :: rest ->
        let log = Path.Combine(tierWork, sprintf "%s-%d.log" name index)
        let! code = execToLogWatching watch backgroundTimeout step.WorkingDir step.Env log step.Argv
        match code with
        | 0 ->
          completions[index].SetResult(Result.Ok ())
          return! go (index + 1) rest
        | failed ->
          let reason =
            sprintf "%s: `%s` exited %d (log: %s)\n%s" name (String.concat " " step.Argv) failed log (tailOfLog log backgroundTailLines)
          // This step and every one after it did not pass.
          for i in index .. completions.Length - 1 do
            completions[i].TrySetResult(Result.Error reason) |> ignore
    }
  Async.Start(go 0 steps)
  { Name = name; Steps = completions }

/// Wait for a background job up to and including step `through` (0-based) and report it.
let joinBackground (job: Background) (through: int) : Async<Result<unit, string>> =
  async {
    let! result = Async.AwaitTask job.Steps[through].Task
    let seconds = backgroundClock[job.Name].Elapsed.TotalSeconds
    printfn "── background %-20s step %d %s after %.0fs (it ran beside the stages before this one)" job.Name through (match result with Result.Ok () -> "passed" | Result.Error _ -> "FAILED") seconds
    return result
  }

/// Filled in by the stage that starts each job, read by the stage that joins it.
let backgroundJobs = Collections.Concurrent.ConcurrentDictionary<string, Background>()

let startBackgroundOnce (name: string) (steps: BackgroundStep list) =
  backgroundJobs.GetOrAdd(name, fun n -> startBackground n steps) |> ignore

let joinBackgroundNamed (name: string) (through: int) : Async<Result<unit, string>> =
  async {
    match backgroundJobs.TryGetValue name with
    | true, job -> return! joinBackground job through
    | false, _ -> return Result.Error(sprintf "background job %s was never started" name)
  }

/// How many contract-test scripts run at once. Each is its own `dotnet fsi` of one file.
let contractTestParallelism = 6

/// The VS Code extension work: needs only node and its own sources, nothing the .NET build produces. The step
/// numbers are what the three stages that used to run it join: compile (2), the test-electron compile (3) and the
/// client contract tests (4 and 5).
let vscodeJobSteps : BackgroundStep list =
  let inVscode argv = { Argv = argv; WorkingDir = vscodeDir; Env = [] }
  [ inVscode [ "dotnet"; "tool"; "restore" ]
    // `npm ci` deletes node_modules and reinstalls: skipped while the lockfile and node are the ones it was installed from.
    inVscode [ "sh"; "-c"; BuildStamps.npmCiScript [ "--include=dev" ] ]
    inVscode [ "npm"; "run"; "compile" ]
    inVscode [ "npm"; "run"; "compile:test-electron" ]
    inVscode [ "npm"; "run"; "test:golden" ]
    inVscode [ "sh"; "-c"; sprintf "printf '%%s\\0' tests/*.fsx | xargs -0 -n 1 -P %d dotnet fsi" contractTestParallelism ] ]

let vscodeCompileThrough = 2
let vscodeTestHostThrough = 3
let vscodeContractThrough = 5

/// The test assembly for every framework but the primary one: restore into a private obj tree, put the shipped closure's
/// intermediate outputs there (`TierPlan.seedScript`), then compile only the test assembly against them. It needs the
/// closure built first (build/GateProducts.proj), so the "build" stage starts it once that has finished.
let net10BuildSteps () : BackgroundStep list =
  let ofCommand (command: string) =
    { Argv = command.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray; WorkingDir = rootDir; Env = [] }
  TierPlan.Framework.all
  |> List.filter (fun f -> f <> TierPlan.Framework.primary)
  |> List.collect (fun framework ->
    match TierPlan.testBuildCommands framework with
    | [ restore; build ] ->
      [ ofCommand restore
        { Argv = [ "sh"; "-c"; TierPlan.seedScript framework ]; WorkingDir = rootDir; Env = [] }
        ofCommand build ]
    | other -> failwithf "expected a restore then a build for %A, got %A" framework other)

/// The format check: reads the tree, writes nothing. It used to wait for the ratchets (about 9 s) and then take 7 s itself.
let formatJobSteps : BackgroundStep list =
  [ { Argv = [ "dotnet"; "format"; "--verify-no-changes"; "--verbosity"; "minimal" ]; WorkingDir = rootDir; Env = [] } ]

/// The samples the integration suites open sessions on. Built by the solution build already, so these are the
/// checks that each one is up to date; same commands, run beside the ratchets instead of after them.
let sampleProjects =
  [ "samples/demos/SageFs.Samples.WebappDatastar/SageFs.Samples.WebappDatastar.fsproj"
    "samples/from-csharp/SageFs.Samples.FromCSharp/SageFs.Samples.FromCSharp.fsproj"
    "samples/demos/SageFs.Samples.ConsoleTicker/SageFs.Samples.ConsoleTicker.fsproj" ]

let sampleJobSteps : BackgroundStep list =
  sampleProjects
  |> List.map (fun project -> { Argv = [ "dotnet"; "build"; project; "-c"; "Release"; "--nologo" ]; WorkingDir = rootDir; Env = [] })

/// The FSI host every tier shares, built into the shared cache once. Cold it costs about 12 s and 24 CPU seconds
/// (0.6 s warm), which used to sit between the last stage and the first tier.
let fsiHostJobSteps : BackgroundStep list =
  [ { Argv = [ "dotnet"; testDll; "--prebuild-host" ]; WorkingDir = rootDir; Env = [ "SAGEFS_HOST_CACHE_DIR", sharedHostCache ] } ]

// ---- the forked MCP SDK's packs, skipped when nothing they read changed --------

/// stdout of `argv` in `workingDir`, trimmed; empty when it cannot run or fails (a key built from it then differs
/// from any stamp, so the stage runs).
let outputOf (workingDir: string) (argv: string list) : string =
  match captureOf workingDir argv with
  | Result.Ok text -> text
  | Result.Error _ -> ""

let mcpPackProjects = [ mcpSdkCoreDir; mcpSdkClientDir; mcpSdkAspNetCoreDir ]

let mcpPackCommands =
  mcpPackProjects |> List.map (fun dir -> $"dotnet pack \"{dir}\" -o \"{mcpNupkgDir}\" -c Release -p:NuGetAudit=false")

let mcpPackStampFile = Path.Combine(mcpNupkgDir, ".pack-stamp")

/// What the packs read: the fork's commit and whether its tree is clean, the SDK that compiles it, and the commands.
let mcpPackKey () : string =
  BuildStamps.keyOf
    [ "mcp-sdk head", outputOf mcpSdkDir [ "git"; "rev-parse"; "HEAD" ]
      "mcp-sdk status", outputOf mcpSdkDir [ "git"; "status"; "--porcelain" ]
      "dotnet sdk", outputOf rootDir [ "dotnet"; "--version" ]
      "commands", String.concat "\n" mcpPackCommands ]

/// A package per project must be there: `<Project>.<version>.nupkg`, the version starting with a digit so
/// ModelContextProtocol does not match ModelContextProtocol.Core's package.
let mcpPackOutputs () : BuildStamps.Outputs =
  mcpPackProjects
  |> List.tryPick (fun dir ->
    let name = Path.GetFileName dir
    let found =
      match Directory.Exists mcpNupkgDir with
      | false -> false
      | true ->
        Directory.GetFiles(mcpNupkgDir, name + ".*.nupkg")
        |> Array.exists (fun f ->
          let file = Path.GetFileName f
          file.Length > name.Length + 1 && Char.IsDigit file[name.Length + 1])
    match found with
    | true -> None
    | false -> Some name)
  |> function
    | None -> BuildStamps.Outputs.Present
    | Some name -> BuildStamps.Outputs.Missing(sprintf "the %s package" name)

// ---- the pipeline ------------------------------------------------------------

/// Run shell steps in order, stopping at the first failure.
let rec runSteps (runCommand: string -> Async<Result<unit, string>>) (steps: string list) =
  async {
    match steps with
    | [] -> return Ok()
    | step :: rest ->
      match! runCommand step with
      | Ok () -> return! runSteps runCommand rest
      | Error e -> return Error e
  }

pipeline "sagefs" {
  description
    "SageFs CI as one typed F# pipeline: restore the forked MCP SDK, build once \
     in Release, then run every check --no-build off that output. Linux leg: \
     format, unit suite, mutation gate, VSIX + nupkg + release manifest. Windows \
     leg: samples + the real-daemon integration-host suites. Identical locally \
     and in GitHub Actions."
  workingDir rootDir
  timeout 3600
  timeoutForStep 900
  collapseGithubActionLogs

  stage "source ratchets" {
    // The ratchets that read only source text (file-size and blocking-call budgets), in seconds, before anything
    // is restored or built. An over-budget file used to be reported after the whole Release build. They read the
    // same table the compiled ratchet lane reads (SageFs.Tests/RatchetBudgets.fs), so this is the same check run
    // earlier: nothing is weakened, and the compiled lane below still runs it again.
    timeoutForStep 120
    run $"dotnet fsi scripts/ratchets-source.fsx -- \"{rootDir}\""
    // The VS Code work needs nothing the .NET build makes, so it starts now and runs beside the restore and the
    // build. The three "vscode ..." stages below join it.
    run (fun _ ->
      async {
        startBackgroundOnce "vscode" vscodeJobSteps
        return Ok()
      })
  }

  stage "restore mcp sdk fork" {
    timeoutForStep 300
    // CI checks the fork out via actions/checkout before this runs; locally we
    // clone it once so the script is a genuine one-command bootstrap. (The empty
    // mcp-sdk-nupkg/ that nuget.config points at is tracked via .gitkeep so the
    // script's own `#r "nuget:"` restore resolves before this stage packs it.)
    run (fun ctx ->
      async {
        if Directory.Exists mcpSdkDir then return Ok()
        else return! ctx.RunCommand $"git clone --depth 1 {mcpSdkRepoUrl} \"{mcpSdkDir}\""
      })
    // The three packs cost 7.5 s of a quiet gate and make the same package every time: the fork is cloned once
    // and never moves. They run only when the fork's commit, its working tree, the SDK or the commands changed, or
    // a package is missing (BuildStamps decides; the key is printed).
    run (fun ctx ->
      async {
        let key = mcpPackKey ()
        match BuildStamps.decide key (BuildStamps.read mcpPackStampFile) (mcpPackOutputs ()) with
        | BuildStamps.Verdict.UpToDate ->
          printfn "mcp sdk packs: up to date (stamp %s), not packing" (key.Substring(0, 12))
          return Ok()
        | BuildStamps.Verdict.Rebuild because ->
          printfn "mcp sdk packs: %s, packing" because
          match! runSteps ctx.RunCommand mcpPackCommands with
          | Ok () ->
            BuildStamps.write mcpPackStampFile key
            return Ok()
          | Error e -> return Error e
      })
  }

  stage "restore harmony fork" {
    timeoutForStep 900
    // LibHarmony is cloned INSIDE this repo, which is safe because it carries its own
    // Directory.Packages.props that opts out of this repo's central package management.
    run (fun ctx ->
      async {
        match File.Exists harmonyNupkg with
        | true -> return Ok()
        | false ->
          return!
            runSteps ctx.RunCommand [
              if not (Directory.Exists harmonyDir) then $"git clone --no-checkout {harmonyRepoUrl} \"{harmonyDir}\""
              $"git -C \"{harmonyDir}\" fetch --depth 1 origin {harmonyCommit}"
              $"git -C \"{harmonyDir}\" checkout --force {harmonyCommit}"
              $"git -C \"{harmonyDir}\" submodule update --init --recursive --depth 1"
              // build, THEN pack --no-build: a combined `dotnet pack` skips the ILRepack step that
              // produces the merged 0Harmony.dll (LibHarmony's own pipeline uses the same two steps).
              $"dotnet build \"{harmonyDir}/Lib.Harmony/Lib.Harmony.csproj\" -c Release -p:SageFsBuild={harmonyBuild}"
              $"dotnet pack \"{harmonyDir}/Lib.Harmony/Lib.Harmony.csproj\" -c Release --no-build -o \"{harmonyNupkgDir}\" -p:SageFsBuild={harmonyBuild}"
            ]
      })
  }

  stage "build" {
    // Build the whole solution ONCE, in Release. Every downstream stage runs
    // --no-build against this exact output — the single build that used to be
    // repeated in build/integration-host/extensions/release-artifacts.
    //
    // Three steps. The shipped closure (Core, Simulation, SageFs, Host; every one of them multi-targets both
    // frameworks) is built first by build/GateProducts.proj. Then the solution build (the net11 test assembly, the
    // fixtures, the samples) and the other frameworks' test assemblies run side by side: the latter compile ONLY the
    // test assembly, against the closure the first step made (TierPlan.testBuildCommands), so nothing is compiled
    // twice. "build other frameworks" below joins them. They share the machine, not files: the net10 test
    // assembly's intermediate files are in a private obj tree.
    run "dotnet restore"
    run "dotnet build build/GateProducts.proj -c Release --no-restore"
    run (fun ctx ->
      async {
        startBackgroundOnce "net10-tests" (net10BuildSteps ())
        return! ctx.RunCommand "dotnet build -c Release --no-restore"
      })
  }

  stage "ratchets" {
    // Every ratchet before anything slow. A line budget, a blocking-call budget, a
    // literal count, a stale generated page or a CI-wiring check used to fail
    // twenty minutes into the gate; they are pure reads of the tree, so they run
    // here, straight after the one build, in seconds. A red ratchet fails this
    // stage, and a failed stage stops the pipeline: format, the sample builds, the
    // VS Code stages and every test tier never start. `--ratchets` runs through
    // TrustSignal.run, so zero ratchets registered or ran is NothingRan (exit 3).
    timeoutForStep 300
    run (fun _ ->
      async {
        // Nothing below depends on the ratchets' verdict (the format check and the sample builds read and build
        // what the build stage made; the host build needs only the test assembly), so they start NOW and the
        // stages that used to run them after the ratchets only join them. A red ratchet still fails this stage
        // and stops the pipeline, and the jobs are killed when it exits.
        startBackgroundOnce "format" formatJobSteps
        startBackgroundOnce "samples" sampleJobSteps
        Directory.CreateDirectory sharedHostCache |> ignore
        startBackgroundOnce "fsi-host" fsiHostJobSteps
        return! runRatchetLane ()
      })
  }

  stage "build other frameworks" {
    // The test assembly for every other framework a tier runs on. Built here,
    // once, so the tiers (which run concurrently, each in a clone of this tree)
    // never build. TierPlan.testBuildCommands explains why these builds leave the
    // tracked lock files and the primary build's obj/ alone. It sits AFTER the
    // ratchets because it costs about a minute and a half (restore plus compile)
    // and the ratchets need only the primary build: a red ratchet should not wait for it.
    //
    // It now RUNS beside the primary build (started by "build"), and this stage
    // joins it, so what is left to wait for here is only what the net10 build
    // costs beyond the net11 build and the ratchets it overlapped.
    run (fun _ -> joinBackgroundNamed "net10-tests" (net10BuildSteps().Length - 1))
  }

  stage "format" {
    // `dotnet format --verify-no-changes --verbosity minimal`, started by "ratchets" and joined here.
    run (fun _ -> joinBackgroundNamed "format" (formatJobSteps.Length - 1))
  }

  stage "build samples for integration suites" {
    // The HTTP API integration suites create real sessions on these samples.
    //
    // A session on an UNBUILT sample does not fail loudly — it warms up,
    // cannot find the DLL, and faults, so the test reports "session should
    // reach Ready ... Actual value was false". That is what a missing entry
    // here looks like from the outside, and it cost a full CI cycle when
    // McpAppRunOutcomeTests started sessioning on ConsoleTicker without one.
    // `Architecture — every sample an integration suite sessions on is built
    // by CI` now fails the fast local suite instead of waiting for CI.
    //
    // The three `dotnet build samples/... -c Release --nologo` commands (sampleProjects) were started by
    // "ratchets" and run beside it; this stage joins them.
    run (fun _ -> joinBackgroundNamed "samples" (sampleJobSteps.Length - 1))
  }

  stage "vscode extension compile" {
    // Needed by both the VS Code command-proof suite (loads the extension from
    // source) and the VSIX package step. The work ran beside the build (started
    // by "source ratchets", see "work that runs beside the stages"); this stage
    // is where a failure in it stops the pipeline.
    run (fun _ -> joinBackgroundNamed "vscode" vscodeCompileThrough)
  }

  stage "vscode command-proof host" {
    // @vscode/test-electron harness for the command-proof suite run under
    // --integration-host. Joined here; it ran beside the build.
    run (fun _ -> joinBackgroundNamed "vscode" vscodeTestHostThrough)
  }

  stage "vscode client contract tests" {
    // Real gates that existed but ran nowhere (outcome-gate-sweep.md Gap
    // B.4): the golden server->client SSE round trip (loads the REAL
    // fable-out/LiveTestingListener.js built by "vscode extension compile"
    // above and feeds it the committed fixtures) plus every standalone
    // sagefs-vscode/tests/*.fsx contract test. Structural, not an enumerated
    // list, so a new *.fsx contract test is picked up here by construction —
    // the same "join CI by construction" discipline TestInfrastructure.
    // Integration.hostList applies on the .NET side. `npm run test:golden`
    // and `tests/*.fsx` (six at a time) ran beside the build; a failure in
    // either stops the pipeline here.
    run (fun _ -> joinBackgroundNamed "vscode" vscodeContractThrough)
  }

  stage "test tiers" {
    // EVERY test tier, each once, whatever happens to the others — scheduled by
    // runTiers (longest expected first; concurrently in private copy-on-write
    // clones when this machine supports it, else one at a time). Judged by the
    // "trust report" stage below, not by this step's exit: a red tier must
    // never hide the tiers after it.
    //
    //   always:   the default suite and the integration-host suites (real FSI
    //             sessions, real hosts, real daemons, the VS Code command-proof).
    //   `ci`:     the mutation-score gate and every real-browser journey —
    //             CI-gated so the fast local loop never fetches a browser.
    timeoutForStep 5400
    run (fun ctx ->
      async {
        let ci = fsi.CommandLineArgs |> Array.contains "ci"
        // The host tier is sharded: its suites are sequenced WITHIN a process
        // (shared in-process state), not across processes, so each shard is its
        // own concurrent tier. SAGEFS_HOST_SHARDS overrides the count.
        let hostShards =
          match Int32.TryParse(Environment.GetEnvironmentVariable "SAGEFS_HOST_SHARDS") with
          | true, n when n >= 1 -> n
          | _ ->
            // The machine as the host tier sees it: the threads this process may use and the memory the runtime may use.
            let machine : TierSchedule.Machine =
              { UsableThreads = Environment.ProcessorCount
                TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes }
            let count = TierSchedule.hostShardCount machine (readJsonMap suiteDurationsFile)
            printfn "host shards: %d (cap %d for %d threads, %d GiB; %d hosts each, %d live of at most %d)"
              count (TierSchedule.hostShardCap machine) machine.UsableThreads
              (machine.TotalMemoryBytes / TierPlan.Admission.bytesPerGiB) TierSchedule.hostsPerShard
              (count * TierSchedule.hostsPerShard) (TierSchedule.liveHostCap machine)
            count
        let ltShards = 4
        let always =
          testTier "--summary"
          // The default suite on the net10 tool asset too (built in the "build"
          // stage). Concurrent with the other tiers, so it adds one slot's worth
          // of load, not its duration, to the wall clock.
          :: testTierOn TierPlan.Net10 "--summary"
          :: [ for k in 1 .. hostShards -> testTier $"--integration-host --shard {k}/{hostShards} --summary" ]
        let ciOnly =
          [ testTier "--mutation-score"
            testTier "--integration-browser --summary"
            testTier "--integration-hr --summary"
            testTier "--integration-hr-restart --summary"
            testTier "--integration-hr-delta --summary"
            // The live-testing cases edit the sample in place, so a tier is a share of them on a copy of its own.
            for k in 1 .. ltShards do
              testTier $"--integration-lt --shard {k}/{ltShards} --summary"
            testTier "--integration-disconnect --summary" ]
        let browserTiers = ciOnly |> List.filter (fun t -> t.Name <> "--mutation-score")
        // Chromium is installed ONCE, before any browser tier starts (they run
        // concurrently). If it fails, those tiers are recorded as failed — never
        // silently dropped from the report.
        let! chromium =
          match ci with
          | false -> async { return Ok() }
          | true ->
            ctx.RunCommand $"{testBinDir}/.playwright/node/linux-x64/node {testBinDir}/.playwright/package/cli.js install chromium"
        let runnable =
          match ci, chromium with
          | false, _ -> always
          | true, Ok () -> always @ ciOnly
          | true, Error e ->
            printfn "Chromium install failed (%s): the browser tiers cannot run" e
            for t in browserTiers do
              lock invokedTiers (fun () -> invokedTiers.Add((t.Name, t.Args, false)))
            always @ [ List.head ciOnly ]
        // The FSI host every tier shares was being built beside the ratchets; wait for it. A failure is not fatal
        // here (the tiers' own prebuild below tries again and says so), but it is never silent.
        match! joinBackgroundNamed "fsi-host" (fsiHostJobSteps.Length - 1) with
        | Ok () -> ()
        | Error e -> printfn "FSI host prebuild (beside the ratchets) failed, the tiers will retry it: %s" e
        // Tiers that already went green on this commit with these exact bytes take their record (PassRecord),
        // and say so; the rest run. Tiers that ran green on a clean tree leave a record for the next run.
        let toRun = takeRecordedPasses runnable
        match toRun with
        | [] -> printfn "pass records: every tier took a record, nothing to run (--fresh runs them all)"
        | _ -> do! runTiers toRun
        restorePartition ()
        recordGreenPasses toRun
        return Ok()
      })
  }

  stage "trust report" {
    // ONE table for every tier this run invoked: registered vs ran vs result,
    // plus the verdict. Fails the pipeline if any tier is not Trusted —
    // failed, errored, ran nothing, ran a different count than it registered,
    // or never reported at all. Written to the GitHub step summary too.
    run (fun _ ->
      async {
        let lines = trustLines ()
        let table = renderTrustTable lines
        printfn "%s" table
        // Kept beside the ledger so a local gate can record it with the pass.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName trustLedger, "trust-report.md"), table + "\n")
        // When each tier started, how long it ran and what it cost, so the critical path is visible without a log.
        let timing = TierSchedule.renderTimingTable (tierTimings.Values |> List.ofSeq)
        printfn "%s" timing
        File.WriteAllText(Path.Combine(Path.GetDirectoryName trustLedger, "tier-timing.md"), timing + "\n")
        match Environment.GetEnvironmentVariable "GITHUB_STEP_SUMMARY" with
        | null | "" -> ()
        | summary -> File.AppendAllText(summary, table + "\n\n" + timing + "\n")
        // The first failure again, last, so it is the thing on screen when the run ends. Also written beside the
        // ledger, so the local gate and the ship can show it without anyone opening a log.
        let failureReportFile = Path.Combine(Path.GetDirectoryName trustLedger, "failure-report.txt")
        if File.Exists failureReportFile then File.Delete failureReportFile
        match firstFailure.Value with
        | None -> ()
        | Some failure ->
          let extra =
            alsoFailed |> Seq.map (fun (tier, case) -> sprintf "%salso failed, tier %s: %s" FailureReport.linePrefix tier case) |> List.ofSeq
          let all = failure.Report @ extra
          File.WriteAllText(failureReportFile, String.concat "\n" all + "\n")
          printfn "%s" (String.concat "\n" all)
        match lines |> List.filter (fun l -> l.Red) with
        | [] when lines.IsEmpty -> return Error "trust report: no test tier ran at all"
        | [] -> return Ok()
        | red ->
          let first =
            match firstFailure.Value with
            | Some f -> sprintf " First failure: %s (tier %s)." f.Case f.Tier
            | None -> ""
          return
            Error(
              sprintf "trust report: %d of %d tier(s) not trusted: %s.%s" red.Length lines.Length
                (red |> List.map (fun l -> $"{l.Tier} ({l.Verdict})") |> String.concat ", ") first)
      })
  }

  stage "package vscode extension" {
    // Produce the shippable VSIX straight into release/ (no separate artifact
    // hand-off between jobs).
    whenCmdArg "release"
    workingDir vscodeDir
    run (fun ctx ->
      async {
        // Start release/ empty. The local gate reuses one checkout, and git clean
        // keeps ignored output, so release/ used to pile up every old nupkg.
        // The installability check then read an old one, and the manifest listed
        // all of them.
        if Directory.Exists releaseDir then Directory.Delete(releaseDir, true)
        Directory.CreateDirectory releaseDir |> ignore
        let vsixOut = Path.Combine(releaseDir, $"sagefs-vscode-{pkgJsonVersion ()}.vsix")
        return! ctx.RunCommand $"npx @vscode/vsce package -o \"{vsixOut}\""
      })
  }

  stage "pack release bundle" {
    // Pack the nupkg, verify it is installable and version-aligned, and write
    // the manifest publish.yml consumes — all straight into release/, uploaded
    // by the one build job. This is the whole former release-artifacts job.
    whenCmdArg "release"
    run (fun _ -> async { verifyVersionAlignment (); return Ok() })
    run (fun _ ->
      async {
        Directory.CreateDirectory releaseDir |> ignore
        return Ok()
      })
    run "dotnet pack SageFs -c Release -o release"
    run (fun _ -> async { verifyToolInstallable (); return Ok() })
    run (fun _ -> async { writeReleaseManifest (); return Ok() })
  }

  stage "packaged tool smoke" {
    // Install the just-packed nupkg as a real tool and run it — the one check
    // that exercises the SHIPPED, INSTALLED artifact rather than the build
    // output. Formerly smoke-test.yml (windows-latest); consolidated here on
    // Linux, cross-platform (no pwsh). Installs to a throwaway --tool-path so it
    // never touches a developer's global tools, then runs `--version`. Runs
    // after "pack release bundle", so it is gated on "release" (present on a
    // master push). This preserves the packaging-regression guard
    // (uninstallable / unlaunchable tool) that verifyToolInstallable's
    // nupkg-structure check alone cannot catch.
    //
    // SageFs now multi-targets net10.0;net11.0 (issue #131), so this smoke
    // test runs TWICE — once per SDK the tool claims to support — each under
    // its own throwaway working dir carrying a global.json PINNED (rollForward
    // "disable") to that exact SDK, so `dotnet` resolution can't accidentally
    // fall through to the other one. A single run under whichever SDK happens
    // to be ambient would only ever prove ONE of the two payloads works.
    whenCmdArg "release"
    timeoutForStep 300
    run (fun _ ->
      async {
        let sdks = [ "10.0.401", "net10.0"; "11.0.100-rc.1.26425.128", "net11.0" ]
        let smokeOneSdk (sdkVersion: string, tfm: string) =
          async {
            let workDir = Path.Combine(rootDir, $".smoke-{tfm}")
            if Directory.Exists workDir then Directory.Delete(workDir, true)
            Directory.CreateDirectory workDir |> ignore
            File.WriteAllText(
              Path.Combine(workDir, "global.json"),
              $"""{{"sdk":{{"version":"{sdkVersion}","rollForward":"disable"}}}}""")
            let toolPath = Path.Combine(workDir, "tool")
            let log = Path.Combine(workDir, "smoke.log")
            let! installExit =
              execToLog (TimeSpan.FromMinutes 10.0) workDir [] log
                [ "dotnet"; "tool"; "install"; "SageFs"; "--tool-path"; toolPath
                  "--version"; pkgJsonVersion ()
                  "--add-source"; releaseDir; "--no-cache" ]
            match installExit with
            | 0 ->
              // `--version` proves the packed tool installed and launches
              // (catches the uninstallable-package / dropped-exe /
              // missing-dll-at-startup class). We deliberately do NOT run
              // `check` here: it probes daemon and port state, whose exit
              // semantics on a clean runner are not pinned, and a smoke stage
              // must never be the flaky one.
              let exe = Path.Combine(toolPath, "sagefs")
              let! versionExit = execToLog (TimeSpan.FromMinutes 2.0) workDir [] log [ exe; "--version" ]
              match versionExit with
              | 0 ->
                printfn "OK: SageFs installs and runs under .NET SDK %s (%s build)" sdkVersion tfm
                return Ok()
              | code -> return Error $"'sagefs --version' under SDK {sdkVersion} (expected {tfm} build) exited {code}; see {log}"
            | code -> return Error $"tool install under SDK {sdkVersion} (expected {tfm} build) exited {code}; see {log}"
          }
        let! results = sdks |> List.map smokeOneSdk |> Async.Sequential
        match results |> Array.toList |> List.choose (function Error e -> Some e | Ok () -> None) with
        | [] -> return Ok()
        | errors -> return Error(String.concat "; " errors)
      })
  }

  runIfOnlySpecified false
}

tryPrintPipelineCommandHelp ()
