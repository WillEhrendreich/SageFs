/// Step 3 of docs/plans/markers-on-expecto-cases.md: the source side of Expecto
/// live testing. `place` answers the question the gutter asks: this reflected test
/// leaf, whose runtime name came from the running code, sits on which line of the
/// file we parsed? Step 5 adds `discover` (from the text) and step 6 `categoryOf`.
namespace SageFs.Features.LiveTesting

open TreeSitter

/// What a node in the source IS: the `[<Tests>]` binding, a list, or a case.
[<RequireQualifiedAccess>]
type SourceKind =
  | Binding
  | List
  | Case

/// One name on the way down to a node: a literal the text shows, or an
/// expression whose value only the running code knows.
[<RequireQualifiedAccess>]
type SourceName =
  | Literal of string
  | Computed

/// One binding, list or case found in the text, with the literal names on the way down to it.
type SourceCase =
  { Kind: SourceKind
    FilePath: string
    /// Outermost list first, this node last; empty for the binding itself.
    Path: SourceName list
    Line: int
    Column: int
    /// The span of the node (of the whole binding for a binding): for the I/O
    /// scan, and for choosing the right ancestor when two bindings' paths match.
    EndLine: int
    Body: string }

/// Where a reflected leaf lands: on its own line, on the nearest ancestor whose
/// path the text verifies, or nowhere in this source.
[<RequireQualifiedAccess>]
type CasePlacement =
  | Exact of file: string * line: int
  | NearestAncestor of file: string * line: int * unmatched: string list
  | NotInSource

module ExpectoCases =

  /// A source path matches a reflected path when every literal is equal and
  /// every computed name matches whatever the running code produced there.
  let matchesPath (path: SourceName list) (segments: string list) : bool =
    path.Length = segments.Length
    && List.forall2
      (fun name segment ->
        match name with
        | SourceName.Literal literal -> literal = segment
        | SourceName.Computed -> true)
      path
      segments

  /// The leading run of literal names: the part of the path the text verifies.
  let literalPrefix (path: SourceName list) : SourceName list =
    path
    |> List.takeWhile (fun name ->
      match name with
      | SourceName.Literal _ -> true
      | SourceName.Computed -> false)

  /// The source case a reflected path lands on: the case whose path is literal
  /// end to end when the text verifies one, otherwise the first case whose path
  /// holds a computed name at those segments. No match is nothing — the caller
  /// answers NotInSource, never a guess. `place` and the edge share this one
  /// selection so a mark and its evidence can never disagree about which case
  /// they came from.
  let matchedCase (source: SourceCase array) (segments: string list) : SourceCase option =
    let cases =
      source
      |> Array.filter (fun entry -> entry.Kind = SourceKind.Case && matchesPath entry.Path segments)
    match
      cases
      |> Array.tryFind (fun entry -> (literalPrefix entry.Path |> List.length) = List.length entry.Path)
    with
    | Some entry -> Some entry
    | None -> cases |> Array.tryHead

  /// Where every reflected leaf places. A path that is literal end to end is
  /// Exact on the case's own line. A path holding a computed name falls back to
  /// NearestAncestor: the line of the deepest ancestor whose own path is literal
  /// and whose span holds the case (the binding when the computed name sits at
  /// the top), carrying the computed segments that could not be checked. A leaf
  /// this source does not know is NotInSource, never a guess.
  let place (source: SourceCase array) (reflectedPaths: (string * string list) list) : (string * CasePlacement) list =
    let holdsLine (caseLine: int) (entry: SourceCase) =
      entry.Line <= caseLine && caseLine <= entry.EndLine
    let placeOne (fullName: string, segments: string list) : string * CasePlacement =
      match matchedCase source segments with
      | None ->
        fullName, CasePlacement.NotInSource
      | Some caseEntry ->
        match (literalPrefix caseEntry.Path |> List.length) = List.length caseEntry.Path with
        | true ->
          fullName, CasePlacement.Exact (caseEntry.FilePath, caseEntry.Line)
        | false ->
          let unmatched =
            List.zip caseEntry.Path segments
            |> List.choose (fun (name, segment) ->
              match name with
              | SourceName.Computed -> Some segment
              | SourceName.Literal _ -> None)
          let prefix = literalPrefix caseEntry.Path
          // The deepest literal ancestor first; the binding (Path = []) is the
          // last prefix tried, and the case's own line only if the source has
          // no ancestor entry at all.
          let rec ancestorAt (length: int) : SourceCase option =
            if length < 0 then
              None
            else
              let ancestorPath = List.take length prefix
              let kind =
                match ancestorPath with
                | [] -> SourceKind.Binding
                | _ -> SourceKind.List
              match
                source
                |> Array.tryFind (fun entry ->
                  entry.Kind = kind && entry.Path = ancestorPath && holdsLine caseEntry.Line entry)
              with
              | Some entry -> Some entry
              | None -> ancestorAt (length - 1)
          match ancestorAt (List.length prefix) with
          | Some ancestor ->
            fullName, CasePlacement.NearestAncestor (caseEntry.FilePath, ancestor.Line, unmatched)
          | None ->
            fullName, CasePlacement.NearestAncestor (caseEntry.FilePath, caseEntry.Line, unmatched)
    reflectedPaths |> List.map placeOne

  // ---- step 5: discover -----------------------------------------------------------------

  /// Every named node of a parse tree, in document order.
  let namedNodes (root: Node) : Node list =
    let collected = ResizeArray<Node>()
    let rec walk (node: Node) =
      if node.IsNamed then collected.Add node
      for child in node.Children do walk child
    walk root
    List.ofSeq collected

  /// True when `outer` covers the given source range and is larger than it.
  let spanStrictlyHolds (outer: Node) (inner: Node) : bool =
    let startIndex = int inner.StartIndex
    let endIndex = int inner.EndIndex
    int outer.StartIndex <= startIndex
    && int outer.EndIndex >= endIndex
    && (int outer.StartIndex < startIndex || int outer.EndIndex > endIndex)

  /// True when `outer` covers the given source range at all.
  let spanHolds (outer: Node) (inner: Node) : bool =
    int outer.StartIndex <= int inner.StartIndex && int outer.EndIndex >= int inner.EndIndex

  /// The innermost application that holds a captured builder identifier: the
  /// builder and its name argument, before any body the grammar adds around it.
  let builderApplication (nodes: Node list) (identifier: Node) : Node option =
    nodes
    |> List.filter (fun node ->
      node.Type = "application_expression"
      && spanHolds node identifier)
    |> List.sortBy (fun node -> int node.EndIndex - int node.StartIndex)
    |> List.tryHead

  /// The full expression the builder application sits in: the application with its
  /// arguments, the infix form, or the computation expression. The application
  /// itself when nothing wraps it, so a case is never dropped for lack of a parent.
  let containingExpression (nodes: Node list) (inner: Node) : Node option =
    let larger =
      nodes
      |> List.filter (fun node ->
        (node.Type = "application_expression"
         || node.Type = "infix_expression"
         || node.Type = "ce_expression")
        && spanStrictlyHolds node inner)
      |> List.sortBy (fun node -> int node.EndIndex - int node.StartIndex)
      |> List.tryHead
    match larger with
    | Some expression -> Some expression
    | None -> Some inner

  /// The name argument of a builder: the string literal the text shows, or
  /// Computed for an expression (an interpolated string counts as computed).
  let nameArgument (code: string) (application: Node) : SourceName =
    if application.Children.Count >= 2 then
      let argument = application.Children.[1]
      match argument.Type with
      | "const" ->
        let raw = code.Substring(int argument.StartIndex, int argument.EndIndex - int argument.StartIndex)
        match raw.Length >= 2 && raw.StartsWith "\"" && raw.EndsWith "\"" with
        | true -> SourceName.Literal (raw.Substring(1, raw.Length - 2))
        | false -> SourceName.Computed
      | _ -> SourceName.Computed
    else SourceName.Computed

  /// The `[<Tests>]` bindings, lists and cases of one Expecto file, read straight
  /// from the parse tree: a name the text shows becomes Literal, a name only the
  /// running code can know becomes Computed. Nothing is discovered without the
  /// native library, which is how TestTreeSitter.discover answers too.
  let discover (filePath: string) (code: string) : SourceCase array =
    if System.String.IsNullOrWhiteSpace code then
      Array.empty
    else
      match TestTreeSitter.tryResources () with
      | None -> Array.empty
      | Some (language, query) ->
        use parser = new Parser(language)
        use tree = parser.Parse(code)
        let root = tree.RootNode
        let nodes = namedNodes root
        let captures = query.Execute(root).Captures |> Seq.toList
        let captured (captureName: string) =
          captures
          |> List.filter (fun capture -> capture.Name = captureName)
          |> List.map (fun capture -> capture.Node)
        let pairsOf (identifiers: Node list) =
          identifiers
          |> List.choose (fun identifier ->
            builderApplication nodes identifier
            |> Option.bind (fun application ->
              containingExpression nodes application |> Option.map (fun expression -> application, expression)))

        // Lists outermost first, so every list path exists before a list nested inside it.
        let listPairs =
          pairsOf (captured "expectoList.builder")
          |> List.sortBy (fun (inner, expression) ->
            int inner.StartIndex, -(int expression.EndIndex - int expression.StartIndex))
        let listPaths, _ =
          listPairs
          |> List.mapFold
            (fun known (inner, expression) ->
              let enclosing =
                known
                |> List.filter (fun (node, _) -> spanHolds node expression)
                |> List.sortBy (fun (node, _) -> int node.EndIndex - int node.StartIndex)
              // Every known path already holds its own ancestors, so the innermost
              // enclosing list is the whole chain above this one.
              let parentPath =
                match List.tryHead enclosing with
                | Some (_, path) -> path
                | None -> []
              let path = parentPath @ [ nameArgument code inner ]
              (expression, path), known @ [ (expression, path) ])
            []
        let listEntries =
          listPaths
          |> List.map (fun (expression, path) ->
            { Kind = SourceKind.List
              FilePath = filePath
              Path = path
              Line = int expression.StartPosition.Row + 1
              Column = int expression.StartPosition.Column
              EndLine = int expression.EndPosition.Row + 1
              Body = code.Substring(int expression.StartIndex, int expression.EndIndex - int expression.StartIndex) })

        let caseEntries =
          pairsOf (captured "expectoCase.builder")
          |> List.map (fun (inner, expression) ->
            let enclosing =
              listPaths
              |> List.filter (fun (node, _) -> spanHolds node expression)
              |> List.sortBy (fun (node, _) -> int node.EndIndex - int node.StartIndex)
            let parentPath =
              match List.tryHead enclosing with
              | Some (_, path) -> path
              | None -> []
            { Kind = SourceKind.Case
              FilePath = filePath
              Path = parentPath @ [ nameArgument code inner ]
              Line = int expression.StartPosition.Row + 1
              Column = int expression.StartPosition.Column
              EndLine = int expression.EndPosition.Row + 1
              Body = code.Substring(int expression.StartIndex, int expression.EndIndex - int expression.StartIndex) })

        let bindingEntries =
          TestTreeSitter.discover filePath code
          |> Array.filter (fun location ->
            location.AttributeName = "Tests" || location.AttributeName = "TestsAttribute")
          |> Array.choose (fun location ->
            let nameRow = location.Line - 1
            let declaration =
              nodes
              |> List.filter (fun node ->
                node.Type = "declaration_expression"
                && int node.StartPosition.Row <= nameRow
                && int node.EndPosition.Row >= nameRow)
              |> List.sortBy (fun node -> int node.EndIndex - int node.StartIndex)
              |> List.tryHead
            declaration
            |> Option.map (fun node ->
              { Kind = SourceKind.Binding
                FilePath = filePath
                Path = []
                Line = int node.StartPosition.Row + 1
                Column = int node.StartPosition.Column
                EndLine = int node.EndPosition.Row + 1
                Body = code.Substring(int node.StartIndex, int node.EndIndex - int node.StartIndex) }))

        List.append (List.append (List.ofArray bindingEntries) listEntries) caseEntries
        |> List.sortBy (fun entry -> entry.Line, entry.Column)
        |> List.toArray

  // ---- step 6: categoryOf ----------------------------------------------------------------

  /// The plan's `ExpectoCases.categoryOf` contract. The rules themselves live in
  /// `LiveTestingCategories`: the FSI host compiles the live-testing executors and
  /// its project takes no packages on purpose, so the file holding the TreeSitter
  /// parse surface cannot join that list, and this re-export keeps the daemon-side
  /// API exactly where the plan put it.
  let categoryOf (evidence: CategoryEvidence) : TestCategory * CategoryWhy =
    LiveTestingCategories.categoryOf evidence

  /// Re-exported so the mutation tests pin the single list of category words.
  let categoryWords : (string * TestCategory) list =
    LiveTestingCategories.categoryWords

  /// Re-exported for the same reason: one splitter, one home.
  let categoryEvidenceTokens (text: string) : Set<string> =
    LiveTestingCategories.categoryEvidenceTokens text
