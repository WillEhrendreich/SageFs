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

  /// When a name lists nothing, ask its parent whether it knows the name and what kind it is.
  let probeParent (ctx: McpContext) (sid: string) (name: string) : Task<ParentProbe> =
    task {
      match splitParent name with
      | None -> return ParentProbe.NoParent
      | Some(parent, _) ->
        let! parentAnswer = membersOf ctx sid parent
        return
          match parentAnswer with
          | CompletionAnswer.Answered(entries, _) -> ParentProbe.Listed(parent, entries)
          | CompletionAnswer.Unavailable reason -> ParentProbe.Unavailable(parent, reason)
    }

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
        let! probe =
          match answer with
          | CompletionAnswer.Answered([], _) -> probeParent ctx sid name
          | CompletionAnswer.Answered(_ :: _, _)
          | CompletionAnswer.Unavailable _ -> Task.FromResult ParentProbe.NoParent
        return formatListMembers name SageFsConfig.ListMembersMax offset answer probe
      })
