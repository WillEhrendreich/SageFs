namespace SageFs

open SageFs.Features.AutoCompletion
open SageFs.McpMemberKinds
open SageFs.McpNamePath

/// Why a name that lists nothing lists nothing. An empty list is not an answer: each case says what is true
/// and what to do next. The name's path is walked (`McpNamePath.resolve`), and where it stops decides the case.
/// How each case reads is `McpNoMembersText`.
module McpNoMembers =

  [<RequireQualifiedAccess>]
  type NoMembersBecause =
    /// The parent lists the name, and its kind is a leaf: a method, a value, a property. It has no members.
    | IsLeaf of kind: CompletionKind * parent: string
    /// The parent lists the name as something that can hold members, and the compiler lists none for it.
    | EmptyContainer of kind: CompletionKind * parent: string
    /// The path resolves as far as `resolvedPrefix`, which lists every member it has and none is `missing`.
    | StopsAt of resolvedPrefix: string * missing: string * nearest: string list
    /// As `StopsAt`, but the list was cut at the wire ceiling, so the name may be past the cut.
    | StopsAtCutList of resolvedPrefix: string * missing: string * nearest: string list
    /// The name has no dot and lists nothing, so there is no parent to check it against.
    | NoParentToAsk
    /// The first segment of a longer name lists nothing, so nothing under it can resolve.
    | NothingListed of first: string
    /// A list in the walk could not be fetched, so how far the name resolves, and whether it is a leaf, is not known.
    | ParentUnavailable of at: string * reason: CompletionUnavailable
    /// The name listed nothing, and then the walk found members for it: the session changed between the two asks.
    | ListedWhenAskedAgain

  /// The parent of a qualified name and its last segment, when it has both.
  let splitParent (qualifiedName: string) : (string * string) option =
    match qualifiedName.LastIndexOf '.' with
    | -1 -> None
    | at when at = 0 || at = qualifiedName.Length - 1 -> None
    | at -> Some(qualifiedName.Substring(0, at), qualifiedName.Substring(at + 1))

  /// Why `qualifiedName` listed nothing, from where the walk of its path ended.
  let classifyEmpty (qualifiedName: string) (resolution: NamePathResolution) : NoMembersBecause =
    match resolution with
    | NamePathResolution.Resolved kind ->
      match splitParent qualifiedName with
      | None -> NoMembersBecause.NoParentToAsk
      | Some(parent, _) ->
        match rungOf kind with
        | Rung.Leaf -> NoMembersBecause.IsLeaf(kind, parent)
        | Rung.Drillable -> NoMembersBecause.EmptyContainer(kind, parent)
    | NamePathResolution.ResolvedRoot -> NoMembersBecause.ListedWhenAskedAgain
    | NamePathResolution.StopsAt(resolvedPrefix, missing, nearest) -> NoMembersBecause.StopsAt(resolvedPrefix, missing, nearest)
    | NamePathResolution.StopsAtCutList(resolvedPrefix, missing, nearest) -> NoMembersBecause.StopsAtCutList(resolvedPrefix, missing, nearest)
    | NamePathResolution.NothingResolves first ->
      match first = qualifiedName with
      | true -> NoMembersBecause.NoParentToAsk
      | false -> NoMembersBecause.NothingListed first
    | NamePathResolution.Unknown(at, reason) -> NoMembersBecause.ParentUnavailable(at, reason)
