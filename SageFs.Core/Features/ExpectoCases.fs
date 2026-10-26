/// Step 3 of docs/plans/markers-on-expecto-cases.md: the source side of Expecto
/// live testing. `place` answers the question the gutter asks: this reflected test
/// leaf, whose runtime name came from the running code, sits on which line of the
/// file we parsed? Step 5 adds `discover` (from the text) and step 6 `categoryOf`.
namespace SageFs.Features.LiveTesting

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
      let cases =
        source
        |> Array.filter (fun entry -> entry.Kind = SourceKind.Case && matchesPath entry.Path segments)
      let exact =
        cases
        |> Array.tryFind (fun entry -> (literalPrefix entry.Path |> List.length) = List.length entry.Path)
      match exact with
      | Some entry ->
        fullName, CasePlacement.Exact (entry.FilePath, entry.Line)
      | None ->
        match cases |> Array.tryHead with
        | None ->
          fullName, CasePlacement.NotInSource
        | Some caseEntry ->
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
