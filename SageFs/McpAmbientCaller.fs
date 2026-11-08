namespace SageFs

/// Who is calling a tool, as the request filter binds it before the tool body runs. AsyncLocal, not a parameter: it flows
/// through the same await chain `Activity.Current` already rides in this codebase (Instrumentation.fs), so the ~40 MCP tool
/// method signatures (whose only caller-supplied "identity" is `agentName`) never need touching to close the
/// identity-spoofing gap. Moved here from Mcp.fs, which sits at its line budget; `McpTools` re-exposes each of these under
/// its old name, so the request filter and the tests that bind an identity are unchanged.
module McpAmbientCaller =

  /// The MCP transport's per-connection identity, bound by the request filter (McpServer.createServerCaptureFilter) before a
  /// tool body runs, never by the tool's self-declared `agentName` argument. None outside any bound connection (direct
  /// in-process calls, and every Expecto unit test that calls these functions directly rather than through the ASP.NET Core
  /// and MCP SDK pipeline), see `memberIdFor`.
  let currentTransportSessionId = new System.Threading.AsyncLocal<string option>()

  /// The member token this call presented and that resolved, bound by the request filter exactly as
  /// `currentTransportSessionId` is. A token outranks the connection: it is the member, and sub-agents sharing one
  /// connection are as many members as they have tokens. None for a call that presented no token.
  let currentCapability = new System.Threading.AsyncLocal<ResolvedCapability option>()

  /// The member a stdio bridge's client key names (`MemberTable.ClientKey`), bound by the request filter like the two above.
  let currentClientKeyMember = new System.Threading.AsyncLocal<MemberTable.MemberId option>()

  /// What the edge decided about the operator credential on this call, bound by the request filter like the three above.
  /// `Some Recognized` is the person who runs the daemon; None is a call that presented none.
  let currentOperatorClaim = new System.Threading.AsyncLocal<EdgeCredential.OperatorClaim option>()

  /// Resolve the BOUND identity for a self-declared agent name (sagefs-multiagent-vision.md §4.1: "identity is bound to the
  /// connection, not declared"). When a real MCP connection is bound, the ambient transport session id wins: a caller can
  /// never spoof or evict another connection's presence just by choosing the same agentName, because the key is the
  /// connection, not the name. With no bound connection (tests, direct calls), falls back to a Minted identity keyed on the
  /// name itself, whose resolved key renders IDENTICAL to the plain name (MemberTable.MemberId.display), so every existing
  /// name-keyed test and call site keeps working unchanged.
  let memberIdFor (agentName: string) : MemberTable.MemberId =
    // A token IS the member (a bad one never gets here: the request filter refuses it first), then a stdio bridge's
    // client key (the same client across a reconnect or restart), then the session id. Each is a fingerprint, never the handle.
    MemberTable.ConnectionIdentity.resolve
      (currentCapability.Value |> Option.map (fun capability -> Capability.memberIdOf capability.Id))
      currentClientKeyMember.Value currentTransportSessionId.Value agentName

  /// The identity this call is bound to: a token, a stdio bridge's client key or a connection. None for a call bound to none
  /// of them (the dashboard, an editor over plain HTTP, the CLI), which has no member to compare with a session's maker.
  let boundMemberOfCall () : MemberTable.MemberId option =
    match currentCapability.Value, currentClientKeyMember.Value, currentTransportSessionId.Value with
    | None, None, None -> None
    | Some _, _, _
    | None, Some _, _
    | None, None, Some _ -> Some(memberIdFor "mcp")

  /// Whether the daemon recognized the operator credential on this call.
  let operatorOnThisCall () : bool =
    match currentOperatorClaim.Value with
    | Some EdgeCredential.OperatorClaim.Recognized -> true
    | Some EdgeCredential.OperatorClaim.Refused
    | None -> false
