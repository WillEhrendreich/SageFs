namespace SageFs

open System
open System.Security.Claims
open System.Threading.Tasks

/// `SessionAccess` for an HTTP request (the stop, stop-app and workflow routes), which has no MCP call to read its caller
/// from. The caller is what the edge already decided about the request: the operator credential it recognized, and the
/// member a token or a stdio bridge's client key named. A request that carries neither (the dashboard, an editor, the
/// CLI) is bound to no member, and no member's session is at stake from it.
module McpHttpAccess =

  /// Whether the edge recognized the operator credential on this request.
  let operatorOf (user: ClaimsPrincipal) : bool =
    match McpCapability.operatorClaimOf user with
    | Some EdgeCredential.OperatorClaim.Recognized -> true
    | Some EdgeCredential.OperatorClaim.Refused
    | None -> false

  /// The member this request is bound to: the token it presented, else its client key. A token that does not resolve binds
  /// nothing here (a call to a tool is refused for it earlier; a route has always treated it as no credential).
  let memberOf (store: CapabilityStore) (now: DateTime) (user: ClaimsPrincipal) : MemberTable.MemberId option =
    match McpCapability.presentToken store now (McpCapability.presentationFrom user null) with
    | Ok(Some capability) -> Some(Capability.memberIdOf capability.Id)
    | Ok None
    | Error _ -> McpCapability.clientKeyMemberOf user

  /// The refusal for `action` on `sessionId` by whoever sent this request, or None when it may go ahead.
  let refusal
    (lookups: McpSessionAccess.Lookups)
    (store: CapabilityStore)
    (user: ClaimsPrincipal)
    (action: SessionAction)
    (sessionId: string)
    : Task<SageFsError option> =
    McpSessionAccess.refusal lookups (operatorOf user) (memberOf store DateTime.UtcNow user) action sessionId
