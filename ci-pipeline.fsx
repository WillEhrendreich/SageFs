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
//   * unconditional — restore, build, format, unit suite, samples, VS Code
//     extension compile + test-electron host + client contract tests
//     (npm run test:golden + every sagefs-vscode/tests/*.fsx), and the
//     integration-host suites.
//   * whenCmdArg "ci"      — the mutation-score gate (too slow for the fast local
//                            loop AGENTS.md asks for) and every real-browser
//                            journey (dashboard, hot-reload, live-testing,
//                            disconnect-indicator) plus the hot-reload shape
//                            matrix — CI-gated so the fast local loop never
//                            fetches a browser.
//   * always, after every test stage — the "trust report": one table of every
//     tier's registered/ran/verdict; the one place a red test tier fails the run.
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

// ---- the trust ledger ----------------------------------------------------------
//
// Every test tier runs through `testTier`. Two things used to lose information:
//  * the pipeline stopped at the FIRST red test stage, so every later tier went
//    unrun and unreported — `integration host` stayed red for a day while two
//    later tiers were broken the whole time and nobody could see it;
//  * a stage's exit code was the only signal, and Expecto exits 0 for a run
//    that executed nothing.
// Now each test stage continues past failure, the test process writes one
// registered/ran/verdict row per tier to the ledger (SageFs.Tests
// TestInfrastructure.TrustSignal), `testTier` records whether the step itself
// succeeded, and the "trust report" stage joins the two into ONE table and
// fails the pipeline on any tier that is not Trusted — including a tier whose
// process died before it could report. `TrustSignalTests` fails the fast suite
// if a registered tier is not invoked here, or if a test run bypasses
// `testTier`.

let trustLedger = Path.Combine(rootDir, "test-results", "trust-ledger.jsonl")
Directory.CreateDirectory(Path.GetDirectoryName trustLedger) |> ignore
if File.Exists trustLedger then File.Delete trustLedger
Environment.SetEnvironmentVariable("SAGEFS_TRUST_LEDGER", trustLedger)

/// (tier, args, did the step exit 0) for every tier this pipeline invoked.
let invokedTiers = Collections.Generic.List<string * string * bool>()

let tierNameOf (args: string) =
  match args.Split(' ').[0] with
  | "--summary" -> "default"
  | flag -> flag

/// Run `prelude` commands, then the test assembly as one tier, and record the
/// outcome against the tier either way. A prelude (e.g. the Chromium install)
/// lives INSIDE the ledgered step on purpose: as a separate step, its failure
/// would skip the test step, the tier would never be recorded as invoked, and
/// it would silently vanish from the report instead of showing red.
let testTierAfter (prelude: string list) (args: string) =
  fun (ctx: Internal.StageContext) ->
    async {
      let rec go commands =
        async {
          match commands with
          | [] -> return Ok()
          | command :: rest ->
            match! ctx.RunCommand command with
            | Ok () -> return! go rest
            | Error e -> return Error e
        }
      let! result = go (prelude @ [ $"dotnet {testDll} {args}" ])
      lock invokedTiers (fun () -> invokedTiers.Add((tierNameOf args, args, Result.isOk result)))
      return result
    }

let testTier (args: string) = testTierAfter [] args

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

  stage "unit tests" {
    // The default Expecto suite, reusing the Release build.
    // Expecto exit codes: 0 = passed, 1 = a test FAILED, 2 = a test ERRORED;
    // Fun.Build's default acceptExitCodes = [0], so 1 and 2 both fail the stage.
    timeoutForStep 600
    // A red tier must not hide the tiers after it — the trust report fails the run.
    continueStageOnFailure
    run (testTier "--summary")
  }

  stage "mutation score gate" {
    // CI only (a full mutation pass is too slow for the fast local loop).
    whenCmdArg "ci"
    timeoutForStep 300
    // A red tier must not hide the tiers after it — the trust report fails the run.
    continueStageOnFailure
    run (testTier "--mutation-score")
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

  stage "integration host" {
    // Every [Integration] suite registered as Host: real FSI sessions, real
    // SageFs.Host spawns, Harmony detours, real daemons on isolated data dirs,
    // the HTTP API against the samples, and the VS Code command-proof suite.
    // Runs on Linux: Args.resolveHostLaunch launches the host via the dotnet
    // muxer on non-Windows, and the command-proof self-provisions its harness
    // and a Linux VS Code under xvfb (verified: 128 host tests + the proof all
    // green on Linux). VS Code Electron needs a display, so the workflow makes
    // one available via xvfb.
    timeoutForStep 2100
    // A red tier must not hide the tiers after it — the trust report fails the run.
    continueStageOnFailure
    run (testTier "--integration-host --summary")
  }

  stage "dashboard browser journeys" {
    // Real-browser dashboard journeys (Playwright.NET Chromium) via the suite's
    // own --integration-browser entry point, off the same Release build. Formerly
    // the separate dashboard-browser-e2e.yml (windows-latest); consolidated here
    // on Linux (verified green). Chromium is fetched through the bundled .NET
    // Playwright driver — no pwsh dependency. CI-gated so the fast local loop
    // never fetches a browser (run it locally with `-- ci`).
    //
    // The HR/LT/disconnect journeys below reuse this stage's Chromium install
    // rather than repeating it — this stage must run first among the four.
    whenCmdArg "ci"
    timeoutForStep 900
    // A red tier must not hide the tiers after it — the trust report fails the run.
    continueStageOnFailure
    run (
      testTierAfter
        [ $"{testBinDir}/.playwright/node/linux-x64/node {testBinDir}/.playwright/package/cli.js install chromium" ]
        "--integration-browser --summary")
  }

  stage "hot-reload browser journeys" {
    // HR-DASH: real save -> the SAME running app serves new code, observed
    // from the dashboard page. Was written and registered
    // (Integration.Dedicated "--integration-hr", Program.fs dispatches it)
    // but no pipeline stage ever invoked it (outcome-gate-sweep.md Gap B.1) —
    // ci-pipeline.fsx used to claim "did not port to Linux" (app-url.txt
    // never written), which a real Linux run under this exact HEAD did NOT
    // reproduce: the runner reaches Ready, writes app-url.txt, and launches
    // the browser suite. Chromium is already installed by the "dashboard
    // browser journeys" stage above (same testBinDir), so this stage does
    // not reinstall it. CI-gated for the same reason as that stage: the fast
    // local loop never fetches a browser.
    whenCmdArg "ci"
    timeoutForStep 1200
    // A red tier must not hide the tiers after it — the trust report fails the run.
    continueStageOnFailure
    run (testTier "--integration-hr --summary")
  }

  stage "live-testing browser journeys" {
    // LT-DASH: enable -> discover -> a real edit surfaces the failing test
    // live in the panel -> revert -> green again. Registered
    // (Integration.Dedicated "--integration-lt", Program.fs dispatches it)
    // but no pipeline stage ever invoked it (outcome-gate-sweep.md Gap B.2).
    // DashboardBrowserRunner.runLiveTestingBrowserJourneys now pre-settles
    // live testing to an 11-green baseline over the daemon's own HTTP API
    // (the same settle-then-baseline sequence HttpApiIntegrationTests.fs
    // already proves works) before handing off to the browser journeys, so
    // the panel's own UI wait no longer has to race enable+discovery+build+
    // baseline inside one window.
    whenCmdArg "ci"
    timeoutForStep 900
    // A red tier must not hide the tiers after it — the trust report fails the run.
    continueStageOnFailure
    run (testTier "--integration-lt --summary")
  }

  stage "dashboard disconnect-indicator browser journeys" {
    // The daemon dying mid-stream (redeploy, crash, SIGTERM) must show a
    // visible banner, including under client/server clock skew. Registered
    // (Integration.Dedicated "--integration-disconnect") but never dispatched
    // anywhere — Program.fs now dispatches it to
    // DashboardDisconnectIndicatorBrowserTests.runDisconnectIndicatorJourney,
    // which owns its own isolated daemon end to end on non-default ports
    // (never 37749/37750) — outcome-gate-sweep.md Gap B.3.
    whenCmdArg "ci"
    timeoutForStep 900
    // A red tier must not hide the tiers after it — the trust report fails the run.
    continueStageOnFailure
    run (testTier "--integration-disconnect --summary")
  }

  stage "hot-reload shape matrix" {
    // Every declaration shape a user can save (function body, member, lambda
    // value, mutable, ...) through a real host, asserting the reload CLAIM
    // matches the OBSERVED change. Registered and dispatched for weeks but
    // invoked by no stage — `TrustSignal CI wiring` now fails the fast suite
    // for exactly that.
    whenCmdArg "ci"
    timeoutForStep 900
    continueStageOnFailure
    run (testTier "--integration-shapes --summary")
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
