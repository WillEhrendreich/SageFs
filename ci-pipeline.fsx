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
// Every downstream check runs against this ONE Release build (see "build" stage).
let testBinDir = "SageFs.Tests/bin/Release/net10.0"
let testDll = $"{testBinDir}/SageFs.Tests.dll"

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
      "Version drift: Directory.Build.props is %s but sagefs-vscode/package.json is %s. Run scripts/pre-commit or bump package.json to %s."
      p k p
  printfn "Versions aligned at %s" p

/// issue #131: a tool packaged for the wrong TFM is uninstallable on net10 SDKs
/// ("DotnetToolSettings.xml was not found"). Fail HERE so it can never ship.
let verifyToolInstallable () =
  match Directory.GetFiles(releaseDir, "SageFs.*.nupkg") |> Array.tryHead with
  | None -> failwith "No SageFs nupkg found in release/ — pack failed?"
  | Some nupkg ->
    use zip = ZipFile.OpenRead nupkg
    let tfms =
      zip.Entries
      |> Seq.map (fun e -> e.FullName)
      |> Seq.filter (fun n -> n.StartsWith "tools/" && n.EndsWith "/any/DotnetToolSettings.xml")
      |> Seq.map (fun n -> (n.Split('/')).[1])
      |> Seq.distinct
      |> Seq.toList
    match tfms with
    | [] ->
      failwithf "DotnetToolSettings.xml not found in %s — net10 SDK installs will fail (issue #131)." (Path.GetFileName nupkg)
    | tfms ->
      for tfm in tfms do
        if tfm <> "net10.0" then
          failwithf "Tool package targets %s, but net10-SDK users cannot install it (issue #131). The repo must pack net10.0." tfm
      printfn "OK %s is installable on net10 SDKs." (Path.GetFileName nupkg)

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
// the mount is required), longest-expected tier first. `TrustSignalTests` fails
// the fast suite if a registered tier is not declared here, or if a test run
// bypasses `testTier`.

#load "build/TierPlan.fs"
open SageFs.Build

let trustLedger = Path.Combine(rootDir, "test-results", "trust-ledger.jsonl")
Directory.CreateDirectory(Path.GetDirectoryName trustLedger) |> ignore
if File.Exists trustLedger then File.Delete trustLedger

/// (tier, args, did the tier's process exit 0) for every tier this pipeline ran.
let invokedTiers = Collections.Generic.List<string * string * bool>()

let tierNameOf = TierPlan.nameOfArgs

/// Declare a test tier: one `dotnet SageFs.Tests.dll <args>` invocation.
let testTier (args: string) = TierPlan.tier args

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
let execToLog (workingDir: string) (env: (string * string) list) (log: string) (argv: string list) =
  async {
    let psi = Diagnostics.ProcessStartInfo(List.head argv)
    List.tail argv |> List.iter psi.ArgumentList.Add
    psi.WorkingDirectory <- workingDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    for (k, v) in env do psi.Environment[k] <- v
    use writer = new StreamWriter(log, false)
    let gate = obj ()
    let write (line: string) = if not (isNull line) then lock gate (fun () -> writer.WriteLine line)
    use p = new Diagnostics.Process(StartInfo = psi)
    p.OutputDataReceived.Add(fun e -> write e.Data)
    p.ErrorDataReceived.Add(fun e -> write e.Data)
    p.Start() |> ignore
    p.BeginOutputReadLine()
    p.BeginErrorReadLine()
    do! p.WaitForExitAsync() |> Async.AwaitTask
    p.WaitForExit() // flush the async readers
    return p.ExitCode
  }

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

/// Run one tier (in its own clone when isolated) and record its outcome.
let runTier (isolation: TierPlan.Isolation) (t: TierPlan.Tier) =
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
        "SAGEFS_SUITE_TIMINGS_OUT", Path.Combine(tierWork, safe + ".suites.json") ]
    let command = $"dotnet {testDll} {t.Args}"
    let sw = Diagnostics.Stopwatch.StartNew()
    let! code =
      match isolation with
      | TierPlan.Shared -> execToLog rootDir env log [ "sh"; "-c"; command ]
      | TierPlan.CopyOnWrite ->
        async {
          match exitOf [ "cp"; "-a"; "--reflink=always"; rootDir; clone ] with
          | 0 ->
            let argv = TierPlan.isolatedArgv (procId "Uid") (procId "Gid") rootDir clone tmpDir command
            return! execToLog rootDir env log argv
          | failed ->
            File.WriteAllText(log, sprintf "could not clone the checkout for this tier (cp exit %d)" failed)
            return failed
        }
    let seconds = sw.Elapsed.TotalSeconds
    lock invokedTiers (fun () -> invokedTiers.Add((t.Name, t.Args, (code = 0))))
    let tail =
      match code with
      | 0 -> ""
      | _ ->
        File.ReadAllLines log
        |> Array.map (fun l -> Text.RegularExpressions.Regex.Replace(l, "\x1b\\[[0-9;?]*[A-Za-z]", ""))
        |> fun lines -> lines[max 0 (lines.Length - 60) ..]
        |> String.concat "\n"
    lock invokedTiers (fun () ->
      printfn "── tier %-28s exit=%d in %.0fs  (log: %s)" t.Name code seconds log
      if tail <> "" then printfn "%s\n── end of %s" tail t.Name)
    // A passing tier's clone is only scratch; a failing one is kept to inspect.
    if code = 0 && Directory.Exists clone then
      try Directory.Delete(clone, true) with _ -> ()
    return t.Name, seconds
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
      execToLog rootDir [ "SAGEFS_HOST_CACHE_DIR", sharedHostCache ] (Path.Combine(tierWork, "prebuild-host.log"))
        [ "dotnet"; testDll; "--prebuild-host" ]
    printfn "FSI host prebuild: exit %d" prebuilt
    let requested =
      match Int32.TryParse(Environment.GetEnvironmentVariable "SAGEFS_TIER_PARALLEL") with
      | true, n -> Some n
      | _ -> None
    let slots = TierPlan.parallelism isolation Environment.ProcessorCount requested
    let durations = readDurations ()
    let ordered = TierPlan.order durations tiers
    let estimate (t: TierPlan.Tier) = durations.TryFind t.Name |> Option.defaultValue 0.0
    let serial = ordered |> List.sumBy estimate
    printfn "Tiers: %d, %d at a time (%A), order: %s" tiers.Length slots isolation
      (ordered |> List.map (fun t -> t.Name) |> String.concat ", ")
    if serial > 0.0 then
      printfn "Expected wall clock %.0fs (serial would be %.0fs), from recorded durations"
        (TierPlan.makespan slots estimate ordered) serial
    let queue = Collections.Concurrent.ConcurrentQueue<TierPlan.Tier>(ordered)
    let worker () =
      async {
        let results = ResizeArray()
        let mutable next = Unchecked.defaultof<TierPlan.Tier>
        while queue.TryDequeue(&next) do
          let! r = runTier isolation next
          results.Add r
        return List.ofSeq results
      }
    let! measured = List.init slots (fun _ -> worker ()) |> Async.Parallel
    // Merge ledgers (per-tier files: separate processes never share a writer).
    for t in tiers do
      let ledger = Path.Combine(tierWork, TierPlan.fileNameOf t.Name + ".jsonl")
      if File.Exists ledger then File.AppendAllText(trustLedger, File.ReadAllText ledger)
    // Per-suite timings from every shard feed the next run's balancing.
    let suiteTimings =
      tiers
      |> List.map (fun t -> Path.Combine(tierWork, TierPlan.fileNameOf t.Name + ".suites.json"))
      |> List.filter File.Exists
      |> List.fold (fun (acc: Map<string, float>) f ->
        readJsonMap f |> Map.fold (fun (m: Map<string, float>) k v -> m.Add(k, v)) acc) (readJsonMap suiteDurationsFile)
    try File.WriteAllText(suiteDurationsFile, JsonSerializer.Serialize suiteTimings) with _ -> ()
    let updated =
      measured |> Seq.concat |> Seq.fold (fun (m: Map<string, float>) (n, s) -> m.Add(n, s)) durations
    try
      Directory.CreateDirectory(Path.GetDirectoryName durationsFile) |> ignore
      File.WriteAllText(durationsFile, JsonSerializer.Serialize updated)
    with _ -> ()
  }

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
let trustLines () =
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
        { Tier = tier; Registered = "?"; Ran = "?"; Passed = "?"; Failed = "?"; Errored = "?"; Ignored = "?"
          Verdict = "NoReport"
          Detail =
            match stepOk with
            | true -> "the step passed but the process reported nothing"
            | false -> "the process failed before reporting (runner setup or crash)"
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
          Verdict = match greenVerdict, stepOk with | true, false -> "ExitMismatch" | _ -> verdict
          Detail =
            match greenVerdict, stepOk with
            | true, false -> "reported green, but the process exited non-zero"
            | _ -> str r "Detail"
          Red = not (greenVerdict && stepOk) } ]

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
    run $"dotnet pack \"{mcpSdkCoreDir}\" -o \"{mcpNupkgDir}\" -c Release -p:NuGetAudit=false"
    run $"dotnet pack \"{mcpSdkClientDir}\" -o \"{mcpNupkgDir}\" -c Release -p:NuGetAudit=false"
    run $"dotnet pack \"{mcpSdkAspNetCoreDir}\" -o \"{mcpNupkgDir}\" -c Release -p:NuGetAudit=false"
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
    run "dotnet build -c Release"
  }

  stage "format" {
    run "dotnet format --verify-no-changes --verbosity minimal"
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
    run "dotnet build samples/demos/SageFs.Samples.WebappDatastar/SageFs.Samples.WebappDatastar.fsproj -c Release --nologo"
    run "dotnet build samples/from-csharp/SageFs.Samples.FromCSharp/SageFs.Samples.FromCSharp.fsproj -c Release --nologo"
    run "dotnet build samples/demos/SageFs.Samples.ConsoleTicker/SageFs.Samples.ConsoleTicker.fsproj -c Release --nologo"
  }

  stage "vscode extension compile" {
    // Needed by both the VS Code command-proof suite (loads the extension from
    // source) and the VSIX package step.
    workingDir vscodeDir
    run "dotnet tool restore"
    run "npm ci"
    run "npm run compile"
  }

  stage "vscode command-proof host" {
    // @vscode/test-electron harness for the command-proof suite run under
    // --integration-host.
    workingDir vscodeDir
    run "npm run compile:test-electron"
  }

  stage "vscode client contract tests" {
    // Real gates that existed but ran nowhere (outcome-gate-sweep.md Gap
    // B.4): the golden server->client SSE round trip (loads the REAL
    // fable-out/LiveTestingListener.js built by "vscode extension compile"
    // above and feeds it the committed fixtures) plus every standalone
    // sagefs-vscode/tests/*.fsx contract test. Structural, not an enumerated
    // list, so a new *.fsx contract test is picked up here by construction —
    // the same "join CI by construction" discipline TestInfrastructure.
    // Integration.hostList applies on the .NET side.
    workingDir vscodeDir
    timeoutForStep 300
    run "npm run test:golden"
    run (fun ctx ->
      async {
        let fsxFiles =
          Directory.GetFiles(Path.Combine(vscodeDir, "tests"), "*.fsx")
          |> Array.sort
        return! runSteps ctx.RunCommand [ for f in fsxFiles -> $"dotnet fsi \"{f}\"" ]
      })
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
          | _ -> 5
        let always =
          testTier "--summary"
          :: [ for k in 1 .. hostShards -> testTier $"--integration-host --shard {k}/{hostShards} --summary" ]
        let ciOnly =
          [ testTier "--mutation-score"
            testTier "--integration-browser --summary"
            testTier "--integration-hr --summary"
            testTier "--integration-lt --summary"
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
        do! runTiers runnable
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
        match Environment.GetEnvironmentVariable "GITHUB_STEP_SUMMARY" with
        | null | "" -> ()
        | summary -> File.AppendAllText(summary, table + "\n")
        match lines |> List.filter (fun l -> l.Red) with
        | [] when lines.IsEmpty -> return Error "trust report: no test tier ran at all"
        | [] -> return Ok()
        | red ->
          return
            Error(
              sprintf "trust report: %d of %d tier(s) not trusted: %s" red.Length lines.Length
                (red |> List.map (fun l -> $"{l.Tier} ({l.Verdict})") |> String.concat ", "))
      })
  }

  stage "package vscode extension" {
    // Produce the shippable VSIX straight into release/ (no separate artifact
    // hand-off between jobs).
    whenCmdArg "release"
    workingDir vscodeDir
    run (fun ctx ->
      async {
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
    // never touches a developer's global tools, then runs `check` (SDK/FSI
    // reachable) and `--version`. Runs after "pack release bundle", so it is
    // gated on "release" (present on a master push). This preserves the
    // packaging-regression guard (uninstallable / unlaunchable tool) that
    // verifyToolInstallable's nupkg-structure check alone cannot catch.
    whenCmdArg "release"
    timeoutForStep 300
    run (fun ctx ->
      async {
        let toolPath = Path.Combine(rootDir, ".smoke-tool")
        if Directory.Exists toolPath then Directory.Delete(toolPath, true)
        let! install =
          ctx.RunCommand $"dotnet tool install SageFs --tool-path \"{toolPath}\" --add-source \"{releaseDir}\" --no-cache"
        match install with
        | Error e -> return Error e
        | Ok () ->
          // `--version` proves the packed tool installed and launches (catches
          // the uninstallable-package / dropped-exe / missing-dll-at-startup
          // class). We deliberately do NOT run `check` here: it probes daemon
          // and port state, whose exit semantics on a clean runner are not
          // pinned, and a smoke stage must never be the flaky one.
          let exe = Path.Combine(toolPath, "sagefs")
          return! ctx.RunCommand $"\"{exe}\" --version"
      })
  }

  runIfOnlySpecified false
}

tryPrintPipelineCommandHelp ()
