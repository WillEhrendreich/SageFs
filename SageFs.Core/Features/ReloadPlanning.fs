/// Decides what a save to a running app's source file needs: patch the changed
/// functions in place, or rebuild and restart because something only takes
/// effect at startup (a type, a module-level value, the entry point) changed.
module SageFs.Features.ReloadPlanning

open System
open Fantomas.FCS.Syntax
open Fantomas.FCS.Text
open SageFs.Features.ReloadOutcome

[<RequireQualifiedAccess>]
type DeclKind =
  | TypeDecl
  | ValueDecl
  /// A module-level `let mutable`. Kept apart from `ValueDecl` because the two
  /// fail for opposite reasons and the user acts on each differently: an
  /// immutable value was COMPUTED once at startup and the app captured the
  /// result, while a mutable one is live DATA whose current value is the
  /// running app's state. Telling someone their request counter "is built at
  /// startup" is true of neither the problem nor the fix.
  | MutableValueDecl
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

/// One declaration of a file, with its exact source text.
type SourceDecl = {
  Name: string
  Kind: DeclKind
  Access: DeclAccess
  /// The nested modules this declaration sits inside, relative to the file's
  /// `ModulePath` — `[]` for a declaration at the file's top level, and
  /// `["Greeting"]` for `let greeting` inside `namespace X` + `module Greeting =`.
  ///
  /// WHY this exists: `namespace X` followed by `module Y =` is the ordinary way
  /// to write an F# file, and the planner used to treat that whole nested module
  /// as ONE opaque declaration. Every edit inside it — including a plain function
  /// body — therefore came back as `ModuleChanged`, which refuses the save with
  /// "SageFs does not re-point a change inside module 'Y' yet". Carrying the
  /// container per declaration is what lets the planner see the function that
  /// actually changed, and what lets `emitStableIdentity` re-emit it under the
  /// module it really lives in so the detour pairs against the compiled method.
  Container: string list
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
  /// The exact source `extractDecls` parsed this from — `None` only for a
  /// `FileDecls` a test built directly without going through `extractDecls`.
  /// Kept so `planReload` can type-check the real file with FCS
  /// (`GetAllUsesOfAllSymbolsInFile`, the same technique `Diagnostics.fs`
  /// uses) for exact symbol identity instead of identifier-name matching —
  /// every `SourceDecl`'s StartLine/EndLine already describe positions
  /// within this same text.
  RawSource: string option
}

[<RequireQualifiedAccess>]
type ReloadChange =
  | TypeChanged of name: string
  | ValueChanged of name: string
  /// A module-level `let mutable` whose HEADER changed (its access, say), so
  /// its live value can't simply be kept. An edited initializer alone is not
  /// this: that's `LiveState.Kept`, and the app keeps its value.
  | MutableStateChanged of name: string
  /// A `let mutable` whose type changed. The app's live value has the old
  /// type, so there is nothing safe to keep. Found from a declared annotation
  /// here, or by the runtime probe when there isn't one.
  | MutableStateRetyped of name: string * was: string * now: string
  | SignatureChanged of name: string
  | EntryPointChanged
  | ModuleChanged of name: string
  | StartupCodeChanged
  | DeclarationRemoved of name: string
  /// A declaration the running build never had. There is no original to
  /// re-point, which is a different thing from one that changed.
  | DeclarationAdded of name: string
  | UsesNonPublicMember of fn: string * memberName: string
  /// A redefined value the running app kept a copy of (rule 2). Found from the
  /// running app's own evidence, never from the source diff.
  | ValueCopied of name: string * holder: string
  /// A redefined value SageFs couldn't check (rule 2), and why.
  | ValueUntraceable of name: string * why: string
  /// A mutable binding's getter and setter were redirected onto different
  /// code generations: one leg landed and the other did not, so reads and
  /// writes now disagree about which field is live. Discovered only from the
  /// RUNTIME detour result — never from a source diff, which is why this
  /// case is reached from the patch-apply path rather than `planReload` —
  /// and it forces a restart rather than joining the ordinary missed-patch
  /// reasons, because by the time this is known the process is already
  /// silently wrong.
  | MutableBindingTorn of binding: string

/// Live module state a patch has to respect. Rule 1 of hot-reload-state-spec.md:
/// code changes land, state stays.
[<RequireQualifiedAccess>]
type LiveState =
  /// An unedited non-public `let mutable` the patch reads or writes. FSI can't
  /// name a private member of the app's assembly, so the patch gets a
  /// same-named stand-in that reads and writes the app's OWN storage. It is
  /// never re-declared, because a re-declaration is a fresh field holding the
  /// initializer and the live value would be gone.
  | Carried of decl: SourceDecl
  /// An edited `let mutable` whose type didn't change (rule 3). The app keeps
  /// its live value and the new initializer waits for an explicit reset. The
  /// save says so, because keeping state quietly is the one thing people
  /// complain about in Flutter.
  | Kept of decl: SourceDecl
  /// An edited public immutable value (rule 2). It's patched, getter and all,
  /// only if the running app says nothing kept a copy of the old one; the
  /// worker asks before patching (ValueReads). Otherwise it's a restart that
  /// names who kept it.
  | Redefined of decl: SourceDecl

[<RequireQualifiedAccess>]
type ReloadPlan =
  | PatchFunctions of changed: SourceDecl list
  /// The same patch, plus live state it has to leave where it is. Head and
  /// rest, so this case can't be built with nothing in it: a patch with no
  /// state to respect is `PatchFunctions`, and there is one way to say it.
  | PatchKeepingState of changed: SourceDecl list * first: LiveState * rest: LiveState list
  | RestartRequired of first: ReloadChange * rest: ReloadChange list

module ReloadChange =
  /// How one reason reads on the session card and in MCP replies.
  let describe (change: ReloadChange) : string =
    match change with
    | ReloadChange.TypeChanged name -> sprintf "type %s changed" name
    | ReloadChange.ValueChanged name -> sprintf "%s changed (it is built at startup)" name
    | ReloadChange.MutableStateChanged name -> sprintf "%s changed (it is mutable module state)" name
    | ReloadChange.MutableStateRetyped (name, was, now) -> sprintf "%s changed type from %s to %s (it is mutable module state)" name was now
    | ReloadChange.SignatureChanged name -> sprintf "the signature of %s changed" name
    | ReloadChange.EntryPointChanged -> "the entry point changed"
    | ReloadChange.ModuleChanged name -> sprintf "module %s changed" name
    | ReloadChange.StartupCodeChanged -> "startup code changed"
    | ReloadChange.DeclarationRemoved name -> sprintf "%s was removed" name
    | ReloadChange.DeclarationAdded name -> sprintf "%s was added" name
    | ReloadChange.UsesNonPublicMember (fn, memberName) ->
      sprintf "%s uses %s, which is not public, so it cannot be patched in place" fn memberName
    | ReloadChange.ValueCopied (name, holder) -> sprintf "the running app kept a copy of %s: %s" name holder
    | ReloadChange.ValueUntraceable (name, why) -> sprintf "%s changed and SageFs can't check where its copies went: %s" name why
    | ReloadChange.MutableBindingTorn binding ->
      sprintf
        "'%s' tore: one of its accessors was re-pointed to the new code and the other was not, so reads and writes now disagree about which field is live"
        binding

  let describeAll (first: ReloadChange) (rest: ReloadChange list) : string =
    first :: rest |> List.map describe |> String.concat "; "

  /// A refusal restated as the SHAPE of the change the user made, which is the
  /// only vocabulary they can act on. `ReloadChange` names what the diff found;
  /// `RestartReason` names what it means for the running process. This is the
  /// one translation between them — every surface that reports a refusal reads
  /// it from here rather than inventing its own wording.
  let restartReason (change: ReloadChange) : RestartReason =
    match change with
    // Live objects in the running process were laid out by the old definition.
    | ReloadChange.TypeChanged name -> RestartReason.TypeShapeChanged name
    // The case users actually hit: `let routes = [ get "/" home ]`, or
    // `let getHome : HttpHandler = Response.ofHtml (...)`. The value was
    // computed during module initialisation and the app captured the result.
    | ReloadChange.ValueChanged name -> RestartReason.StartupComputedValue name
    // Its value is the app's live state. SageFs refuses to guess between
    // carrying it forward (which ignores the edit) and resetting it (which
    // destroys the state) — `MutableModuleState`'s remedy says exactly that.
    | ReloadChange.MutableStateChanged name -> RestartReason.MutableModuleState name
    | ReloadChange.MutableStateRetyped (name, was, now) -> RestartReason.MutableStateTypeChanged (name, was, now)
    | ReloadChange.SignatureChanged name -> RestartReason.SignatureChanged name
    // `main` ran once, at process start, and composed everything now serving.
    | ReloadChange.EntryPointChanged -> RestartReason.StartupComputedValue "[<EntryPoint>] main"
    // A bare module-level expression runs during module initialisation, same as
    // a computed value, and its effects are already in the running process.
    | ReloadChange.StartupCodeChanged -> RestartReason.StartupComputedValue "the module's startup code"
    // Unimplemented rather than impossible: the planner does not descend into
    // nested modules yet, so it refuses the whole module.
    | ReloadChange.ModuleChanged name -> RestartReason.NotYetSupported (sprintf "a change inside module '%s'" name)
    | ReloadChange.DeclarationRemoved name -> RestartReason.NotYetSupported (sprintf "a removed declaration ('%s')" name)
    | ReloadChange.DeclarationAdded name -> RestartReason.NewDeclaration name
    // A patch is compiled in FSI, outside the app's assembly, so it cannot see
    // the file's own private/internal members. Solvable (InternalsVisibleTo,
    // re-emitting the member alongside the patch) — unimplemented, not physics.
    | ReloadChange.UsesNonPublicMember (fn, memberName) ->
      RestartReason.NotYetSupported (sprintf "'%s', because it uses the non-public '%s'" fn memberName)
    | ReloadChange.ValueCopied (name, holder) -> RestartReason.ValueCopiedByApp (name, holder)
    | ReloadChange.ValueUntraceable (name, why) -> RestartReason.ValueUntraceable (name, why)
    // A tear is a mutable-binding coherence failure, not a capability gap —
    // the same reason a source-level `let mutable` edit gets when it cannot
    // be patched at all. The remedy is identical: restart to re-run the
    // initialiser, because SageFs will not guess which field is the real one.
    | ReloadChange.MutableBindingTorn binding -> RestartReason.MutableModuleState binding

  let restartReasons (first: ReloadChange) (rest: ReloadChange list) : RestartReason list =
    first :: rest |> List.map restartReason

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

/// A module-level value bound DIRECTLY to a lambda — `let f : a -> b = fun x
/// -> ...` — compiles to a METHOD, exactly like `let f x = ...`. Read out of the
/// IL, not assumed: the shape-matrix fixture's `lambdaHandler` closure calls
/// `Shapes.lambdaHandler` by name, and re-pointing that method changes what the
/// running app serves. Classifying it as a value reported a restart for an edit
/// that was actually patchable. Anything else on the right-hand side — a
/// computed value, a partial application, a lambda built inside a `let` —
/// stays a value, because then the method does not exist.
let rec private isLambdaBody (expr: SynExpr) =
  match expr with
  | SynExpr.Lambda _ -> true
  | SynExpr.Typed(expr = inner)
  | SynExpr.Paren(expr = inner) -> isLambdaBody inner
  | _ -> false

let private bindingDecl (lines: string array) (container: string list) (binding: SynBinding) : SourceDecl =
  let (SynBinding(attributes = attributes; isMutable = isMutable; headPat = pat; expr = body; trivia = trivia)) = binding
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
  // `isMutable` comes from the compiler's own parse, not from the text: a
  // `let mutable` is not spotted by looking for the word, and a binding that
  // merely mentions `mutable` in a comment is not one.
  let kind =
    match isEntryPoint attributes, isFunctionHead pat || (not isMutable && isLambdaBody body), isMutable with
    | true, _, _ -> DeclKind.EntryPointDecl
    | false, true, _ -> DeclKind.FunctionDecl
    | false, false, true -> DeclKind.MutableValueDecl
    | false, false, false -> DeclKind.ValueDecl
  { Name = patName lines pat
    Kind = kind
    Access = accessOf (patAccess pat)
    Container = container
    Header = header.Trim()
    Text = slice lines (start.Line, start.Column) (whole.EndLine, whole.EndColumn)
    StartLine = start.Line
    EndLine = whole.EndLine }

/// A type's SHAPE: its source with every member BODY cut out, leaving the
/// member signatures, fields and union cases. It is stored in `Header`, which
/// for every declaration means "the part that must be unchanged for a patch" —
/// a function's signature, a type's shape.
///
/// Without it, ANY edit inside a type read as `TypeChanged`, a restart. Editing
/// `static member Render() = "A"` to `"B"` changes a body, not a layout: the
/// running app demonstrably picks it up once the type is re-evaluated (measured
/// against a real host — the shape matrix's `member` cell served "B" while the
/// worker reported RestartRequired, "the shape of type 'Renderer' changed").
/// A type whose fields or members are added, removed or re-typed still has a
/// different shape and still restarts; live instances were laid out by the old
/// definition, and no re-point can reach that.
let private typeShape (lines: string array) (defn: SynTypeDefn) : string =
  let (SynTypeDefn(typeRepr = repr; members = augmentation)) = defn
  let declared =
    match repr with
    | SynTypeDefnRepr.ObjectModel(members = ms) -> ms
    | _ -> []
  let bodies =
    declared @ augmentation
    |> List.choose (function
      | SynMemberDefn.Member(memberDefn = SynBinding(expr = body)) -> Some body.Range
      | _ -> None)
    |> List.sortBy (fun b -> b.StartLine, b.StartColumn)
  let r = defn.Range
  let gapStarts = (r.StartLine, r.StartColumn) :: (bodies |> List.map (fun b -> b.EndLine, b.EndColumn))
  let gapEnds = (bodies |> List.map (fun b -> b.StartLine, b.StartColumn)) @ [ (r.EndLine, r.EndColumn) ]
  List.zip gapStarts gapEnds
  |> List.map (fun (a, b) -> slice lines a b)
  |> String.concat " … "

let private simpleDecl (lines: string array) (container: string list) (name: string) (kind: DeclKind) (access: DeclAccess) (r: range) : SourceDecl =
  { Name = name
    Kind = kind
    Access = access
    Container = container
    Header = ""
    Text = rangeText lines r
    StartLine = r.StartLine
    EndLine = r.EndLine }

let private exceptionName (text: string) =
  match text.Split([| ' '; '\n'; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList with
  | "exception" :: name :: _ -> name
  | _ -> text

/// Flatten a file's declarations, DESCENDING into nested modules.
///
/// Chesterton's fence — this used to stop at a nested module and record it as a
/// single opaque `NestedModuleDecl`. That made `namespace X` + `module Y =` (the
/// ordinary F# file layout, and the layout of every Falco/Giraffe/Saturn app and
/// of this repo's own hot-reload fixture) unpatchable: any edit inside `Y`, even
/// one function body, diffed as `ModuleChanged Y` and was refused with "SageFs
/// does not re-point a change inside module 'Y' yet". Descending is what lets
/// the diff name the function that actually changed. `startups` is threaded
/// through the whole walk so `startup#N` stays unique across the file, and each
/// declaration records the `container` it was found in so `emitStableIdentity`
/// can re-emit it under the module it really lives in.
///
/// A module ABBREVIATION (`module M = A.B.C`) is still opaque: it declares no
/// members of its own, so there is nothing inside it to patch.
let rec private declsIn
  (lines: string array)
  (container: string list)
  (startups: int)
  (decls: SynModuleDecl list)
  : string list * SourceDecl list * int =
  decls
  |> List.fold (fun (opens, found, startups) decl ->
    match decl with
    | SynModuleDecl.Open(target = SynOpenDeclTarget.ModuleOrNamespace(longId = SynLongIdent(id = ids))) ->
      opens @ [ identText ids ], found, startups
    | SynModuleDecl.Open(target = target) ->
      opens @ [ rangeText lines target.Range ], found, startups
    | SynModuleDecl.Let(bindings = bindings) ->
      opens, found @ (bindings |> List.map (bindingDecl lines container)), startups
    | SynModuleDecl.Types(typeDefns = defns) ->
      let types =
        defns
        |> List.map (fun (SynTypeDefn(typeInfo = SynComponentInfo(longId = ids; accessibility = access)) as defn) ->
          { simpleDecl lines container (identText ids) DeclKind.TypeDecl (accessOf access) defn.Range with
              Header = typeShape lines defn })
      opens, found @ types, startups
    | SynModuleDecl.Exception(range = r) ->
      opens,
      found @ [ simpleDecl lines container (exceptionName (rangeText lines r)) DeclKind.TypeDecl DeclAccess.Public r ],
      startups
    | SynModuleDecl.NestedModule(moduleInfo = SynComponentInfo(longId = ids); decls = inner) ->
      let name = identText ids
      let innerOpens, innerDecls, startups = declsIn lines (container @ [ name ]) startups inner
      opens @ innerOpens, found @ innerDecls, startups
    | SynModuleDecl.ModuleAbbrev(ident = ident; range = r) ->
      opens, found @ [ simpleDecl lines container ident.idText DeclKind.NestedModuleDecl DeclAccess.Public r ], startups
    | SynModuleDecl.Expr(range = r) ->
      let name = sprintf "startup#%d" (startups + 1)
      opens, found @ [ simpleDecl lines container name DeclKind.StartupCode DeclAccess.Public r ], startups + 1
    | SynModuleDecl.HashDirective _
    | SynModuleDecl.Attributes _
    | SynModuleDecl.NamespaceFragment _ -> opens, found, startups) ([], [], startups)

let private declsOf (lines: string array) (decls: SynModuleDecl list) : string list * SourceDecl list =
  let opens, found, _ = declsIn lines [] 0 decls
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
        Ok { ModulePath = modulePath; Opens = opens; Decls = found; RawSource = Some source }
      | _ -> Error "the file declares several namespaces or modules at the top level"
    | None, ParsedInput.SigFile _ -> Error "signature files are not reloaded"
  with ex -> Error (sprintf "the file could not be parsed: %s" ex.Message)

let private normalize (text: string) =
  (sourceLines text |> Array.map _.TrimEnd() |> String.concat "\n").Trim()

let private changeFor (decl: SourceDecl) =
  match decl.Kind with
  | DeclKind.TypeDecl -> ReloadChange.TypeChanged decl.Name
  | DeclKind.ValueDecl -> ReloadChange.ValueChanged decl.Name
  | DeclKind.MutableValueDecl -> ReloadChange.MutableStateChanged decl.Name
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
  | DeclKind.MutableValueDecl
  | DeclKind.FunctionDecl
  | DeclKind.NestedModuleDecl -> ReloadChange.DeclarationRemoved decl.Name

/// A declaration the running build never had. Reported as an ADDITION rather
/// than as a change, because "type Cfg changed" for a type that did not exist
/// sends the user looking for a change they never made.
let private additionFor (decl: SourceDecl) =
  match decl.Kind with
  | DeclKind.EntryPointDecl -> ReloadChange.EntryPointChanged
  | DeclKind.StartupCode -> ReloadChange.StartupCodeChanged
  | DeclKind.TypeDecl
  | DeclKind.ValueDecl
  | DeclKind.MutableValueDecl
  | DeclKind.FunctionDecl
  | DeclKind.NestedModuleDecl -> ReloadChange.DeclarationAdded decl.Name

[<RequireQualifiedAccess>]
type private DeclOutcome =
  | Unchanged
  | Patch of SourceDecl
  | Keep of SourceDecl
  /// Rule 2: an edited public value, patched only on the running app's word.
  | Redefine of SourceDecl
  | Restart of ReloadChange

/// A declared type annotation in a value binding's header, e.g. `int` from
/// `let mutable private hidden : int`.
let annotationOf (decl: SourceDecl) : string option =
  match decl.Header.IndexOf(':') with
  | -1 -> None
  | colon ->
    match decl.Header.Substring(colon + 1).Trim() with
    | "" -> None
    | t -> Some t

/// A type and its companion module share a name, and a name can be shadowed,
/// so a declaration is identified by kind, name and occurrence.
/// The container is part of the key: two nested modules in one file may each
/// declare `render`, and pairing one against the other would diff two unrelated
/// functions against each other.
let private keyed (decls: SourceDecl list) =
  decls
  |> List.mapFold (fun (seen: Map<DeclKind * string list * string, int>) d ->
    let n = seen |> Map.tryFind (d.Kind, d.Container, d.Name) |> Option.defaultValue 0
    ((d.Kind, d.Container, d.Name, n), d), Map.add (d.Kind, d.Container, d.Name) (n + 1) seen) Map.empty
  |> fst

let private outcomeOf (baseline: Map<DeclKind * string list * string * int, SourceDecl>) (key, current: SourceDecl) =
  match Map.tryFind key baseline, current.Kind with
  | None, DeclKind.FunctionDecl -> DeclOutcome.Patch current
  | None, _ -> DeclOutcome.Restart (additionFor current)
  | Some before, _ when normalize before.Text = normalize current.Text -> DeclOutcome.Unchanged
  | Some before, DeclKind.FunctionDecl ->
    match normalize before.Header = normalize current.Header with
    | true -> DeclOutcome.Patch current
    | false -> DeclOutcome.Restart (ReloadChange.SignatureChanged current.Name)
  // Same SHAPE (see `typeShape`), different text: only member bodies moved, so
  // re-evaluating the type re-points its members instead of needing a restart.
  | Some before, DeclKind.TypeDecl when normalize before.Header = normalize current.Header ->
    DeclOutcome.Patch current
  // Rule 3: an edited initializer keeps the live value. A changed header is
  // different: a changed annotation is a changed type, and anything else in
  // the header (access, say) changes what the binding IS.
  | Some before, DeclKind.MutableValueDecl ->
    match normalize before.Header = normalize current.Header, annotationOf before, annotationOf current with
    | true, _, _ -> DeclOutcome.Keep current
    | false, was, now when was <> now ->
      let shown = Option.defaultValue "(inferred)"
      DeclOutcome.Restart (ReloadChange.MutableStateRetyped (current.Name, shown was, shown now))
    | false, _, _ -> DeclOutcome.Restart (ReloadChange.MutableStateChanged current.Name)
  // Rule 2: an edited public value can get its new value, if nothing in the
  // running app kept a copy of the old one. That's the app's call, not the
  // diff's, so the plan says "redefine" and the worker asks. A value that
  // isn't public has no public getter to patch, and a changed header (a new
  // annotation, say) changes what the binding IS: both still restart.
  | Some before, DeclKind.ValueDecl when current.Access = DeclAccess.Public && normalize before.Header = normalize current.Header ->
    DeclOutcome.Redefine current
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

/// The identifier-set fallback: a patch "uses" a hidden declaration when one of
/// the hidden declaration's own names (its name, or — for a type — a case/field
/// name) appears as a bare identifier anywhere in the patch's text. This can
/// only ever be MORE eager to restart than exact symbol resolution — a name
/// that merely collides with a hidden declaration (a shadowing parameter, a
/// same-named local) still counts as "uses" here — which is exactly why it is
/// safe as a fallback: it never looks more permissive than the exact check.
let private hiddenUsesViaIdentifiers (patches: SourceDecl list) (hidden: SourceDecl list) : (SourceDecl * SourceDecl list) list =
  let hiddenTargets = hidden |> List.map (fun h -> h, targetNamesOf h |> List.filter isIdentifier |> Set.ofList)
  patches
  |> List.map (fun f ->
    let used = identifiersOf f.Text
    f,
    hiddenTargets
    |> List.filter (fun (h, names) -> h.Name <> f.Name && names |> Set.exists (fun n -> Set.contains n used))
    |> List.map fst)

/// One FSharpChecker, reused across every reload decision in the process: it
/// caches compiler internals (default reference sets, etc.) and is documented
/// as safe under concurrent, repeated use, so there is no reason to pay its
/// construction cost per save.
let private checker = lazy FSharp.Compiler.CodeAnalysis.FSharpChecker.Create()

/// A counter folded into the synthetic file name/version of every check, so
/// FSharpChecker's own internal (fileName, version) result cache can never
/// serve a stale answer for a changed body under a reused name.
let private checkCounter = ref 0

/// Type-checks `source` — a real file's exact text, standalone (no project,
/// no `#load`ed dependencies) — and returns every symbol use FCS found in it,
/// the same `GetAllUsesOfAllSymbolsInFile` technique `Diagnostics.fs` already
/// uses for the live-testing dependency graph. Errors (including "the file
/// depends on something outside itself that a standalone check can't see" —
/// the common case for a real app file with NuGet/ASP.NET references) are
/// reported, never silently swallowed into an empty result: an incomplete
/// symbol table must never be mistaken for "nothing references the hidden
/// declaration."
let private symbolUsesOf (source: string) : Result<FSharp.Compiler.CodeAnalysis.FSharpSymbolUse list, string> =
  try
    let n = System.Threading.Interlocked.Increment checkCounter
    let fileName = sprintf "reload-planning-check-%d.fs" n
    let sourceText = FSharp.Compiler.Text.SourceText.ofString source
    let projOptions, _ =
      checker.Value.GetProjectOptionsFromScript(fileName, sourceText, assumeDotNetFramework = false)
      |> fun a -> Async.RunSynchronously(a, timeout = 10_000)
    let parseResults, answer =
      checker.Value.ParseAndCheckFileInProject(fileName, n, sourceText, projOptions)
      |> fun a -> Async.RunSynchronously(a, timeout = 10_000)
    match answer with
    | FSharp.Compiler.CodeAnalysis.FSharpCheckFileAnswer.Aborted ->
      Error "the standalone type check was aborted"
    | FSharp.Compiler.CodeAnalysis.FSharpCheckFileAnswer.Succeeded checkResults ->
      let isError (d: FSharp.Compiler.Diagnostics.FSharpDiagnostic) =
        d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error
      match Array.append parseResults.Diagnostics checkResults.Diagnostics |> Array.exists isError with
      | true -> Error "the file does not type-check standalone; symbol resolution may be incomplete"
      | false -> Ok (checkResults.GetAllUsesOfAllSymbolsInFile() |> Seq.toList)
  with ex -> Error ex.Message

/// Exact reachability via the compiler's own symbol table, not identifier-name
/// matching: a patch only "uses" a hidden declaration when some use inside the
/// patch's own source lines resolves to a symbol whose *declaration* lies
/// inside that hidden declaration's own source lines. Resolution — not a name
/// list — is what decides it, so a local binding that merely shares a hidden
/// declaration's name (a shadowing parameter, a same-named local) is never
/// mistaken for a reference to it, and a use that only reaches a hidden type
/// through a union case or record field (never spelling the type's own name)
/// still resolves, because the compiler resolved the reference.
let private hiddenUsesViaSymbols (source: string) (patches: SourceDecl list) (hidden: SourceDecl list) : Result<(SourceDecl * SourceDecl list) list, string> =
  symbolUsesOf source
  |> Result.map (fun uses ->
    let within (d: SourceDecl) (line: int) = line >= d.StartLine && line <= d.EndLine
    let declarationLine (su: FSharp.Compiler.CodeAnalysis.FSharpSymbolUse) =
      match su.IsFromDefinition with
      | true -> None
      | false -> su.Symbol.DeclarationLocation |> Option.map (fun r -> r.StartLine)
    patches
    |> List.map (fun f ->
      let declLines =
        uses
        |> Seq.filter (fun su -> within f su.Range.StartLine)
        |> Seq.choose declarationLine
        |> Set.ofSeq
      f, hidden |> List.filter (fun h -> h.Name <> f.Name && declLines |> Set.exists (within h))))

/// Prefers the exact FCS-symbol check on the real file text; falls back to the
/// identifier-set heuristic — never to "reachable" — when there is no real
/// source to check (a `FileDecls` a test built directly) or the standalone
/// check could not run cleanly. The fallback can only ever add restarts the
/// exact check would not have reported, never remove one it would have.
///
/// Answers with EVERY hidden declaration each patch uses, in file order, so the
/// planner can tell the ones it can carry (live mutable storage) from the ones
/// it can't.
let private hiddenUsesOf (current: FileDecls) (patches: SourceDecl list) (hidden: SourceDecl list) : (SourceDecl * SourceDecl list) list =
  match hidden, patches with
  | [], _
  | _, [] -> []
  | _ ->
    match current.RawSource with
    | Some source ->
      match hiddenUsesViaSymbols source patches hidden with
      | Ok found -> found
      | Error _ -> hiddenUsesViaIdentifiers patches hidden
    | None -> hiddenUsesViaIdentifiers patches hidden

/// A hidden declaration a patch can reach anyway: a `let mutable` is only its
/// storage, and the patch gets a stand-in bound to that storage (see
/// `LiveState.Carried`). A hidden function, value or type still can't be
/// reached, because FSI would need its code, not just its field.
let private isCarryable (d: SourceDecl) = d.Kind = DeclKind.MutableValueDecl

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
  let patches = outcomes |> List.choose (function DeclOutcome.Patch f -> Some f | _ -> None)
  let redefined = outcomes |> List.choose (function DeclOutcome.Redefine d -> Some d | _ -> None)
  // A redefined value's new text is emitted into the patch too, so it can't
  // reach the file's hidden members any more than a function can.
  let hiddenUses = hiddenUsesOf current (patches @ redefined) hidden
  let unreachable =
    hiddenUses
    |> List.choose (fun (f, used) ->
      used
      |> List.tryFind (isCarryable >> not)
      |> Option.map (fun h -> ReloadChange.UsesNonPublicMember (f.Name, h.Name)))
  let carried =
    hiddenUses
    |> List.collect snd
    |> List.filter isCarryable
    |> List.distinct
    |> List.map LiveState.Carried
  let kept = outcomes |> List.choose (function DeclOutcome.Keep d -> Some (LiveState.Kept d) | _ -> None)
  let restarts =
    (outcomes |> List.choose (function DeclOutcome.Restart c -> Some c | _ -> None)) @ removed @ unreachable
    |> List.distinct
  match restarts, carried @ kept @ (redefined |> List.map LiveState.Redefined) with
  | [], [] -> ReloadPlan.PatchFunctions patches
  | [], state :: more -> ReloadPlan.PatchKeepingState (patches, state, more)
  | _ :: _, _ ->
    // Restarting anyway, so a redefined value is just another thing the
    // restart picks up, and the card lists it where it sits in the file.
    let all =
      (outcomes
       |> List.choose (function
         | DeclOutcome.Restart c -> Some c
         | DeclOutcome.Redefine d -> Some (ReloadChange.ValueChanged d.Name)
         | DeclOutcome.Unchanged
         | DeclOutcome.Patch _
         | DeclOutcome.Keep _ -> None))
      @ removed @ unreachable
      |> List.distinct
    match all with
    | first :: rest -> ReloadPlan.RestartRequired (first, rest)
    | [] -> ReloadPlan.PatchFunctions patches

[<RequireQualifiedAccess>]
type PatchOutcome =
  | Applied
  | RestartNeeded of first: ReloadChange * rest: ReloadChange list

/// A patched function that already existed must have been detoured onto its new
/// copy (reloadedMethods are the full names of the methods that were detoured);
/// one that was not had its compiled signature changed, so the app must restart.
/// Whether any re-pointed method BELONGS to this declaration. A function is its
/// own method (`…Shapes.render`); a type's members are methods nested UNDER it
/// (`…Shapes.Renderer.Render`), which do not end with the type's name — so a
/// type patch needs its own test or every member-body reload reads as missed.
let private reachedBy (names: string list) (f: SourceDecl) =
  match f.Kind with
  | DeclKind.TypeDecl ->
    names
    |> List.exists (fun m ->
      m.StartsWith(f.Name + ".", StringComparison.Ordinal) || m.Contains("." + f.Name + "."))
  // A value is re-pointed through its getter.
  | DeclKind.ValueDecl ->
    let getter = "get_" + f.Name
    names |> List.exists (fun m -> m = getter || m.EndsWith("." + getter, StringComparison.Ordinal))
  | _ -> names |> List.exists (fun m -> m = f.Name || m.EndsWith("." + f.Name, StringComparison.Ordinal))

/// Whether the running build already had this declaration, of the same kind.
let private existedIn (before: FileDecls) (f: SourceDecl) =
  before.Decls |> List.exists (fun d -> d.Kind = f.Kind && d.Name = f.Name)

let confirmPatch (before: FileDecls) (patched: SourceDecl list) (reloadedMethods: string list) : PatchOutcome =
  let existed = existedIn before
  let detoured = reachedBy reloadedMethods
  let notDetoured =
    patched
    |> List.filter (fun f -> existed f && not (detoured f))
    |> List.map (fun f -> ReloadChange.SignatureChanged f.Name)
  match notDetoured with
  | first :: rest -> PatchOutcome.RestartNeeded (first, rest)
  | [] -> PatchOutcome.Applied

/// The same confirmation, reported as what it did to the RUNNING PROCESS.
///
/// `PatchOutcome.Applied` carries no count, so "applied" was indistinguishable
/// from "applied nothing" — a patch list of zero, or a patch list none of whose
/// functions was actually detoured, both read as success, and the browser was
/// told to refresh into byte-identical code. `ReloadOutcome.ofPatchCounts` is
/// the only constructor and it routes a count of zero to `NoEffect`, so that
/// particular lie is no longer expressible.
///
/// Every function that did not reach the running process is classified by WHY:
/// one that existed in the running build and was not re-pointed had its compiled
/// signature change; one that never existed there has no original to re-point at
/// all, which is a different problem with a different remedy.
/// `reachedRunningProcess` is the subset of `reloadedMethods` whose re-pointed
/// OLD entry point is one the RUNNING PROCESS actually calls — in practice, one
/// that lived in a compiled assembly rather than in FSI's own dynamic assembly.
///
/// It has to be passed separately because a NAME cannot carry it. FSI wraps
/// every submission in an `FSI_NNNN` type and `HotReloadCore.getAllMethods`
/// strips that prefix so both sides register the same qualified name — which is
/// what lets the detour matcher pair anything at all, and also what makes the
/// compiled copy of `M.f` and every prior eval's copy of `M.f` indistinguishable
/// strings. Under `--multiemit-` the single FSI assembly ACCUMULATES every eval
/// (fsi.fs:1818-1830), so there is always a growing pile of older same-named
/// copies to pair with, and pairing one of those re-points code that nothing
/// outside that eval ever calls. Counting it as landed is how a save was
/// reported "Hot reloaded 1 of 1" while the app went on serving the old body.
let confirmPatchAsOutcome
  (before: FileDecls)
  (patched: SourceDecl list)
  (reloadedMethods: string list)
  (reachedRunningProcess: string list)
  : ReloadOutcome =
  let nameMatches = reachedBy
  let existed = existedIn before
  // A declaration that did NOT exist in the running build has no compiled entry
  // point to reach by definition, so for it the FSI copy IS what everything
  // calls and a redirect onto it is genuinely effective. Only a declaration the
  // running build already had must prove it reached a compiled entry point.
  let landed, missed =
    patched
    |> List.partition (fun f ->
      nameMatches reloadedMethods f
      && (nameMatches reachedRunningProcess f || not (existed f)))
  let reasons =
    missed
    |> List.map (fun f ->
      match existed f, nameMatches reloadedMethods f with
      // Re-pointed something, but only a previous eval's copy. The running
      // process is untouched and the user has to restart to see the edit.
      | true, true -> RestartReason.PatchIneffective f.Name
      | true, false -> RestartReason.SignatureChanged f.Name
      | false, _ -> RestartReason.NewDeclaration f.Name)
  ReloadOutcome.ofPatchCounts (List.length landed) (List.length patched) reasons

/// A plan that refused before any patch was attempted, reported in the same
/// vocabulary. SageFs does not own the app's lifetime here, so the user is the
/// one who has to act — which is exactly what `RestartRequired` means.
/// The outcome type and its companion module share a name, and `open`ing the
/// namespace-shaped module puts the MODULE in scope for value lookups — so the
/// union case needs the type spelled out rather than a resolution coin-flip.
let restartOutcome (first: ReloadChange) (rest: ReloadChange list) : ReloadOutcome =
  SageFs.Features.ReloadOutcome.ReloadOutcome.RestartRequired (ReloadChange.restartReasons first rest)

/// How a saved source file reaches the process that is running the user's code.
[<RequireQualifiedAccess>]
type ReloadRoute =
  /// The file has a baseline — the source the loaded assembly was built from —
  /// so only the functions that changed are re-emitted, against the COMPILED
  /// module's own identity (`emitStableIdentity`).
  | PatchInPlace of baseline: FileDecls
  /// No trustworthy baseline (the file is not part of a loaded project, was
  /// edited after the build, or does not parse): re-evaluate the whole file.
  | ReevaluateWholeFile

/// Chesterton's fence — this is THE hot-reload propagation decision, and routing
/// it on "is an app running under AppRunner" instead of "do we have a baseline"
/// was the P0 gap that made hot reload useless for every real web app.
///
/// Re-evaluating a WHOLE file re-declares the types the file itself defines. A
/// handler whose signature mentions one of them (`todoListView (items: TodoItem
/// list)`) then has a parameter type from the FSI assembly while the compiled
/// method's parameter type is the project assembly's — so the detour matcher's
/// parameter-type equality fails, no detour is applied, and the running app
/// keeps calling the old body forever. The browser still refreshes, so it looks
/// like it worked. That is exactly what users saw with Falco/Giraffe/Saturn/
/// Oxpecker route tables and minimal-API endpoints, all of which capture their
/// handlers at startup.
///
/// Emitting ONLY the changed functions against the compiled module keeps every
/// parameter type identical, so the pairing succeeds and the captured handler's
/// entry point is re-pointed. That is correct whether the app was started by
/// `run_app`, by an init script, or by hand in the REPL — so the route depends
/// on the baseline alone.
let routeFor (baselineOf: string -> FileDecls option) (filePath: string) : ReloadRoute =
  match baselineOf filePath with
  | Some baseline -> ReloadRoute.PatchInPlace baseline
  | None -> ReloadRoute.ReevaluateWholeFile
