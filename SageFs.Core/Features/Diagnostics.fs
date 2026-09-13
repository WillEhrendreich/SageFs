module SageFs.Features.Diagnostics

open FSharp.Compiler.EditorServices
open FSharp.Compiler.Interactive
open FSharp.Compiler.Text
open FSharp.Compiler.Diagnostics
open SageFs.Features.LiveTesting

type Range = {
  StartLine: int
  StartColumn: int
  EndLine: int
  EndColumn: int
}

[<RequireQualifiedAccess>]
type DiagnosticSeverity =
  | Error
  | Hidden
  | Info
  | Warning

module DiagnosticSeverity =
  let label = function
    | DiagnosticSeverity.Error -> "error"
    | DiagnosticSeverity.Hidden -> "hidden"
    | DiagnosticSeverity.Info -> "info"
    | DiagnosticSeverity.Warning -> "warning"

type Diagnostic = {
  Message: string
  Subcategory: string
  Range: Range
  Severity: DiagnosticSeverity
  /// The FCS diagnostic's stable `ErrorNumber` (the "39" in "FS0039"), or 0
  /// when this Diagnostic was not derived from a compiler diagnostic (a
  /// hand-built test fixture, for instance). Locale-proof classification
  /// input for `ErrorMessages.categorizeByNumber` — see roast-5 item #10.
  ErrorNumber: int
} with

  static member mkDiagnostic(fsDiagnostic: FSharpDiagnostic) =
    let mapSeverity =
      function
      | FSharpDiagnosticSeverity.Error -> DiagnosticSeverity.Error
      | FSharpDiagnosticSeverity.Hidden -> DiagnosticSeverity.Hidden
      | FSharpDiagnosticSeverity.Info -> DiagnosticSeverity.Info
      | FSharpDiagnosticSeverity.Warning -> DiagnosticSeverity.Warning

    let range = fsDiagnostic.Range

    {
      Message = fsDiagnostic.Message
      Severity = mapSeverity fsDiagnostic.Severity
      Subcategory = fsDiagnostic.Subcategory
      ErrorNumber = fsDiagnostic.ErrorNumber
      Range = {
        StartLine = range.StartLine
        StartColumn = range.StartColumn
        EndLine = range.EndLine
        EndColumn = range.EndColumn
      }
    }

let getDiagnostics (fsiSession: Shell.FsiEvaluationSession) text =
  let parse, typed, glob = fsiSession.ParseAndCheckInteraction text

  parse.Diagnostics
  |> Seq.append typed.Diagnostics
  |> Seq.append glob.Diagnostics
  |> Seq.map Diagnostic.mkDiagnostic
  |> Seq.distinct
  |> Seq.toArray

/// Result of type-check with symbol extraction for the live testing cycle.
type TypeCheckWithSymbolsResult = {
  Diagnostics: Diagnostic array
  SymbolRefs: SymbolReference list
}

/// Extracts symbol references from FCS typed check results.
/// Includes both definitions and uses (needed for dependency graph building).
/// Excludes open statements which are not relevant for test dependency tracking.
let extractSymbolReferences
  (filePath: string)
  (checkResults: FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults)
  : SymbolReference list =
  checkResults.GetAllUsesOfAllSymbolsInFile()
  |> Seq.choose (fun su ->
    match su.IsFromOpenStatement with
    | true -> None
    | false ->
      Some {
        SymbolFullName = su.Symbol.FullName
        UseKind = match su.IsFromDefinition with | true -> SymbolUseKind.Definition | false -> SymbolUseKind.Reference
        UsedInTestId = None
        FilePath = filePath
        Line = su.Range.StartLine
      })
  |> Seq.toList

/// Type-check with both diagnostics and symbol extraction.
/// Used by the live testing cycle's RequestFcsTypeCheck effect.
let getTypeCheckWithSymbols
  (fsiSession: Shell.FsiEvaluationSession)
  (filePath: string)
  (text: string)
  : TypeCheckWithSymbolsResult =
  let parse, typed, glob = fsiSession.ParseAndCheckInteraction text
  let diagnostics =
    parse.Diagnostics
    |> Seq.append typed.Diagnostics
    |> Seq.append glob.Diagnostics
    |> Seq.map Diagnostic.mkDiagnostic
    |> Seq.distinct
    |> Seq.toArray
  let hasErrors =
    diagnostics |> Array.exists (fun d -> d.Severity = DiagnosticSeverity.Error)
  let symbolRefs =
    match hasErrors with
    | true -> []
    | false -> extractSymbolReferences filePath typed
  { Diagnostics = diagnostics; SymbolRefs = symbolRefs }
