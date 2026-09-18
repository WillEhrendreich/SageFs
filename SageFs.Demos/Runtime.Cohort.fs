/// The cohort demo's HOST-SIDE fixture builder (cohort-demo-scenario-plan.md
/// §"The genuine work", item 1-2): the cohort's git operations run against
/// `Environment.CurrentDirectory` (`SageFs.Core/Mcp.fs`'s `mainRepoDir`), so
/// filming the `cohort-landing` scenario for real needs a real git repo with
/// a real, tiny, OFFLINE-buildable Expecto project sitting where the daemon
/// is launched — never this repo itself, never a fake/stubbed git history.
///
/// Mirrors two proven shapes rather than inventing a third:
///   - `SageFs.Tests/CohortLandingGateIntegrationTests.fs`'s fixture
///     discipline: `writeFixtureSources`, the raw-`Process` git helpers, the
///     "commit bin/obj on the base commit, untrack on the first work-branch
///     commit" trick that lets a fresh `git worktree add` warm up without
///     racing a `dotnet build`, and the `global.json` SDK pin that keeps
///     MSBuild's design-time build from picking an incompatible SDK line.
///   - `Runtime.fs`'s `buildSampleFromSource` (~line 96-118): build ONCE on
///     the host, offline, from the machine's already-populated NuGet
///     global-packages cache — the cell this fixture eventually runs inside
///     is `--unshare-net` (demo-gif-plan.md §10), so nothing here may ever
///     touch the network.
///
/// This module intentionally stays a peer of `Runtime.Agent.fs`
/// (`SageFs.Demos.Runtime.Cohort`, not nested under `...Runtime.Core`) for
/// the same reason every other per-actor extension module does — see
/// `Runtime.fs`'s own module doc on the FS0247 rename.
module SageFs.Demos.Runtime.Cohort

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks

/// What phases C/D need to drive the `cohort-landing` scenario: a real repo
/// on disk plus the two commit shas its landing beats present to
/// `request_landing`, and the repo-relative files alice/bob each claim
/// before editing (beats 3/4/5 of the plan's "8 beats").
type FixtureResult =
  { FixtureDir: string
    AliceGoodSha: string
    BobBreakSha: string
    AliceClaimFile: string
    BobClaimFile: string }

let private aliceClaimFile = "src/Alice.fs"
let private bobClaimFile = "src/Bob.fs"

// ---------------------------------------------------------------------------
// Process-spawning helpers — this project deliberately references neither
// SageFs.Core nor SageFs (this file's own header + the fsproj's own doc
// comment on why), so every git/dotnet invocation here is a raw `Process`,
// exactly `CohortLandingGateIntegrationTests.fs`'s own independent copy.
// ---------------------------------------------------------------------------

let private git (dir: string) (args: string list) : Task<string> =
  task {
    let psi =
      ProcessStartInfo(
        "git",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = dir)
    for a in args do
      psi.ArgumentList.Add a
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let! stdout = proc.StandardOutput.ReadToEndAsync()
    let! stderr = proc.StandardError.ReadToEndAsync()
    do! proc.WaitForExitAsync()
    match proc.ExitCode with
    | 0 -> return stdout.Trim()
    | code -> return failwithf "git %s failed (%d) in %s: %s" (String.concat " " args) code dir stderr
  }

let private writeAndCommit (dir: string) (relPath: string) (content: string) (message: string) : Task<string> =
  task {
    let full = Path.Combine(dir, relPath)
    let parent = Path.GetDirectoryName full
    if not (String.IsNullOrEmpty parent) then
      Directory.CreateDirectory parent |> ignore
    File.WriteAllText(full, content)
    let! _ = git dir [ "add"; relPath ]
    let! _ = git dir [ "commit"; "--quiet"; "-m"; message ]
    return! git dir [ "rev-parse"; "HEAD" ]
  }

/// Commits whatever is already staged (used for the `git rm --cached`
/// untrack step below, which stages a deletion with no new file content —
/// mirrors `CohortLandingGateIntegrationTests.fs`'s `commitStaged`).
let private commitStaged (dir: string) (message: string) : Task<string> =
  task {
    let! _ = git dir [ "commit"; "--quiet"; "-m"; message ]
    return! git dir [ "rev-parse"; "HEAD" ]
  }

let private dotnetBuildQuiet (dir: string) : Task<unit> =
  task {
    let psi =
      ProcessStartInfo(
        "dotnet",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = dir)
    for a in [ "build"; "-v:q"; "--nologo" ] do
      psi.ArgumentList.Add a
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let! stdout = proc.StandardOutput.ReadToEndAsync()
    let! stderr = proc.StandardError.ReadToEndAsync()
    do! proc.WaitForExitAsync()
    match proc.ExitCode with
    | 0 -> return ()
    | code -> return failwithf "dotnet build failed (%d) in %s:\n%s\n%s" code dir stdout stderr
  }

// ---------------------------------------------------------------------------
// Fixture sources: two tiny modules (Alice, Bob) each with one function, a
// tests file that exercises BOTH, and a hand-written entry point — mirrors
// `CohortLandingGateIntegrationTests.fs`'s `writeFixtureSources` shape
// exactly, just with two claim-able files instead of one `Util.fs` so the
// scenario's claim-conflict beat (5/8: "bob tries alice's file — REJECTED")
// has two genuinely distinct, disjoint files to claim.
// ---------------------------------------------------------------------------

let private fsprojContent =
  "<Project Sdk=\"Microsoft.NET.Sdk\">\n\n"
  + "  <PropertyGroup>\n"
  + "    <OutputType>Exe</OutputType>\n"
  + "    <TargetFramework>net10.0</TargetFramework>\n"
  + "    <GenerateProgramFile>false</GenerateProgramFile>\n"
  + "  </PropertyGroup>\n\n"
  + "  <ItemGroup>\n"
  + "    <Compile Include=\"src/Alice.fs\" />\n"
  + "    <Compile Include=\"src/Bob.fs\" />\n"
  + "    <Compile Include=\"src/Tests.fs\" />\n"
  + "    <Compile Include=\"Program.fs\" />\n"
  + "  </ItemGroup>\n\n"
  + "  <ItemGroup>\n"
  + "    <PackageReference Include=\"Expecto\" Version=\"11.0.0-alpha8\" />\n"
  + "  </ItemGroup>\n\n"
  + "</Project>\n"

let private aliceSource = "module Fixture.Alice\n\nlet greet name = sprintf \"hello, %s\" name\n"

let private bobSource = "module Fixture.Bob\n\nlet add a b = a + b\n"

/// `bobSource` after the `bob-break` commit — the same function name and
/// signature (so every discovered call site still type-checks), but wrong
/// arithmetic, so any test asserting `add`'s result genuinely fails. Mirrors
/// `CohortLandingGateIntegrationTests.fs`'s own `add a b = a - b` regression.
let private bobBrokenSource = "module Fixture.Bob\n\nlet add a b = a - b\n"

let private testsSource =
  "module Fixture.Tests\n\n"
  + "open Expecto\n"
  + "open Expecto.Flip\n\n"
  + "[<Tests>]\n"
  + "let tests =\n"
  + "  testList \"Fixture\" [\n"
  + "    test \"Alice.greet says hello\" {\n"
  + "      Fixture.Alice.greet \"bob\" |> Expect.equal \"greets by name\" \"hello, bob\"\n"
  + "    }\n"
  + "    test \"Bob.add 2 3 = 5\" {\n"
  + "      Fixture.Bob.add 2 3 |> Expect.equal \"2+3=5\" 5\n"
  + "    }\n"
  + "  ]\n"

/// `testsSource` after the `alice-good` commit — the same two tests PLUS one
/// new passing test exercising the NEW `Alice.farewell` function
/// `aliceGoodSource` adds, exactly the "ADDS a NEW passing test (extends
/// coverage, stays green)" requirement, and genuinely landing a real change
/// to `aliceClaimFile` ("src/Alice.fs") — the file alice claims in beat 3.
let private testsSourceWithAliceGoodTest =
  "module Fixture.Tests\n\n"
  + "open Expecto\n"
  + "open Expecto.Flip\n\n"
  + "[<Tests>]\n"
  + "let tests =\n"
  + "  testList \"Fixture\" [\n"
  + "    test \"Alice.greet says hello\" {\n"
  + "      Fixture.Alice.greet \"bob\" |> Expect.equal \"greets by name\" \"hello, bob\"\n"
  + "    }\n"
  + "    test \"Alice.farewell says goodbye\" {\n"
  + "      Fixture.Alice.farewell \"bob\" |> Expect.equal \"says goodbye by name\" \"goodbye, bob\"\n"
  + "    }\n"
  + "    test \"Bob.add 2 3 = 5\" {\n"
  + "      Fixture.Bob.add 2 3 |> Expect.equal \"2+3=5\" 5\n"
  + "    }\n"
  + "  ]\n"

/// `aliceSource` after the `alice-good` commit — adds a genuinely new
/// function (`farewell`), which `testsSourceWithAliceGoodTest`'s new test
/// exercises. `greet` is left untouched so the pre-existing test keeps
/// passing unmodified.
let private aliceGoodSource =
  "module Fixture.Alice\n\n"
  + "let greet name = sprintf \"hello, %s\" name\n\n"
  + "let farewell name = sprintf \"goodbye, %s\" name\n"

let private programSource =
  "module Fixture.Program\n\n"
  + "open Expecto\n\n"
  + "[<EntryPoint>]\n"
  + "let main argv =\n"
  + "  Tests.runTestsWithCLIArgs [] argv Tests.tests\n"

/// See `CohortLandingGateIntegrationTests.fs`'s own header ("WHY THE FIXTURE
/// PINS AN SDK") for the full empirical account: a temp dir with no
/// `global.json` above it lets `dotnet` resolve whatever SDK line is newest
/// on the box, which can be a major version ahead of the daemon's own
/// runtime and fault the design-time project load. Pinning to the repo's own
/// SDK line keeps this fixture behaving identically to every in-repo sample.
let private globalJsonContent =
  "{\n"
  + "  \"sdk\": {\n"
  + "    \"version\": \"10.0.100\",\n"
  + "    \"rollForward\": \"latestFeature\",\n"
  + "    \"allowPrerelease\": false\n"
  + "  }\n"
  + "}\n"

let private writeFixtureSources (dir: string) : unit =
  Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
  File.WriteAllText(Path.Combine(dir, "global.json"), globalJsonContent)
  File.WriteAllText(Path.Combine(dir, "Fixture.fsproj"), fsprojContent)
  File.WriteAllText(Path.Combine(dir, "src", "Alice.fs"), aliceSource)
  File.WriteAllText(Path.Combine(dir, "src", "Bob.fs"), bobSource)
  File.WriteAllText(Path.Combine(dir, "src", "Tests.fs"), testsSource)
  File.WriteAllText(Path.Combine(dir, "Program.fs"), programSource)

/// Runs the built fixture exe and returns `Ok()` iff Expecto's own process
/// exit code says every test passed (0), `Error` with the captured
/// stdout/stderr otherwise — used to VERIFY (never assume) that the base
/// commit is green, `alice-good` stays green, and `bob-break` genuinely
/// fails at least one test.
let private runFixtureTests (dir: string) : Task<Result<string, string>> =
  task {
    let psi =
      ProcessStartInfo(
        "dotnet",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = dir)
    for a in [ "run"; "--project"; "Fixture.fsproj"; "--no-build"; "--" ] do
      psi.ArgumentList.Add a
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let! stdout = proc.StandardOutput.ReadToEndAsync()
    let! stderr = proc.StandardError.ReadToEndAsync()
    do! proc.WaitForExitAsync()
    match proc.ExitCode with
    | 0 -> return Ok stdout
    | code -> return Error(sprintf "fixture test run exited %d\n--- stdout ---\n%s\n--- stderr ---\n%s" code stdout stderr)
  }

/// Builds the fixture on the HOST (offline from the shared NuGet
/// global-packages cache): a throwaway temp git repo carrying a real, tiny,
/// prebuilt Expecto project — base commit green with `bin`/`obj` committed
/// (see `CohortLandingGateIntegrationTests.fs`'s header on why: a fresh `git
/// worktree add` the daemon creates via `set_integration_ref` must never
/// race a `dotnet build` during session warmup), plus two work-branch
/// commits (`alice-good`, `bob-break`) the scenario's landing beats present
/// to `request_landing` by sha.
let prepareFixture () : Async<Result<FixtureResult, string>> =
  async {
    try
      let dir = Directory.CreateTempSubdirectory("sagefs-demos-cohort-fixture-").FullName

      let! _ = git dir [ "init"; "--quiet"; "-b"; "main" ] |> Async.AwaitTask
      let! _ = git dir [ "config"; "user.email"; "cohort-demo@example.com" ] |> Async.AwaitTask
      let! _ = git dir [ "config"; "user.name"; "SageFs Cohort Demo" ] |> Async.AwaitTask

      writeFixtureSources dir
      do! dotnetBuildQuiet dir |> Async.AwaitTask
      let! _ = git dir [ "add"; "-A" ] |> Async.AwaitTask
      let! _baseSha =
        commitStaged dir "base: two-module Expecto fixture (Alice/Bob), prebuilt (bin/obj committed — see Runtime.Cohort.fs header on why)"
        |> Async.AwaitTask

      match! runFixtureTests dir |> Async.AwaitTask with
      | Error e -> return Error(sprintf "base commit must be green: %s" e)
      | Ok _ ->

      // ── alice-good: adds a NEW passing test extending Alice coverage,
      // stays green. On its own branch off main (never committed directly
      // onto `main` — the scenario's own `set_integration_ref(alice, HEAD)`
      // beat needs `main`'s tip to stay exactly at the base commit).
      //
      // FIRST commit on this branch untracks bin/obj (mirrors
      // `CohortLandingGateIntegrationTests.fs`'s own "WHY THE FIXTURE
      // COMMITS bin/obj" trick, applied here for the SAME reason: once this
      // branch is the good landing and lands into the daemon's real
      // integration branch, the live-testing session watching that
      // worktree's filesystem will auto-rebuild on the Alice/Tests edits,
      // rewriting bin/obj ON DISK. If bin/obj were still TRACKED at that
      // point, that auto-rebuild would leave the worktree with a dirty
      // TRACKED file, and `bob-break`'s own subsequent rebase (beat 8) would
      // then risk being blocked by that unrelated dirty state — a false
      // "blocked" for the wrong reason, defeating the whole point of the
      // demo, which is to prove the gate blocks on a REAL FAILING TEST.
      // Untracking here (keeping the bytes on disk, only removing them from
      // git's index) landed as part of the good commit's range means the
      // auto-rebuild after alice's landing only ever touches UNTRACKED
      // files, which `git rebase` ignores. ──
      let! _ = git dir [ "checkout"; "-b"; "alice-good"; "main" ] |> Async.AwaitTask
      let! _ = git dir [ "rm"; "-r"; "--cached"; "bin"; "obj" ] |> Async.AwaitTask
      let! _ = commitStaged dir "alice-good: untrack build output (see Runtime.Cohort.fs header on why)" |> Async.AwaitTask
      let! _ = writeAndCommit dir "src/Alice.fs" aliceGoodSource "alice-good: add Alice.farewell" |> Async.AwaitTask
      let! aliceGoodSha = writeAndCommit dir "src/Tests.fs" testsSourceWithAliceGoodTest "alice-good: extend Alice test coverage (stays green)" |> Async.AwaitTask

      do! dotnetBuildQuiet dir |> Async.AwaitTask
      match! runFixtureTests dir |> Async.AwaitTask with
      | Error e -> return Error(sprintf "alice-good commit must stay green: %s" e)
      | Ok _ ->

      // ── bob-break: edits Bob.fs so a discovered test genuinely FAILS.
      // Branches off `alice-good`'s OWN tip (mirroring
      // `CohortLandingGateIntegrationTests.fs`'s own `work-break`, which
      // branches from whatever is currently checked out, i.e. `work-good`'s
      // tip) — NOT off `main`. Two reasons: (1) `main`'s tree still has
      // bin/obj TRACKED, so checking out a branch rooted there after
      // alice-good's checkout already rebuilt them as untracked files on
      // disk would fail with "untracked working tree files would be
      // overwritten by checkout" (confirmed empirically while writing this
      // module); (2) it exactly mirrors the real landing order the scenario
      // performs — bob's landing is requested AFTER alice's already landed,
      // so its commit rebasing cleanly onto the (by-then-advanced)
      // integration head only ever needs to carry bob's own one-line diff. ──
      let! _ = git dir [ "checkout"; "-b"; "bob-break"; "alice-good" ] |> Async.AwaitTask
      let! bobBreakSha = writeAndCommit dir "src/Bob.fs" bobBrokenSource "bob-break: regress add (expected to block landing)" |> Async.AwaitTask

      do! dotnetBuildQuiet dir |> Async.AwaitTask
      let! bobBreakOutcome = runFixtureTests dir |> Async.AwaitTask

      match bobBreakOutcome with
      | Ok stdout ->
        return Error(sprintf "bob-break commit must FAIL at least one test, but the fixture run exited 0:\n%s" stdout)
      | Error _ ->

      // ── Leave the repo on `main` at the green base commit — the daemon
      // is launched with this directory as its cwd, and the scenario's own
      // beat 6 (`set_integration_ref(alice, HEAD)`) expects HEAD to be that
      // base commit, not either work branch.
      //
      // `main` still tracks bin/obj (only `alice-good`/`bob-break` untrack
      // them), so the untracked build output the last `dotnetBuildQuiet`
      // left on disk for `bob-break` must be cleaned FIRST — otherwise
      // `git checkout main` refuses with the same "untracked working tree
      // files would be overwritten" error `bob-break`'s own checkout hit
      // before this file branched it off `alice-good` instead of `main`. ──
      let! _ = git dir [ "clean"; "-fdx"; "bin"; "obj" ] |> Async.AwaitTask
      let! _ = git dir [ "checkout"; "main" ] |> Async.AwaitTask

      return
        Ok
          { FixtureDir = dir
            AliceGoodSha = aliceGoodSha
            BobBreakSha = bobBreakSha
            AliceClaimFile = aliceClaimFile
            BobClaimFile = bobClaimFile }
    with ex ->
      return Error(sprintf "prepareFixture failed: %s" ex.Message)
  }

/// RW binds to add to the cell's `cellSpec` (`Runtime.fs`'s own extension
/// point) so the daemon-owned integration session can write a worktree +
/// `obj`/`bin` into the fixture once `set_integration_ref` runs — mirrors
/// `cellSpec`'s existing `sampleDir, sampleDir` RW bind for the same reason
/// (a read-only fixture dir would fault the worktree's own design-time
/// build with `MSB3491: Read-only file system`, exactly `Runtime.fs`'s own
/// doc on `sampleDir` explains).
let actorBinds (fixture: FixtureResult) : (string * string) list = [ (fixture.FixtureDir, fixture.FixtureDir) ]

/// The daemon's working directory inside the cell — `innerScript`'s
/// unavoidable pre-daemon `cd` (the plan's item 1: `mainRepoDir =
/// Environment.CurrentDirectory`, so the fixture must BE the daemon's cwd,
/// not merely bound in alongside it).
let daemonCwd (fixture: FixtureResult) : string = fixture.FixtureDir
