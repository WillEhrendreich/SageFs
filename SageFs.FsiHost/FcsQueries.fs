/// The compiler-service queries the isolated host answers (diagnostics, symbol references, completion
/// candidates). Compiled ONLY into the host, against the SDK's own FSharp.Compiler.Service, and returning wire
/// types from FsiProtocol: no FCS object ever crosses the process boundary, and SageFs needs no FCS to ask.
module SageFs.FsiHost.FcsQueries

open System
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Interactive.Shell
open Microsoft.FSharp.Reflection
open SageFs.FsiHost.FsiProtocol

let private severityOf (severity: FSharpDiagnosticSeverity) : DiagnosticSeverity =
  match severity with
  | FSharpDiagnosticSeverity.Hidden -> DiagHidden
  | FSharpDiagnosticSeverity.Info -> DiagInfo
  | FSharpDiagnosticSeverity.Warning -> DiagWarning
  | FSharpDiagnosticSeverity.Error -> DiagError

let toWire (d: FSharpDiagnostic) : FsiDiagnostic =
  { Severity = severityOf d.Severity
    ErrorNumber = d.ErrorNumber
    Subcategory = d.Subcategory
    Message = d.Message
    StartLine = d.StartLine
    StartColumn = d.StartColumn
    EndLine = d.EndLine
    EndColumn = d.EndColumn }

/// All diagnostics of a parse + check, in the same order and de-duplicated the same way SageFs reports them in-process.
let private diagnosticsOf (parse: FSharpParseFileResults) (typed: FSharpCheckFileResults) (glob: FSharpCheckProjectResults) =
  parse.Diagnostics
  |> Seq.append typed.Diagnostics
  |> Seq.append glob.Diagnostics
  |> Seq.map toWire
  |> Seq.distinct
  |> Seq.toList

let check (session: FsiEvaluationSession) (text: string) : FsiDiagnostic list =
  let parse, typed, glob = session.ParseAndCheckInteraction text
  diagnosticsOf parse typed glob

/// Diagnostics plus, for error-free code, every symbol definition and use (opens excluded): the live-testing
/// dependency graph's input.
let checkWithSymbols (session: FsiEvaluationSession) (filePath: string) (text: string) : FsiDiagnostic list * WireSymbolRef list =
  let parse, typed, glob = session.ParseAndCheckInteraction text
  let diagnostics = diagnosticsOf parse typed glob
  let hasErrors = diagnostics |> List.exists (fun d -> d.Severity = DiagError)
  let symbols =
    match hasErrors with
    | true -> []
    | false ->
      typed.GetAllUsesOfAllSymbolsInFile()
      |> Seq.choose (fun symbolUse ->
        match symbolUse.IsFromOpenStatement with
        | true -> None
        | false ->
          Some
            { SymbolFullName = symbolUse.Symbol.FullName
              Use = (if symbolUse.IsFromDefinition then WireDefinition else WireUsage)
              FilePath = filePath
              Line = symbolUse.Range.StartLine })
      |> Seq.toList
  diagnostics, symbols

/// The completion candidates at the caret, kept as FCS items so a description can be produced on demand.
let candidates (session: FsiEvaluationSession) (text: string) (caret: int) : DeclarationListItem[] =
  let partialName = QuickParse.GetPartialLongNameEx(text, caret - 1)
  let parse, typed, _ = session.ParseAndCheckInteraction text
  (typed.GetDeclarationListInfo(Some parse, 1, text, partialName)).Items

/// FCS's own name for a glyph, read from the type (no hand-written strings): SageFs maps it back by name.
let private glyphName (glyph: FSharpGlyph) : string =
  let case, _ = FSharpValue.GetUnionFields(box glyph, typeof<FSharpGlyph>)
  case.Name

let toCompletion (item: DeclarationListItem) : WireCompletion =
  { DisplayText = item.NameInList
    ReplacementText = item.NameInCode
    Glyph = glyphName item.Glyph }

/// The first description group's main text, as plain text.
let describe (item: DeclarationListItem) : string =
  item.Description
  |> (fun (ToolTipText elements) -> elements)
  |> Seq.collect (function
    | ToolTipElement.Group group -> group
    | _ -> [])
  |> Seq.tryHead
  |> Option.map (fun element -> element.MainDescription |> Array.map (fun tagged -> tagged.Text) |> String.concat "")
  |> Option.defaultValue ""
