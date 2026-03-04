module SageFs.Middleware.CompilationContext

#nowarn "57"

open Fantomas.Core
open Fantomas.FCS.Syntax

// ─────────────────────────────────────────────────────────────────
// Types
// ─────────────────────────────────────────────────────────────────

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
let parseFileStructure (filePath: string) (code: string) : FileStructure =
  let results = CodeFormatter.ParseAsync(false, code) |> Async.RunSynchronously
  let res, _ = results.[0]
  match res with
  | ParsedInput.ImplFile(ParsedImplFileInput(contents = contents)) ->
    let containers = contents |> List.map (extractContainer "")
    let hasFileLevelModule =
      containers
      |> List.exists (fun c -> c.Kind = SynModuleOrNamespaceKind.NamedModule)
    { FilePath = filePath; Containers = containers; HasFileLevelModule = hasFileLevelModule }
  | _ ->
    { FilePath = filePath; Containers = []; HasFileLevelModule = false }

/// Parse with content-hash caching: skips FCS parse when file content hasn't changed.
/// Returns updated FileCache alongside the result.
let parseFileStructureCached
    (filePath: string) (code: string)
    (cache: Map<string, string * FileStructure>)
    : FileStructure * Map<string, string * FileStructure> =
  let hash = contentHash code
  match cache |> Map.tryFind filePath with
  | Some (cachedHash, fs) when cachedHash = hash ->
    fs, cache
  | _ ->
    let fs = parseFileStructure filePath code
    fs, cache |> Map.add filePath (hash, fs)

// ─────────────────────────────────────────────────────────────────
// Block location resolution
// ─────────────────────────────────────────────────────────────────

/// Find the deepest container whose declaration range includes blockStartLine.
let locateBlock (fs: FileStructure) (blockStartLine: int option) (_code: string)
    : ModuleContainer option =
  match blockStartLine with
  | Some startLine ->
    let rec findDeepest (containers: ModuleContainer list) =
      containers |> List.tryPick (fun c ->
        let inRange =
          c.DeclarationRanges
          |> List.exists (fun (s, e) -> startLine >= s && startLine <= e)
        match inRange with
        | true ->
          match findDeepest c.Children with
          | Some child -> Some child
          | None -> Some c
        | false ->
          // always check children — namespace containers don't list
          // nested module ranges in their own DeclarationRanges
          findDeepest c.Children)
    findDeepest fs.Containers
  | None ->
    match fs.Containers with
    | [ single ] ->
      match single.Children with
      | [ onlyChild ] -> Some onlyChild
      | _ -> Some single
    | _ -> fs.Containers |> List.tryHead

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
      let body = lines.[idx + 1 ..] |> String.concat "\n"
      { Code = body
        LineOffset = -1
        ColumnOffset = 0
        OriginalFilePath = Some fs.FilePath }
    | None ->
      { Code = code; LineOffset = 0; ColumnOffset = 0
        OriginalFilePath = Some fs.FilePath }

// ─────────────────────────────────────────────────────────────────
// Block transformation
// ─────────────────────────────────────────────────────────────────

let transformBlock
    (container: ModuleContainer)
    (evaluatedModules: Set<string>)
    (code: string)
    : PreprocessResult =
  let needsOpen = Set.contains container.QualifiedName evaluatedModules
  let openLine =
    match needsOpen with
    | true -> [ sprintf "open %s" container.QualifiedName ]
    | false -> []
  let indentedCode = indentCode code
  let wrapper =
    [ yield! openLine
      yield! container.Opens
      yield sprintf "module %s =" container.QualifiedName
      yield indentedCode ]
    |> String.concat "\n"
  let linesAdded = openLine.Length + container.Opens.Length + 1
  { Code = wrapper
    LineOffset = linesAdded
    ColumnOffset = 2
    OriginalFilePath = None }

// ─────────────────────────────────────────────────────────────────
// Core preprocessing — the public API
// ─────────────────────────────────────────────────────────────────

/// Preprocess code for FSI evaluation, wrapping in module context as needed.
/// Returns the preprocessed result and updated set of evaluated modules.
let preprocessForFsi
    (fileStructure: FileStructure option)
    (evalMode: string option)
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
      | Some "file" -> true
      | Some "block" -> false
      | _ ->
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
      let container = locateBlock fs blockStartLine code
      match container with
      | None ->
        { Code = code; LineOffset = 0; ColumnOffset = 0
          OriginalFilePath = Some fs.FilePath },
        evaluatedModules
      | Some c ->
        let result = transformBlock c evaluatedModules code
        { result with OriginalFilePath = Some fs.FilePath },
        Set.add c.QualifiedName evaluatedModules

// ─────────────────────────────────────────────────────────────────
// Diagnostic line mapping
// ─────────────────────────────────────────────────────────────────

/// Adjust a diagnostic line number by the preprocessing offset.
let mapDiagnosticLine (lineOffset: int) (line: int) = line - lineOffset

/// Adjust a diagnostic column by the preprocessing offset.
let mapDiagnosticColumn (columnOffset: int) (col: int) = max 0 (col - columnOffset)
