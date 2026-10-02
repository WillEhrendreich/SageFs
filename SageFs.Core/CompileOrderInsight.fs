namespace SageFs

open System.IO
open System.Text.RegularExpressions

/// F#-aware analysis layered on top of relayed compiler diagnostics (roast UX-4).
///
/// When a build fails with a cluster of FS0039 "X is not defined" errors there are two different
/// causes that look the same to a generic build tool, and the fixes point in opposite directions:
///
///   * the missing names are DEFINED in a file of the same project that compiles LATER. The fix is a
///     compile-order change, not writing code.
///   * the missing names are defined in a project this one REFERENCES, and the build compiled against
///     a copy of that project's assembly that does not have them yet. The files are in order. The fix
///     is to rebuild the referenced project, and reordering files would send the author the wrong way.
///
/// SageFs has the project's compile order and its references right there in the .fsproj files it can
/// read, so it can tell the two apart. A name only counts as defined by a declaration of the KIND the
/// error asked for: a missing TYPE is not satisfied by a later `let` or a union-case arm that happens
/// to share its spelling.
///
/// The analysis core is pure and injected (`analyzeWithReferences`); the file-reading edge
/// (`forProject`) is the only IO, and it runs only on a real build failure.
[<RequireQualifiedAccess>]
module CompileOrderInsight =

  /// A single "this file must move earlier" recommendation, aggregating every
  /// missing name that traces back to the same later-compiled file.
  type ReorderSuggestion =
    { /// The file that defines the missing names and must compile earlier.
      DefiningFile: string
      /// The earlier file whose FS0039 errors referenced those names.
      UsedInFile: string
      /// The undefined names that this reorder would resolve.
      Names: string list }

  /// Names a referenced project defines that the build did not see.
  type StaleReferenceSuggestion =
    { /// The referenced project file that defines the names.
      DefiningProject: string
      /// The file of the project being built whose FS0039 errors referenced them.
      UsedInFile: string
      /// The undefined names.
      Names: string list }

  [<RequireQualifiedAccess>]
  type Insight =
    | NoIssue
    | Reorder of ReorderSuggestion list
    | StaleReference of StaleReferenceSuggestion list
    | Both of ReorderSuggestion list * StaleReferenceSuggestion list

  /// What kind of thing an FS0039 says is missing.
  [<RequireQualifiedAccess>]
  type UndefinedKind =
    | Type
    | Module
    | Value
    /// The compiler named several kinds at once ("value, constructor, namespace or type").
    | Any

  /// What kind of thing a source line declares.
  [<RequireQualifiedAccess>]
  type DefinitionKind =
    | TypeDecl
    | ModuleDecl
    | ValueDecl
    | CaseDecl
    | ExceptionDecl

  // ── Pure parsing ──

  let private notDefinedRx =
    Regex(@"'([^']+)'\s+is not defined", RegexOptions.Compiled)

  /// Extract the undefined identifier from an FS0039 message. F# phrasings:
  /// "The value or constructor 'foo' is not defined.",
  /// "The namespace or module 'Bar' is not defined.",
  /// "The type 'T' is not defined." — all carry the name in single quotes.
  let undefinedName (message: string) : string option =
    let m = notDefinedRx.Match(message)
    if m.Success then Some m.Groups.[1].Value else None

  /// The kind the compiler said was missing. A message that names more than one kind, or one this
  /// does not recognise, is `Any`: it cannot rule a declaration out.
  let undefinedKind (message: string) : UndefinedKind =
    match message with
    | m when m.Contains "The type '" -> UndefinedKind.Type
    | m when m.Contains "The namespace or module '" -> UndefinedKind.Module
    | m when m.Contains "The value or constructor '" -> UndefinedKind.Value
    | _ -> UndefinedKind.Any

  /// Whether a declaration of this kind could satisfy a use of that kind.
  let accepts (missing: UndefinedKind) (declared: DefinitionKind) : bool =
    match missing, declared with
    | UndefinedKind.Any, _ -> true
    | UndefinedKind.Type, (DefinitionKind.TypeDecl | DefinitionKind.ExceptionDecl) -> true
    | UndefinedKind.Type, (DefinitionKind.ModuleDecl | DefinitionKind.ValueDecl | DefinitionKind.CaseDecl) -> false
    | UndefinedKind.Module, (DefinitionKind.ModuleDecl | DefinitionKind.TypeDecl) -> true
    | UndefinedKind.Module, (DefinitionKind.ValueDecl | DefinitionKind.CaseDecl | DefinitionKind.ExceptionDecl) -> false
    | UndefinedKind.Value, (DefinitionKind.ValueDecl | DefinitionKind.CaseDecl | DefinitionKind.ExceptionDecl | DefinitionKind.TypeDecl) -> true
    | UndefinedKind.Value, DefinitionKind.ModuleDecl -> false

  // Top-level definition shapes. Conservative on purpose: an over-match only
  // fires a hint when the name ALSO matches an undefined FS0039 name AND the
  // declaration is of the kind the error asked for AND it lives in a
  // later-compiled file, so the cost of a stray match is near zero; a miss
  // simply produces no hint (fail-safe).
  let private defRxs =
    [ Regex(@"^\s*type\s+(?:rec\s+|internal\s+|private\s+|public\s+|\[<[^>]*>\]\s*)*(\w+)", RegexOptions.Compiled), DefinitionKind.TypeDecl
      Regex(@"^\s*module\s+(?:rec\s+|internal\s+|private\s+|public\s+)*(\w+)", RegexOptions.Compiled), DefinitionKind.ModuleDecl
      Regex(@"^\s*exception\s+(\w+)", RegexOptions.Compiled), DefinitionKind.ExceptionDecl
      Regex(@"^\s*let\s+(?:rec\s+|inline\s+|mutable\s+|private\s+|internal\s+)*(\w+)", RegexOptions.Compiled), DefinitionKind.ValueDecl
      // union-case constructors: "| Foo" / "| Foo of ..."
      Regex(@"^\s*\|\s*(\w+)", RegexOptions.Compiled), DefinitionKind.CaseDecl ]

  let private namespaceRx =
    Regex(@"^\s*namespace\s+(?:rec\s+)?(?:global\.)?([\w.]+)", RegexOptions.Compiled)

  /// What a source file declares at (near) top level, with the kind of each declaration.
  let definitionsIn (source: string) : (string * DefinitionKind) list =
    source.Replace("\r\n", "\n").Split('\n')
    |> Array.collect (fun line ->
      let declared =
        defRxs
        |> List.choose (fun (rx, kind) ->
          let m = rx.Match(line)
          if m.Success then Some (m.Groups.[1].Value, kind) else None)
      let namespaces =
        let m = namespaceRx.Match(line)
        match m.Success with
        | true -> m.Groups.[1].Value.Split('.') |> Array.map (fun segment -> segment, DefinitionKind.ModuleDecl) |> List.ofArray
        | false -> []
      declared @ namespaces |> List.toArray)
    |> List.ofArray

  /// The names a source file defines at (near) top level — types, modules,
  /// exceptions, let-bindings, and union-case constructors.
  let namesDefinedIn (source: string) : Set<string> =
    definitionsIn source |> List.map fst |> Set.ofList

  // ── Pure analysis ──

  /// What one diagnostic points at.
  [<RequireQualifiedAccess>]
  type private Finding =
    | Reorder of definingFile: string * usedIn: string * name: string
    | Stale of definingProject: string * usedIn: string * name: string

  /// `compileOrder`: file names in .fsproj `<Compile>` order.
  /// `definingFile`: given an undefined name and the kind the error asked for, the file of this
  /// project that defines it (None when unknown/external). `definingProject`: the same for the
  /// projects this one references, answering with the referenced project's file name. Diagnostic
  /// file paths are compared by file name, so absolute build paths and relative compile items
  /// still line up. Pure: `Path.GetFileName` is a string operation, no IO.
  let analyzeWithReferences
    (compileOrder: string list)
    (definingFile: string -> UndefinedKind -> string option)
    (definingProject: string -> UndefinedKind -> string option)
    (diagnostics: BuildDiagnostic list)
    : Insight =
    let indexOf f = List.tryFindIndex ((=) f) compileOrder
    let findingOf (d: BuildDiagnostic) : Finding option =
      match d.Code, d.File with
      | Some code, Some usedInPath when code = "FS0039" ->
        match undefinedName d.Message with
        | None -> None
        | Some name ->
          let kind = undefinedKind d.Message
          let usedIn = Path.GetFileName usedInPath
          match definingFile name kind with
          | Some defIn ->
            match indexOf usedIn, indexOf defIn with
            // defining file compiles STRICTLY AFTER the file that used it
            | Some ui, Some di when di > ui -> Some (Finding.Reorder (defIn, usedIn, name))
            | _ -> None
          | None ->
            match definingProject name kind with
            | Some project -> Some (Finding.Stale (project, usedIn, name))
            | None -> None
      | _ -> None
    let findings = diagnostics |> List.choose findingOf
    let reorders =
      findings
      |> List.choose (function Finding.Reorder (defIn, usedIn, name) -> Some (defIn, usedIn, name) | Finding.Stale _ -> None)
      |> List.groupBy (fun (defIn, usedIn, _) -> (defIn, usedIn))
      |> List.map (fun ((defIn, usedIn), grp) ->
        ({ DefiningFile = defIn
           UsedInFile = usedIn
           Names = grp |> List.map (fun (_, _, n) -> n) |> List.distinct } : ReorderSuggestion))
    let stale =
      findings
      |> List.choose (function Finding.Stale (project, usedIn, name) -> Some (project, usedIn, name) | Finding.Reorder _ -> None)
      |> List.groupBy (fun (project, usedIn, _) -> (project, usedIn))
      |> List.map (fun ((project, usedIn), grp) ->
        ({ DefiningProject = project
           UsedInFile = usedIn
           Names = grp |> List.map (fun (_, _, n) -> n) |> List.distinct } : StaleReferenceSuggestion))
    match reorders, stale with
    | [], [] -> Insight.NoIssue
    | r, [] -> Insight.Reorder r
    | [], s -> Insight.StaleReference s
    | r, s -> Insight.Both (r, s)

  /// The same analysis for a project that references nothing.
  let analyze
    (compileOrder: string list)
    (definingFile: string -> UndefinedKind -> string option)
    (diagnostics: BuildDiagnostic list)
    : Insight =
    analyzeWithReferences compileOrder definingFile (fun _ _ -> None) diagnostics

  let private namesText (names: string list) : string =
    names |> List.map (sprintf "'%s'") |> String.concat ", "

  let private reorderLines (suggestions: ReorderSuggestion list) : string list =
    suggestions
    |> List.map (fun s ->
      sprintf "  • %s %s defined in %s, which compiles AFTER %s — move %s above %s in the .fsproj <Compile> order."
        (namesText s.Names)
        (if s.Names.Length = 1 then "is" else "are")
        s.DefiningFile s.UsedInFile s.DefiningFile s.UsedInFile)

  let private staleLines (suggestions: StaleReferenceSuggestion list) : string list =
    suggestions
    |> List.map (fun s ->
      sprintf "  • %s %s defined in %s, a project this one references, but the build compiled against an older copy of it that does not have %s (used in %s). The files here are in order. Rebuild %s, and check that no other copy of its assembly is referenced; a ProjectReference builds it first, a Reference to a built DLL does not."
        (namesText s.Names)
        (if s.Names.Length = 1 then "is" else "are")
        s.DefiningProject
        (if s.Names.Length = 1 then "it" else "them")
        s.UsedInFile
        s.DefiningProject)

  let private reorderHint (suggestions: ReorderSuggestion list) : string =
    "F#-aware hint: this looks like a compile-order problem, not missing code.\n"
    + "F# compiles files top-to-bottom; a file may only use names defined in files listed BEFORE it.\n"
    + String.concat "\n" (reorderLines suggestions)

  let private staleHint (suggestions: StaleReferenceSuggestion list) : string =
    "F#-aware hint: this looks like a stale reference, not a compile-order problem.\n"
    + String.concat "\n" (staleLines suggestions)

  /// The actionable hint text, worded from the analysis data (no call to action
  /// baked into the domain — mirrors `BuildDiagnostic.describe`'s design).
  let describe (insight: Insight) : string option =
    match insight with
    | Insight.NoIssue -> None
    | Insight.Reorder suggestions -> Some (reorderHint suggestions)
    | Insight.StaleReference suggestions -> Some (staleHint suggestions)
    | Insight.Both (reorders, stale) -> Some (reorderHint reorders + "\n" + staleHint stale)

  // ── Impure edge ──

  let private compileIncludeRx =
    Regex("<Compile\\s+Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

  /// The `<Compile Include>` file names of an .fsproj, in document (compile)
  /// order. Same-project ordering is the common compile-order pitfall.
  let compileOrderOf (projPath: string) : string list =
    try
      let text = File.ReadAllText projPath
      [ for m in compileIncludeRx.Matches text -> Path.GetFileName (m.Groups.[1].Value.Replace('\\', '/')) ]
    with _ -> []

  /// What a project's sources define, in compile order: name, kind, and the file.
  let private definitionsOfProject (projPath: string) : (string * DefinitionKind * string) list =
    let projDir = Path.GetDirectoryName projPath
    compileOrderOf projPath
    |> List.collect (fun fileName ->
      match (try Some (File.ReadAllText (Path.Combine(projDir, fileName))) with _ -> None) with
      | Some src -> definitionsIn src |> List.map (fun (name, kind) -> name, kind, fileName)
      | None -> [])

  /// The first definition, in the order given, of this name that could satisfy this kind of use.
  let private firstDefining (definitions: (string * DefinitionKind * string) list) (name: string) (kind: UndefinedKind) : string option =
    definitions
    |> List.tryPick (fun (n, declared, owner) ->
      match n = name && accepts kind declared with
      | true -> Some owner
      | false -> None)

  /// Compute the compile-order insight for a failed build and, when present,
  /// render it as a trailing advisory `BuildDiagnostic` (Warning, no location,
  /// a distinctive SageFs code) so any surface that already renders diagnostics
  /// surfaces the hint too — with no change to the error's shape. Returns None
  /// when there is nothing to say (or the sources can't be read).
  let forProject (projPath: string) (diagnostics: BuildDiagnostic list) : BuildDiagnostic option =
    let compileOrder = compileOrderOf projPath
    match compileOrder with
    | [] -> None
    | files ->
      let own = definitionsOfProject projPath
      let referenced =
        CoreEvidence.referencedProjects projPath
        |> List.collect (fun refProj ->
          definitionsOfProject refProj |> List.map (fun (name, kind, _) -> name, kind, Path.GetFileName refProj))
      let insight =
        analyzeWithReferences files (firstDefining own) (firstDefining referenced) diagnostics
      match describe insight, insight with
      | None, _ -> None
      | Some hint, Insight.StaleReference _ ->
        Some
          { File = None
            Line = None
            Column = None
            Severity = BuildDiagnosticSeverity.Warning
            Code = Some "SAGEFS-STALE-REFERENCE"
            Message = hint }
      | Some hint, (Insight.Reorder _ | Insight.Both _ | Insight.NoIssue) ->
        Some
          { File = None
            Line = None
            Column = None
            Severity = BuildDiagnosticSeverity.Warning
            Code = Some "SAGEFS-COMPILE-ORDER"
            Message = hint }

  /// Append the compile-order advisory to a failed build's diagnostics when one
  /// applies; otherwise return them unchanged. This keeps the "should we add a
  /// hint?" policy here with the analysis, so the build-fault site is a one-liner.
  let enrich (projPath: string) (diagnostics: BuildDiagnostic list) : BuildDiagnostic list =
    match forProject projPath diagnostics with
    | Some hint -> diagnostics @ [ hint ]
    | None -> diagnostics
