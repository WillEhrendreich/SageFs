/// The multi-agent vision's "honest end-to-end proof": a LIVE cohort dogfood
/// against a REAL isolated daemon, spawned by this process on its own port
/// and its own SAGEFS_DATA_DIR — never the user's live daemon (37749/37750)
/// — driving the production cohort flow through TWO real, distinct MCP
/// client connections (two "agents"), exactly the shape
/// `cohort-integration-plan.md`/`sagefs-multiagent-vision.md` describe:
///
///   join_cohort (x2, distinct connections) -> acquire_claim (disjoint
///   scopes) -> set_integration_ref (conductor-only, real git worktree) ->
///   two real commits, each rebased, verified, and fast-forwarded onto the
///   integration branch by `DaemonMode.fs`'s REAL `cohortLandingPerformer`
///   (not a fake/stub — the exact performer production wires into every
///   daemon) -> teardown with zero leftover processes.
///
///   UPDATE: an earlier revision also re-asserted a real claim CONFLICT
///   (bob stealing alice's already-held scope) and a conductor-only role-gate
///   refusal (bob calling set_integration_ref) through this same real daemon.
///   Both are pure `Cohort.decide` decisions — claim exclusivity and the
///   SetIntegrationHead conductor gate — already covered elsewhere WITHOUT a
///   daemon (claim exclusivity by CohortPropertyTests' property 1; the
///   conductor gate by `CohortIntegrationHeadTests.fs`'s dedicated
///   "Cohort.SetIntegrationHead (item 14c)" list) — so those daemon
///   round trips were simply deleted, no replacement needed. See the "PURE
///   Cohort.decide decisions" section below for the one decision that WAS
///   genuinely uncovered (FastForwardCompleted's claim-release side effect)
///   and got its own new, no-daemon `testCase` here instead.
///
/// What is REAL:
///  - The daemon: a genuine `SageFs` process, `--owner-pid`/`--owner-start`/
///    `--ttl`-fenced (same ownership discipline as
///    `CohortMcpToolsIntegrationTests.fs`/`HttpApiIntegrationTests.fs`), on a
///    reserved loopback port, with an isolated `SAGEFS_DATA_DIR` and a
///    throwaway temp git repo as its working directory (never this repo,
///    never the user's).
///  - Two MCP connections = two cohort members. Identity is bound to the MCP
///    CONNECTION (`Mcp.fs`'s `memberIdFor`/`currentTransportSessionId`), so
///    this genuinely exercises `MemberTable`'s per-connection identity, not
///    two calls on one client with different `agentName` strings.
///  - `set_integration_ref`, `acquire_claim`, `request_landing`: real MCP
///    tool calls through `ModelContextProtocol.Client.McpClient`
///    (`connect`/`callTool` below — same shape as
///    `CohortMcpToolsIntegrationTests.fs`).
///  - The landing pipeline: `DaemonMode.fs`'s production
///    `cohortLandingPerformer` (~line 1712) — the SAME performer every real
///    daemon wires in, not a test double. `Rebase`/`FastForward` run real
///    `git` subprocesses against a real integration worktree that
///    `set_integration_ref` creates automatically.
///
/// What is HONESTLY LIMITED (documented, not hidden):
///  - `ComputeAffected`/`RunTests` run for real against the daemon-owned
///    integration SESSION `set_integration_ref` also creates — but this
///    fixture's temp repo carries no live-testing-discovered tests (no
///    `.fsproj`, no live-testing enabled), so `ComputeAffected` returns `[]`
///    and both landings verify zero tests. This is the SAME documented v1
///    scope `CohortLandingGitAcceptanceTests.fs`'s header names for item
///    14c/14d — a live-testing-backed landing (tests actually failing a
///    landing) is not exercised by this file either. That remains a real,
///    separate gap: nothing in this repo's test suite yet proves a landing
///    blocked by a REAL failing test discovered through live testing.
///  - UPDATE (gap closed): `get_cohort_status`/the `cohort://status` MCP
///    resource used to NOT surface landing state or `IntegrationHead` at
///    all (`renderCohortFrame`'s hard-coded line, formerly `Mcp.fs:4550`:
///    "Landing queue: not yet in the v1 read model") — this test's own
///    assertions were the honest proof of that gap. `Cohort.CohortFrame`
///    (`Cohort.fs`) now carries `IntegrationHead` plus one row per landing
///    (id/requester/statement/commits/state/queue position), populated by
///    `Cohort.project` straight from `CohortState.Landings`/`Queue`/
///    `IntegrationHead`, and both `renderCohortFrame` (`Mcp.fs`) and
///    `cohortFrameJson` (`SseWriter.fs`, the `cohort://status` payload) now
///    render it — see this test's `Landings (` / `state=Landed` assertions
///    below, which replace the old "not yet in the v1 read model" checks.
///    Still stale, out of this fix's file-ownership scope: `request_landing`'s
///    own `[<Description>]` on `get_cohort_status` (`McpTools.fs`) still says
///    the v1 read model has no landing queue — that string was not touched
///    here (another change owns `McpTools.fs`). UPDATE: the claim-auto-release
///    side-effect proof (`Cohort.decide`'s `FastForwardCompleted` handler,
///    `Cohort.fs:911-921`) used to be re-asserted here as `state=Released` in
///    the real-daemon `get_cohort_status` text (a pure Cohort.decide decision,
///    reproved through a real daemon round-trip). It has moved to this file's
///    own PURE "FastForwardCompleted auto-releases every backing claim"
///    testCase (below, outside `Integration.hostList` — runs in the default
///    suite, no daemon needed) — same coverage, no daemon, no network.
///  - Also stale, discovered while writing this test: `request_landing`'s
///    own `[<Description>]` (`McpTools.fs:1862`) still says "rebase/verify/
///    land themselves are a later slice's wiring and are not yet performed"
///    — false today; `DaemonMode.fs`'s real performer (item 14c) has been
///    wired in for every daemon since that slice landed. The tool works;
///    its own doc string undersells it. Reported, not fixed here (out of
///    this item's file-ownership scope).
///  - Two landings genuinely serialize through the ONE FIFO queue
///    (`Cohort.fs:286`, "Only Queue.Head may be Rebasing/Verifying";
///    `advanceQueue`, `Cohort.fs:509-521`, only kicks off the next queued
///    landing's Rebase effect once the front lands and is popped) — but
///    this test does not race two landings queued concurrently. It CANNOT
///    safely do so with the real performer: `Rebase`'s `CohortGit.rebase`
///    rebases whatever branch is CURRENTLY CHECKED OUT in the shared
///    integration worktree (`CohortGit.fs:208`, "Rebases the checked-out
///    branch in repoDir") — there is exactly one worktree/one checkout per
///    cohort in v1, so two members' commits cannot both be "checked out"
///    at once. This test therefore prepares and lands member A's commit
///    FIRST, waits for it to genuinely land (the git-ref oracle), THEN
///    checks out member B's branch and lands B's commit — real, sequential,
///    production-performer landings, each seeing the state the previous one
///    left (B's rebase base is A's already-landed head), but not a stress
///    test of concurrent queue contention. A true concurrent-submission
///    race would need one integration worktree PER IN-FLIGHT LANDING (or a
///    worktree-per-member model) — a genuine open design question for the
///    vision beyond this item's scope, reported here rather than faked.
///
/// This suite is registered `Integration.hostList` — the SAME structural
/// registry `CohortMcpToolsIntegrationTests.fs`/`CohortLandingGitAcceptanceTests.fs`
/// use, so it runs under the existing `--integration-host` entry point with
/// no new CLI flag. `AGENTS.md`'s own `TestInfrastructure.Integration`
/// module doc says why: "--integration-host runs EVERY suite registered as
/// Host, so a new self-contained suite joins CI by construction — no
/// curated list." A bespoke `--integration-cohort` flag would duplicate
/// exactly the name-convention fragility `Program.fs`'s own comments reject
/// elsewhere in this repo (the "no magic strings" / "structural, not a
/// name-string filter" doctrine) — so this file intentionally does NOT add
/// one, and `Program.fs` is untouched.
module SageFs.Tests.CohortDogfoodIntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open SageFs
open SageFs.Cohort

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

// ── Minimal, independent git helper (mirrors CohortLandingGitAcceptanceTests.fs's
//    discipline: fixture setup and oracle verification never go through
//    CohortGit itself, so a shared bug can't hide behind a shared implementation) ──

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

let private reserveLoopbackPort () =
  use listener = new TcpListener(IPAddress.Loopback, 0)
  listener.Start()
  (listener.LocalEndpoint :?> IPEndPoint).Port

/// Spawn an isolated daemon rooted at `workingDir` (a throwaway temp git
/// repo, NEVER this repo, NEVER the user's live checkout) with its own
/// port and its own `SAGEFS_DATA_DIR`. Owned by this test process
/// (--owner-pid/--owner-start) plus a --ttl belt-and-braces, exactly
/// `CohortMcpToolsIntegrationTests.fs`'s `startIsolatedDaemon`.
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

  // Event-driven, not a hand-rolled attempt-counter poll: the same
  // TestInfrastructure.waitForAsync every other integration daemon-readiness
  // wait in this suite uses, on the named Timeouts.integrationDaemonReady
  // deadline ("Deadline for a spawned test daemon to reach a readable Ready
  // state") instead of a bare 300*200ms=60s magic-number loop.
  let! ready =
    SageFs.Tests.TestInfrastructure.waitForAsync
      (int Timeouts.integrationDaemonReady.TotalMilliseconds)
      (fun () -> task {
        try
          let! resp = client.GetAsync "/health"
          return int resp.StatusCode > 0
        with _ -> return false
      })

  if not ready then
    let killAttempt = try proc.Kill true; true with _ -> false
    ignore killAttempt
    proc.Dispose()
    failwithf "cohort dogfood daemon failed to start on port %d within %.0fs" port Timeouts.integrationDaemonReady.TotalSeconds

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

/// "Joined cohort as mcp:xxxx (Implementer)...." -> "mcp:xxxx". Parses OUR
/// OWN tool's documented output shape (`Mcp.fs`'s `joinCohort` — see its
/// `sprintf "Joined cohort as %s (%s)..."`), fragile-string-averse in the
/// same sense `CohortMcpToolsIntegrationTests.fs`'s `claimIdFromAcquireResult`
/// is: it decodes a format this test suite's own production code defines.
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
/// (or the WARNING variant with no `session=`). Extracts `worktree=` and
/// `branch=` — the two fields this test needs to drive real git against the
/// worktree `set_integration_ref` created.
let private worktreeAndBranchFromSetIntegrationRefResult (text: string) : string * string =
  let extract (marker: string) =
    let start = text.IndexOf marker
    if start < 0 then failwithf "set_integration_ref result missing '%s': %s" marker text
    let afterMarker = start + marker.Length
    let stop =
      let space = text.IndexOf(' ', afterMarker)
      if space < 0 then text.Length else space
    text.Substring(afterMarker, stop - afterMarker)
  extract "worktree=", extract "branch="

// Event-driven, not a hand-rolled deadline/sleep loop: delegates the actual
// polling to TestInfrastructure.waitForAsync (an O(1)-stack-depth while loop,
// not a `let rec ... return! self` — that recursive shape overflows the stack
// on a slow runner needing many iterations, the same flake fixed in
// CohortLandingGateIntegrationTests) and only adds the replayable failure
// description this suite's git-ref/read-model oracles want on a miss.
let private waitUntil (timeoutMs: int) (describe: unit -> string) (check: unit -> Task<bool>) : Task<unit> =
  task {
    let! satisfied = SageFs.Tests.TestInfrastructure.waitForAsync timeoutMs check
    if not satisfied then failtestf "condition not met within timeout: %s" (describe ())
  }

/// Deadline for this smoke's real-git/read-model settle waits (branch ref
/// reaching a landed sha). `Timeouts.cohortIntegrationSettle` is the named
/// constant the production landing gate itself uses for "wait for a rebase's
/// landing to settle" — the same semantic wait this test performs against a
/// real daemon, so it reuses the real timeout rather than a bare local literal.
let private landingSettleTimeoutMs () = int Timeouts.cohortIntegrationSettle.TotalMilliseconds

// ── PURE Cohort.decide decisions this real-daemon dogfood used to re-prove ─
//
// The dogfood smoke below used to re-assert TWO pure `Cohort.decide`
// decisions through a real spawned daemon (join_cohort/set_integration_ref/
// get_cohort_status round trips):
//   - the SetIntegrationHead conductor gate — turns out this is NOT a
//     genuinely-uncovered decision: `SageFs.Tests/CohortIntegrationHeadTests.fs`
//     ("Cohort.SetIntegrationHead (item 14c)") already exercises `decide`
//     directly for exactly this (conductor succeeds; a mere Member is refused
//     NotConductor; an anonymous/never-joined caller is refused NotConductor;
//     an unrelated queued landing is untouched). So its real-daemon re-assert
//     was simply deleted — no replacement needed, the pure coverage already
//     exists (found while writing this section; CohortPropertyTests' own
//     properties 19/20 cover the analogous ReassignClaim/DelegateConductor
//     gates but not this one — the actual pure coverage lives in a THIRD
//     file, not CohortPropertyTests).
//   - FastForwardCompleted's claim-auto-release side effect (Cohort.fs's
//     `FastForwardCompleted` handler releasing every backing claim) — this
//     one IS genuinely uncovered as a pure fact: neither CohortPropertyTests
//     (property 6 only checks claims are Held BEFORE land, not Released
//     after) nor any other file in this suite asserts `ClaimState.Released`
//     as `FastForwardCompleted`'s own direct, pure result. It gets a fast,
//     no-daemon `Cohort.decide` testCase here — the same "call `decide`
//     directly, assert the resulting state/events" shape CohortPropertyTests'
//     own targeted-race tests (4, 11, 12, 15, 19, 20) and
//     CohortIntegrationHeadTests.fs use. Runs in the DEFAULT suite (not
//     `Integration.hostList` below), since it needs no process, no port, and
//     no git.
[<Tests>]
let pureDecisionTests =
  testList "CohortDogfood — pure Cohort.decide decisions (no daemon)" [

    testCase "FastForwardCompleted auto-releases every backing claim" <| fun () ->
      let clock = DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
      let step state cmd =
        match decide clock [||] state cmd with
        | Ok(s, _, _) -> s
        | Error e -> failwithf "unexpected setup failure: %A" e
      let requester = "alice"
      let s1 = step (CohortState.empty ()) (CohortCommand.Join(requester, JoinableRole.Implementer, None))
      let s2 = step s1 (CohortCommand.AcquireClaim(requester, ClaimScope.File "X.fs", "purpose"))
      let claimId, claimFence =
        match s2.Claims |> Map.toList with
        | [ (cid, c) ] -> cid, c.Fence
        | other -> failwithf "expected exactly one claim after setup, got %A" other
      let s3 = step s2 (CohortCommand.RequestLanding(requester, [ claimId, claimFence ], [ "commit-1" ], "land it"))
      let landingId =
        match s3.Landings |> Map.toList with
        | [ (lid, _) ] -> lid
        | other -> failwithf "expected exactly one landing after setup, got %A" other
      // Force the landing straight to Verifying against the CURRENT
      // IntegrationHead so FastForwardCompleted's own Property-11 HeadMoved
      // guard passes — this test's subject is the release side effect, not
      // the rebase/verify pipeline (that is CohortLandingSim's job).
      let verifying =
        { s3.Landings.[landingId] with
            State = LandingState.Verifying(s3.IntegrationHead, "rebased-head", 0, 0) }
      let s4 = { s3 with Landings = Map.add landingId verifying s3.Landings }

      match decide clock [||] s4 (CohortCommand.FastForwardCompleted(landingId, "landed-sha")) with
      | Ok(s5, events, _) ->
        match s5.Claims.[claimId].State with
        | ClaimState.Released(by, _) -> by |> Expect.equal "the released claim names the landing's own requester" requester
        | other -> failtestf "expected the backing claim to be Released after a successful land, got %A" other
        events
        |> List.exists (function CohortEvent.ClaimReleased(c, by, _) -> c = claimId && by = requester | _ -> false)
        |> Expect.isTrue "a ClaimReleased event is emitted for the landing's backing claim"
      | Error e -> failtestf "expected FastForwardCompleted to succeed and release the backing claim, got %A" e
  ]

[<Tests>]
let tests =
  Integration.hostList "CohortDogfood" [

    if not (gitAvailable ()) then
      testCase "git must be available on PATH" <| fun () ->
        failtest "git is not available on PATH — the cohort dogfood's real landing pipeline requires a real git executable"
    else

    yield! [

      testTask "WHY — a real two-member cohort joins over two distinct MCP connections, claims disjoint files, and lands two real commits through the real daemon-owned landing pipeline (multi-agent vision, honest e2e proof)" {
        // ── Fixture: a throwaway temp git repo, never this repo, never the
        // user's live daemon's checkout ──
        let mainRepo = Directory.CreateTempSubdirectory("cohort-dogfood-main-").FullName
        let dataDir = Directory.CreateTempSubdirectory("cohort-dogfood-data-").FullName
        let mutable daemonProc: Process option = None
        try
          let! _ = git mainRepo [ "init"; "--quiet"; "-b"; "main" ]
          let! _ = git mainRepo [ "config"; "user.email"; "sagefs-dogfood-test@example.com" ]
          let! _ = git mainRepo [ "config"; "user.name"; "SageFs Dogfood Test" ]
          // "a couple of F# files" (task brief) — no .fsproj: a bare FSI
          // session is the honest, fast, network-free choice here (see this
          // file's header for why ComputeAffected/RunTests still run for
          // real against it, just over zero discovered tests).
          let! _ = writeAndCommit mainRepo "Util.fs" "module Fixture.Util\n\nlet add a b = a + b\n" "base: add Util.fs"
          let! _ = writeAndCommit mainRepo "Trivial.fs" "module Fixture.Trivial\n\n// trivial test placeholder\nlet trivialTestPasses () = Fixture.Util.add 2 3 = 5\n" "base: add Trivial.fs"

          // ── Spawn the isolated daemon (own port, own SAGEFS_DATA_DIR) ──
          let! proc, port = startIsolatedDaemon mainRepo dataDir
          daemonProc <- Some proc
          let daemonPid = proc.Id

          use! alice = connect port
          use! bob = connect port

          // ── Two distinct connections = two distinct cohort members ──
          // (Conductor-on-first-join is a pure Cohort.decide decision, already
          // proven by CohortPropertyTests' property 21 — "ConductorBound fires
          // at most once and Conductor always equals that first joiner" — and
          // re-asserted at this same real-daemon wire level by
          // CohortMcpToolsIntegrationTests.fs. This smoke's own subject is
          // identity: that two DISTINCT MCP connections bind to two DISTINCT
          // members, which no pure property exercises — synthetic `Agent`
          // identities there are never connection-bound.)
          let! aliceJoin = joinCohort alice "alice" "Implementer"
          let aliceId = memberIdFromJoinResult aliceJoin

          let! (bobJoin: string) = joinCohort bob "bob" "Implementer"
          let bobId = memberIdFromJoinResult bobJoin

          aliceId |> Expect.notEqual "two separate MCP connections must be two separate cohort members, never the same one" bobId

          let! statusAfterJoin = getCohortStatus alice
          statusAfterJoin |> Expect.stringContains "status must list both distinct members" "Members (2):"
          statusAfterJoin.Contains aliceId |> Expect.isTrue "alice's real member id appears in status"
          statusAfterJoin.Contains bobId |> Expect.isTrue "bob's real member id appears in status"

          // ── Disjoint claims ──
          let! aliceAcquire = acquireClaim alice "alice" "file:member-a.fs" "add member-a.fs"
          let aliceClaimId, aliceFence = claimIdAndFenceFromAcquireResult aliceAcquire

          let! bobAcquire = acquireClaim bob "bob" "file:member-b.fs" "add member-b.fs"
          let bobClaimId, bobFence = claimIdAndFenceFromAcquireResult bobAcquire

          aliceClaimId |> Expect.notEqual "disjoint claims mint distinct ids" bobClaimId

          // (A real claim CONFLICT over an already-held scope, and the
          // conductor-only role gate on set_integration_ref, used to be
          // re-asserted here through the real daemon. Both are pure
          // Cohort.decide decisions already covered elsewhere without a
          // daemon: claim exclusivity is CohortPropertyTests' property 1
          // ("no two overlapping claims are Held at once, in any reachable
          // state"), and the SetIntegrationHead conductor gate is
          // CohortIntegrationHeadTests.fs's "Cohort.SetIntegrationHead (item
          // 14c)" list (conductor succeeds / non-conductor refused / anonymous
          // refused / unrelated state untouched — see the "PURE Cohort.decide
          // decisions" section above for how that was found). Both duplicative
          // real-daemon re-asserts were deleted; the wire-level role-gate
          // shape is also independently covered by
          // CohortMcpToolsIntegrationTests.fs for release_claim/reassign.)

          // ── The conductor configures the real integration worktree/branch ──
          let! setRefResult = setIntegrationRef alice "alice" "HEAD"
          setRefResult |> Expect.stringContains "set_integration_ref must report success" "Integration configured: head="
          let worktree, branch = worktreeAndBranchFromSetIntegrationRefResult setRefResult
          Directory.Exists worktree |> Expect.isTrue "the integration worktree must really exist on disk"

          // ── Member A's real commit, landed FIRST through the real production performer ──
          let! _ = git worktree [ "checkout"; "-b"; "member-a-work" ]
          let! memberASha = writeAndCommit worktree "member-a.fs" "module Fixture.MemberA\n\nlet greeting = \"alice was here\"\n" "alice: add member-a.fs"

          let! aliceLandingResult = requestLanding alice "alice" (sprintf "%s:%d" aliceClaimId aliceFence) memberASha "land alice's member-a.fs"
          aliceLandingResult |> Expect.stringContains "request_landing must queue a real landing" "queued"

          // Independent oracle: the real git branch ref, exactly
          // CohortLandingGitAcceptanceTests.fs's discipline.
          do!
            waitUntil (landingSettleTimeoutMs ())
              (fun () -> sprintf "integration branch %s to reach %s" branch memberASha)
              (fun () -> task {
                let! branchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
                return branchSha = memberASha
              })

          // (This used to also wait for, then re-assert, get_cohort_status
          // showing the backing claim Released — an MCP-only proof of
          // Cohort.fs's FastForwardCompleted auto-release side effect. That
          // decision is a pure Cohort.decide transition, now covered directly
          // — and faster, with no daemon round-trip — by this file's own PURE
          // "FastForwardCompleted auto-releases every backing claim"
          // testCase below. The git-ref wait above already proves the landing
          // genuinely completed, so no synchronization was lost by dropping
          // the redundant wait.)
          let! statusAfterAliceLanding = getCohortStatus alice
          // The gap this test used to document is closed: get_cohort_status
          // now surfaces the landing directly (Cohort.fs's CohortFrame gained
          // Landings/IntegrationHead fields, sourced from the same
          // CohortState.Landings this landing's FastForwardCompleted
          // transition already wrote to) instead of the old hard-coded
          // "not yet in the v1 read model" line.
          statusAfterAliceLanding
          |> Expect.stringContains "get_cohort_status now surfaces the landed landing directly" "Landings (1):"
          statusAfterAliceLanding
          |> Expect.stringContains "the landed landing shows its terminal Landed state, not the old placeholder line" "state=Landed"

          // ── Member B's real commit, landed SECOND — only now, after A
          // genuinely landed, is it safe to switch the ONE shared
          // integration worktree's checkout to B's branch (see this file's
          // header on why a true concurrent race isn't exercised) ──
          let! _ = git worktree [ "checkout"; "-b"; "member-b-work"; branch ]
          let! memberBSha = writeAndCommit worktree "member-b.fs" "module Fixture.MemberB\n\nlet greeting = \"bob was here\"\n" "bob: add member-b.fs"

          let! bobLandingResult = requestLanding bob "bob" (sprintf "%s:%d" bobClaimId bobFence) memberBSha "land bob's member-b.fs"
          bobLandingResult |> Expect.stringContains "bob's request_landing must also queue a real landing" "queued"

          do!
            waitUntil (landingSettleTimeoutMs ())
              (fun () -> sprintf "integration branch %s to reach %s" branch memberBSha)
              (fun () -> task {
                let! branchSha = git mainRepo [ "rev-parse"; sprintf "refs/heads/%s" branch ]
                return branchSha = memberBSha
              })

          // ── Final independent-oracle verification: both real commits are
          // really in the real integration branch's real history, in order ──
          let! (finalLog: string) = git mainRepo [ "log"; branch; "--oneline" ]
          finalLog.Contains "alice: add member-a.fs" |> Expect.isTrue "the real git log contains alice's landed commit"
          finalLog.Contains "bob: add member-b.fs" |> Expect.isTrue "the real git log contains bob's landed commit"

          // ── Final status: both claims settled, both members still present ──
          let! finalStatus = getCohortStatus bob
          finalStatus |> Expect.stringContains "both members remain present after landing" "Members (2):"
          finalStatus
          |> Expect.stringContains "both landings are now visible in the read model" "Landings (2):"

          // ── Teardown: kill the daemon this test owns, then confirm zero leftovers ──
          killDaemon proc
          daemonProc <- None

          let daemonStillAlive =
            try
              let p = Process.GetProcessById daemonPid
              not p.HasExited
            with _ -> false
          daemonStillAlive |> Expect.isFalse "the isolated daemon process must not remain running after teardown"

          // Belt-and-braces sweep: any process whose command line still
          // references this test's unique SAGEFS_DATA_DIR (a worker the
          // daemon spawned, e.g. for the integration session) would be
          // found here even if it somehow outlived the process-tree kill.
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
