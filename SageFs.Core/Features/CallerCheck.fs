/// Who calls a declaration a save re-signed or removed, when the caller is in another file of the project.
///
/// The planner (`ReloadPlanning`) reads ONE file: the saved one, against its baseline. A caller in another file is not in
/// the patch, so it stays on the old method, and finding it takes the rest of the project. This module reads the saved
/// file's change as a list of subjects, reads the project's other files as text, and answers per subject with a
/// `CallersCheck` that says how it knows: the compiler's symbol resolution over the whole project (the same
/// `GetAllUsesOfAllSymbols` technique the planner's standalone check uses, widened to the project), or a name match that
/// can over-report and never under-reports, with the reason the compiler was not used.
///
/// `decide` is pure, so the DST folds it; `callersAsync` is the one place that asks the compiler.
module SageFs.Features.CallerCheck

open System
open System.IO
open System.Text.RegularExpressions
open FSharp.Compiler.CodeAnalysis
open SageFs.Features.CallerState
open SageFs.Features.ReloadPlanning

/// Where a subject sits in the saved file's NEW text.
[<RequireQualifiedAccess>]
type SubjectLines =
  /// The declaration's first and last line (1-based). A compiler use resolved to a declaration on these lines is a call
  /// to it.
  | Declared of first: int * last: int
  /// It is not in the new text, so there is nothing for the compiler to resolve to.
  | GoneFromFile

/// A declaration the save re-signed or removed.
type SignatureSubject = {
  Edit: SignatureEdit
  /// The bare name callers write.
  Name: string
  Lines: SubjectLines
}

/// A source file of the project other than the saved one, as it is on disk now.
type OtherFile = { Path: string; Text: string }

/// The project's other files, or why there are none to read.
[<RequireQualifiedAccess>]
type OtherSources =
  /// No project is loaded for the saved file.
  | NotLoaded
  | Unreadable of file: string * detail: string
  | Loaded of files: OtherFile list

/// One use the compiler resolved, in a file other than the saved one, to a declaration in the saved file.
type CompilerUse = {
  UseFile: string
  UseLine: int
  DeclaredIn: string
  DeclaredLine: int
}

/// What the compiler said about the project.
type CompilerFindings = {
  /// Every resolved use of a symbol declared in the saved file, from outside it.
  Uses: CompilerUse list
  /// Every line of a file other than the saved one that carries an error, whether or not the use on it resolved. A caller
  /// of a re-signed function is expected to error, so an error alone says nothing; an error with no resolved use on it is
  /// a use the compiler could not place.
  ErrorLines: (string * int) list
}

[<RequireQualifiedAccess>]
type CompilerAnswer =
  | Answered of CompilerFindings
  /// The compiler was not used, and why.
  | Unavailable of NameOnlyReason

/// The qualified name of a declaration, as a caller and a patch both spell it.
let qualifiedName (file: FileDecls) (d: SourceDecl) : string =
  file.ModulePath @ d.Container @ [ d.Name ] |> String.concat "."

/// The functions the save re-signed or removed. A function's header is what callers were compiled against, so a changed
/// header is a new method to the running app, and a function that is gone from the file leaves its old method in the
/// process. A rename is a removal of the old name. An added function strands nobody.
let subjectsOf (filePath: string) (baseline: FileDecls) (current: FileDecls) : SignatureSubject list =
  let now = keyed current.Decls |> Map.ofList
  keyed baseline.Decls
  |> List.choose (fun (key, before) ->
    match before.Kind with
    | DeclKind.FunctionDecl ->
      let edit cause : SignatureEdit =
        { Declaration = qualifiedName baseline before; Cause = cause; File = filePath }
      match Map.tryFind key now with
      | Some after when normalize before.Header <> normalize after.Header ->
        Some { Edit = edit SignatureCause.ReSigned; Name = before.Name; Lines = SubjectLines.Declared(after.StartLine, after.EndLine) }
      | Some _ -> None
      | None -> Some { Edit = edit SignatureCause.Removed; Name = before.Name; Lines = SubjectLines.GoneFromFile }
    | DeclKind.TypeDecl
    | DeclKind.ValueDecl
    | DeclKind.MutableValueDecl
    | DeclKind.ValueClosures
    | DeclKind.EntryPointDecl
    | DeclKind.NestedModuleDecl
    | DeclKind.StartupCode -> None)

let private fullPath (path: string) : string =
  try Path.GetFullPath path with _ -> path

let private samePath (a: string) (b: string) : bool =
  String.Equals(fullPath a, fullPath b, StringComparison.Ordinal)

let private identifierToken = Regex(@"[A-Za-z_][A-Za-z0-9_']*", RegexOptions.Compiled)

/// What stands in front of a name that defines it rather than uses it: `let`, `let rec`, `and`, with their modifiers.
let private definitionPrefix = Regex(@"(^|\s)(let|and)\s+((rec|inline|private|internal|public|mutable)\s+)*$", RegexOptions.Compiled)

/// The text with comments and string literals blanked, and every line break kept, so a line number in it is a line number
/// in the file. (`stripCommentsAndStrings` blanks the breaks inside a multi-line comment or string too.)
let private blankedKeepingLines (text: string) : string =
  let stripped = (stripCommentsAndStrings text).ToCharArray()
  text |> String.iteri (fun i c -> match c with '\n' -> stripped.[i] <- '\n' | _ -> ())
  String(stripped)

/// The lines of `text` where `name` is used: its token appears outside comments and strings, and not as a definition.
let private nameLines (name: string) (text: string) : int list =
  (blankedKeepingLines text).Split('\n')
  |> Array.mapi (fun i line -> i + 1, line)
  |> Array.filter (fun (_, line) ->
    identifierToken.Matches line
    |> Seq.cast<Match>
    |> Seq.exists (fun m -> m.Value = name && not (definitionPrefix.IsMatch(line.Substring(0, m.Index)))))
  |> Array.map fst
  |> Array.toList

/// The qualified declaration that holds `line`, or empty for code outside any declaration (or a file that does not parse).
let private callerAt (text: string) (line: int) : string =
  match extractDecls text with
  | Ok decls ->
    decls.Decls
    |> List.tryFind (fun d -> d.StartLine <= line && line <= d.EndLine)
    |> Option.map (qualifiedName decls)
    |> Option.defaultValue ""
  | Error _ -> ""

let private sitesFor (files: OtherFile list) (found: (OtherFile * int * SiteEvidence) list) : CallSite list =
  found
  |> List.map (fun (file, line, evidence) -> { File = file.Path; Line = line; Caller = callerAt file.Text line; Evidence = evidence })
  |> List.sortBy (fun s -> s.File, s.Line)

let private checkOne (sources: OtherSources) (answer: CompilerAnswer) (subject: SignatureSubject) : CallersCheck =
  match sources with
  | OtherSources.NotLoaded -> CallersCheck.NotChecked UncheckedReason.ProjectNotLoaded
  | OtherSources.Unreadable(file, why) -> CallersCheck.NotChecked(UncheckedReason.SourceUnreadable(file, why))
  | OtherSources.Loaded files ->
    match isIdentifier subject.Name with
    | false -> CallersCheck.NotChecked(UncheckedReason.NotSearchableByName subject.Name)
    | true ->
      let named = files |> List.collect (fun f -> nameLines subject.Name f.Text |> List.map (fun line -> f, line))
      let byName (why: NameOnlyReason) =
        named |> List.map (fun (f, line) -> f, line, SiteEvidence.MatchedByName why)
      let found =
        match subject.Edit.Cause, subject.Lines, answer with
        // A name that is gone from the file resolves to nothing, so only its spelling can be searched for.
        | SignatureCause.Removed, _, _
        | SignatureCause.ReSigned, SubjectLines.GoneFromFile, _ -> byName NameOnlyReason.DeclarationRemoved
        | SignatureCause.ReSigned, SubjectLines.Declared _, CompilerAnswer.Unavailable why -> byName why
        | SignatureCause.ReSigned, SubjectLines.Declared(first, last), CompilerAnswer.Answered findings ->
          let resolved =
            findings.Uses
            |> List.filter (fun u -> samePath u.DeclaredIn subject.Edit.File && first <= u.DeclaredLine && u.DeclaredLine <= last)
            |> List.map (fun u -> fullPath u.UseFile, u.UseLine)
            |> Set.ofList
          let errored = findings.ErrorLines |> List.map (fun (f, l) -> fullPath f, l) |> Set.ofList
          let at (f: OtherFile) line = fullPath f.Path, line
          // Every resolved use counts, found by name or not (a qualified call through an alias still resolves).
          let resolvedSites =
            files
            |> List.collect (fun f ->
              resolved |> Set.toList |> List.filter (fun (p, _) -> p = fullPath f.Path) |> List.map (fun (_, line) -> f, line, SiteEvidence.ResolvedByCompiler))
          // A use the compiler could not place stays a caller until something proves it is not one.
          let unplaced =
            named
            |> List.filter (fun (f, line) -> not (Set.contains (at f line) resolved) && Set.contains (at f line) errored)
            |> List.map (fun (f, line) -> f, line, SiteEvidence.MatchedByName NameOnlyReason.UseNotResolved)
          resolvedSites @ unplaced
      match sitesFor files found with
      | [] -> CallersCheck.NoCallers
      | first :: rest -> CallersCheck.Callers(first, rest)

/// The answer for each subject. Pure: the compiler's answer is an input.
let decide (sources: OtherSources) (answer: CompilerAnswer) (subjects: SignatureSubject list) : (SignatureEdit * CallersCheck) list =
  subjects |> List.map (fun s -> s.Edit, checkOne sources answer s)

/// A checker of its own, with a project cache: the first check of a project is cold, and the next one, after a save that
/// touched a file or two, only rechecks what changed. The planner's standalone checker holds no project.
let projectChecker : Lazy<FSharpChecker> = lazy (FSharpChecker.Create(projectCacheSize = 3))

let private isError (d: FSharp.Compiler.Diagnostics.FSharpDiagnostic) : bool =
  d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error

/// Checks the whole project with the compiler and reads every use that resolves to a declaration in `savedFile`.
/// Bounded by `Timeouts.callerCheck`: past it the answer is `Unavailable`, never a hang and never a guess.
let compilerAnswerAsync (options: FSharpProjectOptions) (savedFile: string) : Async<CompilerAnswer> =
  async {
    try
      let! results = projectChecker.Value.ParseAndCheckProject options |> withinBound SageFs.Timeouts.callerCheck
      let saved = fullPath savedFile
      let uses =
        results.GetAllUsesOfAllSymbols()
        |> Array.toList
        |> List.choose (fun su ->
          match su.IsFromDefinition || samePath su.FileName saved with
          | true -> None
          | false ->
            match su.Symbol.DeclarationLocation with
            | Some declared when samePath declared.FileName saved ->
              Some { UseFile = su.FileName; UseLine = su.Range.StartLine; DeclaredIn = declared.FileName; DeclaredLine = declared.StartLine }
            | Some _
            | None -> None)
      let errorLines =
        results.Diagnostics
        |> Array.toList
        |> List.filter (fun d -> isError d && not (samePath d.FileName saved))
        |> List.collect (fun d -> [ for line in d.StartLine .. d.EndLine -> d.FileName, line ])
        |> List.distinct
      return CompilerAnswer.Answered { Uses = uses; ErrorLines = errorLines }
    with
    | :? TimeoutException -> return CompilerAnswer.Unavailable(NameOnlyReason.CompilerTimedOut SageFs.Timeouts.callerCheck)
    | ex -> return CompilerAnswer.Unavailable(NameOnlyReason.CompilerFailed ex.Message)
  }

/// Who calls each subject. Asks the compiler once, for the whole project, and only when a subject was re-signed (a removed
/// name cannot be resolved, so a removal is read by name whatever the compiler would say). With no compiler options the
/// answer is by name and says so.
let callersAsync (options: FSharpProjectOptions option) (sources: OtherSources) (subjects: SignatureSubject list) : Async<(SignatureEdit * CallersCheck) list> =
  async {
    let reSigned = subjects |> List.exists (fun s -> s.Edit.Cause = SignatureCause.ReSigned)
    let readable = match sources with OtherSources.Loaded _ -> true | OtherSources.NotLoaded | OtherSources.Unreadable _ -> false
    let! answer =
      match subjects, options with
      | first :: _, Some o when reSigned && readable -> compilerAnswerAsync o first.Edit.File
      | _ -> async { return CompilerAnswer.Unavailable NameOnlyReason.NoProjectOptions }
    return decide sources answer subjects
  }

/// Reads the project's other source files from disk. A file that cannot be read makes the whole answer unreadable:
/// a caller in it could be anywhere, so nothing is claimed.
let readOthers (savedFile: string) (projectFiles: string list) : OtherSources =
  match projectFiles with
  | [] -> OtherSources.NotLoaded
  | files ->
    let others = files |> List.filter (fun f -> not (samePath f savedFile))
    let read (path: string) =
      try Ok { Path = path; Text = File.ReadAllText path }
      with ex -> Error(path, ex.Message)
    let results = others |> List.map read
    match results |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
    | Some(file, why) -> OtherSources.Unreadable(file, why)
    | None -> OtherSources.Loaded(results |> List.choose (function Ok f -> Some f | Error _ -> None))
