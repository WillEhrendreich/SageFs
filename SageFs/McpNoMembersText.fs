namespace SageFs

open SageFs.Features.AutoCompletion
open SageFs.McpMemberKinds
open SageFs.McpNoMembers

/// How each reason a name lists nothing reads. The reasons themselves are `McpNoMembers.NoMembersBecause`.
module McpNoMembersText =

  let nearestSentence (nearest: string list) : string =
    match nearest with
    | [] -> ""
    | names -> sprintf " Nearest names there: %s." (String.concat ", " names)

  let renderNoMembers (qualifiedName: string) (because: NoMembersBecause) : string =
    match because with
    | NoMembersBecause.IsLeaf(kind, parent) ->
      sprintf "%s is a %s, a leaf, so it has no members to list. Its siblings are under %s: call list_members on %s, or reflect over it in the session." qualifiedName (fst (kindNoun kind)) parent parent
    | NoMembersBecause.EmptyContainer(kind, parent) ->
      sprintf "%s is a %s listed by %s, and the compiler lists no members for it. It may be empty, or its members may not be accessible from here." qualifiedName (fst (kindNoun kind)) parent
    | NoMembersBecause.StopsAt(resolvedPrefix, missing, nearest) ->
      sprintf "%s lists no members, and %s lists no member named %s, so the name does not resolve in this session.%s Check the spelling and that its namespace is open or its project loaded; call list_members on %s to see what is there." qualifiedName resolvedPrefix missing (nearestSentence nearest) resolvedPrefix
    | NoMembersBecause.StopsAtCutList(resolvedPrefix, missing, nearest) ->
      sprintf "%s lists no members. %s has more members than one list carries, and %s may be past the cut, so whether the name resolves is not known.%s Call list_members on %s and page through it." qualifiedName resolvedPrefix missing (nearestSentence nearest) resolvedPrefix
    | NoMembersBecause.NoParentToAsk ->
      sprintf "%s lists no members and has no parent to check it against. It may not resolve in this session: check the spelling and that its namespace is open or its project loaded." qualifiedName
    | NoMembersBecause.NothingListed first ->
      sprintf "%s lists no members. Nothing named %s is listed in this session, so nothing under it can resolve: check the spelling and that its namespace is open or its project loaded." qualifiedName first
    | NoMembersBecause.ParentUnavailable(at, reason) ->
      sprintf "%s lists no members, and %s could not be asked either (%s), so whether %s is a leaf is not known." qualifiedName at (CompletionUnavailable.describe reason) qualifiedName
    | NoMembersBecause.ListedWhenAskedAgain ->
      sprintf "%s listed no members, and then listed some when asked again, so the session changed between the two asks. Call list_members again." qualifiedName

  /// A listing that could not be made at all: the reason, and what to do.
  let renderUnavailable (qualifiedName: string) (reason: CompletionUnavailable) : string =
    sprintf "Could not list members of %s: %s." qualifiedName (CompletionUnavailable.describe reason)
