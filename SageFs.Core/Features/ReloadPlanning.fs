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

/// A patch is compiled outside the app's assembly, so it cannot see private or internal members.
[<RequireQualifiedAccess>]
type DeclAccess =
  | Public
  | Internal
  | Private

/// One top-level declaration of a file, with its exact source text.
type SourceDecl = {
  Name: string
  Kind: DeclKind
  Access: DeclAccess
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
  | UsesNonPublicMember of fn: string * memberName: string

[<RequireQualifiedAccess>]
type ReloadPlan =
  | PatchFunctions of changed: SourceDecl list
  | RestartRequired of first: ReloadChange * rest: ReloadChange list

module ReloadChange =
  /// How one reason reads on the session card and in MCP replies.
  let describe (change: ReloadChange) : string =
    match change with
    | ReloadChange.TypeChanged name -> sprintf "type %s changed" name
    | ReloadChange.ValueChanged name -> sprintf "%s changed (it is built at startup)" name
    | ReloadChange.SignatureChanged name -> sprintf "the signature of %s changed" name
    | ReloadChange.EntryPointChanged -> "the entry point changed"
    | ReloadChange.ModuleChanged name -> sprintf "module %s changed" name
    | ReloadChange.StartupCodeChanged -> "startup code changed"
    | ReloadChange.DeclarationRemoved name -> sprintf "%s was removed" name
    | ReloadChange.UsesNonPublicMember (fn, memberName) ->
      sprintf "%s uses %s, which is not public, so it cannot be patched in place" fn memberName

  let describeAll (first: ReloadChange) (rest: ReloadChange list) : string =
    first :: rest |> List.map describe |> String.concat "; "

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

let rec private patAccess (pat: SynPat) : SynAccess option =
  match pat with
  | SynPat.LongIdent(accessibility = access) -> access
  | SynPat.Named(accessibility = access) -> access
  | SynPat.Typed(pat = inner)
  | SynPat.Paren(pat = inner)
  | SynPat.Attrib(pat = inner) -> patAccess inner
  | _ -> None

let private accessOf (access: SynAccess option) : DeclAccess =
  match access with
  | Some a when a.IsPrivate -> DeclAccess.Private
  | Some a when a.IsInternal -> DeclAccess.Internal
  | _ -> DeclAccess.Public

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
    Access = accessOf (patAccess pat)
    Header = header.Trim()
    Text = slice lines (start.Line, start.Column) (whole.EndLine, whole.EndColumn)
    StartLine = start.Line
    EndLine = whole.EndLine }

let private simpleDecl (lines: string array) (name: string) (kind: DeclKind) (access: DeclAccess) (r: range) : SourceDecl =
  { Name = name
    Kind = kind
    Access = access
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
          |> List.map (fun (SynTypeDefn(typeInfo = SynComponentInfo(longId = ids; accessibility = access)) as defn) ->
            simpleDecl lines (identText ids) DeclKind.TypeDecl (accessOf access) defn.Range)
        opens, found @ types, startups
      | SynModuleDecl.Exception(range = r) ->
        opens, found @ [ simpleDecl lines (exceptionName (rangeText lines r)) DeclKind.TypeDecl DeclAccess.Public r ], startups
      | SynModuleDecl.NestedModule(moduleInfo = SynComponentInfo(longId = ids; accessibility = access); range = r) ->
        opens, found @ [ simpleDecl lines (identText ids) DeclKind.NestedModuleDecl (accessOf access) r ], startups
      | SynModuleDecl.ModuleAbbrev(ident = ident; range = r) ->
        opens, found @ [ simpleDecl lines ident.idText DeclKind.NestedModuleDecl DeclAccess.Public r ], startups
      | SynModuleDecl.Expr(range = r) ->
        let name = sprintf "startup#%d" (startups + 1)
        opens, found @ [ simpleDecl lines name DeclKind.StartupCode DeclAccess.Public r ], startups + 1
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

/// A source file is a trustworthy hot-reload baseline only if it was not
/// touched after the build that produced the assembly currently running —
/// otherwise `extractDecls` on it would capture an edit the running app
/// never saw, and `planReload` would see current = baseline and silently
/// never surface that edit as a change to patch or restart for.
let baselineIsTrustworthy (assemblyWriteTimeUtc: DateTime) (sourceWriteTimeUtc: DateTime) : bool =
  sourceWriteTimeUtc <= assemblyWriteTimeUtc

let private isIdentifier (name: string) =
  System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_']*$")

/// Blanks out comments and string/char literals (replacing them with spaces, so
/// column positions and neighboring identifiers are unaffected) so identifier
/// extraction can never mistake prose or literal data for a real reference.
/// Handles `//` line comments, nested `(* *)` block comments, regular/verbatim/
/// triple-quoted strings, and simple char literals.
let private stripCommentsAndStrings (text: string) : string =
  let n = text.Length
  let buf = System.Text.StringBuilder(text)
  let blank i j = for k in i .. j - 1 do buf.[k] <- ' '
  let rec go i =
    if i >= n then ()
    elif i + 1 < n && text.[i] = '/' && text.[i + 1] = '/' then
      let e = match text.IndexOf('\n', i) with | -1 -> n | idx -> idx
      blank i e
      go e
    elif i + 1 < n && text.[i] = '(' && text.[i + 1] = '*' then
      let rec find depth k =
        if k >= n then n
        elif k + 1 < n && text.[k] = '(' && text.[k + 1] = '*' then find (depth + 1) (k + 2)
        elif k + 1 < n && text.[k] = '*' && text.[k + 1] = ')' then
          match depth with
          | 1 -> k + 2
          | _ -> find (depth - 1) (k + 2)
        else find depth (k + 1)
      let e = find 1 (i + 2)
      blank i e
      go e
    elif i + 1 < n && text.[i] = '@' && text.[i + 1] = '"' then
      let rec find k =
        if k >= n then n
        elif k + 1 < n && text.[k] = '"' && text.[k + 1] = '"' then find (k + 2)
        elif text.[k] = '"' then k + 1
        else find (k + 1)
      let e = find (i + 2)
      blank i e
      go e
    elif i + 2 < n && text.[i] = '"' && text.[i + 1] = '"' && text.[i + 2] = '"' then
      let e = match text.IndexOf("\"\"\"", i + 3) with | -1 -> n | idx -> idx + 3
      blank i e
      go e
    elif text.[i] = '"' then
      let rec find k =
        if k >= n then n
        elif text.[k] = '\\' && k + 1 < n then find (k + 2)
        elif text.[k] = '"' then k + 1
        else find (k + 1)
      let e = find (i + 1)
      blank i e
      go e
    elif text.[i] = '\'' && i + 2 < n && text.[i + 1] = '\\' then
      match text.IndexOf('\'', i + 2) with
      | idx when idx > i && idx - i <= 8 ->
        blank i (idx + 1)
        go (idx + 1)
      | _ -> go (i + 1)
    elif text.[i] = '\'' && i + 2 < n && text.[i + 1] <> '\'' && text.[i + 2] = '\'' then
      blank i (i + 3)
      go (i + 3)
    else go (i + 1)
  go 0
  buf.ToString()

let private identifierPattern =
  System.Text.RegularExpressions.Regex(@"[A-Za-z_][A-Za-z0-9_']*", System.Text.RegularExpressions.RegexOptions.Compiled)

/// Every bare identifier a piece of source text refers to. Comments and string/char
/// literals are stripped first, so a name that only appears as prose or literal
/// data is never mistaken for a reference — and a qualified use (`Module.name`)
/// is still found, because the identifier itself still appears in the text.
let private identifiersOf (text: string) : Set<string> =
  identifierPattern.Matches(stripCommentsAndStrings text)
  |> Seq.cast<System.Text.RegularExpressions.Match>
  |> Seq.map (fun m -> m.Value)
  |> Set.ofSeq

/// The names a hidden type also exposes without ever spelling its own name: a
/// union case (`Circle 1.0` never says `Shape`) or a record field (`{ Timeout = 5 }`
/// never says `Config`) both make a patch depend on the type just as much as
/// spelling its name would — so both must count as "uses this hidden type".
let private innerNamesOf (typeDecl: SourceDecl) : string list =
  try
    // A TypeDecl's Text is captured from the SynTypeDefn's own range, which starts
    // after the `type`/`and` keyword — put it back so the wrapped snippet parses.
    let wrapped = "module __Hidden__\ntype " + typeDecl.Text
    match Fantomas.FCS.Parse.parseFile false (SourceText.ofString wrapped) [] with
    | ParsedInput.ImplFile(ParsedImplFileInput(contents = [ SynModuleOrNamespace(decls = decls) ])), diagnostics
        when not (diagnostics |> List.exists (fun d -> d.Severity.IsError)) ->
      decls
      |> List.collect (function
        | SynModuleDecl.Types(typeDefns = defns) ->
          defns
          |> List.collect (fun (SynTypeDefn(typeRepr = repr)) ->
            match repr with
            | SynTypeDefnRepr.Simple(simpleRepr = SynTypeDefnSimpleRepr.Union(unionCases = cases)) ->
              cases |> List.map (fun (SynUnionCase(ident = SynIdent(ident, _))) -> ident.idText)
            | SynTypeDefnRepr.Simple(simpleRepr = SynTypeDefnSimpleRepr.Record(recordFields = fields)) ->
              fields |> List.choose (fun (SynField(idOpt = idOpt)) -> idOpt |> Option.map _.idText)
            | _ -> [])
        | _ -> [])
    | _ -> []
  with _ -> []

/// The names that count as "using" a hidden declaration: its own name, plus —
/// for a type — the case/field names a patch can reference without ever
/// naming the type itself.
let private targetNamesOf (decl: SourceDecl) : string list =
  match decl.Kind with
  | DeclKind.TypeDecl -> decl.Name :: innerNamesOf decl
  | _ -> [ decl.Name ]

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
  // A patch cannot see the file's non-public members (it is compiled in FSI,
  // outside the app's assembly) unless the same patch re-emits them.
  let patchedNames =
    outcomes |> List.choose (function DeclOutcome.Patch d -> Some d.Name | _ -> None) |> Set.ofList
  let hidden =
    current.Decls
    |> List.filter (fun d -> d.Access <> DeclAccess.Public && not (patchedNames.Contains d.Name) && isIdentifier d.Name)
  let hiddenTargets = hidden |> List.map (fun h -> h, targetNamesOf h |> List.filter isIdentifier |> Set.ofList)
  let unreachable =
    outcomes
    |> List.choose (function
      | DeclOutcome.Patch f ->
        let used = identifiersOf f.Text
        hiddenTargets
        |> List.tryFind (fun (h, names) -> h.Name <> f.Name && names |> Set.exists (fun n -> Set.contains n used))
        |> Option.map (fun (h, _) -> ReloadChange.UsesNonPublicMember (f.Name, h.Name))
      | _ -> None)
  let restarts =
    (outcomes |> List.choose (function DeclOutcome.Restart c -> Some c | _ -> None)) @ removed @ unreachable
    |> List.distinct
  match restarts with
  | first :: rest -> ReloadPlan.RestartRequired (first, rest)
  | [] -> ReloadPlan.PatchFunctions (outcomes |> List.choose (function DeclOutcome.Patch d -> Some d | _ -> None))

[<RequireQualifiedAccess>]
type PatchOutcome =
  | Applied
  | RestartNeeded of first: ReloadChange * rest: ReloadChange list

/// A patched function that already existed must have been detoured onto its new
/// copy (reloadedMethods are the full names of the methods that were detoured);
/// one that was not had its compiled signature changed, so the app must restart.
let confirmPatch (before: FileDecls) (patched: SourceDecl list) (reloadedMethods: string list) : PatchOutcome =
  let existed (f: SourceDecl) =
    before.Decls |> List.exists (fun d -> d.Kind = DeclKind.FunctionDecl && d.Name = f.Name)
  let detoured (f: SourceDecl) =
    reloadedMethods |> List.exists (fun m -> m = f.Name || m.EndsWith("." + f.Name, StringComparison.Ordinal))
  let notDetoured =
    patched
    |> List.filter (fun f -> existed f && not (detoured f))
    |> List.map (fun f -> ReloadChange.SignatureChanged f.Name)
  match notDetoured with
  | first :: rest -> PatchOutcome.RestartNeeded (first, rest)
  | [] -> PatchOutcome.Applied
