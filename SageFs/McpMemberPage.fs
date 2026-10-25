namespace SageFs

open SageFs.Features.AutoCompletion
open SageFs.McpTools

/// Slice 2 of the progressive map (docs/progressive-map-research-2026-10-24.md section 9): the
/// reply of `list_members` as data first and text second. A page is built once, here, from the
/// full member list, so its totals describe the whole set whatever slice is on screen, and the
/// text is only a rendering of it. Pure, so the bound is testable without a session.
module McpMemberPage =

  /// Whether a client can ask for the members of a name. The same split the VS Code Type Explorer
  /// makes with its `drillableKinds` set (sagefs-vscode/src/TypeExplorerProvider.fs): that file is
  /// Fable and cannot reference this assembly, so `McpMemberPageTests` reads its set and fails
  /// when it stops equalling the kinds this function calls Drillable.
  [<RequireQualifiedAccess>]
  type Rung =
    | Drillable
    | Leaf

  /// Exhaustive over `CompletionKind` on purpose: a new kind breaks the build here until someone
  /// decides which side of the ladder it sits on.
  let rungOf (kind: CompletionKind) : Rung =
    match kind with
    | CompletionKind.Namespace
    | CompletionKind.Module
    | CompletionKind.Class
    | CompletionKind.Struct
    | CompletionKind.Interface
    | CompletionKind.Enum
    | CompletionKind.Union
    | CompletionKind.Type -> Rung.Drillable
    | CompletionKind.Constant
    | CompletionKind.Delegate
    | CompletionKind.EnumMember
    | CompletionKind.Event
    | CompletionKind.Exception
    | CompletionKind.Field
    | CompletionKind.Method
    | CompletionKind.OverriddenMethod
    | CompletionKind.Property
    | CompletionKind.Typedef
    | CompletionKind.Variable
    | CompletionKind.ExtensionMethod
    | CompletionKind.TypeParameter
    | CompletionKind.Keyword
    | CompletionKind.Folder
    | CompletionKind.File -> Rung.Leaf

  /// The worker sends display names only, so the kind of a member is read from the shape of its
  /// name by `completionKindForLabel`, the same reading the Type Explorer's `/api/completions`
  /// reply is built from. A capitalised name is Drillable, which includes a PascalCase method.
  let rungOfName (name: string) : Rung =
    completionKindForLabel name |> rungOf

  let rungLabel (rung: Rung) : string =
    match rung with
    | Rung.Drillable -> "drillable"
    | Rung.Leaf -> "leaf"

  type KindCount = { Rung: Rung; Count: int }

  type Continuation =
    | NextPage of offset: int * remaining: int
    | LastPage

  /// One reply. `Counts` and `Total` cover every member of the name; `Groups` holds only the
  /// slice that fits the cap, drillable names first, so a page boundary never changes a count.
  type MemberPage =
    { Name: string
      Total: int
      First: int
      Last: int
      Counts: KindCount list
      Groups: (Rung * string list) list
      Continuation: Continuation }

  let pageMembers (qualifiedName: string) (maxItems: int) (offset: int) (members: string list) : MemberPage =
    let drillable, leaf =
      members
      |> List.map (fun m -> rungOfName m, m)
      |> List.partition (fun (rung, _) -> rung = Rung.Drillable)
    let ordered = drillable @ leaf
    let total = ordered.Length
    let start = max 0 (min offset total)
    let page = ordered |> List.skip start |> List.truncate maxItems
    let endPos = start + page.Length
    { Name = qualifiedName
      Total = total
      First = (if List.isEmpty page then 0 else start + 1)
      Last = endPos
      Counts =
        [ { Rung = Rung.Drillable; Count = drillable.Length }
          { Rung = Rung.Leaf; Count = leaf.Length } ]
        |> List.filter (fun c -> c.Count > 0)
      Groups =
        page
        |> List.groupBy fst
        |> List.map (fun (rung, tagged) -> rung, List.map snd tagged)
      Continuation =
        (if endPos < total then NextPage(endPos, total - endPos) else LastPage) }

  let render (page: MemberPage) : string =
    let header =
      sprintf "Members of %s: %d total, showing %d-%d." page.Name page.Total page.First page.Last
    let kinds =
      match page.Counts with
      | [] -> "By kind: none."
      | counts ->
        counts
        |> List.map (fun c -> sprintf "%d %s" c.Count (rungLabel c.Rung))
        |> String.concat ", "
        |> sprintf "By kind: %s."
    let body =
      page.Groups
      |> List.collect (fun (rung, names) ->
        sprintf "%s:" (rungLabel rung) :: (names |> List.map (sprintf "  %s")))
    let footer =
      match page.Continuation with
      | NextPage(offset, remaining) -> [ sprintf "(%d more; call again with offset=%d)" remaining offset ]
      | LastPage -> []
    String.concat "\n" ([ header; kinds ] @ body @ footer)
