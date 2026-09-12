#!/usr/bin/env -S dotnet fsi
// ci-pipeline.fsx
//
// PROOF OF CONCEPT: the `build` job from .github/workflows/main.yml, expressed
// as one typed F# Fun.Build pipeline instead of a page of YAML `run:` blocks.
// The point is not "prettier YAML" -- it's that this script IS the pipeline.
// GitHub Actions no longer encodes any build/test/pack logic of its own; it
// only supplies the runner, the two checkouts, and one invocation:
//
//   dotnet fsi ci-pipeline.fsx            # local: restore, build, test, pack
//   dotnet fsi ci-pipeline.fsx -- ci      # CI:    the above + the mutation-score gate
//
// Both commands run the exact same stages against the exact same commands.
// There is no separate "CI logic" to drift out of sync with "what I run on
// my box" -- the `ci` cmdArg only toggles the one stage
// (mutation-score gate) that is deliberately CI-only because a full mutation
// pass is too slow for the fast local loop AGENTS.md asks for.
//
// SCOPE: this is a bounded proof covering ONLY the `build` job (restore the
// forked MCP SDK -> dotnet build -> default Expecto suite -> mutation gate ->
// dotnet pack). integration-host, extensions, benchmarks, and release-artifacts
// are untouched -- see sagefs-roast-4.md / the task brief for why this stays
// deliberately small.
//
// NOTE ON THE REAL PIPELINE'S ARTIFACTS: the `build` job today uploads no
// artifact at all -- `release-assets` is uploaded by the separate
// `release-artifacts` job (release/ dir, after its OWN `dotnet pack SageFs`),
// which merely `needs: [build, ...]` for a green gate, not for build's files.
// So there is nothing for this script to "preserve" on that front; the only
// contract `release-artifacts` has with `build` is "build must succeed."
// That still holds: any stage failure here fails the pipeline non-zero.

#r "nuget: Fun.Build, 1.2.0"

open System.IO
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

pipeline "sagefs-build" {
  description
    "SageFs CI 'build' job as typed F#: restore the forked MCP SDK, dotnet \
     build, the default Expecto suite, the mutation-score gate (CI only), \
     and dotnet pack. Identical locally and in GitHub Actions."
  workingDir rootDir
  timeout 1800
  timeoutForStep 900
  collapseGithubActionLogs

  stage "restore mcp sdk fork" {
    timeoutForStep 300
    // GitHub Actions checks the fork out with its own `actions/checkout`
    // step (see .github/workflows/main.yml) before this script ever runs,
    // so mcp-sdk/ already exists in CI. Locally it doesn't yet on a fresh
    // clone -- fetch it once so this script is a genuine one-command
    // bootstrap, same promise the repo's existing build.fsx makes.
    run (fun ctx ->
      async {
        if Directory.Exists mcpSdkDir then
          return Ok()
        else
          return! ctx.RunCommand $"git clone --depth 1 {mcpSdkRepoUrl} \"{mcpSdkDir}\""
      })
    run $"dotnet pack \"{mcpSdkCoreDir}\" -o \"{mcpNupkgDir}\" -c Release -p:NuGetAudit=false"
    run $"dotnet pack \"{mcpSdkClientDir}\" -o \"{mcpNupkgDir}\" -c Release -p:NuGetAudit=false"
    run $"dotnet pack \"{mcpSdkAspNetCoreDir}\" -o \"{mcpNupkgDir}\" -c Release -p:NuGetAudit=false"
  }

  stage "build" {
    // Matches the real job's `dotnet build` verbatim -- Debug config, no -c.
    // (Every OTHER job in the workflow builds -c Release; this one, today,
    // does not -- the test stage's --no-build depends on this exact output,
    // so the discrepancy is preserved rather than "fixed" out from under it.)
    run "dotnet build"
  }

  stage "test" {
    timeoutForStep 600
    // Expecto: 0 = all passed, 1 = a test FAILED, 2 = a test ERRORED (threw).
    // Fun.Build's default acceptExitCodes is [0], so both failure modes fail
    // this stage -- the YAML comment's warning about never laundering exit 2
    // into green is true here for free, not by convention.
    run "dotnet run --no-build --project SageFs.Tests -- --summary"
  }

  stage "mutation score gate" {
    // CI-only: proves the suite actually catches injected faults. A full
    // mutation pass is too slow to belong in the fast default local loop,
    // so this is the one stage the "full CI variant" adds over the local
    // subset -- exactly the `whenCmdArg "ci"` gate the brief asked for.
    whenCmdArg "ci"
    timeoutForStep 300
    run "dotnet run --no-build --project SageFs.Tests -- --mutation-score"
  }

  stage "pack" {
    // Mirrors the real pack command (release-artifacts job): `dotnet pack
    // SageFs -c Release -o release`. Included in both the local subset and
    // the CI variant per the brief's bounded scope (build + default suite +
    // pack) -- note this DOES give the build job a packing step it does not
    // have today; call this out before merging the YAML side of this proof.
    run "dotnet pack SageFs -c Release -o release"
  }

  runIfOnlySpecified false
}

tryPrintPipelineCommandHelp ()
