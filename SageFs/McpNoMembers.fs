namespace SageFs

open SageFs.Features.AutoCompletion
open SageFs.McpMemberKinds

/// Why a name that lists nothing lists nothing. An empty list is not an answer: each case says what is true
/// and what to do next. The parent of the name is asked once, and what it says decides the case.
module McpNoMembers =

  [<RequireQualifiedAccess>]
  type NoMembersBecause =
    /// The parent lists the name, and its kind is a leaf: a method, a value, a property. It has no members.
    | IsLeaf of kind: CompletionKind * parent: string
    /// The parent lists the name as something that can hold members, and the compiler lists none for it.
    | EmptyContainer of kind: CompletionKind * parent: string
    /// The parent was asked and does not list the name, so the name does not resolve from there.
    | NotListedByParent of parent: string
    /// The name has no dot, so there is no parent to check it against.
    | NoParentToAsk
    /// The parent could not be asked, so whether the name is a leaf is not known.
    | ParentUnavailable of parent: string * reason: CompletionUnavailable

  /// What asking the parent of an empty name found.
  [<RequireQualifiedAccess>]
  type ParentProbe =
    | Listed of parent: string * entries: CompletionEntry list
    | Unavailable of parent: string * reason: CompletionUnavailable
    | NoParent

  /// The parent of a qualified name and its last segment, when it has both.
  let splitParent (qualifiedName: string) : (string * string) option =
    match qualifiedName.LastIndexOf '.' with
    | -1 -> None
    | at when at = 0 || at = qualifiedName.Length - 1 -> None
    | at -> Some(qualifiedName.Substring(0, at), qualifiedName.Substring(at + 1))

  let classifyEmpty (qualifiedName: string) (probe: ParentProbe) : NoMembersBecause =
    match probe with
    | ParentProbe.NoParent -> NoMembersBecause.NoParentToAsk
    | ParentProbe.Unavailable(parent, reason) -> NoMembersBecause.ParentUnavailable(parent, reason)
    | ParentProbe.Listed(parent, entries) ->
      let leaf = qualifiedName.Substring(qualifiedName.LastIndexOf '.' + 1)
      match entries |> List.tryFind (fun e -> e.DisplayText = leaf) with
      | None -> NoMembersBecause.NotListedByParent parent
      | Some entry ->
        match rungOf entry.Kind with
        | Rung.Leaf -> NoMembersBecause.IsLeaf(entry.Kind, parent)
        | Rung.Drillable -> NoMembersBecause.EmptyContainer(entry.Kind, parent)

  let renderNoMembers (qualifiedName: string) (because: NoMembersBecause) : string =
    match because with
    | NoMembersBecause.IsLeaf(kind, parent) ->
      sprintf "%s is a %s, a leaf, so it has no members to list. Its siblings are under %s: call list_members on %s, or reflect over it in the session." qualifiedName (fst (kindNoun kind)) parent parent
    | NoMembersBecause.EmptyContainer(kind, parent) ->
      sprintf "%s is a %s listed by %s, and the compiler lists no members for it. It may be empty, or its members may not be accessible from here." qualifiedName (fst (kindNoun kind)) parent
    | NoMembersBecause.NotListedByParent parent ->
      sprintf "%s lists no members, and %s does not list it either, so the name does not resolve in this session. Check the spelling and that its namespace is open or its project loaded; call list_members on %s to see what is there." qualifiedName parent parent
    | NoMembersBecause.NoParentToAsk ->
      sprintf "%s lists no members and has no parent to check it against. It may not resolve in this session: check the spelling and that its namespace is open or its project loaded." qualifiedName
    | NoMembersBecause.ParentUnavailable(parent, reason) ->
      sprintf "%s lists no members, and its parent %s could not be asked either (%s), so whether %s is a leaf is not known." qualifiedName parent (CompletionUnavailable.describe reason) qualifiedName

  /// A listing that could not be made at all: the reason, and what to do.
  let renderUnavailable (qualifiedName: string) (reason: CompletionUnavailable) : string =
    sprintf "Could not list members of %s: %s." qualifiedName (CompletionUnavailable.describe reason)

