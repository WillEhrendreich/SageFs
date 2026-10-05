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
        let! afterRevokeError, afterRevoke = call viaHeader "get_cohort_status" [] []
        afterRevokeError |> Expect.isTrue "the header token is refused"
        afterRevoke |> Expect.stringContains "as revoked" "revoked"
        let! metaRevokeError, _ = call plain "get_cohort_status" [] [ CapabilityTransport.metaKey, token ]
        metaRevokeError |> Expect.isTrue "and so is the _meta token"
      finally
        SageFs.Tests.CohortMcpToolsIntegrationTests.killDaemon proc
    }
  ]
