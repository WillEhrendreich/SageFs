namespace SageFs

open SageFs.Features.AutoCompletion
open SageFs.McpMemberKinds

/// Slice 2 of the progressive map (docs/progressive-map-research-2026-10-24.md section 9): the
/// reply of `list_members` as data first and text second. A page is built once, here, from the
/// full member list, so its counts describe the whole fetched set whatever slice is on screen, and
/// the text is only a rendering of it. Pure, so the bound is testable without a session.
///
/// The total is a `CompletionTotal`: an exact count, or a floor when the worker stopped at its bound.
module McpMemberPage =

  type KindCount = { Kind: CompletionKind; Count: int }

  type Continuation =
    /// More members of the fetched list follow: ask again at this offset.
    | NextPage of offset: int * remaining: int
    /// The page reached the end of a list whose size is exact.
    | LastPage
    /// The page reached the end of what the worker returned, and the set is larger. Narrow by asking a child.
    | CutShort of fetched: int

  /// One reply. `Counts` cover every member that was fetched; `Groups` holds only the slice that fits
  /// the cap, drillable kinds first, so a page boundary never changes a count.
  type MemberPage =
    { Name: string
      Total: CompletionTotal
      Fetched: int
      First: int
      Last: int
      Counts: KindCount list
      Groups: (CompletionKind * string list) list
      Continuation: Continuation }

  /// The order a page lists in: drillable kinds first (the next rung of the walk is one of them), then by
  /// kind, then by name, so the same set pages the same way on every call.
  let sortKey (entry: CompletionEntry) : int * string * string =
    (match rungOf entry.Kind with | LadderRung.Drillable -> 0 | LadderRung.Leaf -> 1), CompletionKind.label entry.Kind, entry.DisplayText

  let pageMembers (qualifiedName: string) (maxItems: int) (offset: int) (total: CompletionTotal) (members: CompletionEntry list) : MemberPage =
    let ordered = members |> List.sortBy sortKey
    let fetched = ordered.Length
    let start = max 0 (min offset fetched)
    let page = ordered |> List.skip start |> List.truncate maxItems
    let endPos = start + page.Length
    { Name = qualifiedName
      Total = total
      Fetched = fetched
      First = (if List.isEmpty page then 0 else start + 1)
      Last = endPos
      Counts = ordered |> List.countBy (fun e -> e.Kind) |> List.map (fun (kind, count) -> { Kind = kind; Count = count })
      Groups =
        page
        |> List.groupBy (fun e -> e.Kind)
        |> List.map (fun (kind, entries) -> kind, entries |> List.map (fun e -> e.DisplayText))
      Continuation =
        (match endPos < fetched, total with
         | true, _ -> NextPage(endPos, fetched - endPos)
         | false, CompletionTotal.Exact _ -> LastPage
         | false, CompletionTotal.AtLeast _ -> CutShort fetched) }

  let render (page: MemberPage) : string =
    let header =
      match page.Total with
      | CompletionTotal.Exact n ->
        sprintf "Members of %s: %d total, showing %d-%d." page.Name n page.First page.Last
      | CompletionTotal.AtLeast n ->
        sprintf "Members of %s: at least %d (the worker stopped listing there, so the real size is not known), showing %d-%d." page.Name n page.First page.Last
    let kinds =
      match page.Counts with
      | [] -> "By kind: none."
      | counts ->
        let phrases = counts |> List.map (fun c -> kindCountPhrase c.Kind c.Count) |> String.concat ", "
        let drillable = counts |> List.sumBy (fun c -> match rungOf c.Kind with | LadderRung.Drillable -> c.Count | LadderRung.Leaf -> 0)
        sprintf "By kind: %s. Drillable: %d of %d." phrases drillable page.Fetched
    let body =
      page.Groups
      |> List.collect (fun (kind, names) ->
        sprintf "%s (%s):" (snd (kindNoun kind)) (rungLabel (rungOf kind)) :: (names |> List.map (sprintf "  %s")))
    let footer =
      match page.Continuation with
      | NextPage(offset, remaining) -> [ sprintf "(%d more; call again with offset=%d)" remaining offset ]
      | LastPage -> []
      | CutShort fetched ->
        [ sprintf "(the list ends at %d members and the set is larger; ask a drillable child's qualified name to go deeper)" fetched ]
    String.concat "\n" ([ header; kinds ] @ body @ footer)
