module SageFs.Tests.CapabilityHttpIntegrationTests

/// The member-token path through a real daemon: a real MCP client over HTTP, the token in the
/// `X-SageFs-Member-Token` header and in `_meta`, the call filter binding it, the role gate
/// refusing, `tools/list` filtered, and revocation cutting the token off. The daemon runs with
/// `SAGEFS_IDENTITY_POLICY=TokenRequired`, so a connection with no token is the control.
///
/// Spawns its own daemon (own port, own data dir) like CohortMcpToolsIntegrationTests, so it runs
/// under --integration-host and not in the default tier.

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Expecto
open Expecto.Flip
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open SageFs
open SageFs.Capability
open SageFs.EdgeCredential

module Integration = SageFs.Tests.TestInfrastructure.Integration

/// The tool's own answer: the first text block. The daemon's event echo is a later block of its own.
let private textOf (result: CallToolResult) : string =
  result.Content
  |> Seq.choose (function :? TextContentBlock as t -> Some t.Text | _ -> None)
  |> Seq.tryHead
  |> Option.defaultValue ""

let private connectWith (port: int) (headers: (string * string) list) : Task<McpClient> =
  let opts = HttpClientTransportOptions(Endpoint = Uri(sprintf "http://localhost:%d/" port))
  opts.AdditionalHeaders <- Dictionary<string, string>(headers |> Seq.map (fun (k, v) -> KeyValuePair(k, v)))
  let transport = HttpClientTransport(opts, (null: Microsoft.Extensions.Logging.ILoggerFactory))
  McpClient.CreateAsync(transport, null, null, CancellationToken.None)

/// One tool call, returning (isError, text). `meta` is the request's `_meta`, the per-call channel.
let private call (client: McpClient) (name: string) (args: (string * obj) list) (meta: (string * string) list) : Task<bool * string> =
  task {
    let request = CallToolRequestParams(Name = name)
    request.Arguments <- Dictionary<string, System.Text.Json.JsonElement>(
      args |> Seq.map (fun (k, v) -> KeyValuePair(k, System.Text.Json.JsonSerializer.SerializeToElement v)))
    match meta with
    | [] -> ()
    | pairs ->
      let node = JsonObject()
      for (k, v) in pairs do node[k] <- JsonValue.Create v
      request.Meta <- node
    let! result = client.CallToolAsync(request, CancellationToken.None)
    return (result.IsError |> Option.ofNullable |> Option.defaultValue false), textOf result
  }

/// A call the edge refuses before any tool runs. A header token the daemon cannot use gets a 403 whose body names
/// the cause (#182), and the client raises on a 403, so the refusal comes back as the exception's message, body included.
let private callRefusedByEdge (client: McpClient) (name: string) (args: (string * obj) list) : Task<string> =
  task {
    try
      let! _, text = call client name args []
      return failtestf "the edge should have refused %s, but it answered: %s" name text
    with :? System.Net.Http.HttpRequestException as refused ->
      return refused.Message
  }

let private listTools (client: McpClient) (meta: (string * string) list) : Task<string list> =
  task {
    let request = ListToolsRequestParams()
    match meta with
    | [] -> ()
    | pairs ->
      let node = JsonObject()
      for (k, v) in pairs do node[k] <- JsonValue.Create v
      request.Meta <- node
    let! result = client.ListToolsAsync(request, CancellationToken.None)
    return result.Tools |> Seq.map (fun t -> t.Name) |> List.ofSeq
  }

/// The directory the spawned daemon runs in, so it is the checkout a token is minted for.
let private daemonRepo = RepoPaths.repoPathFull [||]

let private tokenOf (mintText: string) : string =
  let m = Regex.Match(mintText, @"sfm_[A-Za-z0-9_\-]+")
  match m.Success with
  | true -> m.Value
  | false -> failtestf "mint_member did not show a token: %s" mintText

let private memberIdOf (mintText: string) : string =
  let m = Regex.Match(mintText, @"cap:[0-9a-f]+")
  match m.Success with
  | true -> m.Value
  | false -> failtestf "mint_member did not name the member: %s" mintText

/// A data directory of this test's own, so the test can read the `operator.token` its daemon makes there.
let private freshDataDir () : string =
  Path.Combine(Path.GetTempPath(), "sagefs-test-operator", Guid.NewGuid().ToString "N")

let private operatorCredentialIn (dataDir: string) : string =
  match OperatorStore.readCredential dataDir with
  | Ok credential -> credential
  | Error refusal -> failtestf "the daemon did not make operator.token: %s" (OperatorStore.ReadRefusal.describe refusal)

/// One POST to the mint route with `authorization` as the Authorization header (none for None): the status and the body.
let private postMint (port: int) (authorization: string option) (body: string) : Task<int * string> =
  task {
    use client = new System.Net.Http.HttpClient(Timeout = TestTimeouts.patience)
    use request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, sprintf "http://localhost:%d%s" port McpMintRoute.Path)
    authorization |> Option.iter (fun value -> request.Headers.TryAddWithoutValidation("Authorization", value) |> ignore)
    request.Content <- new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
    use! response = client.SendAsync request
    let! text = response.Content.ReadAsStringAsync()
    return int response.StatusCode, text
  }

let private caseOf (body: string) : string =
  use document = System.Text.Json.JsonDocument.Parse body
  McpMintRoute.textField document.RootElement "case"

/// Every text file under `dataDir`: the daemon's log and everything else it wrote, read while it runs.
let private everythingWrittenIn (dataDir: string) : string =
  Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories)
  |> Array.filter (fun path -> not (path.EndsWith OperatorStore.FileName))
  |> Array.map (fun path ->
    try
      use stream = new FileStream(path, System.IO.FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
      use reader = new StreamReader(stream)
      reader.ReadToEnd()
    with :? IOException -> "")
  |> String.concat "\n"

let private mintBody (role: string) : string =
  Json.serialize Json.snakeCase { McpMintRoute.requestFor role with Scope = "src/Foo/"; TtlMinutes = 30; WorkingDirectory = daemonRepo }

type private CliRun = { ExitCode: int; Out: string; Err: string }

/// Run `sagefs <args>` as a process, with `dataDir` as its data directory.
let private runCli (dataDir: string) (args: string list) : Task<CliRun> =
  task {
    let psi = Diagnostics.ProcessStartInfo(SageFs.Tests.TestInfrastructure.SageFsBinary.path ())
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.WorkingDirectory <- daemonRepo
    psi.Environment["SAGEFS_DATA_DIR"] <- dataDir
    for arg in args do psi.ArgumentList.Add arg
    SageFs.Tests.TestInfrastructure.SpawnEnvironment.apply psi
    use proc = Diagnostics.Process.Start psi
    let out = proc.StandardOutput.ReadToEndAsync()
    let err = proc.StandardError.ReadToEndAsync()
    use bound = new CancellationTokenSource(TestTimeouts.patience)
    do! proc.WaitForExitAsync bound.Token
    let! o = out
    let! e = err
    return { ExitCode = proc.ExitCode; Out = o; Err = e }
  }

[<Tests>]
let tests =
  Integration.hostList "Capability tokens over HTTP" [

    testTask "WHY - a token in the header or _meta is the member, the role gate and tools/list hold, and revoking cuts it off" {
      let! proc, port =
        SageFs.Tests.CohortMcpToolsIntegrationTests.startIsolatedDaemonWith [ CapabilityTransport.policyEnvVar, "TokenRequired" ]
      try
        // The first joiner is the conductor, even though a token is required for everyone else.
        use! gateway = connectWith port []
        let! joinError, joined = call gateway "join_cohort" [ "agentName", box "gateway"; "role", box "Implementer" ] []
        joinError |> Expect.isFalse (sprintf "the conductor joins: %s" joined)
        joined |> Expect.stringContains "and is the conductor" "You are the conductor"

        // A plain connection with no token is refused everything but status.
        use! plain = connectWith port []
        let! plainStatusError, _ = call plain "get_cohort_status" [] []
        plainStatusError |> Expect.isFalse "status stays readable"
        let! plainEvalError, plainEval = call plain "send_fsharp_code" [ "agentName", box "x"; "code", box "1;;" ] []
        plainEvalError |> Expect.isTrue "a token-less connection cannot eval"
        plainEval |> Expect.stringContains "and is told why" "TokenRequired"
        let! plainTools = listTools plain []
        plainTools |> List.contains "send_fsharp_code" |> Expect.isFalse "tools/list hides eval from it"
        plainTools |> List.contains "get_cohort_status" |> Expect.isTrue "but not status"

        // The conductor mints an Analysis token for src/Foo/, bound to the checkout the daemon runs in.
        let! mintError, minted = call gateway "mint_member" [ "agentName", box "gateway"; "role", box "Analysis"; "scope", box "src/Foo/"; "ttl_minutes", box 30; "working_directory", box daemonRepo ] []
        mintError |> Expect.isFalse (sprintf "the conductor mints: %s" minted)
        let token = tokenOf minted
        let memberId = memberIdOf minted

        // The token in the header: a connection that sends it is that member.
        use! viaHeader = connectWith port [ CapabilityTransport.headerName, token ]
        let! headerTools = listTools viaHeader []
        headerTools |> List.contains "diagnose" |> Expect.isTrue "the role's tools are listed"
        headerTools |> List.contains "send_fsharp_code" |> Expect.isFalse "and eval is not"
        let! evalError, evalText = call viaHeader "send_fsharp_code" [ "agentName", box "a"; "code", box "1;;" ] []
        evalError |> Expect.isTrue "an Analysis token cannot eval"
        evalText |> Expect.stringContains "the refusal names the role" "Analysis"
        evalText |> Expect.stringContains "and the tool" "send_fsharp_code"
        // A bound token names the directory of the cohort it reads: omitted, the call would act in the daemon's own.
        let! statusError, status = call viaHeader "get_cohort_status" [ "working_directory", box daemonRepo ] []
        statusError |> Expect.isFalse "it can read the cohort"
        status |> Expect.stringContains "and is a member by its public id" memberId
        status.Contains token |> Expect.isFalse "status never shows the token"

        // The token in _meta, on a connection that has none: per call, and not sticky.
        let! metaEvalError, metaEval = call plain "send_fsharp_code" [ "agentName", box "a"; "code", box "1;;" ] [ CapabilityTransport.metaKey, token ]
        metaEvalError |> Expect.isTrue "refused as the token's role"
        metaEval |> Expect.stringContains "naming Analysis, not TokenRequired" "Analysis"
        let! afterError, after = call plain "send_fsharp_code" [ "agentName", box "a"; "code", box "1;;" ] []
        afterError |> Expect.isTrue "the next call, with no _meta, is a plain connection again"
        after |> Expect.stringContains "refused for want of a token" "TokenRequired"

        // A token that is bad is refused, never downgraded to the connection.
        let! badError, bad = call plain "get_cohort_status" [] [ CapabilityTransport.metaKey, "sfm_not-a-token-anybody-minted" ]
        badError |> Expect.isTrue "an unknown token is an error even for a call that would have been allowed"
        bad |> Expect.stringContains "saying it is not known" "not known"

        // Revoking cuts the token off on the next call, on both channels.
        let! revokeError, revoked = call gateway "revoke_member" [ "agentName", box "gateway"; "member_id", box memberId ] []
        revokeError |> Expect.isFalse (sprintf "the conductor revokes: %s" revoked)
        let! afterRevoke = callRefusedByEdge viaHeader "get_cohort_status" []
        afterRevoke |> Expect.stringContains "the header token is refused with a 403" "403"
        afterRevoke |> Expect.stringContains "as invalid_token" "invalid_token"
        afterRevoke |> Expect.stringContains "naming it as revoked" "revoked"
        let! metaRevokeError, _ = call plain "get_cohort_status" [] [ CapabilityTransport.metaKey, token ]
        metaRevokeError |> Expect.isTrue "and so is the _meta token"
      finally
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
    }

    testTask "WHY - the operator credential mints over HTTP, every other caller is refused with a named reason, and no secret reaches the log or the status" {
      let dataDir = freshDataDir ()
      let! proc, port =
        SageFs.Tests.CohortMcpToolsIntegrationTests.startIsolatedDaemonWith [ "SAGEFS_DATA_DIR", dataDir; CapabilityTransport.policyEnvVar, "TokenRequired" ]
      try
        let operator = operatorCredentialIn dataDir
        OperatorCredential.isWellFormed operator |> Expect.isTrue "the daemon made a well formed credential"
        let body = mintBody "Analysis"

        // A member token from the conductor's own connection, to show a real token is refused on this route.
        use! gateway = connectWith port []
        let! joinError, joined = call gateway "join_cohort" [ "agentName", box "gateway"; "role", box "Implementer" ] []
        joinError |> Expect.isFalse (sprintf "the conductor joins: %s" joined)
        let! mintError, minted = call gateway "mint_member" [ "agentName", box "gateway"; "role", box "Analysis"; "scope", box "src/Foo/"; "ttl_minutes", box 30; "working_directory", box daemonRepo ] []
        mintError |> Expect.isFalse (sprintf "the conductor mints: %s" minted)
        let memberToken = tokenOf minted

        let! noStatus, noBody = postMint port None body
        noStatus |> Expect.equal "no credential is unauthorized" 401
        caseOf noBody |> Expect.equal "named" "no_credential"

        let! junkStatus, junkBody = postMint port (Some "Bearer sfo_short") body
        junkStatus |> Expect.equal "a cut-short credential is forbidden" 403
        caseOf junkBody |> Expect.equal "named" "unreadable_malformed_operator"

        let! memberStatus, memberBody = postMint port (Some(sprintf "Bearer %s" memberToken)) body
        memberStatus |> Expect.equal "a member token is forbidden" 403
        caseOf memberBody |> Expect.equal "named" "member_not_allowed"

        let otherOperator = OperatorCredential.ofEntropy (Array.create OperatorCredential.entropyBytes 7uy)
        let! wrongStatus, wrongBody = postMint port (Some(sprintf "Bearer %s" otherOperator)) body
        wrongStatus |> Expect.equal "another daemon's credential is forbidden" 403
        caseOf wrongBody |> Expect.equal "named" "operator_not_recognized"

        let! okStatus, okBody = postMint port (Some(sprintf "Bearer %s" operator)) body
        okStatus |> Expect.equal (sprintf "the operator mints: %s" okBody) 200
        let reply =
          Json.deserialize<McpMintRoute.MintReply> Json.camelCase okBody
          |> Result.defaultWith (fun failure -> failtestf "the reply could not be read: %A" failure)
        reply.Token |> Expect.stringStarts "a member token" Token.prefix
        reply.MemberId |> Expect.stringStarts "that is a cap: member" "cap:"
        reply.Role |> Expect.equal "with the role asked for" "Analysis"

        // The minted token is that member: sent as the member header it reads the cohort and is named in it.
        use! asMember = connectWith port [ CapabilityTransport.headerName, reply.Token ]
        let! statusError, status = call asMember "get_cohort_status" [ "working_directory", box daemonRepo ] []
        statusError |> Expect.isFalse "the minted token reads the cohort"
        status |> Expect.stringContains "and is that member" reply.MemberId

        // The ledger names the operator as the minter; the status never shows a token.
        let! daemonStatusError, daemonStatus = call gateway "get_daemon_status" [] []
        daemonStatusError |> Expect.isFalse "status reads"
        let statusText : string = daemonStatus
        statusText.Contains(operator: string) |> Expect.isFalse "get_daemon_status never shows the operator credential"
        statusText.Contains(reply.Token: string) |> Expect.isFalse "or a minted token"

        // Nothing the daemon wrote holds either secret.
        let written = everythingWrittenIn dataDir
        written.Contains operator |> Expect.isFalse "the operator credential is in no file but operator.token"
        written.Contains reply.Token |> Expect.isFalse "and the minted token is in no file at all"
      finally
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
    }

    testTask "WHY - under TokenRequired tools/list depends on the credential presented and not on a join, a seat or the repo (#181)" {
      let dataDir = freshDataDir ()
      let! proc, port =
        SageFs.Tests.CohortMcpToolsIntegrationTests.startIsolatedDaemonWith [ "SAGEFS_DATA_DIR", dataDir; CapabilityTransport.policyEnvVar, "TokenRequired" ]
      try
        let operator = operatorCredentialIn dataDir
        let authorization (credential: string) = "Authorization", sprintf "Bearer %s" credential
        let threeTools = [ "get_cohort_status"; "get_daemon_status"; "join_cohort" ]

        // A conductor with no credential, in the daemon's own repo: the list it saw grew from 3 to 72 after it joined.
        use! gateway = connectWith port []
        let! before = listTools gateway []
        before |> List.sort |> Expect.equal "before any join: status and the join tool" threeTools
        let! joinError, joined = call gateway "join_cohort" [ "agentName", box "gateway"; "role", box "Implementer" ] []
        joinError |> Expect.isFalse (sprintf "the conductor joins: %s" joined)
        let! after = listTools gateway []
        after |> List.sort |> Expect.equal "after the join the list is the same three, so a join is not a side effect on the list" threeTools

        // The seat is still judged at the call: the conductor, with no credential, mints by name.
        let! mintError, minted =
          call gateway "mint_member" [ "agentName", box "gateway"; "role", box "Analysis"; "scope", box "src/Foo/"; "ttl_minutes", box 30; "working_directory", box daemonRepo ] []
        mintError |> Expect.isFalse (sprintf "the call gate still admits the seat: %s" minted)
        let token = tokenOf minted

        // Another connection with no credential sees the same three, whoever joined.
        use! stranger = connectWith port []
        let! strangerList = listTools stranger []
        strangerList |> List.sort |> Expect.equal "a second connection sees the same list" threeTools

        // The operator credential lists everything, the minting tool included.
        use! viaOperator = connectWith port [ authorization operator ]
        let! operatorList = listTools viaOperator []
        operatorList |> List.contains "mint_member" |> Expect.isTrue "the operator is listed mint_member"
        operatorList |> List.contains "send_fsharp_code" |> Expect.isTrue "and eval"
        (operatorList.Length > threeTools.Length) |> Expect.isTrue "far more than the three"

        // A member token lists its role, and outranks an operator credential on the same request.
        use! viaMember = connectWith port [ CapabilityTransport.headerName, token ]
        let! memberList = listTools viaMember []
        memberList |> List.contains "diagnose" |> Expect.isTrue "an Analysis token keeps diagnose"
        memberList |> List.contains "send_fsharp_code" |> Expect.isFalse "and loses eval"
        use! both = connectWith port [ CapabilityTransport.headerName, token; authorization operator ]
        let! bothList = listTools both []
        bothList |> Expect.equal "the member token narrows the list even beside the operator credential" memberList

        // An operator-shaped credential that is not this daemon's is refused, never downgraded to nothing.
        let wrong = OperatorCredential.ofEntropy (Array.create OperatorCredential.entropyBytes 9uy)
        use! viaWrong = connectWith port [ authorization wrong ]
        let! wrongList = listTools viaWrong []
        wrongList |> Expect.equal "the one tool that says why" [ "credential_status" ]
        let! refused = callRefusedByEdge viaWrong "get_cohort_status" []
        refused |> Expect.stringContains "a call gets the 403" "403"
        refused |> Expect.stringContains "as invalid_token" "invalid_token"
        refused |> Expect.stringContains "naming the operator credential" "operator credential"

        // Another system's bearer token on the same header is nobody's business here.
        use! viaOther = connectWith port [ authorization "some-other-systems-token" ]
        let! otherList = listTools viaOther []
        otherList |> List.sort |> Expect.equal "it presents nothing, so it sees the three" threeTools
      finally
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
    }

    testTask "WHY - sagefs member mint prints an export line that works, and says what to do when it cannot" {
      let dataDir = freshDataDir ()
      let! proc, port =
        SageFs.Tests.CohortMcpToolsIntegrationTests.startIsolatedDaemonWith [ "SAGEFS_DATA_DIR", dataDir ]
      try
        let! run =
          runCli dataDir
            [ "member"; "mint"; "--mcp-port"; string port; "--role"; "Analysis"; "--scope"; "src/Foo/"; "--ttl"; "30"; "--dir"; daemonRepo; "--print-env"; "SAGEFS_ANALYST_TOKEN" ]
        run.ExitCode |> Expect.equal (sprintf "it succeeds: %s" run.Err) 0
        let exportLine = run.Out.Trim()
        Regex.IsMatch(exportLine, @"^export SAGEFS_ANALYST_TOKEN=sfm_[A-Za-z0-9_\-]+$") |> Expect.isTrue (sprintf "stdout is one export line: %s" run.Out)
        let token = exportLine.Substring("export SAGEFS_ANALYST_TOKEN=".Length)
        run.Err |> Expect.stringContains "the summary on stderr names the member" "cap:"
        run.Err.Contains token |> Expect.isFalse "and holds no token"

        use! asMember = connectWith port [ CapabilityTransport.headerName, token ]
        let! statusError, status = call asMember "get_cohort_status" [ "working_directory", box daemonRepo ] []
        statusError |> Expect.isFalse "the printed token works"
        status |> Expect.stringContains "as a cap: member" "cap:"

        let emptyDir = freshDataDir ()
        Directory.CreateDirectory emptyDir |> ignore
        let! noFile = runCli emptyDir [ "member"; "mint"; "--mcp-port"; string port; "--role"; "Analysis"; "--dir"; daemonRepo ]
        noFile.ExitCode |> Expect.equal "no operator.token fails" 1
        noFile.Err |> Expect.stringContains "and says where the file should be" OperatorStore.FileName
        noFile.Err |> Expect.stringContains "and what to do" "Start the daemon"
        Directory.Delete(emptyDir, true)
      finally
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc

      // A daemon that is not there: a credential on disk, nothing listening on the port.
      let downDir = freshDataDir ()
      Directory.CreateDirectory downDir |> ignore
      File.WriteAllText(OperatorStore.pathIn downDir, OperatorCredential.ofEntropy (Array.create OperatorCredential.entropyBytes 5uy))
      let freePort = fst (SageFs.Tests.TestInfrastructure.TestPorts.reservePair ())
      let! down = runCli downDir [ "member"; "mint"; "--mcp-port"; string freePort; "--role"; "Analysis"; "--dir"; daemonRepo ]
      down.ExitCode |> Expect.equal "a missing daemon fails" 1
      down.Err |> Expect.stringContains "names the port" (string freePort)
      down.Err |> Expect.stringContains "and says how to start one" "Start it with 'sagefs'"
      Directory.Delete(downDir, true)
    }
  ]
