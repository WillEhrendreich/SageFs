module SageFs.Tests.CohortMcpToolsIntegrationTests

/// RED integration tests for cohort-integration-plan.md Slice 2 (item 9):
/// the CohortOwner wired into the real daemon, exposed as Claims v1 MCP
/// tools (join_cohort/leave_cohort/acquire_claim/release_claim/
/// reassign_claim/request_landing/get_cohort_status).
///
/// Unlike HttpApiIntegrationTests.fs's raw-REST-endpoint tests, cohort tools
/// are ONLY registered as real `[<McpServerTool>]` members (no `/api/...`
/// REST wrapper exists for them, by design — see McpServer.fs's
/// `configureMcpProtocol`/`MapMcp()`), so this suite drives them through a
/// REAL MCP client (`ModelContextProtocol.Client.McpClient` — already a
/// transitive dependency via SageFs.fsproj's `ModelContextProtocol`
/// PackageReference, no new NuGet dependency added here) against a daemon
/// this suite spawns on an isolated port with an isolated SAGEFS_DATA_DIR,
/// exactly the isolation discipline HttpApiIntegrationTests.fs uses.
///
/// Identity is bound to the MCP CONNECTION (Mcp.fs's `memberIdFor`/
/// `currentTransportSessionId`), not to the `agentName` argument — so two
/// "different identities" in cohort terms means two SEPARATE McpClient
/// connections to the same daemon, not two calls on one client with
/// different agentName strings (that would be the SAME member joining
/// twice and hitting DuplicateJoin). Verified live against a real spawned
/// daemon before this file was written.
///
/// EACH TEST GETS ITS OWN DAEMON (own port, own SAGEFS_DATA_DIR, own
/// cohort.ledger.db): the daemon holds exactly one implicit cohort for its
/// whole lifetime (v1, D2), so "the first joiner becomes conductor" is only
/// true once per daemon — sharing one daemon across test cases would make
/// every test after the first observe a cohort that already has a
/// conductor and members from earlier cases.

open System
open System.Diagnostics
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open SageFs

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private repoRoot = RepoPaths.repoPathFull [||]

let private sageFsExe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

/// Spawn an isolated daemon (own port, own SAGEFS_DATA_DIR) and wait for
/// /health to respond. Owned by this test process (--owner-pid/--owner-start,
/// the same ownership fencing HttpApiIntegrationTests.fs uses) plus a --ttl
/// belt-and-braces, so a killed/crashed test process can never orphan it.
let startIsolatedDaemonWith (extraEnv: (string * string) list) : Task<Process * int> = task {
  // TestPorts.reservePair scans only this tier's assigned
  // SAGEFS_TEST_PORT_RANGE when one is set, so a concurrently-running
  // tier's daemon can never win the reserve-then-bind race for this pair
  // (this daemon's dashboard is unused, but SageFs always binds it too).
  let port, _dashboardPort = SageFs.Tests.TestInfrastructure.TestPorts.reservePair ()
  let psi = ProcessStartInfo()
  psi.FileName <- sageFsExe
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- repoRoot
  psi.ArgumentList.Add "--mcp-port"
  psi.ArgumentList.Add(string port)
  psi.ArgumentList.Add("--owner-pid")
  psi.ArgumentList.Add(string (System.Diagnostics.Process.GetCurrentProcess().Id))
  let self = Process.GetCurrentProcess()
  psi.ArgumentList.Add "--owner-pid"
  psi.ArgumentList.Add(string self.Id)
  psi.ArgumentList.Add "--owner-start"
  psi.ArgumentList.Add(string (self.StartTime.ToUniversalTime().Ticks))
  psi.ArgumentList.Add "--ttl"
  psi.ArgumentList.Add "10m"
  let dataDir = IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-test-cohort", Guid.NewGuid().ToString "N")
  psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
  for name, value in extraEnv do
    psi.Environment[name] <- value

  let proc = Process.Start psi
  use client = new Net.Http.HttpClient()
  client.BaseAddress <- Uri(sprintf "http://localhost:%d" port)
  client.Timeout <- SageFs.Tests.TestTimeouts.httpProbe

  // Deadline-based (not a fixed attempt count) so the poll cadence and the
  // give-up bound are independently named and configurable — same pattern as
  // CohortLandingGateIntegrationTests.fs's startIsolatedDaemon, reusing the
  // shared Timeouts constants rather than a bare literal.
  let deadline = DateTime.UtcNow.Add Timeouts.integrationDaemonReady
  let mutable ready = false
  while not ready && DateTime.UtcNow < deadline do
    do! Task.Delay Timeouts.cohortLandingPoll
    try
      let! resp = client.GetAsync "/health"
      if int resp.StatusCode > 0 then ready <- true
    with _ -> ()

  if not ready then
    let killAttempt = try proc.Kill true; true with _ -> false
    ignore killAttempt
    proc.Dispose()
    failwithf "cohort test daemon failed to start on port %d within %O" port Timeouts.integrationDaemonReady

  return proc, port
}

let private startIsolatedDaemon () : Task<Process * int> = startIsolatedDaemonWith []

let killDaemon (proc: Process) =
  try
    if not proc.HasExited then
      proc.Kill(entireProcessTree = true)
      proc.WaitForExit TestTimeouts.patience |> ignore
  with _ -> ()
  proc.Dispose()

/// The tool's own answer: the first TextContentBlock of its result. The daemon's event echo
/// (McpServer.withEventEcho) is a later block of its own, so it is never part of the answer.
let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

/// Connect a fresh MCP client — a fresh client is a fresh transport session,
/// which is a fresh bound cohort identity (Mcp.fs's memberIdFor). Returned
/// as IAsyncDisposable so callers can `use!` it.
let connect (port: int) : Task<McpClient> =
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

/// Item 13c: `join_cohort`'s new optional `working_directory` — resolved to a
/// session id the same way `send_fsharp_code` resolves it (Mcp.fs's
/// `resolveSessionId`) and recorded on the joining member.
let private joinCohortWithDir (client: McpClient) (agentName: string) (role: string) (workingDirectory: string) =
  callTool client "join_cohort" [ "agentName", box agentName; "role", box role; "working_directory", box workingDirectory ]

let private createProjectSession (client: McpClient) (project: string) (workingDirectory: string) =
  callTool client "create_project_session" [ "project", box project; "working_directory", box workingDirectory ]

let private getCohortStatus (client: McpClient) =
  callTool client "get_cohort_status" []

/// `get_cohort_status` with the directory whose cohort is being asked about. One daemon
/// holds one cohort PER REPOSITORY, so a caller that names none reads the daemon's own.
let private getCohortStatusIn (client: McpClient) (workingDirectory: string) =
  callTool client "get_cohort_status" [ "working_directory", box workingDirectory ]

let private acquireClaim (client: McpClient) (agentName: string) (scope: string) (purpose: string) =
  callTool client "acquire_claim" [ "agentName", box agentName; "scope", box scope; "purpose", box purpose ]

let private releaseClaim (client: McpClient) (agentName: string) (claimId: string) (fence: int64) =
  callTool client "release_claim" [ "agentName", box agentName; "claimId", box claimId; "fence", box fence ]

let private reassignClaim (client: McpClient) (agentName: string) (claimId: string) (toMember: string) =
  callTool client "reassign_claim" [ "agentName", box agentName; "claimId", box claimId; "toMember", box toMember ]

/// "Acquired claim c-XXXX over ..." → "c-XXXX". Fragile-string-averse: this
/// parses OUR OWN tool's documented output shape (McpTools.fs's
/// acquire_claim description), not an unrelated format.
let private claimIdFromAcquireResult (text: string) : string =
  let marker = "Acquired claim "
  let start = text.IndexOf marker
  if start < 0 then failwithf "acquire_claim did not report success: %s" text
  let afterMarker = start + marker.Length
  let stop = text.IndexOf(' ', afterMarker)
  text.Substring(afterMarker, stop - afterMarker)

/// Run `body` against a freshly spawned, isolated daemon; the daemon is
/// killed afterwards even on failure.
let withDaemon (body: int -> Task<unit>) : Task<unit> = task {
  let! proc, port = startIsolatedDaemon ()
  try
    do! body port
  finally
    killDaemon proc
}

[<Tests>]
let cohortMcpToolsTests =
  Integration.hostList "Cohort MCP tools" [

    /// THE ORIGINAL BUG, end to end, over a real MCP connection to a real daemon.
    ///
    /// The daemon started ONE `CohortOwner`, bound to the scope it was launched from, and
    /// dispatched every command through it. `join_cohort` derived its scope from the
    /// caller's directory, so an agent in a second repository computed a correct scope and
    /// had it REFUSED as a scope collision — against a cohort it never asked for. This is
    /// the report that started it, driven through the tools rather than the core.
    testTask "WHY — one daemon serves TWO repositories, each with its own conductor seat" {
      do! withDaemon (fun port -> task {
        use! sageFsAgent = connect port
        use! nehemiahAgent = connect port

        // Two real directories under the temp root, so the scopes are genuinely different
        // and neither is the daemon's own working directory.
        let repoA = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cohort-multirepo-a")
        let repoB = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cohort-multirepo-b")
        System.IO.Directory.CreateDirectory repoA |> ignore
        System.IO.Directory.CreateDirectory repoB |> ignore

        // Repo A: the first joiner becomes ITS conductor.
        //
        // The scope's PREFIX is deliberately not asserted. A directory with a `.git` is
        // `repo:` and one without is `named:` — both correct, and which one applies is not
        // what this test is about. What matters is that A and B name DIFFERENT scopes, so the
        // two statuses are compared for difference rather than against a literal.
        let! joinA = joinCohortWithDir sageFsAgent "agent-a" "Implementer" repoA
        joinA |> Expect.stringContains "A's first joiner is A's conductor" "You are the conductor"

        // Repo B: a different connection, a different repository, its OWN conductor seat.
        // Before the fix this was refused as a scope collision against A's cohort.
        let! joinB = joinCohortWithDir nehemiahAgent "agent-b" "Implementer" repoB
        joinB |> Expect.stringContains "B's first joiner is B's OWN conductor" "You are the conductor"

        // And each repository reports ITSELF: the frame a caller reads back is the one for
        // the directory it named, not the daemon's and not its neighbour's. Each status
        // carries its OWN scope's path, which is what makes the two frames distinguishable
        // without asserting a `repo:`/`named:` prefix that depends on whether the directory
        // happens to carry a `.git`.
        let! statusA = getCohortStatusIn sageFsAgent repoA
        statusA |> Expect.stringContains "A reads A's cohort, which holds A's member" "mcp:m-"
        statusA |> Expect.stringContains "and that frame names A's own scope" repoA

        let! statusB = getCohortStatusIn nehemiahAgent repoB
        // NOT `agent-b`: identity is bound to the MCP CONNECTION and displayed as its
        // fingerprint (`mcp:m-<hex>`), not the self-declared `agentName`. Asserting the
        // declared name here tested nothing — the frame never contained it, so the assertion
        // failed for a reason that had nothing to do with which cohort answered.
        statusB |> Expect.stringContains "B reads B's cohort, which holds B's member" "mcp:m-"
        statusB |> Expect.stringContains "and that frame names B's own scope" repoB

        // THE TWO SCOPES ARE DIFFERENT, which is the claim "two repositories, two cohorts"
        // actually makes — a single shared cohort would satisfy every assertion above.
        (statusA = statusB)
        |> Expect.isFalse "the two repositories' frames are not the same frame"

        // THE NEGATIVE, without which the positives above mean nothing: the two cohorts do
        // not see each other. A's member is absent from B's frame and vice versa.
        //
        // It reads the REAL member ids out of each frame rather than the `agentName` each
        // caller declared. The previous form checked for `agent-b` in A's frame — a string
        // that is in NEITHER frame, because identity is the connection's fingerprint. So it
        // passed for a reason unrelated to the cohorts being separate, and would have passed
        // just as happily with ONE shared cohort. A vacuous negative is worse than none.
        let memberIdsOf (status: string) =
          System.Text.RegularExpressions.Regex.Matches(status, @"mcp:m-[0-9a-f]+")
          |> Seq.map (fun m -> m.Value)
          |> Set.ofSeq
        let aMembers = memberIdsOf statusA
        let bMembers = memberIdsOf statusB
        (aMembers.IsEmpty)
        |> Expect.isFalse "A's frame lists at least one member, so the negative below means something"
        (bMembers.IsEmpty)
        |> Expect.isFalse "B's frame lists at least one member, so the negative below means something"
        Set.isSubset bMembers aMembers
        |> Expect.isFalse "and A's frame does NOT list B's member"
        Set.isSubset aMembers bMembers
        |> Expect.isFalse "and B's frame does NOT list A's member"

        do! Task.CompletedTask })
    }

    testTask "join from one identity shows that member as conductor in get_cohort_status" {
      do! withDaemon (fun port -> task {
        use! alice = connect port

        let! joinResult = joinCohort alice "alice" "Implementer"
        joinResult
        |> Expect.stringContains "the first joiner becomes conductor" "You are the conductor"

        let! status = getCohortStatus alice
        status
        |> Expect.stringContains "status should list the joined member as Present" "[Implementer] present"

        status
        |> Expect.stringContains "the conductor line should name a real member, not the unknown placeholder" "Conductor: mcp:"
      })
    }

    testTask "WHY — join_cohort with working_directory binds the member to the matching session (item 13c)" {
      do! withDaemon (fun port -> task {
        use! client = connect port
        let projectFile = System.IO.Path.GetFileName SageFs.Tests.HttpApiIntegrationTests.smokeSampleProject
        let workingDir = SageFs.Tests.HttpApiIntegrationTests.smokeSampleProjectDir

        let! createResult = createProjectSession client projectFile workingDir
        let sessionId = createResult.Split('\n').[0].Trim()
        String.IsNullOrWhiteSpace sessionId
        |> Expect.isFalse "create_project_session should report a session id on its first line"

        let! joinResult = joinCohortWithDir client "checkout-binder" "Implementer" workingDir
        joinResult
        |> Expect.stringContains
          "join_cohort resolves working_directory to the just-created session and reports it"
          (sprintf "Bound to session %s" sessionId)
      })
    }

    testTask "WHY — join_cohort with no resolvable session still joins, but says so honestly (item 13c)" {
      do! withDaemon (fun port -> task {
        use! client = connect port
        // No session exists yet in this fresh, isolated daemon, and no
        // working_directory is given — resolution falls all the way through
        // to Gone, so the member joins with no session bound.
        let! joinResult = joinCohort client "no-session-agent" "Observer"
        joinResult
        |> Expect.stringContains "the member still joins" "Joined cohort as"
        joinResult
        |> Expect.stringContains "an unresolved session is reported honestly, not silently dropped" "No session was resolved"
      })
    }

    testTask "join from a second identity shows two members, second is not conductor" {
      do! withDaemon (fun port -> task {
        use! a = connect port
        use! b = connect port

        let! _ = joinCohort a "member-a" "Implementer"
        let! secondJoin = joinCohort b "member-b" "Verifier"
        secondJoin
        |> Expect.stringContains "the second joiner is a member" "Joined cohort as"
        secondJoin.Contains "You are the conductor"
        |> Expect.isFalse "the second joiner must not become conductor"

        let! status = getCohortStatus a
        status
        |> Expect.stringContains "status should report two members" "Members (2):"
        status
        |> Expect.stringContains "the verifier role should be visible" "[Verifier] present"
      })
    }

    testTask "acquire a claim, status shows it held by the acquirer" {
      do! withDaemon (fun port -> task {
        use! c = connect port

        let! _ = joinCohort c "claimant" "Implementer"
        let! acquireResult = acquireClaim c "claimant" "file:src/Acquired.fs" "editing Acquired.fs"
        acquireResult
        |> Expect.stringContains "acquire_claim should report the new claim" "Acquired claim c-"

        let claimId = claimIdFromAcquireResult acquireResult

        let! status = getCohortStatus c
        status
        |> Expect.stringContains "status should list the acquired claim by id" claimId
        status
        |> Expect.stringContains "status should show the claim held by a real member, not '(none)'" "held-by=mcp:"
      })
    }

    testTask "release by a Verifier is refused before reaching the core (Slice 3 role gate)" {
      do! withDaemon (fun port -> task {
        use! holder = connect port
        use! other = connect port

        let! _ = joinCohort holder "holder" "Implementer"
        let! _ = joinCohort other "bystander" "Verifier"
        let! acquireResult = acquireClaim holder "holder" "file:src/NotHeld2.fs" "editing NotHeld2.fs"
        let claimId = claimIdFromAcquireResult acquireResult

        // A Verifier's `cohortTools` (Affordances.fs, Slice 3) does not
        // include `release_claim` — refused at the authority gate, before
        // `Cohort.decide` ever sees the command (never a NotClaimHolder).
        let! releaseResult = releaseClaim other "bystander" claimId 1L
        releaseResult
        // The gate's real sentence is ToolAuthorityGate.fs's
        // "%s cannot call %s (your role is %s): %s" with the inner reason appended. The
        // assertion used to look for "does not permit it", a string no code has ever
        // produced, so it failed on the CORRECT refusal.
        |> Expect.stringContains "a Verifier's release_claim call must be refused by the role gate" "cannot call release_claim (your role is Verifier)"
      })
    }

    testTask "reassign by a non-conductor is refused before reaching the core (Slice 3 role gate)" {
      do! withDaemon (fun port -> task {
        use! conductor = connect port
        use! nonConductor = connect port

        // conductor joins FIRST — v1's implicit create_cohort semantics
        // (Cohort.fs: the first joiner of an empty cohort becomes conductor).
        let! _ = joinCohort conductor "the-conductor" "Implementer"
        let! _ = joinCohort nonConductor "not-the-conductor" "Verifier"
        let! acquireResult = acquireClaim conductor "the-conductor" "file:src/Reassign.fs" "editing Reassign.fs"
        let claimId = claimIdFromAcquireResult acquireResult

        // Slice 3 (item 11): `reassign_claim` is in NO role's `cohortTools`
        // set except `Conductor` — a caller whose Authority (resolved from
        // the same frame `Cohort.decide` itself reads) is not Conductor is
        // always refused HERE, at the role gate, before `Cohort.decide` runs
        // at all. This makes the core's own `NotConductor` refusal
        // unreachable via this MCP path (it is still exercised directly
        // against `decide` by CohortPropertyTests.fs's property 19) — the
        // role gate is a strictly earlier, friendlier version of the same
        // guarantee, not a bypass of it.
        let! reassignResult = reassignClaim nonConductor "not-the-conductor" claimId "the-conductor"
        reassignResult
        |> Expect.stringContains "a non-conductor's reassign must be refused by the role gate" "cannot call reassign_claim (your role is"
      })
    }
  ]
