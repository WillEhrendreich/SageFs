namespace SageFs

open SageFs.Features.AutoCompletion
open SageFs.McpNoMembersText

/// Slice 1 of the progressive map (docs/progressive-map-research-2026-10-24.md section 9): the pure half of
/// `list_members`. The session-routed half is `McpMembersRoute.listMembers`.
module McpListMembers =

  /// The reply for what the worker said, with where the walk of the name ended when it listed nothing: a total that
  /// says whether it is exact, a count per real kind, a cap, a footer naming the offset that fetches the rest,
  /// and a named reason whenever the list is empty. A non-empty answer never reads `resolution`.
  let formatListMembers
    (qualifiedName: string)
    (maxItems: int)
    (offset: int)
    (answer: CompletionAnswer<CompletionEntry>)
    (resolution: McpNamePath.NamePathResolution)
    : string =
    match answer with
    | CompletionAnswer.Unavailable reason -> renderUnavailable qualifiedName reason
    | CompletionAnswer.Answered([], _) -> renderNoMembers qualifiedName (McpNoMembers.classifyEmpty qualifiedName resolution)
    | CompletionAnswer.Answered(members, total) ->
      McpMemberPage.pageMembers qualifiedName maxItems offset total members |> McpMemberPage.render
