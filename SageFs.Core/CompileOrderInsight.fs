namespace SageFs

open System.IO
open System.Text.RegularExpressions

/// F#-aware analysis layered on top of relayed compiler diagnostics (roast UX-4).
///
/// When a build fails with a cluster of FS0039 "X is not defined" errors and the
/// missing names are actually DEFINED in a file that compiles LATER, the real fix
/// is a compile-order change — not writing code. A generic build tool relays the
/// four "not defined" errors and stops; SageFs has the project's compile order
/// right there in the .fsproj it parsed, so it can connect the dots and say
/// exactly which file to move. This is the F#-aware intelligence a plain build
/// tool cannot offer.
///
/// The analysis core is pure and injected (`analyze`); the file-reading edge
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

  [<RequireQualifiedAccess>]
  type Insight =
    | NoIssue
    | Reorder of ReorderSuggestion list

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

  // Top-level definition shapes. Conservative on purpose: an over-match only
  // fires a hint when the name ALSO matches an undefined FS0039 name AND lives
  // in a later-compiled file, so the cost of a stray match is near zero; a miss
  // simply produces no hint (fail-safe).
  let private defRxs =
    [ Regex(@"^\s*type\s+(?:rec\s+|internal\s+|private\s+|public\s+|\[<[^>]*>\]\s*)*(\w+)", RegexOptions.Compiled)
      Regex(@"^\s*module\s+(?:rec\s+|internal\s+|private\s+|public\s+)*(\w+)", RegexOptions.Compiled)
      Regex(@"^\s*exception\s+(\w+)", RegexOptions.Compiled)
      Regex(@"^\s*let\s+(?:rec\s+|inline\s+|mutable\s+|private\s+|internal\s+)*(\w+)", RegexOptions.Compiled)
      // union-case constructors: "| Foo" / "| Foo of ..."
      Regex(@"^\s*\|\s*(\w+)", RegexOptions.Compiled) ]

  /// The names a source file defines at (near) top level — types, modules,
  /// exceptions, let-bindings, and union-case constructors.
  let namesDefinedIn (source: string) : Set<string> =
    source.Replace("\r\n", "\n").Split('\n')
    |> Array.collect (fun line ->
      defRxs
      |> List.choose (fun rx ->
        let m = rx.Match(line)
        if m.Success then Some m.Groups.[1].Value else None)
      |> List.toArray)
    |> Set.ofArray

  // ── Pure analysis ──

  /// `compileOrder`: file names in .fsproj `<Compile>` order.
  /// `symbolDefiningFile`: given an undefined name, the file name that defines
  /// it (None when unknown/external). Diagnostic file paths are compared by
  /// file name, so absolute build paths and relative compile items still line
  /// up. Pure: `Path.GetFileName` is a string operation, no IO.
  let analyze
    (compileOrder: string list)
    (symbolDefiningFile: string -> string option)
    (diagnostics: BuildDiagnostic list)
    : Insight =
    let indexOf f = List.tryFindIndex ((=) f) compileOrder
    let candidates =
      diagnostics
      |> List.choose (fun d ->
        match d.Code, d.File with
        | Some code, Some usedInPath when code = "FS0039" ->
          match undefinedName d.Message with
          | Some name ->
            match symbolDefiningFile name with
            | Some defIn ->
              let usedIn = Path.GetFileName usedInPath
              match indexOf usedIn, indexOf defIn with
              // defining file compiles STRICTLY AFTER the file that used it
              | Some ui, Some di when di > ui -> Some (defIn, usedIn, name)
              | _ -> None
            | None -> None
          | None -> None
        | _ -> None)
    match candidates with
    | [] -> Insight.NoIssue
    | cs ->
      cs
      |> List.groupBy (fun (defIn, usedIn, _) -> (defIn, usedIn))
      |> List.map (fun ((defIn, usedIn), grp) ->
        { DefiningFile = defIn
          UsedInFile = usedIn
          Names = grp |> List.map (fun (_, _, n) -> n) |> List.distinct })
      |> Insight.Reorder

  /// The actionable hint text, worded from the analysis data (no call to action
  /// baked into the domain — mirrors `BuildDiagnostic.describe`'s design).
  let describe (insight: Insight) : string option =
    match insight with
    | Insight.NoIssue -> None
    | Insight.Reorder suggestions ->
      let lines =
        suggestions
        |> List.map (fun s ->
          let names = s.Names |> List.map (sprintf "'%s'") |> String.concat ", "
          sprintf "  • %s %s defined in %s, which compiles AFTER %s — move %s above %s in the .fsproj <Compile> order."
            names
            (if s.Names.Length = 1 then "is" else "are")
            s.DefiningFile s.UsedInFile s.DefiningFile s.UsedInFile)
      Some (
        "F#-aware hint: this looks like a compile-order problem, not missing code.\n"
        + "F# compiles files top-to-bottom; a file may only use names defined in files listed BEFORE it.\n"
        + String.concat "\n" lines)

  // ── Impure edge ──

  let private compileIncludeRx =
    Regex("<Compile\\s+Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

  /// The `<Compile Include>` file names of an .fsproj, in document (compile)
  /// order. Same-project ordering is the common compile-order pitfall; project
  /// references are out of scope here.
  let compileOrderOf (projPath: string) : string list =
    try
      let text = File.ReadAllText projPath
      [ for m in compileIncludeRx.Matches text -> Path.GetFileName (m.Groups.[1].Value.Replace('\\', '/')) ]
    with _ -> []

  /// Compute the compile-order insight for a failed build and, when present,
  /// render it as a trailing advisory `BuildDiagnostic` (Warning, no location,
  /// a distinctive SageFs code) so any surface that already renders diagnostics
  /// surfaces the hint too — with no change to the error's shape. Returns None
  /// when there is no compile-order issue (or the sources can't be read).
  let forProject (projPath: string) (diagnostics: BuildDiagnostic list) : BuildDiagnostic option =
    let compileOrder = compileOrderOf projPath
    match compileOrder with
    | [] -> None
    | files ->
      let projDir = Path.GetDirectoryName projPath
      // name -> first defining file (in compile order); reads only the sources.
      let index =
        files
        |> List.rev // fold so earlier files win when a name is defined twice
        |> List.fold (fun acc fileName ->
          let full = Path.Combine(projDir, fileName)
          match (try Some (File.ReadAllText full) with _ -> None) with
          | Some src -> namesDefinedIn src |> Set.fold (fun m n -> Map.add n fileName m) acc
          | None -> acc) Map.empty
      let symbolDefiningFile name = Map.tryFind name index
      match describe (analyze files symbolDefiningFile diagnostics) with
      | None -> None
      | Some hint ->
        Some
          { File = None
            Line = None
            Column = None
            Severity = BuildDiagnosticSeverity.Warning
            Code = Some "SAGEFS-COMPILE-ORDER"
            Message = hint }
