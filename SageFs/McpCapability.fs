namespace SageFs

open System
open System.Threading.Tasks
open SageFs.McpTools
open SageFs.Capability

/// RED STUB. The conductor-only tools that mint and revoke per-run member
/// tokens, and the list filter that hides what a token cannot call.
module McpCapability =

  /// What `mint_member` hands back: the token itself, shown once, and the record behind it.
  type MintedMember = {
    Token: string
    Record: CapabilityRecord
    Session: string option
  }

  let mintMember
    (ctx: McpContext)
    (store: CapabilityStore)
    (now: DateTime)
    (agentName: string)
    (role: string)
    (scope: string)
    (ttlMinutes: int)
    (workingDirectory: string option)
    : Task<Result<MintedMember, SageFsError>> =
    raise (NotImplementedException "RED: mintMember is not implemented yet")

  let revokeMember
    (ctx: McpContext)
    (store: CapabilityStore)
    (now: DateTime)
    (agentName: string)
    (memberText: string)
    : Task<Result<string, SageFsError>> =
    raise (NotImplementedException "RED: revokeMember is not implemented yet")

  /// The tool names `tools/list` should show this caller.
  let visibleToolNames
    (policy: IdentityPolicy)
    (authority: Cohort.Authority<MemberTable.MemberId>)
    (presented: ResolvedCapability option)
    (registered: string list)
    : string list =
    raise (NotImplementedException "RED: visibleToolNames is not implemented yet")

  /// The principal the HTTP edge builds from the member-token header's value: a claim holding
  /// the token's HASH (never the token), on an identity that is not authenticated and names no
  /// user, so the SDK's session-to-user binding is neither engaged nor broken.
  let principalOfHeader (headerValue: string) : System.Security.Claims.ClaimsPrincipal =
    raise (NotImplementedException "RED: principalOfHeader is not implemented yet")

  /// Which token a request presented: `_meta` (per call) before the header claim (per connection).
  let presentationFrom (user: System.Security.Claims.ClaimsPrincipal) (meta: System.Text.Json.Nodes.JsonObject) : CapabilityTransport.Presentation =
    raise (NotImplementedException "RED: presentationFrom is not implemented yet")

  /// Resolve what a call presented. A token that is presented and bad is an error, never a fall-back to the connection.
  let presentToken (store: CapabilityStore) (now: DateTime) (presentation: CapabilityTransport.Presentation) : Result<ResolvedCapability option, SageFsError> =
    raise (NotImplementedException "RED: presentToken is not implemented yet")

  /// The text the conductor reads once: the token, who it is for, what it may do, and how to present it.
  let describeMinted (minted: MintedMember) : string =
    raise (NotImplementedException "RED: describeMinted is not implemented yet")

  /// The same text with the token replaced, for the daemon's own log.
  let describeMintedForLog (minted: MintedMember) : string =
    raise (NotImplementedException "RED: describeMintedForLog is not implemented yet")
