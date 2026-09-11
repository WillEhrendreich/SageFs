/// Decides what a save to a running app's source file needs: patch the changed
/// functions in place, or rebuild and restart because something only takes
/// effect at startup (a type, a module-level value, the entry point) changed.
module SageFs.Features.ReloadPlanning

open System
open Fantomas.FCS.Syntax
open Fantomas.FCS.Text

[<RequireQualifiedAccess>]
type DeclKind =
  | TypeDecl
  | ValueDecl
  | FunctionDecl
  | EntryPointDecl
  | NestedModuleDecl
  | StartupCode

/// One top-level declaration of a file, with its exact source text.
type SourceDecl = {
  Name: string
  Kind: DeclKind
  /// For functions, the text before `=`: the part callers were compiled against.
  Header: string
  Text: string
  StartLine: int
  EndLine: int
}

type FileDecls = {
  ModulePath: string list
  Opens: string list
  Decls: SourceDecl list
}

[<RequireQualifiedAccess>]
type ReloadChange =
  | TypeChanged of name: string
  | ValueChanged of name: string
  | SignatureChanged of name: string
  | EntryPointChanged
  | ModuleChanged of name: string
  | StartupCodeChanged
  | DeclarationRemoved of name: string

[<RequireQualifiedAccess>]
type ReloadPlan =
  | PatchFunctions of changed: SourceDecl list
  | RestartRequired of first: ReloadChange * rest: ReloadChange list

/// A binding whose head takes arguments compiles to a method; anything else is a value.
let isFunctionHead (pat: SynPat) =
  match pat with
  | SynPat.LongIdent(argPats = SynArgPats.Pats (_ :: _)) -> true
  | SynPat.LongIdent(argPats = SynArgPats.NamePatPairs(pats = _ :: _)) -> true
  | _ -> false

let private sourceLines (source: string) =
  source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')

/// Source text between two positions (1-based lines, 0-based columns).
let private slice (lines: string array) (startLine: int, startCol: int) (endLine: int, endCol: int) =
  let line i = lines.[max 0 (min (lines.Length - 1) (i - 1))]
  let cut (s: string) (from: int) (upTo: int) =
    let a = max 0 (min s.Length from)
    let b = max a (min s.Length upTo)
    s.Substring(a, b - a)
  match startLine = endLine with
  | true -> cut (line startLine) startCol endCol
  | false ->
    [ yield cut (line startLine) startCol Int32.MaxValue
      for i in startLine + 1 .. endLine - 1 -> line i
      yield cut (line endLine) 0 endCol ]
    |> String.concat "\n"

let private rangeText (lines: string array) (r: range) =
  slice lines (r.StartLine, r.StartColumn) (r.EndLine, r.EndColumn)

let private identText (ids: Ident list) = ids |> List.map _.idText |> String.concat "."

let rec private patName (lines: string array) (pat: SynPat) =
  match pat with
  | SynPat.LongIdent(longDotId = SynLongIdent(id = ids)) -> identText ids
  | SynPat.Named(ident = SynIdent(ident, _)) -> ident.idText
  | SynPat.Typed(pat = inner) -> patName lines inner
  | SynPat.Paren(pat = inner) -> patName lines inner
  | SynPat.Attrib(pat = inner) -> patName lines inner
  | other -> rangeText lines other.Range

let private isEntryPoint (attributes: SynAttributes) =
  attributes
  |> List.collect _.Attributes
  |> List.exists (fun a ->
    match a.TypeName.LongIdent |> List.tryLast with
    | Some id -> id.idText = "EntryPoint" || id.idText = "EntryPointAttribute"
    | None -> false)

let private bindingDecl (lines: string array) (binding: SynBinding) : SourceDecl =
  let (SynBinding(attributes = attributes; headPat = pat; trivia = trivia)) = binding
  let keyword = trivia.LeadingKeyword.Range
  let start =
    match attributes with
    | first :: _ -> first.Range.Start
    | [] -> keyword.Start
  let whole = binding.RangeOfBindingWithRhs
  let header =
    match trivia.EqualsRange with
    | Some eq -> slice lines (keyword.StartLine, keyword.StartColumn) (eq.StartLine, eq.StartColumn)
    | None -> rangeText lines whole
  let kind =
    match isEntryPoint attributes, isFunctionHead pat with
    | true, _ -> DeclKind.EntryPointDecl
    | false, true -> DeclKind.FunctionDecl
    | false, false -> DeclKind.ValueDecl
  { Name = patName lines pat
    Kind = kind
    Header = header.Trim()
    Text = slice lines (start.Line, start.Column) (whole.EndLine, whole.EndColumn)
    StartLine = start.Line
    EndLine = whole.EndLine }

let private simpleDecl (lines: string array) (name: string) (kind: DeclKind) (r: range) : SourceDecl =
  { Name = name
    Kind = kind
    Header = ""
    Text = rangeText lines r
    StartLine = r.StartLine
    EndLine = r.EndLine }

let private exceptionName (text: string) =
  match text.Split([| ' '; '\n'; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList with
  | "exception" :: name :: _ -> name
  | _ -> text

let private declsOf (lines: string array) (decls: SynModuleDecl list) : string list * SourceDecl list =
  let opens, found, _ =
    decls
    |> List.fold (fun (opens, found, startups) decl ->
      match decl with
      | SynModuleDecl.Open(target = SynOpenDeclTarget.ModuleOrNamespace(longId = SynLongIdent(id = ids))) ->
        opens @ [ identText ids ], found, startups
      | SynModuleDecl.Open(target = target) ->
        opens @ [ rangeText lines target.Range ], found, startups
      | SynModuleDecl.Let(bindings = bindings) ->
        opens, found @ (bindings |> List.map (bindingDecl lines)), startups
      | SynModuleDecl.Types(typeDefns = defns) ->
        let types =
          defns
          |> List.map (fun (SynTypeDefn(typeInfo = SynComponentInfo(longId = ids)) as defn) ->
            simpleDecl lines (identText ids) DeclKind.TypeDecl defn.Range)
        opens, found @ types, startups
      | SynModuleDecl.Exception(range = r) ->
        opens, found @ [ simpleDecl lines (exceptionName (rangeText lines r)) DeclKind.TypeDecl r ], startups
      | SynModuleDecl.NestedModule(moduleInfo = SynComponentInfo(longId = ids); range = r) ->
        opens, found @ [ simpleDecl lines (identText ids) DeclKind.NestedModuleDecl r ], startups
      | SynModuleDecl.ModuleAbbrev(ident = ident; range = r) ->
        opens, found @ [ simpleDecl lines ident.idText DeclKind.NestedModuleDecl r ], startups
      | SynModuleDecl.Expr(range = r) ->
        let name = sprintf "startup#%d" (startups + 1)
        opens, found @ [ simpleDecl lines name DeclKind.StartupCode r ], startups + 1
      | SynModuleDecl.HashDirective _
      | SynModuleDecl.Attributes _
      | SynModuleDecl.NamespaceFragment _ -> opens, found, startups) ([], [], 0)
  opens, found

let extractDecls (source: string) : Result<FileDecls, string> =
  try
    let input, diagnostics = Fantomas.FCS.Parse.parseFile false (SourceText.ofString source) []
    match diagnostics |> List.tryFind (fun d -> d.Severity.IsError), input with
    | Some error, _ ->
      let line = error.Range |> Option.map (fun r -> string r.StartLine) |> Option.defaultValue "?"
      Error (sprintf "the file does not parse (line %s: %s)" line error.Message)
    | None, ParsedInput.ImplFile(ParsedImplFileInput(contents = contents)) ->
      let lines = sourceLines source
      match contents with
      | [ SynModuleOrNamespace(longId = ids; kind = kind; decls = decls) ] ->
        let modulePath =
          match kind with
          | SynModuleOrNamespaceKind.AnonModule -> []
          | _ -> ids |> List.map _.idText
        let opens, found = declsOf lines decls
        Ok { ModulePath = modulePath; Opens = opens; Decls = found }
      | _ -> Error "the file declares several namespaces or modules at the top level"
    | None, ParsedInput.SigFile _ -> Error "signature files are not reloaded"
  with ex -> Error (sprintf "the file could not be parsed: %s" ex.Message)

let private normalize (text: string) =
  (sourceLines text |> Array.map _.TrimEnd() |> String.concat "\n").Trim()

let private changeFor (decl: SourceDecl) =
  match decl.Kind with
  | DeclKind.TypeDecl -> ReloadChange.TypeChanged decl.Name
  | DeclKind.ValueDecl -> ReloadChange.ValueChanged decl.Name
  | DeclKind.FunctionDecl -> ReloadChange.SignatureChanged decl.Name
  | DeclKind.EntryPointDecl -> ReloadChange.EntryPointChanged
  | DeclKind.NestedModuleDecl -> ReloadChange.ModuleChanged decl.Name
  | DeclKind.StartupCode -> ReloadChange.StartupCodeChanged

let private removalFor (decl: SourceDecl) =
  match decl.Kind with
  | DeclKind.EntryPointDecl -> ReloadChange.EntryPointChanged
  | DeclKind.StartupCode -> ReloadChange.StartupCodeChanged
  | DeclKind.TypeDecl
  | DeclKind.ValueDecl
  | DeclKind.FunctionDecl
  | DeclKind.NestedModuleDecl -> ReloadChange.DeclarationRemoved decl.Name

[<RequireQualifiedAccess>]
type private DeclOutcome =
  | Unchanged
  | Patch of SourceDecl
  | Restart of ReloadChange

/// A type and its companion module share a name, and a name can be shadowed,
/// so a declaration is identified by kind, name and occurrence.
let private keyed (decls: SourceDecl list) =
  decls
  |> List.mapFold (fun (seen: Map<DeclKind * string, int>) d ->
    let n = seen |> Map.tryFind (d.Kind, d.Name) |> Option.defaultValue 0
    ((d.Kind, d.Name, n), d), Map.add (d.Kind, d.Name) (n + 1) seen) Map.empty
  |> fst

let private outcomeOf (baseline: Map<DeclKind * string * int, SourceDecl>) (key, current: SourceDecl) =
  match Map.tryFind key baseline, current.Kind with
  | None, DeclKind.FunctionDecl -> DeclOutcome.Patch current
  | None, _ -> DeclOutcome.Restart (changeFor current)
  | Some before, _ when normalize before.Text = normalize current.Text -> DeclOutcome.Unchanged
  | Some before, DeclKind.FunctionDecl ->
    match normalize before.Header = normalize current.Header with
    | true -> DeclOutcome.Patch current
    | false -> DeclOutcome.Restart (ReloadChange.SignatureChanged current.Name)
  | Some _, _ -> DeclOutcome.Restart (changeFor current)

/// Types, values and startup code are compared with the source the running app
/// was built from; a function may be patched only if its header is unchanged.
let planReload (baseline: FileDecls) (current: FileDecls) : ReloadPlan =
  let baselineKeyed = keyed baseline.Decls
  let currentKeyed = keyed current.Decls
  let before = Map.ofList baselineKeyed
  let now = Map.ofList currentKeyed
  let outcomes = currentKeyed |> List.map (outcomeOf before)
  let removed =
    baselineKeyed
    |> List.filter (fun (key, _) -> not (Map.containsKey key now))
    |> List.map (snd >> removalFor)
  let restarts =
    (outcomes |> List.choose (function DeclOutcome.Restart c -> Some c | _ -> None)) @ removed
    |> List.distinct
  match restarts with
  | first :: rest -> ReloadPlan.RestartRequired (first, rest)
  | [] -> ReloadPlan.PatchFunctions (outcomes |> List.choose (function DeclOutcome.Patch d -> Some d | _ -> None))
