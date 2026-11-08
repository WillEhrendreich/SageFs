namespace SageFs

open System.Threading.Tasks
open SageFs.McpCompletions
open SageFs.McpMembersReply
open SageFs.McpTools

/// The session-routed half of `list_members`: ask the worker for every member of a name, and when it lists
/// nothing ask the parent why. Name paths are the ladder's identity (they survive an edit; a table of integer
/// handles would not), so the reply carries names, the total, and the next offset, and nothing else.
module McpMembersRoute =

  let listMembers
    (ctx: McpContext)
    (agent: string)
    (QualifiedName qualifiedName)
    (offset: int)
    (target: McpSessionRouting.RouteTarget)
    : Task<string> =
    if System.String.IsNullOrWhiteSpace qualifiedName then
      task { return "Give a qualified_name to list members of, e.g. 'SageFs.SessionManager'." }
    else
      let name = qualifiedName.TrimEnd '.'
      withSessionWd ctx agent target (fun sid -> membersReply (membersOf ctx sid) name offset)
