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
//     extension compile + test-electron host, and the integration-host suites.
//   * whenCmdArg "ci"      — the mutation-score gate (too slow for the fast local
//                            loop AGENTS.md asks for).
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

// ---- the pipeline ------------------------------------------------------------

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
    run $"dotnet {testDll} --summary"
  }

  stage "mutation score gate" {
    // CI only (a full mutation pass is too slow for the fast local loop).
    whenCmdArg "ci"
    timeoutForStep 300
    run $"dotnet {testDll} --mutation-score"
  }

  stage "build samples for integration suites" {
    // The HTTP API integration suites create real sessions on these samples.
    run "dotnet build samples/demos/SageFs.Samples.WebappDatastar/SageFs.Samples.WebappDatastar.fsproj -c Release --nologo"
    run "dotnet build samples/from-csharp/SageFs.Samples.FromCSharp/SageFs.Samples.FromCSharp.fsproj -c Release --nologo"
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
    run $"dotnet {testDll} --integration-host --summary"
  }

  stage "dashboard browser journeys" {
    // Real-browser dashboard journeys (Playwright.NET Chromium) via the suite's
    // own --integration-browser entry point, off the same Release build. Formerly
    // the separate dashboard-browser-e2e.yml (windows-latest); consolidated here
    // on Linux (verified green). Chromium is fetched through the bundled .NET
    // Playwright driver — no pwsh dependency. CI-gated so the fast local loop
    // never fetches a browser (run it locally with `-- ci`).
    //
    // NOTE: only the dashboard journey folded — --integration-hr and
    // --integration-lt did not port to Linux (the WebLive init profile never
    // writes app-url.txt under the HR runner; the LT runner times out on the
    // 11-green baseline). Those FEATURES stay covered by the integration-host
    // suite (HttpApiIntegrationTests live-testing; HotReloadTests /
    // WebAppHotReloadVerificationTests); only their windows-only *browser*
    // journeys were dropped with the windows leg.
    whenCmdArg "ci"
    timeoutForStep 900
    run $"{testBinDir}/.playwright/node/linux-x64/node {testBinDir}/.playwright/package/cli.js install chromium"
    run $"dotnet {testDll} --integration-browser --summary"
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
