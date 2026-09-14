/// Closes Gap 3 from the multi-agent dogfood
/// (`CohortDogfoodIntegrationTests.fs`'s own header names it): that suite's
/// fixture carries NO live-testing-discovered tests (`ComputeAffected`
/// returns `[]`), so nothing in this repo's test suite proved a cohort
/// landing is genuinely BLOCKED by a real, discovered, FAILING test. This
/// file closes that gap with a REAL Expecto test project, real live
/// testing, and the REAL `DaemonMode.fs` `cohortLandingPerformer` — no
/// stub, no fake verifier.
///
/// Mirrors `CohortDogfoodIntegrationTests.fs`'s own fixture/oracle
/// discipline (isolated daemon, own port, own `SAGEFS_DATA_DIR`, never the
/// user's live daemon; git helpers and the independent git-ref oracle are
/// duplicated here rather than shared, exactly the isolation
/// `CohortLandingGitAcceptanceTests.fs`/`CohortDogfoodIntegrationTests.fs`
/// already establish as this repo's pattern for landing-proof fixtures).
///
/// THE FLOW (one cohort, one member, sequential — deliberately NOT
/// replaying the two-member conflict/landing choreography
/// `CohortDogfoodIntegrationTests.fs` already proves; this file's only job
/// is the TEST-GATE question):
///   1. A throwaway temp git repo carries a real, tiny Expecto test project
///      (`Fixture.fsproj` — `add a b = a + b`, one test `add 2 2 = 4`),
///      PRE-BUILT and committed (see "WHY THE FIXTURE COMMITS bin/obj"
///      below) so the daemon-owned integration session can warm up without
///      a race against `dotnet build`.
///   2. `set_integration_ref` creates the real integration worktree +
///      session on that project. Live testing is enabled the same way
///      `HttpApiIntegrationTests.fs`'s compiled-live-testing test does (raw
///      HTTP `/api/live-testing/enable` + `/policy`, on the mcp port — v1
///      has no MCP TOOL for this, only the HTTP route `McpServer.fs` maps),
///      and this test waits for real discovery + a real green baseline.
///   3. GOOD LANDING FIRST: a commit that ADDS test coverage (a second
///      passing test) is landed. This proves the gate lets a real,
///      test-verified, passing change through — using the EXACT SAME
///      pipeline the blocking case below exercises — before the file ever
///      asks anything to block. (Ordering matters: see "WHY GOOD LANDS
///      FIRST" below — landing the fix AFTER a block is not something v1
///      can do in the same cohort.)
///   4. BREAKING LANDING SECOND: a commit that changes `add` to `-` breaks
///      BOTH discovered tests. Its landing is requested and this test
///      proves — via TWO independent signals — that it does NOT land:
///        (a) the independent git-ref oracle: `refs/heads/<integration
///            branch>` in the ORIGINAL repo (never the worktree, never
///            `CohortGit` itself) stays exactly where the good landing
///            left it;
///        (b) the MCP-observable signal `CohortDogfoodIntegrationTests.fs`
///            already established: the backing claim's state, read via
///            `get_cohort_status`, stays `Held` (never flips to
///            `Released`, which only `FastForwardCompleted` does).
///   5. BONUS, HONEST FINDING: this file also submits a THIRD commit that
///      reverts the regression (tests pass again) and requests ITS landing
///      too, reusing the still-`Held` claim from step 4 — genuinely
///      checking whether v1's cohort can recover and land the fix after a
///      block. Source-read first (`Cohort.fs`'s `TestsCompleted` arm, the
///      `Blocked` case): a landing that reaches `Blocked` is NEVER popped
///      from `CohortState.Queue`, and `CohortCommand` has no
///      cancel/dismiss/retry case — so a landing queued behind an
///      unresolved `Blocked` head can never even start its own `Rebase`
///      (`advanceQueue` only fires `Rebase` when the QUEUE HEAD's state is
///      exactly `Queued`). This test verifies that prediction empirically
///      (bounded poll, not a fixed sleep) rather than trusting the source
///      read alone, and asserts the REAL observed outcome — it does not
///      pretend recovery works if it doesn't.
///
/// WHY GOOD LANDS FIRST (not "break, prove blocked, then land the fix" as
/// a naive reading of the brief might suggest): because of the finding in
/// step 5. A landing that reaches `Blocked(FailingTests ...)` permanently
/// occupies `CohortState.Queue`'s head in v1 — there is no
/// `CancelLanding`/`RetryLanding` command in `Cohort.fs`'s
/// `CohortCommand<'m>` DU. Landing the FIX after the BREAK's landing
/// blocked would therefore get stuck too, for a reason that has nothing to
/// do with whether the fix itself is good — it would just prove the queue
/// is jammed, not that "the gate lets good landings through." Landing the
/// good change FIRST (while the queue is still empty/healthy) and the
/// breaking change SECOND (nothing needs to land after it) cleanly proves
/// both halves of the brief with the real production pipeline, and this
/// file still empirically checks the post-block recovery question as a
/// separate, honestly-labeled bonus (step 5) rather than silently avoiding
/// it.
///
/// WHY THE FIXTURE COMMITS bin/obj: `ProjectLoading.fs` faults a session's
/// warmup with "Missing DLL" when a project's `TargetPath` does not exist
/// on disk — SageFs never runs `dotnet build` itself at session-create
/// time (confirmed by reading `SessionManager.fs`'s `CreateSession`
/// handler: it starts the worker process and does nothing else; building
/// only happens on an explicit hard-reset-with-rebuild). A git WORKTREE
/// (what `set_integration_ref` creates) starts with none of the previous
/// checkout's untracked `bin`/`obj` — those are per-worktree files on disk,
/// not shared — so unless the fixture's `bin`/`obj` are themselves
/// TRACKED, the fresh worktree's very first warmup would race a
/// `dotnet build` this test would have to somehow win. Instead: build once
/// in the main repo, commit `bin`/`obj` alongside the source (verified:
/// `git worktree add` materializes tracked files including binaries, and
/// the resulting DLL runs correctly from the new location — package
/// references resolve out of the machine-global NuGet cache, which is
/// location-independent). Then, as the FIRST commit on the FIRST work
/// branch (see below), `git rm -r --cached bin obj` untracks them again
/// (keeping the bytes on disk) so the live session's own automatic
/// rebuild-on-save (the same mechanism
/// `HttpApiIntegrationTests.fs`'s "editing a compiled F# file reruns tests
/// against rebuilt output" test proves) can keep rewriting them WITHOUT
/// dirtying a tracked path — a git rebase refuses to run against a dirty
/// TRACKED file, but happily ignores untracked ones (verified empirically
/// while writing this test: an untracked, on-disk-modified `bin/obj` never
/// blocks `git rebase`).
///
/// WHY THE GIT-REF ORACLE NEEDS A SEPARATE WORK BRANCH PER LANDING (not
/// committing straight onto the integration branch, unlike this being
/// tempting for a single-member flow): `refs/heads/<integration branch>`
/// is the SAME ref the worktree's HEAD would be if commits were made
/// directly on it — and that ref is shared across every worktree of one
/// repo. Committing directly onto it would move the very ref this test
/// uses as its independent "did the landing actually happen" oracle BEFORE
/// any landing ever ran, making the oracle meaningless. Every actual change
/// here happens on its own short-lived `work-*` branch, checked out fresh
/// off the integration branch's current tip; only `FastForward`
/// (`CohortGit.fastForwardBranch`, called from the daemon's OWN working
/// directory — the main repo, not the worktree) is allowed to move
/// `refs/heads/<integration branch>`.
module SageFs.Tests.CohortLandingGateIntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private sageFsExe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

let private gitAvailable () : bool =
  try
    let psi =
      ProcessStartInfo(
        "git", "--version",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false)
    use proc = Process.Start psi
    proc.WaitForExit(5000) |> ignore
    proc.HasExited && proc.ExitCode = 0
  with _ -> false

// ── Minimal, independent git helper (own copy — see this file's header on
//    why fixture setup and oracle verification never go through CohortGit
//    itself, mirroring CohortDogfoodIntegrationTests.fs's own discipline) ──

let private git (dir: string) (args: string list) : Task<string> =
  task {
    let psi =
      ProcessStartInfo(
        "git",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = dir)
    for a in args do psi.ArgumentList.Add a
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
    if not (String.IsNullOrEmpty parent) then Directory.CreateDirectory parent |> ignore
    File.WriteAllText(full, content)
    let! _ = git dir [ "add"; relPath ]
    let! _ = git dir [ "commit"; "--quiet"; "-m"; message ]
    return! git dir [ "rev-parse"; "HEAD" ]
  }

/// Commits whatever is already staged (used for the `git rm --cached`
/// untrack step, which stages a deletion with no new file content).
let private commitStaged (dir: string) (message: string) : Task<string> =
  task {
    let! _ = git dir [ "commit"; "--quiet"; "-m"; message ]
    return! git dir [ "rev-parse"; "HEAD" ]
  }

let private reserveLoopbackPort () =
  use listener = new TcpListener(IPAddress.Loopback, 0)
  listener.Start()
  (listener.LocalEndpoint :?> IPEndPoint).Port

/// Builds the fixture project in `dir` (see this file's header — the
/// fixture's `bin`/`obj` are committed so the worktree the daemon creates
/// later never races a `dotnet build` during warmup).
let private dotnetBuildQuiet (dir: string) : Task<unit> =
  task {
    let psi =
      ProcessStartInfo(
        "dotnet",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = dir)
    for a in [ "build"; "-v:q"; "--nologo" ] do psi.ArgumentList.Add a
    use proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    let! stdout = proc.StandardOutput.ReadToEndAsync()
    let! stderr = proc.StandardError.ReadToEndAsync()
    do! proc.WaitForExitAsync()
    match proc.ExitCode with
    | 0 -> return ()
    | code -> return failwithf "dotnet build failed (%d) in %s:\n%s\n%s" code dir stdout stderr
  }

/// Writes the fixture's four source files: a real, tiny, standalone Expecto
/// test project (net10.0, pinned Expecto version so restore is fully
/// offline-cacheable — mirrors samples/from-csharp/SageFs.Samples.FromCSharp's
/// proven shape: OutputType Exe + GenerateProgramFile=false + a hand-written
/// EntryPoint, which SageFs's live-testing discovery already handles via
/// reflection over the built assembly, [<Tests>] attribute or not).
let private writeFixtureSources (dir: string) : unit =
  let fsproj =
    "<Project Sdk=\"Microsoft.NET.Sdk\">\n\n" +
    "  <PropertyGroup>\n" +
    "    <OutputType>Exe</OutputType>\n" +
    "    <TargetFramework>net10.0</TargetFramework>\n" +
    "    <GenerateProgramFile>false</GenerateProgramFile>\n" +
    "  </PropertyGroup>\n\n" +
    "  <ItemGroup>\n" +
    "    <Compile Include=\"Util.fs\" />\n" +
    "    <Compile Include=\"UtilTests.fs\" />\n" +
    "    <Compile Include=\"Program.fs\" />\n" +
    "  </ItemGroup>\n\n" +
    "  <ItemGroup>\n" +
    "    <PackageReference Include=\"Expecto\" Version=\"11.0.0-alpha8\" />\n" +
    "  </ItemGroup>\n\n" +
    "</Project>\n"
  let util = "module Fixture.Util\n\nlet add a b = a + b\n"
  let utilTests =
    "module Fixture.UtilTests\n\n" +
    "open Expecto\n" +
    "open Expecto.Flip\n\n" +
    "[<Tests>]\n" +
    "let tests =\n" +
    "  testList \"Fixture.Util\" [\n" +
    "    test \"add 2 2 = 4\" {\n" +
    "      Fixture.Util.add 2 2 |> Expect.equal \"2+2=4\" 4\n" +
    "    }\n" +
    "  ]\n"
  let program =
    "module Fixture.Program\n\n" +
    "open Expecto\n\n" +
    "[<EntryPoint>]\n" +
    "let main argv =\n" +
    "  Tests.runTestsWithCLIArgs [] argv UtilTests.tests\n"
  // WHY THE FIXTURE PINS AN SDK (global.json): the integration session's
  // design-time project load runs MSBuild from whatever SDK `dotnet` resolves
  // for the worktree directory. This fixture lives in a throwaway temp dir with
  // NO repo global.json above it, so `dotnet` would resolve the machine's
  // NEWEST installed SDK — which, on a box that also has an SDK a major version
  // ahead of the worker's runtime (e.g. an 11.x preview SDK next to the net10
  // worker), makes Ionide.ProjInfo's design-time build fault with
  // "Could not load file or assembly 'System.Runtime, Version=11.0.0.0'"; the
  // loader then returns 0 projects and silently falls back to a manual fsproj
  // parse that never force-loads the built assembly into the worker AppDomain.
  // Live-testing discovery reflects over the loaded assemblies, finds no test
  // project among them, and reports `ready_zero_tests` — a REAL green session
  // with ZERO discoverable tests, which makes the whole landing test-gate
  // unprovable (this file's entire purpose). Root-caused empirically 2026-09-14
  // by spawning the worker directly and reading its (daemon-dropped) stderr.
  // Pinning the SDK to the worker's own runtime line (net10) makes MSBuild
  // resolve a matching SDK, the project load succeed, and discovery find the
  // seeded test — exactly what every real SageFs project needs (and what the
  // repo's own global.json already provides for the in-repo samples that
  // discover fine). Mirror the repo's pin so this fixture behaves identically
  // to the in-repo samples on CI and locally.
  let globalJson =
    "{\n" +
    "  \"sdk\": {\n" +
    "    \"version\": \"10.0.100\",\n" +
    "    \"rollForward\": \"latestFeature\",\n" +
    "    \"allowPrerelease\": false\n" +
    "  }\n" +
    "}\n"
  File.WriteAllText(Path.Combine(dir, "global.json"), globalJson)
  File.WriteAllText(Path.Combine(dir, "Fixture.fsproj"), fsproj)
  File.WriteAllText(Path.Combine(dir, "Util.fs"), util)
  File.WriteAllText(Path.Combine(dir, "UtilTests.fs"), utilTests)
  File.WriteAllText(Path.Combine(dir, "Program.fs"), program)

/// Spawn an isolated daemon rooted at `workingDir` (a throwaway temp git
/// repo, NEVER this repo, NEVER the user's live checkout) with its own
/// port and its own `SAGEFS_DATA_DIR`. Owned by this test process
/// (--owner-pid/--owner-start) plus a --ttl belt-and-braces — exactly
/// `CohortDogfoodIntegrationTests.fs`'s `startIsolatedDaemon`.
let private startIsolatedDaemon (workingDir: string) (dataDir: string) : Task<Process * int> = task {
  let port = reserveLoopbackPort ()
  let psi = ProcessStartInfo()
  psi.FileName <- sageFsExe
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- workingDir
  psi.ArgumentList.Add "--mcp-port"
  psi.ArgumentList.Add(string port)
  let self = Process.GetCurrentProcess()
  psi.ArgumentList.Add "--owner-pid"
  psi.ArgumentList.Add(string self.Id)
  psi.ArgumentList.Add "--owner-start"
  psi.ArgumentList.Add(string (self.StartTime.ToUniversalTime().Ticks))
  psi.ArgumentList.Add "--ttl"
  psi.ArgumentList.Add "10m"
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir

  let proc = Process.Start psi
  use client = new Net.Http.HttpClient()
  client.BaseAddress <- Uri(sprintf "http://localhost:%d" port)
  client.Timeout <- TimeSpan.FromSeconds 5.0

  let mutable ready = false
  let mutable attempts = 0
  while not ready && attempts < 300 do
    do! Task.Delay 200
    try
      let! resp = client.GetAsync "/health"
      if int resp.StatusCode > 0 then ready <- true
    with _ -> ()
    attempts <- attempts + 1

  if not ready then
    let killAttempt = try proc.Kill true; true with _ -> false
    ignore killAttempt
    proc.Dispose()
    failwithf "cohort landing-gate daemon failed to start on port %d within 60s" port

  return proc, port
}

let private killDaemon (proc: Process) =
  try
    if not proc.HasExited then
      proc.Kill(entireProcessTree = true)
      proc.WaitForExit 5000 |> ignore
  with _ -> ()
  proc.Dispose()

let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> String.concat ""

let private connect (port: int) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

let private callTool (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    let! result = client.CallToolAsync(name, readOnlyDict args, null, null, CancellationToken.None)
    return textOf result
  }

let private joinCohort (client: McpClient) (agentName: string) (role: string) =
  callTool client "join_cohort" [ "agentName", box agentName; "role", box role ]

let private getCohortStatus (client: McpClient) = callTool client "get_cohort_status" []

let private acquireClaim (client: McpClient) (agentName: string) (scope: string) (purpose: string) =
  callTool client "acquire_claim" [ "agentName", box agentName; "scope", box scope; "purpose", box purpose ]

let private setIntegrationRef (client: McpClient) (agentName: string) (integrationRef: string) =
  callTool client "set_integration_ref" [ "agentName", box agentName; "integrationRef", box integrationRef ]

let private requestLanding (client: McpClient) (agentName: string) (claims: string) (commits: string) (statement: string) =
  callTool client "request_landing" [ "agentName", box agentName; "claims", box claims; "commits", box commits; "statement", box statement ]

let private listSessions (client: McpClient) = callTool client "list_sessions" []

/// "Joined cohort as mcp:xxxx (Implementer)...." -> "mcp:xxxx".
let private memberIdFromJoinResult (text: string) : string =
  let marker = "Joined cohort as "
  let start = text.IndexOf marker
  if start < 0 then failwithf "join_cohort did not report success: %s" text
  let afterMarker = start + marker.Length
  let stop = text.IndexOf(' ', afterMarker)
  text.Substring(afterMarker, stop - afterMarker)

/// "Acquired claim c-XXXX over ... (fence=N)." -> ("c-XXXX", N).
let private claimIdAndFenceFromAcquireResult (text: string) : string * int64 =
  let idMarker = "Acquired claim "
  let idStart = text.IndexOf idMarker
  if idStart < 0 then failwithf "acquire_claim did not report success: %s" text
  let afterId = idStart + idMarker.Length
  let idStop = text.IndexOf(' ', afterId)
  let claimId = text.Substring(afterId, idStop - afterId)
  let fenceMarker = "fence="
  let fenceStart = text.IndexOf(fenceMarker, idStop)
  if fenceStart < 0 then failwithf "acquire_claim result carried no fence: %s" text
  let afterFence = fenceStart + fenceMarker.Length
  let fenceStop = text.IndexOf(')', afterFence)
  let fence = Int64.Parse(text.Substring(afterFence, fenceStop - afterFence))
  claimId, fence

/// "Integration configured: head=<sha> worktree=<path> branch=<branch> session=<id>"
/// (or the WARNING variant — not expected here since the fixture always
/// carries a real, prebuilt project).
let private worktreeBranchAndSessionFromSetIntegrationRefResult (text: string) : string * string * string =
  let extract (marker: string) (stopAtSpace: bool) =
    let start = text.IndexOf marker
    if start < 0 then failwithf "set_integration_ref result missing '%s': %s" marker text
    let afterMarker = start + marker.Length
    let stop =
      match stopAtSpace with
      | false -> text.Length
      | true ->
        let space = text.IndexOf(' ', afterMarker)
        if space < 0 then text.Length else space
    text.Substring(afterMarker, stop - afterMarker)
  extract "worktree=" true, extract "branch=" true, extract "session=" false

let rec private waitUntil (deadline: DateTime) (describe: unit -> string) (check: unit -> Task<bool>) : Task<unit> =
  task {
    let! ok = check ()
    if ok then
      return ()
    elif DateTime.UtcNow > deadline then
      return failtestf "condition not met within timeout: %s" (describe ())
    else
      do! Task.Delay 250
      return! waitUntil deadline describe check
  }

/// Polls `probe` for up to `deadline`. Returns `Some` the first time `probe`
/// returns `Some` (something happened FASTER than the whole window — an
/// informative early signal, good or bad). Returns `None` only after the
/// ENTIRE window elapsed with `probe` always returning `None` — the
/// "verified nothing happened" case this file uses to prove a landing
/// never advances the git ref.
let rec private pollForUpTo (deadline: DateTime) (probe: unit -> Task<'a option>) : Task<'a option> =
  task {
    let! result = probe ()
    match result with
    | Some _ -> return result
    | None ->
      if DateTime.UtcNow > deadline then
        return None
      else
        do! Task.Delay 500
        return! pollForUpTo deadline probe
  }

let private seconds (n: float) = DateTime.UtcNow.AddSeconds n

/// The single `get_cohort_status` line describing entity `id` (a claim id or a
/// landing id) — so a state assertion is made against THAT entity's own line,
/// never a `Contains` over the whole multi-entity status blob. The blob lists
/// every claim and landing, so a substring like "state=Released" is true
/// whenever ANY claim is released (e.g. the good landing's claim legitimately
/// released on land) — asserting it "about" a different claim is the classic
/// fragile-string bug. Fails the test if no line mentions the id.
let private lineFor (status: string) (id: string) : string =
  status.Split('\n')
  |> Array.tryFind (fun line -> line.Contains id)
  |> Option.defaultWith (fun () -> failtestf "no line in get_cohort_status mentions '%s'. Status:\n%s" id status)

// ── Raw HTTP against the mcp port for the live-testing routes v1 exposes
//    only as HTTP (McpServer.fs's mapLiveTestingRoutes) — there is no MCP
//    TOOL for enable/disable/policy/status; the SAME routes
//    HttpApiIntegrationTests.fs's compiled-live-testing test uses. ──

type private LiveSnapshot = {
  DiscoveryState: string
  Total: int
  Passed: int
  Failed: int
  Running: int
  Stale: int
  FailedTests: string list
}

let private postJson (http: HttpClient) (path: string) (payload: obj) : Task<int * string> =
  task {
    let json = JsonSerializer.Serialize payload
    use content = new StringContent(json, Encoding.UTF8, "application/json")
    let! resp = http.PostAsync(path, content)
    let! body = resp.Content.ReadAsStringAsync()
    return int resp.StatusCode, body
  }

let private getLiveSnapshot (http: HttpClient) : Task<LiveSnapshot> =
  task {
    let! resp = http.GetAsync "/api/live-testing/status"
    let! body = resp.Content.ReadAsStringAsync()
    if int resp.StatusCode <> 200 then
      failwithf "GET /api/live-testing/status returned %d: %s" (int resp.StatusCode) body
    use doc = JsonDocument.Parse(body: string)
    let root = doc.RootElement
    let summary = root.GetProperty "Summary"
    let failedTests =
      match root.TryGetProperty "FailedTests" with
      | true, tests ->
        tests.EnumerateArray() |> Seq.map (fun e -> e.GetProperty("Name").GetString()) |> Seq.toList
      | false, _ -> []
    return {
      DiscoveryState = root.GetProperty("DiscoveryState").GetString()
      Total = summary.GetProperty("Total").GetInt32()
      Passed = summary.GetProperty("Passed").GetInt32()
      Failed = summary.GetProperty("Failed").GetInt32()
      Running = summary.GetProperty("Running").GetInt32()
      Stale = summary.GetProperty("Stale").GetInt32()
      FailedTests = failedTests
    }
  }

let rec private waitForLiveSnapshot
  (http: HttpClient)
  (deadline: DateTime)
  (describe: string)
  (predicate: LiveSnapshot -> bool)
  : Task<LiveSnapshot> =
  task {
    let! snap = getLiveSnapshot http
    match predicate snap with
    | true -> return snap
    | false ->
      match DateTime.UtcNow > deadline with
      | true -> return failtestf "live-testing status never satisfied '%s' within timeout. Last snapshot: %A" describe snap
      | false ->
        do! Task.Delay 500
        return! waitForLiveSnapshot http deadline describe predicate
  }

let private isSessionReady (sessionId: string) (listSessionsText: string) =
  listSessionsText.Contains sessionId && listSessionsText.Contains " Ready "

let rec private waitForSessionReady (client: McpClient) (sessionId: string) (deadline: DateTime) : Task<unit> =
  task {
    let! text = listSessions client
    match isSessionReady sessionId text with
    | true -> return ()
    | false ->
      match DateTime.UtcNow > deadline with
      | true -> return failtestf "session %s never reached Ready within timeout. list_sessions: %s" sessionId text
      | false ->
        do! Task.Delay 500
        return! waitForSessionReady client sessionId deadline
  }

[<Tests>]
let tests =
  Integration.hostList "CohortLandingGate" [

    if not (gitAvailable ()) then
      testCase "git must be available on PATH" <| fun () ->
        failtest "git is not available on PATH — this landing-gate proof requires a real git executable"
    else

    yield! [

      testTask "WHY — a cohort landing is genuinely BLOCKED by a real, discovered, failing Expecto test — the real daemon-owned landing pipeline never fast-forwards it — while a genuinely passing landing DOES land through the same pipeline (closes multi-agent dogfood Gap 3)" {
        let mainRepo = Directory.CreateTempSubdirectory("cohort-landing-gate-main-").FullName
        let dataDir = Directory.CreateTempSubdirectory("cohort-landing-gate-data-").FullName
        let mutable daemonProc: Process option = None
        try
          // ── Fixture: throwaway temp git repo with a REAL, tiny, prebuilt
          // Expecto test project (never this repo, never the user's) ──
          let! _ = git mainRepo [ "init"; "--quiet"; "-b"; "main" ]
          let! _ = git mainRepo [ "config"; "user.email"; "sagefs-landing-gate-test@example.com" ]
          let! _ = git mainRepo [ "config"; "user.name"; "SageFs Landing Gate Test" ]
          writeFixtureSources mainRepo
          do! dotnetBuildQuiet mainRepo
          let! _ = git mainRepo [ "add"; "-A" ]
          let! baseSha = commitStaged mainRepo "base: real Expecto test project, prebuilt (see file header on why bin/obj are committed)"
          ignore baseSha

          // ── Spawn the isolated daemon (own port, own SAGEFS_DATA_DIR) ──
          let! proc, port = startIsolatedDaemon mainRepo dataDir
          daemonProc <- Some proc

          use! alice = connect port
          let http = new HttpClient()
          http.BaseAddress <- Uri(sprintf "http://localhost:%d" port)

          let! aliceJoin = joinCohort alice "alice" "Implementer"
          aliceJoin |> Expect.stringContains "the sole joiner becomes conductor" "You are the conductor"

          // ── Acquire the claim that will back BOTH the good and the
          // breaking landing request (the good one releases it on success;
          // it is re-acquired before the breaking request) ──
          let! aliceAcquire1 = acquireClaim alice "alice" "file:Util.fs" "extend Util test coverage"
          let claim1Id, claim1Fence = claimIdAndFenceFromAcquireResult aliceAcquire1

          // ── The conductor configures the real integration worktree,
          // branch, and daemon-owned integration session ──
          let! setRefResult = setIntegrationRef alice "alice" "HEAD"
          setRefResult |> Expect.stringContains "set_integration_ref must report success with a real session" "Integration configured: head="
          let worktree, branch, sessionId = worktreeBranchAndSessionFromSetIntegrationRefResult setRefResult
          Directory.Exists worktree |> Expect.isTrue "the integration worktree must really exist on disk"
          String.IsNullOrWhiteSpace sessionId
          |> Expect.isFalse "set_integration_ref must have created a real integration session — without one, ComputeAffected/RunTests can verify nothing (exactly the gap this file closes)"

          // ── Wait for the real integration session to warm up (the
          // prebuilt bin/obj means this should be fast — no dotnet build
          // races against warmup) ──
          do! waitForSessionReady alice sessionId (seconds 60.0)

          // ── Enable real live testing on the integration session (raw
          // HTTP — v1 has no MCP tool for this) and wait for real
          // discovery + a real green baseline (mirrors
          // HttpApiIntegrationTests.fs's compiled-live-testing test) ──
          let! enableStatus, enableBody = postJson http "/api/live-testing/enable" {||}
          enableStatus |> Expect.equal (sprintf "live testing enable should succeed: %s" enableBody) 200
          let! _policyStatus, _policyBody = postJson http "/api/live-testing/policy" {| category = "unit"; policy = "every" |}

          let! discovered =
            waitForLiveSnapshot http (seconds 60.0) "baseline discovery finds the one seeded test"
              (fun s -> s.DiscoveryState = "ready_with_tests" && s.Total >= 1)
          Expect.isGreaterThanOrEqual "baseline discovers at least the one seeded test" (discovered.Total, 1)

          let! baseline = getLiveSnapshot http
          match baseline.Running = 0 && baseline.Failed = 0 && baseline.Passed >= baseline.Total with
          | true -> ()
          | false ->
            let! _runStatus, _runBody = postJson http "/api/live-testing/run" {| pattern = ""; category = "" |}
            let! _ =
              waitForLiveSnapshot http (seconds 60.0) "baseline settles green after an explicit run"
                (fun s -> s.Running = 0 && s.Failed = 0 && s.Passed >= s.Total && s.Total >= 1)
            ()

          let! baselineSettled = getLiveSnapshot http
          baselineSettled.Failed |> Expect.equal "baseline must be genuinely green before either landing" 0

          // ══════════════════════════════════════════════════════════════
          // GOOD LANDING FIRST (see file header, "WHY GOOD LANDS FIRST")
          // ══════════════════════════════════════════════════════════════

          let! _ = git worktree [ "checkout"; "-b"; "work-good" ]
          // Untrack the committed bin/obj so the live session's own
          // automatic rebuild-on-save can keep rewriting them without ever
          // dirtying a TRACKED path (a dirty tracked file blocks `git
          // rebase`; a dirty UNTRACKED one does not — verified empirically
          // while writing this test; see the file header).
          let! _ = git worktree [ "rm"; "-r"; "--cached"; "bin"; "obj" ]
          let! _untrackSha = commitStaged worktree "chore: untrack build output for this worktree checkout"

          let goodUtilTests =
            "module Fixture.UtilTests\n\n" +
            "open Expecto\n" +
            "open Expecto.Flip\n\n" +
            "[<Tests>]\n" +
            "let tests =\n" +
            "  testList \"Fixture.Util\" [\n" +
            "    test \"add 2 2 = 4\" {\n" +
            "      Fixture.Util.add 2 2 |> Expect.equal \"2+2=4\" 4\n" +
            "    }\n" +
            "    test \"add 5 5 = 10\" {\n" +
            "      Fixture.Util.add 5 5 |> Expect.equal \"5+5=10\" 10\n" +
            "    }\n" +
            "  ]\n"
          let! goodSha = writeAndCommit worktree "UtilTests.fs" goodUtilTests "good: extend Util test coverage (keeps the suite green)"

          // Wait for the REAL session (watching the worktree's filesystem,
          // not git) to auto-rebuild, rediscover, and re-run — proving the
          // new test is genuinely live-tested before we ever land it.
          let! _ =
            waitForLiveSnapshot http (seconds 60.0) "the second test is discovered and the suite stays green after the good edit"
              (fun s -> s.Total >= 2 && s.Running = 0 && s.Failed = 0)
          ()

          let! goodLandingResult = requestLanding alice "alice" (sprintf "%s:%d" claim1Id claim1Fence) goodSha "land a real, test-verified, passing change"
          goodLandingResult |> Expect.stringContains "request_landing must queue the good landing" "queued"

          // Independent oracle: the real git branch ref in the MAIN repo
          // (never the worktree, never CohortGit itself).
          do!
            waitUntil (seconds 90.0)
              (fun () -> sprintf "integration branch %s to reach the good landing %s" branch goodSha)
              (fun () -> task {
                let! branchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
                return branchSha = goodSha
              })

          // MCP-observable proof of the same fact: the backing claim
          // auto-releases only on a successful land (Cohort.fs's
          // FastForwardCompleted handler).
          do!
            waitUntil (seconds 30.0)
              (fun () -> sprintf "get_cohort_status to show claim %s Released after the good landing" claim1Id)
              (fun () -> task {
                let! status = getCohortStatus alice
                // Claim-SPECIFIC: claim1's OWN line must show Released — not just
                // "Released appears somewhere in the status".
                return
                  status.Split('\n')
                  |> Array.exists (fun l -> l.Contains claim1Id && l.Contains "state=Released")
              })

          // ══════════════════════════════════════════════════════════════
          // BREAKING LANDING SECOND — THE GATE PROOF
          // ══════════════════════════════════════════════════════════════

          let! aliceAcquire2 = acquireClaim alice "alice" "file:Util.fs" "introduce and land a regression (expected to block)"
          let claim2Id, claim2Fence = claimIdAndFenceFromAcquireResult aliceAcquire2

          let! _ = git worktree [ "checkout"; "-b"; "work-break" ]
          // Already untracked (inherited from the good landing, which
          // fast-forwarded the untrack commit into `branch`) — no second
          // `git rm --cached` needed here.
          let breakingUtil = "module Fixture.Util\n\nlet add a b = a - b\n"
          let! breakSha = writeAndCommit worktree "Util.fs" breakingUtil "break: introduce a regression in add (expected to block landing)"

          // Wait for the real session to genuinely see the regression fail
          // BEFORE we ever request its landing — the verifier that matters
          // (DaemonMode.fs's RunTests, run during the landing itself) is
          // separate from this poll, but seeing it fail here first proves
          // the fixture's failure is real, not a fluke of timing.
          let! failedSnapshot =
            waitForLiveSnapshot http (seconds 60.0) "the regression is discovered as a real failure before landing is requested"
              (fun s -> s.Running = 0 && s.Failed >= 1)
          Expect.isGreaterThanOrEqual "the regression must genuinely fail at least one live-tested test" (failedSnapshot.Failed, 1)

          let! breakLandingResult = requestLanding alice "alice" (sprintf "%s:%d" claim2Id claim2Fence) breakSha "land a regression (expected to block on the real failing test)"
          breakLandingResult |> Expect.stringContains "request_landing must still queue the breaking landing (queueing always succeeds; verification is what blocks it)" "queued"

          // ── THE GATE PROOF: poll for up to 90s. `Some` here would mean
          // the breaking commit's landing WRONGLY fast-forwarded the
          // integration branch — the gate failing to do its job. `None`
          // means the full window elapsed with the branch never reaching
          // breakSha: the landing genuinely never lands. ──
          let! wronglyLanded =
            pollForUpTo (seconds 90.0) (fun () -> task {
              let! branchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
              return if branchSha = breakSha then Some branchSha else None
            })
          wronglyLanded
          |> Expect.isNone "THE GATE PROOF: a landing carrying a commit that fails a real, discovered live test must NEVER fast-forward the integration branch"

          // Final state, both independently confirmed:
          let! finalBranchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
          finalBranchSha
          |> Expect.equal "the integration branch must still sit exactly where the GOOD landing left it — the regression never landed" goodSha

          let! statusAfterBlock = getCohortStatus alice
          // Claim-SPECIFIC assertions against claim2's OWN status line. (An
          // earlier version matched "state=Released" across the whole blob, which
          // broke the moment the GOOD landing correctly released claim1 — a
          // fragile-string bug, not a product bug: the blob now legitimately
          // contains "state=Released" for claim1.)
          let claim2Line = lineFor statusAfterBlock claim2Id
          claim2Line
          |> Expect.stringContains "the blocked landing's claim must still be Held on its OWN line — only a SUCCESSFUL land releases it" "state=Held"
          claim2Line.Contains "state=Released"
          |> Expect.isFalse (sprintf "the blocked landing's claim must NOT be Released. claim2 line: %s" claim2Line)
          // Gap 1 (858b51f8) added landing state to the read model, so
          // get_cohort_status now surfaces landings directly — a STRONGER, direct
          // proof of the block than "the git ref never moved". The good landing
          // shows Landed; the breaking landing shows Blocked on the real failing
          // test (FailingTests), never fast-forwarded.
          statusAfterBlock
          |> Expect.stringContains "the read model now surfaces landings (Gap 1 closed the old 'not in the read model' gap)" "Landings ("
          statusAfterBlock
          |> Expect.stringContains "the breaking landing is Blocked on the REAL failing test, visible directly in the read model" "FailingTests"

          // ══════════════════════════════════════════════════════════════
          // BONUS, HONEST FINDING: does landing the FIX after a block
          // recover? (see file header — source-read prediction: no, v1 has
          // no CancelLanding/RetryLanding, so the blocked landing
          // permanently occupies the queue head and nothing behind it can
          // even start its own Rebase). Verified empirically, not assumed.
          // ══════════════════════════════════════════════════════════════

          let fixedUtil = "module Fixture.Util\n\nlet add a b = a + b\n"
          let! fixSha = writeAndCommit worktree "Util.fs" fixedUtil "fix: revert the regression (tests pass again)"

          let! _ =
            waitForLiveSnapshot http (seconds 60.0) "the revert is discovered as genuinely green again"
              (fun s -> s.Running = 0 && s.Failed = 0)

          // Reuses claim2 — it is still genuinely Held (never released,
          // per the block just proven above), so validateLandingClaims
          // accepts it.
          let! fixLandingResult = requestLanding alice "alice" (sprintf "%s:%d" claim2Id claim2Fence) fixSha "land the fix (empirically checking whether v1 can recover after a block)"
          fixLandingResult |> Expect.stringContains "request_landing itself still succeeds structurally (queueing never inspects the queue's OTHER contents)" "queued"

          let! fixLanded =
            pollForUpTo (seconds 45.0) (fun () -> task {
              let! branchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
              return if branchSha = fixSha then Some branchSha else None
            })

          match fixLanded with
          | Some _ ->
            // v1 CAN recover after a block — genuinely better than the
            // source read predicted. Land the good news.
            let! branchAfterFix = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
            branchAfterFix
            |> Expect.equal "HONEST FINDING (better than predicted): the fix landed after the earlier block — v1's cohort queue recovers on its own" fixSha
          | None ->
            // Matches the source-read prediction: the fix landing never
            // gets its own Rebase started because the QUEUE HEAD (the
            // earlier, still-Blocked breaking landing) is never popped —
            // Cohort.fs's `advanceQueue` only fires the next landing's
            // Rebase effect when the FRONT of the queue is `Queued`, and
            // `TestsCompleted`'s `Blocked` arm never removes the landing
            // from `CohortState.Queue`. There is no CancelLanding/
            // RetryLanding command in `CohortCommand<'m>` to unstick it.
            let! branchStillAtGood = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
            branchStillAtGood
            |> Expect.equal
              "HONEST FINDING (matches source-read prediction): once a landing blocks on failing tests, it permanently occupies the cohort's landing queue head in v1 — a SUBSEQUENT landing (even a genuinely fixing one, reusing the still-Held claim) never lands either, because Cohort.fs has no CancelLanding/RetryLanding command to un-stick the blocked queue head. This is a real, separate gap from the gate itself working correctly."
              goodSha

          http.Dispose()

          // ── Teardown: kill the daemon this test owns, then confirm zero leftovers ──
          killDaemon proc
          daemonProc <- None

          let daemonStillAlive =
            try
              let p = Process.GetProcessById proc.Id
              not p.HasExited
            with _ -> false
          daemonStillAlive |> Expect.isFalse "the isolated daemon process must not remain running after teardown"

          let pgrepPsi =
            ProcessStartInfo(
              "pgrep", sprintf "-f %s" dataDir,
              RedirectStandardOutput = true,
              RedirectStandardError = true,
              UseShellExecute = false)
          let pgrepProc = Process.Start pgrepPsi
          let! (leftovers: string) = pgrepProc.StandardOutput.ReadToEndAsync()
          pgrepProc.WaitForExit 5000 |> ignore
          pgrepProc.Dispose()
          leftovers.Trim()
          |> Expect.isEmpty "no process may still reference this test's isolated SAGEFS_DATA_DIR after teardown"
        finally
          match daemonProc with
          | Some p -> killDaemon p
          | None -> ()
          (try Directory.Delete(mainRepo, true) with _ -> ())
          (try Directory.Delete(dataDir, true) with _ -> ())
      }
    ]
  ]
