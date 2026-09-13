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

module Integration = SageFs.Tests.TestInfrastructure.Integration

let private repoRoot =
  IO.Path.GetFullPath(IO.Path.Combine(__SOURCE_DIRECTORY__, ".."))

let private sageFsExe = SageFs.Tests.TestInfrastructure.SageFsBinary.path ()

let private reserveLoopbackPort () =
  use listener = new TcpListener(IPAddress.Loopback, 0)
  listener.Start()
  (listener.LocalEndpoint :?> IPEndPoint).Port

/// Spawn an isolated daemon (own port, own SAGEFS_DATA_DIR) and wait for
/// /health to respond. Owned by this test process (--owner-pid/--owner-start,
/// the same ownership fencing HttpApiIntegrationTests.fs uses) plus a --ttl
/// belt-and-braces, so a killed/crashed test process can never orphan it.
let private startIsolatedDaemon () : Task<Process * int> = task {
  let port = reserveLoopbackPort ()
  let psi = ProcessStartInfo()
  psi.FileName <- sageFsExe
  psi.UseShellExecute <- false
  psi.CreateNoWindow <- true
  psi.WorkingDirectory <- repoRoot
  psi.ArgumentList.Add "--mcp-port"
  psi.ArgumentList.Add(string port)
  let self = Process.GetCurrentProcess()
  psi.ArgumentList.Add "--owner-pid"
  psi.ArgumentList.Add(string self.Id)
  psi.ArgumentList.Add "--owner-start"
  psi.ArgumentList.Add(string (self.StartTime.ToUniversalTime().Ticks))
  psi.ArgumentList.Add "--ttl"
  psi.ArgumentList.Add "10m"
  let dataDir = IO.Path.Combine(IO.Path.GetTempPath(), "sagefs-test-cohort", Guid.NewGuid().ToString "N")
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
    failwithf "cohort test daemon failed to start on port %d within 60s" port

  return proc, port
}

let private killDaemon (proc: Process) =
  try
    if not proc.HasExited then
      proc.Kill(entireProcessTree = true)
      proc.WaitForExit 5000 |> ignore
  with _ -> ()
  proc.Dispose()

/// Extract the concatenated text of every TextContentBlock in a tool result
/// — the same shape every SageFsTools member returns (see McpTools.fs's
/// withEcho/withEchoOutcome: always a single text block).
let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> String.concat ""

/// Connect a fresh MCP client — a fresh client is a fresh transport session,
/// which is a fresh bound cohort identity (Mcp.fs's memberIdFor). Returned
/// as IAsyncDisposable so callers can `use!` it.
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

let private getCohortStatus (client: McpClient) =
  callTool client "get_cohort_status" []

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
let private withDaemon (body: int -> Task<unit>) : Task<unit> = task {
  let! proc, port = startIsolatedDaemon ()
  try
    do! body port
  finally
    killDaemon proc
}

[<Tests>]
let cohortMcpToolsTests =
  Integration.hostList "Cohort MCP tools" [

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

    testTask "release by a non-holder Implementer is refused (NotClaimHolder-derived error)" {
      do! withDaemon (fun port -> task {
        use! holder = connect port
        use! other = connect port

        let! _ = joinCohort holder "holder" "Implementer"
        // Slice 3 (item 11): `release_claim` is only in an Implementer's
        // `cohortTools` set (a Verifier is refused earlier, at the role
        // gate — see the dedicated test below), so `other` must itself be
        // an Implementer to reach `Cohort.decide`'s own NotClaimHolder
        // check, which is what this test targets.
        let! _ = joinCohort other "bystander" "Implementer"
        let! acquireResult = acquireClaim holder "holder" "file:src/NotHeld.fs" "editing NotHeld.fs"
        let claimId = claimIdFromAcquireResult acquireResult

        let! releaseResult = releaseClaim other "bystander" claimId 1L
        releaseResult
        |> Expect.stringContains "a non-holder's release must be refused" "does not hold claim"
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
        |> Expect.stringContains "a Verifier's release_claim call must be refused by the role gate" "does not permit it"
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
        |> Expect.stringContains "a non-conductor's reassign must be refused by the role gate" "does not permit it"
      })
    }
  ]
