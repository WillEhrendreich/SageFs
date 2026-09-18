/// Orchestrator for the COHORT landing-gate demo recording.
///
/// Two subcommands, run by `drive-cohort.sh`:
///
///   setup-fixture --dir DIR
///     Creates the throwaway temp git repo + real, tiny, PREBUILT Expecto
///     fixture project the beats below drive, exactly the discipline
///     `CohortLandingGateIntegrationTests.fs` documents in its own header
///     ("WHY THE FIXTURE COMMITS bin/obj", "WHY THE FIXTURE PINS AN SDK")
///     — a fresh git WORKTREE never inherits the previous checkout's
///     untracked bin/obj, and SageFs never runs `dotnet build` itself at
///     session-create time, so an unbuilt fixture would race warmup.
///
///   run-beats --mcp-port N --main-repo DIR
///     Connects TWO real MCP client connections ("alice", "bob") to the
///     already-running isolated daemon rooted at DIR, and drives the five
///     demo beats end to end:
///       1. join_cohort x2 — alice first, becomes Conductor.
///       2. Disjoint acquire_claim x2, then bob's claim on alice's file is
///          rejected as a CONFLICT.
///       3. alice (conductor) set_integration_ref HEAD; enable live testing
///          on the real integration session; wait for a real green
///          baseline.
///       4. alice lands a GOOD change (a second passing test) through the
///          real `cohortLandingPerformer` pipeline — Queued -> Rebasing ->
///          Verifying -> Landed.
///       5. bob re-acquires the now-released claim and lands a BREAKING
///          change (`add` -> `-`) that fails both discovered tests — the
///          money shot: the git ref never advances, the landing shows
///          Blocked/FailingTests, and the claim stays Held. No human in
///          the loop rejected it; the real, discovered, failing test did.
///
/// Every helper here mirrors (not imports — this is standalone demo
/// tooling, not test code) `SageFs.Tests/CohortLandingGateIntegrationTests.fs`
/// and `SageFs.Tests/CohortDogfoodIntegrationTests.fs`, which are the
/// proven, CI-passing shape of this exact flow. Camera pauses
/// (`--pause-seconds`) are inserted between beats purely for the
/// recording — they play no role in correctness.
module CohortOrchestrator.Program

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol

// ───────────────────────── git + fixture plumbing ─────────────────────────

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

/// Same fixture shape as CohortLandingGateIntegrationTests.fs's
/// writeFixtureSources: `add a b = a + b` + one seeded passing test. See
/// that file's header for why bin/obj get committed and why an SDK is
/// pinned via global.json.
let private writeFixtureSources (dir: string) : unit =
  let fsproj =
    "<Project Sdk=\"Microsoft.NET.Sdk\">\n\n" +
    "  <PropertyGroup>\n" +
    "    <OutputType>Exe</OutputType>\n" +
    "    <TargetFramework>net10.0</TargetFramework>\n" +
    "    <GenerateProgramFile>false</GenerateProgramFile>\n" +
    "  </PropertyGroup>\n\n" +
    "  <ItemGroup>\n" +
    "    <Compile Include=\"UtilTests.fs\" />\n" +
    "    <Compile Include=\"Program.fs\" />\n" +
    "  </ItemGroup>\n\n" +
    "  <ItemGroup>\n" +
    "    <PackageReference Include=\"Expecto\" Version=\"11.0.0-alpha8\" />\n" +
    "  </ItemGroup>\n\n" +
    "</Project>\n"
  let utilTests =
    "module Fixture.UtilTests\n\n" +
    "open Expecto\n" +
    "open Expecto.Flip\n\n" +
    "let add a b = a + b\n\n" +
    "[<Tests>]\n" +
    "let tests =\n" +
    "  testList \"Fixture.Util\" [\n" +
    "    test \"add 2 2 = 4\" {\n" +
    "      add 2 2 |> Expect.equal \"2+2=4\" 4\n" +
    "    }\n" +
    "  ]\n"
  let program =
    "module Fixture.Program\n\n" +
    "open Expecto\n\n" +
    "[<EntryPoint>]\n" +
    "let main argv =\n" +
    "  Tests.runTestsWithCLIArgs [] argv UtilTests.tests\n"
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
  File.WriteAllText(Path.Combine(dir, "UtilTests.fs"), utilTests)
  File.WriteAllText(Path.Combine(dir, "Program.fs"), program)

let private setupFixture (dir: string) : Task<unit> =
  task {
    Directory.CreateDirectory dir |> ignore
    let! _ = git dir [ "init"; "--quiet"; "-b"; "main" ]
    let! _ = git dir [ "config"; "user.email"; "sagefs-cohort-demo@example.com" ]
    let! _ = git dir [ "config"; "user.name"; "SageFs Cohort Demo" ]
    writeFixtureSources dir
    do! dotnetBuildQuiet dir
    let! _ = git dir [ "add"; "-A" ]
    let! _ = commitStaged dir "base: real Expecto test project, prebuilt (see CohortLandingGateIntegrationTests.fs header)"
    ()
  }

// ───────────────────────────── MCP plumbing ────────────────────────────────

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

let private isSessionReady (sessionId: string) (listSessionsText: string) =
  listSessionsText.Contains sessionId && listSessionsText.Contains " Ready "

// Iterative — not `let rec ... return! self` — a stack-safety lesson this
// repo already paid for (the Gap 3 flake: a recursive task-CE poll unwinds
// completion through every frame, and F# task does not trampoline return!,
// so a slow run needing many iterations can overflow the stack).
let private waitUntil (deadline: DateTime) (describe: unit -> string) (check: unit -> Task<bool>) : Task<unit> =
  task {
    let mutable satisfied = false
    while not satisfied do
      let! ok = check ()
      if ok then satisfied <- true
      elif DateTime.UtcNow > deadline then failwithf "condition not met within timeout: %s" (describe ())
      else do! Task.Delay 250
  }

let private pollForUpTo (deadline: DateTime) (probe: unit -> Task<'a option>) : Task<'a option> =
  task {
    let mutable result = None
    let mutable finished = false
    while not finished do
      let! r = probe ()
      match r with
      | Some _ -> result <- r; finished <- true
      | None ->
        if DateTime.UtcNow > deadline then finished <- true
        else do! Task.Delay 500
    return result
  }

let private seconds (n: float) = DateTime.UtcNow.AddSeconds n

let private lineFor (status: string) (id: string) : string =
  status.Split('\n')
  |> Array.tryFind (fun line -> line.Contains id)
  |> Option.defaultWith (fun () -> failwithf "no line in get_cohort_status mentions '%s'. Status:\n%s" id status)

// ── Raw HTTP against the live-testing routes (no MCP tool for these in v1
//    — see CohortLandingGateIntegrationTests.fs) ──

type private LiveSnapshot = {
  DiscoveryState: string
  Total: int
  Passed: int
  Failed: int
  Running: int
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
    return {
      DiscoveryState = root.GetProperty("DiscoveryState").GetString()
      Total = summary.GetProperty("Total").GetInt32()
      Passed = summary.GetProperty("Passed").GetInt32()
      Failed = summary.GetProperty("Failed").GetInt32()
      Running = summary.GetProperty("Running").GetInt32()
    }
  }

let private waitForLiveSnapshot
  (http: HttpClient)
  (deadline: DateTime)
  (describe: string)
  (predicate: LiveSnapshot -> bool)
  : Task<LiveSnapshot> =
  task {
    let mutable found = None
    while Option.isNone found do
      let! snap = getLiveSnapshot http
      if predicate snap then found <- Some snap
      elif DateTime.UtcNow > deadline then failwithf "live-testing status never satisfied '%s' within timeout. Last snapshot: %A" describe snap
      else do! Task.Delay 500
    return found.Value
  }

// ───────────────────────── console/camera plumbing ─────────────────────────

let mutable private pauseSeconds = 3.0

let private beat (n: int) (title: string) : Task<unit> =
  task {
    Console.Out.WriteLine()
    Console.Out.WriteLine(sprintf "══════════════════════════════════════════════════════════════")
    Console.Out.WriteLine(sprintf "BEAT %d — %s" n title)
    Console.Out.WriteLine(sprintf "══════════════════════════════════════════════════════════════")
    Console.Out.Flush()
  }

let private show (label: string) (text: string) : Task<unit> =
  task {
    Console.Out.WriteLine(sprintf "--- %s ---" label)
    Console.Out.WriteLine(text)
    Console.Out.Flush()
  }

let private camera (secs: float) : Task<unit> =
  task {
    Console.Out.WriteLine(sprintf "[camera pause %.1fs]" secs)
    Console.Out.Flush()
    do! Task.Delay(TimeSpan.FromSeconds secs)
  }

// ─────────────────────────────── beats ──────────────────────────────────

let private runBeats (mcpPort: int) (mainRepo: string) (gateTimeoutSeconds: float) (sessionFile: string option) : Task<int> =
  task {
    let mutable failures = 0
    let assertTrue (msg: string) (cond: bool) =
      if cond then Console.Out.WriteLine(sprintf "  [OK] %s" msg)
      else
        failures <- failures + 1
        Console.Out.WriteLine(sprintf "  [FAIL] %s" msg)

    use! alice = connect mcpPort
    use! bob = connect mcpPort
    let http = new HttpClient()
    http.BaseAddress <- Uri(sprintf "http://localhost:%d" mcpPort)

    // ── Beat 1: join_cohort x2, alice becomes conductor ──
    do! beat 1 "alice and bob join the cohort — alice becomes Conductor"
    let! aliceJoin = joinCohort alice "alice" "Implementer"
    do! show "join_cohort(alice)" aliceJoin
    assertTrue "alice becomes conductor as the sole first joiner" (aliceJoin.Contains "You are the conductor")

    let! bobJoin = joinCohort bob "bob" "Implementer"
    do! show "join_cohort(bob)" bobJoin
    assertTrue "bob does not become conductor" (not (bobJoin.Contains "You are the conductor"))

    let! statusAfterJoin = getCohortStatus alice
    do! show "get_cohort_status" statusAfterJoin
    assertTrue "status lists both members" (statusAfterJoin.Contains "Members (2):")
    do! camera pauseSeconds

    // ── Beat 2: disjoint claims, then a real conflict ──
    do! beat 2 "disjoint claims light up the territory map, then bob's claim on alice's file is REJECTED"
    let! aliceAcquire = acquireClaim alice "alice" "file:UtilTests.fs" "extend Util.fs test coverage"
    do! show "acquire_claim(alice, file:UtilTests.fs)" aliceAcquire
    let claim1Id, claim1Fence = claimIdAndFenceFromAcquireResult aliceAcquire

    let! bobAcquire = acquireClaim bob "bob" "file:member-b.fs" "stake out a disjoint file"
    do! show "acquire_claim(bob, file:member-b.fs)" bobAcquire
    let bobTerritoryClaimId, _ = claimIdAndFenceFromAcquireResult bobAcquire
    assertTrue "disjoint claims mint distinct ids" (claim1Id <> bobTerritoryClaimId)

    let! conflict = acquireClaim bob "bob" "file:UtilTests.fs" "try to steal alice's file"
    do! show "acquire_claim(bob, file:UtilTests.fs) — CONFLICT" conflict
    assertTrue "a claim over an already-held scope is refused, naming the holder" (conflict.Contains "is already claimed by")
    do! camera pauseSeconds

    // ── Beat 3: conductor sets the integration ref; enable live testing; green baseline ──
    do! beat 3 "alice (conductor) wires up the real integration worktree + session; live testing goes green"
    let! setRefResult = setIntegrationRef alice "alice" "HEAD"
    do! show "set_integration_ref(alice, HEAD)" setRefResult
    assertTrue "set_integration_ref reports success" (setRefResult.Contains "Integration configured: head=")
    let worktree, branch, sessionId = worktreeBranchAndSessionFromSetIntegrationRefResult setRefResult
    assertTrue "the integration worktree really exists on disk" (Directory.Exists worktree)
    assertTrue "a real integration session was created" (not (String.IsNullOrWhiteSpace sessionId))
    // Hand the session id back to the driver script so it can navigate the
    // recorded chromium window to /dashboard?session=<id> — the cohort
    // panels only render inside a session view, not on the bare no-session
    // picker page (empirically confirmed via a screenshot probe).
    sessionFile |> Option.iter (fun p -> File.WriteAllText(p, sessionId))

    let! _ =
      waitUntil (seconds 180.0)
        (fun () -> sprintf "session %s to reach Ready" sessionId)
        (fun () -> task {
          let! text = listSessions alice
          return isSessionReady sessionId text
        })

    let! enableStatus, enableBody = postJson http "/api/live-testing/enable" {||}
    assertTrue "live testing enable succeeded" (enableStatus = 200)
    let! _ = postJson http "/api/live-testing/policy" {| category = "unit"; policy = "every" |}

    let! discovered =
      waitForLiveSnapshot http (seconds 120.0) "baseline discovery finds the seeded test"
        (fun s -> s.DiscoveryState = "ready_with_tests" && s.Total >= 1)
    assertTrue "baseline discovers at least the one seeded test" (discovered.Total >= 1)

    let! baseline = getLiveSnapshot http
    let! baselineSettled =
      match baseline.Running = 0 && baseline.Failed = 0 && baseline.Passed >= baseline.Total with
      | true -> task { return baseline }
      | false ->
        task {
          let! _ = postJson http "/api/live-testing/run" {| pattern = ""; category = "" |}
          return! waitForLiveSnapshot http (seconds 120.0) "baseline settles green after an explicit run"
                    (fun s -> s.Running = 0 && s.Failed = 0 && s.Passed >= s.Total && s.Total >= 1)
        }
    assertTrue "baseline is genuinely green before either landing" (baselineSettled.Failed = 0)
    do! show "live-testing baseline" (sprintf "Total=%d Passed=%d Failed=%d" baselineSettled.Total baselineSettled.Passed baselineSettled.Failed)
    do! camera pauseSeconds

    // ── Beat 4: GOOD landing — alice adds a passing test; it lands ──
    do! beat 4 "alice requests a GOOD landing (adds a passing test) — Queued -> Rebasing -> Verifying -> Landed"
    let! _ = git worktree [ "checkout"; "-b"; "work-good" ]
    let! _ = git worktree [ "rm"; "-r"; "--cached"; "bin"; "obj" ]
    let! _ = commitStaged worktree "chore: untrack build output for this worktree checkout"

    let goodUtilTests =
      "module Fixture.UtilTests\n\n" +
      "open Expecto\n" +
      "open Expecto.Flip\n\n" +
      "let add a b = a + b\n\n" +
      "[<Tests>]\n" +
      "let tests =\n" +
      "  testList \"Fixture.Util\" [\n" +
      "    test \"add 2 2 = 4\" {\n" +
      "      add 2 2 |> Expect.equal \"2+2=4\" 4\n" +
      "    }\n" +
      "    test \"add 5 5 = 10\" {\n" +
      "      add 5 5 |> Expect.equal \"5+5=10\" 10\n" +
      "    }\n" +
      "  ]\n"
    let! goodSha = writeAndCommit worktree "UtilTests.fs" goodUtilTests "good: extend Util test coverage (keeps the suite green)"

    let! _ =
      waitForLiveSnapshot http (seconds 120.0) "the second test is discovered and the suite stays green"
        (fun s -> s.Total >= 2 && s.Running = 0 && s.Failed = 0)

    let! goodLandingResult = requestLanding alice "alice" (sprintf "%s:%d" claim1Id claim1Fence) goodSha "land a real, test-verified, passing change"
    do! show "request_landing(alice, GOOD)" goodLandingResult
    assertTrue "request_landing queues the good landing" (goodLandingResult.Contains "queued")

    do!
      waitUntil (seconds 120.0)
        (fun () -> sprintf "integration branch %s to reach the good landing %s" branch goodSha)
        (fun () -> task {
          let! branchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
          return branchSha = goodSha
        })
    let! branchAfterGood = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
    assertTrue "THE GOOD LANDING REALLY LANDED — the integration branch fast-forwarded to it" (branchAfterGood = goodSha)

    do!
      waitUntil (seconds 60.0)
        (fun () -> sprintf "claim %s to show Released" claim1Id)
        (fun () -> task {
          let! status = getCohortStatus alice
          return status.Split('\n') |> Array.exists (fun l -> l.Contains claim1Id && l.Contains "state=Released")
        })
    let! statusAfterGood = getCohortStatus alice
    do! show "get_cohort_status after GOOD landing" statusAfterGood
    do! camera pauseSeconds

    // ── Beat 5: BREAKING landing — bob re-claims Util.fs and lands a regression ──
    do! beat 5 "bob requests a BREAKING landing (add -> subtract) — THE GATE PROOF: automatically BLOCKED, no human in the loop"
    let! bobAcquire2 = acquireClaim bob "bob" "file:UtilTests.fs" "introduce and land a regression (expected to block)"
    do! show "acquire_claim(bob, file:UtilTests.fs) — now free after alice's release" bobAcquire2
    let claim2Id, claim2Fence = claimIdAndFenceFromAcquireResult bobAcquire2

    let! _ = git worktree [ "checkout"; "-b"; "work-break" ]
    let breakingUtilTests =
      "module Fixture.UtilTests\n\n" +
      "open Expecto\n" +
      "open Expecto.Flip\n\n" +
      "let add a b = a - b\n\n" +
      "[<Tests>]\n" +
      "let tests =\n" +
      "  testList \"Fixture.Util\" [\n" +
      "    test \"add 2 2 = 4\" {\n" +
      "      add 2 2 |> Expect.equal \"2+2=4\" 4\n" +
      "    }\n" +
      "    test \"add 5 5 = 10\" {\n" +
      "      add 5 5 |> Expect.equal \"5+5=10\" 10\n" +
      "    }\n" +
      "  ]\n"
    let! breakSha = writeAndCommit worktree "UtilTests.fs" breakingUtilTests "break: introduce a regression in add (expected to block landing)"

    let! failedSnapshot =
      waitForLiveSnapshot http (seconds 120.0) "the regression is discovered as a real failure before landing is requested"
        (fun s -> s.Running = 0 && s.Failed >= 1)
    assertTrue "the regression genuinely fails at least one live-tested test" (failedSnapshot.Failed >= 1)
    do! show "live-testing after the breaking edit" (sprintf "Total=%d Passed=%d Failed=%d" failedSnapshot.Total failedSnapshot.Passed failedSnapshot.Failed)

    let! breakLandingResult = requestLanding bob "bob" (sprintf "%s:%d" claim2Id claim2Fence) breakSha "land a regression (expected to block on the real failing test)"
    do! show "request_landing(bob, BREAKING)" breakLandingResult
    assertTrue "request_landing still queues the breaking landing (queueing always succeeds; verification is what blocks it)" (breakLandingResult.Contains "queued")

    let! wronglyLanded =
      pollForUpTo (seconds gateTimeoutSeconds) (fun () -> task {
        let! branchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
        return if branchSha = breakSha then Some branchSha else None
      })
    assertTrue (sprintf "THE GATE PROOF (polled %.0fs): a landing carrying a commit that fails a real, discovered live test never fast-forwards the integration branch" gateTimeoutSeconds) (Option.isNone wronglyLanded)

    let! finalBranchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
    assertTrue "the integration branch still sits exactly where the GOOD landing left it" (finalBranchSha = goodSha)

    let! statusAfterBlock = getCohortStatus bob
    do! show "get_cohort_status — THE MONEY SHOT" statusAfterBlock
    let claim2Line = lineFor statusAfterBlock claim2Id
    assertTrue "the blocked landing's claim is still Held on its own line" (claim2Line.Contains "state=Held")
    assertTrue "the blocked landing's claim is NOT Released" (not (claim2Line.Contains "state=Released"))
    assertTrue "the read model surfaces landings directly" (statusAfterBlock.Contains "Landings (")
    assertTrue "the breaking landing is Blocked on the REAL failing test" (statusAfterBlock.Contains "FailingTests")

    do! camera (pauseSeconds * 2.0)

    http.Dispose()
    return failures
  }

// ──────────────────────────────── entry point ───────────────────────────────

let private argValue (argv: string[]) (flag: string) (fallback: string option) : string =
  let idx = Array.tryFindIndex ((=) flag) argv
  match idx with
  | Some i when i + 1 < argv.Length -> argv.[i + 1]
  | _ ->
    match fallback with
    | Some v -> v
    | None -> failwithf "missing required argument %s" flag

[<EntryPoint>]
let main argv =
  try
    match argv with
    | _ when argv.Length > 0 && argv.[0] = "setup-fixture" ->
      let dir = argValue argv "--dir" None
      setupFixture(dir).GetAwaiter().GetResult()
      printfn "fixture ready at %s" dir
      0
    | _ when argv.Length > 0 && argv.[0] = "run-beats" ->
      let port = argValue argv "--mcp-port" None |> int
      let mainRepo = argValue argv "--main-repo" None
      pauseSeconds <- argValue argv "--pause-seconds" (Some "3") |> float
      let gateTimeout = argValue argv "--gate-timeout-seconds" (Some "40") |> float
      let sessionFile =
        match Array.tryFindIndex ((=) "--session-file") argv with
        | Some i when i + 1 < argv.Length -> Some argv.[i + 1]
        | _ -> None
      let failures = (runBeats port mainRepo gateTimeout sessionFile).GetAwaiter().GetResult()
      Console.Out.WriteLine()
      if failures = 0 then
        Console.Out.WriteLine "ALL BEATS PASSED — cohort landing gate proven: good change landed, breaking change automatically blocked."
        0
      else
        Console.Out.WriteLine(sprintf "%d ASSERTION(S) FAILED — see [FAIL] lines above." failures)
        1
    | _ ->
      eprintfn "usage: CohortOrchestrator (setup-fixture --dir DIR) | (run-beats --mcp-port N --main-repo DIR [--pause-seconds N] [--gate-timeout-seconds N])"
      2
  with ex ->
    eprintfn "FATAL: %s" (ex.ToString())
    1
