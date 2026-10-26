namespace SageFs

open System
open System.IO
open SageFs.Features

/// Slice 3 of the progressive map (docs/progressive-map-research-2026-10-24.md section 9): the reply of
/// `list_references` as data first and text second. The page is built once from the whole reply, so its totals
/// describe every reference found whatever slice is on screen, and the text is only a rendering of it. The first line of
/// every rendering is the scope, taken from the page and not from a separate argument, so no count can be printed
/// without the scope that produced it. Pure, so the bound is testable without a session.
module McpReferencesPage =

  type Continuation =
    /// More references follow: ask again at this offset.
    | NextPage of offset: int * remaining: int
    | LastPage

  type CountedPage =
    { Name: string
      Scope: CountedScope
      Total: int
      Uses: int
      Definitions: int
      First: int
      Last: int
      /// The directory every file below is shown relative to, empty when the files share none.
      Root: string
      Groups: (string * ReferenceSite list) list
      Unclean: string list
      Continuation: Continuation }

  type Page =
    | Counted of CountedPage
    | Uncounted of name: string * why: ReferenceUnavailable

  /// The longest directory every path sits under, or empty when they share none (or there is one path of no directory).
  let commonDirectory (paths: string list) : string =
    match paths with
    | [] -> ""
    | first :: rest ->
      let dirs = (first :: rest) |> List.map (fun p -> (match Path.GetDirectoryName p with | null -> "" | d -> d).Split('/') |> Array.toList)
      let rec common (a: string list) (b: string list) : string list =
        match a, b with
        | x :: xs, y :: ys when x = y -> x :: common xs ys
        | _ -> []
      dirs |> List.reduce common |> String.concat "/"

  let relativeTo (root: string) (path: string) : string =
    match root with
    | "" -> path
    | _ when path.StartsWith(root + "/", StringComparison.Ordinal) -> path.Substring(root.Length + 1)
    | _ -> path

  let build (name: string) (maxItems: int) (offset: int) (reply: ReferenceReply) : Page =
    match reply with
    | ReferenceReply.NotCounted why -> Page.Uncounted(name, why)
    | ReferenceReply.Counted(scope, sites, unclean) ->
      let total = sites.Length
      let start = max 0 (min offset total)
      let slice = sites |> List.skip start |> List.truncate maxItems
      let endPos = start + slice.Length
      let root = commonDirectory (sites |> List.map (fun s -> s.File))
      Page.Counted
        { Name = name
          Scope = scope
          Total = total
          Uses = sites |> List.filter (fun s -> s.Kind = ReferenceKind.Use) |> List.length
          Definitions = sites |> List.filter (fun s -> s.Kind = ReferenceKind.Definition) |> List.length
          First = (if List.isEmpty slice then 0 else start + 1)
          Last = endPos
          Root = root
          Groups = slice |> List.groupBy (fun s -> s.File)
          Unclean = unclean
          Continuation = (match endPos < total with | true -> NextPage(endPos, total - endPos) | false -> LastPage) }

  /// The first line of every reply.
  let scopeLine (page: Page) : string =
    match page with
    | Page.Counted c -> "Scope: " + ReferenceReply.describeCounted c.Scope + "."
    | Page.Uncounted(_, why) -> "Scope: " + ReferenceReply.describeUnavailable why + "."

  let siteLine (site: ReferenceSite) : string =
    sprintf "  %d:%d %s" site.Line (site.Column + 1) (ReferenceKind.label site.Kind)

  /// What a caller can do about a reply with no count.
  let nextStep (why: ReferenceUnavailable) : string =
    match why with
    | ReferenceUnavailable.NoProjectLoaded ->
      "Create a project session on the project that holds the name (get_available_projects, then create_project_session) and ask again."
    | ReferenceUnavailable.NoProjectChecked _ ->
      "The budget for checking all projects together is SAGEFS_REFERENCES_CHECK_SECONDS; raise it for a larger solution, or load a session on fewer projects. Projects the compiler finished in an earlier ask stay checked."
    | ReferenceUnavailable.SessionUnreachable _ ->
      "Call get_session_status to see whether the session is Ready, then ask again."

  let body (page: Page) : string list =
    match page with
    | Page.Uncounted(name, why) ->
      [ sprintf "No count for %s, so this is not an answer that it has no references. %s" name (nextStep why) ]
    | Page.Counted c ->
      let header =
        match c.Total with
        | 0 ->
          sprintf
            "References to %s: none found in the checked projects. The name must be fully qualified exactly as the compiler spells it; call list_members on its parent to see the names there."
            c.Name
        | _ ->
          sprintf "References to %s: %d total (%d uses, %d definitions), showing %d-%d." c.Name c.Total c.Uses c.Definitions c.First c.Last
      let rooted =
        match c.Root with
        | "" -> []
        | root -> [ sprintf "Files are relative to %s." root ]
      let askAgain =
        match c.Scope with
        | CountedScope.ProjectWide _ -> []
        | CountedScope.SomeProjects _ ->
          [ "Ask again for the rest: projects the compiler has finished stay checked, so a repeat only needs the ones not checked. The budget is SAGEFS_REFERENCES_CHECK_SECONDS." ]
      let uncleanLine =
        match c.Unclean with
        | [] -> []
        | files ->
          [ sprintf "These files did not check cleanly, so a use inside one may be missing: %s." (files |> List.map (relativeTo c.Root) |> String.concat ", ") ]
      let groups =
        c.Groups
        |> List.collect (fun (file, sites) -> sprintf "%s:" (relativeTo c.Root file) :: (sites |> List.map siteLine))
      let footer =
        match c.Continuation with
        | NextPage(offset, remaining) -> [ sprintf "(%d more; call again with offset=%d)" remaining offset ]
        | LastPage -> []
      [ header ] @ askAgain @ rooted @ uncleanLine @ groups @ footer

  let render (page: Page) : string = String.concat "\n" (scopeLine page :: body page)
