module SageFs.Middleware.CompilationContext

#nowarn "57"

open System.Threading.Tasks
open Fantomas.Core
open Fantomas.FCS.Syntax

// ─────────────────────────────────────────────────────────────────
// Types
// ─────────────────────────────────────────────────────────────────

/// How the editor is sending code for evaluation.
type EvalMode =
  | File
  | Block
  | Auto

module EvalMode =
  let parse (s: string option) =
    match s |> Option.map (fun v -> v.ToLowerInvariant()) with
    | Some "file" -> File
    | Some "block" -> Block
    | _ -> Auto

/// A module or namespace container in a file's hierarchy.
type ModuleContainer = {
  QualifiedName: string
  LeafName: string
  Kind: SynModuleOrNamespaceKind
  IsRecursive: bool
  AccessModifier: string option
  Opens: string list
  Children: ModuleContainer list
  /// (startLine, endLine) pairs for non-open, non-nested declarations
  DeclarationRanges: (int * int) list
}

/// Parsed file structure — cached per filePath + content hash.
type FileStructure = {
  FilePath: string
  Containers: ModuleContainer list
  HasFileLevelModule: bool
}

/// Result of preprocessing — transformed code + mapping for diagnostics.
type PreprocessResult = {
  Code: string
  LineOffset: int
  ColumnOffset: int
  OriginalFilePath: string option
}

/// Session-level compilation context state.
type CompilationState = {
  EvaluatedModules: Set<string>
  /// Cache keyed by filePath, value is (contentHash, FileStructure).
  FileCache: Map<string, string * FileStructure>
}

module CompilationState =
  let empty = { EvaluatedModules = Set.empty; FileCache = Map.empty }
  let stateKey = "compilationContext"

// ─────────────────────────────────────────────────────────────────
// String helpers
// ─────────────────────────────────────────────────────────────────

let normalizeLineEndings (s: string) = s.Replace("\r\n", "\n").Replace("\r", "\n")

/// Compute a fast content hash for cache invalidation.
let contentHash (code: string) =
  use hasher = System.Security.Cryptography.SHA256.Create()
  let bytes = System.Text.Encoding.UTF8.GetBytes(code)
  let hash = hasher.ComputeHash(bytes)
  System.Convert.ToHexString(hash)

let splitLines (code: string) =
  (normalizeLineEndings code).Split('\n')

let indentCode (code: string) =
  splitLines code
  |> Array.map (fun l ->
    match l.Trim() = "" with
    | true -> ""
    | false -> "  " + l)
  |> String.concat "\n"

// ─────────────────────────────────────────────────────────────────
// FCS-powered file structure parsing
// ─────────────────────────────────────────────────────────────────

let accessModifierText (acc: SynAccess option) =
  match acc with
  | Some a when a.IsInternal -> Some "internal"
  | Some a when a.IsPrivate -> Some "private"
  | _ -> None

let extractOpen =
  function
  | SynModuleDecl.Open(target = SynOpenDeclTarget.ModuleOrNamespace(longId = lid)) ->
    lid.LongIdent
    |> List.map _.idText
    |> String.concat "."
    |> sprintf "open %s"
    |> Some
  | _ -> None

let extractDeclRange =
  function
  | SynModuleDecl.Open _ -> None
  | SynModuleDecl.NestedModule _ -> None
  | d -> Some(d.Range.StartLine, d.Range.EndLine)

let rec extractNestedModule (parentQualified: string) (decl: SynModuleDecl) =
  match decl with
  | SynModuleDecl.NestedModule(
      moduleInfo = SynComponentInfo(longId = nestedId; accessibility = acc)
      isRecursive = isRec
      decls = innerDecls
      range = nestedRange) ->
    let nestedName = nestedId |> List.map _.idText |> String.concat "."
    let nestedQualified = sprintf "%s.%s" parentQualified nestedName
    let nestedOpens = innerDecls |> List.choose extractOpen
    let nestedDeclRanges = innerDecls |> List.choose extractDeclRange
    let nestedChildren =
      innerDecls |> List.choose (extractNestedModule nestedQualified)
    Some {
      QualifiedName = nestedQualified
      LeafName = nestedName
      Kind = SynModuleOrNamespaceKind.NamedModule
      IsRecursive = isRec
      AccessModifier = accessModifierText acc
      Opens = nestedOpens
      Children = nestedChildren
      DeclarationRanges = [ (nestedRange.StartLine, nestedRange.EndLine) ] @ nestedDeclRanges
    }
  | _ -> None

let extractContainer (prefix: string) (synMod: SynModuleOrNamespace) =
  let (SynModuleOrNamespace(
        longId = l; decls = decls; kind = kind;
        isRecursive = isRec; accessibility = acc)) = synMod
  let name = l |> List.map _.idText |> String.concat "."
  let qualified =
    match prefix with
    | "" -> name
    | p -> sprintf "%s.%s" p name
  let opens = decls |> List.choose extractOpen
  let declRanges = decls |> List.choose extractDeclRange
  let children = decls |> List.choose (extractNestedModule qualified)
  {
    QualifiedName = qualified
    LeafName = name
    Kind = kind
    IsRecursive = isRec
    AccessModifier = accessModifierText acc
    Opens = opens
    Children = children
    DeclarationRanges = declRanges
  }

/// Parse a file's module/namespace structure using FCS.
let parseFileStructure (filePath: string) (code: string) : Task<FileStructure> = task {
  let! results = CodeFormatter.ParseAsync(false, code) |> Async.StartAsTask
  let res, _ = results.[0]
  match res with
  | ParsedInput.ImplFile(ParsedImplFileInput(contents = contents)) ->
    let containers = contents |> List.map (extractContainer "")
    let hasFileLevelModule =
      containers
      |> List.exists (fun c -> c.Kind = SynModuleOrNamespaceKind.NamedModule)
    return { FilePath = filePath; Containers = containers; HasFileLevelModule = hasFileLevelModule }
  | _ ->
    return { FilePath = filePath; Containers = []; HasFileLevelModule = false }
}

/// Parse with content-hash caching: skips FCS parse when file content hasn't changed.
/// Returns updated FileCache alongside the result.
let parseFileStructureCached
    (filePath: string) (code: string)
    (cache: Map<string, string * FileStructure>)
    : Task<FileStructure * Map<string, string * FileStructure>> = task {
  let hash = contentHash code
  match cache |> Map.tryFind filePath with
  | Some (cachedHash, fs) when cachedHash = hash ->
    return fs, cache
  | _ ->
    let! fs = parseFileStructure filePath code
    return fs, cache |> Map.add filePath (hash, fs)
}

// ─────────────────────────────────────────────────────────────────
// Block location resolution
// ─────────────────────────────────────────────────────────────────

/// Returns true when a container represents a real, named module or namespace
/// that can be used as a wrapping context in FSI.
/// FCS parses code fragments that have no module declaration as an anonymous
/// module named "Tmp" (SynModuleOrNamespaceKind.AnonModule).  These must never
/// reach transformBlock — wrapping in "module Tmp =" always fails in FSI
/// because "Tmp" is not a real module in the session.
let isNamedContainer (c: ModuleContainer) =
  match c.Kind with
  | SynModuleOrNamespaceKind.AnonModule -> false
  | _ -> true

/// Find the full ancestor path (root-first) to the deepest container that
/// contains blockStartLine.  Returns the empty list when the file structure
/// only contains anonymous (Tmp) containers — the caller should pass code
/// through unmodified rather than wrapping in a broken "module Tmp =" context.
///
/// WHY a list instead of option:
///   For a block inside "module Outer > module Inner", FSI requires
///     module Outer =
///       module Inner =
///         <code>
///   "module Outer.Inner =" is NOT valid F# module-binding syntax — only
///   single-identifier names are accepted after "module".  The full path
///   gives transformBlock the ancestor chain it needs to emit proper nesting.
let locateBlock (fs: FileStructure) (blockStartLine: int option)
    : ModuleContainer list =
  // Ignore any structure that only has anonymous FCS placeholder containers.
  // This is the defensive guard against the "module Tmp =" bug: if someone
  // accidentally passes block code to parseFileStructure instead of the full
  // file, locateBlock returns [] rather than producing a broken Tmp wrapper.
  let namedContainers = fs.Containers |> List.filter isNamedContainer
  match namedContainers with
  | [] -> []
  | _ ->

  match blockStartLine with
  | Some startLine ->
    // Walk the tree accumulating the ancestor path.  At each level we check
    // DeclarationRanges; namespace containers don't list child ranges in their
    // own DeclarationRanges, so we always recurse into children regardless.
    //
    // Returns Some path if any container in the tree contains startLine,
    // None otherwise.
    let rec findPath (containers: ModuleContainer list) (acc: ModuleContainer list) : ModuleContainer list option =
      containers |> List.tryPick (fun c ->
        let inRange =
          c.DeclarationRanges
          |> List.exists (fun (s, e) -> startLine >= s && startLine <= e)
        if inRange then
          let pathHere = acc @ [c]
          // See if a child is a deeper (more specific) match.
          match findPath c.Children pathHere with
          | Some deeper -> Some deeper
          | None -> Some pathHere
        else
          // Recurse into children even when this container isn't in range —
          // namespace containers don't register nested-module ranges in their
          // own DeclarationRanges.
          findPath c.Children (acc @ [c]))
    findPath namedContainers [] |> Option.defaultValue []

  | None ->
    // No line hint: fall back to the single unambiguous container.
    match namedContainers with
    | [ single ] ->
      match single.Children with
      | [ onlyChild ] -> [ single; onlyChild ]
      | _ -> [ single ]
    // Multiple top-level containers without line info: ambiguous — pass through
    | _ -> []

// ─────────────────────────────────────────────────────────────────
// Whole-file transformation
// ─────────────────────────────────────────────────────────────────

let transformWholeFile (fs: FileStructure) (code: string) : PreprocessResult =
  match fs.HasFileLevelModule with
  | true ->
    let lines = splitLines code
    let moduleLineIdx =
      lines |> Array.tryFindIndex (fun l ->
        let t = l.Trim()
        t.StartsWith("module ") && not (t.Contains("=")))
    match moduleLineIdx with
    | Some idx ->
      let moduleLine = lines.[idx].Trim()
      let bodyLines = lines.[idx + 1 ..]
      let indentedBody =
        bodyLines
        |> Array.map (fun l ->
          match l.Trim() = "" with
          | true -> ""
          | false -> "  " + l)
      let transformed =
        [| yield moduleLine + " ="
           yield! indentedBody |]
        |> String.concat "\n"
      { Code = transformed
        LineOffset = 0
        ColumnOffset = 2
        OriginalFilePath = Some fs.FilePath }
    | None ->
      { Code = code; LineOffset = 0; ColumnOffset = 0
        OriginalFilePath = Some fs.FilePath }
  | false ->
    let lines = splitLines code
    let nsLineIdx =
      lines |> Array.tryFindIndex (fun l ->
        l.Trim().StartsWith("namespace "))
    match nsLineIdx with
    | Some idx ->
      // Chesterton's fence: a `namespace X` file must NOT be reduced to its
      // bare body. The hot-reload pipeline re-evals the file on every save and
      // relies on Harmony detour name-matching between the startup-captured
      // methods and the re-eval'd ones. If the namespace is stripped, FSI puts
      // the re-eval'd modules at the top level (module Greeting =) while the
      // compiled app's methods live under the namespace
      // (FSI_0043.WebAppFixture.Greeting). The FullName suffix no longer
      // matches, no detour fires, and the running app keeps serving the old
      // closure — the exact P0 hot-reload gap. Instead, convert the namespace
      // to nested modules so re-eval'd module identity matches the compiled
      // app: `namespace A.B.C` → `module A = module B = module C =`.
      let nsName =
        let t = lines.[idx].Trim()
        t.Substring("namespace".Length).Trim()
      let nsParts = nsName.Split('.') |> Array.filter (fun p -> p <> "")
      let bodyLines = lines.[idx + 1 ..]
      let indentedBody =
        bodyLines
        |> Array.map (fun l ->
          match l.Trim() = "" with
          | true -> ""
          | false -> "  " + l)
      let wrapped =
        match nsParts with
        | [||] ->
          // Malformed namespace — pass through the body unmodified.
          bodyLines |> String.concat "\n"
        | _ ->
          // Fold outside-in: each part becomes one `module X =` nesting level.
          nsParts
          |> Array.rev
          |> Array.fold (fun inner part ->
            sprintf "module %s =\n%s" part (indentCode inner)) (indentedBody |> String.concat "\n")
      { Code = wrapped
        LineOffset = -1
        ColumnOffset = 2
        OriginalFilePath = Some fs.FilePath }
    | None ->
      { Code = code; LineOffset = 0; ColumnOffset = 0
        OriginalFilePath = Some fs.FilePath }

// ─────────────────────────────────────────────────────────────────
// Block transformation
// ─────────────────────────────────────────────────────────────────

let transformBlock
    (path: ModuleContainer list)
    (evaluatedModules: Set<string>)
    (code: string)
    : PreprocessResult =

  // Split path into namespace containers (skipped as wrappers) and module containers
  // (each one becomes a `module X =` nesting level).
  let isNamespace (c: ModuleContainer) =
    match c.Kind with
    | SynModuleOrNamespaceKind.DeclaredNamespace
    | SynModuleOrNamespaceKind.GlobalNamespace -> true
    | _ -> false

  let moduleContainers = path |> List.filter (fun c -> not (isNamespace c))

  match moduleContainers with
  | [] ->
    // Only namespace containers in path (or empty path) — emit opens from the
    // deepest container in the path, but no module wrapper.
    let opens =
      path
      |> List.collect (fun c -> c.Opens)
      |> List.distinct
    let wrapper =
      [ yield! opens
        yield code ]
      |> String.concat "\n"
    let linesAdded = opens.Length
    { Code = wrapper
      LineOffset = linesAdded
      ColumnOffset = 0
      OriginalFilePath = None }

  | _ ->
    // The deepest module container drives the `open` and `EvaluatedModules` logic.
    let deepest = moduleContainers |> List.last

    // Emit `open <deepest>` before the outermost wrapper when this module has
    // already been evaluated (FSI requires it to re-enter the module scope).
    let needsOpen = Set.contains deepest.QualifiedName evaluatedModules
    let openLine =
      match needsOpen with
      | true -> [ sprintf "open %s" deepest.QualifiedName ]
      | false -> []

    // Collect all `open` directives from every container in the path.
    let allOpens =
      path
      |> List.collect (fun c -> c.Opens)
      |> List.distinct

    // Build the nested `module X =` wrappers from outermost → innermost.
    // Each wrapper adds 2 spaces of indentation and one header line.
    // e.g. for path [Outer; Inner]:
    //   module Outer =
    //     module Inner =
    //       <indented code>
    let wrappedCode =
      moduleContainers
      |> List.rev   // innermost first so we fold outside-in
      |> List.fold (fun innerCode (c: ModuleContainer) ->
        let indented = indentCode innerCode
        sprintf "module %s =\n%s" c.LeafName indented
      ) code

    // Total lines prepended before the user's code:
    //   open line (0 or 1) + allOpens + one line per nesting level
    let linesAdded = openLine.Length + allOpens.Length + moduleContainers.Length

    // Column offset = 2 spaces per nesting level
    let colOffset = moduleContainers.Length * 2

    let wrapper =
      [ yield! openLine
        yield! allOpens
        yield wrappedCode ]
      |> String.concat "\n"

    { Code = wrapper
      LineOffset = linesAdded
      ColumnOffset = colOffset
      OriginalFilePath = None }

// ─────────────────────────────────────────────────────────────────
// Core preprocessing — the public API
// ─────────────────────────────────────────────────────────────────

/// Preprocess code for FSI evaluation, wrapping in module context as needed.
/// Returns the preprocessed result and updated set of evaluated modules.
let preprocessForFsi
    (fileStructure: FileStructure option)
    (evalMode: EvalMode)
    (blockStartLine: int option)
    (evaluatedModules: Set<string>)
    (code: string)
    : PreprocessResult * Set<string> =

  match fileStructure with
  | None ->
    { Code = code; LineOffset = 0; ColumnOffset = 0; OriginalFilePath = None },
    evaluatedModules

  | Some fs ->
    let isWholeFile =
      match evalMode with
      | File -> true
      | Block -> false
      | Auto ->
        let firstNonBlank =
          splitLines code |> Array.tryFind (fun l -> l.Trim() <> "")
        match firstNonBlank with
        | Some l when l.TrimStart().StartsWith("module ") && not (l.Contains("=")) -> true
        | Some l when l.TrimStart().StartsWith("namespace ") -> true
        | _ -> false

    match isWholeFile with
    | true ->
      let transformed = transformWholeFile fs code
      let updatedModules =
        fs.Containers
        |> List.fold (fun acc c ->
          let withSelf = Set.add c.QualifiedName acc
          c.Children |> List.fold (fun a child -> Set.add child.QualifiedName a) withSelf)
          evaluatedModules
      { transformed with OriginalFilePath = Some fs.FilePath },
      updatedModules

    | false ->
      let path = locateBlock fs blockStartLine
      match path with
      | [] ->
        { Code = code; LineOffset = 0; ColumnOffset = 0
          OriginalFilePath = Some fs.FilePath },
        evaluatedModules
      | _ ->
        let result = transformBlock path evaluatedModules code
        // Track only the deepest module container's qualified name.
        let moduleContainers =
          path |> List.filter (fun c ->
            match c.Kind with
            | SynModuleOrNamespaceKind.DeclaredNamespace
            | SynModuleOrNamespaceKind.GlobalNamespace -> false
            | _ -> true)
        let updatedModules =
          match moduleContainers with
          | [] -> evaluatedModules
          | containers ->
            let deepest = containers |> List.last
            Set.add deepest.QualifiedName evaluatedModules
        { result with OriginalFilePath = Some fs.FilePath },
        updatedModules

// ─────────────────────────────────────────────────────────────────
// Diagnostic line mapping
// ─────────────────────────────────────────────────────────────────

/// Adjust a diagnostic line number by the preprocessing offset.
let mapDiagnosticLine (lineOffset: int) (line: int) = line - lineOffset

/// Adjust a diagnostic column by the preprocessing offset.
let mapDiagnosticColumn (columnOffset: int) (col: int) = max 0 (col - columnOffset)
