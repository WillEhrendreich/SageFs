namespace SageFs

open System.Threading.Tasks
open SageFs.Features.AutoCompletion
open SageFs.McpListMembers

/// The whole of `list_members` once a session is chosen, with the way to ask for members as an argument, so the
/// outcome tests run the code the tool runs over a fake session.
module McpMembersReply =

  /// The reply for a name. When the name lists nothing, its path is walked to see where it stops resolving; a
  /// non-empty or unavailable answer has nothing to explain, so the walk is not run and `ResolvedRoot` stands for that.
  let membersReply (askMembers: string -> Task<CompletionAnswer<CompletionEntry>>) (name: string) (offset: int) : Task<string> =
    task {
      let! answer = askMembers name
      let! resolution =
        match answer with
        | CompletionAnswer.Answered([], _) -> McpNamePath.resolve SageFsConfig.NearestNamesMax askMembers name
        | CompletionAnswer.Answered(_ :: _, _)
        | CompletionAnswer.Unavailable _ -> Task.FromResult McpNamePath.NamePathResolution.ResolvedRoot
      return formatListMembers name SageFsConfig.ListMembersMax offset answer resolution
    }
