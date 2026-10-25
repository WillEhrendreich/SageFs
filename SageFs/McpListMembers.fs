namespace SageFs

open System.Threading.Tasks
open SageFs.McpTools

/// Slice 1 of the progressive map (docs/progressive-map-research-2026-10-24.md section 9), its
/// own file because `SageFs/Mcp.fs` sits at its file-size budget and the ratchet asks for a split
/// rather than a raise (same answer as the SessionManager split in 0.6.928).
module McpListMembers =

  /// The members of a qualified name as ONE bounded rung. The engine is the same GetCompletions
  /// route exploreQualifiedName uses; what this adds is the contract: a total, a count per kind, a
  /// cap, and a footer naming the offset that fetches the rest, so one reply can never become the
  /// map. The page itself is built in McpMemberPage.
  let formatListMembers (qualifiedName: string) (maxItems: int) (offset: int) (members: string list) : string =
    McpMemberPage.pageMembers qualifiedName maxItems offset members |> McpMemberPage.render

  /// The session-routed half of the rung. Name paths are the ladder's identity (they survive an
  /// edit; a table of integer handles would not), so the reply carries names, the total, and the
  /// next offset, and nothing else.
  let listMembers
    (ctx: McpContext)
    (agent: string)
    (qualifiedName: string)
    (offset: int)
    (workingDirectory: string option)
    : Task<string> =
    if System.String.IsNullOrWhiteSpace qualifiedName then
      task { return "Give a qualified_name to list members of, e.g. 'SageFs.SessionManager'." }
    else
      let name = qualifiedName.TrimEnd '.'
      withSessionWd ctx agent workingDirectory (fun sid -> task {
        let code = sprintf "%s." name
        let! routeResult =
          routeToSession ctx sid
            (fun replyId -> WorkerProtocol.WorkerMessage.GetCompletions(code, code.Length, WorkerProtocol.SessionId.value replyId))
        return
          match routeResult with
          | Ok (WorkerProtocol.WorkerResponse.CompletionResult(_, members)) ->
            formatListMembers name SageFsConfig.ListMembersMax offset members
          | Ok other -> sprintf "Unexpected response: %A" other
          | Error msg -> sprintf "Error: %s" (McpRouteError.routeErrorMessage msg)
      })
