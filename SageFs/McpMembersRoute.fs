namespace SageFs

open System.Threading.Tasks
open SageFs.Features.AutoCompletion
open SageFs.McpCompletions
open SageFs.McpListMembers
open SageFs.McpNoMembers
open SageFs.McpTools

/// The session-routed half of `list_members`: ask the worker for every member of a name, and when it lists
/// nothing ask the parent why. Name paths are the ladder's identity (they survive an edit; a table of integer
/// handles would not), so the reply carries names, the total, and the next offset, and nothing else.
module McpMembersRoute =

  /// When a name lists nothing, walk its path to see where it stops resolving. A non-empty or unavailable answer
  /// has nothing to explain, so the walk is not run and `ResolvedRoot` stands for that.
  let walkWhenEmpty (ctx: McpContext) (sid: string) (name: string) (answer: CompletionAnswer<CompletionEntry>) : Task<McpNamePath.NamePathResolution> =
    match answer with
    | CompletionAnswer.Answered([], _) -> McpNamePath.resolve SageFsConfig.NearestNamesMax (membersOf ctx sid) name
    | CompletionAnswer.Answered(_ :: _, _)
    | CompletionAnswer.Unavailable _ -> Task.FromResult McpNamePath.NamePathResolution.ResolvedRoot

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
        let! answer = membersOf ctx sid name
        let! resolution = walkWhenEmpty ctx sid name answer
        return formatListMembers name SageFsConfig.ListMembersMax offset answer resolution
      })
